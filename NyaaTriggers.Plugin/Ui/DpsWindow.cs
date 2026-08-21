using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// The app's dps meter: a header with the encounter, its duration and the
/// party's dps, then the members in the configured style — the
/// timeline-style share bars, horizoverlay's skewed side-by-side bars with
/// the job icon straddling the top edge, or kagerou's underlined text rows.
/// The app sends a whole snapshot about once a second, so there is nothing
/// to interpolate; the window just draws the latest one.
/// </summary>
internal sealed class DpsWindow : OverlayWindow
{
    private const float TextPadding = 6.0f;

    /// <summary>tan(30°): how far the bottom edge of a horizoverlay bar shifts
    /// left per pixel of height, the original's skew(-30deg) lean.</summary>
    private const float SkewFactor = 0.57735f;

    // The ACT horizoverlay's own rgb() values, at full alpha: the configured
    // bar opacity scales them at draw time (0.30 by default, not the master
    // branch's 0.50: the reference screenshots read at ~0.3 over their
    // scenes). The faint side of the two-tone bar is a third of that.
    private static readonly Vector4 HorizSelfBar = new(1.000f, 1.000f, 1.000f, 0.80f);
    private static readonly Vector4 HorizDpsBar = new(0.957f, 0.263f, 0.212f, 1.00f);
    private static readonly Vector4 HorizTankBar = new(0.129f, 0.588f, 0.953f, 1.00f);
    private static readonly Vector4 HorizHealerBar = new(0.545f, 0.765f, 0.290f, 1.00f);
    private static readonly Vector4 HorizDimBar = new(0.000f, 0.000f, 0.000f, 0.30f);
    private static readonly Vector4 HorizSelfText = new(0.000f, 0.000f, 0.000f, 1.00f);
    private static readonly Vector4 HorizChip = new(0.000f, 0.000f, 0.000f, 0.25f);

    /// <summary>The two-tone seam across a bar, measured from the left: the
    /// original's 51% gradient stop. The side nearer the member's relevant
    /// stat is solid; a healing healer (hps above dps) flips it.</summary>
    private const float HorizSeam = 0.49f;

    /// <summary>What an unlocked box draws while no encounter is running, so
    /// every style shows its own look instead of a blank frame.</summary>
    private static readonly DpsRow[] SampleRows =
    {
        new("Y'shtola R", "BLM", 10234.5, 21.4, 14.0, true),
        new("Curious Gorge", "WAR", 9876.0, 20.1, 322.0, false),
        new("Beta Tester", "DRG", 9012.0, 17.8, 0.0, false),
        new("Cid Garlond", "MCH", 8456.0, 15.9, 0.0, false),
        new("Thancred W", "DNC", 7890.0, 13.8, 0.0, false),
        new("Alphinaud L", "SGE", 6543.0, 10.9, 9123.4, false),
    };

    private readonly BridgeHost bridge;

    /// <summary>How tall the last frame's content was, window padding
    /// included. The locked horizoverlay sizes its window to this: a strip
    /// has exactly one right height, and a taller configured box only ever
    /// clipped the encounter line or trapped dead space.</summary>
    private float contentHeight;

    /// <summary>Tracks the lock edge so unlocking once re-applies the stored
    /// height. Without it imgui's remembered size is the auto-fit one, and
    /// the geometry capture would save that over the user's own.</summary>
    private bool wasLocked;

    /// <summary>The 1 based places the kept rows held in the full list, filled
    /// by FilterRows. Empty means no filter bit and the rank is the row index
    /// plus one. Solo mode keeps one middle row, and renumbering it to 1
    /// would claim a first place the player did not earn.</summary>
    private readonly List<int> keptRanks = new();

