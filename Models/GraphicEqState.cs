namespace DenonRemote.Models;

/// <summary>Which mechanism a receiver exposes its graphic EQ through.</summary>
public enum GraphicEqBackend
{
    None,
    /// <summary>Pre-HEOS units: a form POST to the ASP setup pages.</summary>
    LegacyAsp,
    /// <summary>HEOS generation: /ajax/audio set_config with GraphicEQ XML.</summary>
    AjaxConfig,
}

/// <summary>
/// The nine bands are fixed across models. The newer API sends tenths of a dB
/// (2.5 dB is "25"); the ASP form sends decimal dB ("2.5").
/// </summary>
public sealed record EqBand(string Key, string Label, string AjaxTag, string LegacyField);

public sealed class GraphicEqState
{
    public GraphicEqBackend Backend { get; set; }
    public bool Available => Backend != GraphicEqBackend.None;

    public bool? Enabled { get; set; }

    /// <summary>ALL / LRS / EAC on the legacy units; the newer API uses its own tokens.</summary>
    public string? SpeakerSelection { get; set; }
    public List<OptionValue> SpeakerSelections { get; } = [];

    /// <summary>The channel currently being adjusted, and what can be chosen.</summary>
    public string? Channel { get; set; }
    public List<OptionValue> Channels { get; } = [];

    /// <summary>Band values in dB, keyed by band key.</summary>
    public Dictionary<string, double> Bands { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double Min { get; set; } = -20;
    public double Max { get; set; } = 6;
    public double Step { get; set; } = 0.5;

    public string? Note { get; set; }
    public DateTimeOffset? LastRead { get; set; }
}

public static class GraphicEq
{
    /// <summary>63 Hz to 16 kHz, in the order both UIs show them.</summary>
    public static readonly EqBand[] Bands =
    [
        new("63",   "63 Hz",  "Eq63Hz",  "textGEQ63"),
        new("125",  "125 Hz", "Eq125Hz", "textGEQ125"),
        new("250",  "250 Hz", "Eq250Hz", "textGEQ250"),
        new("500",  "500 Hz", "Eq500Hz", "textGEQ500"),
        new("1k",   "1 kHz",  "Eq1kHz",  "textGEQ1k"),
        new("2k",   "2 kHz",  "Eq2kHz",  "textGEQ2k"),
        new("4k",   "4 kHz",  "Eq4kHz",  "textGEQ4k"),
        new("8k",   "8 kHz",  "Eq8kHz",  "textGEQ8k"),
        new("16k",  "16 kHz", "Eq16kHz", "textGEQ16k"),
    ];
}
