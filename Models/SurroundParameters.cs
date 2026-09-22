namespace DenonRemote.Models;

public enum ParameterKind { Toggle, Choice, Level }

/// <summary>
/// One PS-family parameter: how to render it and how to turn a UI value into the
/// token the receiver expects. <see cref="Key"/> is the protocol key after "PS".
/// </summary>
public sealed record ParameterSpec(
    string Key,
    string Label,
    ParameterKind Kind,
    OptionValue[]? Options = null,
    double Min = 0,
    double Max = 10,
    double Step = 1,
    string? Unit = null,
    string? Note = null)
{
    /// <summary>Query the receiver sends back a value for, if the setting applies.</summary>
    public string Query => Key == "CINEMA EQ." ? "PSCINEMA EQ. ?" : $"PS{Key} ?";

    public string Command(string value) =>
        Key == "CINEMA EQ." ? $"PSCINEMA EQ.{value}" : $"PS{Key} {value}";
}

/// <summary>
/// The rest of the on-screen Surround Parameter menu. Which of these apply depends on
/// the sound mode and speaker layout, so the app asks for all of them and shows the
/// ones the receiver answers.
/// </summary>
public static class SurroundParameters
{
    private static readonly OptionValue[] OnOff = [new("ON", "On"), new("OFF", "Off")];

    public static readonly ParameterSpec[] All =
    [
        new("CINEMA EQ.", "Cinema EQ", ParameterKind.Toggle, OnOff,
            Note: "Softens the upper treble of film soundtracks"),

        new("LOM", "Loudness mgmt", ParameterKind.Toggle, OnOff),

        new("DRC", "Dyn. compression", ParameterKind.Choice,
            [new("OFF", "Off"), new("AUTO", "Auto"), new("LOW", "Low"), new("MID", "Mid"), new("HI", "High")]),

        new("LFE", "LFE level", ParameterKind.Level, Min: 0, Max: 10, Step: 1, Unit: "-dB",
            Note: "0 to -10 dB"),

        new("SWR", "Subwoofer", ParameterKind.Toggle, OnOff),

        new("REFLEV", "Reference offset", ParameterKind.Choice,
            [new("0", "0 dB"), new("5", "5 dB"), new("10", "10 dB"), new("15", "15 dB")]),

        new("EFF", "Effect level", ParameterKind.Level, Min: 1, Max: 15, Step: 1),

        new("RSZ", "Room size", ParameterKind.Choice,
            [new("S", "Small"), new("MS", "Med small"), new("M", "Medium"), new("ML", "Med large"), new("L", "Large")]),

        new("DELAY", "Audio delay", ParameterKind.Level, Min: 0, Max: 200, Step: 10, Unit: "ms"),

        new("PAN", "Panorama", ParameterKind.Toggle, OnOff),

        new("DIM", "Dimension", ParameterKind.Level, Min: 0, Max: 6, Step: 1),

        new("CEN", "Centre width", ParameterKind.Level, Min: 0, Max: 7, Step: 1),

        new("CEI", "Centre gain", ParameterKind.Level, Min: 0, Max: 10, Step: 1,
            Note: "0.0 to 1.0, DTS Neo:X"),

        new("CES", "Centre spread", ParameterKind.Toggle, OnOff),
    ];

    public static ParameterSpec? Find(string key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
}
