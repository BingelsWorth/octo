# Radio implementation TODO

> Temporary implementation brief. Delete this root-level file after the feature,
> tests, and permanent documentation are complete.

## What we are building

Radio is enabled by default and requires no station-creation step from a listener.
Octo observes qualified Subsonic scrobbles, builds a per-user taste profile, refreshes
stations in the background, and automatically adds ready stations to that user's
playlist list.

There are three related but distinct ways Radio chooses music:

1. **Start radio from a song** — the existing `getSimilarSongs[2]` behavior creates a
   one-time client queue seeded by one song; it is not a reusable station.
2. **Dynamic stations** — learned automatically from a user's recent artists and
   tags: Starter/Your Mix, Discovery Mix, up to two useful artist neighborhoods, and
   up to three non-overlapping genre stations.
3. **Pinned stations** — fixed categories selected in the existing Last.fm admin
   page. Their names and tag seeds are static; their track queues continue to
   refresh. Examples are Rock, Jazz, Electronic, or a custom `electronic + idm`
   station.

All station tracks use Octo's current local-first behavior. An accessible Navidrome
copy wins; otherwise Octo returns its existing external placeholder and uses the
YouTube preview / heart-acquisition paths already in the project. Radio never downloads
an entire station automatically.

### Core architecture constraint

Radio is a core Octo feature, not a sidecar or microservice:

- All Last.fm radio models, services, controllers, persistence, scheduling, and admin
  APIs are compiled into the existing `octo` ASP.NET project and registered in its
  current DI container. New implementation files live under the existing
  `Services/LastFm` area where practical.
- `LastFmRadioRefreshWorker` is an in-process hosted service, like Octo's existing workers.
  It does not have its own executable, container, HTTP API, port, health check, or
  deployment lifecycle.
- Radio uses the existing `/app/config` bind mount for state, the existing typed
  `LastFmService` HTTP path for recommendations, the existing metadata service for
  placeholders, and the already-present yt-dlp shim for playback/prewarming.
- `docker-compose.yml` may gain additional `LastFm__*` environment mappings only. It
  must not gain another service, image, volume topology, exposed port, or
  inter-service protocol.
- Internal boundaries remain ordinary C# interfaces/classes and direct DI calls. Do
  not introduce localhost HTTP, a message broker, RPC, or serialization between Radio
  components.
- Radio health/status is derived from its in-process state and existing provider
  probes; it is not presented as another infrastructure dependency.

### Default lifecycle

- `LastFm.EnablePersonalizedStations = true`
- `LastFm.EnableDiscoveryStations = true`
- Pinned discovery starts with no arbitrary genres selected; the UI offers presets.
- A new user is bootstrapped from their own accessible stars/frequent library data.
- With too little signal, Octo offers honestly named **Starter Radio** rather than an
  empty or falsely personalized mix.
- Crossing the minimum-play threshold automatically turns that stable station into
  **Your Mix** and schedules the other applicable personalized stations.
- Clients receive the last good snapshot immediately. Stale snapshots refresh in the
  background and appear updated the next time the client refreshes playlists.
- Recommendation snapshots store canonical artist/title candidates and can refresh
  without Navidrome credentials. Local-first materialization happens only with
  transient credentials from an authenticated client request; credentials are never
  written into Radio state.
- Disabling Radio hides stations and stops learning/refresh work without deleting
  history or downloaded music.

### Privacy and identity

- History stays in `/app/config`; Last.fm receives only the artist/title/tag lookups
  needed for recommendations.
- Profiles are isolated by the authenticated Navidrome user, not by client app.
- The request's `u` parameter is not trusted until Navidrome accepts its credentials.
- Station IDs are opaque and ownership-checked; they never contain a username.
- The admin API exposes derived station/status information, not the raw play ledger.
- No Last.fm user account or Last.fm scrobbling is required.

## How this fits Octo today

This is the implementation map. Prefer these existing seams over parallel abstractions.

