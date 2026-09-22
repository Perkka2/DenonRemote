using System.Net;
using System.Xml.Linq;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// The setup UI's own configuration API, on HTTPS 10443 on the HEOS generation.
///
///   GET /ajax/{section}/get_config?type={n}
///   GET /ajax/{section}/set_config?type={n}&amp;data={url-encoded XML}
///
/// Newer firmware answers 403 on the whole legacy /goform/ API, so this is the only
/// way in on those units - including for things as basic as the input names.
/// Values are largely the receiver's own numeric codes; the ones it describes with
/// an explicit list of allowed values are the ones safe to offer for editing.
/// </summary>
public sealed class AjaxConfigClient(HttpProbe probe, ILogger<AjaxConfigClient> log)
{
    private static string Url(string host, string section, string verb, int type) =>
        $"https://{host}:10443/ajax/{section}/{verb}_config?type={type}";

    public async Task<XDocument?> ReadAsync(string host, string section, int type, CancellationToken ct)
    {
        var result = await probe.SendAsync("GET", Url(host, section, "get", type), null, ct);
        if (!result.Interesting) return null;

        try { return XDocument.Parse(result.Body); }
        catch (Exception ex)
        {
            log.LogDebug("Unreadable {Section}/{Type} from {Host}: {Message}", section, type, host, ex.Message);
            return null;
        }
    }

    /// <summary>Available when the receiver answers the setup API at all.</summary>
    public async Task<bool> ProbeAsync(string host, CancellationToken ct) =>
        await ReadAsync(host, "general", 1, ct) is not null;

    public async Task<bool> WriteAsync(
        string host, string section, int type, string root, string leaf, string? index, string value,
        CancellationToken ct)
    {
        var url = Url(host, section, "set", type) + "&data=" + Uri.EscapeDataString(BuildWrite(root, leaf, index, value));

        var result = await probe.SendAsync("GET", url, null, ct);
        var ok = result.Error is null && result.Status is >= 200 and < 300;
        if (!ok) log.LogWarning("Write to {Section}/{Type} on {Host} failed: {Status} {Error}",
            section, type, host, result.Status, result.Error);
        return ok;
    }

    /// <summary>
    /// The setup UI wraps the value in its tag, then in the document's root tag. An
    /// indexed setting - one speaker of several, one input of several - carries the
    /// index as an attribute, which is how the UI's own writes identify which one.
    /// </summary>
    public static string BuildWrite(string root, string leaf, string? index, string value)
    {
        var attribute = index is null ? "" : $" index=\"{WebUtility.HtmlEncode(index)}\"";
        return $"<{root}><{leaf}{attribute}>{WebUtility.HtmlEncode(value)}</{leaf}></{root}>";
    }

    /// <summary>
    /// Flattens a config document into rows.
    ///
    /// The receiver describes each setting as it goes: "display" of 1 means it does not
    /// apply to this configuration at all (no centre speaker, so no centre settings),
    /// and gray marks one that exists but cannot be changed right now. Choices come
    /// either from a List of named alternatives - where position is the code the
    /// receiver wants back - or from a SelectableValue list shared by repeated rows.
    /// </summary>
    public static IReadOnlyList<ConfigRow> Flatten(XDocument document)
    {
        var root = document.Root;
        if (root is null) return [];

        var shared = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName is "SelectableValue")
            ?.Descendants().Where(e => e.Name.LocalName is "Item" or "Value")
            .Select(e => new ConfigChoice(e.Value.Trim(), e.Value.Trim()))
            .Where(c => c.Code.Length > 0)
            .ToList() ?? [];

