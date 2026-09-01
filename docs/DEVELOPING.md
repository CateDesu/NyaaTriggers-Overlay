# Developing

## Why a plugin

The app used to draw the overlay itself: a transparent Qt window composited over the game, using
gamescope atoms on Linux and an always-on-top window on Windows. It never worked the same way
twice, and on Linux it silently did nothing unless the game happened to be launched inside
gamescope. A Dalamud plugin draws inside the game the same way on every platform and needs no
compositor tricks, so that's what this is now.

## Building

You need the .NET 10 SDK and a Dalamud install to reference.

```
dotnet build NyaaTriggers.Plugin/NyaaTriggers.Plugin.csproj -c Release
```

`Dalamud.NET.Sdk` finds the assemblies on its own: `~/.xlcore/dalamud/Hooks/dev/` on Linux,
`%APPDATA%\XIVLauncher` on Windows, or `$DALAMUD_HOME` if you've set it. The SDK sets that path
unconditionally, so a `Directory.Build.props` override in this repo would be ignored.

The build drops `NyaaTriggers.dll` and `NyaaTriggers.json` under `NyaaTriggers.Plugin/bin/Release/`,
plus a packaged `NyaaTriggers/latest.zip` for distribution.

`packages.lock.json` is committed: the SDK forces `RestorePackagesWithLockFile`, and a Dalamud
submission is expected to carry the lock file.

## Running it from source

1. `/xlsettings` → **Experimental** → add the full path to `NyaaTriggers.dll` under **Dev Plugin
   Locations**.

   On Linux this has to be a `Z:\` path, not a Linux one. Dalamud runs inside the Wine prefix,
   where `z:` maps to `/`, so the entry looks like
   `Z:\home\you\...\bin\Release\NyaaTriggers.dll`. A Linux path is accepted by the settings box
   without complaint and then resolves to nothing, so the plugin just never appears and no error
   tells you why.

2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **NyaaTriggers**.
3. `/nyaa` opens settings. `/nyaa lock` toggles the overlay lock without opening the window.

`test_bridge.py` drives the plugin without the app, so you can check the drawing before the app is
involved. `python test_bridge.py` fakes a pull; `--dps` fakes an encounter for the meter instead.

## Releasing

Push to main and the release happens on its own. The workflow builds the plugin as
`v<base>.<run number>` (the base comes from `<Version>` in the csproj, so something like
`0.1.0.42`), publishes it with the zip, regenerates `pluginmaster.json` to point at it, and
commits that back to main with `[skip ci]` so the listing update doesn't retrigger the build.
Anyone who added the repo URL gets the update on their next Dalamud refresh, same as any other
plugin update. Older rolling releases are pruned once the new one is up, so the releases page
stays a single current build.

Hand-cut milestones still work when a release is worth naming: bump `<Version>` in the csproj,
then

```
git tag v0.1.1.0 && git push origin v0.1.1.0
```

The tag must equal the csproj version or the workflow fails before building. A milestone is
pruned like everything else once the next rolling build is up: nothing on the releases page is
permanent, only the current build stays downloadable.

The manual **Prune old releases** workflow runs the same cleanup on demand: every release and
tag except the current rolling Latest. Run it from the Actions tab; the dry run input lists
what would go before anything is deleted.

Running the workflow by hand from main does the same as a push; from any other branch it refuses
to publish.

`pluginmaster.json` is generated, never hand-edited. `tools/make_pluginmaster.py` reads the
manifest DalamudPackager already produces and adds only the download URL and timestamp, so the two
can't drift. Stable channel only for now; a testing channel would need its keys merged into the
same entry rather than rewriting it.

There's no XIVLauncher on a CI runner, so the workflow unpacks
`https://goatcorp.github.io/dalamud-distrib/api15/latest.zip` and points `DALAMUD_HOME` at it.
That URL is pinned to the API level on purpose: bare `latest.zip` tracks whatever is current, and
an API bump would rebuild the plugin against headers it was never written for.

## The link

The plugin listens on loopback and the app connects to it, so the two can start in either order.
The app already had reconnect handling for talking to IINACT, and the same code carries this link.

- WebSocket, text frames, one JSON object per frame.
- Bound to `127.0.0.1` and `::1` only, default port **27080**. Both loopback families on purpose:
  bind only IPv4 and a client that resolved `localhost` to `::1` connects to nothing, which looks
  exactly like the overlay being broken.
- One client owns the overlay at a time. A new connection that finishes the handshake replaces the
  old session (the old one gets a 1001 close, not a bare drop).
- Up to four sockets may be open while handshakes settle, since a reconnect often races the old
  session's teardown. Only sessions still in the handshake can be evicted to make room, and if all
  four slots are established sessions the newcomer is refused. Something on the machine can't kick
  the app off the overlay by reconnecting in a loop.
- A handshake gets 5 seconds, then the slot is reclaimed.
- TCP keepalive is on (30s idle, then 3 probes 10s apart), so a half-open peer dies in about a
  minute instead of the OS default of hours.
- Any handshake carrying an `Origin` header is refused. WebSocket is exempt from the same-origin
  policy, so without this any page you happened to be browsing could open the socket and inject
  callouts. Browsers always send `Origin`; the app never does.
