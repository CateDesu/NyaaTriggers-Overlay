# Developing

## Why a plugin

The program used to draw the overlay itself: a transparent Qt window composited over the game, using
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

`test_bridge.py` drives the plugin without the program, so you can check the drawing before the program is
involved. `python test_bridge.py` fakes a pull; `--dps` fakes an encounter for the meter instead.

## Releasing

Push to main and the release happens on its own. The workflow builds the plugin as
`v<base>.<run number>` (the base comes from `<Version>` in the csproj, so something like
`0.1.0.42`), publishes it with the zip, regenerates `pluginmaster.json` to point at it, and
commits that back to main with `[skip ci]` so the listing update doesn't retrigger the build.
Anyone who added the repo URL gets the update on their next Dalamud refresh, same as any other
plugin update. Older releases are pruned after the new package and listing are available.
Publication and manual cleanup share one workflow queue. Cleanup preserves the listed version,
newer versions awaiting publication, drafts, prereleases and tags it cannot recognize.

A rerun can finish a publication whose release was created before its listing update failed.
It checks the workflow run, tag commit and packaged version before reusing an uploaded package.
Missing uploads and interrupted upload placeholders can be retried. An existing release from
another run is refused.

Hand-cut milestones still work when a release is worth naming: bump `<Version>` in the csproj,
then

```
git tag v0.1.1.0 && git push origin v0.1.1.0
```

The tag must equal the csproj version or the workflow fails before building. A milestone is
pruned like everything else once the next rolling build is up: nothing on the releases page is
permanent, only the current build stays downloadable.

The manual **Prune old releases** workflow runs the same cleanup on demand. Run it from the
Actions tab. The dry run input lists older releases without deleting them. Cleanup requires
the listing's current release package to exist before removing fallback releases.

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

The plugin listens on loopback and the program connects to it, so the two can start in either order.
The program already had reconnect handling for talking to IINACT, and the same code carries this link.

- WebSocket, text frames, one JSON object per frame.
- Bound to `127.0.0.1` and `::1` only, default port **27080**. Both loopback families on purpose:
  bind only IPv4 and a client that resolved `localhost` to `::1` connects to nothing, which looks
  exactly like the overlay being broken.
- One client owns the overlay at a time. A new connection that finishes the handshake replaces the
  old session (the old one gets a 1001 close, not a bare drop).
- Up to four sockets may be open while handshakes settle, since a reconnect often races the old
  session's teardown. Only sessions still in the handshake can be evicted to make room, and if all
  four slots are established sessions the newcomer is refused. Bare connect-and-hold floods cannot
  evict the program. A local process that completes handshakes can replace it by design.
- A handshake gets 5 seconds, then the slot is reclaimed. An older accepted socket cannot take
  ownership after a newer session has connected, even if that newer session has disconnected.
- Header parsing preserves the original bytes so invalid request characters cannot become ASCII
  replacements before validation. Only spaces and tabs pad handshake field values.
- TCP keepalive is on (30s idle, then 3 probes 10s apart), so a half-open peer dies in about a
  minute instead of the OS default of hours.
- Any handshake carrying an `Origin` header is refused. WebSocket is exempt from the same-origin
  policy, so without this any page you happened to be browsing could open the socket and inject
  callouts. Browsers always send `Origin`; the program never does.
- The bridge inbox holds at most 4 MiB of UTF-16 payload and 512 messages. Each update
  drains at most 64 messages and 2 MiB. Overflow closes the session so the program can reconnect
  and resend its schedule.
- Text frames are capped at 1 MiB and must be valid UTF-8. Anything outside the protocol closes
  the session with the proper RFC 6455 code rather than being guessed at.

Feed processing runs from Dalamud's framework update, independently of whether the UI draws.
A shared lock serializes updates with drawing and settings changes. Queued messages carry their
receipt time. Delayed ticks advance from that time and expired callouts are discarded.

### Program → plugin

