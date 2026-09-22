using System.Text.RegularExpressions;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// What the HEOS-generation receiver's own setup UI knows about itself.
///
/// That UI is a JavaScript app, and it carries three things the app was otherwise
/// guessing at: which settings screens this model has, what number each one is, and
/// what they are called. Reading them means the Setup tab follows the receiver
/// rather than a list maintained here - the same move that fixed the pre-HEOS tab,
/// and the reason a unit with Dirac Live, Speaker Preset or an HDMI upscaler shows
/// those instead of silently omitting them.
///
///   /{section}/{Section}ServerInterface.js   CONFIG_SPEAKERCONFIG:"3"
///   /{section}/{Section}Settings.js          the menu, in order
///   /LanguageStrings.js                      every label, in eleven languages
/// </summary>
public sealed partial class AjaxUiReader(ILogger<AjaxUiReader> log)
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        // The setup UI is served over HTTPS with the receiver's own certificate.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>
    /// The sections the UI is split into, in the order it shows them, with the prefix
    /// each one's scripts are served under.
    ///
    /// The order is the receiver's own: GlobalsSettings.js lists audio, video,
    /// inputs, speakers, network, general as the menu down the side, and
    /// HomeSettings.js adds Zone and Advanced off the home screen.
    ///
    /// The last three were missing, and that was doing real damage: the receiver
    /// answers advanced/1, advanced/2, advanced/9, control/1, 2, 4, 5, 7, 8 and
    /// home/1 with content, and none of those screens were being read or shown. The
    /// casing is the receiver's too - "advanced" is lower case where every other
    /// section is capitalised, and Home has no folder of its own.
    /// </summary>
    private static readonly (string Section, string Prefix)[] Sections =
    [
        ("audio", "/audio/Audio"), ("video", "/video/Video"), ("inputs", "/inputs/Inputs"),
        ("speakers", "/speakers/Speakers"), ("network", "/network/Network"),
        ("general", "/general/General"), ("control", "/control/Control"),
        ("advanced", "/advanced/advanced"), ("home", "/Home"),
    ];

    /// <summary>
    /// The same sections, for the offline dump of a captured UI - which keeps them in
    /// one flat folder, so /audio/Audio is audio_Audio and /Home is Home. Shared so
    /// there is one list rather than two that drift.
    /// </summary>
    internal static IEnumerable<(string Section, string File)> Captured() =>
        Sections.Select(s => (s.Section, s.Prefix.TrimStart('/').Replace('/', '_')));

    private static string Url(string host, string path) => $"https://{host}:10443{path}";

    /// <summary>
    /// The receiver's own menu. Sections it will not describe are left out, and the
    /// caller falls back to the catalog for those rather than showing nothing.
    /// </summary>
    /// <summary>
    /// Read once per receiver and kept. These files do not change while the app is
    /// running, one of them is 700 KB, and asking for a dozen of them every time the
    /// Setup tab opens is exactly the kind of traffic that makes the receiver stop
    /// answering.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ConfigGroup>>
        Cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ConfigGroup>> ReadAsync(string host, CancellationToken ct)
    {
        if (Cache.TryGetValue(host, out var known)) return known;

        var groups = await ReadFreshAsync(host, ct);
        if (groups.Count > 0) Cache[host] = groups;
        return groups;
    }

    private async Task<IReadOnlyList<ConfigGroup>> ReadFreshAsync(string host, CancellationToken ct)
    {
        var words = await StringsAsync(host, ct);
        if (words.Count == 0) return [];

        var groups = new List<ConfigGroup>();

        // A string is only usable as a name if exactly one reads back to it; two
        // settings that normalise alike would be a coin toss between them.
        var byName = words.Values
            .Where(v => Normalise(v).Length > 0)
            .GroupBy(Normalise)
            .Where(g => g.Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (section, prefix) in Sections)
        {
            if (ct.IsCancellationRequested) break;

            var iface = await GetAsync(host, prefix + "ServerInterface.js", ct);
            if (iface is null) continue;

            var types = Types(iface);
            var settings = await GetAsync(host, prefix + "Settings.js", ct);
            var order = settings is null ? [] : Menu(settings);

            foreach (var constant in Order(types.Keys, order))
                groups.Add(new ConfigGroup(section, types[constant], Name(constant, byName)));
        }

        return groups;
    }

    /// <summary>
    /// The settings of a section in the order its menu lists them, with anything the
    /// menu does not mention after. The menu is only trusted for order - it is an
    /// array in minified code, and being wrong about it costs nothing worse than an
    /// odd running order.
    /// </summary>
    internal static IEnumerable<string> Order(IEnumerable<string> declared, IReadOnlyList<string> menu)
    {
        var left = new HashSet<string>(declared, StringComparer.Ordinal);

        foreach (var constant in menu)
            if (left.Remove(constant)) yield return constant;

        foreach (var constant in left.OrderBy(c => c, StringComparer.Ordinal))
            yield return constant;
    }

    /// <summary>
    /// What to call a setting. CONFIG_TVFORMAT and "TV Format" are the same word with
    /// the spaces taken out, so the receiver's own string table can name it: strip
    /// everything but letters and digits from both and require an exact match.
    ///
    /// This is not fuzzy matching - it either is the same word or it is not - and it
    /// reaches the sections whose menus cannot be read at all. Pairing the menu
    /// positionally named nothing in Audio, General or Network; this names most of
    /// them. Anything it cannot name keeps the receiver's own constant, which is
    /// ugly but true.
    /// </summary>
    internal static string Name(string constant, IReadOnlyDictionary<string, string> byName)
    {
        var bare = constant.StartsWith("CONFIG_", StringComparison.Ordinal) ? constant[7..] : constant;
        return byName.TryGetValue(Normalise(bare), out var word) ? word : Readable(bare);
    }

    internal static string Normalise(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (var c in text) if (char.IsLetterOrDigit(c)) kept.Append(char.ToUpperInvariant(c));
        return kept.ToString();
    }

    /// <summary>A last resort: SPEAKER_LAYOUT reads better than CONFIG_SPEAKER_LAYOUT.</summary>
    private static string Readable(string constant)
    {
        var words = constant.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 1 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w);
        return string.Join(' ', words);
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>CONFIG_SPEAKERCONFIG:"3" - the number this screen is asked for by.</summary>
    internal static Dictionary<string, int> Types(string js) =>
        ConfigConstant().Matches(js)
            .Where(m => int.TryParse(m.Groups["type"].Value, out _))
            // CONFIG_OPTION_* number the choices within a setting - Audyssey's MultEQ
            // is option 2 of setting 9 - and are not screens of their own. Listed as
            // screens they collide with real type numbers and ask for the wrong one.
            .Where(m => !m.Groups["name"].Value.StartsWith("CONFIG_OPTION", StringComparison.Ordinal))
            .GroupBy(m => m.Groups["name"].Value)
            .ToDictionary(g => g.Key, g => int.Parse(g.First().Groups["type"].Value));

    /// <summary>
    /// The order the menu lists its settings in - the longest array of config
    /// constants in the file.
    ///
    /// Only the order comes from here. Labels used to as well, paired positionally
    /// with a neighbouring array of string lookups, and that was worth abandoning:
    /// it named nothing at all in Audio, General and Network, and in General it
    /// matched an array of zone names and read back "Language -> ZONE2". Names now
    /// come from the string table by name, which cannot mispair.
    /// </summary>
    internal static IReadOnlyList<string> Menu(string js)
    {
        var longest = ConstantArray().Matches(js)
            .Select(m => ConfigName().Matches(m.Groups["body"].Value).Select(c => c.Value).ToList())
            .OrderByDescending(list => list.Count)
            .FirstOrDefault();

        return longest ?? [];
    }

    /// <summary>
    /// Every label the UI can show, in English.
    ///
    /// The file holds two dictionaries and the UI merges them, so a key present in
    /// both must take the non-empty one: read naively, "Speaker Config." comes back
    /// as an empty string.
    /// </summary>
    internal static Dictionary<string, string> Strings(string js)
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var table in new[] { "hashStrings_hd", "hashStrings_adv" })
        {
            var at = js.IndexOf(table + ":{", StringComparison.Ordinal);
            if (at < 0) continue;

            foreach (Match entry in Entry().Matches(js, at))
            {
                var english = Unescape(entry.Groups["first"].Value);
                if (english.Length > 0) words.TryAdd(entry.Groups["key"].Value, english);
            }
        }

        return words;
    }

    private static string Unescape(string value) =>
        value.Contains('\\') ? Escape().Replace(value, "$1") : value;

    private async Task<Dictionary<string, string>> StringsAsync(string host, CancellationToken ct)
    {
        var js = await GetAsync(host, "/LanguageStrings.js", ct);
        return js is null ? [] : Strings(js);
    }

    private Task<string?> GetAsync(string host, string path, CancellationToken ct) =>
        // In its turn, like every other call to the receiver.
        ReceiverGate.RunAsync($"{host}:10443", async () =>
        {
            try
            {
                return await Client.GetStringAsync(Url(host, path), ct);
            }
            catch (Exception ex)
            {
                log.LogDebug("Setup UI {Path} on {Host}: {Message}", path, host, ex.Message);
                return null;
            }
        }, text => text is null, ct);

    [GeneratedRegex(@"(?<name>CONFIG_[A-Z0-9_]+)\s*:\s*""(?<type>\d+)""")]
    private static partial Regex ConfigConstant();

    [GeneratedRegex(@"\[(?<body>(?:\s*\w+\.CONFIG_[A-Z0-9_]+\s*,){2,}\s*\w+\.CONFIG_[A-Z0-9_]+\s*)\]")]
    private static partial Regex ConstantArray();

    [GeneratedRegex(@"CONFIG_[A-Z0-9_]+")]
    private static partial Regex ConfigName();

    // getLanguage() has parentheses of its own, so the call cannot be matched with
    // a lazy "anything but a bracket".
    [GeneratedRegex(@"\[(?<body>(?:\s*str\.getString\(\s*(?:""A_\d+""|[\w.]+)\s*,\s*globalsSettings\.getLanguage\(\)\s*\)\s*,){2,}\s*str\.getString\(\s*(?:""A_\d+""|[\w.]+)\s*,\s*globalsSettings\.getLanguage\(\)\s*\)\s*)\]")]
    private static partial Regex LabelArray();

    [GeneratedRegex(@"str\.getString\(\s*(?:""(?<key>A_\d+)""|(?<key>[\w.]+))")]
    private static partial Regex StringKey();

    [GeneratedRegex(@"\b(?<key>A_\d+(?:_\d+)?)\s*:\s*\[\s*""(?<first>(?:[^""\\]|\\.)*)""")]
    private static partial Regex Entry();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex Escape();
}
