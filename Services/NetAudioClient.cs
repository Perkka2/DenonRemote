using System.Xml.Linq;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// The network player on the pre-HEOS units (AVR-X4100W and kin).
///
/// Those receivers have no HEOS and answer the AppCommand API with an empty
/// document, so the only account of what is playing is the one their own web UI
/// uses: a status document polled once a second, and commands posted back as form
/// fields. Both were read out of that UI's JavaScript (see the README).
///
///   GET  /goform/formNetAudio_StatusXml.xml
///   POST /NetAudio/index.put.asp   cmd0=PutNetAudioCommand/CurDown
///                                  cmd1=aspMainZone_WebUpdateStatus/
///                                  ZoneName=MAIN ZONE
/// </summary>
public sealed class NetAudioClient(ILogger<NetAudioClient> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>The UI keeps this in a cookie and defaults it to MAIN ZONE.</summary>
    private const string MainZone = "MAIN ZONE";

    public NetAudioState State { get; } = new();

    /// <summary>Raised when a poll changed anything, so the panel can redraw.</summary>
    public event Action? Changed;

    private string? _signature;

    public static string StatusUrl(string host) =>
        $"http://{host}/goform/formNetAudio_StatusXml.xml";

    public static string CommandUrl(string host) => $"http://{host}/NetAudio/index.put.asp";

    /// <summary>
    /// The album art the receiver is serving, with the read time as a cache-buster -
    /// the URL never changes otherwise, so a browser would show the first cover for
    /// the rest of the session.
    /// </summary>
    public string? ArtUrl(string host) => State.HasArt
        ? $"http://{host}/NetAudio/art.asp-jpg?{State.ReadAt.ToUnixTimeMilliseconds()}"
        : null;

    /// <summary>True when this receiver answers the network-player document at all.</summary>
    public async Task<bool> ProbeAsync(string host, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(StatusUrl(host), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogDebug("No network player on {Host}: {Message}", host, ex.Message);
            return false;
        }
    }

    public async Task<bool> RefreshAsync(string host, CancellationToken ct)
    {
        try
        {
            var xml = await Http.GetStringAsync(StatusUrl(host), ct);
            var changed = Apply(XDocument.Parse(xml));
            if (changed) Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug("Network player read failed on {Host}: {Message}", host, ex.Message);
            return false;
        }
    }

    /// <summary>Separated from the fetch so the self-test can exercise it.</summary>
    internal bool Apply(XDocument document)
    {
        var next = Parse(document);
        if (next is null) return false;

        // Polling once a second would otherwise redraw the panel every second
        // whether or not anything moved.
        var signature = Signature(next);
        var changed = signature != _signature;
        _signature = signature;

        State.Lines.Clear();
        State.Lines.AddRange(next.Lines);
        State.LineFlags.Clear();
        State.LineFlags.AddRange(next.LineFlags);
        State.Service = next.Service;
        State.Input = next.Input;
        State.HasArt = next.HasArt;
        State.Repeat = next.Repeat;
        State.Shuffle = next.Shuffle;
        State.Known = true;
        if (changed) State.ReadAt = DateTimeOffset.UtcNow;

        return changed;
    }

    private static string Signature(NetAudioState state) => string.Join(
        '\u001f',
        [.. state.Lines, state.Service ?? "", state.Input ?? "",
         state.HasArt ? "art" : "", state.Repeat ? "rep" : "", state.Shuffle ? "shf" : ""]);

    /// <summary>Reads the status document. Static and public to the tests.</summary>
    internal static NetAudioState? Parse(XDocument document)
    {
        var root = document.Root;
        if (root is null) return null;

        string Value(string tag) => root
            .Elements().FirstOrDefault(e => e.Name.LocalName == tag)
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "value")?.Value.Trim() ?? "";

        List<string> Values(string tag) => root
            .Elements().FirstOrDefault(e => e.Name.LocalName == tag)
            ?.Elements().Where(e => e.Name.LocalName == "value")
            .Select(e => e.Value.Trim()).ToList() ?? [];

        var state = new NetAudioState
        {
            Service = Empty(Value("NetPlayingTitle")),
            Input = Empty(Value("InputFuncSelect")),
            // 0 means no art; anything else is a handle to it.
            HasArt = Value("Art") is { Length: > 0 } art && art != "0",
            Repeat = Value("NetAudioRepeat").Equals("ON", StringComparison.OrdinalIgnoreCase),
            Shuffle = Value("NetAudioRandom").Equals("ON", StringComparison.OrdinalIgnoreCase),
        };

        var lines = Values("szLine");
        // The buffer is a fixed ten slots and the tail is usually empty; keeping the
        // trailing blanks would draw an empty screen as ten blank rows.
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        state.Lines.AddRange(lines);

        var flags = Values("chFlag");
        for (var i = 0; i < state.Lines.Count; i++)
            state.LineFlags.Add(i < flags.Count && int.TryParse(flags[i], out var flag) ? flag : 0);

        return state;
    }

    private static string? Empty(string value) => value.Length > 0 ? value : null;

    // ------------------------------------------------------------- commands

    public Task CursorUp(string host, CancellationToken ct) => SendAsync(host, "CurUp", ct);
    public Task CursorDown(string host, CancellationToken ct) => SendAsync(host, "CurDown", ct);
    public Task CursorLeft(string host, CancellationToken ct) => SendAsync(host, "CurLeft", ct);
    public Task CursorRight(string host, CancellationToken ct) => SendAsync(host, "CurRight", ct);
    public Task Enter(string host, CancellationToken ct) => SendAsync(host, "CurEnter", ct);
    public Task PageUp(string host, CancellationToken ct) => SendAsync(host, "CmdPageUp", ct);
    public Task PageDown(string host, CancellationToken ct) => SendAsync(host, "CmdPageDown", ct);
    public Task Stop(string host, CancellationToken ct) => SendAsync(host, "CmdStop", ct);
    public Task ToggleRepeat(string host, CancellationToken ct) => SendAsync(host, "CmdRepeatOnOff", ct);
    public Task ToggleShuffle(string host, CancellationToken ct) => SendAsync(host, "CmdRandomOnOff", ct);

    /// <summary>
    /// The UI sends the command and, in the same post, asks the receiver to refresh
    /// the status it will serve next. Without that second field the document can
    /// still describe the screen as it was before the keypress.
    /// </summary>
    internal static Dictionary<string, string> Body(string command) => new()
    {
        ["cmd0"] = "PutNetAudioCommand/" + command,
        ["cmd1"] = "aspMainZone_WebUpdateStatus/",
        ["ZoneName"] = MainZone,
    };

    private async Task SendAsync(string host, string command, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(Body(command));
            using var response = await Http.PostAsync(CommandUrl(host), content, ct);
            if (!response.IsSuccessStatusCode)
                log.LogWarning("Network player command {Command} on {Host} returned {Status}",
                    command, host, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogWarning("Network player command {Command} on {Host} failed: {Message}",
                command, host, ex.Message);
            return;
        }

        // The screen has moved; show it rather than waiting for the next poll.
        await RefreshAsync(host, ct);
    }
}
