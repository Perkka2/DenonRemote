using System.Globalization;
using DenonRemote.Models;

namespace DenonRemote.Services;

public sealed record ApiInfo(string Name, string Version);
public sealed record ApiError(string Error);
public sealed record ApiZone(int Zone, string Name, bool? Power, double? Volume, bool? Mute, string? Source, string? SourceName);
public sealed record ApiSlot(int Slot, string Name);
public sealed record ApiScene(string Id, string Name);

public sealed record ApiReceiver(
    string Id,
    string Name,
    string Model,
    string Host,
    bool Online,
    bool Asleep,
    bool? Power,
    string? SoundMode,
    bool NightMode,
    double MaxVolume,
    int? SleepMinutes,
    int? QuickSelect,
    IReadOnlyList<ApiZone> Zones,
    IReadOnlyList<SourceOption> Sources,
    IReadOnlyList<ApiSlot> QuickSelects,
    IReadOnlyList<ApiScene> Scenes);

/// <summary>
/// A small HTTP API for the things a button, a shortcut or a cron job wants: power,
/// volume, input, sound mode, Quick Select and scenes. See docs/api.md.
///
/// It drives the same <see cref="DenonClient"/> the pages do, so a call shares the
/// one socket per receiver, is verified the same way, shows up on every open page,
/// and cannot pass the volume ceiling.
///
/// There is deliberately no raw-command route. Nothing here is authenticated, and
/// the protocol console is one tap away for whoever is actually at the app.
/// </summary>
public static class RemoteApi
{
    public static void MapRemoteApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            return SameOrigin(request.Headers.Origin, request.Host.Value)
                ? await next(context)
                : Error(403, "cross-site requests are not accepted");
        });

        api.MapGet("/", () => new ApiInfo("Denon Remote", StartupOptions.Version));

        api.MapGet("/receivers", (DenonRegistry registry, SceneStore scenes) =>
            registry.Clients.Select(client => Describe(client, scenes)));

        // Without this an unknown route falls through to the Blazor page, and a
        // script gets a screenful of HTML with a 200 on it.
        api.MapFallback(() => Error(404, "no such route - see docs/api.md"));

        var receiver = api.MapGroup("/receivers/{key}");

        receiver.MapGet("/", (string key, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, _ => null, command: false));

        receiver.MapPost("/power", async (string key, string? on, DenonRegistry registry, SceneStore scenes) =>
        {
            if (Find(registry.Clients, key) is not { } client) return NoReceiver(key);
            if (client.HeosOnly) return NoSocket(client);
            if (Switch(on, client.State.Power) is not { } wanted) return BadSwitch;

            if (client.State.Online) client.SetStandby(wanted);
            // In eco standby the control port is shut; only the HTTP poke wakes it.
            else if (wanted) await client.WakeAsync();
            else return Offline(client);

            return Results.Ok(Describe(client, scenes));
        });

        receiver.MapPost("/sound-mode", (string key, string? code, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                var mode = DenonCatalog.SoundModes.FirstOrDefault(m => Same(m.Code, code) || Same(m.Label, code));
                if (mode is null)
                    return Error(400, "code must be one of: " + string.Join(", ", DenonCatalog.SoundModes.Select(m => m.Code)));

                client.SetSoundMode(mode.Code);
                return null;
            }));

        receiver.MapPost("/quick-select/{slot:int}", (string key, int slot, int? zone, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                if (slot is < 1 or > 4) return Error(400, "slot must be 1 to 4");
                if (!client.Zones.Contains(zone ?? 1)) return NoZone(client, zone ?? 1);

                client.SelectQuick(slot, zone ?? 1);
                return null;
            }));

        receiver.MapPost("/night-mode", (string key, string? on, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                if (Switch(on, client.NightMode) is not { } wanted) return BadSwitch;
                client.SetNightMode(wanted);
                return null;
            }));

        receiver.MapPost("/sleep", (string key, int? minutes, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                if (minutes is null or < 0 or > 120) return Error(400, "minutes must be 0 (off) to 120");
                client.SetSleep(minutes);
                return null;
            }));

        receiver.MapGet("/scenes", (string key, DenonRegistry registry, SceneStore scenes) =>
            Find(registry.Clients, key) is { } client
                ? Results.Ok(scenes.For(client.Config.Id).Select(s => new ApiScene(s.Id, s.Name)))
                : NoReceiver(key));

        receiver.MapPost("/scenes/{scene}", (string key, string scene, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                var mine = scenes.For(client.Config.Id);
                var wanted = mine.FirstOrDefault(s => s.Id == scene) ?? mine.FirstOrDefault(s => Same(s.Name, scene));
                if (wanted is null) return Error(404, $"{client.Config.Name} has no scene '{scene}'");

                client.RunScene(wanted);
                return null;
            }));

        var zones = receiver.MapGroup("/zones/{zone:int}");

        zones.MapPost("/power", (string key, int zone, string? on, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                if (!client.Zones.Contains(zone)) return NoZone(client, zone);
                if (Switch(on, client.State.Zone(zone).Power) is not { } wanted) return BadSwitch;

                client.SetZonePower(zone, wanted);
                return null;
            }));

        zones.MapPost("/mute", (string key, int zone, string? on, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                if (!client.Zones.Contains(zone)) return NoZone(client, zone);
                if (Switch(on, client.State.Zone(zone).Mute) is not { } wanted) return BadSwitch;

                client.SetZoneMute(zone, wanted);
                return null;
            }));

        zones.MapPost("/volume", (string key, int zone, string? value, string? step, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                if (!client.Zones.Contains(zone)) return NoZone(client, zone);

                if (value is not null)
                {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var volume)
                        || !double.IsFinite(volume))
                        return Error(400, "value must be a number on the front-panel scale");

                    client.SetZoneVolume(zone, volume);
                    return null;
                }

                if (Same(step, "up")) client.StepZoneVolume(zone, 1);
                else if (Same(step, "down")) client.StepZoneVolume(zone, -1);
                else return Error(400, "pass value=<number> or step=up|down");
                return null;
            }));

        zones.MapPost("/source", (string key, int zone, string? code, DenonRegistry registry, SceneStore scenes) =>
            Run(registry, scenes, key, client =>
            {
                if (!client.Zones.Contains(zone)) return NoZone(client, zone);

                var sources = client.SourcesFor(zone);
                if (Source(sources, code) is not { } source)
                    return Error(400, "code must be one of: " + string.Join(", ", sources.Select(s => s.Code)));

                client.SetZoneSource(zone, source.Code);
                return null;
            }));
    }

    /// <summary>
    /// Finds the receiver, refuses a command it could not carry out, and answers with
    /// the state as it now stands. That state is what was asked for, not yet what the
    /// receiver confirmed: an unconfirmed change is reverted a moment later, exactly
    /// as it is on the pages.
    /// </summary>
    private static IResult Run(
        DenonRegistry registry, SceneStore scenes, string key, Func<DenonClient, IResult?> act, bool command = true)
    {
        if (Find(registry.Clients, key) is not { } client) return NoReceiver(key);

        if (command && client.HeosOnly) return NoSocket(client);
        if (command && !client.State.Online) return Offline(client);

        return act(client) ?? Results.Ok(Describe(client, scenes));
    }

    private static IResult Error(int status, string message) =>
        Results.Json(new ApiError(message), statusCode: status);

    private static IResult NoReceiver(string key) => Error(404, $"no receiver '{key}'");

    private static IResult NoZone(DenonClient client, int zone) =>
        Error(404, $"{client.Config.Name} has no zone {zone}");

    private static IResult NoSocket(DenonClient client) =>
        Error(409, $"{client.Config.Name} is a HEOS speaker, not a receiver");

    private static IResult Offline(DenonClient client) => Error(503, client.Asleep
        ? $"{client.Config.Name} is in eco standby - POST power?on=true to wake it"
        : $"{client.Config.Name} is not connected");

    private static IResult BadSwitch => Error(400, "on must be true, false or toggle");

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- the parts worth checking

    /// <summary>A receiver by id, or failing that by the name the user gave it.</summary>
    internal static DenonClient? Find(IEnumerable<DenonClient> clients, string? key)
    {
        var all = clients.ToList();
        return all.FirstOrDefault(c => c.Config.Id == key) ?? all.FirstOrDefault(c => Same(c.Config.Name, key));
    }

    /// <summary>
    /// true / false / toggle, with on / off / 1 / 0 accepted too. Null means it was
    /// none of them. Toggling a state the receiver has not reported yet turns it on.
    /// </summary>
    internal static bool? Switch(string? wanted, bool? current) => wanted?.Trim().ToLowerInvariant() switch
    {
        "true" or "on" or "1" => true,
        "false" or "off" or "0" => false,
        "toggle" => current != true,
        _ => null,
    };

    /// <summary>An input by protocol code, or by the name it was given on the receiver.</summary>
    internal static SourceOption? Source(IReadOnlyList<SourceOption> sources, string? wanted) =>
        sources.FirstOrDefault(s => Same(s.Code, wanted)) ?? sources.FirstOrDefault(s => Same(s.Label, wanted));

    /// <summary>
    /// A browser names the page a request came from; curl, Shortcuts and the like
    /// send no Origin at all. Refusing a foreign one is what stops any web page the
    /// household visits from turning the volume up - these routes take no body and
    /// no special header, so the browser would otherwise send them without asking.
    /// </summary>
    internal static bool SameOrigin(string? origin, string? host)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var from) && Same(from.Authority, host);
    }

    internal static ApiReceiver Describe(DenonClient client, SceneStore scenes) =>
        Describe(client, scenes.For(client.Config.Id));

    internal static ApiReceiver Describe(DenonClient client, IReadOnlyList<Scene> scenes)
    {
        var state = client.State;

        return new ApiReceiver(
            client.Config.Id,
            client.Config.Name,
            client.Profile.Model.Length > 0 ? client.Profile.Model : client.Config.Model,
            client.Config.Host,
            client.Reachable,
            client.Asleep,
            state.Power,
            state.SoundMode,
            client.NightMode,
            client.EffectiveMax,
            state.SleepMinutes,
            state.QuickSelect,
            client.HeosOnly ? [] : client.Zones.Select(zone =>
            {
                var z = state.Zone(zone);
                return new ApiZone(zone, client.Profile.ZoneName(zone), z.Power, z.Volume, z.Mute, z.Source,
                    client.SourcesFor(zone).FirstOrDefault(s => s.Code == z.Source)?.Label);
            }).ToList(),
            client.HeosOnly ? [] : client.Profile.Sources,
            client.HeosOnly ? [] : Enumerable.Range(1, 4).Select(slot => new ApiSlot(slot, client.Profile.QuickSelectName(slot))).ToList(),
            scenes.Select(s => new ApiScene(s.Id, s.Name)).ToList());
    }
}
