using System.Collections.Concurrent;
using System.Text.Json;

namespace Octo.Services.Metadata;

/// <summary>
/// Enriches external (YouTube-resolved) tracks with real album/artist metadata
/// from Deezer's public API. Keyless and no ARL — the ARL that expires on the
/// music bot is only for Deezer AUDIO; metadata endpoints are open.
///
/// Everything here is best-effort and cached: a Deezer outage, throttle, or miss
/// returns null, and callers fall back to a synthetic entity. Nothing on this
/// path ever blocks or fails playback.
/// </summary>
public class DeezerMetadataService
{
    public record TrackMeta(string? AlbumTitle, string? AlbumCoverUrl, int? Year, int? Duration,
        string? ArtistName, string? ArtistImageUrl);
    public record ArtistMeta(string? Name, string? ImageUrl);

    /// <summary>Everything Deezer knows about a track, for writing rich file tags.</summary>
    public record FullTrackMeta(
        string? AlbumTitle, string? AlbumCoverUrl, int? Year, int? Duration, string? ArtistName,
        int? TrackNumber, int? DiscNumber, string? Isrc, int? TotalTracks, string? Genre,
        string? Label, string? ReleaseDate);

    private const string Base = "https://api.deezer.com";
    private const int MaxCache = 4096;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeezerMetadataService> _logger;
    private readonly ConcurrentDictionary<string, TrackMeta?> _trackCache = new();
    private readonly ConcurrentDictionary<string, FullTrackMeta?> _fullCache = new();
    private readonly ConcurrentDictionary<string, ArtistMeta?> _artistCache = new();
    private readonly ConcurrentDictionary<long, int?> _albumYearCache = new();
    // Single-flight the album-year fetch: many tracks in one search share an album
    // (a whole album's tracks), so concurrent lookups collapse onto one HTTP call.
    private readonly ConcurrentDictionary<long, Lazy<Task<int?>>> _albumYearTasks = new();

