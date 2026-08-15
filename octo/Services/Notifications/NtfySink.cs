using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Notifications;

/// <summary>
/// ntfy transport, using JSON publishing mode: POST to the server root with the
/// topic in the body, not PUT-to-topic with metadata headers.
///
/// Deliberate: ntfy's header mode carries the title in an HTTP header, and .NET's
/// HttpClient rejects non-ASCII header values — for a music app, "Sigur Rós" and
/// "坂本龍一" are the normal case, not the edge case. ntfy documents RFC-2047-encoding
/// each header as the workaround; a UTF-8 JSON body needs none of that. The only
/// header left is the ASCII Bearer token.
/// </summary>
public sealed class NtfySink : INotificationSink
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<NotificationSettings> _opts;

    public NtfySink(IHttpClientFactory httpFactory, IOptionsMonitor<NotificationSettings> opts)
    {
        _httpFactory = httpFactory;
        _opts = opts;
    }

    // Literal UTF-8 in the body rather than \uXXXX escapes. Both decode the same,
    // but the un-escaped form is what a human sees tailing the wire, and carrying
    // "Sigur Rós" readably is the whole reason this sink uses JSON mode.
    private static readonly JsonSerializerOptions Utf8Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => "ntfy";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.CurrentValue.NtfyUrl);

    public async Task SendAsync(NotificationMessage message, CancellationToken ct)
    {
        var settings = _opts.CurrentValue;
        var (serverRoot, topic) = ParseTopicUrl(settings.NtfyUrl);

        var payload = new JsonObject
        {
            ["topic"] = topic,
            ["title"] = message.Title,
            ["message"] = BuildBody(message),
            ["tags"] = new JsonArray(TagFor(message.Type)),
        };
        if (!string.IsNullOrEmpty(message.ImageUrl))
        {
            // Both slots on purpose: icon is what the phone shows in the
            // notification shade next to the text, attach is the full-size art
            // when the notification is expanded.
            payload["icon"] = message.ImageUrl;
            payload["attach"] = message.ImageUrl;
        }

        var http = _httpFactory.CreateClient(NotificationService.ClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, serverRoot)
        {
            Content = new StringContent(payload.ToJsonString(Utf8Json), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(settings.NtfyToken))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.NtfyToken}");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// ntfy is plain text, so the song card renders as lines rather than embed
    /// fields: the album on its own line, then the stats dot-separated. Built
    /// from the same NotificationMessage as Discord's card, so the transports
    /// carry identical facts.
    /// </summary>
    internal static string BuildBody(NotificationMessage message)
    {
        if (message.Fields is not { Count: > 0 }) return message.Body;

        var stats = string.Join(" · ", message.Fields.Select(f => f.Value));
        return string.IsNullOrEmpty(message.Description)
            ? stats
            : $"{message.Description}\n{stats}";
    }

    /// <summary>
    /// "https://ntfy.sh/octo" -> ("https://ntfy.sh", "octo");
    /// "https://host/ntfy/octo" -> ("https://host/ntfy", "octo").
    /// The config stays one field — the URL users copy out of the ntfy app — and the
    /// sink derives the server root and topic itself. A URL with no topic throws a
    /// message the admin test button surfaces verbatim.
    /// </summary>
    internal static (string ServerRoot, string Topic) ParseTopicUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var uri = new Uri(trimmed, UriKind.Absolute);
        var topic = uri.Segments.Length > 1 ? uri.Segments[^1].Trim('/') : "";
        if (string.IsNullOrEmpty(topic))
            throw new InvalidOperationException(
                "ntfy URL must include a topic, e.g. https://ntfy.sh/your-topic");
        var root = trimmed[..trimmed.LastIndexOf('/')];
        return (root, topic);
    }

    internal static string TagFor(NotificationEventType type) => type switch
    {
        NotificationEventType.DownloadStarted => "arrow_down",
        NotificationEventType.DownloadCompleted => "white_check_mark",
        NotificationEventType.LosslessFallback => "warning",
        NotificationEventType.DownloadFailed => "x",
        NotificationEventType.AlbumCompleted => "cd",
        _ => "bell",
    };
}
