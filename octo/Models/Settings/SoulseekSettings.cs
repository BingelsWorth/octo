namespace Octo.Models.Settings;

/// <summary>
/// Configuration for the Soulseek (slskd) integration.
/// Octo talks to a self-hosted slskd instance which fronts the Soulseek P2P network.
/// </summary>
public class SoulseekSettings
{
    /// <summary>
    /// Base URL of the slskd REST API (e.g. http://slskd:5030 when running in the same docker network).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// slskd web UI / API admin username (Basic Auth).
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// slskd web UI / API admin password (Basic Auth).
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// How long to wait (seconds) for a Soulseek search to gather peer responses
    /// before returning results. Soulseek searches stream in over time, so this is
    /// the difference between finding a lossless file and silently settling for a
    /// transcode.
    ///
    /// This was 6, which measurement showed is simply too short: polling slskd's
    /// /responses for the same query returned nothing at 6s, 10s or 15s, then 14
    /// responses including 5 FLACs at 20s — reproducibly, across three runs. The
    /// effect was that every star fell back to YouTube MP3 while lossless copies
    /// were sitting there unseen. Note that the search status object reports a
    /// responseCount well before /responses will hand the files over, so a status
    /// poll makes short waits look adequate when they are not.
    ///
    /// This is a CEILING, not a duration: the search returns as soon as it has
    /// enough usable candidates to choose from, or as soon as slskd says the search
    /// has finished. A short value is therefore still a hard cap on finding
    /// anything, while a generous one costs nothing when results arrive early or
    /// when the search comes back empty.
    ///
    /// Star-triggered downloads are fire-and-forget, so the wait costs the user
    /// nothing; it only delays the file landing.
    /// </summary>
    public int SearchWaitSeconds { get; set; } = 30;

    /// <summary>
    /// Minimum file size in bytes to consider a search hit a real lossless file.
    /// Default 5 MB filters out 30s teaser clips and mislabelled tiny files.
    /// </summary>
    public long MinFileSizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Preferred file extension. Hits with this extension are sorted first.
    /// </summary>
    public string PreferredExtension { get; set; } = "flac";

    /// <summary>
    /// Max time to wait (seconds) for a download to complete before giving up on that
    /// peer and trying the next one. Per attempt, not per track: a track that has to
    /// walk all five candidates can spend this five times over. A peer that rejects
    /// outright is detected in seconds and does not wait this out.
    /// </summary>
    public int DownloadTimeoutSeconds { get; set; } = 180;
}