    internal DpsWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts)
        : base("NyaaTriggers DPS###nyaaDps", config, fonts)
    {
        this.bridge = bridge;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (this.Config.DpsStyle == DpsMeterStyle.Horizoverlay)
        {
            if (this.Config.Locked && this.contentHeight > 1.0f)
            {
                this.Size = new Vector2(
                    this.StoredSize.X / ImGuiHelpers.GlobalScale,
                    this.contentHeight / ImGuiHelpers.GlobalScale);
            }
            else if (!this.Config.Locked && this.wasLocked)
            {
                // Back from the auto-fit height to the stored one, this once.
                // FirstUseEver would keep imgui's remembered auto-fit size.
                this.SizeCondition = ImGuiCond.Always;
            }
        }

        this.wasLocked = this.Config.Locked;
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
            this.DrawMeter(dps.Title, dps.Duration, dps.EncDps, this.FilterRows(dps.Rows));
        }
        else if (!this.Config.Locked)
        {
            // Placeholder so an unlocked box being positioned is never blank.
            this.DrawMeter("Sample Encounter", "03:12", 81234.5, this.FilterRows(SampleRows));
        }

        // Where the content ended, bottom padding included: the locked
        // horizoverlay's next PreDraw sizes the window to this.
        this.contentHeight = ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y
            + ImGui.GetStyle().WindowPadding.Y;
    }

    /// <summary>The rows after the solo-only and max-combatants filters, in
    /// rank order. The common case, neither filter biting, hands the input
    /// back without a copy. keptRanks records where each kept row sat in the
    /// full list so the solo filter cannot renumber the row to 1.
    /// </summary>
    private IReadOnlyList<DpsRow> FilterRows(IReadOnlyList<DpsRow> rows)
    {
        var max = Math.Clamp(this.Config.DpsMaxRows, 1, 24);
        this.keptRanks.Clear();
        if (!this.Config.DpsSoloOnly && rows.Count <= max)
        {
            return rows;
        }

        var kept = new List<DpsRow>(Math.Min(rows.Count, max));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (this.Config.DpsSoloOnly && !row.IsSelf)
            {
                continue;
            }

            kept.Add(row);
            this.keptRanks.Add(i + 1);
            if (kept.Count >= max)
            {
                break;
            }
        }

        return kept;
    }

    /// <summary>The 1 based rank of kept row i: its place in the full list
    /// when a filter ran, else its own index.</summary>
    private int RankOf(int i)
        => this.keptRanks.Count == 0 ? i + 1 : this.keptRanks[i];

    private void DrawMeter(string title, string duration, double encDps, IReadOnlyList<DpsRow> rows)
    {
        // Horizoverlay is one strip across the top with the members side by
        // side; like the ACT original, its header reads centred underneath,
        // on its own little skewed chip, a 5px margin below the strip.
        if (this.Config.DpsStyle == DpsMeterStyle.Horizoverlay)
        {
            this.DrawHorizoverlayStrip(rows);
            this.DrawHeader(
                title, duration, encDps,
                centered: true, chip: true, topGap: 5.0f * ClampTextScale(this.TextScale));
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
                    this.DrawBarsRow(this.RankOf(i), rows[i]);
                }

                break;
        }
    }

    private void DrawHeader(
        string title, string duration, double encDps,
        bool centered = false, bool chip = false, float topGap = 0.0f)
    {
        if (!this.Config.DpsShowHeader)
        {
            return;
        }

        // Skip whichever parts the frame did not carry or the user turned
        // off rather than printing dangling separators; with none of them
        // there is no header at all.
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(title);
        }

        if (this.Config.DpsHeaderDuration && !string.IsNullOrWhiteSpace(duration))
        {
            parts.Add(duration);
        }

        if (this.Config.DpsHeaderTotalDps && encDps > 0.0)
        {
            parts.Add(FormatDps(encDps));
        }

        if (parts.Count == 0)
        {
            return;
        }

        if (topGap > 0.0f)
        {
            ImGui.Dummy(new Vector2(1.0f, topGap));
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var text = string.Join(" · ", parts);
        var textSize = ImGui.CalcTextSize(text);
        if (centered)
        {
            var slack = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f) - textSize.X;
            origin.X += Math.Max(slack * 0.5f, 0.0f);
        }

        if (chip)
        {
            // The original's encounter line sits on a fit-content chip with
            // the same skew(-30deg) as the bars.
            var scale = ClampTextScale(this.TextScale);
            var chipTop = origin.Y - (2.0f * scale);
            var chipHeight = textSize.Y + (4.0f * scale);
            AddSkewedQuad(
                drawList,
                origin.X - (10.0f * scale),
                chipTop,
                textSize.X + (20.0f * scale),
                chipHeight,
                SkewFactor * chipHeight,
                ToColor(HorizChip));
        }

        this.DrawStyledText(drawList, origin, this.Config.DpsTextColor, text);

        // Reserve the line like a bar row. The row styles draw the header
        // above the rows, so it also carries the gap onto the first row; the
        // horizoverlay chip sits last and needs none.
        ImGui.Dummy(new Vector2(
            Math.Max(ImGui.GetContentRegionAvail().X, 1.0f),
            ImGui.GetTextLineHeight() + (chip ? 0.0f : Math.Max(this.Config.DpsBarSpacing, 0.0f))));
    }

    /// <summary>Bars: the damage share filled into a dark full-length slot
    /// behind the text, exactly like a timeline bar. The meter's own bar
    /// settings drive it, independent of the timeline box.</summary>
    private void DrawBarsRow(int rank, DpsRow row)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var height = this.Config.DpsBarHeight * ClampTextScale(this.TextScale);
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

    /// <summary>Horizoverlay: the ACT original's single row of skewed bars,
    /// one equal-width cell per member, rank 1 on the left. A cell is the rank
    /// and name centred above a parallelogram bar in the role's tint (solid
    /// white for the local player, dark for the black-and-white theme), the
    /// gold job icon straddling the bar's top edge, hps and dps inside the
    /// bar, and the damage share as a thin skewed strip plus a figure
    /// underneath.</summary>
    private void DrawHorizoverlayStrip(IReadOnlyList<DpsRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var scale = ClampTextScale(this.TextScale);

        // The original's own proportions come from a 13px body font: the name
        // and the in-bar numbers share it, the unit labels are 8px and the
        // percent figure 7px. The box's text runs larger than that, so the
        // strip draws in a 0.8x font with 0.5x/0.45x captions.
        var statFont = this.Fonts.Get(this.TextPx * 0.80f);
        if (statFont is { Available: true })
        {
            using (statFont.Push())
            {
                this.DrawHorizoverlayCells(drawList, rows, origin, width, scale);
            }

            return;
        }

        // Until the bucket is built, shrink the body font to the same
        // effective size instead of laying out at body size: a strip that
        // re-lays out when the atlas catches up visibly snaps after load.
        var fontSize = ImGui.GetFont().FontSize;
        if (fontSize <= 0.0f)
        {
            this.DrawHorizoverlayCells(drawList, rows, origin, width, scale);
            return;
        }

        ImGui.SetWindowFontScale((this.TextPx * 0.80f) / fontSize);
        try
        {
            this.DrawHorizoverlayCells(drawList, rows, origin, width, scale);
        }
        finally
        {
            // Back to the body size the base draw established; the header is
            // drawn after the strip, at body size.
            ImGui.SetWindowFontScale(this.TextPx / fontSize);
        }
    }

    private void DrawHorizoverlayCells(
        ImDrawListPtr drawList, IReadOnlyList<DpsRow> rows, Vector2 origin, float width, float scale)
    {
        var lineHeight = ImGui.GetTextLineHeight();

        // The original caps a bar at 140px with 6px of empty space on either
        // side and centres the group; a narrower window shrinks every cell
        // equally (flex: 1). The side space is ours to configure.
        var padding = Math.Max(this.Config.DpsHorizCellPadding, 0.0f) * scale;
        var cellWidth = Math.Min(width / rows.Count, (140.0f * scale) + (2.0f * padding));
        var stripLeft = origin.X + Math.Max((width - (cellWidth * rows.Count)) * 0.5f, 0.0f);

        var barTop = origin.Y + lineHeight + (3.0f * scale);
        var barHeight = lineHeight + (6.0f * scale);
        var barSkew = SkewFactor * barHeight;
        var stripTop = barTop + barHeight + scale;
        var stripHeight = Math.Max(2.0f * scale, 1.5f);
        var iconSize = 20.0f * scale;

        var showRank = this.Config.DpsHorizShowRank;
        var showIcons = this.Config.DpsHorizShowIcons;
        var showHps = this.Config.DpsHorizShowHps;
        var showPercent = this.Config.DpsHorizShowPercent;
        var twoTone = this.Config.DpsHorizHighlight
            && this.Config.DpsHorizTheme != HorizColorTheme.BlackWhite;

        var cellBottom = barTop + barHeight;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var cellLeft = stripLeft + (i * cellWidth);
            var barLeft = cellLeft + padding;
            var barWidth = cellWidth - (2.0f * padding);
            if (barWidth <= 1.0f)
            {
                continue;
            }

            var barColor = this.HorizBarColor(row);

            // The two-tone "highlight" bar: faint overall, solid on the side
            // of the relevant number — the dps side, or the hps side for a
            // healer who out-heals their dps. Self bars and unknown jobs stay
            // solid, as does the whole strip with the highlight turned off.
            var role = JobColors.RoleOf(row.Job);
            if (!twoTone || row.IsSelf || role == null)
            {
                AddSkewedQuad(drawList, barLeft, barTop, barWidth, barHeight, barSkew, ToColor(barColor));
            }
            else
            {
                var faint = barColor.W / 3.0f;
                AddSkewedQuad(
                    drawList, barLeft, barTop, barWidth, barHeight, barSkew,
                    ToColor(new Vector4(barColor.X, barColor.Y, barColor.Z, faint)));
                var solidLeft = role == JobRole.Healer && row.Hps > row.Dps;
                AddSkewedQuad(
                    drawList,
                    solidLeft ? barLeft : barLeft + (barWidth * HorizSeam),
                    barTop,
                    solidLeft ? barWidth * HorizSeam : barWidth * (1.0f - HorizSeam),
                    barHeight,
                    barSkew,
                    ToColor(barColor));
            }

            // A cell narrower than two icons has no room for the name, icon
            // or numbers: leave the bare bar rather than stacking them all
            // into a few unreadable pixels.
            if (barWidth < iconSize * 2.0f)
            {
                continue;
            }

            if (showPercent)
            {
                cellBottom = Math.Max(cellBottom, stripTop + stripHeight);
            }

            // Rank and name centred over the cell, white with the box's text
            // effect, for the self bar too. The original lets a long name
            // overflow into the margins rather than ellipsizing it.
            var name = showRank ? $"{this.RankOf(i)}. {row.Name}" : row.Name;
            var nameWidth = ImGui.CalcTextSize(name).X;
            this.DrawStyledText(
                drawList,
                new Vector2(cellLeft + ((cellWidth - nameWidth) * 0.5f), origin.Y),
                this.Config.DpsTextColor,
                name);

            if (showIcons)
            {
                var icon = JobIcons.Get(row.Job);
                if (icon != null)
                {
                    var iconTopLeft = new Vector2(
                        barLeft + ((barWidth - iconSize) * 0.5f),
                        barTop - (5.0f * scale));
                    drawList.AddImage(icon.Handle, iconTopLeft, iconTopLeft + new Vector2(iconSize, iconSize));
                }
            }

            // hps on the left, dps on the right, each with its smaller unit
            // label; clipped to the bar so a narrow cell cannot spill into
            // its neighbours. With hps off the job acronym takes its slot,
            // like the original. The white self bar reads black without
            // shadow.
            drawList.PushClipRect(
                new Vector2(barLeft - barSkew, barTop),
                new Vector2(barLeft + barWidth, barTop + barHeight),
                true);
            var textTop = barTop + ((barHeight - lineHeight) * 0.5f);
            if (showHps)
            {
                this.DrawHorizStat(
                    drawList, barLeft + (8.0f * scale), textTop, false,
                    row.Hps.ToString("0.0", CultureInfo.InvariantCulture), "HPS", row.IsSelf);
            }
            else if (!string.IsNullOrWhiteSpace(row.Job))
            {
                this.DrawHorizStat(
                    drawList, barLeft + (8.0f * scale), textTop, false,
                    row.Job.ToUpperInvariant(), string.Empty, row.IsSelf);
            }

            this.DrawHorizStat(
                drawList, barLeft + barWidth - (8.0f * scale), textTop, true,
                row.Dps.ToString("0.00", CultureInfo.InvariantCulture), "DPS", row.IsSelf);
            drawList.PopClipRect();

            if (!showPercent)
            {
                continue;
            }

            // Damage share: the thin skewed strip under the bar, shifted left
            // like the original's, plus the percent figure below its right end.
            var stripLeftEdge = barLeft - (8.0f * scale);
            AddSkewedQuad(
                drawList, stripLeftEdge, stripTop, barWidth, stripHeight,
                SkewFactor * stripHeight,
                ToColor(HorizStripColor(barColor, row.IsSelf, foreground: false)));
            var shareWidth = barWidth * Math.Clamp((float)row.Share / 100.0f, 0.0f, 1.0f);
            if (shareWidth > 0.0f)
            {
                AddSkewedQuad(
                    drawList, stripLeftEdge, stripTop, shareWidth, stripHeight,
                    SkewFactor * stripHeight,
                    ToColor(HorizStripColor(barColor, row.IsSelf, foreground: true)));
            }

            var pct = $"{(int)Math.Clamp(row.Share, 0.0, 999.0)}%";
            var pctTop = stripTop + stripHeight - (2.0f * scale);
            var pctRight = barLeft + barWidth - (10.0f * scale);
            cellBottom = Math.Max(
                cellBottom,
                this.DrawSmallText(drawList, pct, pctRight, pctTop, this.Config.DpsTextColor) + (2.0f * scale));
        }

        // Reserve exactly what the strip drew: the gap onto the encounter
        // line is the header's own, not the bar spacing knob's.
        ImGui.Dummy(new Vector2(width, cellBottom - origin.Y));
    }

    /// <summary>One statistic inside a horizoverlay bar: the number in the
    /// body font with its unit label in the smaller caption font ("5450.30
    /// DPS"). Left-aligned from x, or right-aligned ending at x when
    /// rightAligned. The self bar's stats are plain black, no text effect.
    /// </summary>
    private void DrawHorizStat(
        ImDrawListPtr drawList, float x, float top, bool rightAligned,
        string number, string label, bool self)
    {
        var scale = ClampTextScale(this.TextScale);
        var lineHeight = ImGui.GetTextLineHeight();
        var color = self ? HorizSelfText : this.Config.DpsTextColor;

        var numberWidth = ImGui.CalcTextSize(number).X;
        var small = this.Fonts.Get(this.TextPx * 0.50f);
        var labelWidth = 0.0f;
        var labelTop = top;
        if (small is { Available: true })
        {
            using (small.Push())
            {
                labelWidth = ImGui.CalcTextSize(label).X;
                // Bottom-aligned with the number, like the original's caption.
                labelTop = top + lineHeight - ImGui.GetTextLineHeight();
            }
        }
        else
        {
            labelWidth = ImGui.CalcTextSize(label).X;
        }

        var startX = rightAligned
            ? x - numberWidth - (1.0f * scale) - labelWidth
            : x;
        var labelLeft = startX + numberWidth + (1.0f * scale);

        if (self)
        {
            drawList.AddText(new Vector2(startX, top), ToColor(color), number);
        }
        else
        {
            this.DrawStyledText(drawList, new Vector2(startX, top), color, number);
        }

        if (small is { Available: true })
        {
            using (small.Push())
            {
                if (self)
                {
                    drawList.AddText(new Vector2(labelLeft, labelTop), ToColor(color), label);
                }
                else
                {
                    this.DrawStyledText(drawList, new Vector2(labelLeft, labelTop), color, label);
                }
            }
        }
        else if (self)
        {
            drawList.AddText(new Vector2(labelLeft, labelTop), ToColor(color), label);
        }
        else
        {
            this.DrawStyledText(drawList, new Vector2(labelLeft, labelTop), color, label);
        }
    }

    /// <summary>Small caption text (the damage percent figure), right-aligned
    /// to end at right. Returns the bottom edge, so the strip can size the
    /// cell.</summary>
    private float DrawSmallText(ImDrawListPtr drawList, string text, float right, float top, Vector4 color)
    {
        var small = this.Fonts.Get(this.TextPx * 0.45f);
        if (small is { Available: true })
        {
            using (small.Push())
            {
                this.DrawStyledText(
                    drawList,
                    new Vector2(right - ImGui.CalcTextSize(text).X, top),
                    color,
                    text);
                return top + ImGui.GetTextLineHeight();
            }
        }

        this.DrawStyledText(
            drawList,
            new Vector2(right - ImGui.CalcTextSize(text).X, top),
            color,
            text);

        // Report the size the bucket will have rather than the fallback's:
        // a stable cell height is worth the transient few frames where the
        // fallback's larger text descends past what this reserves.
        return top + (this.TextPx * 0.45f);
    }

    /// <summary>The bar tint for one member: white for the local player in
    /// either theme, otherwise the role's tint — or the plain dark bar for
    /// the black-and-white theme and for jobs we do not know. The role
    /// tint's alpha is the configured bar opacity.</summary>
    private Vector4 HorizBarColor(DpsRow row)
    {
        if (row.IsSelf)
        {
            return HorizSelfBar;
        }

        if (this.Config.DpsHorizTheme == HorizColorTheme.BlackWhite)
        {
            return HorizDimBar;
        }

        var opacity = Math.Clamp(this.Config.DpsHorizBarOpacity, 0.05f, 1.0f);
        return JobColors.RoleOf(row.Job) switch
        {
            JobRole.Tank => WithAlpha(HorizTankBar, opacity),
            JobRole.Healer => WithAlpha(HorizHealerBar, opacity),
            JobRole.Dps => WithAlpha(HorizDpsBar, opacity),
            _ => HorizDimBar,
        };
    }

    /// <summary>The damage-share strip colours derive from the bar tint: the
    /// background track at the original's 0.3 alpha (0.5 for the white self
    /// bar), the filled share at 0.7 (solid white for self).</summary>
    private static Vector4 HorizStripColor(Vector4 barColor, bool self, bool foreground)
    {
        var alpha = foreground ? (self ? 1.00f : 0.70f) : (self ? 0.50f : 0.30f);
        return new Vector4(barColor.X, barColor.Y, barColor.Z, alpha);
    }

    /// <summary>The horizoverlay parallelogram: the top edge runs [x, x+w]
    /// at y and the bottom edge shifts left by k, the original's
    /// skew(-30deg) lean.</summary>
    private static void AddSkewedQuad(ImDrawListPtr drawList, float x, float y, float w, float h, float k, uint color)
    {
        if (w <= 0.0f || h <= 0.0f)
        {
            return;
        }

        drawList.AddQuadFilled(
            new Vector2(x, y),
            new Vector2(x + w, y),
            new Vector2(x + w - k, y + h),
            new Vector2(x - k, y + h),
            color);
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
