using System.Net;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services.Notifications;

namespace Octo.Tests;

/// <summary>
/// Discord rejects malformed embeds with a 400 the user never sees (the send is
/// fire-and-forget), so the wire shape is pinned statically: field limits are
/// enforced by truncation rather than trusted, and a missing cover omits the
/// thumbnail object entirely because Discord 400s on a null url.
/// </summary>
public class DiscordSinkTests
{
    private static NotificationMessage Message(
        string title = "Downloaded: A – B",
        string body = "FLAC via Soulseek, 33.1 MB",
        string? image = "https://cdn.example/cover.jpg") =>
        new(NotificationEventType.DownloadCompleted, title, body, image);

    [Fact]
    public void EmbedCarriesTitleBodyThumbnailAndColor()
    {
        var payload = DiscordSink.BuildPayload(Message());
        var embed = payload["embeds"]![0]!;

        Assert.Equal("Downloaded: A – B", (string)embed["title"]!);
        Assert.Equal("FLAC via Soulseek, 33.1 MB", (string)embed["description"]!);
        Assert.Equal(0x22C55E, (int)embed["color"]!);
        Assert.Equal("https://cdn.example/cover.jpg", (string)embed["thumbnail"]!["url"]!);
        Assert.Equal("Octo", (string)embed["footer"]!["text"]!);
        Assert.NotNull(embed["timestamp"]);
    }

    [Fact]
    public void DiscordLimitsAreEnforced()
    {
        var payload = DiscordSink.BuildPayload(Message(
            title: new string('t', 300),
            body: new string('d', 5000)));
        var embed = payload["embeds"]![0]!;

        Assert.Equal(256, ((string)embed["title"]!).Length);
        Assert.Equal(4096, ((string)embed["description"]!).Length);
    }

    [Fact]
    public void FieldsTurnTheEmbedIntoASongCard()
    {
        var rendered = Octo.Services.Notifications.NotificationService.Render(new NotificationEvent
        {
            Type = NotificationEventType.DownloadCompleted,
            Artist = "Randy Rogers Band",
            Title = "In My Arms Instead",
            Album = "Randy Rogers Band",
            Format = "FLAC",
            Source = "Soulseek",
            SizeBytes = 34_684_600,
            DurationSeconds = 223,
            Year = 2008,
            CoverArtUrl = "https://cdn.example/cover.jpg",
        });
        var embed = DiscordSink.BuildPayload(rendered)["embeds"]![0]!;

        // Album as the description line, stats as inline fields, cover full-width.
        Assert.Equal("Randy Rogers Band", (string)embed["description"]!);
        var fields = embed["fields"]!.AsArray()
            .ToDictionary(f => (string)f!["name"]!, f => (string)f!["value"]!);
        Assert.Equal("FLAC", fields["Format"]);
        Assert.Equal("Soulseek", fields["Source"]);
        Assert.Equal("33.1 MB", fields["Size"]);
        Assert.Equal("3:43", fields["Length"]);
        Assert.Equal("≈1,244 kbps", fields["Bitrate"]);
        Assert.Equal("2008", fields["Year"]);
        Assert.All(embed["fields"]!.AsArray(), f => Assert.True((bool)f!["inline"]!));
        // The card uses the full-width image slot, not the corner thumbnail, and
        // does not repeat the prose body alongside the structured fields.
        Assert.Equal("https://cdn.example/cover.jpg", (string)embed["image"]!["url"]!);
        Assert.Null(embed["thumbnail"]);
        Assert.DoesNotContain("FLAC via Soulseek", (string?)embed["description"] ?? "");
    }

    [Fact]
    public void UnknownStatsAreOmittedFromTheCardNotRenderedAsPlaceholders()
    {
        var rendered = Octo.Services.Notifications.NotificationService.Render(new NotificationEvent
        {
            Type = NotificationEventType.DownloadStarted,
            Artist = "A",
            Title = "B",
            Format = "MP3",
            Source = "YouTube",
            // no size, no duration, no year — the YouTube started path
        });
        var embed = DiscordSink.BuildPayload(rendered)["embeds"]![0]!;

        var names = embed["fields"]!.AsArray().Select(f => (string)f!["name"]!).ToList();
        Assert.Equal(new[] { "Format", "Source" }, names);
    }

    [Fact]
    public void AlbumSummaryRendersItsCountsAsFields()
    {
        var rendered = Octo.Services.Notifications.NotificationService.Render(new NotificationEvent
        {
            Type = NotificationEventType.AlbumCompleted,
            Artist = "Tame Impala",
            Title = "Currents",
            TrackCount = 15,
            LosslessCount = 12,
            FailedCount = 1,
        });
        var embed = DiscordSink.BuildPayload(rendered)["embeds"]![0]!;

        var fields = embed["fields"]!.AsArray()
            .ToDictionary(f => (string)f!["name"]!, f => (string)f!["value"]!);
        Assert.Equal("15", fields["Tracks"]);
        Assert.Equal("12", fields["Lossless"]);
        Assert.Equal("1", fields["Failed"]);
    }

    [Fact]
    public void NoThumbnailKeyWithoutCoverArt()
    {
        var payload = DiscordSink.BuildPayload(Message(image: null));
        var embed = payload["embeds"]![0]!;

        // Discord 400s on {"url": null}; the key must be absent, not null.
        Assert.Null(embed["thumbnail"]);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    [Fact]
    public async Task PostGoesToTheConfiguredWebhookAsJson()
    {
        var handler = new CapturingHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));
        var monitor = new Mock<IOptionsMonitor<NotificationSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(new NotificationSettings
        {
            DiscordWebhookUrl = "https://discord.com/api/webhooks/1/abc",
        });

        await new DiscordSink(factory.Object, monitor.Object)
            .SendAsync(Message(), CancellationToken.None);

        Assert.Equal("https://discord.com/api/webhooks/1/abc", handler.Request!.RequestUri!.ToString());
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);
    }
}
