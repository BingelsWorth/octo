using System.Threading.Channels;

namespace Octo.Services.Admin;

/// <summary>
/// Keeps a small, process-local tail of Octo's normal ILogger output and fans new
/// entries out to connected admin pages. This is intentionally diagnostic only:
/// nothing is written to disk, and restarting Octo clears the buffer.
/// </summary>
public sealed class AdminLogBuffer : ILoggerProvider
{
    private const int DefaultCapacity = 500;
    private const int MaximumMessageLength = 16_384;
    private const int MaximumExceptionLength = 32_768;

    private readonly object _gate = new();
    private readonly Queue<AdminLogEntry> _octoEntries;
    private readonly Queue<AdminLogEntry> _externalEntries;
    private readonly Dictionary<long, Channel<AdminLogEntry>> _subscribers = new();
    private readonly int _capacity;
    private long _nextSequence;
    private long _nextSubscriberId;

    public AdminLogBuffer() : this(DefaultCapacity) { }

    internal AdminLogBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _octoEntries = new Queue<AdminLogEntry>(capacity);
        _externalEntries = new Queue<AdminLogEntry>(capacity);
    }

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    public AdminLogSubscription Subscribe(long? afterSequence = null)
    {
        var channel = Channel.CreateBounded<AdminLogEntry>(new BoundedChannelOptions((_capacity * 2) + 64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        long subscriberId;
        lock (_gate)
        {
            subscriberId = ++_nextSubscriberId;
            _subscribers.Add(subscriberId, channel);

            // Keep replay chronological even though first-party and framework
            // entries have separate retention budgets. Routine health probes
            // must not evict the Radio event an operator came here to find.
            foreach (var entry in _octoEntries.Concat(_externalEntries).OrderBy(entry => entry.Sequence))
            {
                if (!afterSequence.HasValue || entry.Sequence > afterSequence.Value)
                    channel.Writer.TryWrite(entry);
            }
        }

        return new AdminLogSubscription(channel.Reader, () => RemoveSubscriber(subscriberId));
    }

    public void Dispose()
    {
        Channel<AdminLogEntry>[] subscribers;
        lock (_gate)
        {
            subscribers = _subscribers.Values.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
            subscriber.Writer.TryComplete();
    }

    private void Publish(LogLevel level, string category, string message, Exception? exception)
    {
        AdminLogEntry entry;
        lock (_gate)
        {
            entry = new AdminLogEntry(
                ++_nextSequence,
                DateTimeOffset.UtcNow,
                level,
                category,
                Truncate(message, MaximumMessageLength),
                exception is null ? null : Truncate(exception.ToString(), MaximumExceptionLength));

            var retainedEntries = IsOctoCategory(category) ? _octoEntries : _externalEntries;
            retainedEntries.Enqueue(entry);
            while (retainedEntries.Count > _capacity)
                retainedEntries.Dequeue();

            foreach (var subscriber in _subscribers.Values)
                subscriber.Writer.TryWrite(entry);
        }
    }

    private void RemoveSubscriber(long subscriberId)
    {
        Channel<AdminLogEntry>? channel = null;
        lock (_gate)
        {
            if (_subscribers.Remove(subscriberId, out var removed))
                channel = removed;
        }

        channel?.Writer.TryComplete();
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : string.Concat(value.AsSpan(0, maximumLength), "\n… [truncated]");

    private static bool IsOctoCategory(string category) =>
        category.Equals("Program", StringComparison.Ordinal)
        || category.StartsWith("Octo.", StringComparison.Ordinal);

    private sealed class BufferLogger(AdminLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Publish(logLevel, category, formatter(state, exception), exception);
        }
    }
}

public sealed record AdminLogEntry(
    long Sequence,
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);

public sealed class AdminLogSubscription(ChannelReader<AdminLogEntry> reader, Action unsubscribe) : IDisposable
{
    private Action? _unsubscribe = unsubscribe;

    public ChannelReader<AdminLogEntry> Reader { get; } = reader;

    public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}
