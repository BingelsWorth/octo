using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Octo.Services.Admin;

namespace Octo.Controllers;

/// <summary>
/// Streams the bounded, in-memory diagnostic tail to the admin Logs page.
/// Server-sent events fit the existing read-only HTTP admin surface
/// and reconnect automatically without adding a second realtime stack.
/// </summary>
[ApiController]
[Route("api/admin/logs")]
public sealed class AdminLogsController(AdminLogBuffer logs) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("stream")]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task Stream([FromQuery] long? after, CancellationToken cancellationToken)
    {
        if (!after.HasValue && long.TryParse(Request.Headers["Last-Event-ID"], out var lastEventId))
            after = lastEventId;

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var subscription = logs.Subscribe(after);
        var reader = subscription.Reader;
        Task<bool> entryReady = reader.WaitToReadAsync(cancellationToken).AsTask();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                if (await Task.WhenAny(entryReady, heartbeat) == heartbeat)
                {
                    await Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                    continue;
                }

                if (!await entryReady) break;

                while (reader.TryRead(out var entry))
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        entry.Sequence,
                        entry.TimestampUtc,
                        level = entry.Level.ToString(),
                        entry.Category,
                        entry.Message,
                        entry.Exception
                    }, JsonOptions);

                    await Response.WriteAsync($"id: {entry.Sequence}\nevent: log\ndata: {payload}\n\n", cancellationToken);
                }

                await Response.Body.FlushAsync(cancellationToken);
                entryReady = reader.WaitToReadAsync(cancellationToken).AsTask();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the page is the normal end of an SSE request.
        }
    }
}
