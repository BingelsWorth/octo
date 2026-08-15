using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Notifications;

/// <summary>
/// Discord webhook transport. One rich embed and no top-level content, which both
/// looks better (thumbnail album art) and sidesteps the 2000-character content
/// limit entirely; a request with embeds and no content is valid per the API.
/// </summary>
public sealed class DiscordSink : INotificationSink
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<NotificationSettings> _opts;

    public DiscordSink(IHttpClientFactory httpFactory, IOptionsMonitor<NotificationSettings> opts)
    {
        _httpFactory = httpFactory;
        _opts = opts;
    }

    public string Name => "discord";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.CurrentValue.DiscordWebhookUrl);

    public async Task SendAsync(NotificationMessage message, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(NotificationService.ClientName);
        using var content = new StringContent(
            BuildPayload(message).ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(_opts.CurrentValue.DiscordWebhookUrl, content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Static so tests pin the exact wire shape without HTTP.</summary>
    internal static JsonObject BuildPayload(NotificationMessage message)
    {
        var embed = new JsonObject
        {
            ["title"] = Truncate(message.Title, 256),
            ["description"] = Truncate(message.Body, 4096),
            ["color"] = ColorFor(message.Type),
            ["footer"] = new JsonObject { ["text"] = "Octo" },
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
        };
        // Key omitted entirely when there is no cover: Discord rejects a null url.
        if (!string.IsNullOrEmpty(message.ImageUrl))
            embed["thumbnail"] = new JsonObject { ["url"] = message.ImageUrl };

        return new JsonObject { ["embeds"] = new JsonArray(embed) };
    }

    internal static int ColorFor(NotificationEventType type) => type switch
    {
        NotificationEventType.DownloadStarted => 0x3B82F6,
        NotificationEventType.DownloadCompleted => 0x22C55E,
        NotificationEventType.LosslessFallback => 0xF59E0B,
        NotificationEventType.DownloadFailed => 0xEF4444,
        NotificationEventType.AlbumCompleted => 0x8B5CF6,
        _ => 0x64748B,
    };

    internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
