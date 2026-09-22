using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DenonRemote.Services;

/// <summary>Command-line options for the published build.</summary>
public sealed class StartupOptions
{
    public int Port { get; private init; } = 5080;
    public bool OpenBrowser { get; private init; } = true;

    /// <summary>Run the built-in checks and exit.</summary>
    public bool SelfTest { get; private init; }

    /// <summary>
    /// Capture a receiver's own setup UI and exit. The app can do this from its
    /// console, but the capture is the first thing wanted on a receiver the app has
    /// never met - and on the pre-HEOS units it is the only way to see the settings
    /// tree at all.
    /// </summary>
    public string? FetchUi { get; private init; }

    /// <summary>Sweep a receiver's HTTP APIs and exit, writing a report.</summary>
    public string? Probe { get; private init; }

    /// <summary>Parse captured setup pages and print what was read. Development aid.</summary>
    public string? ReadPages { get; private init; }

    /// <summary>Print the form fields a page would post. Development aid.</summary>
    public string? ShowPost { get; private init; }

    /// <summary>
    /// Anything on the command line that wasn't understood. A mistyped flag used to
    /// fall through the switch and silently start the web app instead - which looks
    /// exactly like a capture that ran and wrote nothing.
    /// </summary>
    public IReadOnlyList<string> Unknown { get; private init; } = [];

    /// <summary>True when the caller set --urls themselves, so we leave binding alone.</summary>
    public bool UrlsOverridden { get; private init; }

    public static StartupOptions Parse(string[] args)
    {
        var port = 5080;
        var openBrowser = true;
        var urlsOverridden = false;
        var selfTest = false;
        string? fetchUi = null;
        string? probe = null;
        string? readPages = null;
        string? showPost = null;
        var unknown = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" or "-p" when i + 1 < args.Length &&
                                           int.TryParse(args[i + 1], out var parsed):
                    port = parsed;
                    i++;
                    break;

                case "--no-browser":
                    openBrowser = false;
                    break;

                case "--self-test":
                    selfTest = true;
                    break;

                case "--fetch-ui" when i + 1 < args.Length:
                    fetchUi = args[i + 1];
                    i++;
                    break;

                case "--probe" when i + 1 < args.Length:
                    probe = args[i + 1];
                    i++;
                    break;

                case "--read-pages" when i + 1 < args.Length:
                    readPages = args[i + 1];
                    i++;
                    break;

                case "--show-post" when i + 1 < args.Length:
                    showPost = args[i + 1];
                    i++;
                    break;

                case "--urls":
                    urlsOverridden = true;
                    break;

                default:
                    // Whatever the host passes through is not ours to judge; a flag is.
                    if (args[i].StartsWith('-')) unknown.Add(args[i]);
                    break;
            }
        }

        // ASPNETCORE_URLS does the same job, so don't fight it.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            urlsOverridden = true;

        return new StartupOptions
        {
            Port = port,
            OpenBrowser = openBrowser,
            UrlsOverridden = urlsOverridden,
            SelfTest = selfTest,
            FetchUi = fetchUi,
            Probe = probe,
            ReadPages = readPages,
            ShowPost = showPost,
            Unknown = unknown,
        };
    }

    public const string Usage = """

  Usage: DenonRemote [options]

    --port <n>         listen on this port (default 5080)
    --no-browser       don't open a browser on start
    --self-test        run the built-in checks and exit
    --fetch-ui <host>  capture that receiver's own setup UI and exit
    --probe <host>     sweep that receiver's HTTP APIs and exit
    --read-pages <dir> parse captured setup pages in <dir> and print them
    --show-post <file> print the form fields that page would post

""";

    /// <summary>Addresses to print so a phone on the same wifi can be pointed at it.</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(info.Address)) continue;
                yield return info.Address.ToString();
            }
        }
    }

    public string Describe()
    {
        var lines = new List<string>
        {
            "",
            "  Denon Remote",
            $"  On this machine:  http://localhost:{Port.ToString(CultureInfo.InvariantCulture)}",
        };

        var addresses = LocalAddresses().Distinct().ToList();
        if (addresses.Count > 0)
        {
            lines.Add("  From a phone on the same wifi:");
            foreach (var address in addresses)
                lines.Add($"      http://{address}:{Port.ToString(CultureInfo.InvariantCulture)}");
        }

        lines.Add("");
        lines.Add($"  Settings are kept in {AppPaths.Root}");
        lines.Add("  Press Ctrl+C to stop.");
        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }
}
