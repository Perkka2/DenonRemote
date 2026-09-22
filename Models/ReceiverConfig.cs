namespace DenonRemote.Models;

/// <summary>Persisted definition of one receiver.</summary>
public sealed class ReceiverConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public ReceiverSettings Settings { get; set; } = new();
}