| Concern | Current Octo convention | Radio integration |
|---|---|---|
| Configuration | POCOs in `Models/Settings`, `Configure<T>` in `Program.cs`, defaults in `appsettings.json` | Extend `LastFmSettings`; use `IOptionsMonitor` anywhere the admin UI promises live changes |
| Settings persistence | `SettingsFileWriter` deep-merges partial JSON and replaces arrays atomically | Save `LastFm.DiscoveryStations` as one ordered JSON array; preserve stable IDs and full entries |
| Admin projections | `AdminController` exposes form settings, raw config, and config-source keys separately | Extend `LastFm` in all three; do not make the UI show a value the runtime has not adopted |
| Environment installs | `.env.example` feeds `docker-compose.yml`; `install.sh` writes the initial `.env` | Add scalar `LastFm` Radio defaults to all applicable surfaces; complex discovery rows remain JSON/admin managed |
| Local persistence | `DownloadHistoryService` uses a locked, bounded, cached JSON file beside `settings.json` with temp-file rename | Follow that pattern in one versioned `LastFmRadioStateStore`; do not add a database package for the first release |
| Shared work | `SingleFlight<TKey,TValue>`, bounded semaphores, queue/hosted-worker patterns | Collapse same-profile refreshes in the existing process and keep recommendation fan-out off request threads |
| Last.fm | `LastFmService` owns API parsing, language header, caching, and best-effort failure | Extend it for similar artists, top tags, tag top tracks, and track info; do not create another Last.fm client |
| Seed cleanup | `NormalizeSeedArtist`/`NormalizeSeedTitle` are private controller helpers | Extract a reusable radio normalizer and keep current song-radio behavior byte-for-byte compatible |
| Track resolution | `TryFindLocalMatchAsync` then `IMusicMetadataService` creates external placeholders | Extract `LastFmRadioTrackResolver`; recommendation work stores canonical candidates, then authenticated request work resolves permission-safe local/external songs |
| External IDs | `ExternalIdRegistry` creates short deterministic client-safe IDs but stores routes in memory | Persist canonical routing with snapshots and re-register it on load; never persist stream URLs |
| Serialization | `SubsonicResponseBuilder` converts `Song` consistently for JSON/XML | Add playlist list/detail builders there; keep protocol shapes out of recommendation logic |
| Proxy/auth | `SubsonicProxyService` relays to Navidrome and preserves native auth/status | Merge only after a successful upstream response; authenticate locally served station detail before returning it |
| Queue warming | `RadioQueueStore` plus `PrewarmYouTubeIdsForSongIdsAsync` handles sliding windows | Register every served station queue and reuse the same bounded top-window prewarm |
| Client modes | Explicit Subsonic routes plus focused `/api/*` injections in `GenericEndpoint` | Support Subsonic playlists and the native Navidrome playlist routes already used by Feishin |
| Admin UI | One vanilla HTML page, hash panes, inline 1.5px SVG icons, `Section.Key` names, independent `set-card` forms | Expand the existing `#lastfm` pane; reuse its tokens, save/toast flow, source-order row pattern, and responsive breakpoint |
| Tests | xUnit/Moq, `TestOptions.Monitor`, focused regression names, JSON/XML symmetry tests | Add focused service tests and controller integration tests; avoid one giant end-to-end fixture |

`ExternalPlaylist` and `PlaylistSyncService` are intentionally not the Radio model.
They represent provider playlists and permanent `.m3u` acquisition (and contain stale
Deezer/Qobuz assumptions). Generated, read-only station snapshots need a focused model.

## Configuration contract

```json
"LastFm": {
  "ApiKey": "",
  "EnableRadio": true,
  "RadioTrackCount": 50,
  "RadioCacheDurationHours": 24,
  "EnablePersonalizedStations": true,
  "EnableDiscoveryStations": true,
  "HistoryRetentionDays": 90,
  "DiscoveryPercent": 35,
  "RefreshIntervalHours": 12,
  "MinimumPlays": 10,
  "DiscoveryStations": [
    {
      "Id": "generated-stable-id",
      "Name": "Electronic Discovery",
      "Enabled": true,
      "Tags": ["electronic", "electronica", "idm"]
    }
  ]
}
```

