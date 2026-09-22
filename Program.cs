using System.Diagnostics;
using DenonRemote.Services;

var options = StartupOptions.Parse(args);

// Static files are resolved relative to the content root, which defaults to the
// working directory - so launching the executable by path, or double-clicking it,
// would leave the app unable to find wwwroot. Fall back to the folder the
// executable lives in, while keeping the project directory during development so
// edits to site.css show up without a rebuild.
var contentRoot = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"))
    ? Directory.GetCurrentDirectory()
    : AppContext.BaseDirectory;

if (options.Unknown.Count > 0)
{
    Console.Error.WriteLine($"  Unrecognised: {string.Join(", ", options.Unknown)}");
    Console.Error.WriteLine(StartupOptions.Usage);
    return 2;
}

if (options.SelfTest) return SelfTest.Run();

if (options.ReadUi is { } uiDir)
{
    var words = AjaxUiReader.Strings(await File.ReadAllTextAsync(Path.Combine(uiDir, "LanguageStrings.js")));
    Console.WriteLine($"  {words.Count} strings\n");

    foreach (var (path, name) in new[] { ("speakers", "Speakers"), ("audio", "Audio"),
        ("inputs", "Inputs"), ("video", "Video"), ("general", "General"), ("network", "Network") })
    {
        var iface = Path.Combine(uiDir, $"{path}_{name}ServerInterface.js");
        var settings = Path.Combine(uiDir, $"{path}_{name}Settings.js");
        if (!File.Exists(iface) || !File.Exists(settings)) continue;

        var types = AjaxUiReader.Types(await File.ReadAllTextAsync(iface));
        var menu = AjaxUiReader.Menu(await File.ReadAllTextAsync(settings));
        var byName = words.Values
            .Where(v => AjaxUiReader.Normalise(v).Length > 0)
            .GroupBy(AjaxUiReader.Normalise)
            .Where(g => g.Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        Console.WriteLine($"=== {path}: {types.Count} settings");
        foreach (var constant in AjaxUiReader.Order(types.Keys, menu))
            Console.WriteLine($"    {types[constant],3}  {AjaxUiReader.Name(constant, byName)}");
        Console.WriteLine();
    }
    return 0;
}

if (options.ShowPost is { } postFile)
{
    foreach (var field in AspSetupClient.Fields(await File.ReadAllTextAsync(postFile)))
        Console.WriteLine($"{field.Key}={field.Value}");
    return 0;
}

if (options.ReadPages is { } pageDir)
{
    foreach (var file in Directory.GetFiles(pageDir, "*.asp").OrderBy(f => f))
    {
        var page = AspSetupClient.Parse(await File.ReadAllTextAsync(file));
        if (page is null) { Console.WriteLine($"-- {Path.GetFileName(file)}: no form"); continue; }

        Console.WriteLine($"\n=== {page.Title}   [{Path.GetFileName(file)}]");
        if (page.LockedBy is { } why) Console.WriteLine($"    locked: {why}");
        foreach (var row in page.Rows)
        {
            var where = row.Index is null ? "" : $"[{row.Index}] ";
            var choices = row.Options.Count == 0 ? "(text)" : string.Join(", ", row.Options.Select(o => o.Text));
            Console.WriteLine($"    {where}{row.Label,-26} = {row.Value,-12} of {choices}");
        }
    }
    return 0;
}

if (options.Probe is { } sweepTarget)
{
    using var sweepLog = LoggerFactory.Create(b => b.AddSimpleConsole());
    var local = Path.Combine(Directory.GetCurrentDirectory(), "discovery");
    var into = Directory.Exists(local) ? local : AppPaths.Ensure(AppPaths.Discovery);

    var sweeper = new HttpProbe(sweepLog.CreateLogger<HttpProbe>());
    var sweepReport = await sweeper.SweepAsync(sweepTarget, into, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine($"  Report written to {sweepReport}");
    return 0;
}

if (options.FetchUi is { } target)
{
    using var probeLog = LoggerFactory.Create(b => b.AddSimpleConsole());
    // Captures belong with the others. Running from the project, that is its own
    // discovery/ folder; a published build has no such folder and falls back to the
    // profile directory, where everything else the app writes lives.
    var local = Path.Combine(Directory.GetCurrentDirectory(), "discovery");
    var into = Directory.Exists(local) ? local : AppPaths.Ensure(AppPaths.Discovery);

    var probe = new HttpProbe(probeLog.CreateLogger<HttpProbe>());
    var report = await probe.FetchUiAsync(target, into, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine(await File.ReadAllTextAsync(report));
    return 0;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
});

// Bind to every interface so phones on the same wifi can reach it. A caller that
// passes --urls gets what they asked for instead.
if (!options.UrlsOverridden)
    builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(serverOptions =>
{
    serverOptions.DetailedErrors = builder.Environment.IsDevelopment();

    // A backgrounded tab or a sleeping phone drops the socket. Keep its circuit
    // around long enough to pick the page back up exactly where it was, instead of
    // the three minutes that has it expire while you make a cup of tea.
    serverOptions.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(2);
    serverOptions.DisconnectedCircuitMaxRetained = 50;
})
.AddHubOptions(hub =>
{
    // Tolerate a phone that stalls the connection for a while before it drops.
    hub.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
    hub.HandshakeTimeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<ReceiverStore>();
builder.Services.AddSingleton<SceneStore>();
builder.Services.AddSingleton<DeviceProfileReader>();
builder.Services.AddSingleton<DenonRegistry>();
builder.Services.AddSingleton<SsdpDiscovery>();
builder.Services.AddSingleton<HttpProbe>();
builder.Services.AddSingleton<GraphicEqClient>();
builder.Services.AddSingleton<AjaxConfigClient>();
builder.Services.AddSingleton<ReceiverInfoReader>();
builder.Services.AddSingleton<AjaxUiReader>();
builder.Services.AddHostedService<RegistryHostedService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await app.StartAsync();

Console.WriteLine(options.Describe());

if (options.OpenBrowser) OpenBrowser($"http://localhost:{options.Port}");

await app.WaitForShutdownAsync();
return 0;

// A published build is double-clicked, so put the page in front of the person.
static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  (Couldn't open a browser automatically: {ex.Message})");
    }
}
