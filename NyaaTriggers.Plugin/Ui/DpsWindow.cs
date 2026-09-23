using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

internal sealed partial class DpsWindow : OverlayWindow
{
    private const float TextPadding = 6.0f;

    private static readonly Vector4 HorizonChip = new(0.000f, 0.000f, 0.000f, 0.25f);

    private const float HorizonSeam = 0.49f;

    /// <summary>Unit caption size relative to its number.</summary>
    private const float HorizonLabelRatio = 0.625f;

    private static readonly float[] StatFitRatios = { 1.0f, 0.85f, 0.7f, 0.55f };

    private static readonly DpsRow[] SampleRows =
    {
        new("Y'shtola R", "BLM", 10234.5, 21.4, 14.0, true, 0),
        new("Curious Gorge", "WAR", 9876.0, 20.1, 322.0, false, 1),
        new("Beta Tester", "DRG", 9012.0, 17.8, 0.0, false, 0),
        new("Cid Garlond", "MCH", 8456.0, 15.9, 0.0, false, 0),
        new("Thancred W", "DNC", 7890.0, 13.8, 0.0, false, 2),
        new("Alphinaud L", "SGE", 6543.0, 10.9, 9123.4, false, 0),
    };

    private readonly BridgeHost bridge;

    private static readonly Vector4 DeathsColor = new(0.95f, 0.42f, 0.42f, 1.00f);

    private float contentHeight;

    private bool wasLocked;

    /// <summary>Original DPS ranks for filtered or sorted rows.</summary>
    private readonly List<int> keptRanks = new();

