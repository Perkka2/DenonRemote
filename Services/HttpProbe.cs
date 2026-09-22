using System.Text;
using DenonRemote.Models;

namespace DenonRemote.Services;

public sealed record ProbeResult(string Method, string Url, int Status, string? ContentType, string Body, string? Error)
{
    public bool Interesting => Error is null && Status is >= 200 and < 300 && Body.Trim().Length > 0;

    /// <summary>
    /// The receiver buckling rather than answering: it refused the connection, took
    /// too long, or said outright that it is unavailable. Worth another go after a
    /// pause; a 403 or a 404 is an answer and is not.
    ///
    /// A 500 is not in here, though it was briefly. On the X2500H a missing config
    /// type answers 500 with an empty body, consistently, and the very next type
    /// answers 200 - advanced 3 through 8 are 500 and advanced 9 is fine. So a 500
    /// is this receiver's way of saying "no such setting". Retrying those four times
    /// apiece bought nothing and added a few hundred requests to a sweep whose whole
    /// problem was its weight.
    /// </summary>
    public bool Dropped =>
        (Error is not null &&
            (Error.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
             Error.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
             Error.Contains("SSL", StringComparison.OrdinalIgnoreCase)))
        || Status is 503;
}

/// <summary>
/// Sends arbitrary HTTP at a receiver and sweeps its config endpoints.
///
/// The graphic EQ is not in the published control protocol and the receiver's own
/// setup UI drives it over HTTP, so this exists to find out how. On the HEOS
/// generation the Audyssey block is known to live at /ajax/audio/get_config?type=9,
/// which makes the graphic EQ a likely sibling under the same family.
/// </summary>
public sealed class HttpProbe(ILogger<HttpProbe> log, AjaxUiReader ui)
{
    /// <summary>
    /// The setup UI on newer units is HTTPS on 10443 with a self-signed certificate,
    /// so this client skips validation. It is only ever pointed at a receiver on the
    /// local network that the user has configured themselves.
    /// </summary>
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    /// <summary>Plenty for a config response, and what the console shows.</summary>
    private const int MaxBody = 60_000;

    /// <summary>Setup-UI scripts are large; truncating them loses the part we need.</summary>
    private const int MaxAsset = 2_000_000;

    public async Task<ProbeResult> SendAsync(
        string method, string url, string? body, CancellationToken ct, int maxBody = MaxBody,
        ReceiverPace pace = ReceiverPace.Interactive)
    {
        // One at a time, with a pause, and another go if the receiver drops it.
        // See ReceiverGate: hammering these units makes them stop answering, and
        // every setting after that point looks like one they do not have.
        // Authority, not host: port 80 having no server says nothing about 10443.
        var endpoint = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Authority : url;
        return await ReceiverGate.RunAsync(endpoint, () => SendOnceAsync(method, url, body, ct, maxBody),
            r => r.Dropped, ct, pace);
    }

    private static async Task<ProbeResult> SendOnceAsync(
        string method, string url, string? body, CancellationToken ct, int maxBody)
    {
        try
        {
            using var request = new HttpRequestMessage(
                string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get,
                url);

            if (!string.IsNullOrWhiteSpace(body))
                request.Content = new StringContent(body, Encoding.UTF8, "text/xml");

            using var response = await Client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > maxBody) text = text[..maxBody] + "\n… truncated …";

            return new ProbeResult(method, url, (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(), text, null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(method, url, 0, null, "", ex.Message);
        }
    }

    /// <summary>Posts a form body, which is what the pre-HEOS setup pages expect.</summary>
    public async Task<ProbeResult> SendFormAsync(string method, string url, string body, CancellationToken ct)
    {
        // Through the gate like everything else. This one was going out unpaced, and
        // a setup write is immediately followed by a read-back - the two together are
        // exactly the back-to-back pair the receiver dislikes.
        var endpoint = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Authority : url;
        return await ReceiverGate.RunAsync(endpoint, () => SendFormOnceAsync(url, body, ct), r => r.Dropped, ct);
    }

    private static async Task<ProbeResult> SendFormOnceAsync(string url, string body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
            };

            using var response = await Client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > MaxBody) text = text[..MaxBody] + "\n… truncated …";