Effective-value rules belong next to the settings model, similar to
`SubsonicSettings.EffectiveHeartDownloadSources()`:

- retention: 7–365 days; station size: 10–100; refresh: 1–168 hours;
  minimum plays: 3–100; discovery: 0–100 percent;
- at most 12 pinned stations and 5 normalized tags per station;
- a generated ID is stable across rename/reorder and never derived from array index;
- invalid raw/env values clamp or fall back safely; the admin form reports invalid
  names, duplicates, empty tags, and over-limit collections before saving.

## Implementation phases

Each checkbox is an implementation-sized deliverable. The prose beneath it is part of
its acceptance criteria, not a separate micro-task.

### Phase 0 — protect and extract existing behavior

- [x] Run the existing .NET and shim test suites before implementation and record the
  baseline; do not fold unrelated worktree changes into Radio.
- [x] Extract the existing artist/title cleanup into a reusable radio seed normalizer,
  and keep regression coverage for collaborations and feature decorations.
- [x] Extract local-first lookup/external placeholder creation from
  `SubSonicController` into `LastFmRadioTrackResolver`, carrying caller authentication so
  Navidrome music-folder permissions remain authoritative.
- [x] Add playlist list/detail serialization primitives to `SubsonicResponseBuilder`
  using its existing song conversion, namespace, version, and JSON/XML conventions.
- [x] Keep current `getSimilarSongs[2]`, search, stream, star, cover-art, and prewarm
  behavior green after the extractions before adding personalized behavior.

### Phase 1 — settings and state

- [x] Extend `LastFmSettings` and add `DiscoveryStationSettings` with documented
  defaults, effective-value normalization, stable discovery IDs, and bounded
  collection rules; do not introduce a competing top-level settings section.
- [x] Add matching Last.fm defaults to `appsettings.json`; all runtime registrations
  stay in the existing web host and the existing `LastFm` configuration binding.
- [x] Extend AdminController's settings response, raw-config projection, and
  config-source list with exact PascalCase keys, matching the existing casing rules.
- [x] Add scalar Last.fm-radio environment mappings to `.env.example`,
  `docker-compose.yml`, and the non-interactive portion of `install.sh`; keep the
  ordered discovery array in settings JSON because shell encoding would be brittle.
  Do not add a Compose service, image, port, or separate runtime.
- [x] Convert radio-relevant captured `IOptions<LastFmSettings>` values to
  `IOptionsMonitor`, or explicitly mark a setting restart-required; saved UI and
  runtime values must agree.
- [x] Add one singleton `LastFmRadioStateStore` under `Services/LastFm`, following
  `DownloadHistoryService`: a
  versioned, locked, bounded in-memory model persisted atomically as
  `/app/config/lastfm-radio-state.json` with best-effort recovery from missing/corrupt
  files.
- [x] Persist bounded per-user play history, station snapshots, refresh timestamps,
  definition versions, canonical candidates, and external routing—never auth secrets,
  raw request parameters, stream URLs, or unbounded provider payloads.

### Phase 2 — listening signals and profiles

- [x] Extend scrobble handling without changing its relay/prewarm guarantees: record
  credible completed plays, tolerate omitted `submission`, and deduplicate common
  start/end duplicates.
- [x] Extend `SubsonicRequestParser` with a focused repeated-value reader for `id`
  instead of changing its existing dictionary contract; cover query and form batches.
- [x] Resolve local and external scrobbles to normalized artist/title/album/genre and
  store UTC time plus source; a metadata miss must not break upstream scrobbling.
- [x] Isolate profiles by successfully authenticated user and handle renamed/deleted
  users as orphan cleanup, never by merging histories.
