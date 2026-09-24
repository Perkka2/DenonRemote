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

    /// <summary>Player volume, 0-100. On a receiver this is the main zone's, on another scale.</summary>
    public int? Volume { get; set; }
    public bool Muted { get; set; }

    /// <summary>off / on_all / on_one.</summary>
    public string Repeat { get; set; } = "off";
    public bool Shuffle { get; set; }

    /// <summary>Milliseconds. Only some sources report progress.</summary>
    public long? PositionMs { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>HEOS source id of what is playing (4 is Spotify).</summary>
    public int? SourceId { get; set; }

    /// <summary>
    /// Spotify Connect and the like are played by the phone's app, which owns the queue;
    /// HEOS lists something but asking it to play an entry drops the session.
    /// </summary>
    public bool QueueUsable => SourceId is { } sid && sid != HeosClient.SpotifySourceId;

    /// <summary>The queue entry that is playing now, when the source has a queue.</summary>
    public int? QueueId { get; set; }

    /// <summary>The first <see cref="HeosClient.QueuePage"/> entries. Replaced whole, never edited.</summary>
    public IReadOnlyList<HeosQueueItem> Queue { get; set; } = [];

    public bool HasTrack => !string.IsNullOrWhiteSpace(Song) || !string.IsNullOrWhiteSpace(Station);
}

public sealed record HeosQueueItem(int Qid, string? Song, string? Artist, string? Album, string? ImageUrl);

/// <summary>
/// HEOS command line on TCP 1255 - the newer receivers only (the X4100W predates it).
/// Used for what the control protocol cannot give us: what is playing, transport,
/// play mode and the queue. It also tracks the player volume, but the UI only offers
/// it on a HEOS-only speaker: on a receiver that volume is the main zone's on a
/// different scale from the control socket's, and the two controls fight.
/// Like the control socket it stays open and registers for change events, so the
/// track updates on its own rather than being polled.
/// </summary>
public sealed class HeosClient(string host, ILogger<HeosClient> log) : IAsyncDisposable
{
    private const int Port = 1255;

    /// <summary>How much of the queue to fetch. HEOS pages it; one page is plenty for a remote.</summary>
    public const int QueuePage = 50;

    public const int SpotifySourceId = 4;

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

    // ---------------------------------------------------------------- volume, mode, queue

    public void SetVolume(int level)
    {
        if (_pid is null) return;
        level = Math.Clamp(level, 0, 100);
        State.Volume = level;
        StateChanged?.Invoke();
        Command($"player/set_volume?pid={_pid}&level={level}");
    }

    /// <summary>Relative steps; the player answers with a volume-changed event.</summary>
    public void VolumeUp(int step = 2) => Command($"player/volume_up?pid={_pid}&step={Math.Clamp(step, 1, 10)}");
    public void VolumeDown(int step = 2) => Command($"player/volume_down?pid={_pid}&step={Math.Clamp(step, 1, 10)}");

    public void ToggleMute()
    {
        if (_pid is null) return;
        State.Muted = !State.Muted;
        StateChanged?.Invoke();
        Command($"player/set_mute?pid={_pid}&state={(State.Muted ? "on" : "off")}");
    }

    /// <summary>off, then all, then one, then back to off.</summary>
    public void CycleRepeat()
    {
        if (_pid is null) return;
        var next = State.Repeat switch { "off" => "on_all", "on_all" => "on_one", _ => "off" };
        State.Repeat = next;
        StateChanged?.Invoke();
        Command($"player/set_play_mode?pid={_pid}&repeat={next}");
    }

    public void ToggleShuffle()
    {
        if (_pid is null) return;
        State.Shuffle = !State.Shuffle;
        StateChanged?.Invoke();
        Command($"player/set_play_mode?pid={_pid}&shuffle={(State.Shuffle ? "on" : "off")}");
    }

    public void PlayQueueItem(int qid)
    {
        if (_pid is null || !State.QueueUsable) return;
        Command($"player/play_queue?pid={_pid}&qid={qid}");
    }

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
                State.PositionMs = null;
                State.DurationMs = null;
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
                    if (MatchesPlayer(message))
                    {
                        State.PositionMs = null;
                        State.DurationMs = null;
                        Command($"player/get_now_playing_media?pid={_pid}");
                    }
                    break;

                case "event/player_now_playing_progress":
                    // Carries the position itself, so there is nothing to re-fetch.
                    if (MatchesPlayer(message))
                    {
                        State.PositionMs = Number(message, "cur_pos");
                        State.DurationMs = Number(message, "duration");
                        StateChanged?.Invoke();
                    }
                    break;

                case "player/get_volume":
                    if (MatchesPlayer(message) && Number(message, "level") is { } level)
                    {
                        State.Volume = (int)level;
                        StateChanged?.Invoke();
                    }
                    break;

                case "event/player_volume_changed":
                    if (MatchesPlayer(message))
                    {
                        if (Number(message, "level") is { } changed) State.Volume = (int)changed;
                        if (Query(message, "mute") is { } muteState) State.Muted = muteState == "on";
                        StateChanged?.Invoke();
                    }
                    break;

                case "player/get_mute":
                    if (MatchesPlayer(message) && Query(message, "state") is { } mute)
                    {
                        State.Muted = mute == "on";
                        StateChanged?.Invoke();
                    }
                    break;

                case "player/get_play_mode":
                case "event/repeat_mode_changed":
                case "event/shuffle_mode_changed":
                    // The get and the two events differ only in which keys they carry.
                    if (MatchesPlayer(message))
                    {
                        if (Query(message, "repeat") is { } repeat) State.Repeat = repeat;
                        if (Query(message, "shuffle") is { } shuffle) State.Shuffle = shuffle == "on";
                        StateChanged?.Invoke();
                    }
                    break;

                case "player/get_queue":
                    HandleQueue(payload);
                    break;

                case "event/player_queue_changed":
                    if (MatchesPlayer(message))
                        Command($"player/get_queue?pid={_pid}&range=0,{QueuePage - 1}");
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
        Command($"player/get_volume?pid={_pid}");
        Command($"player/get_mute?pid={_pid}");
        Command($"player/get_play_mode?pid={_pid}");
        Command($"player/get_queue?pid={_pid}&range=0,{QueuePage - 1}");
    }

    private void HandleQueue(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array) return;

        var items = new List<HeosQueueItem>();
        foreach (var entry in payload.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Integer(entry, "qid") is not { } qid) continue;
            items.Add(new HeosQueueItem(
                qid,
                Text(entry, "song"),
                Text(entry, "artist"),
                Text(entry, "album"),
                Text(entry, "image_url")));
        }

        State.Queue = items;
        StateChanged?.Invoke();
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
        State.QueueId = Integer(payload, "qid");
        State.SourceId = Integer(payload, "sid");
        log.LogDebug("HEOS now playing: sid {Sid}, qid {Qid}", State.SourceId, State.QueueId);
        StateChanged?.Invoke();
    }

    private bool MatchesPlayer(string message)
    {
        var pid = Query(message, "pid");
        return _pid is null || pid is null || pid == _pid.Value.ToString();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>HEOS sends numbers as numbers in payloads but sometimes as strings.</summary>
    private static int? Integer(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static long? Number(string message, string key) =>
        long.TryParse(Query(message, key), out var value) ? value : null;

    private static string? Query(string message, string key)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var parsed = HttpUtility.ParseQueryString(message);
        return parsed[key];
    }
}
