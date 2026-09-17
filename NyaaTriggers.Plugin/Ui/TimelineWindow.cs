using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>Interpolate the fight clock between program ticks so timeline bars move
/// smoothly.</summary>
internal sealed class TimelineWindow : OverlayWindow
{
    private const float TextPadding = 6.0f;

    private const float FireFlashSeconds = 0.6f;

    private readonly BridgeHost bridge;

    /// <summary>Reuse the row list across frames to avoid allocating a list for each
    /// draw.</summary>
    private readonly List<(string Label, float Remaining, bool Fired, string Kind)> rows = new();

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

    internal override void ResetGeometry()
    {
        var fresh = new Configuration();
        this.StoredPosition = fresh.TimelinePos;
        this.StoredSize = fresh.TimelineSize;
        this.ForceGeometry();
    }

    protected override float TextScale => this.Config.TimelineTextScale;

    protected override float BgOpacity => this.Config.TimelineBgOpacity;

    protected override float FadeOpacity => this.Config.TimelineFade;

    protected override TextEffectStyle TextEffect => this.Config.TimelineTextEffect;

    protected override int EffectThickness => this.Config.TimelineEffectThickness;

    protected override Vector4 EffectColor => this.Config.TimelineEffectColor;

    protected override void DrawContent()
    {
        var window = Math.Max(this.Config.TimelineWindow, 1.0f);
        var max = Math.Clamp(this.Config.TimelineRows, 1, 12);
        var clock = this.bridge.Clock;

        // Collect rows first so bottom placement can use the full stack height.
        var rows = this.rows;
        rows.Clear();
        foreach (var entry in this.bridge.Timeline)
        {
            if (rows.Count >= max)
            {
                break;
            }

            if (!this.KindVisible(entry.Kind))
            {
                continue;
            }

            var remaining = entry.Time - clock;

            // Retain recently fired cues when flashing is enabled. The sorted schedule
            // allows stopping at the first cue beyond the display window.
            var fired = this.Config.TimelineFireFlash
                && remaining < 0.0 && remaining >= -FireFlashSeconds;
            if (remaining < 0.0 && !fired)
            {
                continue;
            }

            if (remaining > window)
            {
                break;
            }

            rows.Add((entry.Label, (float)Math.Max(remaining, 0.0), fired, entry.Kind));
        }

        if (rows.Count == 0 && !this.Config.Locked)
        {
            // Preview all cue kinds and the imminent colour while placing an empty window.
            rows.Add(("Sample tankbuster", window * 0.6f, false, "tankbuster"));
            rows.Add(("Sample raidwide", window * 0.35f, false, "raidwide"));
            rows.Add(("Sample mechanic", this.Config.ImminentSeconds * 0.5f, false, "mechanic"));
        }

        // Preview the clock while unlocked, even before a real tick arrives.
        var clockLine = this.Config.TimelineShowClock
            && (this.bridge.ClockRunning || !this.Config.Locked);

        if (this.Config.TimelineAnchorBottom)
        {
            var spacing = Math.Max(this.Config.TimelineBarSpacing, 0.0f);
            var total = rows.Count *
                ((Math.Max(this.Config.TimelineBarHeight, 1.0f) * ClampTextScale(this.TextScale)) + spacing);
            if (clockLine)
            {
                total += ImGui.GetTextLineHeight() + spacing;
            }

            var slack = ImGui.GetContentRegionAvail().Y - total;
            if (slack > 0.0f)
            {
                ImGui.Dummy(new Vector2(1.0f, slack));
            }
        }

        if (clockLine)
        {
            this.DrawClockLine();
        }

        foreach (var row in rows)
        {
            this.DrawBar(row.Label, row.Remaining, window, row.Fired, row.Kind);
        }
    }

