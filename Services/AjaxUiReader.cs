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

    /// <summary>The sections the UI is split into, and the file names they use.</summary>
    private static readonly (string Path, string Name)[] Sections =
    [
        ("speakers", "Speakers"), ("audio", "Audio"), ("inputs", "Inputs"),
        ("video", "Video"), ("general", "General"), ("network", "Network"),
    ];

    private static string Url(string host, string path) => $"https://{host}:10443{path}";

    /// <summary>
    /// The receiver's own menu. Sections it will not describe are left out, and the
    /// caller falls back to the catalog for those rather than showing nothing.
    /// </summary>
    public async Task<IReadOnlyList<ConfigGroup>> ReadAsync(string host, CancellationToken ct)
    {
        var words = await StringsAsync(host, ct);
        if (words.Count == 0) return [];

        var groups = new List<ConfigGroup>();

        foreach (var (path, name) in Sections)
        {
            if (ct.IsCancellationRequested) break;

            var iface = await GetAsync(host, $"/{path}/{name}ServerInterface.js", ct);
            var settings = await GetAsync(host, $"/{path}/{name}Settings.js", ct);
            if (iface is null || settings is null) continue;

            var types = Types(iface);
            foreach (var (constant, key) in Menu(settings))
            {
                if (!types.TryGetValue(constant, out var type)) continue;
                if (!words.TryGetValue(key, out var label) || label.Length == 0) continue;

                groups.Add(new ConfigGroup(path, type, label));
            }
        }

        return groups;
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>CONFIG_SPEAKERCONFIG:"3" - the number this screen is asked for by.</summary>
    internal static Dictionary<string, int> Types(string js) =>
        ConfigConstant().Matches(js)
            .Where(m => int.TryParse(m.Groups["type"].Value, out _))
            .GroupBy(m => m.Groups["name"].Value)
            .ToDictionary(g => g.Key, g => int.Parse(g.First().Groups["type"].Value));

    /// <summary>
    /// The menu: an array of config constants with an array of label lookups beside
    /// it, one for one.
    ///
    /// Pairing them positionally is only safe with the length check. Without it the
    /// General section matched a nearby array of zone names and read back
    /// "Language → ZONE2" - confident, wrong, and impossible to notice from the UI.
    /// Where nothing lines up this returns nothing and the caller falls back.
    /// </summary>
    internal static IReadOnlyList<(string Constant, string Key)> Menu(string js)
    {
        foreach (Match consts in ConstantArray().Matches(js))
        {
            var names = ConfigName().Matches(consts.Groups["body"].Value)
                .Select(m => m.Value).ToList();

            foreach (Match labels in LabelArray().Matches(js, consts.Index + consts.Length))
            {
                // Only the array that belongs to this one, not any array later on.
                if (labels.Index - (consts.Index + consts.Length) > NearbyChars) break;

                var keys = StringKey().Matches(labels.Groups["body"].Value)
                    .Select(m => m.Groups["key"].Value).ToList();

                if (keys.Count == names.Count)
                    return [.. names.Zip(keys)];
            }
        }

        return [];
    }

    private const int NearbyChars = 4000;

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

    private async Task<string?> GetAsync(string host, string path, CancellationToken ct)
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
    }

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
