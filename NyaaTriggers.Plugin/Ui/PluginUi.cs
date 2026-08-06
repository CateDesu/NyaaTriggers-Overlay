using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// Owns the window set and decides, once per frame, which boxes should be up.
/// </summary>
internal sealed class PluginUi : IDisposable
{
    private readonly Configuration config;
    private readonly BridgeHost bridge;
    private readonly ScaledFonts fonts;
    private readonly WindowSystem windows = new("NyaaTriggers");
    private readonly TimelineWindow timeline;
    private readonly AlertsWindow alerts;
    private readonly DpsWindow dps;
    private readonly ConfigWindow configWindow;

    internal PluginUi(Configuration config, BridgeHost bridge, ScaledFonts fonts)
    {
        this.config = config;
        this.bridge = bridge;
        this.fonts = fonts;

        this.timeline = new TimelineWindow(config, bridge, fonts);
        this.alerts = new AlertsWindow(config, bridge, fonts);
        this.dps = new DpsWindow(config, bridge, fonts);
        this.configWindow = new ConfigWindow(config, bridge, this);

        this.windows.AddWindow(this.timeline);
        this.windows.AddWindow(this.alerts);
        this.windows.AddWindow(this.dps);
        this.windows.AddWindow(this.configWindow);
    }

    internal void ToggleConfig() => this.configWindow.Toggle();

    internal void OpenConfig() => this.configWindow.IsOpen = true;

    /// <summary>Lock or unlock both boxes. Geometry is only written back while
    /// unlocked, so this is the moment it is worth persisting.</summary>
    internal void SetLocked(bool locked)
    {
        this.config.Locked = locked;
        this.config.Save();
    }

    /// <summary>Whether the boxes were drawable on the last frame, for the
    /// settings window to explain a test callout that goes nowhere.</summary>
    internal bool OverlayVisible { get; private set; } = true;

    internal void Draw()
    {
        // Everything the socket threads queued is applied here, on the draw
        // thread, before anything reads it.
        this.bridge.Update();

        this.OverlayVisible = this.ShouldShowOverlay();
        this.timeline.IsOpen = this.OverlayVisible && this.config.ShowTimeline;
        this.alerts.IsOpen = this.OverlayVisible && this.config.ShowAlerts;

        // The meter only exists while an encounter runs; unlocked keeps it up
        // anyway, since that is when the box is being positioned.
        var dps = this.bridge.Dps;
        this.dps.IsOpen = this.OverlayVisible && this.config.ShowDps &&
            (!this.config.Locked || (dps.Show && dps.Rows.Count > 0));

        this.windows.Draw();
    }

    private bool ShouldShowOverlay()
    {
        // While unlocked the boxes must stay up regardless: that is the state
        // the user is in when positioning them, and hiding them there means
        // they cannot be placed.
        if (!this.config.Locked)
        {
            return true;
        }

        if (Services.Condition[ConditionFlag.BetweenAreas] ||
            Services.Condition[ConditionFlag.BetweenAreas51] ||
            Services.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Services.Condition[ConditionFlag.WatchingCutscene] ||
            Services.Condition[ConditionFlag.WatchingCutscene78])
        {
            return false;
        }

        if (this.config.OnlyInDuty && !Services.Condition[ConditionFlag.BoundByDuty])
        {
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        this.windows.RemoveAllWindows();
        this.fonts.Dispose();
    }
}
