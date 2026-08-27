using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.LastFm;
using Octo.Services.Soulseek;

namespace Octo.Tests;

public sealed class LastFmRadioControllerTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicPlaylistList_MergesReadyStationAfterSuccessfulAuthentication(string format)
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync($"/rest/getPlaylists?u=alice&t=token&s=salt&f={format}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Native Playlist", body);
        Assert.Contains("Your Mix", body);
        Assert.Contains(fixture.StationId, body);
        Assert.True(body.IndexOf("Native Playlist", StringComparison.Ordinal)
            < body.IndexOf("Your Mix", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicPlaylistDetail_IsOwnedReadOnlyStableAndPrewarmed(string format)
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(
            $"/rest/getPlaylist?id={fixture.StationId}&u=alice&t=token&s=salt&f={format}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Your Mix", body);
        Assert.Contains("local-one", body);
        Assert.Contains("readonly", body);
        Assert.Contains("validUntil", body);
        fixture.Metadata.Verify(service => service.PrewarmYouTubeIdsAsync(
            It.IsAny<IEnumerable<Octo.Models.Domain.Song>>(), 8, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubsonicPlaylistDetail_RejectsWrongUserAndReservedMutations()
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var wrongOwner = await client.GetStringAsync(
            $"/rest/getPlaylist?id={fixture.StationId}&u=bob&t=token&s=salt&f=json");
        Assert.DoesNotContain("Your Mix", wrongOwner);

        foreach (var endpoint in new[] { "updatePlaylist", "deletePlaylist" })
        {
            var body = await client.GetStringAsync(
                $"/rest/{endpoint}?playlistId={fixture.StationId}&u=alice&t=token&s=salt&f=json");
            Assert.Contains("read-only", body);
            Assert.Contains("failed", body);
        }
    }

    [Fact]
    public async Task ScrobbleBatch_PreservesRepeatedIdsAndLearnsOnlyCompletedAuthenticatedPlays()
    {
        await using var fixture = new RadioWebFactory();
        using var client = fixture.CreateClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("u", "alice"),
            new KeyValuePair<string,string>("t", "token"),
            new KeyValuePair<string,string>("s", "salt"),
            new KeyValuePair<string,string>("f", "json"),
            new KeyValuePair<string,string>("id", "one"),
            new KeyValuePair<string,string>("id", "two"),
            new KeyValuePair<string,string>("submission", "true"),
            new KeyValuePair<string,string>("submission", "true"),
        });
        using var response = await client.PostAsync("/rest/scrobble", content);
        response.EnsureSuccessStatusCode();
        Assert.Equal(["one", "two"], fixture.Handler.RelayedScrobbleIds);
        Assert.Equal(2, fixture.State.GetUser("alice").Plays.Count);

        using var duplicate = await client.PostAsync("/rest/scrobble", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("u", "alice"), new("f", "json"),
            new("id", "one"), new("submission", "true")
        }));
        duplicate.EnsureSuccessStatusCode();
        Assert.Equal(2, fixture.State.GetUser("alice").Plays.Count);

        using var startOnly = await client.PostAsync("/rest/scrobble", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("u", "alice"), new("f", "json"),
            new("id", "three"), new("submission", "false")
        }));
        startOnly.EnsureSuccessStatusCode();
        Assert.Equal(2, fixture.State.GetUser("alice").Plays.Count);
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotExposeStationsOrLearnScrobbles()
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var list = await client.GetStringAsync("/rest/getPlaylists?u=bad&f=json");
        Assert.DoesNotContain("Your Mix", list);
        await client.GetAsync("/rest/scrobble?u=bad&id=one&submission=true&f=json");
        Assert.Empty(fixture.State.GetUser("bad").Plays);
    }

    [Fact]
    public async Task PlaylistMaterialization_AppliesConfiguredExplicitFilter()
    {
        await using var fixture = new RadioWebFactory("CleanOnly");
        fixture.Handler.ReturnLocalMatches = false;
        fixture.Metadata.Setup(service => service.SearchSongsByArtistTitleAsync(
                It.IsAny<string>(), It.IsAny<string>(), 1, It.IsAny<int?>()))
            .ReturnsAsync((string artist, string title, int _, int? duration) =>
            [new Octo.Models.Domain.Song
            {
                Id = title == "Song One" ? "explicit" : "clean", Artist = artist, Title = title,
                Album = title, Duration = duration, IsLocal = false,
                ExplicitContentLyrics = title == "Song One" ? 1 : 0
            }]);
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var body = await client.GetStringAsync(
            $"/rest/getPlaylist?id={fixture.StationId}&u=alice&t=token&s=salt&f=json");
        using var document = JsonDocument.Parse(body);
        var ids = document.RootElement.GetProperty("subsonic-response").GetProperty("playlist")
            .GetProperty("entry").EnumerateArray().Select(song => song.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain("explicit", ids);
        Assert.Contains("clean", ids);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task InternetRadioList_MergesOrdinaryAndGeneratedStationsWithOpaqueStreamUrl(string format)
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var body = await client.GetStringAsync(
            $"/rest/getInternetRadioStations?u=alice&t=token&s=salt&f={format}");
        Assert.Contains("Native Radio", body);
        Assert.Contains("Your Mix", body);
        Assert.Contains("/radio/stream/", body);
        Assert.DoesNotContain("t=token", body);
        Assert.DoesNotContain("u=alice", body);
    }

    [Fact]
    public async Task InternetRadioList_PreparesStarterOnceBeforePublishingStation()
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var url = "/rest/getInternetRadioStations?u=alice&t=token&s=salt&f=json";

        Assert.Contains("Your Mix", await client.GetStringAsync(url));
        Assert.Equal(1, fixture.Transcoder.Calls);
        Assert.Contains("Your Mix", await client.GetStringAsync(url));
        Assert.Equal(1, fixture.Transcoder.Calls);
    }

    [Fact]
    public async Task PlaylistAndInternetRadioPublication_AreIndependentAndReservedRadioIsReadOnly()
    {
        await using var fixture = new RadioWebFactory(exposePlaylists: false, exposeStreams: true);
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var playlists = await client.GetStringAsync(
            "/rest/getPlaylists?u=alice&t=token&s=salt&f=json");
        Assert.DoesNotContain("Your Mix", playlists);
        var radios = await client.GetStringAsync(
            "/rest/getInternetRadioStations?u=alice&t=token&s=salt&f=json");
        Assert.Contains("Your Mix", radios);
        var mutation = await client.GetStringAsync(
            $"/rest/deleteInternetRadioStation?id={fixture.StationId}&u=alice&t=token&s=salt&f=json");
        Assert.Contains("read-only", mutation);
    }

    [Fact]
    public async Task OpaqueInternetRadioUrl_StreamsMp3AndCancelsWhenClientDisconnects()
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var listBody = await client.GetStringAsync(
            "/rest/getInternetRadioStations?u=alice&t=token&s=salt&f=json");
        using var list = JsonDocument.Parse(listBody);
        var url = list.RootElement.GetProperty("subsonic-response")
            .GetProperty("internetRadioStations").GetProperty("internetRadioStation")
            .EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Your Mix")
            .GetProperty("streamUrl").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var bytes = new byte[3];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal("MP3", Encoding.ASCII.GetString(bytes));
        Assert.Equal(192, fixture.Transcoder.LastBitrateKbps);
        for (var attempt = 0; attempt < 50 && fixture.State.GetUser("alice").Plays.Count == 0;
             attempt++)
            await Task.Delay(10);
        Assert.Single(fixture.State.GetUser("alice").Plays);
        Assert.Single(fixture.Handler.RelayedScrobbleIds);
    }

    [Fact]
    public async Task InternetRadioPreparation_SkipsFailedSourceAndCachesPlayableFallback()
    {
        await using var fixture = new RadioWebFactory();
        fixture.Transcoder.FailuresBeforeSuccess = 1;
        fixture.Transcoder.CompleteCalls = 1;
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        var listBody = await client.GetStringAsync(
            "/rest/getInternetRadioStations?u=alice&t=token&s=salt&f=json");
        Assert.Contains("Your Mix", listBody);
        Assert.Equal(2, fixture.Transcoder.Calls);
        Assert.Contains("Your Mix", await client.GetStringAsync(
            "/rest/getInternetRadioStations?u=alice&t=token&s=salt&f=json"));
        Assert.Equal(2, fixture.Transcoder.Calls);
    }
}

