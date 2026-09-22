using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;

namespace DenonRemote.Services;

public sealed class HeosState
{
    public bool Connected { get; set; }
    public string? Song { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Station { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>play / pause / stop.</summary>
    public string? PlayState { get; set; }

    public bool HasTrack => !string.IsNullOrWhiteSpace(Song) || !string.IsNullOrWhiteSpace(Station);
}

/// <summary>
/// HEOS command line on TCP 1255 - the newer receivers only (the X4100W predates it).
/// Used for what the control protocol cannot give us: what is playing, and transport.
/// Like the control socket it stays open and registers for change events, so the
/// track updates on its own rather than being polled.
/// </summary>
public sealed class HeosClient(string host, ILogger<HeosClient> log) : IAsyncDisposable
{
    private const int Port = 1255;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private StreamWriter? _writer;
    private int? _pid;

    public HeosState State { get; } = new();
    public event Action? StateChanged;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        try { await (_loop ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _cts = null;
    }

    // ---------------------------------------------------------------- transport

    public void Play() => SetPlayState("play");
    public void Pause() => SetPlayState("pause");
    public void Stop() => SetPlayState("stop");

    public void TogglePlay() =>
        SetPlayState(string.Equals(State.PlayState, "play", StringComparison.OrdinalIgnoreCase) ? "pause" : "play");

    public void Next() => Command($"player/play_next?pid={_pid}");
    public void Previous() => Command($"player/play_previous?pid={_pid}");

    private void SetPlayState(string state)
    {
        if (_pid is null) return;
        State.PlayState = state;
        StateChanged?.Invoke();
        Command($"player/set_play_state?pid={_pid}&state={state}");
    }

    private void Command(string command)
    {
        var writer = _writer;
        if (writer is null) return;

        try
        {
            writer.Write("heos://" + command + "\r\n");
            writer.Flush();
        }
        catch (Exception ex)
        {
            log.LogDebug("HEOS write failed: {Message}", ex.Message);
        }
    }

    // ---------------------------------------------------------------- loop

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(3);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // IPv4 only - see the note in DenonClient.ConnectionLoopAsync.
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(4));
                    await tcp.ConnectAsync(host, Port, connectCts.Token);
                }

                tcp.NoDelay = true;

                using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false));

                State.Connected = true;
                backoff = TimeSpan.FromSeconds(3);
                StateChanged?.Invoke();
                log.LogInformation("HEOS connected on {Host}", host);

                Command("system/register_for_change_events?enable=on");
                Command("player/get_players");

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) throw new IOException("HEOS closed the connection.");
                    if (line.Length == 0) continue;
                    HandleLine(line);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug("HEOS connection to {Host} lost: {Message}", host, ex.Message);
            }
            finally
            {
                _writer = null;
                State.Connected = false;
                StateChanged?.Invoke();
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 1.6));
        }
    }

    private void HandleLine(string line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch { return; }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("heos", out var header)) return;

            var command = header.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            var message = header.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var payload = document.RootElement.TryGetProperty("payload", out var p) ? p : default;

            switch (command)
            {
                case "player/get_players":
                    HandlePlayers(payload);
                    break;

                case "player/get_now_playing_media":
                    HandleNowPlaying(payload);
                    break;

                case "player/get_play_state":
                case "event/player_state_changed":
                    var state = Query(message, "state");
                    if (state is not null && MatchesPlayer(message))
                    {
                        State.PlayState = state;
                        StateChanged?.Invoke();
                    }
                    break;

                case "event/player_now_playing_changed":
                case "event/player_now_playing_progress":
                    if (MatchesPlayer(message))
                        Command($"player/get_now_playing_media?pid={_pid}");
                    break;
            }
        }
    }

    private void HandlePlayers(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array) return;

        int? chosen = null;

        foreach (var player in payload.EnumerateArray())
        {
            if (!player.TryGetProperty("pid", out var pidElement)) continue;

            var pid = pidElement.ValueKind == JsonValueKind.Number
                ? pidElement.GetInt32()
                : int.TryParse(pidElement.GetString(), out var parsed) ? parsed : (int?)null;
            if (pid is null) continue;

            chosen ??= pid;

            // Prefer the player that is this receiver rather than another HEOS speaker.
            var ip = player.TryGetProperty("ip", out var ipElement) ? ipElement.GetString() : null;
            if (string.Equals(ip, host, StringComparison.OrdinalIgnoreCase))
            {
                chosen = pid;
                break;
            }
        }

        if (chosen is null) return;

        _pid = chosen;
        Command($"player/get_now_playing_media?pid={_pid}");
        Command($"player/get_play_state?pid={_pid}");
    }

    private void HandleNowPlaying(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;

        string? Field(string name) =>
            payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        State.Song = Field("song");
        State.Artist = Field("artist");
        State.Album = Field("album");
        State.Station = Field("station");
        State.ImageUrl = Field("image_url");
        StateChanged?.Invoke();
    }

    private bool MatchesPlayer(string message)
    {
        var pid = Query(message, "pid");
        return _pid is null || pid is null || pid == _pid.Value.ToString();
    }

    private static string? Query(string message, string key)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var parsed = HttpUtility.ParseQueryString(message);
        return parsed[key];
    }
}
