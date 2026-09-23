# Developing

## Build

Requires the .NET 10 SDK and Dalamud API 15 assemblies. The SDK uses
`~/.xlcore/dalamud/Hooks/dev/` on Linux, or set `DALAMUD_HOME` to the assemblies directory.

```sh
dotnet build NyaaTriggers.Plugin/NyaaTriggers.Plugin.csproj -c Release
```

Output: `NyaaTriggers.Plugin/bin/Release/NyaaTriggers.dll` and `NyaaTriggers/latest.zip`
inside the same release directory.

## Run locally

1. Add the DLL under `/xlsettings` → **Experimental** → **Dev Plugin Locations**.
   On Linux, use its Wine path, such as `Z:\home\you\...\NyaaTriggers.dll`.
2. Enable **NyaaTriggers** under `/xlplugins` → **Dev Tools** → **Installed Dev Plugins**.
3. Open settings with `/nyaa`. Use `/nyaa lock` to toggle placement.

`python3 test_bridge.py` sends sample timelines and callouts. Add `--dps` for meter data.
Requires the Python `websockets` package.

## Test

Run from the repository root:

```sh
dotnet run --project tests/MeterEngineTests
dotnet run --project tests/BridgeTests
env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/HeadlessDpsTests
env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/OverlayRegressionTests
python3 tests/test_release_version.py
python3 tests/test_release_channel.py
```

Drawing tests use recording adapters. Check native rendering in game.

## Meter settings

Configuration version 7 stores `BarsMeter`, `HorizonMeter` and `KagerouMeter`
separately. The inherited flat meter fields remain readable for migration from
older settings and appearance profiles. Rendering and settings controls use each
window's own `MeterSettings`. The bridge shares encounter data between windows.
The LMeter style retains enum value zero and the `BarsMeter` JSON key so saved
settings and profiles still load. Version 7 replaces the old Bars defaults while
preserving placement, visibility and custom dimensions. LMeter bar lengths use
the full incoming roster's maximum DPS before display filters. Header totals use
the full encounter data. A truncated roster cannot supply a total death count
and displays a dash.

Migration preserves the selected meter's placement and enabled state. Other
meters receive separate settings and start disabled. Appearance profiles include
all three meter appearances while preserving window visibility and placement.

## Release

Pushing to `main` builds and publishes `v<base>.<run number>`, updates `pluginmaster.json`,
then prunes older releases. The base comes from `<Version>` in the csproj.
Keep `pluginmaster.json` generated. Tagged releases must match the csproj version and
reference a commit on `main`.

## Protocol

The plugin accepts one client on loopback port **27080**, over IPv4 or IPv6.
Send one JSON object per UTF-8 WebSocket text message, at most 1 MiB. `Origin` headers
are rejected. Check `protocol` in the initial `hello` before sending commands.

Command fields are defined in [BridgeHost.cs](../NyaaTriggers.Plugin/Bridge/BridgeHost.cs),
with runnable examples in [test_bridge.py](../test_bridge.py).
Times are in seconds, numbers must be finite, and DPS rows arrive sorted by DPS with
percentage shares. `hasDamage` includes damage dealt or taken.

DPS rows can append a statistics object after deaths. Its optional numeric fields are
`damage`, `healed`, `healShare`, `crit`, `direct`, `critDirect`, `taken`, `healingTaken`,
`heals`, `overheal` and `hits`. Rates use percentages from 0 to 100. Missing values stay
unknown, so older senders remain usable. Raw-feed critical rates count damaging hits;
aggregate periodic ticks have no per-hit critical flags. Healing includes raw heal
amounts, with no overheal correction. `heals` counts direct healing effects.

The encounter object can also supply `id`, `zone`, `hps` and `participants`. Keep `id`
stable from the live encounter through its final frame so repeated final snapshots
update the same history entry.

End encounters with `show:false` and the final `enc` and `rows`. Held results remain until
new damage or a full clear. Use `keepDps:true` on wipe clears. `dpsRetention:true` in the
greeting advertises this support. Older senders can end the last snapshot with a bare
`{"c":"dps","show":false}`.

## Standalone meter

With **Standalone meter** enabled, the plugin reads IINACT at `ws://127.0.0.1:10501/ws`
while no program session is connected. A program connection takes priority.
