namespace Octo.Services.Notifications;

/// <summary>
/// One notification transport. Sinks MAY throw from SendAsync — the orchestrator
/// owns the catch for pipeline events, and the admin test endpoint wants the real
/// error text rather than a swallowed failure.
/// </summary>
public interface INotificationSink
{
    /// <summary>Stable short name for logs and the test endpoint ("ntfy", "discord").</summary>
    string Name { get; }

    /// <summary>True when this sink's URL is non-empty in current settings. Reads
    /// IOptionsMonitor at call time, so toggling in the admin UI applies without a
    /// restart.</summary>
    bool IsConfigured { get; }

    Task SendAsync(NotificationMessage message, CancellationToken ct);
}

/// <summary>
/// Transport-agnostic rendered content, produced once per event.
///
/// Body is the complete plain-text rendering and is what text-first transports
/// (ntfy) send. Description and Fields exist for transports with layout: when
/// Fields is non-null, Discord builds a song card — description line, inline
/// stat fields, full-width art — instead of repeating the Body prose. Both forms
/// are produced by the same Render call from the same event, so the transports
/// never disagree on facts, only on formatting.
/// </summary>
public sealed record NotificationMessage(
    NotificationEventType Type,
    string Title,
    string Body,
    string? ImageUrl,
    string? Description = null,
    IReadOnlyList<KeyValuePair<string, string>>? Fields = null);

/// <summary>Per-sink outcome of the admin "Send test" button.</summary>
public sealed record NotificationTestResult(string Sink, bool Configured, bool Ok, string Detail);
