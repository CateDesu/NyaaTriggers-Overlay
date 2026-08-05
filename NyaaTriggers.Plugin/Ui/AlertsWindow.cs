using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// Callout text, newest first, colour-coded by severity and fading out as it
/// expires. Text is wrapped rather than clipped: a long callout truncated at
/// the box edge is worse than useless mid-pull.
/// </summary>
internal sealed class AlertsWindow : OverlayWindow
{
    /// <summary>Seconds of fade at the end of an alert's life.</summary>
    private const float FadeSeconds = 0.6f;

    /// <summary>Seconds of grow-in at the start, so a new alert reads as new
    /// even when it replaces one with the same text.</summary>
    private const float RiseSeconds = 0.12f;

    private readonly BridgeHost bridge;

    internal AlertsWindow(Configuration config, BridgeHost bridge)
        : base("NyaaTriggers Alerts###nyaaAlerts", config)
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

    protected override void DrawContent()
    {
        var alerts = this.bridge.Alerts;
        if (alerts.Count == 0)
        {
            if (!this.Config.Locked)
            {
                this.DrawLine("Sample callout", this.Config.ColorAlarm, 1.0f);
                this.DrawLine("Sample callout", this.Config.ColorAlert, 1.0f);
            }

            return;
        }

        var now = Environment.TickCount64;
        for (var i = alerts.Count - 1; i >= 0; i--)
        {
            var alert = alerts[i];
            var color = alert.Severity switch
            {
                Severity.Alarm => this.Config.ColorAlarm,
                Severity.Alert => this.Config.ColorAlert,
                _ => this.Config.ColorInfo,
            };

            var remaining = (alert.ExpiresAt - now) / 1000.0f;
            var age = (now - alert.ShownAt) / 1000.0f;
            var alpha = Math.Min(
                remaining >= FadeSeconds ? 1.0f : Math.Max(remaining, 0.0f) / FadeSeconds,
                age >= RiseSeconds ? 1.0f : Math.Max(age, 0.0f) / RiseSeconds);

            this.DrawLine(alert.Text, color, alpha);
        }
    }

    private void DrawLine(string text, Vector4 color, float alpha)
    {
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var pos = ImGui.GetCursorPos();

        // Shadow pass first (same wrap, offset by a pixel): the text floats
        // over the game with little or no backdrop and washes out without it.
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 0.0f, 0.0f, 0.9f * alpha));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.SetCursorPos(pos + new Vector2(1.0f, 1.0f));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.SetCursorPos(pos);
        ImGui.PushStyleColor(ImGuiCol.Text, WithAlpha(color, alpha));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        try
        {
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
    }
}
