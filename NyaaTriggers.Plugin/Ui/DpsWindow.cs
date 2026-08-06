using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// The app's dps meter: a header with the encounter, its duration and the
/// party's dps, then one row per member in the configured style — the
/// timeline-style share bars, horizoverlay's solid job-coloured bars, or
/// kagerou's underlined text rows. The app sends a whole snapshot about once
/// a second, so there is nothing to interpolate; the window just draws the
/// latest one.
/// </summary>
internal sealed class DpsWindow : OverlayWindow
{
    private const float TextPadding = 6.0f;

    /// <summary>What an unlocked box draws while no encounter is running, so
    /// every style shows its own look instead of a blank frame.</summary>
    private static readonly DpsRow[] SampleRows =
    {
        new("Alphinaud L", "SGE", 10234.5, 21.4),
        new("Beta Tester", "DRG", 9876.0, 20.1),
        new("Cid Garlond", "MCH", 9012.0, 17.8),
        new("Curious Gorge", "WAR", 8456.0, 15.9),
        new("Y'shtola R", "BLM", 7890.0, 13.8),
        new("Thancred W", "DNC", 6543.0, 10.9),
    };

    private readonly BridgeHost bridge;

    internal DpsWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts)
        : base("NyaaTriggers DPS###nyaaDps", config, fonts)
    {
        this.bridge = bridge;
    }

    protected override Vector2 StoredPosition
    {
        get => this.Config.DpsPos;
        set => this.Config.DpsPos = value;
    }

    protected override Vector2 StoredSize
    {
        get => this.Config.DpsSize;
        set => this.Config.DpsSize = value;
    }

    protected override float TextScale => this.Config.DpsTextScale;

    protected override float BgOpacity => this.Config.DpsBgOpacity;

    protected override void DrawContent()
    {
        var dps = this.bridge.Dps;
        if (dps.Show && dps.Rows.Count > 0)
        {
            this.DrawHeader(dps.Title, dps.Duration, dps.EncDps);
            this.DrawRows(dps.Rows);
            return;
        }

        if (!this.Config.Locked)
        {
            // Placeholder so an unlocked box being positioned is never blank.
            this.DrawHeader("Sample Encounter", "03:12", 81234.5);
            this.DrawRows(SampleRows);
        }
    }

    /// <summary>The member rows in the configured style. The header is drawn
    /// the same way for all three; only the rows change.</summary>
    private void DrawRows(IReadOnlyList<DpsRow> rows)
    {
        switch (this.Config.DpsStyle)
        {
            case DpsMeterStyle.Horizoverlay:
                for (var i = 0; i < rows.Count; i++)
                {
                    this.DrawHorizoverlayRow(rows[i], rows[0].Share);
                }

                break;

            case DpsMeterStyle.Kagerou:
                for (var i = 0; i < rows.Count; i++)
                {
                    this.DrawKagerouRow(rows[i]);
                }

                break;

            default:
                for (var i = 0; i < rows.Count; i++)
                {
                    this.DrawBarsRow(i + 1, rows[i]);
                }

                break;
        }
    }

    private void DrawHeader(string title, string duration, double encDps)
    {
        // Skip whichever parts the frame did not carry rather than printing
        // dangling separators; with none of them there is no header at all.
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(title);
        }

        if (!string.IsNullOrWhiteSpace(duration))
        {
            parts.Add(duration);
        }

        if (encDps > 0.0)
        {
            parts.Add(FormatDps(encDps));
        }

        if (parts.Count == 0)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        this.DrawStyledText(drawList, origin, this.Config.ColorBarText, string.Join(" · ", parts));

        // Reserve the line like a bar row, so the first row lands underneath.
        ImGui.Dummy(new Vector2(
            Math.Max(ImGui.GetContentRegionAvail().X, 1.0f),
            ImGui.GetTextLineHeight() + Math.Max(this.Config.BarSpacing, 0.0f)));
    }

    /// <summary>Bars: the damage share filled into a faint full-length track
    /// behind the text, exactly like a timeline bar. The timeline's bar
    /// settings carry over so the two boxes match.</summary>
    private void DrawBarsRow(int rank, DpsRow row)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var height = this.Config.BarHeight * Math.Clamp(this.TextScale, 0.5f, 3.0f);
        var rounding = Math.Min(Math.Max(this.Config.BarRounding, 0.0f), height * 0.5f);

        // The share bar reads like a timeline bar: a faint full-length track
        // with the member's share of the party's damage filled in behind the
        // text. The timeline's bar settings carry over so the two boxes match.
        drawList.AddRectFilled(
            origin,
            origin + new Vector2(width, height),
            ToColor(WithAlpha(this.Config.ColorBar, this.Config.BarTrackOpacity)),
            rounding);

        var fillWidth = width * Math.Clamp((float)row.Share / 100.0f, 0.0f, 1.0f);
        if (fillWidth > 0.0f)
        {
            var fillOrigin = this.Config.BarRightToLeft
                ? origin + new Vector2(width - fillWidth, 0.0f)
                : origin;
            drawList.AddRectFilled(
                fillOrigin,
                fillOrigin + new Vector2(fillWidth, height),
                ToColor(this.Config.ColorBar),
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

        var label = string.IsNullOrWhiteSpace(row.Job)
            ? $"{rank}  {row.Name}"
            : $"{rank}  {row.Name} · {row.Job}";
        var dpsText = FormatDps(row.Dps);
        var textY = origin.Y + ((height - ImGui.CalcTextSize(label).Y) * 0.5f);

        this.DrawStyledText(
            drawList, origin + new Vector2(TextPadding, textY), this.Config.ColorBarText, label);

        // The dps pins to the right edge so the numbers never shift the names
        // as they tick over, same as the timeline's split countdown.
        var dpsWidth = ImGui.CalcTextSize(dpsText).X;
        this.DrawStyledText(
            drawList,
            new Vector2(origin.X + Math.Max(width - dpsWidth - TextPadding, TextPadding), textY),
            this.Config.ColorBarText,
            dpsText);

        // Reserve the row so the next one lands underneath it: the bars are
        // drawn straight to the draw list and take no layout space by default.
        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Config.BarSpacing, 0.0f)));
    }

    /// <summary>Horizoverlay: a solid bar in the job's colour and nothing
    /// else. The length is the member's damage next to the top member's, so
    /// rank 1 runs the full width; the bar colour says the job, so the text
    /// carries only the name and the numbers.</summary>
    private void DrawHorizoverlayRow(DpsRow row, double topShare)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var height = this.Config.BarHeight * Math.Clamp(this.TextScale, 0.5f, 3.0f);

        var fraction = topShare > 0.0
            ? Math.Clamp((float)(row.Share / topShare), 0.0f, 1.0f)
            : 0.0f;
        var barWidth = width * fraction;
        if (barWidth > 0.0f)
        {
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(barWidth, height),
                ToColor(JobColors.Get(row.Job)));
        }

        var numbers = $"{FormatDps(row.Dps)}  {FormatShare(row.Share)}";
        var textY = origin.Y + ((height - ImGui.CalcTextSize(row.Name).Y) * 0.5f);

        this.DrawStyledText(
            drawList, origin + new Vector2(TextPadding, textY), this.Config.ColorBarText, row.Name);

        // The numbers pin to the row's right edge, not the bar's, so they
        // never jump sideways as the bars tick over.
        var numbersWidth = ImGui.CalcTextSize(numbers).X;
        this.DrawStyledText(
            drawList,
            new Vector2(origin.X + Math.Max(width - numbersWidth - TextPadding, TextPadding), textY),
            this.Config.ColorBarText,
            numbers);

        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Config.BarSpacing, 0.0f)));
    }

    /// <summary>Kagerou: no bars, just the text line — job acronym and name
    /// on the left, dps and share on the right — with a thin job-coloured
    /// underline whose length is the member's share of the party's damage.
    /// </summary>
    private void DrawKagerouRow(DpsRow row)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var lineHeight = ImGui.GetTextLineHeight();

        // Thick enough to read as a bar, thin enough to stay an underline.
        var underline = Math.Clamp(this.Config.BarHeight * 0.15f, 2.0f, 4.0f);

        var label = string.IsNullOrWhiteSpace(row.Job) ? row.Name : $"{row.Job}  {row.Name}";
        var numbers = $"{FormatDps(row.Dps)} · {FormatShare(row.Share)}";

        this.DrawStyledText(drawList, origin, this.Config.ColorBarText, label);

        var numbersWidth = ImGui.CalcTextSize(numbers).X;
        this.DrawStyledText(
            drawList,
            new Vector2(origin.X + Math.Max(width - numbersWidth - TextPadding, TextPadding), origin.Y),
            this.Config.ColorBarText,
            numbers);

        var underlineY = origin.Y + lineHeight + 1.0f;
        var fillWidth = width * Math.Clamp((float)row.Share / 100.0f, 0.0f, 1.0f);
        if (fillWidth > 0.0f)
        {
            drawList.AddRectFilled(
                new Vector2(origin.X, underlineY),
                new Vector2(origin.X + fillWidth, underlineY + underline),
                ToColor(JobColors.Get(row.Job)));
        }

        ImGui.Dummy(new Vector2(
            width,
            lineHeight + 1.0f + underline + Math.Max(this.Config.BarSpacing, 0.0f)));
    }

    /// <summary>Damage share as a percentage. One decimal matches the dps
    /// format's precision so the two read as a pair.</summary>
    private static string FormatShare(double share)
        => share.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Compact dps: 81.2k reads faster mid-pull than 81,234. Used by
    /// the header and every row so the two always read the same way.</summary>
    private static string FormatDps(double dps)
        => dps >= 1000.0
            ? (dps / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k"
            : dps.ToString("0", CultureInfo.InvariantCulture);
}
