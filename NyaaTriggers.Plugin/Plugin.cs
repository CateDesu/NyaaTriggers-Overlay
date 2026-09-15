using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Ui;

namespace NyaaTriggers.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/nyaa";

    private readonly Configuration config;
    private readonly BridgeHost bridge;
    private readonly ScaledFonts fonts;
    private readonly PluginUi ui;
    private readonly bool commandRegistered;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Services.Initialize(pluginInterface);

        this.config = Configuration.Load(pluginInterface.GetPluginConfig, pluginInterface.ConfigFile.FullName);
        if (this.config.Version < 4)
        {
            if (this.config.Version < 2)
            {
                this.config.MigrateFromV1();
            }

            if (this.config.Version < 3)
            {
                this.config.MigrateFromV2();
            }

            this.config.MigrateFromV3();
            this.config.Save();
        }

        this.config.Sanitize();
        this.bridge = new BridgeHost(this.config);
        ScaledFonts? fonts = null;
        PluginUi? ui = null;
        try
        {
            this.fonts = fonts = new ScaledFonts();
            this.ui = ui = new PluginUi(this.config, this.bridge, this.fonts);
            this.bridge.Start();
            this.commandRegistered = Services.Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open NyaaTriggers settings. /nyaa lock toggles the overlay lock.",
            });

            Services.Framework.Update += this.OnFrameworkUpdate;
            pluginInterface.UiBuilder.Draw += this.ui.Draw;
            pluginInterface.UiBuilder.OpenConfigUi += this.ui.OpenConfig;
            pluginInterface.UiBuilder.OpenMainUi += this.ui.OpenConfig;

            if (!this.config.Locked)
            {
                Services.Log.Information(
                    "NyaaTriggers overlay is unlocked. Position the boxes, then tick Lock in /nyaa.");
            }
        }
        catch
        {
            Services.Framework.Update -= this.OnFrameworkUpdate;
            if (ui != null)
            {
                pluginInterface.UiBuilder.Draw -= ui.Draw;
                pluginInterface.UiBuilder.OpenConfigUi -= ui.OpenConfig;
                pluginInterface.UiBuilder.OpenMainUi -= ui.OpenConfig;
            }

            if (this.commandRegistered)
            {
                Services.Commands.RemoveHandler(CommandName);
            }

            try
            {
                if (ui != null)
                {
                    ui.Dispose();
                }
                else
                {
                    fonts?.Dispose();
                }
            }
            finally
            {
                this.bridge.Dispose();
            }

            throw;
        }
    }

    private void OnFrameworkUpdate(IFramework framework) => this.ui.Update();

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
        Services.Framework.Update -= this.OnFrameworkUpdate;
        Services.PluginInterface.UiBuilder.Draw -= this.ui.Draw;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= this.ui.OpenConfig;
        Services.PluginInterface.UiBuilder.OpenMainUi -= this.ui.OpenConfig;
        if (this.commandRegistered)
        {
            Services.Commands.RemoveHandler(CommandName);
        }

        try
        {
            this.ui.Dispose();
        }
        finally
        {
            this.bridge.Dispose();
        }

        // Geometry is only tracked in memory while unlocked; make sure the last
        // drag survives a reload rather than only a settings click.
        this.config.Save();
    }
}
