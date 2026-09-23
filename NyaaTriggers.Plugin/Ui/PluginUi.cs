using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

internal sealed class PluginUi : IDisposable
{
    // Match AlertsWindow timings so the screen flash and callout fade together.
    private const float AlarmFadeSeconds = 0.6f;
    private const float AlarmRiseSeconds = 0.12f;

    private bool disposed;
    private readonly Configuration config;
    private readonly BridgeHost bridge;
    private readonly ScaledFonts fonts;
    private readonly WindowSystem windows = new("NyaaTriggers");
    private readonly FlashWindow flash;
    private readonly TimelineWindow timeline;
    private readonly AlertsWindow alerts;
    private readonly DpsWindow[] meters;
    private readonly ConfigWindow configWindow;

    internal PluginUi(Configuration config, BridgeHost bridge, ScaledFonts fonts)
    {
        this.config = config;
        this.bridge = bridge;
        this.fonts = fonts;

        this.flash = new FlashWindow(config);
        this.timeline = new TimelineWindow(config, bridge, fonts);
        this.alerts = new AlertsWindow(config, bridge, fonts);
        this.meters = new[]
        {
            new DpsWindow(config, bridge, fonts, config.GetMeter(DpsMeterStyle.LMeter)),
            new DpsWindow(config, bridge, fonts, config.GetMeter(DpsMeterStyle.HorizonOverlay)),
            new DpsWindow(config, bridge, fonts, config.GetMeter(DpsMeterStyle.Kagerou)),
        };
        this.configWindow = new ConfigWindow(config, bridge, this);

        // Draw the flash behind the other windows.
        this.windows.AddWindow(this.flash);
        this.windows.AddWindow(this.timeline);
        this.windows.AddWindow(this.alerts);
        foreach (var meter in this.meters) this.windows.AddWindow(meter);
        this.windows.AddWindow(this.configWindow);
    }

    internal void ToggleConfig()
    {
        lock (this.bridge.StateLock)
        {
            if (!this.disposed) this.configWindow.Toggle();
        }
    }

    internal void OpenConfig()
    {
        lock (this.bridge.StateLock)
        {
            if (!this.disposed) this.configWindow.IsOpen = true;
        }
    }

    internal void SetLocked(bool locked)
    {
        lock (this.bridge.StateLock)
        {
            if (this.disposed) return;
            this.config.Locked = locked;
            this.config.Save();
        }
    }

    internal bool AlertsVisible { get; private set; } = true;

    /// <summary>Feed processing continues when Dalamud suppresses drawing.</summary>
    internal void Update()
    {
        lock (this.bridge.StateLock)
        {
            if (!this.disposed) this.bridge.Update();
        }
    }

    internal void Draw()
    {
        lock (this.bridge.StateLock)
        {
            if (!this.disposed) this.DrawWindows();
        }
    }

    private void DrawWindows()
    {
        this.AlertsVisible = this.ShouldShow(this.config.AlertsOnlyInDuty, this.config.AlertsOnlyInCombat);
        this.alerts.IsOpen = this.AlertsVisible && this.config.ShowAlerts;
        this.timeline.IsOpen = this.ShouldShow(this.config.TimelineOnlyInDuty, this.config.TimelineOnlyInCombat)
            && this.config.ShowTimeline;

        // Held rows bypass combat filtering but still obey duty and cutscene filters.
        foreach (var window in this.meters)
        {
            var meter = window.Meter;
            var dps = window.CurrentDps;
            var held = window.HasHeldContent;
            window.IsOpen = this.ShouldShow(meter.DpsOnlyInDuty, meter.DpsOnlyInCombat && !held)
                && meter.ShowDps &&
                (!this.config.Locked || (dps.Show && dps.Rows.Count > 0) || held);
        }

        var alarmAlpha = this.LiveAlarmAlpha();
        this.flash.AlarmAlpha = alarmAlpha;
        this.flash.IsOpen = this.AlertsVisible && this.config.Locked &&
            this.config.ShowAlerts && this.config.AlarmScreenFlash &&
            this.config.AlertsShowAlarm && alarmAlpha > 0.0f;

        this.windows.Draw();
    }

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
        // Keep unlocked windows visible for placement.
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

    internal void ResetPlacement()
    {
        this.timeline.ResetGeometry();
        this.alerts.ResetGeometry();
        foreach (var meter in this.meters) meter.ResetGeometry();
        this.config.Save();
    }

    public void Dispose()
    {
        lock (this.bridge.StateLock)
        {
            if (this.disposed) return;
            this.disposed = true;
            this.windows.RemoveAllWindows();
            this.fonts.Dispose();
        }
    }
}