    public DeezerMetadataService(IHttpClientFactory httpFactory, ILogger<DeezerMetadataService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private HttpClient Client()
    {
        var c = _httpFactory.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(8);
        return c;
    }

    /// <summary>Resolve "artist + title" to the real album + artist (name, art, year).
    /// Pass includeYear=false to skip the extra album-detail call (bulk enrichment
    /// wants duration + album fast; the year is fetched lazily by the album view).</summary>
    public async Task<TrackMeta?> EnrichTrackAsync(string? artist, string? title, bool includeYear = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{artist}|{title}".ToLowerInvariant();
        if (_trackCache.TryGetValue(key, out var cached)) return cached;

        TrackMeta? meta = null;
        try
        {
            var q = Uri.EscapeDataString($"artist:\"{artist}\" track:\"{title}\"");
            using var doc = await GetJsonAsync($"{Base}/search?q={q}&limit=1", ct);
            if (FirstData(doc) is JsonElement t)
            {
                string? albTitle = null, cover = null, artName = null, artImg = null;
                long albId = 0;
                if (t.TryGetProperty("album", out var alb))
                {
                    albTitle = Str(alb, "title");
                    cover = Str(alb, "cover_xl") ?? Str(alb, "cover_medium");
                    if (alb.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number)
                        albId = aid.GetInt64();
                }
                if (t.TryGetProperty("artist", out var art))
                {
                    artName = Str(art, "name");
                    artImg = Str(art, "picture_xl") ?? Str(art, "picture_medium");
                }
                int? duration = t.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number
                    ? du.GetInt32() : null;
                var year = includeYear && albId > 0 ? await AlbumYearAsync(albId, ct) : null;
                meta = new TrackMeta(albTitle, cover, year, duration, artName, artImg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer enrich track '{A} - {T}' failed: {M}", artist, title, ex.Message);
        }

        Cache(_trackCache, key, meta);
        return meta;
    }

    /// <summary>
    /// Full track metadata for tagging a downloaded file: one track search (album,
    /// cover_xl, artist, duration, track_position, disk_number, isrc) plus one album
    /// detail call (release year, genre, total tracks, label). Cached; best-effort.
    /// </summary>
    public async Task<FullTrackMeta?> EnrichTrackFullAsync(string? artist, string? title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) return null;
        var key = $"full|{artist}|{title}".ToLowerInvariant();
        if (_fullCache.TryGetValue(key, out var cached)) return cached;

        FullTrackMeta? meta = null;
        try
        {
            var q = Uri.EscapeDataString($"artist:\"{artist}\" track:\"{title}\"");
            using var doc = await GetJsonAsync($"{Base}/search?q={q}&limit=1", ct);
            if (FirstData(doc) is JsonElement t)
            {
                string? albTitle = null, cover = null, artName = null;
                var isrc = Str(t, "isrc");
                long albId = 0;
                if (t.TryGetProperty("album", out var alb))
                {
                    albTitle = Str(alb, "title");
                    cover = Str(alb, "cover_xl") ?? Str(alb, "cover_big") ?? Str(alb, "cover_medium");
                    if (alb.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number)
                        albId = aid.GetInt64();
                }
                if (t.TryGetProperty("artist", out var art)) artName = Str(art, "name");

                int? year = null, totalTracks = null;
                string? genre = null, label = null, releaseDate = null;
                if (albId > 0)
                {
                    using var adoc = await GetJsonAsync($"{Base}/album/{albId}", ct);
                    if (adoc != null)
                    {
                        var root = adoc.RootElement;
                        releaseDate = Str(root, "release_date");
                        if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && int.TryParse(releaseDate[..4], out var yr))
                            year = yr;
                        totalTracks = Int(root, "nb_tracks");
                        label = Str(root, "label");
                        if (root.TryGetProperty("genres", out var g) && g.TryGetProperty("data", out var gd)
                            && gd.ValueKind == JsonValueKind.Array && gd.GetArrayLength() > 0)
                            genre = Str(gd[0], "name");
                    }
                }

                meta = new FullTrackMeta(albTitle, cover, year, Int(t, "duration"), artName,
                    Int(t, "track_position"), Int(t, "disk_number"), isrc, totalTracks, genre, label, releaseDate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer full enrich '{A} - {T}' failed: {M}", artist, title, ex.Message);
        }

        Cache(_fullCache, key, meta);
        return meta;
    }

    /// <summary>Resolve an artist name to its Deezer name + image.</summary>
    public async Task<ArtistMeta?> EnrichArtistAsync(string? artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;
        var key = artist.ToLowerInvariant();
        if (_artistCache.TryGetValue(key, out var cached)) return cached;

        ArtistMeta? meta = null;
        try
        {
            var q = Uri.EscapeDataString(artist);
            using var doc = await GetJsonAsync($"{Base}/search/artist?q={q}&limit=1", ct);
            if (FirstData(doc) is JsonElement a)
                meta = new ArtistMeta(Str(a, "name"), Str(a, "picture_xl") ?? Str(a, "picture_medium"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer enrich artist '{A}' failed: {M}", artist, ex.Message);
        }

        Cache(_artistCache, key, meta);
        return meta;
    }

    private Task<int?> AlbumYearAsync(long albumId, CancellationToken ct)
    {
        if (_albumYearCache.TryGetValue(albumId, out var y)) return Task.FromResult(y);
        // Shared across concurrent callers for the same album id (single-flight).
        return _albumYearTasks.GetOrAdd(albumId,
            id => new Lazy<Task<int?>>(() => FetchAlbumYearAsync(id))).Value;
    }

    private async Task<int?> FetchAlbumYearAsync(long albumId)
    {
        int? year = null;
        try
        {
            using var doc = await GetJsonAsync($"{Base}/album/{albumId}", CancellationToken.None);
            var rd = doc is null ? null : Str(doc.RootElement, "release_date");
            if (!string.IsNullOrEmpty(rd) && rd.Length >= 4 && int.TryParse(rd[..4], out var yr))
                year = yr;
        }
        catch { /* best-effort */ }
        _albumYearCache[albumId] = year;
        _albumYearTasks.TryRemove(albumId, out _);
        return year;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Client().GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var s = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(s);
    }

    private static JsonElement? FirstData(JsonDocument? doc)
    {
        if (doc is null) return null;
        if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
            return d[0];
        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;

    private static void Cache<TK, TV>(ConcurrentDictionary<TK, TV> cache, TK key, TV val) where TK : notnull
    {
        cache[key] = val;
        if (cache.Count > MaxCache) cache.Clear();  // crude bound; simple and safe
    }
}
