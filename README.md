# NyaaTriggers Overlay

This is a companion plugin for [NyaaTriggers](https://github.com/CateDesu/NyaaTriggers). It draws the
timeline bars, callouts, and a DPS meter in the game. The meter has three looks: share bars, the
Horizon Overlay's job bars, or kagerou-style text rows. It does not work on its own, and NyaaTriggers does not draw
in the game without it, so you need both.

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

5. Click the "Save" button.
6. Type `/xlplugins`, find **NyaaTriggers**, and install it.

Then type `/nyaa` in game chat to set it up. The boxes start unlocked so you can drag them where you
want. Tick **Lock** when you are happy and clicks pass through to the game again.

## Notes

This is a custom repository and will never be on the official plugin list. Dalamud's rules do not
allow plugins that bridge to ACT, which is what NyaaTriggers is on the other end. IINACT ships from
its own repository for the same reason.

Building it, running it from source, and the protocol it speaks to the app are in
[docs/DEVELOPING.md](docs/DEVELOPING.md).
