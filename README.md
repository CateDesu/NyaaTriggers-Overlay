# NyaaTriggers companion plugin

A Dalamud plugin that draws NyaaTriggers' timeline bars and alert callouts inside FFXIV.

The desktop app still does all the thinking: it reads the combat log from IINACT, runs the trigger
engines, and speaks the callouts. This plugin only draws what the app tells it to draw. It reads
nothing about the game beyond whether the overlay should be visible, and it never acts on your behalf.

## Why a plugin

The app used to composite a transparent Qt window over the game: gamescope atoms on Linux, an
always-on-top window on Windows. Both were ways of working around the fact that a separate process
cannot draw inside the game. Neither worked the same way twice, and the Linux path silently did
nothing unless FFXIV happened to be launched inside gamescope. Drawing through Dalamud is the same
on every platform and needs no compositor setup.

## Building

Needs the .NET 10 SDK and a Dalamud install to reference.

```
dotnet build plugin/NyaaTriggers.Plugin/NyaaTriggers.Plugin.csproj -c Release
```

`Dalamud.NET.Sdk` locates the assemblies itself: `~/.xlcore/dalamud/Hooks/dev/` on Linux,
`%APPDATA%\XIVLauncher` on Windows, or `$DALAMUD_HOME` if set. It sets that path unconditionally, so
a `Directory.Build.props` override in this repo would not take effect.

The build produces `bin/Release/NyaaTriggers.dll` alongside `NyaaTriggers.json`, and a packaged
`bin/Release/NyaaTriggers/latest.zip` for distribution.

`packages.lock.json` is committed: the SDK forces `RestorePackagesWithLockFile`, and a submission is
expected to carry it.

## Running it during development

1. `/xlsettings` → **Experimental** → add the full path to `NyaaTriggers.dll` under **Dev Plugin Locations**.

   **On Linux this must be a `Z:\` path**, not the Linux one. Dalamud runs inside the Wine prefix,
   where `z:` maps to `/`, so `bin/Release/NyaaTriggers.dll` has to be entered as
   `Z:\home\you\...\bin\Release\NyaaTriggers.dll`. A Linux path is accepted by the settings box and
   then resolves to nothing, so the plugin simply never appears with no error to explain it.

2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **NyaaTriggers**.
3. `/nyaa` opens settings. The boxes start unlocked so you can drag them; tick **Lock** when they are
   where you want them, and clicks pass through to the game from then on.

`/nyaa lock` toggles the lock without opening the window.

## Distribution

**Not implemented yet.** There is no repo JSON and the release workflow does not build or upload the
plugin; right now the only way to run it is the dev-plugin route above.

The plan is a **custom Dalamud repository**, not the official plugin list. The official
[plugin restrictions](https://dalamud.dev/plugin-publishing/restrictions/) rule out plugins that act
as a bridge to the ACT/raid-logging ecosystem, which is exactly what the app on the other end of this
socket is. IINACT ships from its own repo for the same reason.

What that still needs: a repo JSON served over HTTP with `DownloadLinkInstall` / `DownloadLinkUpdate`
pointing at a release asset, and a workflow step that builds the plugin and uploads `latest.zip`.

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

Unknown commands are ignored rather than treated as errors, so a newer app can talk to an older plugin.

### Plugin → app

| Message | Meaning |
|---|---|
| `{"ev":"hello","protocol":1,"plugin":"0.1.0"}` | Sent on connect. Check `protocol` before driving it. |
| `{"ev":"pong"}` | Reply to `ping`. |

`protocol` is bumped only on an incompatible change to the table above.

## Status

The plugin half is written; the app half is not yet. `MainWindow._emit_alert` in the Python app is
the single fan-out point where the push belongs, and `TimelineEngine.upcoming()` already produces the
schedule in the shape the `timeline` command wants.

`test_bridge.py` in this directory drives the plugin on its own, so the drawing can be checked
before the app knows anything about it.
