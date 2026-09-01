using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// Callout text, colour-coded by severity and fading out as it expires. Text
/// is wrapped rather than clipped: a long callout truncated at the box edge is
/// worse than useless mid-pull. Wrapping is done by hand so each line can be
/// aligned, which ImGui's own wrapped text cannot do.
/// </summary>
internal sealed class AlertsWindow : OverlayWindow
{
    /// <summary>Seconds of fade at the end of an alert's life.</summary>
    private const float FadeSeconds = 0.6f;

    /// <summary>Seconds of grow-in at the start, so a new alert reads as new
    /// even when it replaces one with the same text.</summary>
    private const float RiseSeconds = 0.12f;

    /// <summary>Gap between one callout's block and the next.</summary>
    private const float BlockSpacing = 4.0f;

    private readonly BridgeHost bridge;

    internal AlertsWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts)
        : base("NyaaTriggers Alerts###nyaaAlerts", config, fonts)
    {
        this.bridge = bridge;
    }

    protected override Vector2 StoredPosition
    {
        get => this.Config.AlertsPos;
        set => this.Config.AlertsPos = value;
    }

    protected override Vector2 StoredSize
    {
        get => this.Config.AlertsSize;
        set => this.Config.AlertsSize = value;
    }

    internal override void ResetGeometry()
    {
        var fresh = new Configuration();
        this.StoredPosition = fresh.AlertsPos;
        this.StoredSize = fresh.AlertsSize;
        this.ForceGeometry();
    }

    protected override float TextScale => this.Config.AlertsTextScale;

    protected override float BgOpacity => this.Config.AlertsBgOpacity;

    protected override TextEffectStyle TextEffect => this.Config.AlertsTextEffect;

    protected override int EffectThickness => this.Config.AlertsEffectThickness;

    protected override Vector4 EffectColor => this.Config.AlertsEffectColor;

    /// <summary>One callout laid out for drawing: the wrapped or elided
    /// lines, the severity colour, the fade, and whether it is an alarm.
    /// Lines are resolved up front so the bottom-anchored layout can total
    /// the stack's height before anything is placed.</summary>
    private readonly record struct DrawItem(List<string> Lines, Vector4 Color, float Alpha, bool IsAlarm);

    /// <summary>Scratch for the collect-then-draw pass, cleared each frame.
    /// The per-callout line lists still allocate, as the wrapped draw always
    /// did; the outer list no longer does.</summary>
    private readonly List<DrawItem> items = new();

    protected override void DrawContent()
    {
        var items = this.CollectItems();

        if (this.Config.AlertsAnchorBottom && items.Count > 0)
        {
            var lineHeight = ImGui.GetTextLineHeight();
            var total = 0.0f;
            foreach (var item in items)
            {
                total += (item.Lines.Count * lineHeight) + BlockSpacing;
            }

            var slack = ImGui.GetContentRegionAvail().Y - total;
            if (slack > 0.0f)
            {
                ImGui.Dummy(new Vector2(1.0f, slack));
            }
        }

        // The box border pulses while an alarm is up, fading with the alarm
        // itself rather than cutting out at the expiry tick.
        var alarmAlpha = 0.0f;
        foreach (var item in items)
        {
            this.DrawAlert(item);
            if (item.IsAlarm)
            {
                alarmAlpha = Math.Max(alarmAlpha, item.Alpha);
            }
        }

        if (alarmAlpha > 0.0f && this.Config.AlertsAlarmFlash)
        {
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetWindowPos();
            var phase = (float)((Math.Sin(Environment.TickCount64 / 140.0) * 0.3) + 0.7);
            drawList.AddRect(
                pos,
                pos + ImGui.GetWindowSize(),
                ToColor(WithAlpha(this.Config.ColorAlarm, alarmAlpha * phase)),
                4.0f,
                ImDrawFlags.None,
                2.0f);
        }
    }

    /// <summary>The visible callouts in draw order, filters applied. An empty
    /// stack draws samples while unlocked so the box is never a blank frame.</summary>
    private List<DrawItem> CollectItems()
    {
        var items = this.items;
        items.Clear();
        var alerts = this.bridge.Alerts;
        if (alerts.Count == 0)
        {
            if (!this.Config.Locked)
            {
                items.Add(this.MakeItem("Sample callout", this.Config.ColorAlarm, 1.0f, false));
                items.Add(this.MakeItem("Sample callout", this.Config.ColorAlert, 1.0f, false));
            }

            return items;
        }

        // The newest alert lives at the end of the list. Either way the stack
        // is the most recent few, filtered severities costing no slot; the
        // order setting only picks which way up they stack.
        var max = Math.Clamp(this.Config.AlertsMaxVisible, 1, 8);
        for (var i = alerts.Count - 1; i >= 0 && items.Count < max; i--)
        {
            this.AddItem(items, alerts[i]);
        }

        if (this.Config.AlertOrder == AlertOrder.OldestFirst)
        {
            items.Reverse();
        }

        return items;
    }

    private void AddItem(List<DrawItem> items, ActiveAlert alert)
    {
        var color = alert.Severity switch
        {
            Severity.Alarm => this.Config.ColorAlarm,
            Severity.Alert => this.Config.ColorAlert,
            _ => this.Config.ColorInfo,
        };

        var visible = alert.Severity switch
        {
            Severity.Alarm => this.Config.AlertsShowAlarm,
            Severity.Alert => this.Config.AlertsShowAlert,
            _ => this.Config.AlertsShowInfo,
        };

        if (!visible)
        {
            return;
        }

        var alpha = 1.0f;
        if (this.Config.AlertsAnimate)
        {
            var now = Environment.TickCount64;
            var remaining = (alert.ExpiresAt - now) / 1000.0f;
            var age = (now - alert.ShownAt) / 1000.0f;
            alpha = Math.Min(
                remaining >= FadeSeconds ? 1.0f : Math.Max(remaining, 0.0f) / FadeSeconds,
                age >= RiseSeconds ? 1.0f : Math.Max(age, 0.0f) / RiseSeconds);
        }

        var text = alert.Text;
        if (this.Config.AlertsCollapseDupes && alert.Count > 1)
        {
            text = $"{text} ×{Math.Min(alert.Count, 99)}";
        }

        items.Add(this.MakeItem(text, color, alpha, alert.Severity == Severity.Alarm));
    }

    /// <summary>Resolve one callout's lines: wrapped to the box width, or one
    /// elided line when wrapping is off.</summary>
    private DrawItem MakeItem(string text, Vector4 color, float alpha, bool isAlarm)
    {
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var lines = this.Config.AlertsWrap
            ? WrapLines(text, width)
            : new List<string> { Elide(text, width) };
        return new DrawItem(lines, color, alpha, isAlarm);
    }

    private void DrawAlert(DrawItem item)
    {
        var drawList = ImGui.GetWindowDrawList();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var origin = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();

        if (this.Config.AlertsSeverityTint)
        {
            var plate = WithAlpha(
                item.Color,
                Math.Clamp(this.Config.AlertsSeverityTintOpacity, 0.0f, 1.0f) * item.Alpha);
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(width, item.Lines.Count * lineHeight),
                ToColor(plate),
                4.0f);
        }

        var y = 0.0f;
        foreach (var line in item.Lines)
        {
            var lineWidth = ImGui.CalcTextSize(line).X;
            var x = this.Config.AlertsAlign switch
            {
                TextAlign.Center => Math.Max((width - lineWidth) * 0.5f, 0.0f),
                TextAlign.Right => Math.Max(width - lineWidth, 0.0f),
                _ => 0.0f,
            };

            this.DrawStyledText(
                drawList,
                origin + new Vector2(x, y),
                WithAlpha(item.Color, item.Alpha),
                line);
            y += lineHeight;
        }

        // Reserve the block so the next callout lands underneath it: the text
        // goes straight to the draw list and takes no layout space by default.
        ImGui.Dummy(new Vector2(width, y + BlockSpacing));
    }

    /// <summary>Greedy word wrap measured with the current font. A word wider
    /// than the box is left to overflow and be clipped rather than broken
    /// mid-word.</summary>
    private static List<string> WrapLines(string text, float width)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' '))
        {
            if (current.Length == 0)
            {
                current = word;
                continue;
            }

            var candidate = current + " " + word;
            if (ImGui.CalcTextSize(candidate).X > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0 || lines.Count == 0)
        {
            lines.Add(current);
        }

        return lines;
    }
}
