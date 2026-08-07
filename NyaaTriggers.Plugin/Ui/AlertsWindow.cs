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

    protected override float TextScale => this.Config.AlertsTextScale;

    protected override float BgOpacity => this.Config.AlertsBgOpacity;

    protected override TextEffectStyle TextEffect => this.Config.AlertsTextEffect;

    protected override int EffectThickness => this.Config.AlertsEffectThickness;

    protected override Vector4 EffectColor => this.Config.AlertsEffectColor;

    protected override void DrawContent()
    {
        var alerts = this.bridge.Alerts;
        if (alerts.Count == 0)
        {
            if (!this.Config.Locked)
            {
                this.DrawAlert("Sample callout", this.Config.ColorAlarm, 1.0f);
                this.DrawAlert("Sample callout", this.Config.ColorAlert, 1.0f);
            }

            return;
        }

        // The newest alert lives at the end of the list. The order setting
        // picks which end of the stack it is drawn at; either way only the
        // most recent few are shown.
        var max = Math.Clamp(this.Config.AlertsMaxVisible, 1, 8);
        if (this.Config.AlertOrder == AlertOrder.NewestFirst)
        {
            for (var i = alerts.Count - 1; i >= 0 && alerts.Count - i <= max; i--)
            {
                this.DrawWithFade(alerts[i]);
            }
        }
        else
        {
            for (var i = Math.Max(alerts.Count - max, 0); i < alerts.Count; i++)
            {
                this.DrawWithFade(alerts[i]);
            }
        }
    }

    private void DrawWithFade(ActiveAlert alert)
    {
        var color = alert.Severity switch
        {
            Severity.Alarm => this.Config.ColorAlarm,
            Severity.Alert => this.Config.ColorAlert,
            _ => this.Config.ColorInfo,
        };

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

        this.DrawAlert(alert.Text, color, alpha);
    }

    private void DrawAlert(string text, Vector4 color, float alpha)
    {
        var drawList = ImGui.GetWindowDrawList();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var origin = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();
        var y = 0.0f;

        foreach (var line in WrapLines(text, width))
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
                WithAlpha(color, alpha),
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
