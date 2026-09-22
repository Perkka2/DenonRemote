# What the receivers actually speak

Denon publishes a control protocol document. It covers the socket on TCP 23 and
nothing else, and a good deal of what this app does is not in it. Everything below was
read off the receivers — mostly out of their own web UI's JavaScript — rather than
guessed. The captures live in `discovery/`, which is deliberately not in the
repository: they are the receivers' own UI code, they are large, and they record one
household's configuration.

Two generations, and they have almost nothing in common over HTTP:

| | AVR-X4100W and kin | AVR-X2500H and kin |
|---|---|---|
| Generation | pre-HEOS | HEOS |
| Control socket | TCP 23 | TCP 23 |
| Now playing | its own web UI's endpoints | HEOS CLI on TCP 1255 |
| Legacy `/goform/` API | works | **403 on newer firmware** |
| Setup | a frame-based ASP tree on port 80 | a config API on HTTPS 10443 |
| Graphic EQ | a form post | an XML config write |

That 403 matters more than it looks: on newer firmware the whole legacy API is gone,
so things as basic as the input names have to come from the setup API instead.

## The graphic EQ

The 9-band Graphic EQ is its own Audio menu item, separate from Audyssey, and it is in
neither Denon's protocol document, nor Home Assistant's `denonavr` integration, nor the
Denon Advanced Audio one.

**Pre-HEOS units** serve ASP setup pages, and the EQ page posts its whole form:

```
POST /SETUP/AUDIO/GRAPHICEQ/s_audio.asp
radioGraphicEQ=ON&listGEQSpSelection=LRS&listGEQAdjustEQ=FRO
&textGEQ63=5.0&textGEQ125=2.5&…&textGEQ16k=0.0&setAdjustEQ=Set
```

Band values are decimal dB, the range comes from the page itself (−20 to +6 in 0.5 dB
steps on the X4100W), and `setGEQSetDefaults=Set` resets the curve.

**HEOS-generation units** expose the setup UI's config API on port 10443 over HTTPS,
with the receiver's own certificate:

```
GET /ajax/audio/set_config?type=10&data=
    <GraphicEQ><AdjustEQ><Channel>…</Channel><Eq63Hz>25</Eq63Hz>…</AdjustEQ></GraphicEQ>
```

Band values are tenths of a dB, so +2.5 dB is `25`. `<Enable>1|2</Enable>`,
`<SpeakerSelection>`, `<CurveCopy>1</CurveCopy>` and `<SetDefaults>1</SetDefaults>`
take the same shape, and `get_config?type=10` reads it all back. The EQ can't be
enabled while Audyssey MultEQ is active — the receiver greys it out — so the panel says
so rather than pretending.

The app detects which mechanism a receiver offers and shows the EQ only where there is
one.

## The setup API (HEOS generation)

```
GET /ajax/{section}/get_config?type={n}
GET /ajax/{section}/set_config?type={n}&data={url-encoded XML}
```

Sections are `audio`, `video`, `inputs`, `speakers`, `network`, `general`, `control`,
`advanced` and `home`. The type numbers are not guessed: the receiver's own UI declares
them, and the app reads that rather than keeping a list here.

```
/{section}/{Section}ServerInterface.js   CONFIG_TVFORMAT:"9"
/{section}/{Section}Settings.js          the menu, in order
/LanguageStrings.js                      every label, in eleven languages
```

Three quirks in those files, each of which cost a bug:

* `CONFIG_OPTION_*` constants number the *choices* within a setting — Audyssey's MultEQ
  is option 2 of setting 9 — not screens of their own. Listed as screens they collide
  with real type numbers and fetch the wrong thing.
* Every section declares `2..n` and omits type 1, which exists and answers perfectly
  well on all of them.
* `LanguageStrings.js` holds two dictionaries that the UI merges, and a key present in
  both must take the non-empty one. Read naively, "Speaker Config." comes back blank.

Names are matched to the string table by normalised name — `CONFIG_TVFORMAT` and
"TV Format" are the same word with the spaces taken out. That is not fuzzy matching; it
either is the same word or it isn't, and anything it can't name keeps the receiver's
own constant, which is ugly but true. It replaced pairing the menu positionally against
a neighbouring array of labels, which named nothing at all in Audio, General and
Network, and in General matched an array of zone names and read back "Language →
ZONE2".

### Reading a config document

The receiver describes each setting as it goes, and the app follows it rather than
deciding for itself:

* `display="1"` — doesn't apply to this configuration at all (no centre speaker, so no
  centre settings). Left out.
* `display="2"` or `gray` — exists but can't be changed right now. Shown read-only.
* `display="3"` — changeable.

Choices are per row, not per setting. The fronts offer Small/Large, the centre adds
None, the subwoofer answers Yes/No — all from the one `<Speaker>` tag. Repeated `<List>`
siblings are that row's own allowed values.

A document that is really a table is drawn as one. Input assign hangs its settings off
`<Source index="n">`, so it renders as a grid: one row per input, one column per
connector. Three things make that work, and each was a bug first:

* a child with no index of its own inherits its parent's, or a write lands on the wrong
  input;
* the row heading is the source's own `<Name>` from the document — live, so it follows a
  rename — and that column is then not repeated as a setting;
* the trailing `<SelectionList>` is the vocabulary, not data: each `<Item>`'s own
  `index` is the code the receiver wants back. Walked as data it invented a tenth input.

Column headings are the tag names. A harvested label is no use there — in a table the
receiver's UI labels every control with the *row* it sits in, so all four columns would
read "CBL/SAT".

