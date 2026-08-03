using System.Threading.RateLimiting;

namespace Octo.Services.Metadata;

/// <summary>
/// Keeps Octo inside Deezer's public-API budget of roughly 50 requests per 5 seconds.
///
/// Exceeding it does not produce a 429. Deezer answers HTTP 200 with an error envelope,
/// which used to parse as a valid-but-empty payload and get cached, so going over budget
/// was silently destructive rather than merely slow (issue #8).
///
/// Two lanes, one budget. Interactive work (a search the user is waiting on, cover art a
/// client is rendering) must not queue behind background cache warming. The permit counts
/// are chosen to SUM below the ceiling: separate limiters each get their own window, so
/// two generous lanes would admit their total rather than capping it.
/// </summary>
public sealed class DeezerRateLimiter : IDisposable
{
    /// <summary>Named HttpClient that carries the limiting handler. Both the metadata
    /// service and the cover-art lookup must resolve this name or they bypass the budget.</summary>
    public const string ClientName = "deezer";

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    // 30 + 10 = 40 of Deezer's ~50, leaving headroom for retries elsewhere and for the
    // fact that the ceiling is approximate rather than published as a contract.
    private const int InteractivePermits = 30;
    private const int BackgroundPermits = 10;

    // Queue depths are bounded by the 8s HttpClient timeout, which now covers waiting for
    // a permit as well as the call itself. Interactive drains at 6/s, so 32 queued is a
    // ~5s worst case and stays inside it; a deeper queue would just manufacture timeouts.
    private readonly SlidingWindowRateLimiter _interactive = Build(InteractivePermits, queueLimit: 32);
    private readonly SlidingWindowRateLimiter _background = Build(BackgroundPermits, queueLimit: 32);

    private static SlidingWindowRateLimiter Build(int permits, int queueLimit) =>
        new(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = Window,
            // One-second granularity, so permits free up smoothly instead of all at once
            // at the end of each window.
            SegmentsPerWindow = 5,
            // Must be set explicitly: it defaults to 0, which makes AcquireAsync return a
            // NON-acquired lease immediately instead of waiting. Every over-budget call
            // would then be dropped rather than delayed.
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

    public ValueTask<RateLimitLease> AcquireAsync(bool background, CancellationToken ct) =>
        (background ? _background : _interactive).AcquireAsync(1, ct);

    public void Dispose()
    {
        _interactive.Dispose();
        _background.Dispose();
    }
}
