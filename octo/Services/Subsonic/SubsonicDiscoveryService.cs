using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Octo.Services.Subsonic;

/// <summary>One Subsonic-compatible server found on the local network.</summary>
public record DiscoveredServer(string Url, string? Type, string? ServerVersion, bool RequiresAuth);

/// <summary>
/// Finds Subsonic/Navidrome servers on the local network so Octo, which is an
/// accessory to an existing server, can auto-configure its upstream URL instead of
/// making the user hand-type it. The probe is credential-free: a Subsonic ping to a
/// real server returns a `{"subsonic-response":...}` envelope (even with no/bad
/// auth, as a `code 40` failure), while a non-Subsonic host returns 404/HTML. The
/// envelope also carries the server `type` and `serverVersion` for display.
///
/// Scope is the host's own /24 on a small set of well-known ports, run concurrently
/// with short timeouts, so a full sweep takes a few seconds. Only works where Octo
/// can see the real LAN (host networking or an LXC); in a Docker bridge network it
/// only sees the internal subnet and will typically find nothing.
/// </summary>
public class SubsonicDiscoveryService
{
    private readonly ILogger<SubsonicDiscoveryService> _logger;

    // Navidrome default is 4533; 4040 is Airsonic-Advanced; 4747/8080 are common
    // reverse-proxy/self-host choices. Kept short so the sweep stays fast.
    private static readonly int[] CandidatePorts = { 4533, 4040, 4747, 8080 };
    private const int ProbeTimeoutMs = 800;
    private const int MaxConcurrency = 96;

    public SubsonicDiscoveryService(ILogger<SubsonicDiscoveryService> logger)
    {
        _logger = logger;
    }

    public async Task<List<DiscoveredServer>> ScanAsync(CancellationToken ct = default)
    {
        var targets = BuildTargets();
        if (targets.Count == 0)
        {
            _logger.LogInformation("Server discovery: no scannable local /24 found (bridge network?).");
            return new List<DiscoveredServer>();
        }

        _logger.LogInformation("Server discovery: probing {Hosts} hosts x {Ports} ports...",
            targets.Count, CandidatePorts.Length);

        var found = new List<DiscoveredServer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(MaxConcurrency);
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs) };

        var tasks = new List<Task>();
        foreach (var ip in targets)
        {
            foreach (var port in CandidatePorts)
            {
                await gate.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var server = await ProbeAsync(http, ip, port, ct);
                        if (server != null)
                        {
                            lock (found)
                            {
                                if (seen.Add(server.Url)) found.Add(server);
                            }
                        }
                    }
                    catch { /* unreachable host/port; ignore */ }
                    finally { gate.Release(); }
                }, ct));
            }
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("Server discovery: found {N} Subsonic server(s).", found.Count);
        return found.OrderBy(s => s.Url, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ping-probe one host:port. Returns a server only if it answers with a
    /// Subsonic envelope.</summary>
    private static async Task<DiscoveredServer?> ProbeAsync(HttpClient http, string ip, int port, CancellationToken ct)
    {
        var baseUrl = $"http://{ip}:{port}";
        var url = $"{baseUrl}/rest/ping.view?c=octo&v=1.16.1&f=json";
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!body.Contains("subsonic-response", StringComparison.OrdinalIgnoreCase))
            return null;

        string? type = null, version = null;
        var requiresAuth = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("subsonic-response", out var r))
            {
                if (r.TryGetProperty("type", out var t)) type = t.GetString();
                if (r.TryGetProperty("serverVersion", out var v)) version = v.GetString();
                // A "failed / code 40" ping is still a positive server hit — it just
                // means it wants credentials, which the user supplies later.
                if (r.TryGetProperty("status", out var s) && s.GetString() == "failed")
                    requiresAuth = true;
            }
        }
        catch { /* non-JSON but contained the marker; still count it */ }

        return new DiscoveredServer(baseUrl, type, version, requiresAuth);
    }

    /// <summary>The host's own /24 address list (network+1 .. network+254), across
    /// every up, non-loopback IPv4 interface. Capped at /24 so the sweep is bounded
    /// even when the real subnet is larger.</summary>
    private static List<string> BuildTargets()
    {
        var ips = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var addr = ua.Address.GetAddressBytes();
                // Skip link-local 169.254.x.x and anything that isn't a normal LAN.
                if (addr[0] == 169 && addr[1] == 254) continue;

                // Enumerate the /24 containing this address (addr[0..2].x, 1..254).
                for (var host = 1; host <= 254; host++)
                {
                    ips.Add($"{addr[0]}.{addr[1]}.{addr[2]}.{host}");
                }
            }
        }
        return ips.Distinct().ToList();
    }
}