| Message | Meaning |
|---|---|
| `{"c":"tick","t":12.5}` | Fight clock, in timeline seconds. The plugin interpolates from here, so this only has to beat drift, not the frame rate. |
| `{"c":"timeline","v":[[18.0,"Wing","mechanic"],[24.5,"Dive","tankbuster"]]}` | Replace the schedule. `[time, label, kind]` entries in timeline seconds; the time and label are the program's `TimelineEngine.upcoming()` shape and `kind` is the tag the program derives from the label text: `tankbuster`, `raidwide` or `mechanic`. The kind is optional and free-form; an absent or unknown kind draws with the shared bar colour. |
| `{"c":"alert","text":"Stack","sev":"alarm","ttl":4.0}` | Show a callout. `sev` is `info`, `alert` or `alarm`; `ttl` is optional and falls back to the configured time for that severity. |
| `{"c":"dps","show":true,"enc":{"t":"Everkeep","d":"03:12","dps":81234.5,"hasDamage":true},"rows":[["Alphinaud L","SGE",10234.5,21.4,300.1,true,0]]}` | DPS meter snapshot; see below. |
| `{"c":"clear","keepDps":false}` | Drop the schedule, live alerts and meter history on zone changes. Send `keepDps:true` on wipes to retain DPS while clearing the other state. |
| `{"c":"ping"}` | Liveness check; answered with `{"ev":"pong"}`. |

`dps` goes out about once a second while an encounter runs. `enc` carries the encounter title, the
fight duration as `mm:ss` text and the party's combined dps; `rows` is at most 24
`[name, job, encdps, share, hps, is_self, deaths]` arrays already sorted by encdps descending, where `job`
is the job acronym (or `""`), `share` is the member's damage percentage, `hps` is the member's
healing per second, `is_self` is true on the local player's row and `deaths` counts the member's
deaths this encounter. 24 covers a full alliance;
the plugin's Max combatants setting only narrows what it draws. The trailing fields are
optional, so a shorter row from an older program still parses with the defaults.
`enc.hasDamage` is an optional boolean reporting whether damage was dealt or taken in the
displayed encounter. It stays true even when the displayed DPS rounds to zero. New programs
send it on every live and final frame. Older programs fall back to positive DPS or damage share.
Their frames cannot distinguish an opening hit taken from healing or an empty encounter.
Older plugins ignore the added field, and the protocol version remains 1.

When an encounter ends, the program sends `show:false` with its complete final `enc` and
`rows`. This includes damage since the last live update and covers pulls that finish between
updates. The plugin marks those values as ended so the hold-last option can keep them visible.
A wipe then sends `{"c":"clear","keepDps":true}` to clear the schedule, clock and alerts while
preserving the meter. Repeated wipes can send this frame without another encounter ending.
Zone changes send `keepDps:false`, which also discards cached rows used by older end markers.
If the program loses its feed, it finalizes the encounter before sending a full clear so
the ending cannot restore rows after the disconnect.

For older programs, a bare `{"c":"dps","show":false}` still ends the latest received snapshot.
A clear without `keepDps` hides the meter but retains that snapshot for an older sender's
clear followed by end sequence. The greeting advertises `dpsRetention:true` when final payloads
and preserving clears are supported. Without that boolean, the program publishes final values
as a live snapshot followed by a bare ending, and restores the current DPS state after each
preserving clear. A full clear first replaces the older plugin's cached rows with an empty
snapshot so a later end marker cannot restore the old zone. The fallback keeps completed rows
through empty pulls and discards its cache on reconnect. The plugin keeps only the latest
snapshot; there is nothing to acknowledge.

Holding the final meter is enabled by default and enabled once when upgrading older settings.
It can still be disabled in the DPS settings. While a final result is held, frames with
no damage leave it in place, including frames containing only healing. The first frame with
damage replaces it. A clear still removes the held result on zone changes.

A new program session clears the previous session's final DPS history. Same-session wipe
sequences still retain their final rows. Wire numbers must be finite. Timeline values must also
fit a float, and damage shares are bounded to 0 through 100.

Unknown commands are ignored rather than treated as errors, so a newer program can talk to an older
plugin.

### Plugin → program

| Message | Meaning |
|---|---|
| `{"ev":"hello","protocol":1,"plugin":"0.2.0","dpsRetention":true}` | Sent on connect, always the first frame. Check `protocol` before driving it. The optional boolean selects native retained DPS messages. |
| `{"ev":"pong"}` | Reply to `ping`. |

`protocol` is bumped only on an incompatible change to the tables above.

## Standalone meter

The plugin can run the dps meter without the program. With **Standalone meter** ticked and no program
session live, it dials IINACT's ACT-compatible feed itself (default `ws://127.0.0.1:10501/ws`),
subscribes to `LogLine`, `ChangePrimaryPlayer`, `ChangeZone`, `PartyChanged` and `InCombat`, and
parses the log lines with `Meter/MeterEngine.cs`, a C# port of the program's `dps_meter.py`. The
port carries only what the overlay draws, so the program's parse-table columns and pull logging
stay program-side; the encounter lifecycle, the effect-pair decode and the pet merging match it
line for line.

