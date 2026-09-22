# Notes for whoever maintains this

## Layout

```
Program.cs                      DI + Blazor Server wiring, startup flags
Services/
  DenonClient.cs                one receiver: socket, send queue, protocol parsing
  DenonRegistry.cs              singleton owning one client per receiver
  DeviceProfileReader.cs        model, zone count, real input names, HEOS probe
  HeosClient.cs                 HEOS command line: now playing and transport
  NetAudioClient.cs             the pre-HEOS network player
  GraphicEqClient.cs            graphic EQ: ASP form post and ajax config backends
  AjaxConfigClient.cs           the HEOS-generation setup API
  AjaxUiReader.cs               what that receiver's own setup UI says it has
  AspSetupClient.cs             the pre-HEOS setup tree
  ReceiverGate.cs               request pacing — read this before adding traffic
  HttpProbe.cs                  HTTP probing, discovery sweep, setup-UI capture
  SsdpDiscovery.cs              M-SEARCH + UPnP description parsing, direct probe
  ReceiverStore.cs / SceneStore.cs / AppPaths.cs
  StartupOptions.cs             --port / --no-browser / --self-test / --probe
  SelfTest.cs                   every check, in one file
Models/                         state, catalogs, surround parameter specs
Pages/                          Index (Remote…Setup), Setup, Console
Components/                     zone panel, grids, sliders, menu pad, now playing
publish.sh                      self-contained builds for every platform
.github/workflows/release.yml   the same on tag, attached to a release
```

## Checks

```bash
dotnet run -- --self-test
```

A flag on the app rather than a test project, so it needs no packages and runs
anywhere the app runs. `publish.sh` and the release workflow both run it before
building.

It covers the parts that have actually had bugs: protocol line parsing across all the
prefixes, zone and parameter handling, command encoding and the volume ceiling, both
graphic-EQ payload shapes, the setup API's XML, the pre-HEOS setup pages (using
fragments of the real markup, verbatim), the network player's two screen modes, and
the request pacing.

The pacing checks watch the gate's waits through an injectable sleep rather than
sitting through fourteen seconds of backoff — and rather than asserting against a
second copy of the timing rules written out in the test, which is how an earlier check
came to be confidently wrong about the crossover.

Beyond the self-test, the app is driven through headless Chromium against Python
stand-ins that speak the control protocol, the HEOS command line and the HTTP
endpoints, including the real captured setup pages from both receivers.

## Building and releasing

```bash
./publish.sh                 # every target, into dist/
./publish.sh osx-arm64       # just one
```

