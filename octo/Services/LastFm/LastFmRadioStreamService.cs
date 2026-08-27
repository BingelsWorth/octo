using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Local;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Services.LastFm;

/// <summary>Turns a ready generated station into one long MP3 response. Recommendation
/// state and stream orchestration stay in core Octo; ffmpeg is only a codec adapter.</summary>
public sealed class LastFmRadioStreamService
{
    public const int ReadyPoolSize = 3;
    private static readonly SemaphoreSlim ConcurrentStreams = new(8, 8);
    private readonly LastFmRadioStateStore _state;
    private readonly IOptionsMonitor<LastFmSettings> _settings;
    private readonly ILocalLibraryService _library;
    private readonly SubsonicProxyService _proxy;
    private readonly IDownloadService _downloads;
    private readonly ILastFmRadioAudioTranscoder _transcoder;
    private readonly LastFmRadioTrackCache _cache;
    private readonly LastFmRadioStreamSessionStore _sessions;
    private readonly LastFmRadioTrackResolver _resolver;
    private readonly IMusicMetadataService _metadata;
    private readonly RadioQueueStore _queues;
    private readonly LastFmRadioRefreshQueue _refreshQueue;
    private readonly ILogger<LastFmRadioStreamService> _logger;
    private readonly ConcurrentDictionary<string, Task> _poolWarmers = new();

    public LastFmRadioStreamService(LastFmRadioStateStore state,
        IOptionsMonitor<LastFmSettings> settings, ILocalLibraryService library,
        SubsonicProxyService proxy, IDownloadService downloads,
        ILastFmRadioAudioTranscoder transcoder, LastFmRadioTrackCache cache,
        LastFmRadioStreamSessionStore sessions,
        LastFmRadioTrackResolver resolver,
        IMusicMetadataService metadata,
        RadioQueueStore queues, LastFmRadioRefreshQueue refreshQueue,
        ILogger<LastFmRadioStreamService> logger)
    {
        _state = state; _settings = settings; _library = library; _proxy = proxy;
        _downloads = downloads; _transcoder = transcoder; _cache = cache;
        _sessions = sessions;
        _resolver = resolver; _metadata = metadata;
        _queues = queues; _refreshQueue = refreshQueue; _logger = logger;
    }

    public LastFmRadioStation? Resolve(LastFmRadioStreamSession session)
    {
        if (!_settings.CurrentValue.EnableRadio || !_settings.CurrentValue.ExposeRadioAsStreams)
            return null;
        var station = _state.FindStation(session.Username, session.StationId);
        if (station is not null && (station.Personalized
                ? !_settings.CurrentValue.EnablePersonalizedStations
                : !_settings.CurrentValue.EnableDiscoveryStations)) return null;
        return station is { Tracks.Count: > 0 } ? station : null;
    }

