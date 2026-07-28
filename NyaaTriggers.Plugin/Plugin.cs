using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Ui;

namespace NyaaTriggers.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/nyaa";

    private readonly Configuration config;
    private readonly BridgeHost bridge;
    private readonly PluginUi ui;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Services.Initialize(pluginInterface);

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.bridge = new BridgeHost(this.config);
        this.ui = new PluginUi(this.config, this.bridge);

        Services.Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open NyaaTriggers settings. /nyaa lock toggles the overlay lock.",
        });

        pluginInterface.UiBuilder.Draw += this.ui.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.ui.OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += this.ui.OpenConfig;

        this.bridge.Start();

        // A fresh install starts unlocked so the boxes are visible and can be
        // placed; the user locks them once they are where they want them.
        if (!this.config.Locked)
        {
            Services.Log.Information(
                "NyaaTriggers overlay is unlocked. Position the boxes, then tick Lock in /nyaa.");
        }
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "lock":
                this.ui.SetLocked(!this.config.Locked);
                Services.Chat.Print(
                    this.config.Locked
                        ? "[NyaaTriggers] Overlay locked."
                        : "[NyaaTriggers] Overlay unlocked - drag the boxes into place.");
                break;

            case "":
                this.ui.ToggleConfig();
                break;

            default:
                Services.Chat.Print("[NyaaTriggers] Usage: /nyaa  or  /nyaa lock");
                break;
        }
    }

    public void Dispose()
    {
        Services.PluginInterface.UiBuilder.Draw -= this.ui.Draw;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= this.ui.OpenConfig;
        Services.PluginInterface.UiBuilder.OpenMainUi -= this.ui.OpenConfig;
        Services.Commands.RemoveHandler(CommandName);

        this.ui.Dispose();
        this.bridge.Dispose();

        // Geometry is only tracked in memory while unlocked; make sure the last
        // drag survives a reload rather than only a settings click.
        this.config.Save();
    }
}
