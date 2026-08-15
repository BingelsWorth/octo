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
