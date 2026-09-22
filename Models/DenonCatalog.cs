using System.Globalization;

namespace DenonRemote.Models;

public sealed record SourceOption(string Code, string Label);
public sealed record SoundModeOption(string Code, string Label);
public sealed record OptionValue(string Code, string Label);

/// <summary>
/// Fallback tables and label lookups. Anything the receiver tells us about itself
/// (see <see cref="DeviceProfile"/>) takes priority over the source list here.
/// Codes are raw protocol tokens: "SI" + Code, "MS" + Code, "Z2" + Code.
/// </summary>
public static class DenonCatalog
{
    public static readonly SourceOption[] Sources =
    [
        new("SAT/CBL", "Cable / Sat"),
        new("MPLAY",   "Media Player"),
        new("TV",      "TV Audio"),
        new("BD",      "Blu-ray"),
        new("DVD",     "DVD"),
        new("GAME",    "Game"),
        new("AUX1",    "AUX 1"),
        new("CD",      "CD"),
        new("PHONO",   "Phono"),
        new("TUNER",   "Tuner"),
        new("NET",     "HEOS / Net"),
        new("BT",      "Bluetooth"),
    ];

    public static readonly SourceOption FollowMain = new("SOURCE", "Follow Main");

    public static readonly SoundModeOption[] SoundModes =
    [
        new("DIRECT",         "Direct"),
        new("PURE DIRECT",    "Pure Direct"),
        new("STEREO",         "Stereo"),
        new("AUTO",           "Auto"),
        new("MOVIE",          "Movie"),
        new("MUSIC",          "Music"),
        new("GAME",           "Game"),
        new("MCH STEREO",     "Multi Ch Stereo"),
        new("DOLBY DIGITAL",  "Dolby"),
        new("DTS SURROUND",   "DTS"),
        new("VIRTUAL",        "Virtual"),
    ];

    public static readonly OptionValue[] MultEqModes =
    [
        new("AUDYSSEY", "Reference"),
        new("BYP.LR",   "Bypass L/R"),
        new("FLAT",     "Flat"),
        new("OFF",      "Off"),
    ];

    /// <summary>Newer firmware answers LIT/MED/HEV; older units answer DAY/EVE/NGT.</summary>
    public static readonly OptionValue[] DynamicVolumes =
    [
        new("OFF", "Off"),
        new("LIT", "Light"),
        new("MED", "Medium"),
        new("HEV", "Heavy"),
    ];

    public static readonly OptionValue[] RestorerModes =
    [
        new("OFF",   "Off"),
        new("MODE1", "Hi"),
        new("MODE2", "Medium"),
        new("MODE3", "Lo"),
    ];

    private static readonly Dictionary<string, string> ChannelLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FL"] = "Front L",     ["FR"] = "Front R",
        ["C"] = "Centre",       ["SW"] = "Sub trim",
        ["SW2"] = "Sub 2 trim",
        ["SL"] = "Surround L",  ["SR"] = "Surround R",
        ["SBL"] = "Back L",     ["SBR"] = "Back R",     ["SB"] = "Back",
        ["FHL"] = "Height L",   ["FHR"] = "Height R",
        ["FWL"] = "Wide L",     ["FWR"] = "Wide R",
        ["TFL"] = "Top Front L",["TFR"] = "Top Front R",
        ["TML"] = "Top Mid L",  ["TMR"] = "Top Mid R",
        ["FDL"] = "Dolby Fr L", ["FDR"] = "Dolby Fr R",
        ["SDL"] = "Dolby Sur L",["SDR"] = "Dolby Sur R",
    };

    /// <summary>
    /// The rename table reports some inputs under a different spelling than the one
    /// the SI command wants ("CBL/SAT" vs "SAT/CBL").
    /// </summary>
    private static readonly Dictionary<string, string> CodeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CBL/SAT"] = "SAT/CBL",
        ["SAT"] = "SAT/CBL",
        ["IPOD/USB"] = "USB/IPOD",
        ["USB"] = "USB/IPOD",
        ["NETWORK"] = "NET",
        ["NET/USB"] = "NET",
        ["BLUETOOTH"] = "BT",
        ["MEDIA PLAYER"] = "MPLAY",
        ["BLU-RAY"] = "BD",
        // The pre-HEOS zone documents spell this one out.
        ["TV AUDIO"] = "TV",
    };

    public static string NormaliseSourceCode(string raw)
    {
        var code = raw.Trim().ToUpperInvariant();
        return CodeAliases.TryGetValue(code, out var mapped) ? mapped : code;
    }

    public static string LabelForChannel(string code) =>
        ChannelLabels.TryGetValue(code, out var label) ? label : code;

    public static string LabelForSource(IEnumerable<SourceOption> known, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";
        var match = known.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Label;
        if (string.Equals(code, FollowMain.Code, StringComparison.OrdinalIgnoreCase)) return FollowMain.Label;
        return Pretty(code);
    }

    public static string LabelForSource(string? code) => LabelForSource(Sources, code);

    public static string LabelForMode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";
        var match = SoundModes.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
        return match?.Label ?? Pretty(code);
    }

    public static string LabelFor(OptionValue[] options, string? code, string fallback = "—")
    {
        if (string.IsNullOrWhiteSpace(code)) return fallback;
        var match = options.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
        return match?.Label ?? Pretty(code);
    }

    private static string Pretty(string code) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(code.ToLowerInvariant());
}
