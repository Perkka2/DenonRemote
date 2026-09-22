# Denon Remote

A fast replacement for the web UI built into Denon AVR receivers, for the
**AVR-X4100W** and **AVR-X2500H** (anything speaking the same control protocol
should work).

Blazor Server on .NET 8, same shape as SpeakerBoxDesigner: `Microsoft.NET.Sdk.Web`,
`AddServerSideBlazor()`, `MapBlazorHub()`, `_Host.cshtml` as the prerendered host page.
No WebAssembly payload, no client-side .NET.

## Why it's quicker than the receiver's own page

The built-in UI does a full HTTP round trip per action and re-reads the whole status
document each time. This app keeps the receiver's control socket (TCP 23) open for the
lifetime of the process:

* a command is a few bytes on an already-open socket — no connection setup per tap;
* the receiver *pushes* state changes back, so volume, input and sound mode update
  instantly, including changes made from the physical remote or the front panel;
* the UI updates optimistically, then reconciles with what the receiver reports;
* dragging a slider coalesces queued commands, so you send the position you landed on
  rather than a backlog;
* HTTP `/goform` is only a fallback used while the socket is down.

Because the registry is a singleton, every phone, tablet and laptop looking at the app
shares one socket per receiver and sees the same live state. That also sidesteps the
receivers' limit on concurrent control connections.

## Running it

```bash
cd ~/Documents/GitHub/DenonRemote
dotnet run
```

Then open <http://localhost:5080>. It listens on `0.0.0.0:5080`, so from a phone on the
same wifi use `http://<your-mac>.local:5080` or the Mac's IP. macOS will ask to allow
incoming connections the first time.

On first run hit **Setup → Scan for receivers** (SSDP). If your network blocks SSDP,
add the receiver's IP by hand on the same page. Receivers live in
`~/.denonremote/receivers.json`, scenes in `~/.denonremote/scenes.json`.

Add it to the home screen on iOS and it opens full-screen as a standalone app.

## What it does

**Remote** — power, volume, mute, inputs and sound mode, per zone. Zone 2 and Zone 3
appear only if the receiver has them: the zone count comes from the unit, and a zone
that answers a query is shown even if the count said otherwise.

**Input names come from the receiver.** On connect the app reads `Deviceinfo.xml` and
asks `AppCommand.xml` for `GetRenameSource` and `GetDeletedSource`, so the grid shows
the names you set in the unit's own setup menu and hides inputs you deleted there.
Each receiver gets its own list. If a unit doesn't answer, the generic table is used
and the Input header says "default names".

**Quick Select 1–4** — the receiver's own slots, plus *Save to…* to store the current
setup into one.

**Scenes** — app-side presets the hardware can't do: one tile sets input, volume, sound
mode and the other zones together. *Save current* captures what the receiver is doing
now, with a pause inserted after power-on so the input change isn't ignored.

**Sound** — the settings buried deepest in the on-screen menu: tone control, bass,
treble, subwoofer, dialogue, every channel trim the unit reports, plus Dynamic EQ,
MultEQ, Dynamic Volume and Audio Restorer. Bass and treble grey out in Direct modes,
which bypass tone control.

**Surround parameters** (under Sound) — the rest of the on-screen Surround Parameter
menu: Cinema EQ, Dynamic Compression, LFE level, subwoofer on/off, Loudness Management,
Reference Level Offset, effect level, room size, audio delay, Panorama, Dimension,
Centre Width, Centre Gain, Centre Spread. Which of these apply depends on the sound
mode and speaker layout, so the app queries all of them and shows the ones the receiver
answered for; *Show all* reveals the rest, greyed out.

**System** — ECO mode, front-panel dimmer, auto-standby, HDMI monitor output, HDMI
audio out (amp or TV) and video select. Anything the unit doesn't report is named as
not reported rather than silently doing nothing.

**Tuner** — appears in Remote while the tuner is the selected input: frequency,
tune up/down, presets 1–8 and preset stepping.

**Menu** — cursor pad, OK, Back, Menu and Option, for the on-screen setup screens.

**Playing** — HEOS (TCP 1255) now-playing and transport. The X2500H has it; the X4100W
predates HEOS, so the tab simply doesn't appear there. Like the control socket it
registers for change events rather than polling.

