using Microsoft.AspNetCore.Mvc;
using Octo.Models.Settings;

namespace Octo.Services.Subsonic;

/// <summary>
/// Handles proxying requests to the underlying Subsonic server.
/// </summary>
public class SubsonicProxyService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SubsonicProxyService(
        IHttpClientFactory httpClientFactory,
        Microsoft.Extensions.Options.IOptions<SubsonicSettings> subsonicSettings,
        IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Relays a request to the Subsonic server and returns the response.
    /// </summary>
    public async Task<(byte[] Body, string? ContentType)> RelayAsync(
        string endpoint, 
        Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url)
            || !Uri.TryCreate(_subsonicSettings.Url, UriKind.Absolute, out _))
        {
            throw new OctoNotConfiguredException(
                "Octo has no valid Navidrome URL. Set SUBSONIC_URL (Subsonic__Url) to your " +
                "Navidrome server, e.g. http://192.168.1.10:4533 — an absolute URL reachable " +
                "from the Octo container, not localhost.");
        }

        var query = string.Join("&", parameters.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_subsonicSettings.Url.TrimEnd('/')}/{endpoint}?{query}";

        HttpResponseMessage response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        var body = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();
        
        return (body, contentType);
    }

    /// <summary>
    /// Faithful relay: forwards the caller's HTTP method + body + content-type and
    /// returns the upstream status verbatim (no EnsureSuccessStatusCode). This is
    /// what makes non-GET / body-carrying endpoints work through Octo — e.g.
    /// Navidrome's native POST /auth/login that some clients use to sign in.
    /// Requires request buffering (enabled in Program.cs) so the body is re-readable.
    /// </summary>
    public async Task<(int Status, byte[] Body, string? ContentType)> RelayRawAsync(
        string endpoint,
        Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url)
            || !Uri.TryCreate(_subsonicSettings.Url, UriKind.Absolute, out _))
        {
            throw new OctoNotConfiguredException(
                "Octo has no valid Navidrome URL. Set SUBSONIC_URL (Subsonic__Url) to your " +
                "Navidrome server, e.g. http://192.168.1.10:4533 — an absolute URL reachable " +
                "from the Octo container, not localhost.");
        }

        var query = string.Join("&", parameters.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_subsonicSettings.Url.TrimEnd('/')}/{endpoint}?{query}";

        var ctx = _httpContextAccessor.HttpContext;
        var incoming = ctx?.Request;
        var method = incoming?.Method ?? "GET";
        using var req = new HttpRequestMessage(new HttpMethod(method), url);

        // Forward the raw request body captured by the middleware (the live body
        // stream is already closed by parameter extraction at this point).
        if (ctx?.Items.TryGetValue("Octo.RawBody", out var rb) == true
            && rb is byte[] bytes && bytes.Length > 0)
        {
            req.Content = new ByteArrayContent(bytes);
            if (!string.IsNullOrEmpty(incoming?.ContentType))
                req.Content.Headers.TryAddWithoutValidation("Content-Type", incoming.ContentType);
        }

        using var response = await _httpClient.SendAsync(req);
        var body = await response.Content.ReadAsByteArrayAsync();
        return ((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
    }

    /// <summary>
    /// Safely relays a request to the Subsonic server, returning null on failure.
    /// </summary>
    public async Task<(byte[]? Body, string? ContentType, bool Success)> RelaySafeAsync(
        string endpoint, 
        Dictionary<string, string> parameters)
    {
        try
        {
            var result = await RelayAsync(endpoint, parameters);
            return (result.Body, result.ContentType, true);
        }
        catch
        {
            return (null, null, false);
        }
    }

    private static readonly string[] StreamingRequiredHeaders =
    {
        "Accept-Ranges",
        "Content-Range",
        "Content-Length",
        "ETag",
        "Last-Modified"
    };

    /// <summary>
    /// Relays a stream request to the Subsonic server with range processing support.
    /// </summary>
    public async Task<IActionResult> RelayStreamAsync(
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get HTTP context for request/response forwarding
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return new ObjectResult(new { error = "HTTP context not available" })
                {
                    StatusCode = 500
                };
            }
            
            var incomingRequest = httpContext.Request;
            var outgoingResponse = httpContext.Response;

            var query = string.Join("&", parameters.Select(kv => 
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            var url = $"{_subsonicSettings.Url}/rest/stream?{query}";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Forward Range headers for progressive streaming support (iOS clients)
            if (incomingRequest.Headers.TryGetValue("Range", out var range))
            {
                request.Headers.TryAddWithoutValidation("Range", range.ToArray());
            }
            
            if (incomingRequest.Headers.TryGetValue("If-Range", out var ifRange))
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange.ToArray());
            }
            
            var response = await _httpClient.SendAsync(
                request, 
                HttpCompletionOption.ResponseHeadersRead, 
                cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                return new StatusCodeResult((int)response.StatusCode);
            }

            // Forward HTTP status code (e.g., 206 Partial Content for range requests)
            outgoingResponse.StatusCode = (int)response.StatusCode;

            // Forward streaming-required headers from upstream response
            foreach (var header in StreamingRequiredHeaders)
            {
                if (response.Headers.TryGetValues(header, out var values) ||
                    response.Content.Headers.TryGetValues(header, out values))
                {
                    outgoingResponse.Headers[header] = values.ToArray();
                }
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
            
            return new FileStreamResult(stream, contentType)
            {
                EnableRangeProcessing = true
            };
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = $"Error streaming from Subsonic: {ex.Message}" })
            {
                StatusCode = 500
            };
        }
    }
}

/// <summary>
/// Thrown when Octo's upstream Navidrome URL is missing or not an absolute URL.
/// The global handler surfaces its message to the client as an actionable
/// Subsonic error instead of an opaque "Invalid request".
/// </summary>
public class OctoNotConfiguredException : Exception
{
    public OctoNotConfiguredException(string message) : base(message) { }
}
