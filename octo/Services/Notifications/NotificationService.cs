using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Notifications;

/// <summary>
/// Fans one domain event out to every configured transport, if its toggle is on.
///
/// The one unforgivable failure here would be disturbing a download, so the public
/// entry point is fire-and-forget and the whole dispatch is caught at two levels:
/// once around the body, and once per sink so one transport being down cannot delay
/// or kill the other. Rendering happens exactly once so ntfy and Discord always say
/// the same thing.
/// </summary>
public sealed class NotificationService
{
    /// <summary>Named HttpClient with a short timeout: a slow notification server
    /// must never be felt anywhere near the download path.</summary>
    public const string ClientName = "notifications";

    private readonly IReadOnlyList<INotificationSink> _sinks;
    private readonly IOptionsMonitor<NotificationSettings> _opts;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IEnumerable<INotificationSink> sinks,
        IOptionsMonitor<NotificationSettings> opts,
        ILogger<NotificationService> logger)
    {
        _sinks = sinks.ToList();
        _opts = opts;
        _logger = logger;
    }

    /// <summary>THE publish call. Never throws and never blocks the caller.</summary>
    public void Notify(NotificationEvent evt) => _ = NotifyInternalAsync(evt);

    /// <summary>Awaitable core, internal so tests can pin behavior deterministically
    /// instead of racing a discarded task.</summary>
    internal async Task NotifyInternalAsync(NotificationEvent evt)
    {
        try
        {
            var settings = _opts.CurrentValue;
            if (!IsEnabled(settings, evt.Type)) return;

            var configured = _sinks.Where(s => s.IsConfigured).ToList();
            if (configured.Count == 0) return;

            var message = Render(evt);
            await Task.WhenAll(configured.Select(async sink =>
            {
                try
                {
                    await sink.SendAsync(message, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Notification via {Sink} failed: {Msg}", sink.Name, ex.Message);
                }
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Notification dispatch failed: {Msg}", ex.Message);
        }
    }

    /// <summary>Admin test button. Unlike Notify this awaits every sink and reports
    /// each outcome, including the transport's real error text on failure.</summary>
    public async Task<IReadOnlyList<NotificationTestResult>> SendTestAsync(CancellationToken ct)
    {
        var message = Render(new NotificationEvent { Type = NotificationEventType.Test });
        var results = new List<NotificationTestResult>();
        foreach (var sink in _sinks)
        {
            if (!sink.IsConfigured)
            {
                results.Add(new NotificationTestResult(sink.Name, Configured: false, Ok: false, "not configured"));
                continue;
            }
            try
            {
                await sink.SendAsync(message, ct);
                results.Add(new NotificationTestResult(sink.Name, Configured: true, Ok: true, "delivered"));
            }
            catch (Exception ex)
            {
                results.Add(new NotificationTestResult(sink.Name, Configured: true, Ok: false, ex.Message));
            }
        }
        return results;
    }

    internal static bool IsEnabled(NotificationSettings s, NotificationEventType type) => type switch
    {
        NotificationEventType.DownloadStarted => s.NotifyDownloadStarted,
        NotificationEventType.DownloadCompleted => s.NotifyDownloadCompleted,
        NotificationEventType.LosslessFallback => s.NotifyLosslessFallback,
        NotificationEventType.DownloadFailed => s.NotifyDownloadFailed,
        NotificationEventType.AlbumCompleted => s.NotifyAlbumCompleted,
        // The test button bypasses toggles by design: it verifies transports.
        NotificationEventType.Test => true,
        _ => false,
    };

    /// <summary>Single source of truth for the text both transports carry.</summary>
    internal static NotificationMessage Render(NotificationEvent evt)
    {
        var track = string.IsNullOrEmpty(evt.Artist) ? evt.Title ?? "unknown track"
            : $"{evt.Artist} – {evt.Title}";

        return evt.Type switch
        {
            NotificationEventType.DownloadStarted => new NotificationMessage(
                evt.Type,
                $"Downloading: {track}",
                $"{evt.Format} via {evt.Source}" + (evt.SizeBytes is long sb and > 0 ? $" ({FormatSize(sb)})" : ""),
                evt.CoverArtUrl),

            NotificationEventType.DownloadCompleted => new NotificationMessage(
                evt.Type,
                $"Downloaded: {track}",
                (string.IsNullOrEmpty(evt.Album) ? "" : evt.Album + "\n")
                    + $"{evt.Format} via {evt.Source}"
                    + (evt.SizeBytes is long cb and > 0 ? $", {FormatSize(cb)}" : ""),
                evt.CoverArtUrl),

            NotificationEventType.LosslessFallback => new NotificationMessage(
                evt.Type,
                $"Lossless miss: {track}",
                $"Soulseek failed ({evt.Detail ?? "no usable result"}); settling for YouTube MP3.",
                evt.CoverArtUrl),

            NotificationEventType.DownloadFailed => new NotificationMessage(
                evt.Type,
                $"Download failed: {track}",
                evt.Detail ?? "Both sources failed.",
                evt.CoverArtUrl),

            NotificationEventType.AlbumCompleted => new NotificationMessage(
                evt.Type,
                $"Album complete: {track}",
                $"{evt.TrackCount} tracks fetched, {evt.LosslessCount} lossless"
                    + (evt.FailedCount is int f and > 0 ? $", {f} failed" : ""),
                evt.CoverArtUrl),

            _ => new NotificationMessage(
                evt.Type,
                "Octo test notification",
                "Transports are wired up correctly.",
                null),
        };
    }

    internal static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
        : $"{bytes / 1024.0:F0} KB";
}
