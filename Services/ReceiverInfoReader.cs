using System.Xml.Linq;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// Reads the setup API's Information document: what the receiver is actually
/// receiving and playing, plus its firmware version. Nothing here is available
/// over the control protocol.
/// </summary>
public sealed class ReceiverInfoReader(AjaxConfigClient ajax)
{
    private const string Section = "general";
    private const int InformationType = 12;

    public async Task<bool> ReadAsync(string host, ReceiverInfo info, CancellationToken ct)
    {
        var document = await ajax.ReadAsync(host, Section, InformationType, ct);
        return Parse(document, info);
    }

    /// <summary>Separated from the fetch so it can be exercised by the self-test.</summary>
    public static bool Parse(System.Xml.Linq.XDocument? document, ReceiverInfo info)
    {
        if (document?.Root is null) return false;

        string? Text(params string[] path)
        {
            XElement? node = document.Root;
            foreach (var name in path)
            {
                node = node?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                if (node is null) return null;
            }
            return ReceiverInfo.Clean(node.Value);
        }

        info.SoundMode = Text("Audio", "SoundMode");
        info.InputSignal = Text("Audio", "InputSignal");
        info.Format = Text("Audio", "Format");

        info.Resolution = Text("Video", "HDMISignalInfo", "Resolution");
        info.Hdr = Text("Video", "HDMISignalInfo", "HDR");
        info.ColorSpace = Text("Video", "HDMISignalInfo", "ColorSpace");
        info.PixelDepth = Text("Video", "HDMISignalInfo", "PixelDepth");

        info.MonitorInterface = Text("Video", "HDMIMonitor1", "Interface");
        info.MonitorHdr = Text("Video", "HDMIMonitor1", "HDR");

        info.MonitorResolutions.Clear();
        var resolutions = document.Root
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Video")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "HDMIMonitor1")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Resolutions")
            ?.Elements().Select(e => e.Value.Trim()).Where(v => v.Length > 0);
        if (resolutions is not null) info.MonitorResolutions.AddRange(resolutions);

        info.MainSource = Text("Zone", "MainZone", "SelectSource");
        info.Zone2Source = Text("Zone", "Zone2", "SelectSource");
        info.Zone2Volume = Text("Zone", "Zone2", "Volume");

        info.FirmwareVersion = Text("Firmware", "Version");
        info.DtsVersion = Text("Firmware", "DTSVersion");

        info.LastRead = DateTimeOffset.Now;
        return true;
    }
}