    public async Task StreamAsync(LastFmRadioStreamSession session, Stream output,
        CancellationToken cancellationToken)
    {
        await ConcurrentStreams.WaitAsync(cancellationToken);
        try
        {
            var station = Resolve(session)
                ?? throw new InvalidOperationException("Radio station is no longer available");
            var tracks = station.Tracks.Where(track => !string.IsNullOrWhiteSpace(track.ResolvedId)).ToList();
            if (tracks.Count == 0) throw new InvalidOperationException("Radio station has no playable tracks");
            var ids = tracks.Select(track => track.ResolvedId!).ToList();
            _queues.Register(ids);
            _ = _metadata.PrewarmYouTubeIdsForSongIdsAsync(ids, topN: 8);
            // A published session starts with three complete MP3 segments. Keep those
            // exact tracks even if the recommendation snapshot changes before tune-in;
            // the next replenishment crosses onto the current snapshot cleanly.
            var ready = (session.ReadyPool ?? [])
                .Where(item => _cache.IsReadyPath(item.Path)).ToList();
            if (ready.Count == 0) ready = GetReadyPool(session).ToList();
            if (ready.Count == 0)
                ready = (await PrepareReadyPoolAsync(session, 1, cancellationToken)).ToList();
            if (ready.Count == 0)
                throw new InvalidOperationException("Radio station has no cached ready track");

            var queue = new Queue<PreparedRadioTrack>(ready);
            var nextIndex = (ready[^1].Index + 1) % tracks.Count;
            var failures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (queue.Count == 0)
                {
                    var emergency = await PrepareNextAsync(session, nextIndex,
                        new HashSet<string>(StringComparer.Ordinal),
                        cancellationToken);
                    if (emergency is null)
                        throw new InvalidOperationException(
                            "No tracks in this Radio snapshot have a playable source");
                    queue.Enqueue(emergency);
                    nextIndex = emergency.Index + 1;
                }

                var prepared = queue.Dequeue();
                _sessions.ConsumeReadyTrack(session.Token, prepared.CacheKey);
                var reserved = queue.Select(item => item.CacheKey).ToHashSet(StringComparer.Ordinal);
                var replenishment = PrepareNextAsync(session, nextIndex, reserved,
                    CancellationToken.None);
                _ = PersistReplenishmentAsync(session.Token, replenishment);

                try
                {
                    await using (var cached = _cache.OpenRead(prepared.Path))
                        await cached.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    failures = 0;
                    var song = await _resolver.ResolveAsync(prepared.Track.Artist,
                        prepared.Track.Title, prepared.Track.Duration, session.Authentication,
                        cancellationToken);
                    if (song is not null)
                        await RecordCompletionAsync(session, prepared.Track, song, cancellationToken);

                    var replacement = await replenishment.WaitAsync(cancellationToken);
                    if (replacement is not null
                        && queue.All(item => item.CacheKey != replacement.CacheKey))
                    {
                        queue.Enqueue(replacement);
                        nextIndex = replacement.Index + 1;
                    }

                    var current = Resolve(session);
                    var upcomingTracks = current?.Tracks
                        .Where(track => !string.IsNullOrWhiteSpace(track.ResolvedId)).ToList() ?? [];
                    var upcoming = Enumerable.Range(0, Math.Min(8, upcomingTracks.Count))
                        .Select(offset => upcomingTracks[(nextIndex + offset) % upcomingTracks.Count]
                            .ResolvedId!)
                        .ToList();
                    _ = _metadata.PrewarmYouTubeIdsForSongIdsAsync(upcoming, topN: 8);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failures++;
                    RejectAndRefill(session, prepared.Track);
                    _logger.LogWarning(ex, "Skipping unavailable continuous Radio track {Artist} - {Title}",
                        prepared.Track.Artist, prepared.Track.Title);
                    if (failures >= tracks.Count)
                        throw new InvalidOperationException(
                            "No tracks in this Radio snapshot have a playable source", ex);
                }
            }
        }
        finally { ConcurrentStreams.Release(); }
    }

    /// <summary>Returns up to three current-snapshot tracks that already satisfy the
    /// existing radio-cache retention and size policy. This method never performs I/O
    /// beyond checking the cache, so station listings remain responsive.</summary>
    public IReadOnlyList<PreparedRadioTrack> GetReadyPool(LastFmRadioStreamSession session)
    {
        var ready = new List<PreparedRadioTrack>();
        foreach (var candidate in Candidates(session))
        {
            var path = _cache.GetReadyPath(candidate.Key);
            if (path is null) continue;
            ready.Add(candidate.Prepared(path));
            if (ready.Count == ReadyPoolSize) break;
        }
        return ready;
    }

    /// <summary>Starts one deduplicated background fill for this station snapshot.
    /// Request cancellation deliberately does not own cache production: a client that
    /// refreshes or navigates away must not discard work needed by its next listing.</summary>
    public void WarmReadyPool(LastFmRadioStreamSession session)
    {
        var station = Resolve(session);
        if (station is null) return;
        var key = string.Join('|', session.Username, station.Id, station.ChangedUtc.Ticks,
            _settings.CurrentValue.EffectiveRadioStreamBitrateKbps);
        _poolWarmers.GetOrAdd(key, ignoredKey => Task.Run(async () =>
        {
            try
            {
                var ready = await PrepareReadyPoolAsync(session, ReadyPoolSize,
                    CancellationToken.None);
                _logger.LogInformation("Radio ready pool {Ready}/{Target} for {Station}",
                    ready.Count, ReadyPoolSize, station.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not warm Radio ready pool for {Station}",
                    station.Name);
            }
            finally { _poolWarmers.TryRemove(key, out _); }
        }));
    }

    private async Task<IReadOnlyList<PreparedRadioTrack>> PrepareReadyPoolAsync(
        LastFmRadioStreamSession session, int target, CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedRadioTrack>();
        var rejectedAny = false;
        foreach (var candidate in Candidates(session))
        {
            try
            {
                prepared.Add(await PrepareCandidateAsync(session, candidate, cancellationToken));
                if (prepared.Count == target) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                rejectedAny |= _state.RejectTrack(session.Username, candidate.Track) > 0;
                _logger.LogWarning(ex,
                    "Could not prepare Radio pool track {Artist} - {Title}; trying the next track",
                    candidate.Track.Artist, candidate.Track.Title);
            }
        }
        if (rejectedAny) _refreshQueue.Enqueue(session.Username);
        return prepared;
    }

    private async Task<PreparedRadioTrack?> PrepareNextAsync(LastFmRadioStreamSession session,
        int startIndex, IReadOnlySet<string> reserved, CancellationToken cancellationToken)
    {
        var candidates = Candidates(session);
        if (candidates.Count == 0) return null;
        for (var offset = 0; offset < candidates.Count; offset++)
        {
            var candidate = candidates[(startIndex + offset) % candidates.Count];
            if (reserved.Contains(candidate.Key)) continue;
            try { return await PrepareCandidateAsync(session, candidate, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw; }
            catch (Exception ex)
            {
                RejectAndRefill(session, candidate.Track);
                _logger.LogWarning(ex,
                    "Could not replenish Radio pool with {Artist} - {Title}",
                    candidate.Track.Artist, candidate.Track.Title);
            }
        }
        return null;
    }

    private async Task<PreparedRadioTrack> PrepareCandidateAsync(
        LastFmRadioStreamSession session, RadioCandidate candidate,
        CancellationToken cancellationToken)
    {
        var bitrateKbps = _settings.CurrentValue.EffectiveRadioStreamBitrateKbps;
        var path = await _cache.GetOrCreateAsync(candidate.Key, async (output, token) =>
        {
            var opened = await OpenTrackAsync(candidate.Track, session.Authentication, token)
                ?? throw new InvalidOperationException("No playable source");
            await using (opened.Source.AudioStream)
                await _transcoder.TranscodeToMp3Async(opened.Source.AudioStream, output,
                    bitrateKbps, token);
        }, cancellationToken);
        return candidate.Prepared(path);
    }

    private List<RadioCandidate> Candidates(LastFmRadioStreamSession session)
    {
        var station = Resolve(session);
        if (station is null) return [];
        var bitrateKbps = _settings.CurrentValue.EffectiveRadioStreamBitrateKbps;
        return station.Tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.ResolvedId))
            .Select((track, index) => new RadioCandidate(track, index,
                _cache.Key(session.Username, station.Id, track.ResolvedId!, bitrateKbps)))
            .ToList();
    }

    private async Task PersistReplenishmentAsync(string token,
        Task<PreparedRadioTrack?> replenishment)
    {
        try
        {
            var track = await replenishment;
            if (track is not null) _sessions.AppendReadyTrack(token, track, ReadyPoolSize);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Radio pool replenishment ended without a ready track");
        }
    }

    private void RejectAndRefill(LastFmRadioStreamSession session, LastFmRadioTrack track)
    {
        if (_state.RejectTrack(session.Username, track) <= 0) return;
        _refreshQueue.Enqueue(session.Username);
    }

    private async Task<OpenedRadioTrack?> OpenTrackAsync(LastFmRadioTrack track,
        IReadOnlyDictionary<string, string> authentication, CancellationToken cancellationToken)
    {
        var song = await _resolver.ResolveAsync(track.Artist, track.Title, track.Duration,
            authentication, cancellationToken);
        if (song is null) return null;
        var id = song.Id;
        var (external, provider, externalId) = _library.ParseSongId(id);
        if (external)
        {
            var source = await _downloads.GetDirectStreamAsync(
                provider ?? song.ExternalProvider ?? track.ExternalProvider ?? "lastfm",
                externalId ?? song.ExternalId ?? id, null, cancellationToken);
            return source is null ? null : new OpenedRadioTrack(source, song);
        }

        var parameters = authentication.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        parameters["id"] = id;
        parameters["format"] = "raw";
        var localSource = await _proxy.OpenAudioStreamAsync(parameters, cancellationToken);
        return localSource is null ? null : new OpenedRadioTrack(localSource, song);
    }

    private async Task RecordCompletionAsync(LastFmRadioStreamSession session,
        LastFmRadioTrack track, Song song, CancellationToken cancellationToken)
    {
        var id = song.Id;
        _state.RecordPlay(session.Username, new LastFmRadioPlay
        {
            SongId = id, Artist = track.Artist, Title = track.Title, Album = track.Album,
            Genre = track.Genre ?? song.Genre, Duration = track.Duration ?? song.Duration,
            IsLocal = song.IsLocal,
            Source = "internet-radio", PlayedAtUtc = DateTime.UtcNow,
        });
        if (!song.IsLocal) return;
        var parameters = session.Authentication.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        parameters["id"] = id;
        parameters["submission"] = "true";
        parameters["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        await _proxy.RelaySafeAsync("rest/scrobble", parameters);
    }

    private sealed record RadioCandidate(LastFmRadioTrack Track, int Index, string Key)
    {
        public PreparedRadioTrack Prepared(string path) => new(path, Track, Index, Key);
    }

    private sealed record OpenedRadioTrack(DirectStreamInfo Source, Song Song);
}

public sealed record PreparedRadioTrack(
    string Path, LastFmRadioTrack Track, int Index, string CacheKey = "");
