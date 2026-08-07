using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// The app's dps meter: a header with the encounter, its duration and the
/// party's dps, then the members in the configured style — the
/// timeline-style share bars, horizoverlay's single top strip of
/// job-coloured segments side by side, or kagerou's underlined text rows.
/// The app sends a whole snapshot about once a second, so there is nothing
/// to interpolate; the window just draws the latest one.
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

    protected override TextEffectStyle TextEffect => this.Config.DpsTextEffect;

    protected override int EffectThickness => this.Config.DpsEffectThickness;

    protected override Vector4 EffectColor => this.Config.DpsEffectColor;

    protected override void DrawContent()
    {
        var dps = this.bridge.Dps;
        if (dps.Show && dps.Rows.Count > 0)
        {
            this.DrawMeter(dps.Title, dps.Duration, dps.EncDps, dps.Rows);
            return;
        }

        if (!this.Config.Locked)
        {
            // Placeholder so an unlocked box being positioned is never blank.
            this.DrawMeter("Sample Encounter", "03:12", 81234.5, SampleRows);
        }
    }

    private void DrawMeter(string title, string duration, double encDps, IReadOnlyList<DpsRow> rows)
    {
        // Horizoverlay is one strip across the top with the members side by
        // side; like the ACT original, its header reads centred underneath
        // instead of above the rows.
        if (this.Config.DpsStyle == DpsMeterStyle.Horizoverlay)
        {
            this.DrawHorizoverlayStrip(rows);
            this.DrawHeader(title, duration, encDps, centered: true);
            return;
        }

        this.DrawHeader(title, duration, encDps);
        this.DrawRows(rows);
    }

    /// <summary>The member rows in the row-based styles. The header is drawn
    /// the same way for both; only the rows change. Horizoverlay never reaches
    /// here: it is a strip, not rows, and is drawn by DrawMeter itself.</summary>
    private void DrawRows(IReadOnlyList<DpsRow> rows)
    {
        switch (this.Config.DpsStyle)
        {
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

    private void DrawHeader(string title, string duration, double encDps, bool centered = false)
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
        var text = string.Join(" · ", parts);
        if (centered)
        {
            var slack = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f) - ImGui.CalcTextSize(text).X;
            origin.X += Math.Max(slack * 0.5f, 0.0f);
        }

        this.DrawStyledText(drawList, origin, this.Config.DpsTextColor, text);

        // Reserve the line like a bar row, so the first row lands underneath.
        ImGui.Dummy(new Vector2(
            Math.Max(ImGui.GetContentRegionAvail().X, 1.0f),
            ImGui.GetTextLineHeight() + Math.Max(this.Config.DpsBarSpacing, 0.0f)));
    }

    /// <summary>Bars: the damage share filled into a dark full-length slot
    /// behind the text, exactly like a timeline bar. The meter's own bar
    /// settings drive it, independent of the timeline box.</summary>
    private void DrawBarsRow(int rank, DpsRow row)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var height = this.Config.DpsBarHeight * Math.Clamp(this.TextScale, 0.5f, 3.0f);
        var rounding = Math.Min(Math.Max(this.Config.DpsBarRounding, 0.0f), height * 0.5f);

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(width, height),
            ToColor(WithAlpha(this.Config.DpsBarTrackColor, this.Config.DpsBarTrackOpacity)),
            rounding);

        var fillWidth = width * Math.Clamp((float)row.Share / 100.0f, 0.0f, 1.0f);
        if (fillWidth > 0.0f)
        {
            var fillOrigin = this.Config.DpsBarRightToLeft
                ? origin + new Vector2(width - fillWidth, 0.0f)
                : origin;
            AddBarFill(drawList, fillOrigin, fillOrigin + new Vector2(fillWidth, height),
                this.Config.DpsBarColor, rounding);
        }

        if (this.Config.DpsBarBorderThickness > 0.0f)
        {
            drawList.AddRect(
                origin,
                origin + new Vector2(width, height),
                ToColor(this.Config.DpsBarBorderColor),
                rounding,
                ImDrawFlags.None,
                this.Config.DpsBarBorderThickness);
        }

        var label = string.IsNullOrWhiteSpace(row.Job)
            ? $"{rank}  {row.Name}"
            : $"{rank}  {row.Name} · {row.Job}";
        var dpsText = FormatDps(row.Dps);
        var dpsWidth = ImGui.CalcTextSize(dpsText).X;

        // The name ends in an ellipsis rather than running into the pinned
        // number on a narrow box.
        label = Elide(label, Math.Max(width - dpsWidth - (TextPadding * 3.0f), 1.0f));
        var textY = origin.Y + ((height - ImGui.CalcTextSize(label).Y) * 0.5f);

        this.DrawStyledText(
            drawList, origin + new Vector2(TextPadding, textY), this.Config.DpsTextColor, label);

        // The dps pins to the right edge so the numbers never shift the names
        // as they tick over, same as the timeline's split countdown.
        this.DrawStyledText(
            drawList,
            new Vector2(origin.X + Math.Max(width - dpsWidth - TextPadding, TextPadding), textY),
            this.Config.DpsTextColor,
            dpsText);

        // Reserve the row so the next one lands underneath it: the bars are
        // drawn straight to the draw list and take no layout space by default.
        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Config.DpsBarSpacing, 0.0f)));
    }

    /// <summary>Horizoverlay: one strip across the top, split into one
    /// segment per member side by side, rank 1 on the left. A segment's width
    /// is the member's share of the party's damage and its colour is the job,
    /// like the ACT original; the text carries rank and name, then job and
    /// dps, with the damage percentage at the bottom right.</summary>
    private void DrawHorizoverlayStrip(IReadOnlyList<DpsRow> rows)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var lineHeight = ImGui.GetTextLineHeight();

        // Three text lines plus breathing room, matching the original's tall
        // strip; the line height already carries the configured text scale.
        var height = (lineHeight * 3.0f) + (TextPadding * 2.0f);

        var totalShare = 0.0;
        foreach (var row in rows)
        {
            totalShare += Math.Max(row.Share, 0.0);
        }

        var x = origin.X;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            // The last segment takes whatever is left so rounding never
            // leaves a slit at the strip's right edge.
            var segWidth = i == rows.Count - 1
                ? origin.X + width - x
                : totalShare > 0.0
                    ? width * (float)(Math.Max(row.Share, 0.0) / totalShare)
                    : width / rows.Count;

            var segOrigin = new Vector2(x, origin.Y);
            var segEnd = new Vector2(x + segWidth, origin.Y + height);

            // A dark pixel between segments keeps adjacent job colours apart.
            var fillEnd = i == rows.Count - 1 ? segEnd : segEnd - new Vector2(1.0f, 0.0f);
            if (fillEnd.X > segOrigin.X)
            {
                AddBarFill(drawList, segOrigin, fillEnd, JobColors.Get(row.Job), 0.0f);
            }

            // Clip to the segment: a thin slice in a big party must not spill
            // its text over the neighbours.
            drawList.PushClipRect(segOrigin, segEnd, true);

            var textX = x + TextPadding;
            var textColor = this.Config.DpsTextColor;
            this.DrawStyledText(
                drawList,
                new Vector2(textX, origin.Y + TextPadding),
                textColor,
                Elide($"{i + 1}. {row.Name}", Math.Max(segWidth - (TextPadding * 2.0f), 1.0f)));

            var numbers = string.IsNullOrWhiteSpace(row.Job)
                ? $"{FormatDps(row.Dps)} DPS"
                : $"{row.Job}  {FormatDps(row.Dps)} DPS";
            this.DrawStyledText(
                drawList, new Vector2(textX, origin.Y + TextPadding + lineHeight), textColor, numbers);

            var share = FormatShare(row.Share);
            var shareWidth = ImGui.CalcTextSize(share).X;
            this.DrawStyledText(
                drawList,
                new Vector2(
                    Math.Max(x + segWidth - shareWidth - TextPadding, textX),
                    origin.Y + TextPadding + (lineHeight * 2.0f)),
                textColor,
                share);

            drawList.PopClipRect();

            x += segWidth;
        }

        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Config.DpsBarSpacing, 0.0f)));
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
        var underline = Math.Clamp(this.Config.DpsBarHeight * 0.15f, 2.0f, 4.0f);

        var numbers = $"{FormatDps(row.Dps)} · {FormatShare(row.Share)}";
        var numbersWidth = ImGui.CalcTextSize(numbers).X;
        var nameWidth = Math.Max(width - numbersWidth - (TextPadding * 2.0f), 1.0f);

        // The acronym carries the job colour, like kagerou's own; the name
        // stays in the box's text colour and ends in an ellipsis rather than
        // running into the pinned numbers.
        if (string.IsNullOrWhiteSpace(row.Job))
        {
            this.DrawStyledText(drawList, origin, this.Config.DpsTextColor, Elide(row.Name, nameWidth));
        }
        else
        {
            this.DrawStyledText(drawList, origin, JobColors.Get(row.Job), row.Job);
            var nameX = origin.X + ImGui.CalcTextSize($"{row.Job}  ").X;
            this.DrawStyledText(
                drawList, new Vector2(nameX, origin.Y), this.Config.DpsTextColor,
                Elide(row.Name, Math.Max(origin.X + nameWidth - nameX, 1.0f)));
        }

        this.DrawStyledText(
            drawList,
            new Vector2(origin.X + Math.Max(width - numbersWidth - TextPadding, TextPadding), origin.Y),
            this.Config.DpsTextColor,
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
            lineHeight + 1.0f + underline + Math.Max(this.Config.DpsBarSpacing, 0.0f)));
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
