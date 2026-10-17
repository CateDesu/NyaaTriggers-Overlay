# NyaaTriggers Overlay

A companion plugin for [NyaaTriggers](https://github.com/CateDesu/NyaaTriggers) that displays
timelines, callouts, and three independent DPS meters in game: [LMeter](https://github.com/lichie567/LMeter),
Horizon, and [Kagerou](https://github.com/hibiyasleep/kagerou).

Timelines and callouts need the program running. For meters alone, tick **Standalone meter**
to read combat data directly from [IINACT](https://github.com/marzent/IINACT).

## Installing

1. Install [FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) and enable Dalamud in
   its settings. You have to run the game through FFXIVQuickLauncher for any of this to work.
2. Open Dalamud settings by typing `/xlsettings` in game chat.
3. Go to the "Experimental" tab.
4. Find the "Custom Plugin Repositories" section, agree with the listed terms if needed, and paste
   this link into the text input field:

   ```
   https://raw.githubusercontent.com/CateDesu/NyaaTriggers-Overlay/main/pluginmaster.json
   ```

5. Click **+** to add the repository, check that it is enabled, then click **Save**.
6. Type `/xlplugins`, find **NyaaTriggers**, and install it.

## Setup

Type `/nyaa` to open settings. Enable timelines and callouts under **Boxes**, and tick **Enable**
in each meter's section to show it. Each meter keeps its own position, size and appearance.
You can show several meters at once.

Drag and resize the unlocked windows, then tick **Lock** to let clicks pass through.
`/nyaa lock` toggles the lock. Kagerou can keep its controls clickable with
**Kagerou → Appearance → Use meter controls while locked**.

For timelines and callouts, run the NyaaTriggers program. It connects automatically when the
ports match; the default is **27080**. Check **Settings → In-Game Overlay** in the program and
**Link** in the plugin for connection status.

For meters without the program, [install and enable IINACT](https://www.iinact.com/installation/)
and tick **Standalone meter** under **Link**. The default feed is `ws://127.0.0.1:10501/ws`.
A connected NyaaTriggers program takes priority over this feed.

Under each meter's **Display** section, **Keep the last encounter on screen** keeps final results
until the next pull deals or takes damage.
Healing and entering combat leave those results in place. Changing zones clears them.

## Notes

The plugin is distributed through this custom repository. Dalamud's
[plugin guidelines](https://dalamud.dev/plugin-publishing/restrictions/) exclude parsing and DPS meters
from its official repository.

Building it, running it from source, and the protocol it speaks to the program are in
[docs/DEVELOPING.md](docs/DEVELOPING.md).

The LMeter style follows its [source](https://github.com/lichie567/LMeter) and defaults.
Its MIT notice is included in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
