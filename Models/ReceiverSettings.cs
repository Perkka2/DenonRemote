namespace DenonRemote.Models;

/// <summary>Per-receiver guardrails, stored alongside the receiver list.</summary>
public sealed class ReceiverSettings
{
    /// <summary>Hard ceiling on the Denon scale. Nothing the app sends exceeds it.</summary>
    public double MaxVolume { get; set; } = 80;

    /// <summary>Above this, a jump has to be confirmed. Null disables the prompt.</summary>
    public double? ConfirmAbove { get; set; } = 65;

    /// <summary>Ceiling applied while night mode is on.</summary>
    public double NightMaxVolume { get; set; } = 50;

    /// <summary>Dynamic Volume setting applied by night mode.</summary>
    public string NightDynamicVolume { get; set; } = "MED";

    /// <summary>Night mode survives a restart; it used to be forgotten.</summary>
    public bool NightMode { get; set; }
}
