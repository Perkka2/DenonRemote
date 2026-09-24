using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
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

    /// <summary>Every player the connected device can see, replaced whole on each refresh.</summary>
    public IReadOnlyList<HeosPlayer> Players { get; set; } = [];

    /// <summary>The player being controlled. Defaults to the device itself; the person can change it.</summary>
    public int? PlayerId { get; set; }
    public string? PlayerName { get; set; }

    /// <summary>True when the controlled player is the device this client connected to.</summary>
    public bool SelectedIsHost { get; set; }

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

    /// <summary>The HEOS account the device is signed in to; null when unknown.</summary>
    public bool? SignedIn { get; set; }
    public string? Account { get; set; }

    /// <summary>The last thing the device refused or failed to play, in its own words.</summary>
    public string? Error { get; set; }

    public bool HasTrack => !string.IsNullOrWhiteSpace(Song) || !string.IsNullOrWhiteSpace(Station);
}

/// <summary>
/// HEOS command line on TCP 1255 - the newer receivers, and HEOS speakers on their own
/// (the X4100W predates it). Gives what the control protocol cannot: what is playing,
/// transport, play mode, the queue, browsing and search, and every other HEOS player
/// on the network, which it can group and control.
/// It also tracks player volume, but the UI only offers it where it does not clash: a
/// receiver's own player volume is the main zone's on a different scale from the
/// control socket's, so it is offered for a speaker or another room, not for that.
/// Like the control socket it stays open and registers for change events, so the
/// track updates on its own rather than being polled.
/// </summary>
public sealed class HeosClient(string host, ILogger<HeosClient> log) : IAsyncDisposable
{
    private const int Port = 1255;

    /// <summary>How much of the queue to fetch. HEOS pages it; one page is plenty for a remote.</summary>
    public const int QueuePage = 50;

    public const int SpotifySourceId = 4;
    public const int FavoritesSourceId = 1028;

    /// <summary>What add_to_queue's aid means.</summary>
    public const int PlayNow = 1, PlayNext = 2, AddToEnd = 3, ReplaceAndPlay = 4;

    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HeosReply>> _pending = new();
    private int _sequence;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private StreamWriter? _writer;
    private int? _pid;
    private int? _wantedPid;

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

    public void SetVolume(int level) => SetVolume(_pid, level);

    /// <summary>Sets one player's volume - the selected one, or another room's.</summary>
    public void SetVolume(int? pid, int level)
    {
        if (pid is null) return;
        level = Math.Clamp(level, 0, 100);
        Levels(pid.Value, level, null);
        Command($"player/set_volume?pid={pid}&level={level}");
    }

    /// <summary>Relative steps; the player answers with a volume-changed event.</summary>
    public void VolumeUp(int step = 2) => Command($"player/volume_up?pid={_pid}&step={Math.Clamp(step, 1, 10)}");
    public void VolumeDown(int step = 2) => Command($"player/volume_down?pid={_pid}&step={Math.Clamp(step, 1, 10)}");

    public void ToggleMute() => ToggleMute(_pid);