            return new ProbeResult("POST", url, (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(), text, null);
        }
        catch (Exception ex)
        {
            return new ProbeResult("POST", url, 0, null, "", ex.Message);
        }
    }

    /// <summary>Everything worth asking a receiver that might expose the EQ.</summary>
    public static IEnumerable<(string Method, string Url, string? Body)> SweepTargets(string host)
    {
        yield return ("GET", $"http://{host}/goform/Deviceinfo.xml", null);
        yield return ("GET", $"http://{host}/goform/formMainZone_MainZoneXmlStatusLite.xml", null);

        // What the pre-HEOS web UI itself polls, once a second, to draw its zone and
        // player views. On those units there is no HEOS, so this is the only account
        // of what is actually playing.
        yield return ("GET", $"http://{host}/goform/formMainZone_MainZoneXml.xml", null);
        yield return ("GET", $"http://{host}/goform/formNetAudio_StatusXml.xml", null);
        yield return ("GET", $"http://{host}/goform/formZone2_Zone2XmlStatus.xml", null);
        yield return ("GET", $"http://{host}/goform/formZone3_Zone3XmlStatus.xml", null);

        string AppCommand(string command) =>
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><tx><cmd id=\"1\">{command}</cmd></tx>";

        foreach (var endpoint in new[] { "AppCommand.xml", "AppCommand0300.xml" })
        foreach (var command in new[]
                 {
                     "GetAudyssey", "GetToneControl", "GetSurroundModeStatus",
                     "GetGraphicEQ", "GetManualEQ", "GetEQCurve", "GetAllZonePowerStatus",
                 })
        {
            yield return ("POST", $"http://{host}/goform/{endpoint}", AppCommand(command));
        }

        // Which transport the config API is on: plain HTTP on some firmware, HTTPS
        // 10443 on the rest. Three types is enough to tell - the rest of the API is
        // covered by what the receiver declares, so walking 1..12 over both
        // transports was twenty-four questions to answer one.
        for (var type = 1; type <= 3; type++)
            yield return ("GET", $"http://{host}/ajax/audio/get_config?type={type}", null);
    }

    /// <summary>
    /// How far up to count in each section when the receiver will not say.
    ///
    /// Guesses, and they were guessed short once already: video stopped at 4, so TV
    /// Format at 9 was never captured and looked for all the world like a setting the
    /// receiver did not have.
    /// </summary>
    private static readonly (string Section, int Max)[] Fallback =
    [
        ("audio", 16), ("video", 16), ("inputs", 8), ("speakers", 20),
        ("network", 14), ("general", 24), ("control", 8), ("advanced", 12),
        ("home", 2),
    ];

    /// <summary>
    /// The config screens to ask this particular receiver for.
    ///
    /// Its own setup UI declares most of them - CONFIG_TVFORMAT:"9" and the rest - so
    /// the ceiling comes from the receiver instead of from a guess here, which is the
    /// point: a guess that is short makes a setting the unit has look like one it does
    /// not.
    ///
    /// Not only the declared numbers, though. Every section on the X2500H declares
    /// 2..n and omits 1, and 1 answers perfectly well on all of them - so the range is
    /// 1 up to the highest it names. And two sections, Zone and Home, declare nothing
    /// at all while answering six and one screens respectively; those fall back to
    /// counting.
    /// </summary>
    public static IEnumerable<(string Method, string Url, string? Body)> ConfigTargets(
        string host, IReadOnlyList<ConfigGroup> declared)
    {
        var highest = declared
            .GroupBy(g => g.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Type), StringComparer.OrdinalIgnoreCase);

        foreach (var (section, guess) in Fallback)
        {
            var max = highest.TryGetValue(section, out var said) ? said : guess;
            for (var type = 1; type <= max; type++)
                yield return ("GET", $"https://{host}:10443/ajax/{section}/get_config?type={type}", null);
        }

        // A section the receiver named that this list does not know about.
        foreach (var (section, max) in highest)
        {
            if (Fallback.Any(f => string.Equals(f.Section, section, StringComparison.OrdinalIgnoreCase)))
                continue;
            for (var type = 1; type <= max; type++)
                yield return ("GET", $"https://{host}:10443/ajax/{section}/get_config?type={type}", null);
        }
    }

    /// <summary>The setup UI's own section pages, from its left-hand menu.</summary>
    private static readonly string[] SectionPages =
    [
        // HEOS-generation setup UI.
        "/", "/audio/audio.html", "/inputs/inputs.html", "/general/general.html",
        "/speakers/speakers.html", "/video/video.html", "/network/network.html",
        "/advanced/advanced.html", "/control/zone.html",

        // The pre-HEOS units serve ASP pages instead, and those do carry the EQ.
        "/SETUP/f_home.asp",
        "/SETUP/AUDIO/f_audio.asp",
        "/SETUP/AUDIO/GRAPHICEQ/f_audio.asp",
        "/SETUP/AUDIO/MANUALEQ/f_audio.asp",
    ];

    /// <summary>
    /// Downloads the receiver's own setup UI - every section page and the scripts each
    /// one loads. The UI drives the undocumented settings, so its JavaScript states
    /// exactly how, which beats guessing at payloads and, unlike probing set_config,
    /// changes nothing on the receiver.
    /// </summary>
    public async Task<string> FetchUiAsync(string host, string directory, CancellationToken ct)
    {
        var root = Path.Combine(directory, "ui-" + host.Replace(':', '_'));
        Directory.CreateDirectory(root);

        var report = new StringBuilder();
        report.AppendLine($"Setup UI fetch — {host} — {DateTimeOffset.Now:u}");
        report.AppendLine(new string('=', 72));

        // Newer firmware serves the setup UI over HTTPS on 10443; older over plain HTTP.
        //
        // This first request decides the whole capture, and these receivers answer
        // slowly when they feel like it - a single six-second timeout here has
        // already ended a run with "No setup UI answered" on a receiver that was
        // perfectly awake. So the deciding request, and only that one, is retried.
        // A host given with its own port says where to look, so don't also guess 10443
        // and make an unparseable "host:port:10443" out of it.
        var candidates = OriginCandidates(host);

        string? origin = null;
        foreach (var candidate in candidates)
        {
            for (var attempt = 1; attempt <= OriginAttempts && origin is null; attempt++)
            {
                if (ct.IsCancellationRequested) break;

                var probe = await SendAsync("GET", candidate + "/", null, ct, MaxAsset, ReceiverPace.Bulk);
                var outcome = probe.Error is null ? probe.Status.ToString() : probe.Error;
                report.AppendLine($"GET {candidate}/ -> {outcome}"
                    + (attempt > 1 ? $"  (attempt {attempt})" : ""));

                if (probe.Interesting) origin = candidate;

                // A refused connection is an answer: nothing is listening there.
                else if (probe.Error?.Contains("refused", StringComparison.OrdinalIgnoreCase) == true) break;
            }

            if (origin is not null) break;
        }

        if (origin is null)
        {
            report.AppendLine();
            report.AppendLine("No setup UI answered on this receiver.");
            return await WriteReportAsync(directory, host, report, ct);
        }

        var saved = new List<string>();
        var fetched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The pre-HEOS ASP tree is far bigger than any list worth hardcoding - six
        // sections, each with its own subpages - so the section pages are only seeds
        // and the rest is followed from the receiver's own links.
        var queue = new Queue<(Uri Url, int Depth)>();
        foreach (var page in SectionPages)
        {
            if (Uri.TryCreate(origin + page, UriKind.Absolute, out var seed) && fetched.Add(seed.AbsoluteUri))
                queue.Enqueue((seed, 0));
        }

        var origin_ = new Uri(origin);
        var pages = 0;

        while (queue.Count > 0 && pages < MaxPages)
        {
            if (ct.IsCancellationRequested) break;

            var (pageUrl, depth) = queue.Dequeue();
            var response = await SendAsync("GET", pageUrl.AbsoluteUri, null, ct, MaxAsset, ReceiverPace.Bulk);
            report.AppendLine();
            report.AppendLine($"GET {pageUrl.AbsoluteUri} -> {(response.Error is null ? response.Status.ToString() : response.Error)}");
            if (!response.Interesting) continue;

            pages++;
            await SaveAsync(root, pageUrl.AbsoluteUri, response.Body, saved, ct);

            foreach (var reference in References(response.Body).Distinct().Take(60))
            {
                if (ct.IsCancellationRequested) break;
                if (!Uri.TryCreate(pageUrl, reference, out var linked)) continue;

                // href="index.html#" is the same page, and fetching it again wastes a
                // request and saves a second copy under a name ending in '#'.
                if (linked.Fragment.Length > 0)
                    linked = new Uri(linked.GetLeftPart(UriPartial.Query));
                if (!SameOrigin(origin_, linked) || !SafeToRead(linked)) continue;
                if (!fetched.Add(linked.AbsoluteUri)) continue;

                // A page can hold more pages; a script cannot, so it is fetched once
                // and not walked.
                if (IsDocument(linked) && depth < MaxDepth)
                {
                    queue.Enqueue((linked, depth + 1));
                    continue;
                }

                var asset = await SendAsync("GET", linked.AbsoluteUri, null, ct, MaxAsset, ReceiverPace.Bulk);
                report.AppendLine($"  {linked.AbsolutePath} -> {(asset.Error is null ? asset.Status.ToString() : asset.Error)}");
                if (asset.Interesting) await SaveAsync(root, linked.AbsoluteUri, asset.Body, saved, ct);
            }
        }

        if (queue.Count > 0)
            report.AppendLine($"\nStopped at {MaxPages} pages with {queue.Count} still queued.");

        report.AppendLine();
        report.AppendLine($"Saved {saved.Count} file(s) under {root}");
        foreach (var file in saved) report.AppendLine("  " + file);

        return await WriteReportAsync(directory, host, report, ct);
    }

    private async Task<string> WriteReportAsync(string directory, string host, StringBuilder report, CancellationToken ct)
    {
        var path = Path.Combine(directory, $"ui-{host.Replace(':', '_')}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, report.ToString(), ct);
        log.LogInformation("UI fetch report written to {Path}", path);
        return path;
    }

    /// <summary>How far to follow the receiver's own links, and how much to take.</summary>
    private const int MaxPages = 250;
    private const int OriginAttempts = 3;

    /// <summary>
    /// Where the setup UI might be. Newer firmware serves it over HTTPS on 10443,
    /// older over plain HTTP. A host given with its own port says where to look, so
    /// don't also guess 10443 and make an unparseable "host:port:10443" out of it.
    /// </summary>
    internal static string[] OriginCandidates(string host) =>
        host.Contains(':') ? [$"http://{host}"] : [$"https://{host}:10443", $"http://{host}"];
    private const int MaxDepth = 6;

    internal static IEnumerable<string> ReferencesOf(string html) => References(html);

    private static IEnumerable<string> References(string html)
    {
        foreach (var match in Matches(html, "<script[^>]+src=[\"']([^\"']+)[\"']")) yield return match;
        foreach (var match in Matches(html, "<link[^>]+href=[\"']([^\"']+\\.js)[\"']")) yield return match;

        // The old ASP UI is frame-based, and the frames are where the controls live.
        foreach (var match in Matches(html, "<i?frame[^>]+src=[\"']([^\"']+)[\"']")) yield return match;

        // Its menus are plain links, which is how the rest of the tree is reached.
        foreach (var match in Matches(html, "<a[^>]+href=[\"']([^\"']+)[\"']")) yield return match;

        // The zone and player pages carry no <script src> at all: they build the tag
        // at runtime - loadLib('index.js?201112230000') - so the file that holds all
        // their behaviour is invisible to a tag-only scan. Any quoted .js path inside
        // a script block is followed instead.
        foreach (var block in Matches(html, "<script[^>]*>(.*?)</script>"))
        {
            foreach (var match in Matches(block, "[\"']([^\"']+\\.js(?:\\?[^\"']*)?)[\"']"))
                yield return match;

            // Some frames are stubs: an empty form and a window.onload that sends the
            // browser on to the page that actually has the settings. Network's
            // Connection and Settings are both like this, and following only links
            // and frames left their real pages uncaptured entirely.
            foreach (var match in Matches(block, "location\\.href\\s*=\\s*[\"']([^\"']+)[\"']"))
                yield return match;
        }
    }

    internal static bool SameOrigin(Uri origin, Uri candidate) =>
        candidate.Scheme == origin.Scheme && candidate.Host == origin.Host && candidate.Port == origin.Port;

    internal static bool IsDocument(Uri url) =>
        url.AbsolutePath.EndsWith(".asp", StringComparison.OrdinalIgnoreCase)
        || url.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || url.AbsolutePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A capture must read and change nothing. Two shapes on this UI do change things:
    /// the s_*.asp pages are where it submits a section's form, and a query string is
    /// how it carries a value. Neither is ever fetched, however tempting the name.
    /// </summary>
    internal static bool SafeToRead(Uri url)
    {
        // A query string on a page is how this UI carries a value to apply. On an
        // asset it is a cache-buster - index.js?201112230000 - and refusing those
        // would drop the very scripts the zone pages are made of.
        if (url.Query.Length > 0 && IsDocument(url)) return false;

        var file = url.Segments.Length > 0 ? url.Segments[^1] : "";
        if (file.StartsWith("s_", StringComparison.OrdinalIgnoreCase)) return false;

        // Corners of the setup menu that do something rather than show something.
        // Config save and load, initialise and firmware update are all one page away
        // from the menu, and none of them has anything to teach a capture.
        foreach (var segment in url.Segments)
        {
            var name = segment.Trim('/');
            if (DangerousSegments.Contains(name)) return false;
        }

        return true;
    }

    private static readonly HashSet<string> DangerousSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE", "LOAD", "RESTORE", "INITIALIZE", "INITIALIZ", "UPDATE", "UPGRADE", "FIRMWARE", "RESET",
    };

    // This UI writes href='../VIDEO/f_video.asp ' - the space is inside the quotes.
    private static IEnumerable<string> Matches(string text, string pattern) =>
        System.Text.RegularExpressions.Regex
            // Singleline so a script block's body can span lines; no other pattern
            // here uses a bare dot, so nothing else is affected.
            .Matches(text, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(v => v.Length > 0);

    private async Task SaveAsync(string root, string url, string body, List<string> saved, CancellationToken ct)
    {
        var name = new Uri(url).AbsolutePath.Trim('/');
        if (name.Length == 0) name = "index.html";
        name = name.Replace('/', '_');
        if (name.Length > 120) name = name[^120..];

        await File.WriteAllTextAsync(Path.Combine(root, name), body, ct);
        saved.Add(name + (body.Contains("GraphicEQ", StringComparison.OrdinalIgnoreCase)
            ? "   <-- mentions GraphicEQ" : ""));
    }

    /// <summary>
    /// Runs the sweep and writes a readable report. Returns the file path.
    /// </summary>
    public async Task<string> SweepAsync(string host, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"probe-{host.Replace(':', '_')}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var report = new StringBuilder();
        report.AppendLine($"Denon HTTP probe — {host} — {DateTimeOffset.Now:u}");
        report.AppendLine(new string('=', 72));

        var hits = 0;
        var sent = 0;
        var started = DateTimeOffset.UtcNow;

        // Ask the receiver what it has before asking it for anything. Counting types
        // upwards meant most of the sweep was questions it could only answer 500 to,
        // and the weight of that is what made it stop answering at all.
        var declared = await ui.ReadAsync(host, ct);
        var config = ConfigTargets(host, declared).ToList();

        report.AppendLine(declared.Count > 0
            ? $"Setup UI declares {declared.Count} config screen(s); asking for {config.Count}."
            : $"Setup UI did not answer; counting config types upwards ({config.Count} requests).");

        foreach (var (method, url, body) in SweepTargets(host).Concat(config))
        {
            if (ct.IsCancellationRequested) break;

            sent++;

            var result = await SendAsync(method, url, body, ct, MaxBody, ReceiverPace.Bulk);

            report.AppendLine();
            report.AppendLine($"{method} {url}");
            if (body is not null) report.AppendLine($"  body: {body}");

            if (result.Error is not null)
            {
                report.AppendLine($"  -> failed: {result.Error}");
                continue;
            }

            report.AppendLine($"  -> {result.Status} {result.ContentType}");

            if (result.Body.Trim().Length == 0)
            {
                report.AppendLine("  -> empty");
                continue;
            }

            if (result.Interesting) hits++;
            report.AppendLine("  ----------------------------------------------------------");
            foreach (var line in result.Body.Split('\n'))
                report.AppendLine("  " + line.TrimEnd());
            report.AppendLine("  ----------------------------------------------------------");
        }

        report.AppendLine();
        report.AppendLine($"{hits} endpoint(s) answered with content.");
        // Worth seeing in the report: this is a lot of traffic for one receiver, and
        // the pacing that keeps it upright is what makes it take as long as it does.
        report.AppendLine(
            $"{sent} request(s) in {(DateTimeOffset.UtcNow - started).TotalSeconds:F0}s, paced one at a time.");

        await File.WriteAllTextAsync(path, report.ToString(), ct);
        log.LogInformation("Probe report written to {Path}", path);
        return path;
    }
}
