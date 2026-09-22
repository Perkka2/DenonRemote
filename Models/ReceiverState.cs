namespace DenonRemote.Models;

public sealed class ZoneState
{
    public bool? Power { get; set; }
    /// <summary>Denon scale 0..98 (half steps allowed). Absolute dB = value - 80.</summary>
    public double? Volume { get; set; }
    public bool? Mute { get; set; }
    public string? Source { get; set; }
}

/// <summary>Tone, channel trims and the Audyssey family. Values are on the raw
/// protocol scale where 50 means 0 dB.</summary>
public sealed class AudioState
{
    public bool? ToneControl { get; set; }
    public double? Bass { get; set; }
    public double? Treble { get; set; }
    public double? Subwoofer { get; set; }
    public double? Dialog { get; set; }

    public bool? DynamicEq { get; set; }
    /// <summary>OFF / LIT / MED / HEV (older firmware answers DAY / EVE / NGT).</summary>
    public string? DynamicVolume { get; set; }
    /// <summary>AUDYSSEY / BYP.LR / FLAT / MANUAL / OFF.</summary>
    public string? MultEq { get; set; }
    /// <summary>OFF / MODE1 / MODE2 / MODE3.</summary>
    public string? Restorer { get; set; }

    /// <summary>
    /// Raw value of every PS parameter the receiver has reported, keyed by the
    /// protocol key after "PS". A key being present is how we know the setting
    /// applies to this unit in its current mode.
    /// </summary>
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-channel trims keyed by protocol code (FL, FR, C, SW, SL ...),
    /// in the order the receiver reported them.</summary>
    public Dictionary<string, double> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ChannelOrder { get; } = [];

    public void SetChannel(string code, double value)
    {
        if (!Channels.ContainsKey(code)) ChannelOrder.Add(code);
        Channels[code] = value;
    }
}

/// <summary>Live state of one receiver, kept up to date by the telnet event stream.</summary>
public sealed class ReceiverState
{
    public bool Online { get; set; }
    public string? Error { get; set; }

    /// <summary>Whole-unit power (PW). Standby means nothing else responds.</summary>
    public bool? Power { get; set; }

    public double VolumeMax { get; set; } = 98;
    public string? SoundMode { get; set; }

    private readonly Dictionary<int, ZoneState> _zones = new()
    {
        [1] = new ZoneState(),
        [2] = new ZoneState(),
        [3] = new ZoneState(),
    };

    public ZoneState Main => _zones[1];
    public ZoneState Zone2 => _zones[2];
    public ZoneState Zone3 => _zones[3];
    public ZoneState Zone(int zone) => _zones.TryGetValue(zone, out var z) ? z : _zones[1];

    public AudioState Audio { get; } = new();
    public SystemState System { get; } = new();

    /// <summary>Minutes left on the sleep timer, or null when it is off.</summary>
    public int? SleepMinutes { get; set; }

    public bool MenuOpen { get; set; }

    /// <summary>Quick Select slot the receiver last reported (1-4).</summary>
    public int? QuickSelect { get; set; }

    /// <summary>True once the receiver has answered a Z3 query.</summary>
    public bool Zone3Seen { get; set; }

    public DateTimeOffset LastUpdate { get; set; }
}
