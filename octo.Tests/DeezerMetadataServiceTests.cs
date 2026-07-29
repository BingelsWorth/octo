using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Octo.Services.Metadata;
using System.Net;

namespace Octo.Tests;

public class DeezerMetadataServiceTests
{
    /// <summary>Builds a service whose HTTP layer answers from a url-substring to body map.
    /// Any url with no match returns 404, which exercises the best-effort paths.</summary>
    private static DeezerMetadataService BuildService(Dictionary<string, string> routes)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri!.ToString();
                foreach (var (needle, body) in routes)
                {
                    if (url.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(body),
                        };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object));

        return new DeezerMetadataService(factory.Object, new Mock<ILogger<DeezerMetadataService>>().Object);
    }

    [Fact]
    public async Task SearchAlbumsAsync_MapsFieldsAndDropsSingles()
    {
        // Arrange: one real album, one EP, and a one-track "single" that must be dropped.
        var json = @"{""data"":[
            {""id"":14880659,""title"":""In Rainbows"",""record_type"":""album"",""nb_tracks"":10,
             ""cover_xl"":""https://cdn/xl.jpg"",""artist"":{""name"":""Radiohead""}},
            {""id"":14880561,""title"":""In Rainbows (Disk 2)"",""record_type"":""ep"",""nb_tracks"":8,
             ""cover_xl"":""https://cdn/ep.jpg"",""artist"":{""name"":""Radiohead""}},
            {""id"":999,""title"":""Nude"",""record_type"":""single"",""nb_tracks"":1,
             ""cover_xl"":""https://cdn/s.jpg"",""artist"":{""name"":""Radiohead""}}
        ]}";
        var svc = BuildService(new() { ["/search/album"] = json });

        // Act
        var hits = await svc.SearchAlbumsAsync("In Rainbows", 10);

        // Assert
        Assert.Equal(2, hits.Count);
        Assert.Equal("14880659", hits[0].DeezerId);
        Assert.Equal("In Rainbows", hits[0].Title);
        Assert.Equal("Radiohead", hits[0].Artist);
        Assert.Equal("https://cdn/xl.jpg", hits[0].CoverUrl);
        Assert.Equal(10, hits[0].TrackCount);
        Assert.DoesNotContain(hits, h => h.Title == "Nude");
    }

    [Fact]
    public async Task SearchAlbumsAsync_KeepsMultiTrackSingle()
    {
        // Only a 1-2 track "single" is noise; a longer one is a real release.
        var json = @"{""data"":[{""id"":5,""title"":""Long Single"",""record_type"":""single"",
            ""nb_tracks"":6,""artist"":{""name"":""X""}}]}";
        var svc = BuildService(new() { ["/search/album"] = json });

        var hits = await svc.SearchAlbumsAsync("q", 10);

        Assert.Single(hits);
    }

    [Fact]
    public async Task GetAlbumDetailAsync_OrdersByDiscThenTrackPosition()
    {
        // Arrange: deliberately out of order, spanning two discs.
        var album = @"{""id"":1,""title"":""Test Album"",""cover_xl"":""https://cdn/a.jpg"",
            ""release_date"":""1997-05-21"",""label"":""Label X"",
            ""artist"":{""name"":""Test Artist""},""genres"":{""data"":[{""name"":""Rock""}]}}";
        var tracks = @"{""total"":4,""data"":[
            {""title"":""D2T1"",""duration"":100,""track_position"":1,""disk_number"":2,""artist"":{""name"":""Test Artist""}},
            {""title"":""D1T2"",""duration"":200,""track_position"":2,""disk_number"":1,""isrc"":""ABC"",""artist"":{""name"":""Test Artist""}},
            {""title"":""D1T1"",""duration"":300,""track_position"":1,""disk_number"":1,""artist"":{""name"":""Test Artist""}},
            {""title"":""D2T2"",""duration"":150,""track_position"":2,""disk_number"":2,""artist"":{""name"":""Test Artist""}}
        ]}";
        var svc = BuildService(new()
        {
            ["/album/1/tracks"] = tracks,
            ["/album/1"] = album,
        });

        // Act
        var detail = await svc.GetAlbumDetailAsync("1");

        // Assert
        Assert.NotNull(detail);
        Assert.Equal("Test Album", detail!.Title);
        Assert.Equal("Test Artist", detail.Artist);
        Assert.Equal(1997, detail.Year);
        Assert.Equal("Rock", detail.Genre);
        Assert.Equal("Label X", detail.Label);
        Assert.Equal(new[] { "D1T1", "D1T2", "D2T1", "D2T2" }, detail.Tracks.Select(t => t.Title));
        Assert.Equal("ABC", detail.Tracks[1].Isrc);
        Assert.Equal(300, detail.Tracks[0].Duration);
    }

    [Fact]
    public async Task GetAlbumDetailAsync_MalformedPayload_ReturnsNullWithoutThrowing()
    {
        var svc = BuildService(new() { ["/album/"] = "{ this is not json" });

        var detail = await svc.GetAlbumDetailAsync("1");

        Assert.Null(detail);
    }

    [Fact]
    public async Task GetAlbumDetailAsync_UnreachableApi_ReturnsNull()
    {
        // No routes registered, so every request 404s.
        var svc = BuildService(new());

        Assert.Null(await svc.GetAlbumDetailAsync("1"));
    }

    [Fact]
    public async Task FindAlbumIdAsync_ReturnsFirstMatch()
    {
        var json = @"{""data"":[{""id"":14880659,""title"":""In Rainbows""}]}";
        var svc = BuildService(new() { ["/search/album"] = json });

        var id = await svc.FindAlbumIdAsync("Radiohead", "In Rainbows");

        Assert.Equal("14880659", id);
    }

    [Fact]
    public async Task FindAlbumIdAsync_NoAlbumName_ReturnsNullWithoutCallingApi()
    {
        var svc = BuildService(new());

        Assert.Null(await svc.FindAlbumIdAsync("Radiohead", ""));
    }

    [Fact]
    public async Task SearchAlbumsAsync_EmptyQueryOrZeroLimit_ReturnsEmpty()
    {
        var svc = BuildService(new());

        Assert.Empty(await svc.SearchAlbumsAsync("", 10));
        Assert.Empty(await svc.SearchAlbumsAsync("q", 0));
    }
}
