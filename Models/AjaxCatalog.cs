namespace DenonRemote.Models;

/// <summary>
/// One config document on the receiver's setup API: /ajax/{Section}/get_config?type={Type}.
/// The type numbers come from the receiver's own setup UI (see discovery/).
/// </summary>
public sealed record ConfigGroup(string Section, int Type, string Label, string? Note = null);

public static class AjaxCatalog
{
    /// <summary>Groups worth showing. Ordered as the setup UI orders them.</summary>
    public static readonly ConfigGroup[] Groups =
    [
        new("speakers", 2,  "Amp assign"),
        new("speakers", 3,  "Speaker config"),
        new("speakers", 4,  "Distances"),
        new("speakers", 5,  "Channel levels"),
        new("speakers", 6,  "Crossovers"),
        new("speakers", 7,  "Bass"),

        new("video", 3, "HDMI setup"),
        new("video", 4, "Output settings"),
        new("video", 7, "On-screen display"),
        new("video", 8, "4K signal format"),
        new("video", 9, "TV format"),

        new("inputs", 2, "Input assign"),
        new("inputs", 4, "Hidden sources"),
        new("inputs", 5, "Source level"),

        new("general", 3,  "ECO"),
        new("general", 4,  "Zone setup", "Zone 2 tone, levels and volume limits"),
        new("general", 10, "Front display"),
        new("general", 11, "Firmware updates"),
        new("general", 17, "Auto standby"),

        new("advanced", 9, "EDID log"),
    ];
}

/// <summary>One choice a setting will accept: the receiver's code and its own word for it.</summary>
public sealed record ConfigChoice(string Code, string Text);

/// <summary>A single setting read out of a config document.</summary>
public sealed record ConfigRow(
    string Name,
    string Label,
    string Value,
    IReadOnlyList<ConfigChoice> Options,
    string? Index,
    bool Locked,
    string? Field = null)
{
    /// <summary>
    /// What a write names this setting. Usually the same as <see cref="Name"/>, but
    /// in a table Name is the column heading - "HDMI" - shared by every row, while
    /// the control posting the value is listHdmiAssignSAT/CBL. Writing by the
    /// heading would aim at a field the page does not have.
    /// </summary>
    public string PostName => Field ?? Name;

    public bool HasOptions => Options.Count > 0;

    /// <summary>
    /// Changeable when the receiver offers choices and hasn't greyed it out. It greys
    /// out what doesn't apply to the current setup - centre channel settings with no
    /// centre speaker, for instance.
    /// </summary>
    public bool Editable => HasOptions && !Locked;
}