- Text frames are capped at 1 MiB and must be valid UTF-8. Anything outside the protocol closes
  the session with the proper RFC 6455 code rather than being guessed at.

### App → plugin

| Message | Meaning |
|---|---|
| `{"c":"tick","t":12.5}` | Fight clock, in timeline seconds. The plugin interpolates from here, so this only has to beat drift, not the frame rate. |
| `{"c":"timeline","v":[[18.0,"Wing","mechanic"],[24.5,"Dive","tankbuster"]]}` | Replace the schedule. `[time, label, kind]` entries in timeline seconds; the time and label are the app's `TimelineEngine.upcoming()` shape and `kind` is the tag the app derives from the label text: `tankbuster`, `raidwide` or `mechanic`. The kind is optional and free-form; an absent or unknown kind draws with the shared bar colour. |
| `{"c":"alert","text":"Stack","sev":"alarm","ttl":4.0}` | Show a callout. `sev` is `info`, `alert` or `alarm`; `ttl` is optional and falls back to the configured time for that severity. |
| `{"c":"dps","show":true,"enc":{"t":"Everkeep","d":"03:12","dps":81234.5},"rows":[["Alphinaud L","SGE",10234.5,21.4,300.1,true,0]]}` | DPS meter snapshot; see below. |
| `{"c":"clear"}` | Drop the schedule, any live alerts and the meter. Send on zone change and on wipes, not on a normal fight end; see below. |
| `{"c":"ping"}` | Liveness check; answered with `{"ev":"pong"}`. |

`dps` goes out about once a second while an encounter runs. `enc` carries the encounter title, the
fight duration as `mm:ss` text and the party's combined dps; `rows` is at most 24
`[name, job, encdps, share, hps, is_self, deaths]` arrays already sorted by encdps descending, where `job`
is the job acronym (or `""`), `share` is the member's damage percentage, `hps` is the member's
healing per second, `is_self` is true on the local player's row and `deaths` counts the member's
deaths this encounter. 24 covers a full alliance;
the plugin's Max combatants setting only narrows what it draws. The trailing fields are
optional, so a shorter row from an older program still parses with the defaults.
`{"c":"dps","show":false}` hides the meter, so the app sends it when the encounter ends. The
plugin records that frame as the encounter having ended rather than wiped, and the hold-last
option keys on the distinction: it keeps the final rows up after an ended fight but not after
a clear. The last frame of a fight wins, so a clear landing after `show:false` still wipes the
state. Send `show:false` for a fight that ran its course and `clear` for a wipe or zone change;
a sender that ends fights with `clear` alone never produces the ended marker and hold-last has
nothing to hold. The plugin keeps only the latest snapshot; there is nothing to acknowledge.

Unknown commands are ignored rather than treated as errors, so a newer app can talk to an older
plugin.

### Plugin → app

| Message | Meaning |
|---|---|
| `{"ev":"hello","protocol":1,"plugin":"0.1.0"}` | Sent on connect, always the first frame. Check `protocol` before driving it. |
| `{"ev":"pong"}` | Reply to `ping`. |

`protocol` is bumped only on an incompatible change to the tables above.

## Standalone meter

The plugin can run the dps meter without the program. With **Standalone meter** ticked and no app
session live, it dials IINACT's ACT-compatible feed itself (default `ws://127.0.0.1:10501/ws`),
subscribes to `LogLine`, `ChangePrimaryPlayer`, `ChangeZone`, `PartyChanged` and `InCombat`, and
parses the log lines with `Meter/MeterEngine.cs`, a C# port of the program's `dps_meter.py`. The
port carries only what the overlay draws, so the program's parse-table columns and pull logging
stay program-side; the encounter lifecycle, the effect-pair decode and the pet merging match it
line for line.

The same frame semantics the app sends are produced locally: a live snapshot about once a second,
the ended marker when the encounter finalizes, and a clear on zone change, so hold-last engages
after a fight but never across a zone on either feed. One divergence to know about: a wipe
finalizes the encounter, and nothing clears after it here, so the standalone feed holds the final
rows after a wipe. The app sends its clear after the end frame on a wipe, so the app feed does
not hold there. The app's feed always wins: while a session is live the IINACT client
stays off, and when the app goes away mid-fight the engine starts cold, classifying the party from
the subscribe burst and a one-shot `getCombatants`, the same way the program handles a mid-instance
start.

`tests/MeterEngineTests` is a dependency-free harness that drives the engine with synthetic log
lines, including the wire-decode examples from `dps_meter.py`'s docstring. Run it with
`dotnet run --project tests/MeterEngineTests`.

## Status

Both halves work and are in use in game. The app side is `plugin_link.py` in the
[NyaaTriggers](https://github.com/CateDesu/NyaaTriggers) repo: `MainWindow` feeds `PluginLink`
the callouts (`send_alert`), the fight clock (`send_tick`), the schedule on connect
(`send_timeline`) and the meter once a second during an encounter (`send_dps`). The link is
configured under **Settings > In-Game Overlay**.
