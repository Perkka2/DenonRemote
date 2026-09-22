namespace DenonRemote.Models;

/// <summary>A setting's human label and, where it is an enumeration, what its codes mean.</summary>
public sealed record FieldLabels(string Label, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// The setup API answers in the receiver's own numeric codes - "2" rather than "Large".
/// Its setup UI translates them, so this table was harvested from that UI running against
/// a real receiver (see discovery/ and the README): every dropdown's label and its
/// value/text pairs, matched back to the XML tag each one belongs to.
///
/// Entries are keyed section/type/tag, and again with the row index where the choices
/// differ per row - the centre channel offers None/Small/Large where the fronts offer
/// only Small/Large, and the subwoofer offers Yes/No. Anything absent falls back to
/// showing the raw value, which is honest rather than guessed.
/// </summary>
public static class AjaxLabels
{
    /// <summary>Label and value meanings, keyed section/type/tag and section/type/tag/index.</summary>
    public static readonly Dictionary<string, FieldLabels> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/4/Subwoofer"] = new("Subwoofer", new Dictionary<string, string> { ["0"] = "Off", ["1"] = "On" }),
        ["audio/6/Adjust"] = new("Adjust", new Dictionary<string, string>()),
        ["audio/6/AutoLipSync"] = new("Auto Lip Sync", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["audio/7/Limit"] = new("Limit", new Dictionary<string, string> { ["1"] = "Off", ["2"] = "-20dB", ["3"] = "-10dB", ["4"] = "0dB" }),
        ["audio/7/MuteLevel"] = new("Mute Level", new Dictionary<string, string> { ["1"] = "Full", ["3"] = "-40dB", ["2"] = "-20dB" }),
        ["audio/7/PowerOnLevel"] = new("Power On Level", new Dictionary<string, string>()),
        ["audio/7/Scale"] = new("Scale", new Dictionary<string, string> { ["1"] = "0-98", ["2"] = "-79.5dB - 18.0dB" }),
        ["audio/9/MultEQ"] = new("MultEQ XT", new Dictionary<string, string> { ["1"] = "Reference", ["2"] = "L/R Bypass", ["3"] = "Flat", ["4"] = "Off" }),
        ["general/10/Dimmer"] = new("Dimmer", new Dictionary<string, string> { ["1"] = "Bright", ["2"] = "Dim", ["3"] = "Dark", ["4"] = "Off" }),
        ["general/11/AllowUpdate"] = new("Allow Update", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["general/11/AutoUpdate"] = new("Auto-Update", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["general/11/TimeZone"] = new("Time Zone", new Dictionary<string, string> { ["2"] = "Kiritimati Time (+14H)", ["3"] = "Samoa, Nuku'alofa Time (+13H)", ["4"] = "New Zealand, Fiji Time (+12H)", ["5"] = "Solomon Islands, New Caledonia Time (+11H)", ["6"] = "Australian Eastern Standard Time (+10H)", ["7"] = "Vladivostok Time (+10H)", ["8"] = "Australian Central Standard Time (+9.5H)", ["9"] = "Japan Standard Time (+9H)", ["10"] = "Korea Standard Time (+9H)", ["11"] = "Yakutsk Time (+9H)", ["12"] = "Chinese Standard Time (+8H)", ["13"] = "Australian Western Standard Time (+8H)", ["14"] = "Irkutsk Time (+8H)", ["15"] = "Krasnoyarsk Time (+7H)", ["16"] = "Myanmar Time (+6.5H)", ["17"] = "Omsk Time (+6H)", ["18"] = "Nepal Time (+5.75H)", ["19"] = "Indian Standard Time (+5.5H)", ["20"] = "Sri Lanka Time (+5.5H)", ["21"] = "Yekaterinburg Time (+5H)", ["22"] = "Afghanistan Time (+4.5H)", ["23"] = "Samara, Baku, Muscat, Tbilisi Time (+4H)", ["24"] = "Iran Time (+3.5H)", ["25"] = "Moscow Standard Time (+3H)", ["26"] = "Kaliningrad Time (+2H)", ["27"] = "Eastern European Time (+2H)", ["28"] = "Central European Time (+1H)", ["29"] = "Greenwich Mean Time (0H)", ["30"] = "Western European Time (0H)", ["31"] = "Azores, Cape Verde Time (-1H)", ["32"] = "(-2H)", ["33"] = "Brasilia, Buenos Aires Time (-3H)", ["34"] = "Newfoundland Standard Time (-3.5H)", ["35"] = "Atlantic Standard Time (-4H)", ["36"] = "Eastern Standard Time (-5H)", ["37"] = "Central Standard Time (-6H)", ["38"] = "Mountain Standard Time (-7H)", ["39"] = "Pacific Standard Time (-8H)", ["40"] = "Alaska Standard Time (-9H)", ["41"] = "Hawaii-Aleutian Standard Time (-10H)", ["42"] = "(-11H)", ["43"] = "(-12H)" }),
        ["general/11/UpgradeNotification"] = new("Upgrade Notification", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["general/3/MainZone"] = new("MAIN ZONE", new Dictionary<string, string> { ["3"] = "60 min", ["2"] = "30 min", ["1"] = "15 min", ["4"] = "Off" }),
        ["general/3/Mode"] = new("Mode", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Auto", ["3"] = "Off" }),
        ["general/3/OnScreenDisplay"] = new("On Screen Display", new Dictionary<string, string> { ["1"] = "Always On", ["2"] = "Auto", ["3"] = "Off" }),
        ["general/3/PowerOnDefault"] = new("Power On Default", new Dictionary<string, string> { ["3"] = "Last", ["1"] = "On", ["2"] = "Auto", ["4"] = "Off" }),
        ["general/3/Zone2"] = new("ZONE2", new Dictionary<string, string> { ["3"] = "8 hours", ["2"] = "4 hours", ["1"] = "2 hours", ["4"] = "Off" }),
        ["general/4/LchLevel"] = new("Lch Level", new Dictionary<string, string> { ["-12dB"] = "-12dB", ["-11dB"] = "-11dB", ["-10dB"] = "-10dB", ["-9dB"] = "-9dB", ["-8dB"] = "-8dB", ["-7dB"] = "-7dB", ["-6dB"] = "-6dB", ["-5dB"] = "-5dB", ["-4dB"] = "-4dB", ["-3dB"] = "-3dB", ["-2dB"] = "-2dB", ["-1dB"] = "-1dB", ["0dB"] = "0dB", ["+1dB"] = "+1dB", ["+2dB"] = "+2dB", ["+3dB"] = "+3dB", ["+4dB"] = "+4dB", ["+5dB"] = "+5dB", ["+6dB"] = "+6dB", ["+7dB"] = "+7dB", ["+8dB"] = "+8dB", ["+9dB"] = "+9dB", ["+10dB"] = "+10dB", ["+11dB"] = "+11dB", ["+12dB"] = "+12dB" }),
        ["general/4/MuteLevel"] = new("Mute Level", new Dictionary<string, string> { ["1"] = "Full", ["2"] = "-20dB", ["3"] = "-40dB" }),
        ["general/4/RchLevel"] = new("Rch Level", new Dictionary<string, string> { ["-12dB"] = "-12dB", ["-11dB"] = "-11dB", ["-10dB"] = "-10dB", ["-9dB"] = "-9dB", ["-8dB"] = "-8dB", ["-7dB"] = "-7dB", ["-6dB"] = "-6dB", ["-5dB"] = "-5dB", ["-4dB"] = "-4dB", ["-3dB"] = "-3dB", ["-2dB"] = "-2dB", ["-1dB"] = "-1dB", ["0dB"] = "0dB", ["+1dB"] = "+1dB", ["+2dB"] = "+2dB", ["+3dB"] = "+3dB", ["+4dB"] = "+4dB", ["+5dB"] = "+5dB", ["+6dB"] = "+6dB", ["+7dB"] = "+7dB", ["+8dB"] = "+8dB", ["+9dB"] = "+9dB", ["+10dB"] = "+10dB", ["+11dB"] = "+11dB", ["+12dB"] = "+12dB" }),
        ["general/4/VolumeLevel"] = new("Volume Level", new Dictionary<string, string>()),
        ["inputs/2/Analog"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/1"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/2"] = new("DVD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/3"] = new("Blu-ray", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/4"] = new("Game", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/5"] = new("Media Player", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/6"] = new("TV Audio", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/7"] = new("AUX1", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/8"] = new("AUX2", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Analog/9"] = new("CD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4" }),
        ["inputs/2/Digital"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/1"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/2"] = new("DVD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/3"] = new("Blu-ray", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/4"] = new("Game", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/5"] = new("Media Player", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/6"] = new("TV Audio", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/7"] = new("AUX1", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/8"] = new("AUX2", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/Digital/9"] = new("CD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "OPT1", ["4"] = "OPT2" }),
        ["inputs/2/HDMI"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/1"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/2"] = new("DVD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/3"] = new("Blu-ray", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/4"] = new("Game", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/5"] = new("Media Player", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/7"] = new("AUX1", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/8"] = new("AUX2", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/HDMI/9"] = new("CD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "1", ["4"] = "2", ["5"] = "3", ["6"] = "4", ["7"] = "5", ["8"] = "6", ["9"] = "7", ["10"] = "Front" }),
        ["inputs/2/Video"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/1"] = new("CBL/SAT", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/2"] = new("DVD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/3"] = new("Blu-ray", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/4"] = new("Game", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/5"] = new("Media Player", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/6"] = new("TV Audio", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/7"] = new("AUX1", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/8"] = new("AUX2", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/2/Video/9"] = new("CD", new Dictionary<string, string> { ["1"] = "-", ["3"] = "VIDEO1", ["4"] = "VIDEO2", ["8"] = "COMP1", ["9"] = "COMP2" }),
        ["inputs/4/Source"] = new("DVD", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/10"] = new("Phono", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/11"] = new("Tuner", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/2"] = new("DVD", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/3"] = new("Blu-ray", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/4"] = new("Game", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/5"] = new("Media Player", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/7"] = new("AUX1", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/8"] = new("AUX2", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["inputs/4/Source/9"] = new("CD", new Dictionary<string, string> { ["1"] = "Show", ["2"] = "Hide" }),
        ["speakers/2/AssignMode"] = new("Assign Mode", new Dictionary<string, string> { ["1"] = "Surround Back", ["2"] = "ZONE2", ["7"] = "Bi-Amp", ["6"] = "Front B", ["5"] = "Front Height", ["8"] = "Top Front", ["9"] = "Top Middle", ["10"] = "Front Dolby", ["11"] = "Surround Dolby" }),
        ["speakers/3/Speaker"] = new("Front", new Dictionary<string, string> { ["2"] = "Small", ["3"] = "Large" }),
        ["speakers/3/Speaker/0"] = new("Front", new Dictionary<string, string> { ["2"] = "Small", ["3"] = "Large" }),
        ["speakers/3/Speaker/1"] = new("Center", new Dictionary<string, string> { ["1"] = "None", ["2"] = "Small", ["3"] = "Large" }),
        ["speakers/3/Speaker/2"] = new("Subwoofer", new Dictionary<string, string> { ["5"] = "No", ["4"] = "Yes" }),
        ["speakers/3/Speaker/3"] = new("Surround", new Dictionary<string, string> { ["1"] = "None", ["2"] = "Small", ["3"] = "Large" }),
        ["speakers/4/Unit"] = new("Unit", new Dictionary<string, string> { ["1"] = "Meters", ["2"] = "Feet" }),
        ["speakers/6/Selection"] = new("Speaker Selection", new Dictionary<string, string> { ["1"] = "All", ["2"] = "Individual" }),
        ["speakers/6/Speaker"] = new("Surround", new Dictionary<string, string> { ["40"] = "40Hz", ["60"] = "60Hz", ["80"] = "80Hz", ["90"] = "90Hz", ["100"] = "100Hz", ["110"] = "110Hz", ["120"] = "120Hz", ["150"] = "150Hz", ["200"] = "200Hz", ["250"] = "250Hz" }),
        ["speakers/6/Speaker/2"] = new("Surround", new Dictionary<string, string> { ["40"] = "40Hz", ["60"] = "60Hz", ["80"] = "80Hz", ["90"] = "90Hz", ["100"] = "100Hz", ["110"] = "110Hz", ["120"] = "120Hz", ["150"] = "150Hz", ["200"] = "200Hz", ["250"] = "250Hz" }),
        ["speakers/7/LPFforLFE"] = new("LPF for LFE", new Dictionary<string, string> { ["80"] = "80Hz", ["90"] = "90Hz", ["100"] = "100Hz", ["110"] = "110Hz", ["120"] = "120Hz", ["150"] = "150Hz", ["200"] = "200Hz", ["250"] = "250Hz" }),
        ["video/3/HDMIControl"] = new("HDMI Control", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["video/3/PassThroughSource"] = new("Pass Through Source", new Dictionary<string, string> { ["1"] = "Last", ["2"] = "CBL/SAT", ["3"] = "DVD", ["4"] = "Blu-ray", ["5"] = "Game", ["6"] = "Media Player", ["7"] = "AUX2", ["9"] = "AUX1" }),
        ["video/3/PowerOffControl"] = new("Power Off Control", new Dictionary<string, string> { ["1"] = "All", ["2"] = "Video", ["3"] = "Off" }),
        ["video/3/PowerSaving"] = new("Power Saving", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["video/3/SmartMenu"] = new("Smart Menu", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["video/3/TVAudioSwitching"] = new("TV Audio Switching", new Dictionary<string, string> { ["1"] = "On", ["2"] = "Off" }),
        ["video/4/HDMIVideoOutput"] = new("HDMI Video Output", new Dictionary<string, string> { ["1"] = "Auto(Dual)", ["2"] = "Monitor 1", ["3"] = "Monitor 2" }),
        ["video/7/Info"] = new("Info", new Dictionary<string, string> { ["0"] = "Off", ["1"] = "On" }),
        ["video/7/NowPlaying"] = new("Now Playing", new Dictionary<string, string> { ["1"] = "Always On", ["2"] = "Auto Off" }),
        ["video/7/Volume"] = new("Volume", new Dictionary<string, string> { ["3"] = "Off", ["1"] = "Bottom", ["2"] = "Top" }),
        ["video/8/Format"] = new("4K Signal Format", new Dictionary<string, string> { ["1"] = "Standard", ["2"] = "Enhanced" }),
        ["video/9/TVFormat"] = new("Format", new Dictionary<string, string> { ["1"] = "NTSC", ["2"] = "PAL" }),
    };

    /// <summary>Names for indexed rows: which speaker or input each index is.</summary>
    public static readonly Dictionary<string, string> RowNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inputs/2/Analog/1"] = "CBL/SAT",
        ["inputs/2/Analog/2"] = "DVD",
        ["inputs/2/Analog/3"] = "Blu-ray",
        ["inputs/2/Analog/4"] = "Game",
        ["inputs/2/Analog/5"] = "Media Player",
        ["inputs/2/Analog/6"] = "TV Audio",
        ["inputs/2/Analog/7"] = "AUX1",
        ["inputs/2/Analog/8"] = "AUX2",
        ["inputs/2/Analog/9"] = "CD",
        ["inputs/2/Digital/1"] = "CBL/SAT",
        ["inputs/2/Digital/2"] = "DVD",
        ["inputs/2/Digital/3"] = "Blu-ray",
        ["inputs/2/Digital/4"] = "Game",
        ["inputs/2/Digital/5"] = "Media Player",
        ["inputs/2/Digital/6"] = "TV Audio",
        ["inputs/2/Digital/7"] = "AUX1",
        ["inputs/2/Digital/8"] = "AUX2",
        ["inputs/2/Digital/9"] = "CD",
        ["inputs/2/HDMI/1"] = "CBL/SAT",
        ["inputs/2/HDMI/2"] = "DVD",
        ["inputs/2/HDMI/3"] = "Blu-ray",
        ["inputs/2/HDMI/4"] = "Game",
        ["inputs/2/HDMI/5"] = "Media Player",
        ["inputs/2/HDMI/7"] = "AUX1",
        ["inputs/2/HDMI/8"] = "AUX2",
        ["inputs/2/HDMI/9"] = "CD",
        ["inputs/2/Video/1"] = "CBL/SAT",
        ["inputs/2/Video/2"] = "DVD",
        ["inputs/2/Video/3"] = "Blu-ray",
        ["inputs/2/Video/4"] = "Game",
        ["inputs/2/Video/5"] = "Media Player",
        ["inputs/2/Video/6"] = "TV Audio",
        ["inputs/2/Video/7"] = "AUX1",
        ["inputs/2/Video/8"] = "AUX2",
        ["inputs/2/Video/9"] = "CD",
        ["inputs/4/Source/10"] = "Phono",
        ["inputs/4/Source/11"] = "Tuner",
        ["inputs/4/Source/2"] = "DVD",
        ["inputs/4/Source/3"] = "Blu-ray",
        ["inputs/4/Source/4"] = "Game",
        ["inputs/4/Source/5"] = "Media Player",
        ["inputs/4/Source/7"] = "AUX1",
        ["inputs/4/Source/8"] = "AUX2",
        ["inputs/4/Source/9"] = "CD",
        ["speakers/3/Speaker/0"] = "Front",
        ["speakers/3/Speaker/1"] = "Center",
        ["speakers/3/Speaker/2"] = "Subwoofer",
        ["speakers/3/Speaker/3"] = "Surround",
        ["speakers/6/Speaker/2"] = "Surround",
    };

    /// <summary>The row's own entry if there is one, otherwise the field's.</summary>
    public static FieldLabels? For(string section, int type, string tag, string? index = null)
    {
        if (index is not null && Fields.TryGetValue($"{section}/{type}/{tag}/{index}", out var row)) return row;
        return Fields.TryGetValue($"{section}/{type}/{tag}", out var field) ? field : null;
    }

    /// <summary>The label for one row, preferring the name of the indexed thing.</summary>
    public static string Label(string section, int type, string tag, string? index, string fallback)
    {
        if (index is not null && RowNames.TryGetValue($"{section}/{type}/{tag}/{index}", out var row))
            return row;
        return For(section, type, tag, index)?.Label is { Length: > 0 } label ? label : fallback;
    }

    /// <summary>
    /// The name of one indexed thing - which input, which speaker - and nothing else.
    /// Deliberately without the unindexed fallback that <see cref="Label"/> has: a row
    /// the harvest never saw would otherwise take the first row's name, and a table
    /// would show "CBL/SAT" twice over.
    /// </summary>
    public static string? RowName(string section, int type, string tag, string index)
    {
        if (RowNames.TryGetValue($"{section}/{type}/{tag}/{index}", out var named)) return named;

        return Fields.TryGetValue($"{section}/{type}/{tag}/{index}", out var field)
            && field.Label.Length > 0 ? field.Label : null;
    }

    /// <summary>What a code means, or the code itself when the table doesn't know.</summary>
    public static string Value(string section, int type, string tag, string value, string? index = null) =>
        For(section, type, tag, index)?.Values.TryGetValue(value, out var text) == true ? text : value;

    /// <summary>The choices for a row, empty when it isn't an enumeration we know.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Choices(
        string section, int type, string tag, string? index = null) =>
        For(section, type, tag, index)?.Values.ToList() ?? [];
}
