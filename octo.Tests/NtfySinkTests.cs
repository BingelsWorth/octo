using System.Net;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services.Notifications;

namespace Octo.Tests;

/// <summary>
/// The ntfy sink publishes in JSON mode on purpose: ntfy's header mode carries the
/// title in an HTTP header, and .NET's HttpClient rejects non-ASCII header values,
/// which for a music library ("Sigur Rós", "坂本龍一") is the normal case. These
/// tests pin that choice, the topic-URL parsing that lets the config stay one
/// field, and the auth header only appearing when a token is set.
/// </summary>
public class NtfySinkTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static (NtfySink Sink, CapturingHandler Handler) Build(string url, string token = "")
    {
        var handler = new CapturingHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));
        var opts = Options.Create(new NotificationSettings { NtfyUrl = url, NtfyToken = token });
        var monitor = new Mock<IOptionsMonitor<NotificationSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(opts.Value);
        return (new NtfySink(factory.Object, monitor.Object), handler);
    }

    private static NotificationMessage Message(string title = "t", string body = "b", string? image = null) =>
        new(NotificationEventType.DownloadCompleted, title, body, image);

    [Theory]
    [InlineData("https://ntfy.sh/octo", "https://ntfy.sh", "octo")]
    [InlineData("https://host/ntfy/octo", "https://host/ntfy", "octo")]
    [InlineData("https://ntfy.sh/octo/", "https://ntfy.sh", "octo")]
    public void TopicUrlSplitsIntoRootAndTopic(string url, string root, string topic)
    {
        var parsed = NtfySink.ParseTopicUrl(url);

        Assert.Equal(root, parsed.ServerRoot);
        Assert.Equal(topic, parsed.Topic);
    }

    [Fact]
    public void BareServerUrlIsReportedNotSwallowed()
    {
        // The admin test button surfaces this message verbatim, so it names the fix.
        var ex = Assert.Throws<InvalidOperationException>(() => NtfySink.ParseTopicUrl("https://ntfy.sh"));

        Assert.Contains("must include a topic", ex.Message);
    }

    [Fact]
    public async Task PublishesUtf8JsonNotHeaders()
    {
        var (sink, handler) = Build("https://ntfy.sh/octo");

        await sink.SendAsync(Message(title: "Sigur Rós – Ágætis byrjun"), CancellationToken.None);

        // The whole reason for JSON mode: the title travels in the UTF-8 body and
        // never as an HTTP header.
        Assert.Contains("Sigur Rós", handler.Body);
        Assert.False(handler.Request!.Headers.Contains("Title"));
        Assert.Contains("\"topic\":\"octo\"", handler.Body);
        Assert.Equal("https://ntfy.sh/", handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task BearerTokenOnlyWhenConfigured()
    {
        var (bare, bareHandler) = Build("https://ntfy.sh/octo");
        await bare.SendAsync(Message(), CancellationToken.None);
        Assert.False(bareHandler.Request!.Headers.Contains("Authorization"));

        var (authed, authedHandler) = Build("https://ntfy.sh/octo", token: "tk_secret");
        await authed.SendAsync(Message(), CancellationToken.None);
        Assert.Equal("Bearer tk_secret", authedHandler.Request!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task CoverArtBecomesAttachAndAbsentMeansNoKey()
    {
        var (sink, handler) = Build("https://ntfy.sh/octo");

        await sink.SendAsync(Message(image: "https://cdn.example/cover.jpg"), CancellationToken.None);
        Assert.Contains("\"attach\":\"https://cdn.example/cover.jpg\"", handler.Body);

        await sink.SendAsync(Message(image: null), CancellationToken.None);
        Assert.DoesNotContain("attach", handler.Body);
    }
}
