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

    /// <summary>Gap under a callout's text before its remaining-time strip,
    /// and the strip's own height.</summary>
    private const float LifelineGap = 2.0f;
    private const float LifelineHeight = 2.0f;

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

    protected override float FadeOpacity => this.Config.AlertsFade;

    protected override TextEffectStyle TextEffect => this.Config.AlertsTextEffect;

    protected override int EffectThickness => this.Config.AlertsEffectThickness;

    protected override Vector4 EffectColor => this.Config.AlertsEffectColor;

    /// <summary>One callout laid out for drawing: the wrapped or elided
    /// lines, the severity colour, the fade, whether it is an alarm, the
    /// alarm's size multiplier, its line height at that size, and the share
    /// of its lifetime still left. Lines and line height are resolved up
    /// front so the bottom-anchored layout can total the stack's height
    /// before anything is placed.</summary>
    private readonly record struct DrawItem(
        List<string> Lines, Vector4 Color, float Alpha, bool IsAlarm,
        float Scale, float LineHeight, float Life);

    /// <summary>Scratch for the collect-then-draw pass, cleared each frame.
    /// The per-callout line lists still allocate, as the wrapped draw always
    /// did; the outer list no longer does.</summary>
    private readonly List<DrawItem> items = new();

    protected override void DrawContent()
    {
        var items = this.CollectItems();

        if (this.Config.AlertsAnchorBottom && items.Count > 0)
        {
            var total = 0.0f;
            foreach (var item in items)
            {
                total += this.BlockHeight(item);
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
                // Full lifelines: the strip previews solid while idle.
                items.Add(this.MakeItem("Sample callout", this.Config.ColorAlarm, 1.0f, false, 1.0f, 1.0f));
                items.Add(this.MakeItem("Sample callout", this.Config.ColorAlert, 1.0f, false, 1.0f, 1.0f));
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

        var now = Environment.TickCount64;
        var alpha = 1.0f;
        if (this.Config.AlertsAnimate)
        {
            var remaining = (alert.ExpiresAt - now) / 1000.0f;
            var age = (now - alert.ShownAt) / 1000.0f;
            alpha = Math.Min(
                remaining >= FadeSeconds ? 1.0f : Math.Max(remaining, 0.0f) / FadeSeconds,
                age >= RiseSeconds ? 1.0f : Math.Max(age, 0.0f) / RiseSeconds);
        }

        // Share of the callout's life still left, for the strip under it. A
        // merged repeat resets both ends, so the strip refills with it.
        var span = alert.ExpiresAt - alert.ShownAt;
        var life = span > 0
            ? Math.Clamp((alert.ExpiresAt - now) / (float)span, 0.0f, 1.0f)
            : 1.0f;

        var scale = alert.Severity == Severity.Alarm
            ? Math.Clamp(this.Config.AlertsAlarmScale, 1.0f, 2.0f)
            : 1.0f;

        var text = alert.Text;
        if (this.Config.AlertsCollapseDupes && alert.Count > 1)
        {
            text = $"{text} ×{Math.Min(alert.Count, 99)}";
        }

        items.Add(this.MakeItem(text, color, alpha, alert.Severity == Severity.Alarm, scale, life));
    }

    /// <summary>Resolve one callout's lines: wrapped to the box width, or one
    /// elided line when wrapping is off. An alarm scaled up is measured in its
    /// own font so the wrap and the line height match what DrawAlert paints.</summary>
    private DrawItem MakeItem(string text, Vector4 color, float alpha, bool isAlarm, float scale, float life)
    {
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        if (scale > 1.0f)
        {
            var handle = this.Fonts.Get(this.TextPx * scale);
            if (handle is { Available: true })
            {
                using (handle.Push())
                {
                    return this.MakeItemMeasured(text, color, alpha, isAlarm, scale, life, width);
                }
            }

            // Bucket still building: wrap against a narrowed width and scale
            // the line height, a close guess at the scaled layout for the few
            // frames until the atlas catches up.
            var item = this.MakeItemMeasured(text, color, alpha, isAlarm, scale, life, width / scale);
            return item with { LineHeight = item.LineHeight * scale };
        }

        return this.MakeItemMeasured(text, color, alpha, isAlarm, scale, life, width);
    }

    private DrawItem MakeItemMeasured(
        string text, Vector4 color, float alpha, bool isAlarm, float scale, float life, float width)
    {
        var lines = this.Config.AlertsWrap
            ? WrapLines(text, width)
            : new List<string> { Elide(text, width) };
        return new DrawItem(lines, color, alpha, isAlarm, scale, ImGui.GetTextLineHeight(), life);
    }

    /// <summary>What one callout's block occupies vertically, strip and block
    /// gap included.</summary>
    private float BlockHeight(DrawItem item)
    {
        var height = item.Lines.Count * item.LineHeight;
        if (this.Config.AlertsLifeline)
        {
            height += LifelineGap + LifelineHeight;
        }

        return height + BlockSpacing;
    }

    private void DrawAlert(DrawItem item)
    {
        var drawList = ImGui.GetWindowDrawList();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var origin = ImGui.GetCursorScreenPos();

        if (this.Config.AlertsSeverityTint)
        {
            var plate = WithAlpha(
                item.Color,
                Math.Clamp(this.Config.AlertsSeverityTintOpacity, 0.0f, 1.0f) * item.Alpha);
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(width, this.BlockHeight(item) - BlockSpacing),
                ToColor(plate),
                4.0f);
        }

        // An alarm scaled up draws in its own font. The layout already
        // reserved its height. While the bucket builds, stretch the window
        // font instead, the same fallback the base draw uses.
        if (item.Scale > 1.0f)
        {
            var handle = this.Fonts.Get(this.TextPx * item.Scale);
            if (handle is { Available: true })
            {
                using (handle.Push())
                {
                    this.DrawAlertLines(drawList, item, origin, width);
                }
            }
            else
            {
                var fontSize = ImGui.GetFont().FontSize;
                var restore = fontSize > 0.0f ? this.TextPx / fontSize : 1.0f;
                ImGui.SetWindowFontScale(restore * item.Scale);
                try
                {
                    this.DrawAlertLines(drawList, item, origin, width);
                }
                finally
                {
                    ImGui.SetWindowFontScale(restore);
                }
            }
        }
        else
        {
            this.DrawAlertLines(drawList, item, origin, width);
        }

        // The remaining-time strip empties as the callout ages. It hugs the
        // box edge the alignment points at, so a right aligned stack drains
        // toward the left and a centred one toward its middle.
        if (this.Config.AlertsLifeline)
        {
            var fillWidth = width * Math.Clamp(item.Life, 0.0f, 1.0f);
            if (fillWidth > 0.0f)
            {
                var stripY = origin.Y + (item.Lines.Count * item.LineHeight) + LifelineGap;
                var stripX = this.Config.AlertsAlign switch
                {
                    TextAlign.Right => origin.X + width - fillWidth,
                    TextAlign.Center => origin.X + ((width - fillWidth) * 0.5f),
                    _ => origin.X,
                };
                drawList.AddRectFilled(
                    new Vector2(stripX, stripY),
                    new Vector2(stripX + fillWidth, stripY + LifelineHeight),
                    ToColor(WithAlpha(item.Color, 0.8f * item.Alpha)),
                    1.0f);
            }
        }

        // Reserve the block so the next callout lands underneath it: the text
        // goes straight to the draw list and takes no layout space by default.
        ImGui.Dummy(new Vector2(width, this.BlockHeight(item)));
    }

    private void DrawAlertLines(ImDrawListPtr drawList, DrawItem item, Vector2 origin, float width)
    {
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
            y += item.LineHeight;
        }
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
