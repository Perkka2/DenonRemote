namespace DenonRemote.Models;

public enum CommandOutcome
{
    /// <summary>Sent, waiting for the receiver to report back.</summary>
    Pending,
    /// <summary>The receiver reported the value we asked for.</summary>
    Confirmed,
    /// <summary>The receiver reported something else - it clamped or overrode us.</summary>
    Mismatch,
    /// <summary>Nothing came back. Usually means the unit doesn't support the command.</summary>
    NoResponse,
}

/// <summary>What became of one command. Keyed by setting, so the newest wins.</summary>
public sealed record CommandStatus(
    string Key,
    string Label,
    string Command,
    string? Expected,
    string? Actual,
    CommandOutcome Outcome,
    DateTimeOffset At)
{
    public bool Failed => Outcome is CommandOutcome.Mismatch or CommandOutcome.NoResponse;

    public string Describe => Outcome switch
    {
        CommandOutcome.Confirmed => $"{Label} confirmed",
        CommandOutcome.Mismatch => $"{Label}: asked for {Expected}, receiver reports {Actual}",
        CommandOutcome.NoResponse => $"{Label}: no reply to {Command} — this unit probably doesn't support it",
        _ => $"{Label} pending",
    };
}
