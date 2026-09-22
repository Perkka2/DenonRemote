namespace DenonRemote.Models;

/// <summary>
/// What a particular receiver can actually do, read from the unit itself rather than
/// assumed. Falls back to the static catalog when the unit does not answer.
/// </summary>
public sealed class DeviceProfile
{
    public string Model { get; set; } = "";
    public int ZoneCount { get; set; } = 2;
    public bool Heos { get; set; }

    /// <summary>Inputs this unit has, with the names configured on the unit.</summary>
    public List<SourceOption> Sources { get; set; } = [];

    /// <summary>True when the source list came from the receiver, not the fallback table.</summary>
    public bool SourcesFromDevice { get; set; }

    /// <summary>The receiver's own zone names, keyed by zone number.</summary>
    public Dictionary<int, string> ZoneNames { get; } = [];

    /// <summary>The receiver's Quick Select names, keyed by slot.</summary>
    public Dictionary<int, string> QuickSelectNames { get; } = [];

    /// <summary>True when the setup API answered, which is how the newer firmware talks.</summary>
    public bool SetupApi { get; set; }

    /// <summary>
    /// True when the pre-HEOS network player answered. On those units it is the only
    /// account of what is playing, HEOS being absent.
    /// </summary>
    public bool NetAudio { get; set; }

    public string ZoneName(int zone) =>
        ZoneNames.TryGetValue(zone, out var name) && name.Length > 0
            ? name
            : zone == 1 ? "Main zone" : $"Zone {zone}";

    public string QuickSelectName(int slot) =>
        QuickSelectNames.TryGetValue(slot, out var name) && name.Length > 0
            ? name
            : slot.ToString();

    public static DeviceProfile Fallback(string model) => new()
    {
        Model = model,
        ZoneCount = 2,
        Sources = [.. DenonCatalog.Sources],
    };
}
