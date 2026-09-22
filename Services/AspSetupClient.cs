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

    /// <summary>
    /// Reads one screen, following the redirect some of them open with.
    ///
    /// Network's Connection and Settings frames are stubs: an empty form and a
    /// window.onload that sends the browser on to the page with the settings in it.
    /// Read without following that, they are pages with nothing on them - which is
    /// exactly how they looked.
    /// </summary>
    public async Task<AspPage?> ReadAsync(string host, string path, CancellationToken ct)
    {
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            var html = await GetAsync(host, path, ct);
            if (html is null) return null;

            var page = Parse(html);
            if (page is not null && page.Rows.Count > 0) return page;

            // Nothing on it: if it only points somewhere else, go there.
            var onward = Redirect().Match(html);
            if (!onward.Success) return page;

            path = Resolve(path, onward.Groups["to"].Value.Trim());
        }

        return null;
    }

    private const int MaxRedirects = 3;

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

    // ---------------------------------------------------------------- menus

    /// <summary>
    /// The screens this receiver actually has, read from its own menus rather than
    /// from a list kept here.
    ///
    /// A hand-written catalog was wrong within a day: it was built by guessing that a
    /// content frame is always named d_*.asp, and Network's Connection and Settings
    /// screens use r_network_setting_dhcp.asp, so they were silently absent. The
    /// receiver knows what it has and says so, so ask it.
    ///
    ///   /SETUP/f_home.asp          frameset; its left frame lists the sections
    ///   /SETUP/AUDIO/f_audio.asp   frameset; its right frame lists that menu
    /// </summary>
    public async Task<IReadOnlyList<AspGroup>> ReadMenusAsync(string host, CancellationToken ct)
    {
        var groups = new List<AspGroup>();

        foreach (var root in AspCatalog.Roots)
        {
            var sections = root.Section is { } fixedName
                ? [(fixedName, root.Path)]
                : await SectionsAsync(host, root.Path, ct);

            foreach (var (name, frameset) in sections)
            {
                if (ct.IsCancellationRequested) break;

                // The section's own menu is the right-hand frame of its frameset.
                var menu = await ContentFrameAsync(host, frameset, ct);
                if (menu is null) continue;

                foreach (var (label, path) in await LinksAsync(host, menu, ct))
                    groups.Add(new AspGroup(name, label, path));
            }
        }

        return groups;
    }

    private async Task<List<(string Name, string Path)>> SectionsAsync(
        string host, string homePath, CancellationToken ct)
    {
        // The home frameset's LEFT frame is the one listing the sections.
        var html = await GetAsync(host, homePath, ct);
        if (html is null) return [];

        var left = Frames(html).FirstOrDefault(f => f.Contains("d_left", StringComparison.OrdinalIgnoreCase));
        if (left is null) return [];

        var menu = Resolve(homePath, left);
        return (await LinksAsync(host, menu, ct)).Select(l => (l.Label, l.Path)).ToList();
    }

    /// <summary>
    /// The menu links, in the receiver's own order and words. Only links onward into
    /// the tree count: Back goes up, and config Save and Load do something rather
    /// than show something.
    /// </summary>
    private async Task<List<(string Label, string Path)>> LinksAsync(
        string host, string menuPath, CancellationToken ct)
    {
        var html = await GetAsync(host, menuPath, ct);
        if (html is null) return [];

        var found = new List<(string, string)>();
        foreach (Match link in Anchor().Matches(html))
        {
            var href = link.Groups["href"].Value.Trim();
            var label = Label(link.Groups["text"].Value);

            if (label.Length == 0 || href.Length == 0) continue;
            if (!href.Contains("/f_", StringComparison.OrdinalIgnoreCase)) continue;
            if (Unwanted().IsMatch(href)) continue;

            var path = Resolve(menuPath, href);
            if (found.All(f => f.Item2 != path)) found.Add((label, path));
        }

        return found;
    }

    /// <summary>
    /// The frame holding a screen's controls. A frameset has a left frame for its
    /// menu, the content frame, and a hidden third pointed at dummy.asp that the
    /// form posts into - so the content is the one that is neither.
    /// </summary>
    public async Task<string?> ContentFrameAsync(string host, string framesetPath, CancellationToken ct)
    {
        var html = await GetAsync(host, framesetPath, ct);
        if (html is null) return null;

        var src = Frames(html).FirstOrDefault(f =>
            !f.Contains("d_left", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("dummy", StringComparison.OrdinalIgnoreCase));

        return src is null ? null : Resolve(framesetPath, src);
    }

    private async Task<string?> GetAsync(string host, string path, CancellationToken ct)
    {
        try
        {
            return await Http.GetStringAsync(PageUrl(host, path), ct);
        }
        catch (Exception ex)
        {
            log.LogDebug("Setup page {Path} on {Host} failed: {Message}", path, host, ex.Message);
            return null;
        }
    }

    private static IEnumerable<string> Frames(string html) =>
        Frame().Matches(html).Select(m => m.Groups["src"].Value.Trim()).Where(s => s.Length > 0);

    /// <summary>Resolves a relative href against the page it was found on.</summary>
    internal static string Resolve(string from, string href)
    {
        if (href.StartsWith('/')) return href;

        var parts = new List<string>(from.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);   // drop the file

        foreach (var step in href.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (step == ".") continue;
            if (step == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(step);
        }

        return "/" + string.Join('/', parts);
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// The fields a browser would submit for this form, in document order.
    ///
    /// There is no need to discover whether the receiver accepts one field on its
    /// own: the page arrives with every field and its current value already in it,
    /// so the whole form can be reproduced exactly as the browser sends it. What
    /// matters instead is completeness - a field left out of a post is a field the
    /// receiver may read as cleared - so this follows the HTML rules for which
    /// controls are successful rather than only the ones the panel happens to draw.
    ///
    /// Buttons are excluded: these forms submit from JavaScript (form.submit()),
    /// which carries no button value, and type=button never submits regardless.
    /// </summary>
    public static List<KeyValuePair<string, string>> Fields(string html)
    {
        var body = FormBody(html);
        var fields = new List<KeyValuePair<string, string>>();
        var seenRadio = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match control in AnyControl().Matches(body))
        {
            // A disabled control is not submitted. The proxy port box on Network
            // Settings is disabled while the proxy is off, and sending it was the
            // one place this differed from what the browser does.
            if (Has(control.Value, "disabled")) continue;

            if (control.Groups["select"].Success)
            {
                var name = Attribute(control.Value, "name");
                if (name.Length == 0) continue;

                // An unselected select still submits - its first option.
                var options = Option().Matches(control.Groups["options"].Value);
                var chosen = options.FirstOrDefault(o => o.Groups["selected"].Success)
                             ?? options.FirstOrDefault();
                if (chosen is not null)
                    fields.Add(new(name, WebUtility.HtmlDecode(chosen.Groups["value"].Value)));
                continue;
            }

            var tag = control.Value;
            var type = Attribute(tag, "type");
            var field = Attribute(tag, "name");
            if (field.Length == 0) continue;

            switch (type.ToLowerInvariant())
            {
                // Only the checked one in a group is submitted.
                case "radio":
                    if (!Has(tag, "checked")) continue;
                    if (!seenRadio.Add(field)) continue;
                    fields.Add(new(field, WebUtility.HtmlDecode(Attribute(tag, "value"))));
                    break;

                // An unchecked box is not submitted at all.
                case "checkbox":
                    if (!Has(tag, "checked")) continue;
                    var box = Attribute(tag, "value");
                    fields.Add(new(field, WebUtility.HtmlDecode(box.Length > 0 ? box : "on")));
                    break;

                case "button" or "submit" or "reset" or "image" or "file":
                    continue;

                // text, hidden, password and anything unrecognised submit their value.
                default:
                    fields.Add(new(field, WebUtility.HtmlDecode(Attribute(tag, "value"))));
                    break;
            }
        }

        return fields;
    }

    /// <summary>The whole form as the browser would send it, with one field changed.</summary>
    internal static List<KeyValuePair<string, string>>? BuildPost(string html, string name, string value)
    {
        var fields = Fields(html);

        var at = fields.FindIndex(f => f.Key == name);
        // Refusing rather than appending: a name the page does not have means the
        // panel and the page have drifted, and inventing a field is how you set
        // something the receiver never offered.
        if (at < 0) return null;

        fields[at] = new(name, value);
        return fields;
    }

    /// <summary>
    /// Applies one change by posting the whole form, then reads the page back.
    /// Returns the page as it stands afterwards - which is the only honest answer
    /// to whether the change took.
    /// </summary>
    public async Task<AspPage?> WriteAsync(
        string host, string contentPath, string name, string value, CancellationToken ct)
    {
        var html = await GetAsync(host, contentPath, ct);
        if (html is null) return null;

        var fields = BuildPost(html, name, value);
        if (fields is null)
        {
            log.LogWarning("{Field} is not on {Path}; not posting", name, contentPath);
            return null;
        }

        // The form's action is relative to the page it is on; with none, it posts back.
        var action = ActionAttribute().Match(FormTag().Match(html).Value).Groups["action"].Value;
        var target = action.Length > 0 ? Resolve(contentPath, action) : contentPath;

        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await Http.PostAsync(PageUrl(host, target), content, ct);
            if (!response.IsSuccessStatusCode)
                log.LogWarning("Write to {Target} on {Host} returned {Status}",
                    target, host, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogWarning("Write to {Target} on {Host} failed: {Message}", target, host, ex.Message);
            return null;
        }

        return await ReadAsync(host, contentPath, ct);
    }

    private static string FormBody(string html)
    {
        var form = FormExtent().Match(html);
        return form.Success ? form.Groups["body"].Value : html;
    }

    private static string Attribute(string tag, string name) =>
        Regex.Match(tag, $@"\b{name}\s*=\s*['""](?<v>[^'""]*)['""]", RegexOptions.IgnoreCase)
            .Groups["v"].Value;

    private static bool Has(string tag, string name) =>
        Regex.IsMatch(tag, $@"\b{name}\b(?!\s*=)", RegexOptions.IgnoreCase);

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
            Action = ActionAttribute().Match(form.Groups["attrs"].Value).Groups["action"].Value,
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
                    label.Length > 0 ? label : (i + 1).ToString(), page.Locked,
                    // The column heading is shared down the table; this is the one
                    // control, and the only name a write can use.
                    Field: controls[i].Name));
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

    // A form with no action posts to the page it is on, and the redirect stubs are
    // written that way - requiring one made those pages parse as nothing at all.
    [GeneratedRegex(@"<form\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();

    [GeneratedRegex(@"action=['""](?<action>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex ActionAttribute();

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

    [GeneratedRegex(@"<form\b[^>]*>(?<body>.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FormExtent();

    /// <summary>Every input and select, in the order the page writes them.</summary>
    [GeneratedRegex(@"(?<select><select\b[^>]*>(?<options>.*?)</select>)|<input\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnyControl();

    [GeneratedRegex(@"location\.href\s*=\s*['""](?<to>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex Redirect();

    [GeneratedRegex(@"<frame\b[^>]*src=['""](?<src>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex Frame();

    [GeneratedRegex(@"<a\b[^>]*href=['""](?<href>[^'""]+)['""][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    /// <summary>Config save and load act rather than show; index.asp goes back out.</summary>
    [GeneratedRegex(@"/(SAVE|LOAD|RESTORE|INITIALIZE|UPDATE|FIRMWARE)/|index\.asp", RegexOptions.IgnoreCase)]
    private static partial Regex Unwanted();

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
