using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// Asks the receiver what it is and what it has: model and zone count from
/// Deviceinfo.xml, and the real input names from the AppCommand endpoint - the same
/// names you set in the unit's own setup menu, so the grid says "Apple TV" rather
/// than "MPLAY", and hides inputs you deleted.
/// </summary>
public sealed class DeviceProfileReader(AjaxConfigClient ajax, ILogger<DeviceProfileReader> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>These models answer AppCommand on 80; the HEOS generation also on 8080.</summary>
    private static readonly int[] Ports = [80, 8080];

    public async Task<DeviceProfile> ReadAsync(string host, string fallbackModel, CancellationToken ct)
    {
        var profile = DeviceProfile.Fallback(fallbackModel);

        await ReadDeviceInfoAsync(host, profile, ct);

        var sources = await ReadSourcesAsync(host, ct);
        if (sources.Count > 0)
        {
            profile.Sources = sources;
            profile.SourcesFromDevice = true;
        }

        // Newer firmware answers 403 on the whole /goform/ API, so everything above
        // comes back empty. The setup API has the same information.
        profile.SetupApi = await ajax.ProbeAsync(host, ct);
        if (profile.SetupApi) await ReadFromSetupApiAsync(host, profile, ct);

        profile.Heos = await HasHeosAsync(host, ct);

        log.LogInformation(
            "{Host}: {Model}, {Zones} zones, {Count} inputs ({Origin}), HEOS {Heos}, setup API {Api}",
            host, profile.Model, profile.ZoneCount, profile.Sources.Count,
            profile.SourcesFromDevice ? "from device" : "fallback", profile.Heos, profile.SetupApi);

        return profile;
    }

    private async Task ReadDeviceInfoAsync(string host, DeviceProfile profile, CancellationToken ct)
    {
        foreach (var port in Ports)
        {
            try
            {
                var xml = await Http.GetStringAsync($"http://{host}:{port}/goform/Deviceinfo.xml", ct);
                var doc = XDocument.Parse(xml);

                string Value(string name) => doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

                // The unit prefixes its model with '*'.
                var model = Value("ModelName").TrimStart('*');
                if (!string.IsNullOrWhiteSpace(model)) profile.Model = model;

                if (int.TryParse(Value("DeviceZones"), out var zones) && zones is > 0 and <= 4)
                    profile.ZoneCount = zones;

                return;
            }
            catch (Exception ex)
            {
                log.LogDebug("Deviceinfo.xml on {Host}:{Port} failed: {Message}", host, port, ex.Message);
            }
        }
    }

    /// <summary>
    /// Source names and hidden inputs from /ajax/inputs, plus the zone and Quick
    /// Select names the receiver keeps in /ajax/general.
    /// </summary>
    private async Task ReadFromSetupApiAsync(string host, DeviceProfile profile, CancellationToken ct)
    {
        if (!profile.SourcesFromDevice)
        {
            var renames = await ajax.ReadAsync(host, "inputs", 3, ct);
            var hidden = await ajax.ReadAsync(host, "inputs", 4, ct);

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (renames?.Root is not null)
            {
                foreach (var source in renames.Root.Elements().Where(e => e.Name.LocalName == "Source"))
                {
                    var code = DenonCatalog.NormaliseSourceCode(Child(source, "Default"));
                    var label = Child(source, "Rename");
                    if (code.Length > 0) names[code] = label.Length > 0 ? label : code;
                }
            }

            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hidden?.Root is not null)
            {
                foreach (var source in hidden.Root.Elements().Where(e => e.Name.LocalName == "Source"))
                {
                    // Hide of 1 means shown; anything else means hidden in the unit's setup.
                    var code = DenonCatalog.NormaliseSourceCode(Child(source, "Name"));
                    if (code.Length > 0 && Child(source, "Hide") != "1") suppressed.Add(code);
                }
            }

            if (names.Count > 0)
            {
                var resolved = new List<SourceOption>();
                foreach (var known in DenonCatalog.Sources)
                {
                    if (suppressed.Contains(known.Code)) continue;
                    resolved.Add(names.TryGetValue(known.Code, out var label)
                        ? new SourceOption(known.Code, label)
                        : known);
                }

                if (resolved.Count > 0)
                {
                    profile.Sources = resolved;
                    profile.SourcesFromDevice = true;
                }
            }
        }

        var zones = await ajax.ReadAsync(host, "general", 6, ct);
        if (zones?.Root is not null)
        {
            foreach (var zone in zones.Root.Elements().Where(e => e.Name.LocalName == "Zone"))
            {
                if (!int.TryParse(zone.Attribute("index")?.Value, out var number)) continue;
                var name = Child(zone, "Rename");
                if (name.Length > 0) profile.ZoneNames[number] = Title(name);
            }
        }

        var quick = await ajax.ReadAsync(host, "general", 7, ct);
        if (quick?.Root is not null)
        {
            foreach (var item in quick.Root.Elements().Where(e => e.Name.LocalName == "Item"))
            {
                if (!int.TryParse(item.Attribute("index")?.Value, out var index)) continue;
                var name = Child(item, "Name");
                // The document is zero-based; the protocol's slots are one-based.
                if (name.Length > 0) profile.QuickSelectNames[index + 1] = name;
            }
        }
    }

    /// <summary>The receiver shouts its zone names: MAIN ZONE, ZONE2.</summary>
    private static string Title(string value) =>
        value.Any(char.IsLower) ? value
            : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length > 1
                    ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
                    : word));

    private async Task<List<SourceOption>> ReadSourcesAsync(string host, CancellationToken ct)
    {
        var renames = await AppCommandAsync(host, "GetRenameSource", ct);
        var deletes = await AppCommandAsync(host, "GetDeletedSource", ct);

        // The pre-HEOS units answer these two with an empty <rx/>, but publish the
        // same table in their zone status document. See ParseLegacySources.
        if (renames is null && deletes is null) return await ReadLegacySourcesAsync(host, ct);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (renames is not null)
        {
            foreach (var entry in renames.Descendants().Where(e => e.Name.LocalName == "list"))
            {
                var code = DenonCatalog.NormaliseSourceCode(Child(entry, "name"));
                var label = Child(entry, "rename");
                if (code.Length == 0) continue;
                names[code] = label.Length > 0 ? label : code;
            }
        }

        if (deletes is not null)
        {
            foreach (var entry in deletes.Descendants().Where(e => e.Name.LocalName == "list"))
            {
                var code = DenonCatalog.NormaliseSourceCode(Child(entry, "name"));
                // "use" is 1 for an input that is in use, 0 for one deleted in setup.
                if (code.Length > 0 && Child(entry, "use") == "0") hidden.Add(code);
            }
        }

        var result = new List<SourceOption>();

        // Keep the catalog's order for inputs we know, then append anything unexpected.
        foreach (var known in DenonCatalog.Sources)
        {
            if (hidden.Contains(known.Code)) continue;
            if (names.TryGetValue(known.Code, out var label))
                result.Add(new SourceOption(known.Code, label));
            else if (names.Count == 0)
                result.Add(known);
        }

        foreach (var (code, label) in names)
        {
            if (hidden.Contains(code)) continue;
            if (result.Any(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new SourceOption(code, label));
        }

        return result;
    }

    /// <summary>
    /// The X4100W generation's own source table, read from the Zone 2 status document.
    /// Three arrays in step: what the input is called internally, what the user renamed
    /// it to, and whether it is in use. It is not zone-specific - renaming and hiding
    /// are unit-wide in setup - and it is the only place this firmware publishes it.
    /// </summary>
    private async Task<List<SourceOption>> ReadLegacySourcesAsync(string host, CancellationToken ct)
    {
        foreach (var port in Ports)
        {
            try
            {
                var xml = await Http.GetStringAsync(
                    $"http://{host}:{port}/goform/formZone2_Zone2XmlStatus.xml", ct);
                var parsed = ParseLegacySources(XDocument.Parse(xml));
                if (parsed.Count > 0) return parsed;
            }
            catch (Exception ex)
            {
                log.LogDebug("No legacy source table from {Host}:{Port}: {Message}", host, port, ex.Message);
            }
        }

        return [];
    }

    /// <summary>Separated from the fetch so the self-test can exercise it.</summary>
    internal static List<SourceOption> ParseLegacySources(XDocument document)
    {
        if (document.Root is null) return [];

        List<string> Values(string tag) => document.Root
            .Elements().FirstOrDefault(e => e.Name.LocalName == tag)
            ?.Elements().Where(e => e.Name.LocalName == "value")
            // RenameSource nests a second <value> inside each entry.
            .Select(e => (e.Elements().FirstOrDefault()?.Value ?? e.Value).Trim())
            .ToList() ?? [];

        var codes = Values("InputFuncList");
        var labels = Values("RenameSource");
        var use = Values("SourceDelete");
        if (codes.Count == 0) return [];

        var result = new List<SourceOption>();
        for (var i = 0; i < codes.Count; i++)
        {
            var code = DenonCatalog.NormaliseSourceCode(codes[i]);
            if (code.Length == 0) continue;

            // Only an explicit refusal hides an input. This firmware leaves the flag
            // blank on at least one row - on the very input that was playing at the
            // time - and hiding what is currently playing is a far worse failure than
            // listing something the user had hidden.
            var flag = i < use.Count ? use[i] : "USE";
            if (flag.Length > 0 && !flag.Equals("USE", StringComparison.OrdinalIgnoreCase)) continue;

            var label = i < labels.Count && labels[i].Length > 0
                ? labels[i]
                : DenonCatalog.LabelForSource(code);

            result.Add(new SourceOption(code, label));
        }

        return result;
    }

    private static string Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

    private static string Child(XElement parent, string name, string fallback) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? fallback;

    private async Task<XDocument?> AppCommandAsync(string host, string command, CancellationToken ct)
    {
        var body = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><tx><cmd id=\"1\">{command}</cmd></tx>";

        foreach (var port in Ports)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "text/xml");
                using var response = await Http.PostAsync($"http://{host}:{port}/goform/AppCommand.xml", content, ct);
                if (!response.IsSuccessStatusCode) continue;

                var xml = await response.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(xml);
                if (doc.Descendants().Any(e => e.Name.LocalName == "list")) return doc;
            }
            catch (Exception ex)
            {
                log.LogDebug("{Command} on {Host}:{Port} failed: {Message}", command, host, port, ex.Message);
            }
        }

        return null;
    }

    /// <summary>HEOS listens on 1255. The pre-HEOS units simply refuse the connection.</summary>
    private async Task<bool> HasHeosAsync(string host, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(host, 1255, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
