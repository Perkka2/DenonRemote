namespace DenonRemote.Models;

/// <summary>Persisted definition of one receiver.</summary>
public sealed class ReceiverConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public ReceiverSettings Settings { get; set; } = new();

    /// <summary>
    /// Names this receiver, falling back so the list can never show a nameless row.
    ///
    /// The name is the user's, not the receiver's - discovery only seeds it with
    /// whatever the unit calls itself, which is its model number, and that is no use
    /// at all with two of them in the house. Clearing it returns to that seed rather
    /// than leaving a blank, and this is the one place that rule lives so adding a
    /// receiver and renaming one cannot disagree about it.
    /// </summary>
    public void SetName(string? wanted)
    {
        var name = wanted?.Trim() ?? "";

        Name = name.Length > 0 ? name
            : string.IsNullOrWhiteSpace(Model) ? Host
            : Model;
    }
}
