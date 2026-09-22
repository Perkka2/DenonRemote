using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// The graphic EQ, which is absent from the published control protocol - the receiver's
/// own setup UI drives it over HTTP, and the two generations do it completely differently.
///
/// Pre-HEOS units (AVR-X4100W and kin) serve ASP pages and take a form POST:
///   POST /SETUP/AUDIO/GRAPHICEQ/s_audio.asp
///   radioGraphicEQ=ON&listGEQSpSelection=LRS&listGEQAdjustEQ=FRO&textGEQ63=5.0&…&setAdjustEQ=Set
/// with band values in decimal dB.
///
/// HEOS-generation units expose the setup UI's own config API on 10443:
///   GET /ajax/audio/set_config?type=10&data=<GraphicEQ><AdjustEQ><Channel>…</Channel>
///       <Eq63Hz>25</Eq63Hz>…</AdjustEQ></GraphicEQ>
/// with band values in tenths of a dB, so 2.5 dB is "25".
///
/// Both were read off the receivers themselves rather than guessed.
/// </summary>
public sealed partial class GraphicEqClient(HttpProbe probe, ILogger<GraphicEqClient> log)
{
    private const int AjaxType = 10;

    private static string AjaxBase(string host) => $"https://{host}:10443/ajax/audio";
    private static string LegacyPage(string host) => $"http://{host}/SETUP/AUDIO/GRAPHICEQ/d_audio.asp";
    private static string LegacyPost(string host) => $"http://{host}/SETUP/AUDIO/GRAPHICEQ/s_audio.asp";

    // ---------------------------------------------------------------- detection

    /// <summary>Works out which mechanism this receiver offers, if any.</summary>
    public async Task<GraphicEqBackend> DetectAsync(string host, CancellationToken ct)
    {
        var ajax = await probe.SendAsync("GET", $"{AjaxBase(host)}/get_config?type={AjaxType}", null, ct);
        if (ajax.Interesting && ajax.Body.Contains("GraphicEQ", StringComparison.OrdinalIgnoreCase))
            return GraphicEqBackend.AjaxConfig;

        var legacy = await probe.SendAsync("GET", LegacyPage(host), null, ct);
        if (legacy.Interesting && legacy.Body.Contains("GraphicEQ", StringComparison.OrdinalIgnoreCase))
            return GraphicEqBackend.LegacyAsp;

        return GraphicEqBackend.None;
    }

    // ---------------------------------------------------------------- reading

    public async Task ReadAsync(string host, GraphicEqState state, CancellationToken ct)
    {
        if (state.Backend == GraphicEqBackend.AjaxConfig) await ReadAjaxAsync(host, state, ct);
        else if (state.Backend == GraphicEqBackend.LegacyAsp) await ReadLegacyAsync(host, state, ct);
        state.LastRead = DateTimeOffset.Now;
    }

    private async Task ReadAjaxAsync(string host, GraphicEqState state, CancellationToken ct)
    {
        var result = await probe.SendAsync("GET", $"{AjaxBase(host)}/get_config?type={AjaxType}", null, ct);
        if (!result.Interesting) { state.Note = "No answer from the receiver."; return; }

        XDocument document;
        try { document = XDocument.Parse(result.Body); }
        catch { state.Note = "Unreadable response."; return; }

        XElement? Element(string name) =>
            document.Descendants().FirstOrDefault(e => e.Name.LocalName == name);

        // The top-level Enable, not the headphone one nested above it.
        var root = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "GraphicEQ");
        var enable = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enable");
        state.Enabled = enable?.Value.Trim() == "1";

        state.SpeakerSelection = Element("SpeakerSelection")?.Value.Trim();
        state.Channel = Element("Channel")?.Value.Trim();

