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
    private static readonly SemaphoreSlim ConcurrentStreams = new(8, 8);
    private readonly LastFmRadioStateStore _state;
    private readonly IOptionsMonitor<LastFmSettings> _settings;
    private readonly ILocalLibraryService _library;
    private readonly SubsonicProxyService _proxy;
    private readonly IDownloadService _downloads;
    private readonly ILastFmRadioAudioTranscoder _transcoder;
    private readonly LastFmRadioTrackCache _cache;
    private readonly LastFmRadioTrackResolver _resolver;
    private readonly IMusicMetadataService _metadata;
    private readonly RadioQueueStore _queues;
    private readonly LastFmRadioRefreshQueue _refreshQueue;
    private readonly ILogger<LastFmRadioStreamService> _logger;

    public LastFmRadioStreamService(LastFmRadioStateStore state,
        IOptionsMonitor<LastFmSettings> settings, ILocalLibraryService library,
        SubsonicProxyService proxy, IDownloadService downloads,
        ILastFmRadioAudioTranscoder transcoder, LastFmRadioTrackCache cache,
        LastFmRadioTrackResolver resolver,
        IMusicMetadataService metadata,
        RadioQueueStore queues, LastFmRadioRefreshQueue refreshQueue,
        ILogger<LastFmRadioStreamService> logger)
    {
        _state = state; _settings = settings; _library = library; _proxy = proxy;
        _downloads = downloads; _transcoder = transcoder; _cache = cache;
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
            // Keep one connection at one declared bitrate. A saved quality change is
            // picked up by the next tune-in instead of changing encoder parameters
            // underneath a client that is already decoding the stream.
            var bitrateKbps = _settings.CurrentValue.EffectiveRadioStreamBitrateKbps;

            var ids = tracks.Select(track => track.ResolvedId!).ToList();
            _queues.Register(ids);
            _ = _metadata.PrewarmYouTubeIdsForSongIdsAsync(ids, topN: 8);
            // The session carries the exact starter that was complete before this
            // station URL was published. A recommendation refresh may replace the
            // snapshot between listing and tune-in; it must not invalidate that
            // already-proven startup path.
            var starter = session.Starter is { } published
                && _cache.IsReadyPath(published.Path)
                    ? published
                    : await PrepareAsync(session, cancellationToken)
                        ?? throw new InvalidOperationException(
                            "Radio station has no cached starter track");
            await using (var cached = _cache.OpenRead(starter.Path))
                await cached.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            var starterSong = await _resolver.ResolveAsync(starter.Track.Artist,
                starter.Track.Title, starter.Track.Duration, session.Authentication,
                cancellationToken);
            if (starterSong is not null)
                await RecordCompletionAsync(session, starter.Track, starterSong, cancellationToken);

            var index = (starter.Index + 1) % tracks.Count;
            var failures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Pick up a refreshed snapshot at the boundary without cutting off the
                // song currently playing. Preserve the current index modulo the new size.
                var current = Resolve(session);
                if (current is not null)
                {
                    var refreshed = current.Tracks
                        .Where(track => !string.IsNullOrWhiteSpace(track.ResolvedId)).ToList();
                    if (refreshed.Count > 0) { tracks = refreshed; index %= tracks.Count; }
                }
                var track = tracks[index];
                index = (index + 1) % tracks.Count;
                try
                {
                    var opened = await OpenTrackAsync(track, session.Authentication,
                        cancellationToken);
                    if (opened is null) throw new InvalidOperationException("No playable source");
                    await using (opened.Source.AudioStream)
                        await _transcoder.TranscodeToMp3Async(opened.Source.AudioStream, output,
                            bitrateKbps, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    failures = 0;
                    await RecordCompletionAsync(session, track, opened.Song, cancellationToken);

                    var upcoming = Enumerable.Range(0, Math.Min(8, tracks.Count))
                        .Select(offset => tracks[(index + offset) % tracks.Count].ResolvedId!)
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
                    RejectAndRefill(session, track);
                    _logger.LogWarning(ex, "Skipping unavailable continuous Radio track {Artist} - {Title}",
                        track.Artist, track.Title);
                    if (failures >= tracks.Count)
                        throw new InvalidOperationException(
                            "No tracks in this Radio snapshot have a playable source", ex);
                }
            }
        }
        finally { ConcurrentStreams.Release(); }
    }

    /// <summary>Ensures this station has one complete, immediately playable MP3.
    /// Callers publish only stations for which this succeeds.</summary>
    public async Task<PreparedRadioStarter?> PrepareAsync(LastFmRadioStreamSession session,
        CancellationToken cancellationToken)
    {
        var station = Resolve(session);
        if (station is null) return null;
        var bitrateKbps = _settings.CurrentValue.EffectiveRadioStreamBitrateKbps;
        var tracks = station.Tracks.Where(track => !string.IsNullOrWhiteSpace(track.ResolvedId)).ToList();
        var candidates = tracks.Select((track, index) =>
        {
            var identity = track.ResolvedId ?? $"{track.Artist}\n{track.Title}";
            var key = _cache.Key(session.Username, station.Id, identity, bitrateKbps);
            return (Track: track, Index: index, Key: key);
        }).ToList();
        var rejectedAny = false;

        // A later candidate may have been the first playable source on an earlier
        // request. Find any completed starter before retrying failed candidates.
        foreach (var candidate in candidates)
        {
            var readyPath = _cache.GetReadyPath(candidate.Key);
            if (readyPath is not null)
                return new PreparedRadioStarter(readyPath, candidate.Track, candidate.Index);
        }
        foreach (var candidate in candidates)
        {
            var track = candidate.Track;
            try
            {
                var path = await _cache.GetOrCreateAsync(candidate.Key, async (output, token) =>
                {
                    var opened = await OpenTrackAsync(track, session.Authentication, token)
                        ?? throw new InvalidOperationException("No playable source");
                    await using (opened.Source.AudioStream)
                        await _transcoder.TranscodeToMp3Async(opened.Source.AudioStream, output,
                            bitrateKbps, token);
                }, cancellationToken);
                if (rejectedAny) _refreshQueue.Enqueue(session.Username);
                return new PreparedRadioStarter(path, track, candidate.Index);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                rejectedAny |= _state.RejectTrack(session.Username, track) > 0;
                _logger.LogWarning(ex,
                    "Could not prepare Radio starter {Artist} - {Title}; trying the next track",
                    track.Artist, track.Title);
            }
        }
        if (rejectedAny) _refreshQueue.Enqueue(session.Username);
        return null;
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

    private sealed record OpenedRadioTrack(DirectStreamInfo Source, Song Song);
}

public sealed record PreparedRadioStarter(string Path, LastFmRadioTrack Track, int Index);
