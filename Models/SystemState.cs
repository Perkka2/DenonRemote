namespace DenonRemote.Models;

/// <summary>Unit-wide settings that live outside the audio path.</summary>
public sealed class SystemState
{
    /// <summary>ON / AUTO / OFF.</summary>
    public string? Eco { get; set; }
    /// <summary>BRI / DIM / DAR / OFF - front panel brightness.</summary>
    public string? Dimmer { get; set; }
    /// <summary>15M / 30M / 60M / OFF.</summary>
    public string? AutoStandby { get; set; }

    /// <summary>1 / 2 / AUTO - which HDMI monitor output is active.</summary>
    public string? MonitorOut { get; set; }
    /// <summary>AMP / TV - where HDMI audio is sent.</summary>
    public string? HdmiAudio { get; set; }
    /// <summary>SOURCE / OFF / an input code - video source override.</summary>
    public string? VideoSelect { get; set; }

    /// <summary>Tuned frequency in MHz (FM) or kHz (AM).</summary>
    public double? TunerFrequency { get; set; }
    public int? TunerPreset { get; set; }
}

public static class SystemCatalog
{
    public static readonly OptionValue[] Eco =
        [new("ON", "On"), new("AUTO", "Auto"), new("OFF", "Off")];

    public static readonly OptionValue[] Dimmer =
        [new("BRI", "Bright"), new("DIM", "Dim"), new("DAR", "Dark"), new("OFF", "Off")];

    public static readonly OptionValue[] AutoStandby =
        [new("15M", "15 min"), new("30M", "30 min"), new("60M", "60 min"), new("OFF", "Off")];

    public static readonly OptionValue[] MonitorOut =
        [new("AUTO", "Auto"), new("1", "HDMI 1"), new("2", "HDMI 2")];

    public static readonly OptionValue[] HdmiAudio =
        [new("AMP", "Amp"), new("TV", "TV")];
}