public sealed class LastFmRadioNativeApiTests
{
    [Fact]
    public async Task FeishinListDetailAndPagedTracks_HaveNativeShapeHeadersAndOwnership()
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        fixture.Identity.CaptureLogin(Encoding.UTF8.GetBytes(
            "{\"token\":\"native-token\",\"username\":\"alice\",\"isAdmin\":false}"));
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Nd-Authorization", "Bearer native-token");

        using var listResponse = await client.GetAsync("/api/playlist?_start=0&_end=20");
        listResponse.EnsureSuccessStatusCode();
        Assert.Equal("2", listResponse.Headers.GetValues("X-Total-Count").Single());
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, list.RootElement.GetArrayLength());
        var radio = list.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetString() == fixture.StationId);
        Assert.True(radio.GetProperty("readonly").GetBoolean());

        using var detailResponse = await client.GetAsync($"/api/playlist/{fixture.StationId}");
        detailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal("Your Mix", detail.RootElement.GetProperty("name").GetString());

        using var tracksResponse = await client.GetAsync(
            $"/api/playlist/{fixture.StationId}/tracks?_start=1&_end=2");
        tracksResponse.EnsureSuccessStatusCode();
        Assert.Equal("2", tracksResponse.Headers.GetValues("X-Total-Count").Single());
        using var tracks = JsonDocument.Parse(await tracksResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, tracks.RootElement.GetArrayLength());
        Assert.Equal("local-two", tracks.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task NativeReservedMutationIsReadOnlyAndOrdinaryDetailRelays()
    {
        await using var fixture = new RadioWebFactory();
        fixture.InstallStation();
        using var client = fixture.CreateClient();
        using var mutation = await client.PutAsync($"/api/playlist/{fixture.StationId}",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, mutation.StatusCode);

        using var ordinary = await client.GetAsync("/api/playlist/native-1");
        ordinary.EnsureSuccessStatusCode();
        Assert.Contains("Native Playlist", await ordinary.Content.ReadAsStringAsync());
    }
}