    public void ToggleMute(int? pid)
    {
        if (pid is null) return;
        var current = pid == _pid
            ? State.Muted
            : State.Players.FirstOrDefault(p => p.Pid == pid)?.Muted ?? false;
        var muted = !current;
        Levels(pid.Value, null, muted);
        Command($"player/set_mute?pid={pid}&state={(muted ? "on" : "off")}");
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

    // ---------------------------------------------------------------- players and groups

    /// <summary>Controls another player on the network through this connection.</summary>
    public void SelectPlayer(int pid)
    {
        if (_pid == pid || _writer is null) return;
        var player = State.Players.FirstOrDefault(p => p.Pid == pid);
        if (player is null) return;

        _pid = pid;
        _wantedPid = pid;
        State.PlayerId = pid;
        State.PlayerName = player.Name;
        State.SelectedIsHost = player.IsHost;
        ResetPlayerState();
        StateChanged?.Invoke();
        LoadPlayer();
    }

    /// <summary>First pid is the leader. A lone pid ungroups.</summary>
    public void SetGroup(IEnumerable<int> pids) =>
        Command($"group/set_group?pid={string.Join(",", pids)}");

    /// <summary>The members of the group the selected player is in, leader first.</summary>
    public IReadOnlyList<HeosPlayer> GroupOfSelected()
    {
        var players = State.Players;
        var selected = players.FirstOrDefault(p => p.Pid == _pid);
        if (selected is null) return [];

        var leader = selected.Gid ?? selected.Pid;
        var members = players.Where(p => p.Gid == leader && p.Pid != leader).ToList();
        var head = players.FirstOrDefault(p => p.Pid == leader) ?? selected;
        return [head, .. members];
    }

    public void ClearError()
    {
        State.Error = null;
        StateChanged?.Invoke();
    }

    // ---------------------------------------------------------------- account

    public void CheckAccount() => Command("system/check_account");

    /// <summary>The reply arrives as a change to <see cref="HeosState.SignedIn"/> or <see cref="HeosState.Error"/>.</summary>
    public void SignIn(string user, string password) =>
        Command($"system/sign_in?un={Encode(user)}&pw={Encode(password)}");

    public void SignOut() => Command("system/sign_out");

    // ---------------------------------------------------------------- browse

    public async Task<IReadOnlyList<HeosSource>> GetSourcesAsync(CancellationToken ct = default)
    {
        var reply = await AskAsync("browse/get_music_sources", ct);
        var sources = new List<HeosSource>();
        if (reply.Payload.ValueKind != JsonValueKind.Array) return sources;

        foreach (var entry in reply.Payload.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Integer(entry, "sid") is not { } sid) continue;
            sources.Add(new HeosSource(
                sid,
                Text(entry, "name") ?? $"Source {sid}",
                Text(entry, "type") ?? "",
                Text(entry, "image_url"),
                Raw(entry, "available") != "false"));
        }

        return sources;
    }

    /// <summary>The top of a source when cid is null, otherwise the inside of that container.</summary>
    public async Task<HeosPage> BrowseAsync(int sid, string? cid, int start, int count, CancellationToken ct = default)
    {
        var command = $"browse/browse?sid={sid}";

        // Only favorites pages at the top level; everything deeper does.
        if (cid is not null) command += $"&cid={cid}";
        if (cid is not null || sid == FavoritesSourceId) command += $"&range={start},{start + count - 1}";

        return ParsePage(await AskAsync(command, ct));
    }

    public async Task<IReadOnlyList<HeosSearchCriterion>> GetSearchCriteriaAsync(int sid, CancellationToken ct = default)
    {
        var reply = await AskAsync($"browse/get_search_criteria?sid={sid}", ct);
        var criteria = new List<HeosSearchCriterion>();
        if (reply.Payload.ValueKind != JsonValueKind.Array) return criteria;

        foreach (var entry in reply.Payload.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Raw(entry, "scid") is not { } scid) continue;
            criteria.Add(new HeosSearchCriterion(Text(entry, "name") ?? scid, scid, Raw(entry, "wildcard") == "yes"));
        }