        // Input assign ends with a SelectionList: not settings, but the codes each
        // column will take, the code being the Item's own index. Walking it as data
        // invented a row of its own; read as a vocabulary it is what the dropdowns
        // should have been offering all along, straight from the receiver.
        var vocabulary = new Dictionary<string, IReadOnlyList<ConfigChoice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in root.Elements().FirstOrDefault(e => e.Name.LocalName is "SelectionList")?.Elements()
                 ?? [])
        {
            var choices = column.Descendants()
                .Where(e => e.Name.LocalName is "Item" or "Value")
                .Select(e => new ConfigChoice(e.Attribute("index")?.Value ?? e.Value.Trim(), e.Value.Trim()))
                .Where(c => c.Code.Length > 0)
                .ToList();

            if (choices.Count > 0) vocabulary[column.Name.LocalName] = choices;
        }

        var rows = new List<ConfigRow>();
        Walk(root, "", null, rows, shared, vocabulary);
        return rows;
    }

    private static readonly string[] ValueNames = ["Value", "Source"];

    private static void Walk(
        XElement element, string prefix, string? inheritedIndex,
        List<ConfigRow> rows, IReadOnlyList<ConfigChoice> shared,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigChoice>>? vocabulary = null)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            if (name is "SelectableValue" or "SelectionList") continue;

            // Not applicable to how this receiver is set up.
            if (child.Attribute("display")?.Value == "1") continue;

            // Input assign hangs its settings off <Source index="1">, so a child with
            // no index of its own still belongs to that row - and a write that forgot
            // the index would land on the wrong input.
            var index = child.Attribute("index")?.Value ?? inheritedIndex;

            var label = prefix.Length > 0 ? $"{prefix} · {name}" : name;
            if (child.Attribute("index")?.Value is { } own) label += $" {own}";

            // The receiver grades its own controls: 1 does not apply here (dropped
            // above), 3 can be changed, 2 exists but not right now - the crossover
            // for an individual speaker while the selection says All, say. Reading
            // 2 as changeable offered edits the receiver had already refused.
            var locked = child.Attribute("gray")?.Value is "1" or "2"
                      || child.Attribute("display")?.Value == "2";

            var valueChild = child.Elements().FirstOrDefault(e => ValueNames.Contains(e.Name.LocalName));
            var lists = child.Elements().Where(e => e.Name.LocalName == "List").ToList();
            var others = child.Elements()
                .Where(e => e.Name.LocalName is not ("List" or "Value" or "Source"))
                .ToList();

            // One List of differently-named alternatives is an enumeration, and the
            // receiver refers to each by its position.
            var enumeration = lists.Count == 1
                && lists[0].Elements().Count() > 1
                && lists[0].Elements().Select(e => e.Name.LocalName).Distinct().Count()
                   == lists[0].Elements().Count();

            // Repeated <List> siblings are this row's own allowed codes, which is how
            // each speaker says what it will accept.
            var ownCodes = lists.Count > 1 && lists.All(l => !l.HasElements)
                ? lists.Select(l => l.Value.Trim()).Where(v => v.Length > 0).ToList()
                : [];

            if (others.Count > 0 && !enumeration && ownCodes.Count == 0)
            {
                Walk(child, label, index, rows, shared, vocabulary);
                continue;
            }

            var published = vocabulary is not null && vocabulary.TryGetValue(name, out var listed)
                ? listed
                : null;

            var options =
                enumeration ? lists[0].Elements()
                    .Select((e, i) => new ConfigChoice((i + 1).ToString(), e.Value.Trim())).ToList()
                : ownCodes.Count > 0 ? ownCodes.Select(c => new ConfigChoice(c, c)).ToList()
                : published is not null ? published
                : name == "Speaker" && shared.Count > 0 ? shared.ToList()
                : [];

            var value = (valueChild?.Value ?? child.Value).Trim();
            if (value.Length == 0 && options.Count == 0) continue;

            rows.Add(new ConfigRow(name, label, value, options, index, locked));

            // Rows nested inside a plain List, as the per-speaker crossovers are.
            if (!enumeration && ownCodes.Count == 0 && lists.Count == 1)
                Walk(lists[0], label, index, rows, shared, vocabulary);
        }
    }
}