    internal DpsWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts, MeterSettings? settings = null)
        : base($"NyaaTriggers {MeterName(settings?.DpsStyle ?? config.DpsStyle)}###nyaaDps{settings?.DpsStyle ?? config.DpsStyle}", config, fonts)
    {
        this.bridge = bridge;
        this.Meter = settings ?? config;
    }

    internal MeterSettings Meter { get; }

    internal DpsState CurrentDps => this.Meter.DpsHoldLast ? this.bridge.Dps : this.bridge.UnheldDps;

    private static double? EncounterHps(DpsState state)
        => state.EncHps > 0 ? state.EncHps
            : state.Participants > state.Rows.Count ? null : state.Rows.Sum(row => row.Hps);

    internal static string MeterName(DpsMeterStyle style) => style switch
    {
        DpsMeterStyle.HorizonOverlay => "Horizon",
        DpsMeterStyle.Kagerou => "Kagerou",
        _ => "LMeter",
    };

    public override void PreDraw()
    {
        base.PreDraw();
        if (this.Meter.DpsStyle == DpsMeterStyle.Kagerou && this.Meter.DpsKagerouInteractive)
        {
            this.Flags &= ~ImGuiWindowFlags.NoInputs;
        }

        if (this.Config.Locked && this.Meter.DpsStyle == DpsMeterStyle.HorizonOverlay
            && this.contentHeight > 1.0f)
        {
            this.Size = new Vector2(
                this.StoredSize.X / ImGuiHelpers.GlobalScale,
                this.contentHeight / ImGuiHelpers.GlobalScale);
        }
        else if (!this.Config.Locked && this.wasLocked)
        {
            this.SizeCondition = ImGuiCond.Always;
        }

        this.wasLocked = this.Config.Locked;
    }

    protected override Vector2 StoredPosition
    {
        get => this.Meter.DpsPos;
        set => this.Meter.DpsPos = value;
    }

    protected override Vector2 StoredSize
    {
        get => this.Meter.DpsSize;
        set => this.Meter.DpsSize = value;
    }

    internal override void ResetGeometry()
    {
        var fresh = MeterSettings.DefaultsFor(this.Meter.DpsStyle);
        this.StoredPosition = fresh.DpsPos;
        this.StoredSize = fresh.DpsSize;
        this.ForceGeometry();
    }

    protected override float TextScale => this.Meter.DpsTextScale;

    protected override float BgOpacity => this.Meter.DpsBgOpacity;

    protected override float FadeOpacity => this.Meter.DpsFade;

    protected override TextEffectStyle TextEffect => this.Meter.DpsTextEffect;

    protected override int EffectThickness => this.Meter.DpsEffectThickness;

    protected override Vector4 EffectColor => this.Meter.DpsEffectColor;

    /// <summary>Endings carry final rows even when no draw saw them live.</summary>
    internal bool HasHeldContent =>
        (this.Meter.DpsStyle == DpsMeterStyle.Kagerou && this.kagerouHistory != null)
        || (this.Meter.DpsHoldLast && !this.bridge.Dps.Show && this.bridge.Dps.Ended
        && this.bridge.Dps.Rows.Count > 0);

    protected override void DrawContent()
    {
        var dps = this.CurrentDps;
        if (this.Meter.DpsStyle == DpsMeterStyle.Kagerou)
        {
            this.DrawKagerou(dps);
        }
        else if (this.Meter.DpsStyle == DpsMeterStyle.LMeter)
        {
            if ((dps.Show && dps.Rows.Count > 0) || this.HasHeldContent) this.DrawLMeter(dps);
            else if (!this.Config.Locked) this.DrawLMeter(LMeterSample);
        }
        else if ((dps.Show && dps.Rows.Count > 0) || this.HasHeldContent)
        {
            this.DrawHorizonMeter(dps.Title, dps.Duration, dps.EncDps, this.FilterRows(dps.Rows));
        }
        else if (!this.Config.Locked)
        {
            this.DrawHorizonMeter("Sample Encounter", "03:12", 81234.5, this.FilterRows(SampleRows));
        }

        this.contentHeight = ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y
            + ImGui.GetStyle().WindowPadding.Y;
    }

    /// <summary>Preserve DPS ranks and pin the local player before limiting rows.</summary>
    private IReadOnlyList<DpsRow> FilterRows(IReadOnlyList<DpsRow> rows)
    {
        var max = this.Meter.DpsStyle == DpsMeterStyle.Kagerou && this.Meter.DpsKagerouTab == KagerouTab.Alliance
            ? 24 : Math.Clamp(this.Meter.DpsMaxRows, 1, 24);
        var sort = this.Meter.DpsSortOrder;
        this.keptRanks.Clear();
        if (!this.Meter.DpsSoloOnly && !this.Meter.DpsSelfFirst
            && sort == DpsSortOrder.ByDps && rows.Count <= max)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                this.keptRanks.Add(rows[i].Rank > 0 ? rows[i].Rank : i + 1);
            }

            return rows;
        }

        var kept = new List<DpsRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (this.Meter.DpsSoloOnly && !row.IsSelf)
            {
                continue;
            }

            kept.Add(row);
            this.keptRanks.Add(row.Rank > 0 ? row.Rank : i + 1);
        }

        if (sort != DpsSortOrder.ByDps)
        {
            var order = new int[kept.Count];
            for (var i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, Comparer<int>.Create(
                (a, b) => CompareRows(kept[a], kept[b], a, b, sort)));
            var sortedRows = new List<DpsRow>(kept.Count);
            var sortedRanks = new List<int>(kept.Count);
            foreach (var i in order)
            {
                sortedRows.Add(kept[i]);
                sortedRanks.Add(this.keptRanks[i]);
            }

            kept = sortedRows;
            this.keptRanks.Clear();
            this.keptRanks.AddRange(sortedRanks);
        }

        if (this.Meter.DpsSelfFirst)
        {
            for (var i = 1; i < kept.Count; i++)
            {
                if (!kept[i].IsSelf)
                {
                    continue;
                }

                var row = kept[i];
                var rank = this.keptRanks[i];
                kept.RemoveAt(i);
                this.keptRanks.RemoveAt(i);
                kept.Insert(0, row);
                this.keptRanks.Insert(0, rank);
                break;
            }
        }

        if (kept.Count > max)
        {
            kept.RemoveRange(max, kept.Count - max);
            this.keptRanks.RemoveRange(max, this.keptRanks.Count - max);
        }

        return kept;
    }

    private int CompareRows(DpsRow a, DpsRow b, int indexA, int indexB, DpsSortOrder sort)
    {
        var by = sort switch
        {
            DpsSortOrder.Alphabetical => string.Compare(this.RowName(a), this.RowName(b), StringComparison.OrdinalIgnoreCase),
            DpsSortOrder.ByRole => RoleRank(a.Job).CompareTo(RoleRank(b.Job)),
            _ => 0,
        };
        return by != 0 ? by : indexA.CompareTo(indexB);
    }

    private static int RoleRank(string job) => JobColors.RoleOf(job) switch
    {
        JobRole.Tank => 0,
        JobRole.Healer => 1,
        JobRole.Dps => 2,
        _ => 3,
    };

    private string RowName(DpsRow row)
    {
        if (row.IsSelf)
        {
            return this.Meter.DpsSelfNameYou ? "YOU" : row.Name;
        }

        return this.Meter.DpsNamePrivacy switch
        {
            NamePrivacyStyle.Initials => Initials(row.Name),
            NamePrivacyStyle.Hidden => string.Empty,
            _ => row.Name,
        };
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(name.Length + parts.Length);
        foreach (var part in parts)
        {
            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1 && char.IsHighSurrogate(part[0]) && char.IsLowSurrogate(part[1]))
            {
                builder.Append(part[1]);
            }

            builder.Append(". ");
        }

        return builder.ToString().TrimEnd();
    }

    private string? DeathsMarker(DpsRow row)
        => this.Meter.DpsShowDeaths && row.Deaths > 0 ? $" x{row.Deaths}" : null;

    private int RankOf(int i)
        => this.keptRanks.Count == 0 ? i + 1 : this.keptRanks[i];

    private void DrawHorizonMeter(string title, string duration, double encDps, IReadOnlyList<DpsRow> rows)
    {
        this.DrawHorizonStrip(rows);
        this.DrawHeader(title, duration, encDps,
            centered: true, chip: true, topGap: 5.0f * ClampTextScale(this.TextScale));
    }

    private void DrawHeader(
        string title, string duration, double encDps,
        bool centered = false, bool chip = false, float topGap = 0.0f)
    {
        if (!this.Meter.DpsShowHeader)
        {
            return;
        }

        var text = this.FormatHeaderLine(title, duration, encDps);
        if (text.Length == 0)
        {
            return;
        }

        if (topGap > 0.0f)
        {
            ImGui.Dummy(new Vector2(1.0f, topGap));
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        if (centered)
        {
            var slack = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f) - textSize.X;
            origin.X += Math.Max(slack * 0.5f, 0.0f);
        }

        if (chip)
        {
            var scale = ClampTextScale(this.TextScale);
            var chipTop = origin.Y - (2.0f * scale);
            var chipHeight = textSize.Y + (4.0f * scale);
            AddSkewedQuad(
                drawList,
                origin.X - (10.0f * scale),
                chipTop,
                textSize.X + (20.0f * scale),
                chipHeight,
                this.HorizonSkew() * chipHeight,
                ToColor(HorizonChip));
        }

        this.DrawStyledText(drawList, origin, this.Meter.DpsTextColor, text);

        // Horizon draws its header last, so it needs no gap below.
        ImGui.Dummy(new Vector2(
            Math.Max(ImGui.GetContentRegionAvail().X, 1.0f),
            ImGui.GetTextLineHeight() + (chip ? 0.0f : Math.Max(this.Meter.DpsBarSpacing, 0.0f))));
    }

    private static readonly Regex HeaderSpaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Token values must not expand as nested templates.</summary>
    private static readonly Regex HeaderTokens = new(
        @"\{(?:title|duration|dps)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Collapse adjacent separators left by omitted header tokens.</summary>
    private static readonly Regex HeaderSepRuns = new(
        @"\s*[·•|/\-–—]\s*(\s*[·•|/\-–—]\s*)+", RegexOptions.Compiled);

    private string FormatHeaderLine(string title, string duration, double encDps)
    {
        var titleText = string.IsNullOrWhiteSpace(title) ? string.Empty : title;
        var durationText = this.Meter.DpsHeaderDuration && !string.IsNullOrWhiteSpace(duration)
            ? duration
            : string.Empty;
        var dpsText = this.Meter.DpsHeaderTotalDps && encDps > 0.0 ? FormatDps(encDps) : string.Empty;

        var format = this.Meter.DpsHeaderFormat;
        if (string.IsNullOrWhiteSpace(format))
        {
            var parts = new List<string>(3);
            if (titleText.Length > 0)
            {
                parts.Add(titleText);
            }

            if (durationText.Length > 0)
            {
                parts.Add(durationText);
            }

            if (dpsText.Length > 0)
            {
                parts.Add(dpsText);
            }

            return string.Join(" · ", parts);
        }

        var text = HeaderTokens.Replace(format, match => match.Value.ToLowerInvariant() switch
        {
            "{title}" => titleText,
            "{duration}" => durationText,
            _ => dpsText,
        });
        text = HeaderSepRuns.Replace(HeaderSpaces.Replace(text, " "), FirstSeparator);
        return text.Trim(' ', '·', '•', '|', '/', '-', '–', '—');
    }

    private static string FirstSeparator(Match match)
    {
        foreach (var ch in match.Value)
        {
            if (ch is '·' or '•' or '|' or '/' or '-' or '–' or '—')
            {
                return $" {ch} ";
            }
        }

        return " ";
    }

    private void DrawHorizonStrip(IReadOnlyList<DpsRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var scale = ClampTextScale(this.TextScale);

        using (this.UseFont(this.TextPx * this.HorizonStatScale()))
        {
            this.DrawHorizonCells(drawList, rows, origin, width, scale);
        }
    }

    private void DrawHorizonCells(
        ImDrawListPtr drawList, IReadOnlyList<DpsRow> rows, Vector2 origin, float width, float scale)
    {
        var lineHeight = ImGui.GetTextLineHeight();

        var padding = Math.Max(this.Meter.DpsHorizCellPadding, 0.0f) * scale;
        var maxBarWidth = Math.Clamp(this.Meter.DpsHorizMaxBarWidth, 40.0f, 400.0f) * scale;
        var cellWidth = Math.Min(width / rows.Count, maxBarWidth + (2.0f * padding));
        var stripLeft = origin.X + Math.Max((width - (cellWidth * rows.Count)) * 0.5f, 0.0f);

        var showNames = this.Meter.DpsHorizShowNames;
        var showRank = this.Meter.DpsHorizShowRank;
        var showIcons = this.Meter.DpsHorizShowIcons;
        var showHps = this.Meter.DpsHorizShowHps;
        var showPercent = this.Meter.DpsHorizShowPercent;
        var twoTone = this.Meter.DpsHorizHighlight
            && this.Meter.DpsHorizTheme != HorizonColorTheme.BlackWhite;

        var barHeight = Math.Max(Math.Clamp(this.Meter.DpsHorizBarHeight, 10.0f, 60.0f) * scale, lineHeight + scale);
        var barSkew = this.HorizonSkew() * barHeight;
        var iconSize = Math.Clamp(this.Meter.DpsHorizIconSize, 8.0f, 64.0f) * scale;

        // Reserve the icon overhang when no name line provides that space.
        var iconOverhang = showIcons ? iconSize * 0.25f : 0.0f;
        var nameBand = showNames ? lineHeight + (3.0f * scale) : 0.0f;
        var barTop = origin.Y + Math.Max(nameBand, iconOverhang + scale);
        var stripHeight = Math.Max(2.0f * scale, 1.5f);

        // Include the icon bottom to avoid clipping or overlapping the share strip.
        var iconBottom = showIcons ? barTop - iconOverhang + iconSize : barTop;
        var cellBottom = Math.Max(barTop + barHeight, iconBottom);
        var stripTop = cellBottom + scale;
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

            var barColor = this.HorizonBarColor(row);

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
                    solidLeft ? barLeft : barLeft + (barWidth * HorizonSeam),
                    barTop,
                    solidLeft ? barWidth * HorizonSeam : barWidth * (1.0f - HorizonSeam),
                    barHeight,
                    barSkew,
                    ToColor(barColor));
            }

            if (barWidth < 36.0f * scale)
            {
                continue;
            }

            if (showPercent)
            {
                cellBottom = Math.Max(cellBottom, stripTop + stripHeight);
            }

            // Allow names to extend into cell margins, matching the original overlay.
            if (showNames)
            {
                var name = showRank ? $"{this.RankOf(i)}. {this.RowName(row)}" : this.RowName(row);
                var nameWidth = ImGui.CalcTextSize(name).X;
                var nameLeft = cellLeft + ((cellWidth - nameWidth) * 0.5f);
                this.DrawStyledText(
                    drawList,
                    new Vector2(nameLeft, origin.Y),
                    this.Meter.DpsTextColor,
                    name);

                var deaths = this.DeathsMarker(row);
                if (deaths != null)
                {
                    this.DrawStyledText(
                        drawList,
                        new Vector2(nameLeft + nameWidth, origin.Y),
                        DeathsColor,
                        deaths);
                }
            }

            // Reserve horizontal space for the icon only when it overlaps the stat line.
            var hasIcon = false;
            var iconLeft = barLeft + ((barWidth - iconSize) * 0.5f);
            if (showIcons)
            {
                var icon = JobIcons.Get(row.Job);
                if (icon != null)
                {
                    hasIcon = true;
                    var iconTopLeft = new Vector2(iconLeft, barTop - iconOverhang);
                    drawList.AddImage(
                        icon.Handle,
                        iconTopLeft,
                        iconTopLeft + new Vector2(iconSize, iconSize),
                        Vector2.Zero,
                        Vector2.One,
                        ToColor(new Vector4(1.0f, 1.0f, 1.0f, 1.0f)));
                }
            }

            var inset = 8.0f * scale;
            var middle = barLeft + (barWidth * 0.5f);
            var statBottom = barTop + barHeight - scale;
            var iconInTheWay = hasIcon
                && barTop - iconOverhang + iconSize > statBottom - lineHeight + (1.0f * scale);
            var leftZoneRight = iconInTheWay ? iconLeft - (2.0f * scale) : middle;
            var rightZoneLeft = iconInTheWay ? iconLeft + iconSize + (2.0f * scale) : middle;
            drawList.PushClipRect(
                new Vector2(barLeft - barSkew, barTop),
                new Vector2(barLeft + barWidth, barTop + barHeight),
                true);
            if (showHps)
            {
                this.DrawHorizonStat(
                    drawList, barLeft + inset, leftZoneRight, statBottom, false,
                    row.Hps.ToString("0.0", CultureInfo.InvariantCulture), "HPS", row.IsSelf);
            }
            else if (!string.IsNullOrWhiteSpace(row.Job))
            {
                this.DrawHorizonStat(
                    drawList, barLeft + inset, leftZoneRight, statBottom, false,
                    row.Job.ToUpperInvariant(), string.Empty, row.IsSelf);
            }

            this.DrawHorizonStat(
                drawList, rightZoneLeft, barLeft + barWidth - inset, statBottom, true,
                this.FormatRowDps(row.Dps), "DPS", row.IsSelf);
            drawList.PopClipRect();

            if (!showPercent)
            {
                continue;
            }

            var stripLeftEdge = Math.Max(barLeft - (8.0f * scale), origin.X);
            var stripSkew = this.HorizonSkew() * stripHeight;
            AddSkewedQuad(
                drawList, stripLeftEdge, stripTop, barWidth, stripHeight,
                stripSkew,
                ToColor(HorizonStripColor(barColor, row.IsSelf, foreground: false)));
            var shareWidth = barWidth * Math.Clamp((float)row.Share / 100.0f, 0.0f, 1.0f);
            if (shareWidth > 0.0f)
            {
                AddSkewedQuad(
                    drawList, stripLeftEdge, stripTop, shareWidth, stripHeight,
                    stripSkew,
                    ToColor(HorizonStripColor(barColor, row.IsSelf, foreground: true)));
            }

            var pct = $"{(int)Math.Clamp(row.Share, 0.0, 999.0)}%";
            var pctTop = stripTop + stripHeight - (2.0f * scale);
            var pctRight = barLeft + barWidth - (10.0f * scale);
            cellBottom = Math.Max(
                cellBottom,
                this.DrawSmallText(drawList, pct, pctRight, pctTop, this.Meter.DpsTextColor) + (2.0f * scale));
        }

        ImGui.Dummy(new Vector2(width, cellBottom - origin.Y));
    }

    /// <summary>Drop the unit, shrink, then truncate. Local player stats skip text effects.</summary>
    private void DrawHorizonStat(
        ImDrawListPtr drawList, float zoneLeft, float zoneRight, float bottom,
        bool rightAligned, string number, string label, bool self)
    {
        var maxWidth = zoneRight - zoneLeft;
        if (maxWidth <= 1.0f || string.IsNullOrEmpty(number))
        {
            return;
        }

        var scale = ClampTextScale(this.TextScale);
        var gap = 1.0f * scale;
        var color = self ? this.Meter.DpsHorizSelfTextColor : this.Meter.DpsTextColor;
        var statPx = this.TextPx * this.HorizonStatScale();
        var withLabel = !string.IsNullOrEmpty(label);

        foreach (var ratio in StatFitRatios)
        {
            // Skip smaller fonts that are still loading.
            var handle = ratio >= 1.0f ? null : this.Fonts.Get(statPx * ratio);
            if (ratio < 1.0f && handle is not { Available: true })
            {
                continue;
            }

            using (this.UseFont(statPx * ratio))
            {
                var numberWidth = ImGui.CalcTextSize(number).X;
                var labelPx = statPx * ratio * HorizonLabelRatio;
                var labelWidth = 0.0f;
                if (withLabel && this.Fonts.Get(labelPx) is { Available: true })
                {
                    using (this.UseFont(labelPx))
                    {
                        labelWidth = ImGui.CalcTextSize(label).X;
                    }
                }

                var fitsLabel = labelWidth > 0
                    && numberWidth + gap + labelWidth <= maxWidth;
                if (!fitsLabel && numberWidth > maxWidth)
                {
                    continue;
                }

                this.PaintHorizonStat(
                    drawList, zoneLeft, zoneRight, bottom, rightAligned,
                    number, label, fitsLabel ? labelPx : 0.0f,
                    fitsLabel ? labelWidth : 0.0f, numberWidth, gap, color, self);
                return;
            }
        }

        var trimmed = Elide(number, maxWidth);
        this.PaintHorizonStat(
            drawList, zoneLeft, zoneRight, bottom, rightAligned,
            trimmed, string.Empty, 0.0f, 0.0f, ImGui.CalcTextSize(trimmed).X,
            gap, color, self);
    }

    private void PaintHorizonStat(
        ImDrawListPtr drawList, float zoneLeft, float zoneRight, float bottom,
        bool rightAligned, string number, string label, float labelPx,
        float labelWidth, float numberWidth, float gap, Vector4 color, bool self)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var top = bottom - lineHeight;
        var startX = rightAligned
            ? zoneRight - numberWidth - (labelWidth > 0.0f ? gap + labelWidth : 0.0f)
            : zoneLeft;

        if (self)
        {
            drawList.AddText(new Vector2(startX, top), ToColor(color), number);
        }
        else
        {
            this.DrawStyledText(drawList, new Vector2(startX, top), color, number);
        }

        if (labelPx <= 0.0f || string.IsNullOrEmpty(label))
        {
            return;
        }

        using (this.UseFont(labelPx))
        {
            var labelTop = top + lineHeight - ImGui.GetTextLineHeight();
            var labelLeft = startX + numberWidth + gap;
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

    private float DrawSmallText(ImDrawListPtr drawList, string text, float right, float top, Vector4 color)
    {
        using (this.UseFont(this.TextPx * this.HorizonPercentScale()))
        {
            this.DrawStyledText(
                drawList,
                new Vector2(right - ImGui.CalcTextSize(text).X, top),
                color,
                text);
            return top + ImGui.GetTextLineHeight();
        }
    }

    private Vector4 HorizonBarColor(DpsRow row)
    {
        if (row.IsSelf)
        {
            return this.Meter.DpsHorizSelfColor;
        }

        if (this.Meter.DpsHorizTheme == HorizonColorTheme.BlackWhite)
        {
            return this.Meter.DpsHorizDimColor;
        }

        var opacity = Math.Clamp(this.Meter.DpsHorizBarOpacity, 0.05f, 1.0f);
        return JobColors.RoleOf(row.Job) switch
        {
            JobRole.Tank => WithAlpha(this.Meter.DpsHorizTankColor, opacity),
            JobRole.Healer => WithAlpha(this.Meter.DpsHorizHealerColor, opacity),
            JobRole.Dps => WithAlpha(this.Meter.DpsHorizDpsColor, opacity),
            _ => this.Meter.DpsHorizDimColor,
        };
    }

    private static Vector4 HorizonStripColor(Vector4 barColor, bool self, bool foreground)
    {
        var alpha = foreground ? (self ? 1.00f : 0.70f) : (self ? 0.50f : 0.30f);
        return new Vector4(barColor.X, barColor.Y, barColor.Z, alpha);
    }

    private float HorizonSkew()
        => (float)Math.Tan(Math.Clamp(this.Meter.DpsHorizSkew, 0.0f, 45.0f) * Math.PI / 180.0);

    private float HorizonStatScale() => Math.Clamp(this.Meter.DpsHorizStatScale, 0.4f, 1.5f);

    private float HorizonPercentScale() => Math.Clamp(this.Meter.DpsHorizPercentScale, 0.4f, 1.5f);

    private string FormatRowDps(double dps)
    {
        if (this.Meter.DpsHorizCompact)
        {
            return FormatDps(dps);
        }

        return Math.Clamp(this.Meter.DpsHorizDecimals, 0, 2) switch
        {
            0 => dps.ToString("0", CultureInfo.InvariantCulture),
            1 => dps.ToString("0.0", CultureInfo.InvariantCulture),
            _ => dps.ToString("0.00", CultureInfo.InvariantCulture),
        };
    }

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

    private static string FormatShare(double share)
        => share.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string FormatDps(double dps)
    {
        // Switch units before rounding would produce 1000 in the smaller unit.
        if (dps >= 999950.0)
        {
            return (dps / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }

        if (dps >= 999.5)
        {
            return (dps / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }

        return dps.ToString("0", CultureInfo.InvariantCulture);
    }
}
