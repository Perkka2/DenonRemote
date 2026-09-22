namespace DenonRemote.Models;

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
