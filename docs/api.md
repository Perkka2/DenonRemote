# HTTP API

For the things a button, a shortcut or a cron job wants: power, volume, input, sound
mode, Quick Select and scenes. It is served by the app itself, on the same port as
the pages.

```bash
curl http://localhost:5080/api/receivers
curl -X POST 'http://localhost:5080/api/receivers/Living%20Room/zones/1/volume?value=45'
curl -X POST 'http://localhost:5080/api/receivers/Living%20Room/scenes/Movie%20night'
```

A call goes through the same client the pages use. So it shares the one socket per
receiver, it shows up on every open page at once, each change is verified against
what the receiver reports, and **the volume ceiling applies** — night mode's lower
one included. The one guardrail it skips is the confirm step for a big jump, there
being nobody to ask.

## Conventions

* `{receiver}` is a receiver's id, or the name you gave it (any case, URL-encoded).
* Reads are `GET`. Anything that changes the receiver is `POST`, with its values in
  the query string and no body.
* A switch (`on=`) takes `true`, `false` or `toggle`; `on`/`off` and `1`/`0` work too.
* Volume is the number on the receiver's front panel, half steps allowed in the main
  zone.
* An input or sound mode is given by its protocol code (`BD`) or by its name
  (`Blu-ray`, or whatever the input was renamed to on the receiver).

Every successful call answers `200` with the receiver as it now stands — see below.
After a `POST` that is what was *asked for*. If the receiver does not confirm it
within a moment the value is put back, exactly as on the pages, so read the receiver
again if it matters.

## Routes

| | | |
|---|---|---|
| `GET`  | `/api` | app name and version |
| `GET`  | `/api/receivers` | every receiver |
| `GET`  | `/api/receivers/{receiver}` | one receiver |
| `POST` | `/api/receivers/{receiver}/power?on=` | the whole unit, on or to standby. `on=true` also wakes a unit from eco standby |
| `POST` | `/api/receivers/{receiver}/sound-mode?code=` | `STEREO`, `MOVIE`, `MUSIC`, `DIRECT` … |
| `POST` | `/api/receivers/{receiver}/quick-select/{1-4}` | recall one of the receiver's Quick Selects. `?zone=2` for another zone |
| `POST` | `/api/receivers/{receiver}/night-mode?on=` | night mode |
| `POST` | `/api/receivers/{receiver}/sleep?minutes=` | sleep timer, 1–120. `0` turns it off |
| `GET`  | `/api/receivers/{receiver}/scenes` | this receiver's scenes |
| `POST` | `/api/receivers/{receiver}/scenes/{scene}` | run a scene, by id or name |
| `POST` | `/api/receivers/{receiver}/zones/{zone}/power?on=` | one zone |
| `POST` | `/api/receivers/{receiver}/zones/{zone}/volume?value=` | set the volume |
| `POST` | `/api/receivers/{receiver}/zones/{zone}/volume?step=up` | one step `up` or `down` |
| `POST` | `/api/receivers/{receiver}/zones/{zone}/mute?on=` | mute |
| `POST` | `/api/receivers/{receiver}/zones/{zone}/source?code=` | input |

## A receiver

```json
{
  "id": "3f9a1c2e",
  "name": "Living Room",
  "model": "AVR-X2500H",
  "host": "10.0.1.197",
  "online": true,
  "asleep": false,
  "power": true,
  "soundMode": "STEREO",
  "nightMode": false,
  "maxVolume": 60,
  "sleepMinutes": null,
  "quickSelect": 2,
  "zones": [
    { "zone": 1, "name": "Main zone", "power": true, "volume": 45.5, "mute": false,
      "source": "BD", "sourceName": "Blu-ray" }
  ],
  "sources": [ { "code": "BD", "label": "Blu-ray" } ],
  "quickSelects": [ { "slot": 1, "name": "Movie" } ],
  "scenes": [ { "id": "8c1d02aa", "name": "Movie night" } ]
}
```

`maxVolume` is the ceiling in force right now. `asleep` means eco standby: the unit
has stopped answering its control port and needs `power?on=true`. A value the
receiver has not reported yet is `null`.

## Errors

An error is `{ "error": "…" }`, in words meant to be read.

| | |
|---|---|
| `400` | a value was missing or not understood; the message lists what is accepted |
| `403` | the request came from another web site — see below |
| `404` | no such receiver, zone, scene or route |
| `409` | that is a HEOS speaker, which has no receiver to control |
| `503` | the receiver is not connected, or is in eco standby |

## Who can call it

Anything on the network that can reach the app, the same as the pages: there is no
login. Two things are done about that.

There is no route for raw protocol commands. The console in the app is for that.

A request that carries an `Origin` header naming another site is refused. Browsers
add that header; curl, Shortcuts and Home Assistant do not. Without the check, any
web page opened by someone in the house could post to the app behind their back,
because these routes need no body and no special header.