    private void DrawClockLine()
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var text = this.bridge.ClockRunning ? FormatClock(this.bridge.Clock) : "12:34";
        this.DrawAlignedText(drawList, text, origin, width, origin.Y);
        ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight() + Math.Max(this.Config.TimelineBarSpacing, 0.0f)));
    }

    /// <summary>Missing and unknown kinds use the mechanic filter.</summary>
    private bool KindVisible(string kind) => kind switch
    {
        "tankbuster" => this.Config.TimelineShowTankbuster,
        "raidwide" => this.Config.TimelineShowRaidwide,
        _ => this.Config.TimelineShowMechanic,
    };

    /// <summary>Missing and unknown kinds use the shared colour.</summary>
    private Vector4 BarColor(string kind)
    {
        if (!this.Config.TimelineKindColors)
        {
            return this.Config.TimelineBarColor;
        }

        return kind switch
        {
            "tankbuster" => this.Config.TimelineTankbusterColor,
            "raidwide" => this.Config.TimelineRaidwideColor,
            "mechanic" => this.Config.TimelineMechanicColor,
            _ => this.Config.TimelineBarColor,
        };
    }

    private static string FormatClock(double seconds)
    {
        var total = Math.Max((int)seconds, 0);
        return string.Concat(
            (total / 60).ToString("D2", CultureInfo.InvariantCulture),
            ":",
            (total % 60).ToString("D2", CultureInfo.InvariantCulture));
    }

    private void DrawBar(string label, float remaining, float window, bool fired, string kind)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var height = Math.Max(this.Config.TimelineBarHeight, 1.0f) * ClampTextScale(this.TextScale);
        var rounding = Math.Min(Math.Max(this.Config.TimelineBarRounding, 0.0f), height * 0.5f);

        // Fired cues flash as full bars in either fill mode.
        var fraction = fired ? 1.0f : Math.Clamp(remaining / window, 0.0f, 1.0f);
        if (!fired && this.Config.BarFill == BarFillMode.Fill)
        {
            fraction = 1.0f - fraction;
        }

        var imminent = fired || remaining <= Math.Max(this.Config.ImminentSeconds, 0.0f);
        var fill = imminent ? this.Config.ColorImminent : this.BarColor(kind);

        if (imminent && this.Config.ImminentPulse)
        {
            var phase = (float)((Math.Sin(Environment.TickCount64 / 120.0) * 0.15) + 0.85);
            fill = WithAlpha(fill, phase);
        }

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(width, height),
            ToColor(WithAlpha(this.Config.TimelineBarTrackColor, this.Config.TimelineBarTrackOpacity)),
            rounding);

        var fillWidth = width * fraction;
        if (fillWidth > 0.0f)
        {
            var fillOrigin = this.Config.BarRightToLeft
                ? origin + new Vector2(width - fillWidth, 0.0f)
                : origin;
            AddBarFill(drawList, fillOrigin, fillOrigin + new Vector2(fillWidth, height), fill, rounding);
        }

        var border = Math.Clamp(this.Config.TimelineBarBorderThickness, 0.0f, 4.0f);
        if (border > 0.0f)
        {
            drawList.AddRect(
                origin,
                origin + new Vector2(width, height),
                ToColor(this.Config.TimelineBarBorderColor),
                rounding,
                ImDrawFlags.None,
                border);
        }

        this.DrawBarText(drawList, label, remaining, origin, width, height);

        // Draw list calls reserve no layout space, so advance past this row explicitly.
        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Config.TimelineBarSpacing, 0.0f)));
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
            // Reserve the countdown width before fitting the label. Omit the label if both
            // cannot fit.
            var countdownWidth = ImGui.CalcTextSize(countdown).X;
            if (width - countdownWidth - (2.0f * TextPadding) > 0.0f)
            {
                this.DrawAlignedText(
                    drawList, label, origin, width - countdownWidth - TextPadding, textY);
            }

            this.DrawStyledText(
                drawList,
                new Vector2(origin.X + Math.Max(width - countdownWidth - TextPadding, TextPadding), textY),
                this.Config.TimelineTextColor,
                countdown);
            return;
        }

        var text = countdown == null ? label : $"{label}  {countdown}";
        this.DrawAlignedText(drawList, text, origin, width, textY);
    }

    private void DrawAlignedText(ImDrawListPtr drawList, string text,
        Vector2 origin, float width, float y)
    {
        text = Elide(text, Math.Max(width - TextPadding, 1.0f));
        var textWidth = ImGui.CalcTextSize(text).X;
        var x = this.Config.BarTextAlign switch
        {
            TextAlign.Center => Math.Max((width - textWidth) * 0.5f, TextPadding),
            TextAlign.Right => Math.Max(width - textWidth - TextPadding, TextPadding),
            _ => TextPadding,
        };

        this.DrawStyledText(drawList, new Vector2(origin.X + x, y), this.Config.TimelineTextColor, text);
    }
}