The same frame semantics the program sends are produced locally: a live snapshot about once a
second, a complete final snapshot when the encounter ends, and a clear on zone change, so hold-last
engages after a fight but never across a zone on either feed. A wipe finalizes the encounter.
The program then clears callouts and the timeline while preserving DPS, so both feeds hold the
final rows after a wipe. The program's feed always wins: while a session is
live the IINACT client stays off, and when the program goes away mid-fight the engine starts cold,
classifying the party from the subscribe burst and a one-shot `getCombatants`, the same way the
program handles a mid-instance start.

Standalone publication waits for damage dealt or taken before showing a new pull, then sends
that first snapshot immediately. Empty encounters cannot replace a held result.
Encounter endings and zone clears are applied in arrival order within each update. A later
empty encounter cannot cancel a zone clear or discard the newest completed pull.

Applying a different feed endpoint clears the old standalone result. Cached initial zone data
preserves the local identity delivered by the subscription, including after reconnects.
Zone IDs and names may arrive separately. Additional metadata fills in the current encounter,
while a changed known ID clears it even before the new name arrives. Reconnects track new zone
metadata separately from the zone attached to held rows. Delayed initial metadata preserves a
completed pull from the new session too. A real zone boundary or another reconnect resets that
protection. Combatant snapshots use the same
player ID range as spawn lines so NPC jobs cannot enter player totals.
Combat state events require both boolean flags. Missing or malformed flags leave the active pull intact.

The snapshot carries the top 24 damage rows and the local row if ranked lower. Solo and self-first
views keep that row's original rank. Each encounter and display segment retain at most 1024 actor
records. Local and current party members are protected from eviction. Older nonparty records are
retired under pressure, with their damage retained in the encounter total. The title then shows
`[limited actors]`. A returning retired player still contributes damage if its job cache entry has expired.
It starts a new individual row, so individual history
and ranks are limited in that case. The next display segment starts clean.

Appearance profiles export only settings they can apply. Copying a profile saved by an older
build removes its connection and placement fields. Loaded, imported and manually entered dimensions are bounded
to the supported settings ranges before drawing.
Header formats keep the text editor's 128 character limit when loaded or imported. Token values
are inserted once, so text inside a title or duration cannot expand more tokens.

`tests/MeterEngineTests` is a dependency-free harness that drives the engine with synthetic log
lines, including the wire-decode examples from `dps_meter.py`'s docstring. Run it with
`dotnet run --project tests/MeterEngineTests`.

`tests/HeadlessDpsTests` checks the complete DPS path without a display or the game:

```sh
env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/HeadlessDpsTests
```

It compiles the meter, bridge, DPS window and visibility code from the program's source.
A local WebSocket server replays synthetic IINACT events, including fragmented frames and
a real disconnect and reconnect. The checks cover damage totals, pets, healing, deaths,
all three meter styles, encounter endings, zone changes and handoffs to the program.
Drawing calls go to a recording adapter. Game conditions, fonts and textures are stubbed,
so this verifies the state and drawing commands but does not test native rendering in game.
The release workflow also runs the broader regression suite:

```sh
env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/OverlayRegressionTests
python3 tests/test_release_channel.py
```

This suite includes the production plugin lifecycle and all windows with recording service
adapters. It exercises suppressed drawing, delayed queues, concurrent updates, session changes,
profile sharing, extreme geometry and actor retention. It also checks moved meter rows, font loading,
large icons, split zone metadata, reflected damage and malformed WebSocket handshakes and frames.
Font checks cover cached fonts becoming available at different times, nested caption scales,
global UI scaling, custom font scales, alarm wrapping and recovery after drawing errors.
Bar heights are minimums and expand to fit text in DPS and timeline windows. Regression checks
cover consecutive rows, Horizon clip bounds and fonts becoming available at different times.
Overlays reserve their own gaps and suppress implicit ImGui item spacing while drawing.
Both drawing adapters include that spacing and cursor pixel alignment in their layout calculations.
Placement checks include changing meter styles
while unlocking at different UI scales.
Release tests use a recording GitHub CLI
substitute and do not publish or delete live releases.

## Status

Both halves work and are in use in game. The program side is `plugin_link.py` in the
[NyaaTriggers](https://github.com/CateDesu/NyaaTriggers) repo: `MainWindow` feeds `PluginLink`
the callouts (`send_alert`), the fight clock (`send_tick`), the schedule on connect
(`send_timeline`) and the meter once a second during an encounter (`send_dps`). The link is
configured under **Settings > In-Game Overlay**.
