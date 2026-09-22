namespace DenonRemote.Models;

/// <summary>One line of protocol traffic, for the console.</summary>
public sealed record LogEntry(DateTimeOffset At, bool Outgoing, string Text)
{
    public string Arrow => Outgoing ? "→" : "←";
    public string Time => At.ToString("HH:mm:ss.fff");
}
