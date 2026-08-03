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

    /// <summary>One album from a catalog search. Year is not on the search payload;
    /// the detail call fills it.</summary>
    public record AlbumHit(string DeezerId, string Title, string Artist,
        string? CoverUrl, int? Year, int TrackCount, string? RecordType);

    /// <summary>One track of an album, with the real length and position.</summary>
    public record AlbumTrack(string Title, string Artist, int? Duration,
        int? TrackPosition, int? DiscNumber, string? Isrc);

    /// <summary>An album plus its full tracklist.</summary>
    public record AlbumDetail(string DeezerId, string Title, string Artist,
        string? CoverUrl, int? Year, string? Genre, string? Label, List<AlbumTrack> Tracks);

    private const string Base = "https://api.deezer.com";
    private const int MaxCache = 4096;

    /// <summary>
    /// The only Deezer error code meaning "this genuinely does not exist". Everything
    /// else, including quota (code 4) and any code we do not recognise, is treated as
    /// transient. Caching an error we do not understand is exactly how one throttled
    /// call turned into an album that reported zero tracks for the life of the process.
    /// </summary>
    private const int DefinitiveErrorCode = 800;

    /// <summary>
    /// Result of one Deezer call. Deezer answers HTTP 200 even when it is refusing the
    /// request, so "we parsed a document" is not the same as "the call succeeded", and
    /// callers must never cache anything derived from a transient failure.
    /// </summary>
    private sealed class DeezerResponse : IDisposable
    {
        public JsonDocument? Doc { get; init; }

        /// <summary>Failed in a way that may succeed later. Nothing about this call
        /// may be written to a cache.</summary>
        public bool Transient { get; init; }

        public void Dispose() => Doc?.Dispose();
    }

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeezerMetadataService> _logger;
    private readonly ConcurrentDictionary<string, TrackMeta?> _trackCache = new();
    private readonly ConcurrentDictionary<string, FullTrackMeta?> _fullCache = new();
    private readonly ConcurrentDictionary<string, ArtistMeta?> _artistCache = new();
    private readonly ConcurrentDictionary<long, int?> _albumYearCache = new();
    private readonly ConcurrentDictionary<string, List<AlbumHit>> _albumSearchCache = new();
    private readonly ConcurrentDictionary<string, string?> _albumIdCache = new();
    private readonly ConcurrentDictionary<string, AlbumDetail?> _albumDetailCache = new();
    // Single-flight the album-year fetch: many tracks in one search share an album
    // (a whole album's tracks), so concurrent lookups collapse onto one HTTP call.
    private readonly ConcurrentDictionary<long, Lazy<Task<(int? Year, bool Transient)>>> _albumYearTasks = new();

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
            using var r = await GetJsonAsync($"{Base}/search?q={q}&limit=1", ct);
            if (r.Transient) return null;
            if (FirstData(r.Doc) is JsonElement t)
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
                int? year = null;
                if (includeYear && albId > 0)
                {
                    var (y, yearTransient) = await AlbumYearAsync(albId, ct);
                    // A throttled year lookup would otherwise be cached as "this track
                    // has no year", permanently, on an otherwise good result.
                    if (yearTransient) return null;
                    year = y;
                }
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
            using var r = await GetJsonAsync($"{Base}/search?q={q}&limit=1", ct);
            if (r.Transient) return null;
            if (FirstData(r.Doc) is JsonElement t)
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
                    using var ar = await GetJsonAsync($"{Base}/album/{albId}", ct);
                    if (ar.Transient) return null;
                    if (ar.Doc != null)
                    {
                        var root = ar.Doc.RootElement;
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
            using var r = await GetJsonAsync($"{Base}/search/artist?q={q}&limit=1", ct);
            if (r.Transient) return null;
            if (FirstData(r.Doc) is JsonElement a)
                meta = new ArtistMeta(Str(a, "name"), Str(a, "picture_xl") ?? Str(a, "picture_medium"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer enrich artist '{A}' failed: {M}", artist, ex.Message);
        }

        Cache(_artistCache, key, meta);
        return meta;
    }

    /// <summary>Search the album catalog. Single-track "albums" are dropped: a plain
    /// artist query returns a lot of them and they crowd out real records.</summary>
    public async Task<List<AlbumHit>> SearchAlbumsAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new List<AlbumHit>();
        var key = $"{query}|{limit}".ToLowerInvariant();
        if (_albumSearchCache.TryGetValue(key, out var cached)) return cached;

        var hits = new List<AlbumHit>();
        try
        {
            var q = Uri.EscapeDataString(query);
            using var r = await GetJsonAsync($"{Base}/search/album?q={q}&limit={limit}", ct);
            // Caching an empty list here is what would make external albums silently
            // vanish from search3 for the rest of the process.
            if (r.Transient) return new List<AlbumHit>();
            if (r.Doc is not null
                && r.Doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                // Materialize everything before the JsonDocument is disposed.
                foreach (var a in data.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number
                        ? aid.GetInt64().ToString() : null;
                    var title = Str(a, "title");
                    if (id is null || string.IsNullOrWhiteSpace(title)) continue;

                    var recordType = Str(a, "record_type");
                    var trackCount = Int(a, "nb_tracks") ?? 0;
                    if (string.Equals(recordType, "single", StringComparison.OrdinalIgnoreCase) && trackCount <= 2)
                        continue;

                    var artist = a.TryGetProperty("artist", out var art) ? Str(art, "name") : null;
                    hits.Add(new AlbumHit(
                        id, title, artist ?? "",
                        Str(a, "cover_xl") ?? Str(a, "cover_medium"),
                        null, trackCount, recordType));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer album search '{Q}' failed: {M}", query, ex.Message);
        }

        Cache(_albumSearchCache, key, hits);
        return hits;
    }

    /// <summary>Resolve an artist + album name to a Deezer album id. Needed because album
    /// ids minted from a song row carry no Deezer id, so the name is all we have.</summary>
    public async Task<string?> FindAlbumIdAsync(string? artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;
        var key = $"{artist}|{album}".ToLowerInvariant();
        if (_albumIdCache.TryGetValue(key, out var cached)) return cached;

        string? id = null;
        try
        {
            var q = Uri.EscapeDataString(
                string.IsNullOrWhiteSpace(artist) ? $"album:\"{album}\"" : $"artist:\"{artist}\" album:\"{album}\"");
            using var r = await GetJsonAsync($"{Base}/search/album?q={q}&limit=1", ct);
            if (r.Transient) return null;
            if (FirstData(r.Doc) is JsonElement a
                && a.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number)
            {
                id = aid.GetInt64().ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer album id lookup '{A} - {Al}' failed: {M}", artist, album, ex.Message);
        }

        Cache(_albumIdCache, key, id);
        return id;
    }

    /// <summary>Album detail plus its full tracklist, ordered by disc then track position.
    /// One bounded request per resource; a release larger than the cap is reported as
    /// truncated rather than silently presented as complete.</summary>
    public async Task<AlbumDetail?> GetAlbumDetailAsync(string deezerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deezerId)) return null;
        if (_albumDetailCache.TryGetValue(deezerId, out var cached)) return cached;

        AlbumDetail? detail = null;
        try
        {
            string title = "", artist = "", genre = "", label = "", cover = "";
            int? year = null;
            // Declared out here on purpose: the document below is disposed before the
            // tracklist call, and this is what tells an empty tracklist apart from an
            // album that genuinely has no tracks.
            int? nbTracks = null;

            using (var r = await GetJsonAsync($"{Base}/album/{deezerId}", ct))
            {
                if (r.Transient) return null;
                if (r.Doc is not null)
                {
                    var root = r.Doc.RootElement;
                    nbTracks = Int(root, "nb_tracks");
                    title = Str(root, "title") ?? "";
                    cover = Str(root, "cover_xl") ?? Str(root, "cover_medium") ?? "";
                    label = Str(root, "label") ?? "";
                    var rd = Str(root, "release_date");
                    if (!string.IsNullOrEmpty(rd) && rd.Length >= 4 && int.TryParse(rd[..4], out var yr))
                        year = yr;
                    if (root.TryGetProperty("artist", out var art))
                        artist = Str(art, "name") ?? "";
                    if (root.TryGetProperty("genres", out var genres)
                        && genres.TryGetProperty("data", out var gd)
                        && gd.ValueKind == JsonValueKind.Array && gd.GetArrayLength() > 0)
                        genre = Str(gd[0], "name") ?? "";
                }
            }

            if (string.IsNullOrWhiteSpace(title)) { Cache(_albumDetailCache, deezerId, null); return null; }

            var tracks = new List<AlbumTrack>();
            using (var tr = await GetJsonAsync($"{Base}/album/{deezerId}/tracks?limit=300", ct))
            {
                // The album call can succeed while the tracklist call is throttled. That
                // built a perfectly valid AlbumDetail carrying title, year and genre with
                // an empty tracklist, cached it permanently, and is why getAlbum reported
                // songCount 0 forever while still showing real metadata.
                if (tr.Transient) return null;
                if (tr.Doc is not null
                    && tr.Doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in data.EnumerateArray())
                    {
                        var tTitle = Str(t, "title");
                        if (string.IsNullOrWhiteSpace(tTitle)) continue;
                        var tArtist = t.TryGetProperty("artist", out var ta) ? Str(ta, "name") : null;
                        tracks.Add(new AlbumTrack(
                            tTitle, tArtist ?? artist, Int(t, "duration"),
                            Int(t, "track_position"), Int(t, "disk_number"), Str(t, "isrc")));
                    }

                    var total = Int(tr.Doc.RootElement, "total");
                    if (total is int n && n > tracks.Count)
                        _logger.LogWarning(
                            "deezer album '{Title}' ({Id}) returned {Got} of {Total} tracks; tracklist is truncated",
                            title, deezerId, tracks.Count, n);
                }
            }

            // An empty tracklist on an album Deezer says HAS tracks is a failure, not an
            // answer. Note the null check is load-bearing: Int() returns int?, and a lifted
            // `nbTracks > 0` is FALSE when nb_tracks is absent, so testing that alone would
            // let the empty result through and cache it exactly as before.
            if (tracks.Count == 0 && (nbTracks is null || nbTracks > 0))
            {
                _logger.LogWarning(
                    "deezer album '{Title}' ({Id}) reports {Expected} track(s) but returned none; not caching",
                    title, deezerId, nbTracks?.ToString() ?? "an unknown number of");
                return null;
            }

            tracks = tracks
                .OrderBy(t => t.DiscNumber ?? 1)
                .ThenBy(t => t.TrackPosition ?? int.MaxValue)
                .ToList();

            detail = new AlbumDetail(deezerId, title, artist,
                string.IsNullOrEmpty(cover) ? null : cover, year,
                string.IsNullOrEmpty(genre) ? null : genre,
                string.IsNullOrEmpty(label) ? null : label,
                tracks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer album detail {Id} failed: {M}", deezerId, ex.Message);
        }

        Cache(_albumDetailCache, deezerId, detail);
        return detail;
    }

    private Task<(int? Year, bool Transient)> AlbumYearAsync(long albumId, CancellationToken ct)
    {
        if (_albumYearCache.TryGetValue(albumId, out var y)) return Task.FromResult<(int?, bool)>((y, false));
        // Shared across concurrent callers for the same album id (single-flight).
        return _albumYearTasks.GetOrAdd(albumId,
            id => new Lazy<Task<(int? Year, bool Transient)>>(() => FetchAlbumYearAsync(id))).Value;
    }

    private async Task<(int? Year, bool Transient)> FetchAlbumYearAsync(long albumId)
    {
        int? year = null;
        using var r = await GetJsonAsync($"{Base}/album/{albumId}", CancellationToken.None);
        _albumYearTasks.TryRemove(albumId, out _);

        // This write bypasses Cache() and is the one that used to make a throttled
        // year lookup permanent.
        if (r.Transient) return (null, true);

        var rd = r.Doc is null ? null : Str(r.Doc.RootElement, "release_date");
        if (!string.IsNullOrEmpty(rd) && rd.Length >= 4 && int.TryParse(rd[..4], out var yr))
            year = yr;
        _albumYearCache[albumId] = year;
        return (year, false);
    }

    private async Task<DeezerResponse> GetJsonAsync(string url, CancellationToken ct)
    {
        JsonDocument? doc = null;
        try
        {
            using var resp = await Client().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new DeezerResponse { Transient = true };

            var s = await resp.Content.ReadAsStringAsync(ct);
            doc = JsonDocument.Parse(s);

            // Deezer reports throttling as 200 + {"error":{"code":4,...}}, which parses
            // perfectly and then reads as "the album has no tracks". Catching it here is
            // what stops a quota blip becoming permanent cached state.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object)
            {
                var code = Int(err, "code");
                var definitive = code == DefinitiveErrorCode;
                _logger.LogWarning("deezer refused {Url}: {Type} \"{Msg}\" (code {Code}, treated as {Kind})",
                    url, Str(err, "type"), Str(err, "message"), code, definitive ? "definitive" : "transient");
                doc.Dispose();
                return new DeezerResponse { Transient = !definitive };
            }

            var ok = new DeezerResponse { Doc = doc };
            doc = null;
            return ok;
        }
        catch (Exception ex)
        {
            doc?.Dispose();
            _logger.LogDebug("deezer request {Url} failed: {M}", url, ex.Message);
            return new DeezerResponse { Transient = true };
        }
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
