using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DenonRemote.Services;

public sealed record DiscoveredDevice(string Host, string Name, string Model, string Manufacturer);

/// <summary>
/// Finds Denon/Marantz receivers on the LAN with an SSDP M-SEARCH, then confirms each
/// candidate by reading its UPnP description document.
/// </summary>
public sealed partial class SsdpDiscovery(ILogger<SsdpDiscovery> log)
{
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);

    private const string Search =
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        "ST: ssdp:all\r\n" +
        "\r\n";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    [GeneratedRegex(@"^LOCATION:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LocationHeader();

    public async Task<IReadOnlyList<DiscoveredDevice>> SearchAsync(TimeSpan timeout, CancellationToken ct)
    {
        var locations = await CollectLocationsAsync(timeout, ct);
        log.LogInformation("SSDP returned {Count} distinct description URLs", locations.Count);

        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in locations)
        {
            var device = await DescribeAsync(location, ct);
            if (device is not null && !found.ContainsKey(device.Host))
                found[device.Host] = device;
        }

        return found.Values.OrderBy(d => d.Name).ToList();
    }

    /// <summary>Check one address directly, for when SSDP is blocked or the user types an IP.</summary>
    public async Task<DiscoveredDevice?> ProbeAsync(string host, CancellationToken ct)
    {
        foreach (var url in new[]
                 {
                     $"http://{host}:8080/description.xml",
                     $"http://{host}:60006/upnp/desc/aios_device/aios_device.xml",
                     $"http://{host}/description.xml",
                 })
        {
            var device = await DescribeAsync(url, ct);
            if (device is not null) return device;
        }

        // Last resort: the control endpoint itself.
        try
        {
            var xml = await Http.GetStringAsync($"http://{host}/goform/Deviceinfo.xml", ct);
            var doc = XDocument.Parse(xml);
            var model = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ModelName")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(model))
                return new DiscoveredDevice(host, model, model, "Denon");
        }
        catch (Exception ex)
        {
            log.LogDebug("Probe of {Host} failed: {Message}", host, ex.Message);
        }

        return null;
    }

    private async Task<HashSet<string>> CollectLocationsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payload = Encoding.ASCII.GetBytes(Search);
        var deadline = DateTime.UtcNow + timeout;
        var listeners = new List<Task>();
        var clients = new List<UdpClient>();

        foreach (var address in LocalAddresses())
        {
            UdpClient client;
            try
            {
                client = new UdpClient(new IPEndPoint(address, 0)) { EnableBroadcast = true };
                client.Client.ReceiveTimeout = 1000;
            }
            catch (Exception ex)
            {
                log.LogDebug("Cannot bind SSDP socket on {Address}: {Message}", address, ex.Message);
                continue;
            }

            clients.Add(client);
            listeners.Add(ListenAsync(client, locations, deadline, ct));

            try
            {
                await client.SendAsync(payload, payload.Length, Multicast);
                await Task.Delay(120, ct);
                await client.SendAsync(payload, payload.Length, Multicast);
            }
            catch (Exception ex)
            {
                log.LogDebug("SSDP send on {Address} failed: {Message}", address, ex.Message);
            }
        }

        try { await Task.WhenAll(listeners); } catch { /* best effort */ }
        foreach (var client in clients) client.Dispose();

        return locations;
    }

    private async Task ListenAsync(UdpClient client, HashSet<string> locations, DateTime deadline, CancellationToken ct)
    {
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var receive = client.ReceiveAsync(ct).AsTask();
                var completed = await Task.WhenAny(receive, Task.Delay(remaining, ct));
                if (completed != receive) break;

                var response = Encoding.ASCII.GetString(receive.Result.Buffer);
                var match = LocationHeader().Match(response);
                if (!match.Success) continue;

                var location = match.Groups[1].Value.Trim();
                lock (locations) locations.Add(location);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }
        }
    }

    private async Task<DiscoveredDevice?> DescribeAsync(string location, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)) return null;

            var xml = await Http.GetStringAsync(uri, ct);
            var doc = XDocument.Parse(xml);

            string Value(string name) => doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

            var manufacturer = Value("manufacturer");
            var model = Value("modelName");
            var friendly = Value("friendlyName");

            var looksRight =
                manufacturer.Contains("denon", StringComparison.OrdinalIgnoreCase) ||
                manufacturer.Contains("marantz", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AVR", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AVC", StringComparison.OrdinalIgnoreCase);

            if (!looksRight) return null;

            var name = !string.IsNullOrWhiteSpace(friendly) ? friendly
                     : !string.IsNullOrWhiteSpace(model) ? model
                     : uri.Host;

            return new DiscoveredDevice(uri.Host, name, model, manufacturer);
        }
        catch (Exception ex)
        {
            log.LogTrace("Description fetch failed for {Location}: {Message}", location, ex.Message);
            return null;
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return info.Address;
            }
        }
    }
}