Each zip holds a folder: the executable with the .NET runtime beside it, `wwwroot` and
`appsettings.json`. The recipient installs nothing. About 90 MB per platform. Targets:
`osx-arm64`, `osx-x64`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`.

Tag a release and GitHub Actions does the same on native runners, with ReadyToRun for
quicker startup, and attaches the zips:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

Three deliberate choices in there:

* **Not single-file.** Bundling the native libraries makes the app extract them to a
  temp directory at startup, and on macOS that produced an `AccessViolationException`
  inside a socket call before the app could start. A plain folder is larger, boring,
  and works.
* **No trimming.** Blazor's reflection doesn't survive it.
* **`--self-contained` on the publish command**, not in the csproj, so `dotnet run`
  stays fast during development.

The packaging step is split by OS: `zip` everywhere except Windows, which has no `zip`
on the runner, and `Compress-Archive` there. They are not interchangeable —
`Compress-Archive` drops the executable bit, so it can't be used for the Unix targets.

### What the recipient sees

Unzip, run `DenonRemote` inside the folder, and a browser opens at the app. The console
prints the address to use from a phone. `--port 5090` moves it; `--no-browser` stops it
opening one. Settings live in `~/.denonremote`, never beside the executable, so a build
in `/Applications` or `Program Files` still works.

Three things to warn people about:

* **macOS** — the build is unsigned, so Gatekeeper quarantines it. Right-click →
  Open the first time, or `xattr -dr com.apple.quarantine DenonRemote-osx-arm64`.
  Signing properly needs an Apple Developer ID at $99/yr plus notarisation.
* **Windows** — SmartScreen warns about an unknown publisher: More info → Run anyway.
* **The firewall prompt** — it binds `0.0.0.0`, so both systems ask whether to allow
  incoming connections. Decline it and phones silently can't reach it.

## Things that will bite you

**Ask the receiver before you add a request.** Everything HTTP goes through
`ReceiverGate`, and it is there because these units stop answering under load in a way
that looks exactly like missing features. If you add a code path that talks to a
receiver, put it through the gate, and give it `ReceiverPace.Bulk` if nobody is waiting
on each request.

**The content root is pinned to the executable's folder.** Static files resolve relative
to the content root, which defaults to the *working directory*. Running the published
executable by its full path from elsewhere — or double-clicking it, where the working
directory is `/` — otherwise leaves it unable to find `wwwroot`, and the app comes up
completely unstyled. `Program.cs` falls back to `AppContext.BaseDirectory` when there's
no `wwwroot` beside the working directory, which keeps live CSS edits working under
`dotnet run`.

**A `<select>` Blazor thinks is unchanged is not redrawn.** After a write is read back,
a refused setting comes back to the value it always had — no diff, no patch, and the
control sits there showing what the receiver just declined. The dropdowns are keyed on
a counter bumped by every read, so each read makes them new elements.

**Sockets are explicitly IPv4.** The parameterless `TcpClient` builds a dual-stack
socket and sets `IPv6Only` on it during construction; on macOS, in a self-contained
build, that faulted. The receivers are IPv4 on the LAN, so
`AddressFamily.InterNetwork` is both the fix and the honest description. `NoDelay` is
set after connecting rather than in an initialiser.

**Read the receiver, don't tabulate it.** This project has repeatedly been wrong in
the same way: a hand-maintained list of the receiver's settings that was short, and so
presented as a missing feature. Which menus exist, what they are called, what order
they come in, and what values each row accepts are all things the receiver will tell
you, and all four are now read from it.

Three tables survive, and it is worth knowing what each is still for:

* `Models/AjaxCatalog.cs` — a **fallback** for sections the setup UI won't describe.
  `SetupBrowser` uses the live reading and fills in from the catalog only for sections
  that declared nothing.
* `Models/AspCatalog.cs` — the seed roots for the pre-HEOS tree. The pages under them
  are followed from the receiver's own menus.
* `Models/AjaxLabels.cs` — still the source of the **value** words on the HEOS
  generation ("2" → "Large"). The setting names come from the receiver now; mapping its
  own string table to individual values has not been worked out, so this is the one
  harvested table with no live replacement yet. See below.

If you find yourself adding to any of them, check first whether the receiver will say.

### Regenerating the label table

`Models/AjaxLabels.cs` is generated, not hand-written. The receiver's setup UI fills its
dropdowns only when a section is opened, so the harvest drives it: open each section
page (`/audio/audio.html` and the rest), click each numbered item in the right-hand
menu, and read every visible `<select>` — its id, its row label and its value/text
pairs. Keep the results in `localStorage` across pages (same origin), export as JSON,
then match each field id to its XML tag by comparing normalised ids and labels against
a captured payload of that document.

Indexed fields (`speaker_config_0_value` and kin) become row names for
`<Speaker index="0">`, and are written out under a `section/type/tag/index` key as well
as the plain one, since rows of the same tag don't share choices. A row the harvest
never saw must fall back to its index, never to the unindexed entry: taking that would
give every unharvested input the first input's name. That is what `AjaxLabels.RowName`
is for, as against `AjaxLabels.Label`.

Both halves live in `discovery/`: the captured payloads and the resolved table.

## Surviving tab switches and sleep

Blazor Server keeps the UI in a circuit on the server, tied to a WebSocket. Background
the tab or lock the phone and that socket drops while the page is suspended, so the
client spends its retry budget on a page nobody is looking at — you come back to a dead
page and a Reload link. Three things address it:

* the server keeps a disconnected circuit for two hours instead of three minutes, so a
  phone that has been away resumes the same session with its state intact;
* the page retries on its own schedule and, crucially, the instant the tab becomes
  visible again rather than on a timer that was frozen with it;
* when the circuit really is gone it reloads silently instead of showing a dead page.
  That costs nothing: all real state lives in the registry on the server, and the
  current receiver, zone and section live in the URL, so the reload comes back to
  exactly the view you left.

Two details if this is ever touched. `blazor.server.js` takes `reconnectionHandler` and
`reconnectionOptions` at the **top level** of `Blazor.start`; nesting them under
`circuit` is the `blazor.web.js` shape, which is accepted silently and leaves the
custom handler uninstalled. And Blazor injects its own reconnect overlay when the page
has no `#components-reconnect-modal` element — that overlay goes on swallowing taps
after the connection is back, so the page supplies an inert one.

## Adding commands

Everything goes through `DenonClient.Send("<raw command>")`, so anything in the Denon
control protocol document works without new plumbing. Pass a coalesce key for anything
you might send in a rapid stream, and `@DELAY:1500` inside a scene waits instead of
sending.

For anything *not* in that document, `Setup → Console` is the tool: raw send, live
traffic both directions, and the HTTP probe. That is how everything in
[protocol.md](protocol.md) was found.
