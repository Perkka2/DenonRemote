namespace DenonRemote.Models;

/// <summary>What a scene sets. Powering the main zone on is always part of it.</summary>
[Flags]
public enum SceneParts
{
    None = 0,
    Input = 1,
    SoundMode = 2,
    /// <summary>Volume, and unmuting with it.</summary>
    Volume = 4,
    /// <summary>The other zones: power, and their input and volume if those are included.</summary>
    Zones = 8,
    All = Input | SoundMode | Volume | Zones,
}

/// <summary>
/// A named list of raw protocol commands. "@DELAY:1500" waits instead of sending,
/// which matters after a power-on before the receiver accepts an input change.
/// </summary>
public sealed class Scene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string ReceiverId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Commands { get; set; } = [];
}
