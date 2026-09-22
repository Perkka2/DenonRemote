namespace DenonRemote.Models;

/// <summary>
/// What the receiver reports about the signal it is actually handling. None of this
/// is in the control protocol - it comes from the setup API's Information document.
/// </summary>
public sealed class ReceiverInfo
{
    public string? SoundMode { get; set; }
    public string? InputSignal { get; set; }
    public string? Format { get; set; }

    public string? Resolution { get; set; }
    public string? Hdr { get; set; }
    public string? ColorSpace { get; set; }
    public string? PixelDepth { get; set; }

    public string? MonitorInterface { get; set; }
    public string? MonitorHdr { get; set; }
    public List<string> MonitorResolutions { get; } = [];

    public string? MainSource { get; set; }
    public string? Zone2Source { get; set; }
    public string? Zone2Volume { get; set; }

    public string? FirmwareVersion { get; set; }
    public string? DtsVersion { get; set; }

    public DateTimeOffset? LastRead { get; set; }

    /// <summary>
    /// The receiver pads fields it has nothing for rather than leaving them out:
    /// " ---  -&gt;  --- " for video, " / /.0" for an audio format. Anything with no
    /// letters and no digit above zero is one of those placeholders.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        var meaningful = trimmed.Any(char.IsLetter) || trimmed.Any(c => c is >= '1' and <= '9');
        return meaningful ? trimmed : null;
    }

    public bool HasSignal => Clean(Resolution) is not null || Clean(Format) is not null;
}
