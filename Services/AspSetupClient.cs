using System.Net;
using System.Text.RegularExpressions;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// The setup tree on the pre-HEOS receivers (AVR-X4100W and kin).
///
/// Those units have no config API - the whole /ajax/ surface is refused - and serve
/// a frame-based ASP tree instead, one page per settings screen. The pages are
/// rendered by the receiver with the current state already in them, which makes
/// them richer than the newer generation's API: the values are words rather than
/// numeric codes, so nothing has to be harvested to translate them.
///
///   GET  /SETUP/SPEAKERS/SPEAKERCONFIG/d_speakersetup.asp    the screen
///   POST /SETUP/SPEAKERS/SPEAKERCONFIG/s_speakersetup.asp    its form
///
/// This class only reads. The s_*.asp endpoints are where the UI applies a change,
/// and whether they accept a partial post - one field rather than the whole form -
/// is not something to find out by guessing on someone's receiver.
/// </summary>
public sealed partial class AspSetupClient(ILogger<AspSetupClient> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static string PageUrl(string host, string path) => $"http://{host}{path}";

    public async Task<AspPage?> ReadAsync(string host, string path, CancellationToken ct)
    {
        try
        {
            var html = await Http.GetStringAsync(PageUrl(host, path), ct);
            return Parse(html);
        }
        catch (Exception ex)
        {
            log.LogDebug("Setup page {Path} on {Host} failed: {Message}", path, host, ex.Message);
            return null;
        }
    }

    /// <summary>True when this receiver serves the ASP setup tree at all.</summary>
    public async Task<bool> ProbeAsync(string host, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(PageUrl(host, "/SETUP/f_home.asp"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogDebug("No ASP setup tree on {Host}: {Message}", host, ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// Reads one rendered settings page. Regex rather than a parser because the app
    /// takes no third-party packages, and because these pages are machine-generated
    /// by firmware that has not changed since 2014 - the shapes here are the shapes.
    /// </summary>
    public static AspPage? Parse(string html)
    {
        var form = FormTag().Match(html);
        if (!form.Success) return null;

        var page = new AspPage
        {
            Title = Label(Title().Match(html).Groups[1].Value),
            Help = Text(Help().Match(html).Groups[1].Value),
            Action = form.Groups["action"].Value,
        };

        // Two hidden fields guard every page: the receiver refuses to change anything
        // in Pure Direct, and Setup Lock refuses the lot. Its own UI redirects to an
        // explaining page rather than letting a control be touched.
        if (Hidden(html, "setPureDirectOn")) page.LockedBy = "Pure Direct is on";
        else if (Hidden(html, "setSetupLock")) page.LockedBy = "Setup Lock is on";

        string[] columns = [];

        foreach (Match row in Rows().Matches(Normalise(html)))
        {
            var cells = SplitCells(row.Value);
            if (cells.Count == 0) continue;

            var label = Label(cells[0]);
            var controls = cells.Skip(1).SelectMany(ReadControls).ToList();

            if (controls.Count == 0)
            {
                // A header row is the one with an empty corner cell: nothing above the
                // row labels, then a name per column. Requiring that corner matters -
                // Hide Sources has a row reading "CBL/SAT | ZONE 2", two plain cells
                // and no controls, and taking it for a header turned every source
                // below it into a cell of a table that does not exist.
                var named = cells.Skip(1).Select(Label).Where(t => t.Length > 0).ToList();
                if (label.Length == 0 && named.Count > 1) columns = [.. named];
                else if (label.Length > 0) page.Headings.Add(label);
                continue;
            }

            // A table is a table because the page drew a header row for one. Several
            // controls on a row is not enough on its own: "Power On Level" is a radio
            // group and a text box side by side, one setting in two parts, and reading
            // that as a two-column table listed it twice with neither part named.
            // ...and a row belongs to that table only if it has a control per column.
            if (columns.Length < 2 || controls.Count != columns.Length)
            {
                var first = controls[0];
                page.Rows.Add(new ConfigRow(
                    first.Name, label.Length > 0 ? label : Pretty(first.Name),
                    first.Value, first.Options, null, page.Locked));

                // Anything beside it is that setting's companion - the box that holds
                // the number when the radio says "a specific level".
                foreach (var extra in controls.Skip(1).Where(c => c.Value.Length > 0))
                    page.Rows.Add(new ConfigRow(
                        extra.Name, $"{label} value", extra.Value, extra.Options, null, page.Locked));
                continue;
            }

            for (var i = 0; i < controls.Count; i++)
            {
                var name = i < columns.Length ? columns[i] : Pretty(controls[i].Name);
                page.Rows.Add(new ConfigRow(
                    name, name, controls[i].Value, controls[i].Options,
                    label.Length > 0 ? label : (i + 1).ToString(), page.Locked));
            }
        }

        return page;
    }

    private sealed record Control(string Name, string Value, IReadOnlyList<ConfigChoice> Options);

    /// <summary>
    /// The controls in one table cell. A radio group is one setting however many
    /// buttons it has, so the buttons are folded together by their shared name.
    /// </summary>
    private static IEnumerable<Control> ReadControls(string cell)
    {
        var found = new List<Control>();

        foreach (Match select in Select().Matches(cell))
        {
            var options = new List<ConfigChoice>();
            var value = "";
            foreach (Match option in Option().Matches(select.Groups["body"].Value))
            {
                var code = WebUtility.HtmlDecode(option.Groups["value"].Value).Trim();
                options.Add(new ConfigChoice(code, Text(option.Groups["text"].Value) is { Length: > 0 } t ? t : code));
                if (option.Groups["selected"].Success) value = code;
            }

            found.Add(new Control(select.Groups["name"].Value, value, options));
        }

        var radios = new Dictionary<string, (string Value, List<ConfigChoice> Options)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Match radio in Radio().Matches(cell))
        {
            var name = radio.Groups["name"].Value;
            if (!radios.TryGetValue(name, out var group))
            {
                group = ("", []);
                radios[name] = group;
                order.Add(name);
            }

            var code = WebUtility.HtmlDecode(radio.Groups["value"].Value).Trim();
            group.Options.Add(new ConfigChoice(code, Text(radio.Groups["text"].Value) is { Length: > 0 } t ? t : code));
            // "checked" is a bare attribute after the value, so it rides in `rest`.
            if (radio.Groups["rest"].Value.Contains("checked", StringComparison.OrdinalIgnoreCase))
                radios[name] = (code, group.Options);
        }

        foreach (var name in order)
            found.Add(new Control(name, radios[name].Value, radios[name].Options));

        // Channel levels are sliders. The slider itself is unnamed - it drives a
        // hidden field beside it, and that hidden field is what the form posts - so
        // the value comes off the slider and the name off its companion.
        foreach (Match slider in Slider().Matches(cell))
            found.Add(new Control(slider.Groups["name"].Value,
                WebUtility.HtmlDecode(slider.Groups["value"].Value).Trim(), []));

        // A text box has no choices, so it reads as a fact rather than a dropdown.
        foreach (Match text in TextBox().Matches(cell))
            found.Add(new Control(text.Groups["name"].Value,
                WebUtility.HtmlDecode(text.Groups["value"].Value).Trim(), []));

        return found;
    }

    /// <summary>
    /// Source Level opens its table with a cell and no row - "&lt;TABLE&gt;&lt;TD&gt;" - which
    /// browsers forgive and a row-based reader does not: the page came back with no
    /// settings at all. One implied row is put back where the markup left it out.
    /// </summary>
    internal static string Normalise(string html) =>
        TableThenCell().Replace(html, "$1<TR>$2");

    /// <summary>Cells, without needing a real parser: a row is its TD boundaries.</summary>
    internal static List<string> SplitCells(string row)
    {
        var cells = new List<string>();
        var matches = CellStart().Matches(row);
        for (var i = 0; i < matches.Count; i++)
        {
            var from = matches[i].Index + matches[i].Length;
            var to = i + 1 < matches.Count ? matches[i + 1].Index : row.Length;
            cells.Add(row[from..to]);
        }
        return cells;
    }

    /// <summary>Visible text, tags and entities resolved. Nothing else removed.</summary>
    internal static string Text(string html)
    {
        var bare = WebUtility.HtmlDecode(Tags().Replace(html, " "));
        return Whitespace().Replace(bare, " ").Trim();
    }

    /// <summary>
    /// A setting's name. The pages indent sub-settings with non-breaking spaces and a
    /// leading dash - " -Power On Default" - which is layout, not the name.
    ///
    /// Only labels get this. A value may legitimately start with a minus, and taking
    /// it off turned the volume limit's -20dB into 20dB: a plausible-looking number
    /// forty decibels from the truth.
    /// </summary>
    internal static string Label(string html) => Text(html).TrimStart('-', ' ').Trim();

    private static string Pretty(string name) =>
        Label(NamePrefix().Replace(name, "")) is { Length: > 0 } stripped ? stripped : name;

    private static bool Hidden(string html, string name) =>
        Regex.IsMatch(html, $@"name=['""]{Regex.Escape(name)}['""]\s+value=['""]ON['""]",
            RegexOptions.IgnoreCase);

    [GeneratedRegex(@"<form[^>]*action=['""](?<action>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();

    [GeneratedRegex(@"<div class=[""']Title[""']>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();

    [GeneratedRegex(@"<div class=[""']HelpText[""']>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Help();

    [GeneratedRegex(@"<tr\b.*?(?=<tr\b|</table)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Rows();

    [GeneratedRegex(@"<td\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellStart();

    [GeneratedRegex(@"<select[^>]*name=['""](?<name>[^'""]+)['""][^>]*>(?<body>.*?)</select>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Select();

    [GeneratedRegex(@"<option\s+value=['""](?<value>[^'""]*)['""](?<selected>[^>]*\bselected\b)?[^>]*>(?<text>.*?)</option>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Option();

    [GeneratedRegex(@"<input[^>]*type=['""]radio['""][^>]*name=['""](?<name>[^'""]+)['""][^>]*value=['""](?<value>[^'""]*)['""](?<rest>[^>]*)>(?<text>[^<]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Radio();

    [GeneratedRegex(@"<input[^>]*type=['""]text['""][^>]*name=['""](?<name>[^'""]+)['""][^>]*value=['""](?<value>[^'""]*)['""]",
        RegexOptions.IgnoreCase)]
    private static partial Regex TextBox();

    [GeneratedRegex(@"<input[^>]*type=['""]range['""][^>]*value=['""](?<value>[^'""]*)['""][^>]*>.*?name=['""](?<name>[^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Slider();

    [GeneratedRegex(@"(<table\b[^>]*>)(\s*)(?=<td\b)", RegexOptions.IgnoreCase)]
    private static partial Regex TableThenCell();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(radio|list|text|btn|chk)", RegexOptions.IgnoreCase)]
    private static partial Regex NamePrefix();
}

/// <summary>One rendered settings screen from the pre-HEOS setup tree.</summary>
public sealed class AspPage
{
    /// <summary>The receiver's own title, such as "Speakers/Speaker Config.".</summary>
    public string Title { get; set; } = "";

    /// <summary>Its own one-line explanation of the screen.</summary>
    public string Help { get; set; } = "";

    /// <summary>Where its form posts - not used while this is read-only.</summary>
    public string Action { get; set; } = "";

    /// <summary>Why nothing on this page can be changed right now, if anything.</summary>
    public string? LockedBy { get; set; }

    public bool Locked => LockedBy is not null;

    /// <summary>Section headings the page draws between its settings.</summary>
    public List<string> Headings { get; } = [];

    public List<ConfigRow> Rows { get; } = [];
}