internal sealed class RadioWebFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-radio-web-" + Guid.NewGuid());
    public RadioUpstreamHandler Handler { get; } = new();
    public Mock<IMusicMetadataService> Metadata { get; } = new();
    public BlockingRadioTranscoder Transcoder { get; } = new();
    public LastFmRadioStateStore State => Services.GetRequiredService<LastFmRadioStateStore>();
    public Octo.Services.Subsonic.NavidromeIdentityService Identity =>
        Services.GetRequiredService<Octo.Services.Subsonic.NavidromeIdentityService>();
    public string StationId => LastFmRadioStateStore.StationId("alice", "your-mix");
    private readonly string _explicitFilter;
    private readonly bool _exposePlaylists;
    private readonly bool _exposeStreams;

    public RadioWebFactory(string explicitFilter = "All", bool exposePlaylists = true,
        bool exposeStreams = true)
    {
        _explicitFilter = explicitFilter;
        _exposePlaylists = exposePlaylists;
        _exposeStreams = exposeStreams;
        Directory.CreateDirectory(_directory);
        Metadata.Setup(service => service.PrewarmYouTubeIdsAsync(
                It.IsAny<IEnumerable<Octo.Models.Domain.Song>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Metadata.Setup(service => service.PrewarmYouTubeIdsForSongIdsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void InstallStation()
    {
        State.ReplaceStations("alice", [new LastFmRadioStation
        {
            Id = StationId, Key = "your-mix", Name = "Your Mix", Owner = "alice",
            Kind = LastFmRadioStationKind.YourMix, Personalized = true,
            CreatedUtc = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc),
            ChangedUtc = new DateTime(2026, 8, 25, 2, 0, 0, DateTimeKind.Utc),
            ValidUntilUtc = new DateTime(2026, 8, 26, 2, 0, 0, DateTimeKind.Utc),
            Tracks =
            [
                new() { Artist = "Artist One", Title = "Song One", Duration = 180 },
                new() { Artist = "Artist Two", Title = "Song Two", Duration = 200 }
            ]
        }]);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Subsonic:Url"] = "http://navidrome.test",
                ["Subsonic:AutoDetectDownloadPath"] = "false",
                ["Subsonic:ExplicitFilter"] = _explicitFilter,
                ["Library:DownloadPath"] = _directory,
                ["LastFm:EnableRadio"] = "true",
                ["LastFm:EnablePersonalizedStations"] = "true",
                ["LastFm:EnableDiscoveryStations"] = "true",
                ["LastFm:ExposeRadioAsPlaylists"] = _exposePlaylists.ToString(),
                ["LastFm:ExposeRadioAsStreams"] = _exposeStreams.ToString(),
                ["LastFm:RadioStreamBitrateKbps"] = "192",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new RadioHttpClientFactory(Handler));
            services.RemoveAll<IMusicMetadataService>();
            services.AddSingleton(Metadata.Object);
            services.RemoveAll<ILastFmRadioAudioTranscoder>();
            services.AddSingleton<ILastFmRadioAudioTranscoder>(Transcoder);
            services.RemoveAll<LastFmRadioTrackCache>();
            services.AddSingleton(new LastFmRadioTrackCache(Path.Combine(_directory, "radio-cache")));
            services.RemoveAll<LastFmRadioStateStore>();
            services.AddSingleton(provider => new LastFmRadioStateStore(
                Path.Combine(_directory, "radio-state.json"),
                provider.GetRequiredService<IOptionsMonitor<LastFmSettings>>(),
                provider.GetRequiredService<ExternalIdRegistry>(),
                provider.GetRequiredService<ILogger<LastFmRadioStateStore>>()));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_directory, true); } catch { }
    }

    private sealed class RadioHttpClientFactory(RadioUpstreamHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed class BlockingRadioTranscoder : ILastFmRadioAudioTranscoder
    {
        public int LastBitrateKbps { get; private set; }
        public int FailuresBeforeSuccess { get; set; }
        public int CompleteCalls { get; set; } = 1;
        public int Calls => Volatile.Read(ref _calls);
        private int _calls;
        public async Task TranscodeToMp3Async(Stream input, Stream output, int bitrateKbps,
            CancellationToken cancellationToken)
        {
            LastBitrateKbps = bitrateKbps;
            var call = Interlocked.Increment(ref _calls);
            if (call <= FailuresBeforeSuccess) throw new InvalidOperationException("fixture source failed");
            await output.WriteAsync("MP3"u8.ToArray(), cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (call <= FailuresBeforeSuccess + CompleteCalls) return;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}

internal sealed class RadioUpstreamHandler : HttpMessageHandler
{
    public IReadOnlyList<string> RelayedScrobbleIds { get; private set; } = [];
    public bool ReturnLocalMatches { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var format = query["f"] ?? "json";
        var username = query["u"] ?? "";
        if (username == "bad") return Result(format == "xml" ? FailedXml() : FailedJson());

        if (path.Equals("rest/scrobble", StringComparison.OrdinalIgnoreCase))
            RelayedScrobbleIds = query.GetValues("id") ?? [];
        if (path.Equals("rest/getSong", StringComparison.OrdinalIgnoreCase))
        {
            var id = query["id"] ?? "song";
            return Result(OkJson($"\"song\":{{\"id\":\"{id}\",\"artist\":\"Artist {id}\",\"title\":\"Title {id}\",\"album\":\"Album {id}\",\"genre\":\"Rock\",\"duration\":180}}"));
        }
        if (path.Equals("rest/search3", StringComparison.OrdinalIgnoreCase))
        {
            if (!ReturnLocalMatches)
                return Result(OkJson("\"searchResult3\":{\"song\":[]}"));
            var search = query["query"] ?? "";
            var id = search.Contains("Two", StringComparison.OrdinalIgnoreCase) ? "local-two" : "local-one";
            var artist = id == "local-two" ? "Artist Two" : "Artist One";
            var title = id == "local-two" ? "Song Two" : "Song One";
            return Result(OkJson($"\"searchResult3\":{{\"song\":[{{\"id\":\"{id}\",\"artist\":\"{artist}\",\"title\":\"{title}\",\"album\":\"Album\",\"duration\":180}}]}}"));
        }
        if (path.Equals("rest/getPlaylists", StringComparison.OrdinalIgnoreCase))
            return Result(format == "xml" ?
                "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"><playlists><playlist id=\"native-1\" name=\"Native Playlist\" owner=\"alice\" songCount=\"1\" duration=\"180\"/></playlists></subsonic-response>" :
                OkJson("\"playlists\":{\"playlist\":[{\"id\":\"native-1\",\"name\":\"Native Playlist\",\"owner\":\"alice\",\"songCount\":1,\"duration\":180}]}"));
        if (path.Equals("rest/getInternetRadioStations", StringComparison.OrdinalIgnoreCase))
            return Result(format == "xml" ?
                "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"><internetRadioStations><internetRadioStation id=\"native-radio\" name=\"Native Radio\" streamUrl=\"https://radio.test/live\"/></internetRadioStations></subsonic-response>" :
                OkJson("\"internetRadioStations\":{\"internetRadioStation\":[{\"id\":\"native-radio\",\"name\":\"Native Radio\",\"streamUrl\":\"https://radio.test/live\"}]}"));
        if (path.Equals("rest/stream", StringComparison.OrdinalIgnoreCase))
            return Result("source-audio", "application/octet-stream");
        if (path.Equals("rest/getPlaylist", StringComparison.OrdinalIgnoreCase))
            return Result(OkJson("\"playlist\":{\"id\":\"native-1\",\"name\":\"Native Playlist\",\"entry\":[]}"));
        if (path.Equals("rest/ping", StringComparison.OrdinalIgnoreCase)
            || path.Equals("rest/scrobble", StringComparison.OrdinalIgnoreCase))
            return Result(format == "xml" ? OkXml() : OkJson("\"scrobble\":{}"));
        if (path.Equals("api/playlist", StringComparison.OrdinalIgnoreCase))
            return Result("[{\"id\":\"native-1\",\"name\":\"Native Playlist\",\"songCount\":1}]", "application/json");
        if (path.Equals("api/playlist/native-1", StringComparison.OrdinalIgnoreCase))
            return Result("{\"id\":\"native-1\",\"name\":\"Native Playlist\"}", "application/json");
        return Result(format == "xml" ? OkXml() : OkJson(""));
    }

    private static Task<HttpResponseMessage> Result(string body, string contentType = "application/json") =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, contentType) });
    private static string OkJson(string fields) =>
        "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"" +
        (fields.Length == 0 ? "" : "," + fields) + "}}";
    private static string FailedJson() =>
        "{\"subsonic-response\":{\"status\":\"failed\",\"version\":\"1.16.1\",\"error\":{\"code\":40,\"message\":\"Wrong username or password\"}}}";
    private static string OkXml() =>
        "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"/>";
    private static string FailedXml() =>
        "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"failed\" version=\"1.16.1\"><error code=\"40\" message=\"Wrong username or password\"/></subsonic-response>";
}