        // Tenths of a dB on this API.
        state.Bands.Clear();
        foreach (var band in GraphicEq.Bands)
        {
            var value = Element(band.AjaxTag)?.Value.Trim();
            if (value is null) continue;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tenths))
                state.Bands[band.Key] = tenths / 10.0;
        }

        state.Note = state.Enabled == true
            ? null
            : "Graphic EQ is off on the receiver. It can't be switched on while Audyssey MultEQ is active.";
    }

    [GeneratedRegex(@"name=['""]textGEQ([0-9]+k?)['""]\s+value=['""](-?[0-9.]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyBandValue();

    [GeneratedRegex(@"<input[^>]*id=['""]RangeGEQ[^'""]*['""][^>]*min=['""](-?[0-9.]+)['""][^>]*max=['""](-?[0-9.]+)['""][^>]*step=['""]([0-9.]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyRange();

    [GeneratedRegex(@"name=['""]radioGraphicEQ['""]\s+value=['""](ON|OFF)['""][^>]*checked", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyEnabled();

    [GeneratedRegex(@"<select[^>]*name=['""](listGEQSpSelection|listGEQAdjustEQ)['""][^>]*>(.*?)</select>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LegacySelect();

    [GeneratedRegex(@"<option\s+value=['""]([^'""]+)['""]\s*(selected)?\s*>([^<]*)</option>", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyOption();

    private async Task ReadLegacyAsync(string host, GraphicEqState state, CancellationToken ct)
    {
        var result = await probe.SendAsync("GET", LegacyPage(host), null, ct);
        if (!result.Interesting) { state.Note = "No answer from the receiver."; return; }

        var html = result.Body;

        state.Enabled = LegacyEnabled().Match(html) is { Success: true } match
            && match.Groups[1].Value.Equals("ON", StringComparison.OrdinalIgnoreCase);

        if (LegacyRange().Match(html) is { Success: true } range)
        {
            state.Min = double.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
            state.Max = double.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            state.Step = double.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        state.Bands.Clear();
        foreach (Match band in LegacyBandValue().Matches(html))
        {
            var key = band.Groups[1].Value.ToLowerInvariant();
            if (double.TryParse(band.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dB))
                state.Bands[key] = dB;
        }

        state.SpeakerSelections.Clear();
        state.Channels.Clear();

        foreach (Match select in LegacySelect().Matches(html))
        {
            var target = select.Groups[1].Value.Equals("listGEQSpSelection", StringComparison.OrdinalIgnoreCase)
                ? state.SpeakerSelections
                : state.Channels;

            foreach (Match option in LegacyOption().Matches(select.Groups[2].Value))
            {
                var code = option.Groups[1].Value;
                target.Add(new OptionValue(code, option.Groups[3].Value.Trim()));

                if (!option.Groups[2].Success) continue;
                if (ReferenceEquals(target, state.SpeakerSelections)) state.SpeakerSelection = code;
                else state.Channel = code;
            }
        }

        state.Note = state.Enabled == true ? null : "Graphic EQ is switched off on the receiver.";
    }

    // ---------------------------------------------------------------- writing

    public Task<bool> SetEnabledAsync(string host, GraphicEqState state, bool on, CancellationToken ct) =>
        state.Backend == GraphicEqBackend.AjaxConfig
            ? AjaxSetAsync(host, $"<Enable>{(on ? 1 : 2)}</Enable>", ct)
            : LegacyPostAsync(host, state, on, submit: "setAdjustEQ", ct);

    public Task<bool> SetSpeakerSelectionAsync(
        string host, GraphicEqState state, string selection, CancellationToken ct)
    {
        state.SpeakerSelection = selection;
        return state.Backend == GraphicEqBackend.AjaxConfig
            ? AjaxSetAsync(host, $"<SpeakerSelection>{WebUtility.HtmlEncode(selection)}</SpeakerSelection>", ct)
            : LegacyPostAsync(host, state, state.Enabled == true, submit: "setAdjustEQ", ct);
    }

    /// <summary>Writes every band for the currently selected channel.</summary>
    public Task<bool> SetBandsAsync(string host, GraphicEqState state, CancellationToken ct)
    {
        if (state.Backend == GraphicEqBackend.LegacyAsp)
            return LegacyPostAsync(host, state, state.Enabled == true, submit: "setAdjustEQ", ct);

        return AjaxSetAsync(host, BuildAdjustEq(state), ct);
    }

    public Task<bool> SetDefaultsAsync(string host, GraphicEqState state, CancellationToken ct) =>
        state.Backend == GraphicEqBackend.AjaxConfig
            ? AjaxSetAsync(host, "<SetDefaults>1</SetDefaults>", ct)
            : LegacyPostAsync(host, state, state.Enabled == true, submit: "setGEQSetDefaults", ct);

    public Task<bool> CurveCopyAsync(string host, GraphicEqState state, CancellationToken ct) =>
        state.Backend == GraphicEqBackend.AjaxConfig
            ? AjaxSetAsync(host, "<CurveCopy>1</CurveCopy>", ct)
            : LegacyPostAsync(host, state, state.Enabled == true, submit: "setGEQCurveCopy", ct);

    /// <summary>The AdjustEQ block, in tenths of a dB as the API expects.</summary>
    public static string BuildAdjustEq(GraphicEqState state)
    {
        var xml = new StringBuilder("<AdjustEQ>");
        if (!string.IsNullOrWhiteSpace(state.Channel))
            xml.Append($"<Channel>{WebUtility.HtmlEncode(state.Channel)}</Channel>");

        foreach (var band in GraphicEq.Bands)
        {
            if (!state.Bands.TryGetValue(band.Key, out var dB)) continue;
            var tenths = (int)Math.Round(dB * 10, MidpointRounding.AwayFromZero);
            xml.Append($"<{band.AjaxTag}>{tenths}</{band.AjaxTag}>");
        }

        xml.Append("</AdjustEQ>");
        return xml.ToString();
    }

    /// <summary>The ASP page's whole form, in decimal dB as that page expects.</summary>
    public static string BuildLegacyForm(GraphicEqState state, bool enabled, string submit)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("setPureDirectOn", "OFF"),
            new("setSetupLock", "OFF"),
            new("radioGraphicEQ", enabled ? "ON" : "OFF"),
        };

        if (!string.IsNullOrWhiteSpace(state.SpeakerSelection))
            fields.Add(new("listGEQSpSelection", state.SpeakerSelection));
        if (!string.IsNullOrWhiteSpace(state.Channel))
            fields.Add(new("listGEQAdjustEQ", state.Channel));

        foreach (var band in GraphicEq.Bands)
        {
            var value = state.Bands.TryGetValue(band.Key, out var dB) ? dB : 0;
            fields.Add(new(band.LegacyField, value.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        foreach (var button in new[] { "setAdjustEQ", "setGEQCurveCopy", "setGEQSetDefaults" })
            fields.Add(new(button, button == submit ? "Set" : "off"));

        return string.Join("&", fields.Select(f =>
            $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
    }

    private async Task<bool> AjaxSetAsync(string host, string inner, CancellationToken ct)
    {
        var data = Uri.EscapeDataString($"<GraphicEQ>{inner}</GraphicEQ>");
        var url = $"{AjaxBase(host)}/set_config?type={AjaxType}&data={data}";

        var result = await probe.SendAsync("GET", url, null, ct);
        if (result.Error is not null || result.Status is < 200 or >= 300)
            log.LogWarning("Graphic EQ write to {Host} failed: {Status} {Error}", host, result.Status, result.Error);

        return result.Error is null && result.Status is >= 200 and < 300;
    }

    /// <summary>
    /// The ASP page posts its whole form, so we rebuild it rather than sending one field.
    /// </summary>
    private async Task<bool> LegacyPostAsync(
        string host, GraphicEqState state, bool enabled, string submit, CancellationToken ct)
    {
        var body = BuildLegacyForm(state, enabled, submit);

        var result = await probe.SendFormAsync("POST", LegacyPost(host), body, ct);
        if (result.Error is not null)
            log.LogWarning("Graphic EQ post to {Host} failed: {Error}", host, result.Error);

        return result.Error is null && result.Status is >= 200 and < 400;
    }
}
