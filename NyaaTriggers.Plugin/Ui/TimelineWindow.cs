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
    private const float TextPadding = 6.0f;

    private readonly BridgeHost bridge;

    internal TimelineWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts)
        : base("NyaaTriggers Timeline###nyaaTimeline", config, fonts)
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

    protected override float BgOpacity => this.Config.TimelineBgOpacity;

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
            this.DrawBar("Sample mechanic", this.Config.ImminentSeconds * 0.5f, window);
        }
    }

    private void DrawBar(string label, float remaining, float window)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var height = this.Config.BarHeight * Math.Clamp(this.TextScale, 0.5f, 3.0f);
        var rounding = Math.Min(Math.Max(this.Config.BarRounding, 0.0f), height * 0.5f);

        // Depleting bars shrink toward zero as the cue arrives, so the bar
        // reads as time left; filling bars invert that and grow instead.
        var fraction = Math.Clamp(remaining / window, 0.0f, 1.0f);
        if (this.Config.BarFill == BarFillMode.Fill)
        {
            fraction = 1.0f - fraction;
        }

        var imminent = remaining <= Math.Max(this.Config.ImminentSeconds, 0.0f);
        var fill = imminent ? this.Config.ColorImminent : this.Config.ColorBar;

        if (imminent && this.Config.ImminentPulse)
        {
            // Pulse the last few seconds so it catches the eye without the
            // whole bar changing size.
            var phase = (float)((Math.Sin(Environment.TickCount64 / 120.0) * 0.15) + 0.85);
            fill = WithAlpha(fill, phase);
        }

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(width, height),
            ToColor(WithAlpha(fill, this.Config.BarTrackOpacity)),
            rounding);

        var fillWidth = width * fraction;
        if (fillWidth > 0.0f)
        {
            var fillOrigin = this.Config.BarRightToLeft
                ? origin + new Vector2(width - fillWidth, 0.0f)
                : origin;
            drawList.AddRectFilled(
                fillOrigin,
                fillOrigin + new Vector2(fillWidth, height),
                ToColor(fill),
                rounding);
        }

        if (this.Config.BarBorderThickness > 0.0f)
        {
            drawList.AddRect(
                origin,
                origin + new Vector2(width, height),
                ToColor(this.Config.ColorBarBorder),
                rounding,
                ImDrawFlags.None,
                this.Config.BarBorderThickness);
        }

        this.DrawBarText(drawList, label, remaining, origin, width, height);

        // Reserve the row so the next bar lands underneath it: the bars are
        // drawn straight to the draw list and take no layout space by default.
        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Config.BarSpacing, 0.0f)));
    }

    private void DrawBarText(ImDrawListPtr drawList, string label, float remaining,
        Vector2 origin, float width, float height)
    {
        var countdown = this.Config.Countdown switch
        {
            CountdownStyle.Hidden => null,
            CountdownStyle.Seconds => remaining.ToString("0", CultureInfo.InvariantCulture),
            _ => remaining.ToString("0.0", CultureInfo.InvariantCulture),
        };

        var textY = origin.Y + ((height - ImGui.CalcTextSize(label).Y) * 0.5f);

        if (countdown != null && this.Config.CountdownSplit)
        {
            // The countdown pins to the right edge so the numbers never shift
            // the label as they tick, and the label aligns inside the space
            // left of it so the two cannot overlap.
            var countdownWidth = ImGui.CalcTextSize(countdown).X;
            this.DrawAlignedText(
                drawList, label, origin, width - countdownWidth - TextPadding, textY);
            this.DrawStyledText(
                drawList,
                new Vector2(origin.X + Math.Max(width - countdownWidth - TextPadding, TextPadding), textY),
                this.Config.ColorBarText,
                countdown);
            return;
        }

        var text = countdown == null ? label : $"{label}  {countdown}";
        this.DrawAlignedText(drawList, text, origin, width, textY);
    }

    private void DrawAlignedText(ImDrawListPtr drawList, string text,
        Vector2 origin, float width, float y)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        var x = this.Config.BarTextAlign switch
        {
            TextAlign.Center => Math.Max((width - textWidth) * 0.5f, TextPadding),
            TextAlign.Right => Math.Max(width - textWidth - TextPadding, TextPadding),
            _ => TextPadding,
        };

        this.DrawStyledText(drawList, new Vector2(origin.X + x, y), this.Config.ColorBarText, text);
    }
}