Writes carry the index as an attribute:
`<SpeakerConfig><Speaker index="0">3</Speaker></SpeakerConfig>`. That was read out of
the UI's JavaScript rather than guessed, because a write with the wrong shape that the
receiver *accepts* would silently reconfigure a speaker. Every write is read back and
compared, so a setting the receiver quietly declines says so instead of appearing to
have worked.

## The setup tree (pre-HEOS)

A frameset per section, a menu frame, a content frame, and an `s_*.asp` the form posts
to. The app walks the receiver's own menus rather than keeping a list of pages.

Changes are sent as the **whole form**, exactly as the page's own submit does, with
HTML's successful-control rules applied: only the checked radio of a group, an
unchecked checkbox omitted entirely, disabled controls omitted, buttons never included.
This was verified field-for-field against Chromium submitting the same pages — 12 page
shapes, 165 fields, byte-identical.

Numeric fields have a Set button beside them and a hidden flag: the value alone is
accepted and ignored, and the flag has to be turned on in the same post.

## The network player (pre-HEOS)

These units have no HEOS at all and answer the AppCommand API with an empty document,
so the only account of what they are playing is the one their own web UI uses:

```
GET  /goform/formNetAudio_StatusXml.xml
POST /NetAudio/index.put.asp   cmd0=PutNetAudioCommand/CurDown
                               cmd1=aspMainZone_WebUpdateStatus/
                               ZoneName=MAIN ZONE
```

The second field matters: without it the next status document can still describe the
screen as it was before the keypress.

**That document describes a screen, not a track.** `szLine` is a ten-slot display
buffer. Playing a track it reads Now Playing / title / artist / album; browsing a folder
the same slots hold list entries, with `chFlag` marking the cursor. Reading slot 2 as
"the artist" would confidently report a menu item as the artist, so the panel renders
the screen as the screen — true in both modes.

The text arrives **escaped twice**: the receiver escapes it for HTML before putting it
in the XML, so parsing the XML only undoes the outer layer and a track called
`Rush > Moving Pictures` displays as `Rush &gt; Moving Pictures` unless you decode
again.

**The buttons mean different things in the two modes**, which is worth knowing because
it looks like a bug either way. Playing, this firmware has no transport commands of its
own — its web UI wires them to the cursor keys:

| Button | Playing | Browsing |
|---|---|---|
| `CurUp` | rewind | move up |
| `CurDown` | next track | move down |
| `CurEnter` | play / pause | select |
| `CurLeft` / `CurRight` | nothing | back / forward |
| `CmdPageUp` / `CmdPageDown` | nothing | page, past 7 pages only |

Its own UI decides which mode it's in by whether `szLine(0)` contains "Playing", and
reads the page count out of `szLine(8)` as `[ 2/ 9]`.

Repeat and random are **set, not toggled**: `UsbRepOff` / `UsbRepOn` (one) /
`UsbRepAll`, and `UsbRanOn` / `UsbRanOff`. The remote's `CmdRepeatOnOff` and
`CmdRandomOnOff` are ignored by this firmware. Repeat reads back as `OFF`, `ONE` or
`ALL` — never `ON`.

## These receivers fall over

The HTTP server inside them is small and gives up easily. A sweep of about a hundred
ordinary GETs, one after another as fast as they go, produced two timeouts and then
twenty-five refused connections in a row: it had stopped accepting anything at all.
Nothing was wrong with the requests, but every setting after that point looked, from
the app, like a setting the receiver did not have — which is exactly how a "missing
menu" bug presents.

So every request goes through `ReceiverGate`: one at a time per receiver, 250 ms apart
for anything a person is waiting on and 500 ms for bulk work, and once it has dropped
something, 1.5 s for the next half minute rather than straight back to full speed. It
does not recover between one request and the next. Retries back off 2 s, 4 s, 8 s.

Two things that look like the receiver buckling and are not:

* **A 500 is an answer.** A missing config type answers 500 with an empty body,
  consistently, and the very next type answers 200 — `advanced` 3–8 are 500 and
  `advanced` 9 is fine. Retrying those was adding hundreds of requests to a sweep whose
  whole problem was its weight.
* **A refusal from a port that has never answered** is a port with nothing behind it,
  not a receiver in trouble. Port 80 has no server at all on some firmware. Tracking
  this per host rather than per port meant one good read on 10443 made every refusal on
  port 80 look like a collapse, at fourteen seconds of backoff apiece.

## Capturing a receiver's setup UI

```bash
dotnet run -- --fetch-ui 10.0.1.197    # the pages and scripts
dotnet run -- --probe   10.0.1.197     # what every endpoint answers
```

Both write under `discovery/` with a report. The same things sit behind
*Console → Fetch UI* and *Probe* for the connected receiver; the flags are for a
receiver the app has never met.

The HEOS generation serves a handful of known pages, so those are fetched by name. The
pre-HEOS units serve an ASP tree far too large to list, so the named pages are only
seeds and the rest is followed from the receiver's own links, bounded to 250 pages and
6 levels.

**A capture reads and changes nothing, and that takes care**, because this UI applies
settings through links that look like any other:

* `s_*.asp` is where a section submits its form, and is never fetched;
* a query string is how a link carries a value (`d_audio.asp?ch=1&val=3`), so no URL
  with one is ever fetched;
* config save and load, initialise and firmware update are one link off the menu and
  have nothing to teach a capture, so those path segments are skipped by name.

All three are linked from pages the crawl does read, and all three answer 200. The only
thing keeping them untouched is that it declines to ask.
