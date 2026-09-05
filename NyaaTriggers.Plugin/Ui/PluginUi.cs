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
    // Fade timings mirrored from the alerts box, which keeps its own
    // private. The flash fades on the same clock so the two never split.
    private const float AlarmFadeSeconds = 0.6f;
    private const float AlarmRiseSeconds = 0.12f;

    private readonly Configuration config;
    private readonly BridgeHost bridge;
    private readonly ScaledFonts fonts;
    private readonly WindowSystem windows = new("NyaaTriggers");
    private readonly FlashWindow flash;
    private readonly TimelineWindow timeline;
    private readonly AlertsWindow alerts;
    private readonly DpsWindow dps;
    private readonly ConfigWindow configWindow;

    internal PluginUi(Configuration config, BridgeHost bridge, ScaledFonts fonts)
    {
        this.config = config;
        this.bridge = bridge;
        this.fonts = fonts;

        this.flash = new FlashWindow(config);
        this.timeline = new TimelineWindow(config, bridge, fonts);
        this.alerts = new AlertsWindow(config, bridge, fonts);
        this.dps = new DpsWindow(config, bridge, fonts);
        this.configWindow = new ConfigWindow(config, bridge, this);

        // The flash is added first so the boxes draw over the glow, not under.
        this.windows.AddWindow(this.flash);
        this.windows.AddWindow(this.timeline);
        this.windows.AddWindow(this.alerts);
        this.windows.AddWindow(this.dps);
        this.windows.AddWindow(this.configWindow);
    }

    internal void ToggleConfig() => this.configWindow.Toggle();

    internal void OpenConfig() => this.configWindow.IsOpen = true;

    /// <summary>Lock or unlock the boxes. Geometry is only written back while
    /// unlocked, so this is the moment it is worth persisting.</summary>
    internal void SetLocked(bool locked)
    {
        this.config.Locked = locked;
        this.config.Save();
    }

    /// <summary>Whether the alerts box was drawable on the last frame, for the
    /// settings window to explain a test callout that goes nowhere.</summary>
    internal bool AlertsVisible { get; private set; } = true;

    internal void Draw()
    {
        // Everything the socket threads queued is applied here, on the draw
        // thread, before anything reads it.
        this.bridge.Update();

        this.AlertsVisible = this.ShouldShow(this.config.AlertsOnlyInDuty, this.config.AlertsOnlyInCombat);
        this.alerts.IsOpen = this.AlertsVisible && this.config.ShowAlerts;
        this.timeline.IsOpen = this.ShouldShow(this.config.TimelineOnlyInDuty, this.config.TimelineOnlyInCombat)
            && this.config.ShowTimeline;

        // The meter only exists while an encounter runs, or just ran when the
        // hold-last option keeps the final numbers up. Unlocked keeps it up
        // anyway, since that is when the box is being positioned. A held
        // meter outlasts the only-in-combat filter, the fight it holds is
        // exactly the one that just dropped combat. The duty filter and the
        // cutscene suppression still hide it.
        var dps = this.bridge.Dps;
        var held = this.dps.HasHeldContent;
        this.dps.IsOpen = this.ShouldShow(this.config.DpsOnlyInDuty, this.config.DpsOnlyInCombat && !held)
            && this.config.ShowDps &&
            (!this.config.Locked || (dps.Show && dps.Rows.Count > 0) || held);

        // The screen flash is strictly the raid-night state: locked, alerts
        // on, an alarm live and not filtered out. While unlocked the boxes
        // are being placed and a full screen glow would only annoy. It follows
        // the alerts box's visibility: a suppressed alerts box means its flash
        // is just as unwanted. The pulse is scaled by the alarm's own alpha
        // so the glow fades out with the callout like the box border does,
        // instead of holding full strength until the bridge culls it.
        var alarmAlpha = this.LiveAlarmAlpha();
        this.flash.AlarmAlpha = alarmAlpha;
        this.flash.IsOpen = this.AlertsVisible && this.config.Locked &&
            this.config.ShowAlerts && this.config.AlarmScreenFlash &&
            this.config.AlertsShowAlarm && alarmAlpha > 0.0f;

        this.windows.Draw();
    }

    /// <summary>The strongest alpha among the live alarms, faded exactly as
    /// the alerts box fades each callout. Zero when no alarm is live, which
    /// doubles as the old any-alarm check for the flash gate.</summary>
    private float LiveAlarmAlpha()
    {
        var strongest = 0.0f;
        var now = Environment.TickCount64;
        foreach (var alert in this.bridge.Alerts)
        {
            if (alert.Severity != Severity.Alarm)
            {
                continue;
            }

            var alpha = 1.0f;
            if (this.config.AlertsAnimate)
            {
                var remaining = (alert.ExpiresAt - now) / 1000.0f;
                var age = (now - alert.ShownAt) / 1000.0f;
                alpha = Math.Min(
                    remaining >= AlarmFadeSeconds ? 1.0f : Math.Max(remaining, 0.0f) / AlarmFadeSeconds,
                    age >= AlarmRiseSeconds ? 1.0f : Math.Max(age, 0.0f) / AlarmRiseSeconds);
            }

            strongest = Math.Max(strongest, alpha);
        }

        return strongest;
    }

    private bool ShouldShow(bool onlyInDuty, bool onlyInCombat)
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

        if (onlyInDuty && !Services.Condition[ConditionFlag.BoundByDuty])
        {
            return false;
        }

        if (onlyInCombat && !Services.Condition[ConditionFlag.InCombat])
        {
            return false;
        }

        return true;
    }

    /// <summary>Put every box back at its shipped position and size, for a box
    /// dragged off screen or stranded by a resolution change.</summary>
    internal void ResetPlacement()
    {
        this.timeline.ResetGeometry();
        this.alerts.ResetGeometry();
        this.dps.ResetGeometry();
        this.config.Save();
    }

    public void Dispose()
    {
        this.windows.RemoveAllWindows();
        this.fonts.Dispose();
    }
}
