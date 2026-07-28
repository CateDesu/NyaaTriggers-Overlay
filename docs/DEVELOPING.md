# Developing

## Why a plugin

The app used to composite a transparent Qt window over the game: gamescope atoms on Linux, an
always-on-top window on Windows. Both were ways of working around the fact that a separate process
cannot draw inside the game. Neither worked the same way twice, and the Linux path silently did
nothing unless FFXIV happened to be launched inside gamescope. Drawing through Dalamud is the same
on every platform and needs no compositor setup.

## Building

Needs the .NET 10 SDK and a Dalamud install to reference.

```
dotnet build NyaaTriggers.Plugin/NyaaTriggers.Plugin.csproj -c Release
```

`Dalamud.NET.Sdk` locates the assemblies itself: `~/.xlcore/dalamud/Hooks/dev/` on Linux,
`%APPDATA%\XIVLauncher` on Windows, or `$DALAMUD_HOME` if set. It sets that path unconditionally, so
a `Directory.Build.props` override in this repo would not take effect.

The build produces `NyaaTriggers.Plugin/bin/Release/NyaaTriggers.dll` alongside `NyaaTriggers.json`,
and a packaged `NyaaTriggers.Plugin/bin/Release/NyaaTriggers/latest.zip` for distribution.

`packages.lock.json` is committed: the SDK forces `RestorePackagesWithLockFile`, and a submission is
expected to carry it.

## Running it from source

1. `/xlsettings` → **Experimental** → add the full path to `NyaaTriggers.dll` under **Dev Plugin
   Locations**.

   **On Linux this must be a `Z:\` path**, not the Linux one. Dalamud runs inside the Wine prefix,
   where `z:` maps to `/`, so `bin/Release/NyaaTriggers.dll` has to be entered as
   `Z:\home\you\...\bin\Release\NyaaTriggers.dll`. A Linux path is accepted by the settings box and
   then resolves to nothing, so the plugin simply never appears with no error to explain it.

2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **NyaaTriggers**.
3. `/nyaa` opens settings. `/nyaa lock` toggles the lock without opening the window.

`test_bridge.py` drives the plugin on its own, so the drawing can be checked before the app knows
anything about it.

## Releasing

The tag is the version. It must match `<Version>` in the csproj, or the build stops.

```
git tag v0.1.0.1 && git push origin v0.1.0.1
```

That builds against the pinned Dalamud API 15 assemblies, attaches `latest.zip` to a GitHub release,
regenerates `pluginmaster.json` from the built manifest, and commits it to `main`. Anyone who added
the repository URL picks the new version up on their next Dalamud refresh.

Running the workflow by hand is a dry run: it builds and checks, and publishes nothing.

`pluginmaster.json` is generated, never hand-edited. `tools/make_pluginmaster.py` reads the manifest
DalamudPackager already produces and adds only the release URL and timestamp, so the two cannot
drift. Stable channel only for now; a testing channel would need its keys merged into the same entry
rather than rewriting it.

CI has no XIVLauncher, so the workflow unpacks
`https://goatcorp.github.io/dalamud-distrib/api15/latest.zip` and points `DALAMUD_HOME` at it. That
URL is pinned per API level on purpose: bare `latest.zip` tracks whatever is current, so an API bump
would rebuild this against headers it was never written for.

## The link

The plugin listens on loopback and the app connects to it, so the two can start in either order and
the app's existing reconnect handling carries over from how it already talks to IINACT.

- Transport: WebSocket, text frames, one JSON object per frame.
- Bound to `127.0.0.1` and `::1` only, default port **27080**. Both loopback families are bound
  deliberately: binding only IPv4 leaves a client that resolved `localhost` to `::1` connected to
  nothing, which looks exactly like the overlay being broken.
- One client at a time. A new connection replaces the previous session.
- Any handshake carrying an `Origin` header is refused. WebSocket is exempt from the same-origin
  policy, so without this any page you happened to be browsing could open this socket and inject
  callouts. Browsers always send `Origin`; the app never does.

### App → plugin

| Message | Meaning |
|---|---|
| `{"c":"tick","t":12.5}` | Fight clock, in timeline seconds. The plugin interpolates from here, so this only has to beat drift, not the frame rate. |
| `{"c":"timeline","v":[[18.0,"Wing"],[24.5,"Dive"]]}` | Replace the schedule. `[time, label]` pairs in timeline seconds, same shape as the app's `TimelineEngine.upcoming()`. |
| `{"c":"alert","text":"Stack","sev":"alarm","ttl":4.0}` | Show a callout. `sev` is `info`, `alert` or `alarm`; `ttl` is optional and falls back to the configured alert time. |
| `{"c":"clear"}` | Drop the schedule and any live alerts. Send on zone change and fight end. |
| `{"c":"ping"}` | Liveness check; answered with `{"ev":"pong"}`. |

Unknown commands are ignored rather than treated as errors, so a newer app can talk to an older
plugin.

### Plugin → app

| Message | Meaning |
|---|---|
| `{"ev":"hello","protocol":1,"plugin":"0.1.0"}` | Sent on connect, always the first frame. Check `protocol` before driving it. |
| `{"ev":"pong"}` | Reply to `ping`. |

`protocol` is bumped only on an incompatible change to the tables above.

## Status

Both halves are written and talk to each other. The app side is `plugin_client.py` in the
[NyaaTriggers](https://github.com/CateDesu/NyaaTriggers) repo, wired into `MainWindow._emit_alert`
for callouts and `TimelineEngine.upcoming()` for the schedule, with the link configured under
**Settings - In-Game Display**.

Not yet confirmed in a real game session.
