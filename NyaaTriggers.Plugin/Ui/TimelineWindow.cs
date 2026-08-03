using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// Upcoming timeline cues as depleting bars. The app sends the schedule once
/// and a clock tick periodically; the bar widths interpolate from the tick, so
/// they move at frame rate rather than in 250 ms steps.
/// </summary>
internal sealed class TimelineWindow : OverlayWindow
{
    /// <summary>Seconds out at which a bar turns to the imminent colour.</summary>
    private const float ImminentAt = 5.0f;

    private const float BarSpacing = 4.0f;

    private readonly BridgeHost bridge;

    internal TimelineWindow(Configuration config, BridgeHost bridge)
        : base("NyaaTriggers Timeline###nyaaTimeline", config)
    {
        this.bridge = bridge;
    }

    protected override Vector2 StoredPosition
    {
        get => this.Config.TimelinePos;
        set => this.Config.TimelinePos = value;
    }

    protected override Vector2 StoredSize
    {
        get => this.Config.TimelineSize;
        set => this.Config.TimelineSize = value;
    }

    protected override float TextScale => this.Config.TimelineTextScale;

    protected override void DrawContent()
    {
        var window = Math.Max(this.Config.TimelineWindow, 1.0f);
        var clock = this.bridge.Clock;
        var drawn = 0;

        foreach (var entry in this.bridge.Timeline)
        {
            if (drawn >= this.Config.TimelineRows)
            {
                break;
            }

            var remaining = entry.Time - clock;

            // Past cues fall off; anything beyond the window is not yet worth
            // the row. The schedule is sorted, so the first one past the window
            // means every later one is too.
            if (remaining < 0)
            {
                continue;
            }

            if (remaining > window)
            {
                break;
            }

            this.DrawBar(entry.Label, (float)remaining, window);
            drawn++;
        }

        if (drawn == 0 && !this.Config.Locked)
        {
            // Placeholder so an unlocked box being positioned is never blank.
            this.DrawBar("Sample mechanic", window * 0.6f, window);
            this.DrawBar("Sample mechanic", ImminentAt * 0.5f, window);
        }
    }

    private void DrawBar(string label, float remaining, float window)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var height = this.Config.BarHeight * Math.Clamp(this.TextScale, 0.5f, 3.0f);

        // Depletes toward zero as the cue arrives, so the bar reads as time
        // left rather than time elapsed.
        var fraction = Math.Clamp(remaining / window, 0.0f, 1.0f);
        var imminent = remaining <= ImminentAt;
        var fill = imminent ? this.Config.ColorImminent : this.Config.ColorBar;

        if (imminent)
        {
            // Pulse the last few seconds so it catches the eye without the
            // whole bar changing size.
            var phase = (float)((Math.Sin(Environment.TickCount64 / 120.0) * 0.15) + 0.85);
            fill = WithAlpha(fill, phase);
        }

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(width, height),
            ToColor(WithAlpha(fill, 0.25f)),
            3.0f);
        drawList.AddRectFilled(
            origin,
            origin + new Vector2(width * fraction, height),
            ToColor(fill),
            3.0f);

        var text = $"{label}  {remaining.ToString("0.0", CultureInfo.InvariantCulture)}";
        var textSize = ImGui.CalcTextSize(text);
        drawList.AddText(
            origin + new Vector2(6.0f, (height - textSize.Y) * 0.5f),
            ToColor(this.Config.ColorBarText),
            text);

        // Reserve the row so the next bar lands underneath it: the bars are
        // drawn straight to the draw list and take no layout space by default.
        ImGui.Dummy(new Vector2(width, height + BarSpacing));
    }
}
