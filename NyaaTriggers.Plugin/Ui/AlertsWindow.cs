using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>Wrap callouts manually so each line can use the configured alignment.</summary>
internal sealed class AlertsWindow : OverlayWindow
{
    private const float FadeSeconds = 0.6f;

    private const float RiseSeconds = 0.12f;

    private const float BlockSpacing = 4.0f;

    private const float LifelineGap = 2.0f;
    private const float LifelineHeight = 2.0f;

    private readonly BridgeHost bridge;
    private readonly Dictionary<LayoutKey, List<MeasuredLine>> layouts = new();
    private readonly record struct LayoutKey(string Text, float Width, ImFontPtr Font, float FontSize, int Generation, bool Wrap);
    private readonly record struct MeasuredLine(string Text, float Width);

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

    /// <summary>Resolve line layout before drawing so a stack anchored at the bottom can be
    /// positioned from its total height.</summary>
    private readonly record struct DrawItem(
        List<MeasuredLine> Lines, Vector4 Color, float Alpha, bool IsAlarm,
        float Scale, float LineHeight, float MeasuredHeight, float Life);

    /// <summary>Reuse drawing storage each frame. Cache text layouts until the text or font
    /// changes.</summary>
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

        // Fade the alarm border with the callout.
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

    /// <summary>Collect visible callouts in display order. Use samples when empty and
    /// unlocked.</summary>
    private List<DrawItem> CollectItems()
    {
        var items = this.items;
        items.Clear();
        var alerts = this.bridge.Alerts;
        if (alerts.Count == 0)
        {
            if (!this.Config.Locked)
            {
                items.Add(this.MakeItem("Sample callout", this.Config.ColorAlarm, 1.0f, false, 1.0f, 1.0f));
                items.Add(this.MakeItem("Sample callout", this.Config.ColorAlert, 1.0f, false, 1.0f, 1.0f));
            }

            return items;
        }

        // Take the newest visible alerts before applying display order. Filtered severities
        // do not consume slots.
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

        // A merged repeat resets both timestamps, refilling the remaining time strip.
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

    /// <summary>Measure wrapped or truncated lines using the alarm font size so layout
    /// matches drawing.</summary>
    private DrawItem MakeItem(string text, Vector4 color, float alpha, bool isAlarm, float scale, float life)
    {
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        using (this.UseFont(this.TextPx * scale))
        {
            return this.MakeItemMeasured(text, color, alpha, isAlarm, scale, life, width);
        }
    }

    private DrawItem MakeItemMeasured(
        string text, Vector4 color, float alpha, bool isAlarm, float scale, float life, float width)
    {
        var key = new LayoutKey(text, width, ImGui.GetFont(),
            ImGui.GetFontSize(), this.Fonts.Generation, this.Config.AlertsWrap);
        if (!this.layouts.TryGetValue(key, out var lines))
        {
            if (this.layouts.Count >= 16)
            {
                this.layouts.Clear();
            }

            var wrapped = this.Config.AlertsWrap ? WrapLines(text, width) : new List<string> { Elide(text, width) };
            lines = new List<MeasuredLine>(wrapped.Count);
            foreach (var line in wrapped)
            {
                lines.Add(new MeasuredLine(line, ImGui.CalcTextSize(line).X));
            }

            this.layouts[key] = lines;
        }

        var height = ImGui.GetTextLineHeight();
        return new DrawItem(lines, color, alpha, isAlarm, scale, height, height, life);
    }

    /// <summary>Include the remaining time strip and gap in the block height.</summary>
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
        var extent = new Vector2(width, this.BlockHeight(item));
        if (!ImGui.IsRectVisible(origin - new Vector2(8), origin + extent + new Vector2(8)))
        {
            ImGui.Dummy(extent);
            return;
        }

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

        using (this.UseFont(this.TextPx * item.Scale))
        {
            this.DrawAlertLines(drawList, item, origin, width);
        }

        // Anchor the remaining time strip to the callout alignment.
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

        // Draw list calls reserve no layout space, so advance past this block explicitly.
        ImGui.Dummy(new Vector2(width, this.BlockHeight(item)));
    }

    private void DrawAlertLines(ImDrawListPtr drawList, DrawItem item, Vector2 origin, float width)
    {
        var y = 0.0f;
        foreach (var line in item.Lines)
        {
            var lineWidth = line.Width * ImGui.GetTextLineHeight() / item.MeasuredHeight;
            var x = this.Config.AlertsAlign switch
            {
                TextAlign.Center => Math.Max((width - lineWidth) * 0.5f, 0.0f),
                TextAlign.Right => Math.Max(width - lineWidth, 0.0f),
                _ => 0.0f,
            };

            var position = origin + new Vector2(x, y);
            if (ImGui.IsRectVisible(position - new Vector2(8),
                    position + new Vector2(lineWidth + 8, item.LineHeight + 8)))
            {
                this.DrawStyledText(drawList, position, WithAlpha(item.Color, item.Alpha), line.Text);
            }
            y += item.LineHeight;
        }
    }

    /// <summary>Wrap at word boundaries using the current font. Words wider than the window
    /// remain intact and are clipped.</summary>
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
