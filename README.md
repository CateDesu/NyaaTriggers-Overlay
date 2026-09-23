# NyaaTriggers Overlay

This is a companion plugin for [NyaaTriggers](https://github.com/CateDesu/NyaaTriggers). It draws the
timeline bars, callouts, and three independent meter windows in the game: [LMeter](https://github.com/lichie567/LMeter),
Horizon, and [Kagerou](https://github.com/hibiyasleep/kagerou). The callouts and timeline come from the
program, so for those you need both. If all you want is the DPS meter, the plugin can instead read
the combat feed straight from [IINACT](https://github.com/marzent/IINACT) with no program running:
tick **Standalone meter** in its settings.

Open each meter's section in `/nyaa` and tick its **Enable** checkbox. Each window
keeps its own position, size, appearance and visibility settings. You can enable
several at once or switch between them without losing your settings. Horizon's
options are inside its own section, as are LMeter and Kagerou. Existing settings
carry over to the meter you were using; the other windows start disabled.

After a wipe, each meter can keep the last pull visible until damage starts on the next pull.
Healing and entering combat leave the final numbers in place. Changing zones clears them.

LMeter replaces the old Bars style. It has a compact encounter header, job icons,
flat job-colored bars and DPS, HPS and death counts on the right. The highest DPS
fills its row and the other bars scale against it. Its settings can hide individual
metrics, change colors and spacing, or show compact numbers. Bars placement and
visibility carry over. Kagerou and Horizon keep their own settings.

The Kagerou view includes DPS, Tank, Heal and 24 tabs, job icons, column headings,
an encounter header and a footer with your rank and the party total. Tank sorts by
damage received and Heal sorts by healing. The clock opens the last 20 finished
encounters, the arrow collapses the table, and the menu has quick display options.
History lasts until the plugin reloads.

Unlock the overlay to use these controls, or enable **Use meter controls while locked**
under **Kagerou → Appearance**. That section also selects the view and optional DPS columns.
Under **Kagerou → Column headings**, choose **Dead**, **Deaths**, **D**, or **No letters**
for the death heading. Death counts can align **Left**, **Center**, or **Right**, with
**Center** as the default. Name, DPS, D%, H% and Crit headings can each be hidden
without hiding their values. You can also hide the whole heading row. These options
are available from the Kagerou window's menu too.
Unknown statistics display as a dash. Overheal is unavailable from the raw feed;
older NyaaTriggers versions also omit the new critical hit and healing totals.

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

Building it, running it from source, and the protocol it speaks to the program are in
[docs/DEVELOPING.md](docs/DEVELOPING.md).

The LMeter style follows its [source](https://github.com/lichie567/LMeter) and defaults.
Its MIT notice is included in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