- [x] Bootstrap a new profile, using that caller's auth, from bounded `getStarred2` and
  frequent/recent library results; fall back to accessible random local seeds for
  Starter Radio without claiming they are learned preferences.
- [x] Build the taste profile inside `LastFmRadioRecommendationService` with time decay,
  repeat caps, normalized artist names, tag aliases, and a denylist for non-genre
  Last.fm tags such as `seen live` or `favorites`.
- [x] Treat hearts as an optional strong positive signal only when it can be captured
  without coupling Radio to acquisition success; do not infer dislike from a skip
  without reliable playback-position evidence.

### Phase 3 — recommendation and station generation

- [x] Extend `LastFmService` with cached, cancellable methods for artist similarity,
  artist/track top tags, tag top tracks, and track info, following its current tolerant
  JSON parsing and failure logging.
- [x] Add explicit refresh budgets: bounded Last.fm calls, candidate counts,
  parallelism, elapsed time, and rate-limit handling. A provider problem returns a
  partial/fallback result rather than taking station playback down.
- [x] Generate **Your Mix** from varied weighted artist/track/tag seeds, balancing
  familiar and new candidates with `LastFm.DiscoveryPercent`.
- [x] Generate **Discovery Mix**, up to two distinct artist-neighborhood stations, and
  up to three meaningful genre stations only when each has enough unique artists and
  low overlap with already selected stations.
- [x] Generate every enabled pinned discovery station from its ordered tag set,
  sharing Last.fm candidate caches across users; keep the canonical result independent
  from any one user's Navidrome permissions.
- [x] Rank and shape all queues consistently: normalize/dedupe artist-title pairs,
  exclude seeds where appropriate, suppress recent plays, apply existing explicit
  filtering, space artists/albums, prefer accessible local matches, and cap station
  overlap.
- [x] Keep station and track order deterministic within one installed snapshot; a
  refresh may vary seeds, but repeated reads must not reshuffle underneath a client.
- [x] Rehydrate external routes through `ExternalIdRegistry` after restart and
  materialize canonical candidates through `LastFmRadioTrackResolver` only with current,
  successfully authenticated request parameters. Revalidate cached local IDs so
  rescans, deletion, or permission changes do not leave dead/unauthorized entries.

### Phase 4 — automatic refresh lifecycle

- [x] Add a bounded `LastFmRadioRefreshQueue` plus hosted
  `LastFmRadioRefreshWorker` under `Services/LastFm`, mirroring the repository's
  in-process queue/worker separation; use `SingleFlight` per user/station so concurrent
  triggers share work. Register the worker on Octo's existing host and create a DI
  scope per job for scoped proxy services—no IPC or standalone worker.
- [x] Trigger work automatically when bootstrap succeeds, the user crosses minimum
  plays, enough new plays accumulate, a snapshot expires, or a pinned definition
  changes; changing one pinned station must not rebuild unrelated stations.
- [x] Load snapshots at startup and enqueue stale known profiles with jitter and
  bounded concurrency so a restart does not stampede Last.fm. Startup may refresh
  credential-free recommendation candidates, but local materialization waits for the
  next authenticated request.
- [x] Serve stale-but-good snapshots while refresh runs and replace them only after a
  sufficiently complete build; failed refreshes retain the prior snapshot and expose
  a useful status.
- [x] Make disabled behavior exact: personalized and pinned toggles independently hide
  their stations and stop their work without deleting state; re-enable resumes it.
- [x] Define the first release as single-writer state. Document that multiple Octo
  instances cannot share `lastfm-radio-state.json` until cross-process locking exists.

### Phase 5 — client-facing API

- [x] Add explicit `getPlaylists[.view]` routes: relay first to validate auth, parse the
  successful upstream JSON/XML, and append the current user's ready Octo stations
  without changing Navidrome playlist order or fields. Pass a short-lived sanitized
  auth context to queued local materialization when a snapshot needs it; never log or
  persist that context.
- [x] Add explicit `getPlaylist[.view]` handling for a reserved opaque Radio ID;
  authenticate against Navidrome, verify ownership, and relay every non-Radio ID
  unchanged.
