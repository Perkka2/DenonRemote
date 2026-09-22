using System.Net.Sockets;
using System.Text;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// One persistent connection to one receiver.
///
/// The receiver's own web UI is slow because every action is an HTTP round trip that
/// re-reads the whole status document. Here we hold the control-protocol socket open
/// (TCP 23), so commands are a few bytes and the receiver pushes unsolicited state
/// changes back to us the moment anything changes - including changes made with the
/// physical remote. HTTP /goform is only a fallback for when the socket is down.
/// </summary>
public sealed class DenonClient : IAsyncDisposable
{
    private const int TelnetPort = 23;

    /// <summary>The protocol wants a small gap between commands or it drops them.</summary>
    private static readonly TimeSpan SendGap = TimeSpan.FromMilliseconds(55);

    /// <summary>Prefix for a queue entry that waits instead of sending.</summary>
    private const string DelayToken = "@DELAY:";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly ILogger<DenonClient> _log;
    private readonly DeviceProfileReader _profiles;
    private readonly GraphicEqClient _eq;
    private readonly AjaxConfigClient _ajax;
    private readonly ReceiverInfoReader _info;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private readonly List<PendingCommand> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);

    private readonly LinkedList<LogEntry> _traffic = new();
    private readonly Dictionary<string, PendingChange> _pending = new();
    private readonly Dictionary<string, CommandStatus> _statuses = new();
    private Timer? _watchdog;

    private CancellationTokenSource? _cts;
    private Task? _connectionLoop;
    private Task? _senderLoop;
    private NetworkStream? _stream;
    private int _profileLoading;

    public DenonClient(
        ReceiverConfig config,
        DeviceProfileReader profiles,
        GraphicEqClient eq,
        AjaxConfigClient ajax,
        ReceiverInfoReader info,
        ILoggerFactory loggerFactory)
    {
        Config = config;
        _profiles = profiles;
        _eq = eq;
        _ajax = ajax;
        _info = info;
        NightMode = config.Settings.NightMode;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<DenonClient>();
        Profile = DeviceProfile.Fallback(config.Model);
    }

    public ReceiverConfig Config { get; }
    public ReceiverState State { get; } = new();
    public DeviceProfile Profile { get; private set; }
    public HeosClient? Heos { get; private set; }

    /// <summary>
    /// The pre-HEOS network player. Present only where HEOS is not: those units have
    /// no other way to say what they are playing.
    /// </summary>
    public NetAudioClient? NetAudio { get; private set; }

    /// <summary>
    /// The graphic EQ, which lives outside the control protocol entirely - see
    /// <see cref="GraphicEqClient"/>. Not every model exposes one.
    /// </summary>
    public GraphicEqState Eq { get; } = new();

    /// <summary>What the receiver says it is receiving, from the setup API.</summary>
    public ReceiverInfo Info { get; } = new();

    /// <summary>Night mode caps the volume and leans on Dynamic Volume.</summary>
    public bool NightMode { get; private set; }

    /// <summary>The receiver answered HTTP but not the control socket - eco standby.</summary>
    public bool Asleep { get; private set; }

    /// <summary>Raised on any state change. Fired from background threads.</summary>
    public event Action<DenonClient>? StateChanged;

    /// <summary>Raised for every line sent or received, for the protocol console.</summary>
    public event Action? LogAppended;

    /// <summary>Raised when something worth persisting to receivers.json changes.</summary>
    public event Action? SettingsChanged;

    /// <summary>The last few hundred lines of traffic, oldest first.</summary>
    public IReadOnlyList<LogEntry> Log
    {
        get { lock (_traffic) return _traffic.ToList(); }
    }

    public void ClearLog()
    {
        lock (_traffic) _traffic.Clear();
        LogAppended?.Invoke();
    }

    private void Record(bool outgoing, string text)
    {
        lock (_traffic)
        {
            _traffic.AddLast(new LogEntry(DateTimeOffset.Now, outgoing, text));
            while (_traffic.Count > 400) _traffic.RemoveFirst();
        }
        LogAppended?.Invoke();
    }

    private sealed record PendingCommand(string Text, string? CoalesceKey);

    private sealed record PendingChange(
        string Key, string Label, string Command, string? Expected, DateTimeOffset Sent, Action? Revert);

    // ---------------------------------------------------------------- lifecycle

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _connectionLoop = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        _senderLoop = Task.Run(() => SenderLoopAsync(_cts.Token));
        _ = Task.Run(() => LoadProfileAsync(_cts.Token));
        _watchdog = new Timer(_ => SweepPending(), null,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();

        if (_watchdog is not null) await _watchdog.DisposeAsync();
        if (Heos is not null) await Heos.DisposeAsync();

        try
        {
            await Task.WhenAll(_connectionLoop ?? Task.CompletedTask, _senderLoop ?? Task.CompletedTask)
                      .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* shutting down */ }

        _cts.Dispose();
        _cts = null;
    }

    private async Task LoadProfileAsync(CancellationToken ct)
    {
        // Start() and the connection loop both ask; only the first one does the work.
        if (Interlocked.CompareExchange(ref _profileLoading, 1, 0) != 0) return;

        try
        {
            Profile = await _profiles.ReadAsync(Config.Host, Config.Model, ct);

            if (string.IsNullOrWhiteSpace(Config.Model) && !string.IsNullOrWhiteSpace(Profile.Model))
                Config.Model = Profile.Model;

            Eq.Backend = await _eq.DetectAsync(Config.Host, ct);
            if (Eq.Available) await _eq.ReadAsync(Config.Host, Eq, ct);

            if (Profile.SetupApi) await _info.ReadAsync(Config.Host, Info, ct);

            if (Profile.Heos && Heos is null)
            {
                Heos = new HeosClient(Config.Host, _loggerFactory.CreateLogger<HeosClient>());
                Heos.StateChanged += () => Touch();
                Heos.Start();
            }

            // Only where HEOS isn't: on a HEOS unit the CLI is the better source, and
            // asking both would be two pollers describing the same thing.
            if (!Profile.Heos && NetAudio is null)
            {
                var player = new NetAudioClient(_loggerFactory.CreateLogger<NetAudioClient>());
                if (await player.ProbeAsync(Config.Host, ct))
                {
                    player.Changed += Touch;
                    NetAudio = player;
                    Profile.NetAudio = true;
                    await player.RefreshAsync(Config.Host, ct);
                }
            }

            Touch();
        }
        catch (Exception ex)
        {
            // Let a later reconnect try again.
            Interlocked.Exchange(ref _profileLoading, 0);
            _log.LogDebug("Profile read for {Host} failed: {Message}", Config.Host, ex.Message);
        }
    }

    // ---------------------------------------------------------------- zones

    /// <summary>Zones worth showing: what the unit reported, plus any zone that has answered.</summary>
    public IReadOnlyList<int> Zones
    {
        get
        {
            var count = Math.Clamp(Profile.ZoneCount, 1, 3);
            if (State.Zone3Seen) count = 3;
            return Enumerable.Range(1, count).ToList();
        }
    }

    public IReadOnlyList<SourceOption> SourcesFor(int zone) =>
        zone == 1 ? Profile.Sources : [DenonCatalog.FollowMain, .. Profile.Sources];

    private static string ZonePrefix(int zone) => zone switch { 2 => "Z2", 3 => "Z3", _ => "" };

    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Queue a raw protocol command. <paramref name="coalesceKey"/> replaces any
    /// queued-but-unsent command with the same key, so dragging the volume slider
    /// sends the latest position instead of a backlog of stale ones.
    /// </summary>
    public void Send(string command, string? coalesceKey = null)
    {
        lock (_gate)
        {
            if (coalesceKey is not null)
                _queue.RemoveAll(c => c.CoalesceKey == coalesceKey);
            _queue.Add(new PendingCommand(command, coalesceKey));
        }
        _signal.Release();
    }

    public void Refresh() => QueryAll();

    /// <summary>Queued but unsent commands. Internal so the self-test can read them
    /// without a receiver on the other end.</summary>
    internal IReadOnlyList<string> Pending
    {
        get { lock (_gate) return _queue.Select(c => c.Text).ToList(); }
    }

    internal void ClearPending()
    {
        lock (_gate) _queue.Clear();
    }

    // ---------------------------------------------------------------- verification

    /// <summary>
    /// The receiver answers a command it accepted and stays silent on one it doesn't
    /// support, so every setting we change is followed by a query and checked against
    /// what comes back. Anything unanswered is reverted rather than left looking applied.
    /// </summary>
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>Latest outcome per setting.</summary>
    public IReadOnlyList<CommandStatus> Statuses
    {
        get { lock (_gate) return _statuses.Values.OrderByDescending(s => s.At).ToList(); }
    }

    /// <summary>The most recent command that did not take, if any.</summary>
    public CommandStatus? LastFailure
    {
        get
        {
            lock (_gate)
                return _statuses.Values
                    .Where(s => s.Failed)
                    .OrderByDescending(s => s.At)
                    .FirstOrDefault();
        }
    }

    public void ClearStatuses()
    {
        lock (_gate) _statuses.Clear();
        Touch();
    }

    /// <summary>
    /// Sends a command, then asks for the setting back. <paramref name="revert"/> undoes
    /// the optimistic UI change if nothing answers.
    /// </summary>
    private void SendVerified(
        string key, string label, string command, string? expected, string query, Action? revert = null)
    {
        lock (_gate)
        {
            _pending[key] = new PendingChange(key, label, command, expected, DateTimeOffset.Now, revert);
            _statuses[key] = new CommandStatus(key, label, command, expected, null,
                CommandOutcome.Pending, DateTimeOffset.Now);
        }

        Send(command, key);
        Send(query, key + "?");
    }

    /// <summary>Called for every value the receiver reports, to settle anything pending.</summary>
    private void Confirm(string key, string? actual)
    {
        PendingChange? change;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out change)) return;
            _pending.Remove(key);

            var outcome = Matches(change.Expected, actual) ? CommandOutcome.Confirmed : CommandOutcome.Mismatch;
            _statuses[key] = new CommandStatus(key, change.Label, change.Command, change.Expected,
                actual, outcome, DateTimeOffset.Now);
        }
    }

    private static bool Matches(string? expected, string? actual)
    {
        if (expected is null) return true;
        if (actual is null) return false;

        expected = expected.Trim();
        actual = actual.Trim();

        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return true;

        // Volumes and levels: "45" and "450" are the same reading.
        if (TryLevel(expected, out var a) && TryLevel(actual, out var b) && Math.Abs(a - b) < 0.01) return true;

        // Sound modes come back more specific than asked: DOLBY DIGITAL -> DOLBY DIGITAL+.
        return actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private void SweepPending()
    {
        List<PendingChange> expired;
        lock (_gate)
        {
            var cutoff = DateTimeOffset.Now - VerifyTimeout;
            expired = _pending.Values.Where(p => p.Sent < cutoff).ToList();
            foreach (var change in expired)
            {
                _pending.Remove(change.Key);
                _statuses[change.Key] = new CommandStatus(change.Key, change.Label, change.Command,
                    change.Expected, null, CommandOutcome.NoResponse, DateTimeOffset.Now);
            }
        }

        if (expired.Count == 0) return;

        // Put the UI back to what the receiver actually reports.
        foreach (var change in expired) change.Revert?.Invoke();
        Touch();
    }


    // Power ------------------------------------------------------------

    public void SetStandby(bool on)
    {
        var previous = State.Power;
        State.Power = on;
        if (!on) State.Main.Power = false;
        Touch();
        SendVerified("PW", "Power", on ? "PWON" : "PWSTANDBY", on ? "ON" : "STANDBY", "PW?",
            () => State.Power = previous);
    }

    /// <summary>
    /// In eco standby the unit stops answering the control socket but still answers
    /// HTTP, so this pokes it awake the only way that works.
    /// </summary>
    public async Task WakeAsync()
    {
        try
        {
            await Http.GetAsync($"http://{Config.Host}/goform/formiPhoneAppDirect.xml?PWON");
        }
        catch (Exception ex)
        {
            _log.LogDebug("HTTP wake failed for {Host}: {Message}", Config.Host, ex.Message);
        }
    }

    // Zone -------------------------------------------------------------

    public void SetZonePower(int zone, bool on)
    {
        var previous = State.Zone(zone).Power;
        State.Zone(zone).Power = on;
        if (on && zone == 1) State.Power = true;
        Touch();
        SendVerified($"Z{zone}PW", $"Zone {zone} power",
            zone == 1 ? (on ? "ZMON" : "ZMOFF") : $"{ZonePrefix(zone)}{(on ? "ON" : "OFF")}",
            on ? "ON" : "OFF",
            zone == 1 ? "ZM?" : $"{ZonePrefix(zone)}?",
            () => State.Zone(zone).Power = previous);
    }

    /// <summary>The ceiling actually applied to anything the app sends.</summary>
    public double EffectiveMax =>
        Math.Min(NightMode ? Config.Settings.NightMaxVolume : Config.Settings.MaxVolume, State.VolumeMax);

    public void SetZoneVolume(int zone, double value)
    {
        value = Math.Clamp(value, 0, EffectiveMax);
        var previous = State.Zone(zone).Volume;
        State.Zone(zone).Volume = value;
        Touch();
        SendVerified($"Z{zone}VOL", $"Zone {zone} volume",
            (zone == 1 ? "MV" : ZonePrefix(zone)) + EncodeVolume(value),
            EncodeVolume(value),
            zone == 1 ? "MV?" : $"{ZonePrefix(zone)}?",
            () => State.Zone(zone).Volume = previous);
    }

    public void StepZoneVolume(int zone, int direction)
    {
        var current = State.Zone(zone).Volume;

        // Stepping up into the cap would otherwise be a silent no-op at the receiver.
        if (direction > 0 && current is not null && current.Value >= EffectiveMax)
        {
            State.Zone(zone).Volume = EffectiveMax;
            Touch();
            return;
        }

        if (current is not null)
        {
            State.Zone(zone).Volume = Math.Clamp(current.Value + (direction > 0 ? 0.5 : -0.5), 0, EffectiveMax);
            Touch();
        }

        Send(zone == 1
            ? (direction > 0 ? "MVUP" : "MVDOWN")
            : $"{ZonePrefix(zone)}{(direction > 0 ? "UP" : "DOWN")}");
    }

    public void SetZoneMute(int zone, bool mute)
    {
        var previous = State.Zone(zone).Mute;
        State.Zone(zone).Mute = mute;
        Touch();
        SendVerified($"Z{zone}MU", $"Zone {zone} mute",
            zone == 1 ? (mute ? "MUON" : "MUOFF") : $"{ZonePrefix(zone)}MU{(mute ? "ON" : "OFF")}",
            mute ? "ON" : "OFF",
            zone == 1 ? "MU?" : $"{ZonePrefix(zone)}MU?",
            () => State.Zone(zone).Mute = previous);
    }

    public void SetZoneSource(int zone, string code)
    {
        var previous = State.Zone(zone).Source;
        State.Zone(zone).Source = code;
        Touch();
        SendVerified($"Z{zone}SI", $"Zone {zone} input",
            (zone == 1 ? "SI" : ZonePrefix(zone)) + code, code,
            zone == 1 ? "SI?" : $"{ZonePrefix(zone)}?",
            () => State.Zone(zone).Source = previous);
    }

    public void SetSoundMode(string code)
    {
        var previous = State.SoundMode;
        State.SoundMode = code;
        Touch();
        SendVerified("MS", "Sound mode", "MS" + code, code, "MS?",
            () => State.SoundMode = previous);
    }

    // Quick Select -----------------------------------------------------

    public void SelectQuick(int slot, int zone = 1)
    {
        var previous = State.QuickSelect;
        State.QuickSelect = slot;
        Touch();
        SendVerified("QUICK", "Quick Select",
            zone == 1 ? $"MSQUICK{slot}" : $"{ZonePrefix(zone)}QUICK{slot}",
            slot.ToString(), "MS?",
            () => State.QuickSelect = previous);
    }

    /// <summary>Stores the receiver's current settings into a Quick Select slot.</summary>
    public void SaveQuick(int slot, int zone = 1) =>
        Send(zone == 1 ? $"MSQUICK{slot} MEMORY" : $"{ZonePrefix(zone)}QUICK{slot} MEMORY");

    // Tone, trims and the Audyssey family --------------------------------

    public void SetToneControl(bool on)
    {
        var previous = State.Audio.ToneControl;
        State.Audio.ToneControl = on;
        Touch();
        SendVerified("PS:TONE CTRL", "Tone control", $"PSTONE CTRL {(on ? "ON" : "OFF")}",
            on ? "ON" : "OFF", "PSTONE CTRL ?",
            () => State.Audio.ToneControl = previous);
    }

    public void SetBass(double value) => SetLevel("PSBAS", value, v => State.Audio.Bass = v);
    public void SetTreble(double value) => SetLevel("PSTRE", value, v => State.Audio.Treble = v);
    public void SetSubwoofer(double value) => SetLevel("PSSWL", value, v => State.Audio.Subwoofer = v);
    public void SetDialog(double value) => SetLevel("PSDIL", value, v => State.Audio.Dialog = v);

    public void SetChannelLevel(string channel, double value)
    {
        value = Math.Clamp(value, 38, 62);
        State.Audio.SetChannel(channel, value);
        Touch();
        SendVerified($"CV:{channel}", DenonCatalog.LabelForChannel(channel),
            $"CV{channel} {EncodeLevel(value)}", EncodeLevel(value), "CV?");
    }

    private void SetLevel(string command, double value, Action<double> apply)
    {
        value = Math.Clamp(value, 38, 62);
        apply(value);
        Touch();

        var key = command[2..];
        SendVerified("PS:" + key, key, $"{command} {EncodeLevel(value)}",
            EncodeLevel(value), $"{command} ?");
    }

    public void SetDynamicEq(bool on)
    {
        var previous = State.Audio.DynamicEq;
        State.Audio.DynamicEq = on;
        Touch();
        SendVerified("PS:DYNEQ", "Dynamic EQ", $"PSDYNEQ {(on ? "ON" : "OFF")}",
            on ? "ON" : "OFF", "PSDYNEQ ?",
            () => State.Audio.DynamicEq = previous);
    }

    public void SetDynamicVolume(string code)
    {
        var previous = State.Audio.DynamicVolume;
        State.Audio.DynamicVolume = code;
        Touch();
        SendVerified("PS:DYNVOL", "Dynamic Volume", $"PSDYNVOL {code}", code, "PSDYNVOL ?",
            () => State.Audio.DynamicVolume = previous);
    }

    public void SetMultEq(string code)
    {
        var previous = State.Audio.MultEq;
        State.Audio.MultEq = code;
        Touch();
        SendVerified("PS:MULTEQ", "MultEQ", $"PSMULTEQ:{code}", code, "PSMULTEQ: ?",
            () => State.Audio.MultEq = previous);
    }

    public void SetRestorer(string code)
    {
        var previous = State.Audio.Restorer;
        State.Audio.Restorer = code;
        Touch();
        SendVerified("PS:RSTR", "Restorer", $"PSRSTR {code}", code, "PSRSTR ?",
            () => State.Audio.Restorer = previous);
    }

    // Surround parameters, system, video, tuner ---------------------------

    /// <summary>Raw value of a PS parameter, or null if the receiver hasn't reported it
    /// - which also means the setting doesn't apply in the current mode.</summary>
    public string? Parameter(string key) =>
        State.Audio.Parameters.TryGetValue(key, out var value) ? value : null;

    public void SetParameter(ParameterSpec spec, string value)
    {
        var previous = State.Audio.Parameters.TryGetValue(spec.Key, out var old) ? old : null;
        State.Audio.Parameters[spec.Key] = value;
        Touch();
        SendVerified("PS:" + spec.Key, spec.Label, spec.Command(value), value, spec.Query,
            () =>
            {
                if (previous is null) State.Audio.Parameters.Remove(spec.Key);
                else State.Audio.Parameters[spec.Key] = previous;
            });
    }

    public void SetParameterLevel(ParameterSpec spec, double value)
    {
        value = Math.Clamp(value, spec.Min, spec.Max);
        // Audio delay is sent as three digits, everything else as two.
        var text = spec.Max > 99 ? $"{(int)value:000}" : $"{(int)value:00}";
        SetParameter(spec, text);
    }

    public void SetEco(string mode)
    {
        var previous = State.System.Eco;
        State.System.Eco = mode;
        Touch();
        SendVerified("ECO", "ECO mode", "ECO" + mode, mode, "ECO?",
            () => State.System.Eco = previous);
    }

    public void SetDimmer(string level)
    {
        var previous = State.System.Dimmer;
        State.System.Dimmer = level;
        Touch();
        SendVerified("DIM", "Dimmer", "DIM " + level, level, "DIM ?",
            () => State.System.Dimmer = previous);
    }

    public void SetAutoStandby(string value)
    {
        var previous = State.System.AutoStandby;
        State.System.AutoStandby = value;
        Touch();
        SendVerified("STBY", "Auto standby", "STBY" + value, value, "STBY?",
            () => State.System.AutoStandby = previous);
    }

    public void SetMonitorOut(string value)
    {
        var previous = State.System.MonitorOut;
        State.System.MonitorOut = value;
        Touch();
        SendVerified("VSMONI", "Monitor out", "VSMONI" + value, value, "VSMONI ?",
            () => State.System.MonitorOut = previous);
    }

    public void SetHdmiAudio(string value)
    {
        var previous = State.System.HdmiAudio;
        State.System.HdmiAudio = value;
        Touch();
        SendVerified("VSAUDIO", "HDMI audio", "VSAUDIO " + value, value, "VSAUDIO ?",
            () => State.System.HdmiAudio = previous);
    }

    public void SetVideoSelect(string value)
    {
        var previous = State.System.VideoSelect;
        State.System.VideoSelect = value;
        Touch();
        SendVerified("SV", "Video select", "SV" + value, value, "SV?",
            () => State.System.VideoSelect = previous);
    }

    public void TunePreset(int preset)
    {
        var previous = State.System.TunerPreset;
        State.System.TunerPreset = preset;
        Touch();
        SendVerified("TPAN", "Tuner preset", $"TPAN{Math.Clamp(preset, 1, 56):00}",
            $"{Math.Clamp(preset, 1, 56):00}", "TPAN?",
            () => State.System.TunerPreset = previous);
    }

    public void StepPreset(int direction) => Send(direction > 0 ? "TPANUP" : "TPANDOWN");

    public void StepFrequency(int direction) => Send(direction > 0 ? "TFANUP" : "TFANDOWN");

    // Graphic EQ -----------------------------------------------------------

    public async Task RefreshEqAsync()
    {
        if (!Eq.Available) return;
        await _eq.ReadAsync(Config.Host, Eq, CancellationToken.None);
        Touch();
    }

    public async Task SetEqEnabledAsync(bool on)
    {
        if (!Eq.Available) return;
        await _eq.SetEnabledAsync(Config.Host, Eq, on, CancellationToken.None);
        await RefreshEqAsync();
    }

    public async Task SetEqChannelAsync(string channel)
    {
        if (!Eq.Available) return;
        Eq.Channel = channel;
        Touch();
        // The channel picks which curve the bands belong to, so re-read after switching.
        await RefreshEqAsync();
    }

    public async Task SetEqSpeakerSelectionAsync(string selection)
    {
        if (!Eq.Available) return;
        await _eq.SetSpeakerSelectionAsync(Config.Host, Eq, selection, CancellationToken.None);
        await RefreshEqAsync();
    }

    /// <summary>Sets one band; the receiver takes the whole curve in one write.</summary>
    public async Task SetEqBandAsync(string bandKey, double dB)
    {
        if (!Eq.Available) return;
        Eq.Bands[bandKey] = Math.Clamp(dB, Eq.Min, Eq.Max);
        Touch();
        await _eq.SetBandsAsync(Config.Host, Eq, CancellationToken.None);
    }

    public async Task ResetEqAsync()
    {
        if (!Eq.Available) return;
        await _eq.SetDefaultsAsync(Config.Host, Eq, CancellationToken.None);
        await RefreshEqAsync();
    }

    // Setup API ------------------------------------------------------------

    public async Task RefreshInfoAsync()
    {
        if (!Profile.SetupApi) return;
        await _info.ReadAsync(Config.Host, Info, CancellationToken.None);
        Touch();
    }

    /// <summary>Reads one of the receiver's config documents for the setup browser.</summary>
    public async Task<(IReadOnlyList<ConfigRow> Rows, string? Root)> ReadConfigAsync(ConfigGroup group)
    {
        var document = await _ajax.ReadAsync(Config.Host, group.Section, group.Type, CancellationToken.None);
        if (document?.Root is null) return ([], null);
        return (AjaxConfigClient.Flatten(document), document.Root.Name.LocalName);
    }

    public Task<bool> WriteConfigAsync(ConfigGroup group, string root, ConfigRow row, string value) =>
        _ajax.WriteAsync(Config.Host, group.Section, group.Type, root, row.Name, row.Index, value,
            CancellationToken.None);

    // Sleep, night mode, menu -------------------------------------------

    public void SetSleep(int? minutes)
    {
        var previous = State.SleepMinutes;
        State.SleepMinutes = minutes;
        Touch();
        SendVerified("SLP", "Sleep timer",
            minutes is null or <= 0 ? "SLPOFF" : $"SLP {Math.Clamp(minutes.Value, 1, 120):000}",
            minutes is null or <= 0 ? "OFF" : $"{Math.Clamp(minutes.Value, 1, 120):000}",
            "SLP?",
            () => State.SleepMinutes = previous);
    }

    public void SetNightMode(bool on)
    {
        NightMode = on;
        Config.Settings.NightMode = on;
        SettingsChanged?.Invoke();

        if (on)
        {
            SetDynamicVolume(Config.Settings.NightDynamicVolume);
            SetDynamicEq(true);

            var current = State.Main.Volume;
            if (current is not null && current.Value > Config.Settings.NightMaxVolume)
                SetZoneVolume(1, Config.Settings.NightMaxVolume);
        }

        Touch();
    }

    /// <summary>Cursor pad and menu keys: MNMEN ON, MNCUP, MNENT, MNRTN, MNOPT ...</summary>
    public void MenuCommand(string code)
    {
        if (code is "MNMEN ON" or "MNMEN OFF") State.MenuOpen = code.EndsWith("ON");
        Touch();
        Send(code);
    }

    // Scenes -------------------------------------------------------------

    public void RunScene(Scene scene)
    {
        foreach (var command in scene.Commands)
            Send(command);
    }

    /// <summary>Builds a scene from what the receiver is doing right now.</summary>
    public Scene CaptureScene(string name)
    {
        var commands = new List<string> { "ZMON", $"{DelayToken}1200" };

        if (State.Main.Source is { Length: > 0 } source) commands.Add("SI" + source);
        if (State.SoundMode is { Length: > 0 } mode && !mode.StartsWith("QUICK")) commands.Add("MS" + mode);
        if (State.Main.Volume is { } volume) commands.Add("MV" + EncodeVolume(volume));
        if (State.Main.Mute == false) commands.Add("MUOFF");

        foreach (var zone in Zones.Where(z => z != 1))
        {
            var zoneState = State.Zone(zone);
            if (zoneState.Power is null) continue;

            commands.Add($"{ZonePrefix(zone)}{(zoneState.Power == true ? "ON" : "OFF")}");
            if (zoneState.Power != true) continue;

            if (zoneState.Source is { Length: > 0 } zoneSource) commands.Add($"{ZonePrefix(zone)}{zoneSource}");
            if (zoneState.Volume is { } zoneVolume) commands.Add($"{ZonePrefix(zone)}{EncodeVolume(zoneVolume)}");
        }

        return new Scene { Name = name, ReceiverId = Config.Id, Commands = commands };
    }

    // ---------------------------------------------------------------- encoding

    private static string EncodeVolume(double value)
    {
        value = Math.Clamp(value, 0, 98);
        var whole = (int)Math.Floor(value);
        var half = value - whole >= 0.5;
        return half ? $"{whole:00}5" : $"{whole:00}";
    }

    private static string EncodeLevel(double value)
    {
        var whole = (int)Math.Floor(value);
        var half = value - whole >= 0.5;
        return half ? $"{whole:00}5" : $"{whole:00}";
    }

    private void QueryAll()
    {
        string[] queries =
        [
            "PW?", "ZM?", "MV?", "MU?", "SI?", "MS?", "SLP?",
            "PSTONE CTRL ?", "PSBAS ?", "PSTRE ?", "PSSWL ?", "PSDIL ?",
            "PSDYNEQ ?", "PSDYNVOL ?", "PSMULTEQ: ?", "PSRSTR ?",
            "CV?",
            "Z2?", "Z2MU?", "Z3?",
            "ECO?", "DIM ?", "STBY?",
            "VSMONI ?", "VSAUDIO ?", "SV?",
        ];

        foreach (var query in queries) Send(query);

        // Ask for every surround parameter; the ones that come back are the ones
        // that apply to this unit in its current mode.
        foreach (var spec in SurroundParameters.All) Send(spec.Query);

        if (string.Equals(State.Main.Source, "TUNER", StringComparison.OrdinalIgnoreCase))
        {
            Send("TFAN?");
            Send("TPAN?");
        }
    }

    // ---------------------------------------------------------------- transport

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Explicitly IPv4. The parameterless TcpClient builds a dual-stack
                // socket and sets IPv6Only on it during construction, which faults on
                // macOS in a self-contained build - and the receivers are IPv4 anyway.
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(4));
                    await tcp.ConnectAsync(Config.Host, TelnetPort, connectCts.Token);
                }

                tcp.NoDelay = true;

                _stream = tcp.GetStream();
                backoff = TimeSpan.FromSeconds(2);
                Asleep = false;
                SetOnline(true, null);
                _log.LogInformation("Connected to {Host}", Config.Host);

                _ = Task.Run(() => LoadProfileAsync(ct), ct);
                QueryAll();
                await ReadLoopAsync(_stream, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug("Connection to {Host} lost: {Message}", Config.Host, ex.Message);
                SetOnline(false, ex.Message);
                Asleep = await AnswersHttpAsync(ct);
                if (Asleep) Touch();
            }
            finally
            {
                _stream = null;
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(20, backoff.TotalSeconds * 1.6));
        }

        SetOnline(false, State.Error);
    }

    /// <summary>A unit in eco standby refuses TCP 23 but still serves HTTP.</summary>
    private async Task<bool> AnswersHttpAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"http://{Config.Host}/goform/Deviceinfo.xml", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var line = new StringBuilder(32);

        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) throw new IOException("Receiver closed the connection.");

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\r')
                {
                    if (line.Length > 0) Handle(line.ToString());
                    line.Clear();
                }
                else if (b >= 0x20 && b < 0x7f)
                {
                    if (line.Length < 64) line.Append((char)b);
                }
            }
        }
    }

    private async Task SenderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            PendingCommand? command = null;
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    command = _queue[0];
                    _queue.RemoveAt(0);
                }
            }
            if (command is null) continue;

            if (command.Text.StartsWith(DelayToken, StringComparison.Ordinal))
            {
                var ms = int.TryParse(command.Text[DelayToken.Length..], out var parsed) ? parsed : 500;
                try { await Task.Delay(Math.Clamp(ms, 0, 5000), ct); } catch { break; }
                continue;
            }

            await TransmitAsync(command.Text, ct);
            try { await Task.Delay(SendGap, ct); } catch { break; }
        }
    }

    private async Task TransmitAsync(string command, CancellationToken ct)
    {
        Record(outgoing: true, command);
        var stream = _stream;
        if (stream is not null)
        {
            try
            {
                var bytes = Encoding.ASCII.GetBytes(command + "\r");
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                return;
            }
            catch (Exception ex)
            {
                _log.LogDebug("Socket write failed ({Message}); falling back to HTTP.", ex.Message);
            }
        }

        // Fallback: the receiver's fire-and-forget HTTP command endpoint.
        try
        {
            var url = $"http://{Config.Host}/goform/formiPhoneAppDirect.xml?{command.Replace(" ", "%20")}";
            using var response = await Http.GetAsync(url, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug("HTTP fallback failed for {Command}: {Message}", command, ex.Message);
        }
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>Internal so the self-test can feed it protocol lines directly.</summary>
    internal void Handle(string line)
    {
        Record(outgoing: false, line);
        var changed = true;

        if (line.StartsWith("PS", StringComparison.Ordinal))
            changed = HandleParameter(line[2..]);
        else if (line.StartsWith("CV", StringComparison.Ordinal))
            changed = HandleChannel(line[2..]);
        else if (line.StartsWith("MNMEN", StringComparison.Ordinal))
            State.MenuOpen = line.EndsWith("ON", StringComparison.Ordinal);
        else if (line.StartsWith("SLP", StringComparison.Ordinal))
            changed = HandleSleep(line[3..]);
        else if (line.StartsWith("PW", StringComparison.Ordinal))
        {
            State.Power = line == "PWON";
            Confirm("PW", line[2..]);
        }
        else if (line.StartsWith("ZM", StringComparison.Ordinal))
        {
            State.Main.Power = line == "ZMON";
            Confirm("Z1PW", line[2..]);
        }
        else if (line.StartsWith("MVMAX", StringComparison.Ordinal))
            changed = TryVolume(line[5..], out var max) && Assign(() => State.VolumeMax = max);
        else if (line.StartsWith("MV", StringComparison.Ordinal))
        {
            Confirm("Z1VOL", line[2..]);
            changed = TryVolume(line[2..], out var vol) && Assign(() => State.Main.Volume = vol);
        }
        else if (line.StartsWith("MU", StringComparison.Ordinal))
        {
            State.Main.Mute = line == "MUON";
            Confirm("Z1MU", line[2..]);
        }
        else if (line.StartsWith("SI", StringComparison.Ordinal))
        {
            State.Main.Source = line[2..];
            Confirm("Z1SI", line[2..]);
        }
        else if (line.StartsWith("MSQUICK", StringComparison.Ordinal))
        {
            State.QuickSelect = int.TryParse(line.AsSpan(7, 1), out var slot) ? slot : State.QuickSelect;
            Confirm("QUICK", State.QuickSelect?.ToString());
        }
        else if (line.StartsWith("MS", StringComparison.Ordinal))
        {
            State.SoundMode = line[2..];
            Confirm("MS", line[2..]);
        }
        else if (line.StartsWith("Z2", StringComparison.Ordinal))
            changed = HandleZone(2, line[2..]);
        else if (line.StartsWith("Z3", StringComparison.Ordinal))
            changed = HandleZone(3, line[2..]);
        else if (line.StartsWith("ECO", StringComparison.Ordinal))
            State.System.Eco = Settle("ECO", line[3..]);
        else if (line.StartsWith("DIM ", StringComparison.Ordinal))
            State.System.Dimmer = Settle("DIM", line[4..]);
        else if (line.StartsWith("STBY", StringComparison.Ordinal))
            State.System.AutoStandby = Settle("STBY", line[4..]);
        else if (line.StartsWith("VSMONI", StringComparison.Ordinal))
            State.System.MonitorOut = Settle("VSMONI", line[6..]);
        else if (line.StartsWith("VSAUDIO ", StringComparison.Ordinal))
            State.System.HdmiAudio = Settle("VSAUDIO", line[8..]);
        else if (line.StartsWith("SV", StringComparison.Ordinal))
            State.System.VideoSelect = Settle("SV", line[2..]);
        else if (line.StartsWith("TFAN", StringComparison.Ordinal))
            changed = HandleTunerFrequency(line[4..]);
        else if (line.StartsWith("TPAN", StringComparison.Ordinal))
        {
            Confirm("TPAN", line[4..]);
            changed = int.TryParse(line[4..], out var preset) && Assign(() => State.System.TunerPreset = preset);
        }
        else
            changed = false;   // SS / SD / VS* ... not modelled

        if (changed) Touch();
    }

    private bool HandleZone(int zone, string rest)
    {
        if (rest.Length == 0) return false;

        if (zone == 3) State.Zone3Seen = true;
        var zoneState = State.Zone(zone);

        if (rest.StartsWith("QUICK", StringComparison.Ordinal))
            return false;
        if (rest.StartsWith("MU", StringComparison.Ordinal))
        {
            zoneState.Mute = rest == "MUON";
            Confirm($"Z{zone}MU", rest[2..]);
        }
        else if (rest is "ON" or "OFF")
        {
            zoneState.Power = rest == "ON";
            Confirm($"Z{zone}PW", rest);
        }
        else if (rest.All(char.IsAsciiDigit))
        {
            Confirm($"Z{zone}VOL", rest);
            return TryVolume(rest, out var vol) && Assign(() => zoneState.Volume = vol);
        }
        else if (rest.StartsWith("PS", StringComparison.Ordinal) ||
                 rest.StartsWith("CV", StringComparison.Ordinal) ||
                 rest.StartsWith("CS", StringComparison.Ordinal) ||
                 rest.StartsWith("HP", StringComparison.Ordinal) ||
                 rest.StartsWith("SLP", StringComparison.Ordinal))
            return false;
        else
        {
            zoneState.Source = rest;
            Confirm($"Z{zone}SI", rest);
        }

        return true;
    }

    /// <summary>PS lines: "BAS 51", "DYNEQ ON", "MULTEQ:AUDYSSEY", "CINEMA EQ.ON".</summary>
    private bool HandleParameter(string rest)
    {
        if (rest.StartsWith("MULTEQ:", StringComparison.Ordinal))
        {
            State.Audio.MultEq = Settle("PS:MULTEQ", rest[7..]);
            return true;
        }

        if (rest.StartsWith("TONE CTRL", StringComparison.Ordinal))
        {
            var tone = rest[9..].Trim();
            State.Audio.ToneControl = tone.Equals("ON", StringComparison.OrdinalIgnoreCase);
            Confirm("PS:TONE CTRL", tone);
            return true;
        }

        // Cinema EQ is the one parameter whose value is glued to the key.
        if (rest.StartsWith("CINEMA EQ.", StringComparison.Ordinal))
        {
            State.Audio.Parameters["CINEMA EQ."] = Settle("PS:CINEMA EQ.", rest[10..]);
            return true;
        }

        var space = rest.IndexOf(' ');
        if (space <= 0) return false;

        var key = rest[..space];
        var value = rest[(space + 1)..].Trim();

        // Anything the receiver answers is a setting that applies to it right now,
        // so the UI can show exactly the parameters this unit supports in this mode.
        State.Audio.Parameters[key] = value;
        Confirm("PS:" + key, value);

        switch (key)
        {
            case "BAS": return TryLevel(value, out var bass) && Assign(() => State.Audio.Bass = bass);
            case "TRE": return TryLevel(value, out var treble) && Assign(() => State.Audio.Treble = treble);
            case "SWL": return TryLevel(value, out var sub) && Assign(() => State.Audio.Subwoofer = sub);
            case "DIL": return TryLevel(value, out var dialog) && Assign(() => State.Audio.Dialog = dialog);
            case "DYNEQ": State.Audio.DynamicEq = value == "ON"; return true;
            case "DYNVOL": State.Audio.DynamicVolume = value; return true;
            case "RSTR": State.Audio.Restorer = value; return true;
            default: return SurroundParameters.Find(key) is not null;
        }
    }

    /// <summary>"TFAN09790" is 97.90 MHz; AM comes back in kHz.</summary>
    private bool HandleTunerFrequency(string rest)
    {
        rest = rest.Trim();
        if (!rest.All(char.IsAsciiDigit) || rest.Length == 0) return false;
        if (!int.TryParse(rest, out var raw)) return false;

        State.System.TunerFrequency = raw / 100.0;
        return true;
    }

    /// <summary>CV lines: "FL 50", "C 505", terminated by "END".</summary>
    private bool HandleChannel(string rest)
    {
        if (rest.StartsWith("END", StringComparison.Ordinal)) return false;

        var space = rest.IndexOf(' ');
        if (space <= 0) return false;

        var channel = rest[..space];
        Confirm($"CV:{channel}", rest[(space + 1)..]);
        if (!TryLevel(rest[(space + 1)..], out var level)) return false;

        State.Audio.SetChannel(channel, level);
        return true;
    }

    private bool HandleSleep(string rest)
    {
        rest = rest.Trim();
        Confirm("SLP", rest);
        if (rest.Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            State.SleepMinutes = null;
            return true;
        }

        if (!int.TryParse(rest, out var minutes)) return false;
        State.SleepMinutes = minutes > 0 ? minutes : null;
        return true;
    }

    /// <summary>"45" -> 45, "455" -> 45.5. Volumes are 0..98 on the Denon scale.</summary>
    private static bool TryVolume(string text, out double value) => TryLevel(text, out value);

    /// <summary>Same 2-or-3 digit encoding used by volume, tone and channel trims.</summary>
    private static bool TryLevel(string text, out double value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length is 0 or > 3 || !text.All(char.IsAsciiDigit)) return false;

        if (text.Length == 3)
        {
            value = int.Parse(text[..2]) + (text[2] == '5' ? 0.5 : 0);
            return true;
        }

        value = int.Parse(text);
        return true;
    }

    private static bool Assign(Action set) { set(); return true; }

    /// <summary>Records a reported value against any pending change and returns it.</summary>
    private string Settle(string key, string raw)
    {
        var value = raw.Trim();
        Confirm(key, value);
        return value;
    }

    private void SetOnline(bool online, string? error)
    {
        State.Online = online;
        State.Error = online ? null : error;
        if (!online)
        {
            State.Power = null;
            State.Main.Power = null;
            State.Zone2.Power = null;
            State.Zone3.Power = null;
        }
        Touch();
    }

    private void Touch()
    {
        State.LastUpdate = DateTimeOffset.Now;
        StateChanged?.Invoke(this);
    }
}