        return criteria;
    }

    public async Task<HeosPage> SearchAsync(int sid, string text, string scid, int start, int count, CancellationToken ct = default)
    {
        // The search string is limited to 128 characters.
        if (text.Length > 128) text = text[..128];

        var command = $"browse/search?sid={sid}&search={Encode(text)}&scid={scid}&range={start},{start + count - 1}";
        return ParsePage(await AskAsync(command, ct));
    }

    /// <summary>Plays a station or input. Null on success, otherwise what the device said.</summary>
    public Task<string?> PlayStationAsync(int sid, string? cid, string mid, string name, CancellationToken ct = default)
    {
        if (_pid is null) return Task.FromResult<string?>("No HEOS player to play on.");

        var command = $"browse/play_stream?pid={_pid}&sid={sid}";
        if (!string.IsNullOrEmpty(cid)) command += $"&cid={cid}";
        command += $"&mid={mid}&name={Encode(name)}";
        return SucceededAsync(command, ct);
    }

    /// <summary>
    /// Adds a track (mid given) or a whole container (mid null) to the queue. aid is one of
    /// <see cref="PlayNow"/>, <see cref="PlayNext"/>, <see cref="AddToEnd"/>, <see cref="ReplaceAndPlay"/>.
    /// </summary>
    public Task<string?> AddToQueueAsync(int sid, string? cid, string? mid, int aid, CancellationToken ct = default)
    {
        if (_pid is null) return Task.FromResult<string?>("No HEOS player to play on.");

        var command = $"browse/add_to_queue?pid={_pid}&sid={sid}";
        if (!string.IsNullOrEmpty(cid)) command += $"&cid={cid}";
        if (!string.IsNullOrEmpty(mid)) command += $"&mid={mid}";
        command += $"&aid={aid}";
        return SucceededAsync(command, ct);
    }

    private async Task<string?> SucceededAsync(string command, CancellationToken ct)
    {
        var reply = await RequestAsync(command, ct);
        return reply.Success ? null : reply.ErrorText;
    }

    private async Task<HeosReply> AskAsync(string command, CancellationToken ct)
    {
        var reply = await RequestAsync(command, ct);
        if (!reply.Success) throw new HeosException(reply.ErrorText);
        return reply;
    }

    /// <summary>
    /// Sends a browse-family command and waits for its own answer. The device echoes every
    /// argument back, so a SEQUENCE number pairs the two; the interim "command under
    /// process" reply, which has no arguments, is ignored.
    /// </summary>
    private async Task<HeosReply> RequestAsync(string command, CancellationToken ct)
    {
        if (_writer is null) return HeosReply.Failed("HEOS is not connected.");

        var sequence = Interlocked.Increment(ref _sequence);
        var waiting = new TaskCompletionSource<HeosReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sequence] = waiting;

        try
        {
            Command($"{command}{(command.Contains('?') ? "&" : "?")}SEQUENCE={sequence}");

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(25));
            using var registration = limit.Token.Register(() =>
                waiting.TrySetResult(HeosReply.Failed("HEOS did not answer in time.")));

            return await waiting.Task;
        }
        finally
        {
            _pending.TryRemove(sequence, out _);
        }
    }

    private static HeosPage ParsePage(HeosReply reply)
    {
        var items = new List<HeosItem>();
        if (reply.Payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in reply.Payload.EnumerateArray())
            {
                if (ParseItem(entry) is { } item) items.Add(item);
            }
        }

        var returned = Number(reply.Message, "returned") is { } r ? (int)r : items.Count;
        var count = Number(reply.Message, "count") is { } c ? (int)c : 0;
        return new HeosPage(items, returned, count);
    }

    private static HeosItem? ParseItem(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || Text(entry, "name") is not { } name) return null;

        return new HeosItem(
            name,
            Text(entry, "type") ?? "",
            Text(entry, "image_url"),
            Raw(entry, "cid"),
            Raw(entry, "mid"),
            Integer(entry, "sid"),
            Raw(entry, "container") == "yes",
            Raw(entry, "playable") == "yes",
            Text(entry, "artist"),
            Text(entry, "album"));
    }

    // ---------------------------------------------------------------- wire

    private void Command(string command)
    {
        try
        {
            // The socket is shared by every browser tab and the reader's own follow-ups.
            lock (_writeLock)
            {
                var writer = _writer;
                if (writer is null) return;
                writer.Write("heos://" + command + "\r\n");
                writer.Flush();
            }
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

                // Come back to the same player after a reconnect, rather than the default.
                _wantedPid = _pid ?? _wantedPid;
                _pid = null;

                State.Connected = true;
                backoff = TimeSpan.FromSeconds(3);
                StateChanged?.Invoke();
                log.LogInformation("HEOS connected on {Host}", host);

                Command("system/register_for_change_events?enable=on");
                Command("system/check_account");
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

                // Nothing waiting on a dead socket will ever be answered.
                foreach (var waiting in _pending.Values)
                    waiting.TrySetResult(HeosReply.Failed("HEOS connection lost."));

                StateChanged?.Invoke();
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 1.6));
        }
    }

    /// <summary>One line from the device, for the self-test to feed in without a socket.</summary>
    internal void Handle(string line) => HandleLine(line);

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
            var result = header.TryGetProperty("result", out var r) ? r.GetString() : null;
            var payload = document.RootElement.TryGetProperty("payload", out var p) ? p : default;

            // The interim answer to a slow browse; the real one follows.
            if (message.Contains("command under process", StringComparison.OrdinalIgnoreCase)) return;

            // An answer somebody is waiting for.
            if (Query(message, "SEQUENCE") is { } sequenceText
                && int.TryParse(sequenceText, out var sequence)
                && _pending.TryGetValue(sequence, out var waiting))
            {
                waiting.TrySetResult(new HeosReply(
                    result == "success",
                    message,
                    payload.ValueKind == JsonValueKind.Undefined ? default : payload.Clone()));
                return;
            }

            // A command nobody waits on failed. Say so, except for reads that came back empty.
            if (result == "fail")
            {
                if (!command.Contains("/get_", StringComparison.Ordinal))
                {
                    State.Error = Query(message, "text") ?? "HEOS refused the command.";
                    StateChanged?.Invoke();
                }
                return;
            }

            switch (command)
            {
                case "player/get_players":
                    HandlePlayers(payload);
                    break;

                case "event/players_changed":
                case "event/groups_changed":
                    Command("player/get_players");
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

                case "event/player_playback_error":
                    if (MatchesPlayer(message))
                    {
                        State.Error = Query(message, "error") ?? "Playback failed.";
                        StateChanged?.Invoke();
                    }
                    break;

                case "player/get_volume":
                case "player/get_mute":
                case "event/player_volume_changed":
                    // Any room's, not only the selected one's: the rooms list shows them all.
                    ApplyLevels(message);
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
                    if (MatchesPlayer(message)) HandleQueue(payload);
                    break;

                case "event/player_queue_changed":
                    if (MatchesPlayer(message))
                        Command($"player/get_queue?pid={_pid}&range=0,{QueuePage - 1}");
                    break;

                case "system/check_account":
                case "system/sign_in":
                case "system/sign_out":
                case "event/user_changed":
                    if (message.StartsWith("signed_in", StringComparison.Ordinal))
                    {
                        State.SignedIn = true;
                        State.Account = Query(message, "un");
                        State.Error = null;
                        StateChanged?.Invoke();
                    }
                    else if (message.StartsWith("signed_out", StringComparison.Ordinal))
                    {
                        State.SignedIn = false;
                        State.Account = null;
                        StateChanged?.Invoke();
                    }
                    break;
            }
        }
    }

    private void HandlePlayers(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array) return;

        var previous = State.Players;
        var players = new List<HeosPlayer>();
        var total = payload.GetArrayLength();

        foreach (var entry in payload.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Integer(entry, "pid") is not { } pid) continue;

            var before = previous.FirstOrDefault(known => known.Pid == pid);
            players.Add(new HeosPlayer
            {
                Pid = pid,
                Name = Text(entry, "name") ?? $"Player {pid}",
                Model = Text(entry, "model"),
                Gid = Integer(entry, "gid"),
                Ip = Raw(entry, "ip"),

                // This device answers HEOS as the player whose address it is; a lone player is it too.
                IsHost = total == 1 || string.Equals(Raw(entry, "ip"), host, StringComparison.OrdinalIgnoreCase),
                Volume = before?.Volume,
                Muted = before?.Muted ?? false,
            });
        }

        if (players.Count == 0) return;
        State.Players = players;

        // Keep the player the person picked; failing that this device's own, else the first.
        var wanted = _pid ?? _wantedPid;
        var pick =
            players.FirstOrDefault(known => known.Pid == wanted)
            ?? players.FirstOrDefault(known => known.IsHost)
            ?? players[0];

        State.SelectedIsHost = pick.IsHost;
        State.PlayerName = pick.Name;

        if (_pid == pick.Pid)
        {
            // Same player; only the list, and so the groups, changed.
            FetchMissingLevels(players);
            StateChanged?.Invoke();
            return;
        }

        var switching = _pid is not null;
        _pid = pick.Pid;
        State.PlayerId = pick.Pid;
        if (switching) ResetPlayerState();

        LoadPlayer();
        FetchMissingLevels(players);
        StateChanged?.Invoke();
    }

    /// <summary>Everything about the selected player, fetched fresh.</summary>
    private void LoadPlayer()
    {
        Command($"player/get_now_playing_media?pid={_pid}");
        Command($"player/get_play_state?pid={_pid}");
        Command($"player/get_volume?pid={_pid}");
        Command($"player/get_mute?pid={_pid}");
        Command($"player/get_play_mode?pid={_pid}");
        Command($"player/get_queue?pid={_pid}&range=0,{QueuePage - 1}");
    }

    private void FetchMissingLevels(IEnumerable<HeosPlayer> players)
    {
        foreach (var player in players)
        {
            if (player.Pid == _pid || player.Volume is not null) continue;
            Command($"player/get_volume?pid={player.Pid}");
            Command($"player/get_mute?pid={player.Pid}");
        }
    }

    private void ResetPlayerState()
    {
        State.Song = null;
        State.Artist = null;
        State.Album = null;
        State.Station = null;
        State.ImageUrl = null;
        State.PlayState = null;
        State.Volume = null;
        State.Muted = false;
        State.Repeat = "off";
        State.Shuffle = false;
        State.PositionMs = null;
        State.DurationMs = null;
        State.SourceId = null;
        State.QueueId = null;
        State.Queue = [];
    }

    private void ApplyLevels(string message)
    {
        if (Number(message, "pid") is not { } pid) return;

        // get_volume and the event say level; get_mute says state and the event says mute.
        var level = Number(message, "level");
        var mute = Query(message, "mute") ?? Query(message, "state");

        int? newLevel = level is { } l ? (int)l : null;
        bool? newMute = mute is null ? null : mute == "on";
        Levels((int)pid, newLevel, newMute);
    }

    private void Levels(int pid, int? level, bool? muted)
    {
        var player = State.Players.FirstOrDefault(known => known.Pid == pid);

        if (level is { } newLevel)
        {
            if (player is not null) player.Volume = newLevel;
            if (pid == _pid) State.Volume = newLevel;
        }

        if (muted is { } newMuted)
        {
            if (player is not null) player.Muted = newMuted;
            if (pid == _pid) State.Muted = newMuted;
        }

        StateChanged?.Invoke();
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

        State.Song = Text(payload, "song");
        State.Artist = Text(payload, "artist");
        State.Album = Text(payload, "album");
        State.Station = Text(payload, "station");
        State.ImageUrl = Text(payload, "image_url");
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

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// The device encodes &amp;, = and % in every string it sends, and expects the same
    /// back. Ids taken from a reply go back untouched; text shown to a person is decoded,
    /// and text a person typed is encoded.
    /// </summary>
    public static string Encode(string value) =>
        value.Replace("%", "%25").Replace("&", "%26").Replace("=", "%3D");

    public static string Decode(string value) =>
        value.Replace("%26", "&").Replace("%3D", "=").Replace("%25", "%");

    /// <summary>A string or number field as it came, still encoded.</summary>
    private static string? Raw(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>A string field decoded for display.</summary>
    private static string? Text(JsonElement element, string name) =>
        Raw(element, name) is { } raw ? Decode(raw) : null;

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