- [x] Return stable playlist metadata including owner, song count, duration,
  created/changed times, cover art, OpenSubsonic `readonly=true`, and `validUntil`;
  update `changed` only when a new snapshot is installed.
- [x] Guard `createPlaylist`, `updatePlaylist`, and `deletePlaylist` for reserved Radio
  IDs with a clear read-only error while passing all ordinary playlist mutations
  through unchanged.
- [x] Add focused native Navidrome parity in `GenericEndpoint` for `api/playlist`,
  `api/playlist/{id}`, and `api/playlist/{id}/tracks`, following the existing native
  JSON injection/header/paging style used for Feishin; guard native mutations too.
- [x] Register each served station with `RadioQueueStore` and prewarm only the existing
  bounded leading window through `IMusicMetadataService`.
- [x] Add a stable Octo-branded Radio cover-art ID/path through the existing cover-art
  handler; do not build live mosaics on playlist requests.
- [x] Keep no-key/outage behavior useful: last snapshot first, then local-only genre
  matching where possible, then an explicit learning/degraded admin state rather than
  a protocol error.

### Phase 6 — expand the Last.fm admin page

- [x] Keep the existing Last.fm sidebar item and hash-addressable
  `section[data-pane="lastfm"]`; expand that pane with the current page header,
  `set-section`, and `set-card` structure rather than adding a Radio navigation item,
  framework, palette, or type system.
- [x] Keep the API key and existing start-from-a-song controls on Last.fm, organizing
  the page into clear radio basics, station playback methods, dynamic stations,
  pinned stations, and station status sections. Explain that only dynamic and pinned
  stations use the playlist/continuous-stream publication
  settings. Preserve every existing `LastFm.*` field name.
- [x] Add a station overview backed by focused `/api/admin/lastfm/radio` status and
  refresh endpoints: user selector when needed, learning progress,
  bootstrap/history source, seeds, track count, last success/failure, compact preview,
  and automatic refresh state without requiring a manual refresh action.
- [x] Add personalized settings using normal independent save cards and new
  `LastFm.*` field names; put refresh/minimum-play tuning under native `<details>`
  progressive disclosure.
- [x] Add a pinned discovery editor using existing settings-card controls:
  preset/custom tags, stable hidden ID, enable, rename, and remove.
  Do not show ordering controls because Subsonic clients choose how station and
  playlist collections are displayed.
- [x] Save the full discovery array via the existing `data-json="true"` convention,
  preserving unknown fields; validate inline and confirm removal without deleting
  history or downloaded tracks.
- [x] Cover ready, learning, refreshing, degraded, empty, and missing-Last.fm-key states
  with visible text, `aria-live` feedback, keyboard focus, existing semantic colors,
  44px actions, the current mobile breakpoint, and reduced-motion behavior.
- [x] Add confirmed per-user history reset through a focused admin endpoint; it clears
  Radio state only and reports exactly what was removed and that downloaded music is
  untouched.

### Phase 7 — continuous Subsonic radio streams

- [x] Add independent `LastFm.ExposeRadioAsPlaylists` and
  `LastFm.ExposeRadioAsStreams` settings, both enabled by default, so deployments may
  publish either representation or both without changing recommendation state.
- [x] Add a bounded `LastFm.RadioStreamBitrateKbps` setting with a safe default and
  explicit supported qualities; playlist playback keeps each track's normal
  local-first quality while internet radio is continuously transcoded.
- [x] Intercept authenticated `getInternetRadioStations[.view]`, relay first, and merge
  the current listener's ready Octo stations into valid JSON and XML without
  replacing ordinary Navidrome internet-radio entries.
- [x] Issue opaque, expiring, station/user-scoped stream sessions without putting
  Navidrome credentials or usernames in stream URLs; bound and prune the in-memory
  session store and ownership-check every stream open.