**Guardrails** — a per-receiver volume ceiling nothing can exceed, a confirm step for
a slider jump above a threshold, a sleep timer, and night mode (tighter ceiling plus
Dynamic Volume). Set the numbers under Setup → Settings.

**Signal** — what the receiver is actually handling: sound mode, input signal and
format, HDMI resolution, HDR, colour space and pixel depth, what the connected
display accepts, and the firmware version. None of this exists in the control
protocol; it comes from the setup API, and it is polled while the tab is open.

**Setup** — the receiver's own configuration documents, read live and in its own
words: amp assign, speaker config, distances, channel levels, crossovers, bass,
HDMI and output settings, on-screen display, 4K signal format, TV format, input
assign, hidden sources, source level, ECO, zone setup, front display, firmware and
the EDID log.

The API answers in numeric codes — "2" rather than "Large" — so the labels were
harvested from the receiver's own setup UI (see *Regenerating the label table*) and
matched back to the XML tag each belongs to. A setting with known choices is a
dropdown; anything the table doesn't cover shows the raw code rather than a guess.

**Choices are per row, not per setting.** The fronts offer Small/Large, the centre
adds None, the subwoofer answers Yes/No — all from the one `<Speaker>` tag. The
codes come from the document where it publishes them (repeated `<List>` siblings are
that row's own allowed values) and the harvested table is keyed by row index too, so
each row gets its own words. Where the receiver sits on a code it leaves off its own
list, that code is offered as well, so a dropdown is never blank.

**Documents that are really a table are drawn as one.** Input assign hangs its
settings off `<Source index="n">`, so it renders as a grid: one row per input, one
column per connector. Three things make that work, and each was a bug first:

* a child with no index of its own inherits its parent's, or a write lands on the
  wrong input;
* the row heading is the source's own `<Name>` from the document — live, and it
  follows a rename — and that column is then not repeated as a setting;
* the trailing `<SelectionList>` is the vocabulary, not data: each `<Item>`'s own
  `index` is the code the receiver wants back. Walked as data it invented a tenth
  input; read as a vocabulary it is where the dropdowns' codes come from.

Column headings are the tag names. The harvested label is no use there — in a table
the receiver's UI labels every control with the row it sits in, so all four columns
would read "CBL/SAT".

The receiver says what applies to your setup and the app follows it: a setting marked
`display="1"` doesn't apply at all and is left out, and one marked grey exists but
can't be changed right now — no centre speaker, no centre settings — so it shows
read-only as "fixed by the current setup". Writes are read back and compared, so a
setting the receiver quietly declines says so instead of appearing to have worked.

**Names come from the receiver** wherever it will say: input names and hidden inputs,
zone names and Quick Select names. On newer firmware, where the whole legacy
`/goform/` API answers 403, these come from the setup API instead.

**Protocol console** (Setup → Console) — send any raw command and watch every line in
both directions, live, with a filter. This is the tool for working out commands that
aren't in the protocol document.

**Every command verifies itself.** The receiver answers a command it accepted and
stays silent on one it doesn't support, so the app follows each change with a query
and checks the reply. A command that goes unanswered is reverted in the UI and
reported as unsupported instead of sitting there looking applied; one the receiver
clamps or overrides is reported as a mismatch. Setup → Console lists the outcomes.

### The graphic EQ

The 9-band Graphic EQ is its own Audio menu item, separate from Audyssey, and it is
not in Denon's published control protocol — nor in Home Assistant's `denonavr`
integration, nor the Denon Advanced Audio one. Each generation drives it from its own
setup UI over HTTP, and both mechanisms here were read off the receivers rather than
guessed (see `discovery/` for the captures).

**Pre-HEOS units** (AVR-X4100W and kin) serve ASP setup pages. The EQ page posts its
whole form:

```
POST /SETUP/AUDIO/GRAPHICEQ/s_audio.asp
radioGraphicEQ=ON&listGEQSpSelection=LRS&listGEQAdjustEQ=FRO
&textGEQ63=5.0&textGEQ125=2.5&…&textGEQ16k=0.0&setAdjustEQ=Set
```

Band values are decimal dB, the range comes from the page itself (−20 to +6 in 0.5 dB
steps on the X4100W), and `setGEQSetDefaults=Set` resets the curve.

**HEOS-generation units** expose the setup UI's config API on port 10443 over HTTPS
with a self-signed certificate:

```
GET /ajax/audio/set_config?type=10&data=
    <GraphicEQ><AdjustEQ><Channel>…</Channel><Eq63Hz>25</Eq63Hz>…</AdjustEQ></GraphicEQ>
```

Band values are tenths of a dB, so +2.5 dB is `25`. `<Enable>1|2</Enable>`,
`<SpeakerSelection>`, `<CurveCopy>1</CurveCopy>` and `<SetDefaults>1</SetDefaults>`
take the same shape, and `get_config?type=10` reads it all back. Note the EQ can't be
enabled while Audyssey MultEQ is active — the receiver greys it out — so the panel
says so rather than pretending.

The app detects which mechanism a receiver offers and shows the EQ section only where
there is one. The console's HTTP probe and discovery sweep are what found these, and
remain there for the next undocumented thing.

Whole-unit standby is "All off (standby)". In eco standby the unit stops answering
TCP 23 while still serving HTTP; the app notices, says so, and offers a wake that goes
over HTTP.

Volume is shown as the number on the receiver's front panel: the protocol's 0–98 scale
minus 80 dB. `MVMAX` from the receiver caps the slider alongside your own ceiling.

## Sharing it with other people

The app is a server, so the first question is whether anyone needs their own copy.
If they just need to *use* it, one machine runs it and everyone else opens
`http://that-machine:5080` from a phone or laptop. Only hand out builds when people
want to run it themselves.

### Building

```bash
./publish.sh                 # every target, into dist/
./publish.sh osx-arm64       # just one
```

Each zip holds a folder: the executable with the .NET runtime beside it, `wwwroot`
and `appsettings.json`. The recipient installs nothing. About 90 MB per platform.
Targets: `osx-arm64`, `osx-x64`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`.

These are deliberately **not** single-file builds. Bundling the native libraries
makes the app extract them to a temp directory at startup, and on macOS that
produced an `AccessViolationException` inside a socket call before the app could
start. A plain folder is larger and boring, and it works.

Tag a release and GitHub Actions does the same thing on native runners, with
ReadyToRun for quicker startup, and attaches the zips:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

Trimming is off for the same family of reasons — Blazor's reflection doesn't survive
it. `--self-contained` lives on the publish command rather than in the csproj, so
`dotnet run` stays fast during development.

### What the recipient sees

Unzip the folder, run `DenonRemote` inside it, and a browser opens at the app. The console prints the
address to use from a phone on the same wifi. `--port 5090` moves it;
`--no-browser` stops it opening one.

Three things to warn people about:

* **macOS** — the build is unsigned, so Gatekeeper quarantines it. Right-click the
  executable and choose Open the first time, or run
  `xattr -dr com.apple.quarantine DenonRemote-osx-arm64`. Signing properly needs an
  Apple Developer ID at $99/yr plus notarisation.
* **Windows** — SmartScreen warns about an unknown publisher: More info → Run anyway.
* **The firewall prompt** — it binds `0.0.0.0`, so both systems ask whether to allow
  incoming connections. Decline it and phones silently can't reach it.

Settings live in `~/.denonremote` (receivers, scenes, discovery reports), never
beside the executable, so a build in `/Applications` or `Program Files` still works.

## Layout

```
Program.cs                      DI + Blazor Server wiring
Services/DenonClient.cs         one receiver: socket, send queue, protocol parsing
Services/DenonRegistry.cs       singleton owning one client per receiver
Services/DeviceProfileReader.cs model, zone count, real input names, HEOS probe
Services/HeosClient.cs          HEOS command line: now playing and transport
Services/GraphicEqClient.cs     graphic EQ: ASP form post and ajax config backends
Services/HttpProbe.cs           HTTP probing, discovery sweep, setup-UI capture
Services/SsdpDiscovery.cs       M-SEARCH + UPnP description parsing, direct probe
Services/ReceiverStore.cs       receivers.json
Services/SceneStore.cs          scenes.json
Services/AppPaths.cs            where writable state lives
Services/StartupOptions.cs      --port / --no-browser, startup banner
publish.sh                      self-contained builds for every platform
.github/workflows/release.yml   the same on tag, attached to a release
Models/DenonCatalog.cs          fallback input table, sound modes, labels
Models/SurroundParameters.cs    surround parameter specs: keys, kinds, ranges
Models/SystemState.cs           ECO, dimmer, standby, HDMI routing, tuner
Pages/Index.razor               Remote / Sound / System / Menu / Playing
Pages/Setup.razor               discovery, receiver list, per-receiver settings
Pages/Console.razor             raw command entry and live traffic log
Components/                     zone panel, grids, sliders, menu pad, now playing
```

## Surviving tab switches and sleep

Blazor Server keeps the UI in a circuit on the server, tied to a WebSocket. Background
the tab or lock the phone and that socket drops while the page is suspended, so the
client spends its retry budget on a page nobody is looking at — you come back to a
dead page and a Reload link. Three things address it:

* the server keeps a disconnected circuit for two hours instead of three minutes, so
  a phone that has been away can resume the same session with its state intact;
* the page retries on its own schedule and, crucially, retries the instant the tab
  becomes visible again rather than waiting on a timer that was frozen with it;
* when the circuit really is gone, it reloads silently instead of showing a dead page.
  That costs nothing: all real state lives in the receiver registry on the server, and
  the current receiver, zone and section live in the URL, so the reload comes back to
  exactly the view you left.

Two details worth keeping in mind if this is ever touched. `blazor.server.js` takes
`reconnectionHandler` and `reconnectionOptions` at the **top level** of `Blazor.start`;
nesting them under `circuit` is the `blazor.web.js` shape, which is accepted silently
and leaves the custom handler uninstalled. And Blazor injects its own reconnect overlay
when the page has no `#components-reconnect-modal` element — that overlay goes on
swallowing taps after the connection is back, so the page supplies an inert one.

### Regenerating the label table

`Models/AjaxLabels.cs` is generated, not hand-written. The receiver's setup UI fills
its dropdowns only when a section is opened, so the harvest drives it: open each
section page (`/audio/audio.html`, `/video/video.html`, `/speakers/speakers.html`,
`/inputs/inputs.html`, `/general/general.html`), click each numbered item in the
right-hand menu, and read every visible `<select>` — its id, its row label and its
value/text pairs. Keep the results in `localStorage` across pages (same origin),
export as JSON, then match each field id to its XML tag by comparing normalised ids
and labels against a captured payload of that document. Indexed fields
(`speaker_config_0_value` and kin) become row names for `<Speaker index="0">`, and
are written out under a `section/type/tag/index` key as well as the plain one, since
rows of the same tag don't share choices.

A row the harvest never saw must fall back to its index, never to the unindexed
entry: taking that would give every unharvested input the first input's name. That
is what `AjaxLabels.RowName` is for, as against `AjaxLabels.Label`.

Both halves live in `discovery/`: the captured payloads and the resolved table.

`discovery/` is deliberately not in the repository. The captures are the
receivers' own UI code, they are large, and they record one household's
configuration - so they are kept locally and regenerated per receiver with the
two commands below rather than committed. Everything derived from them that the
app actually needs is checked in, in `Models/AjaxLabels.cs`.

### Capturing a receiver's setup UI

```bash
dotnet run -- --fetch-ui 10.0.1.197
```

Writes the pages and their scripts under `discovery/ui-<host>/` with a report beside
them. The same thing sits behind *Console → Fetch UI* for the connected receiver;
the flag is for a receiver the app has never met.

The HEOS generation serves a handful of known pages, so those are fetched by name.
The pre-HEOS units serve a frame-based ASP tree far too large to list — six sections,
each with its own subpages — so the named pages are only seeds and the rest is
followed from the receiver's own links, bounded to 250 pages and 6 levels.

**A capture reads and changes nothing, and that takes care**, because this UI applies
settings through links that look like any other:

* `s_*.asp` is where a section submits its form, and it is never fetched;
* a query string is how a link carries a value (`d_audio.asp?ch=1&val=3`), so no URL
  with one is ever fetched;
* config save and load, initialise and firmware update are one link off the menu and
  have nothing to teach a capture, so those path segments are skipped by name.

All three are linked from pages the crawl does read, and all three answer 200 — the
only thing keeping them untouched is that it declines to ask.

## Checks

```bash
dotnet run -- --self-test
```

Covers the parts that have actually had bugs: protocol line parsing across all the
prefixes, zone and parameter handling, command encoding and the volume ceiling,
both graphic-EQ payload shapes, and the setup API's XML. It is a flag on the app
rather than a test project so it needs no packages and runs anywhere the app runs —
`publish.sh` and the release workflow both run it before building.

## Notes for whoever maintains this

**The content root is pinned to the executable's folder.** Static files resolve
relative to the content root, which defaults to the *working directory*. Running the
published executable by its full path from somewhere else — or double-clicking it,
where the working directory is `/` — otherwise leaves it unable to find `wwwroot`,
and the app comes up completely unstyled. `Program.cs` falls back to
`AppContext.BaseDirectory` when there's no `wwwroot` beside the working directory,
which keeps live CSS edits working under `dotnet run`.

**A `<select>` Blazor thinks is unchanged is not redrawn.** After a write is read
back, a refused setting comes back to the value it always had — no diff, no patch,
and the control sits there showing what the receiver just declined. The dropdowns
are keyed on a counter bumped by every read, so each read makes them new elements.

**Indexed writes carry an index attribute.** The setup UI sends
`<SpeakerConfig><Speaker index="0">3</Speaker></SpeakerConfig>` — the index is an
attribute, not part of the tag. That was read out of the UI's own JavaScript rather
than guessed, because a write with the wrong shape that the receiver *accepts* would
silently reconfigure a speaker.

**Sockets are explicitly IPv4.** The parameterless `TcpClient` builds a dual-stack
socket and sets `IPv6Only` on it during construction; on macOS, in a self-contained
build, that faulted. The receivers are IPv4 on the LAN, so `AddressFamily.InterNetwork`
is both the fix and the honest description of what's going on. `NoDelay` is set after
connecting rather than in an initialiser.

## Adding commands

Everything goes through `DenonClient.Send("<raw command>")`, so anything in the Denon
control protocol document works without new plumbing. Pass a coalesce key for anything
you might send in a rapid stream, and `@DELAY:1500` inside a scene waits instead of
sending.

## Tested

Built and driven against a scripted stand-in that speaks the control protocol, the
HEOS command line and the HTTP endpoints, exercised through a headless browser:

* query burst on connect, `MVUP` round trips, mute, input switching, sound mode;
* renamed inputs (Apple TV, Chromecast) with deleted ones hidden, 3 zones detected;
* Zone 2 and Zone 3 power, volume and source;
* volume ceiling holding at the cap, and the confirm prompt on a jump past 65;
* Quick Select, scene capture and replay, sleep timer, night mode;
* tone, channel trims, MultEQ, Dynamic Volume, Restorer;
* surround parameters, including the applicability filter (7 of 14 shown when the
  stand-in answers for 7) and Cinema EQ, whose value is glued to its key;
* ECO, dimmer, auto-standby, HDMI monitor out, HDMI audio, video select;
* tuner frequency, tuning and presets, with the panel appearing only for that input;
* the console: probe buttons, raw send, filtering, live traffic both directions;
* the graphic EQ on both backends: the ASP page parsed from a real X4100W capture
  and posted back in the page's own form shape, and the ajax backend writing
  `<Eq1kHz>-30</Eq1kHz>` for −3 dB;
* command verification: an unsupported command reverts and is reported, and a value
  the receiver clamps (asked 78, got 70) is reported as a mismatch;
* a published self-contained build started with no .NET installed, **launched from a
  different working directory**, connected to the stand-in over the control socket,
  served the app and its stylesheet, and wrote its state to the user profile;
* the Signal panel and Setup browser driven against the real config documents
  captured from the receiver: settings and values shown in the receiver's own words,
  a nested value-plus-choices element collapsing to one row, greyed settings shown
  read-only, inapplicable ones left out, and an edit written back, re-read and
  confirmed;
* zone, input and Quick Select names coming from the receiver rather than the
  fallback table;
* reconnection, by killing the server under a live page: the banner appeared, the
  page reloaded itself when the server returned, came back to the same section from
  the URL and was interactive again;
* a network blip with the server still up: the same circuit resumed with no reload;
* menu pad and menu open/close;
* HEOS now-playing updating from push events, next/previous/play-pause;
* reconnect with backoff after the receiver drops the socket.

Untested here: the actual hardware.