- [x] Serve a cancellation-aware continuous MP3 response from the core Octo process,
  cycling the ready snapshot and resolving each track through the existing local-first
  path; normalize each source to the configured bitrate, prewarm ahead, and skip an
  unavailable track without ending the station.
- [x] Fully transcode at least one local-first starter song into Octo's bounded temporary
  cache before publishing each continuous station in the same station-list response,
  because clients may not poll again. Share concurrent preparation, try fallback
  tracks, expand to a three-song background runway, and never turn preparation into a
  permanent library acquisition.
- [x] Warm persisted station runways in core Octo at application startup, retry missing
  readiness during the existing minute scan, and queue another warm after the internal
  refresh worker installs a new snapshot. Use external previews before login because
  listener credentials remain memory-only; keep authenticated replenishment local-first.
- [x] Treat an unresolvable or untranscodable song as unavailable: remove it from every
  current station, retain a bounded 24-hour negative-availability cooldown so a
  deterministic rebuild cannot immediately restore it, and queue the existing core
  refresh worker with candidate headroom to refill toward `RadioTrackCount`.
- [x] Record qualified track completions from continuous streams for Radio learning and
  relay their scrobbles to Navidrome, because the client sees one station URL rather
  than individual Subsonic track requests.
- [x] Keep generated internet-radio entries read-only while preserving ordinary
  create/update/delete passthrough behavior.
- [x] Add Last.fm admin controls and help that explain both independent modes: playlists
  preserve normal playback quality and support hearts for a permanent lossless copy;
  internet radio is continuous and transcoded to the selected quality.
- [ ] Add JSON/XML/session/auth/cancellation/failure/quality fixtures and manually verify
  the continuous endpoint in Arpeggi while confirming playlist publication still works.

### Phase 8 — verification, documentation, and cleanup

- [x] Add state-store tests for version/load recovery, atomic writes, bounds/pruning,
  concurrency, per-user isolation, disabled persistence, and route rehydration.
- [x] Extend `LastFmServiceTests` with captured HTTP fixtures for every new method,
  malformed/empty responses, caching, cancellation, language, and rate limiting.
- [x] Add deterministic recommendation tests for decay/repeat caps, aliases/denylist,
  discovery ratios, pinned multi-tag merge, dedupe, overlap suppression, explicit
  filtering, artist spacing, local-first selection, and sparse-history fallback.
- [x] Add refresh tests for trigger thresholds, single-flight behavior, jittered startup,
  stale serving, failed replacement, definition invalidation, and independent toggles.
- [x] Add controller tests for scrobble batches plus Subsonic playlist JSON/XML merge,
  detail, auth failure, ownership, read-only mutations, stable metadata, and prewarm.
- [ ] Add native API fixtures for Feishin-shaped list/detail/track responses, paging and
  headers; manually verify at least Feishin/Arpeggi plus one conventional Subsonic
  client and document any client that requires manual playlist refresh.
- [ ] Verify the Radio pane at desktop/mobile widths with keyboard and screen-reader
  basics, then run `dotnet test octo.sln` and the yt-dlp-shim tests.
- [ ] Update README behavior/privacy/API tables, `.env.example`, compose comments,
  admin raw-config help, and release notes; follow the dated version convention when
  releasing, then delete `RADIO_TODO.md`.

## Deferred, not hidden scope

- Optional Last.fm username import and Navidrome-to-Last.fm user mapping.
- User-created artist-seed stations, negative seeds, “less like this,” or manual seed
  weights. Pinned tag stations and learned artist neighborhoods cover v1.
- Mood/BPM/audio-feature stations, household-shared taste profiles, weekly archives,
  live cover mosaics, or automatic station downloads.
- Multi-instance shared-state support or a database migration.

## Delete-this-file gate

Remove this TODO only when every phase checkbox is complete, the permanent docs carry
the surviving behavior/configuration details, the full automated suite passes, and the
supported-client smoke test confirms stations are automatically offered and playable.
The final Compose topology must contain no new Radio service or sidecar.
