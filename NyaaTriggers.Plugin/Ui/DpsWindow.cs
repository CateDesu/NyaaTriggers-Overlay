using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

internal sealed class DpsWindow : OverlayWindow
{
    private const float TextPadding = 6.0f;

    private static readonly Vector4 HorizonChip = new(0.000f, 0.000f, 0.000f, 0.25f);

    /// <summary>Split between the two bar shades. Emphasize HPS for healers whose HPS
    /// exceeds DPS, otherwise DPS.</summary>
    private const float HorizonSeam = 0.49f;

    /// <summary>Unit caption size relative to its number.</summary>
    private const float HorizonLabelRatio = 0.625f;

    /// <summary>Try smaller stat fonts in this order after dropping the unit
    /// label.</summary>
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

    /// <summary>Content height including padding, used to fit the locked Horizon Overlay
    /// window.</summary>
    private float contentHeight;

    /// <summary>Restore the stored height on unlock so geometry capture does not overwrite
    /// it with the fitted height.</summary>
    private bool wasLocked;

    /// <summary>Original ranks for filtered rows. Empty when the original list is used
    /// unchanged.</summary>
    private readonly List<int> keptRanks = new();

    internal DpsWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts)
        : base("NyaaTriggers DPS###nyaaDps", config, fonts)
    {
        this.bridge = bridge;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (this.Config.DpsStyle == DpsMeterStyle.HorizonOverlay)
        {
            if (this.Config.Locked && this.contentHeight > 1.0f)
            {
                this.Size = new Vector2(
                    this.StoredSize.X / ImGuiHelpers.GlobalScale,
                    this.contentHeight / ImGuiHelpers.GlobalScale);
            }
            else if (!this.Config.Locked && this.wasLocked)
            {
                // Force the stored height once because FirstUseEver would preserve the
                // fitted height.
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

    internal override void ResetGeometry()
    {
        var fresh = new Configuration();
        this.StoredPosition = fresh.DpsPos;
        this.StoredSize = fresh.DpsSize;
        this.ForceGeometry();
    }

    protected override float TextScale => this.Config.DpsTextScale;

    protected override float BgOpacity => this.Config.DpsBgOpacity;

    protected override float FadeOpacity => this.Config.DpsFade;

    protected override TextEffectStyle TextEffect => this.Config.DpsTextEffect;

    protected override int EffectThickness => this.Config.DpsEffectThickness;

    protected override Vector4 EffectColor => this.Config.DpsEffectColor;

    /// <summary>Use only final rows attached to this ending, even if no draw saw them
    /// live.</summary>
    internal bool HasHeldContent =>
        this.Config.DpsHoldLast && !this.bridge.Dps.Show && this.bridge.Dps.Ended
        && this.bridge.Dps.Rows.Count > 0;

    protected override void DrawContent()
    {
        var dps = this.bridge.Dps;
        if ((dps.Show && dps.Rows.Count > 0) || this.HasHeldContent)
        {
            this.DrawMeter(dps.Title, dps.Duration, dps.EncDps, this.FilterRows(dps.Rows));
        }
        else if (!this.Config.Locked)
        {
            this.DrawMeter("Sample Encounter", "03:12", 81234.5, this.FilterRows(SampleRows));
        }

        // Use the measured height, including padding, for the next locked Horizon Overlay
        // draw.
        this.contentHeight = ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y
            + ImGui.GetStyle().WindowPadding.Y;
    }

    /// <summary>Filter and sort while preserving original ranks. Pin the local player
    /// before limiting row count. Return the input unchanged when no transformation is
    /// needed.</summary>
    private IReadOnlyList<DpsRow> FilterRows(IReadOnlyList<DpsRow> rows)
    {
        var max = Math.Clamp(this.Config.DpsMaxRows, 1, 24);
        var sort = this.Config.DpsSortOrder;
        this.keptRanks.Clear();
        if (!this.Config.DpsSoloOnly && !this.Config.DpsSelfFirst
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
            if (this.Config.DpsSoloOnly && !row.IsSelf)
            {
                continue;
            }

            kept.Add(row);
            this.keptRanks.Add(row.Rank > 0 ? row.Rank : i + 1);
        }

        if (sort != DpsSortOrder.ByDps)
        {
            // Move ranks with their rows and preserve original order for ties.
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

        if (this.Config.DpsSelfFirst)
        {
            for (var i = 1; i < kept.Count; i++)
            {
                if (!kept[i].IsSelf)
                {
                    continue;
                }

                // The first slot places the pinned player at the left of the strip or top
                // of a list.
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

    /// <summary>Sort using displayed names or role. Hidden names compare equally. Original
    /// order breaks ties.</summary>
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
            return this.Config.DpsSelfNameYou ? "YOU" : row.Name;
        }

        return this.Config.DpsNamePrivacy switch
        {
            NamePrivacyStyle.Initials => Initials(row.Name),
            NamePrivacyStyle.Hidden => string.Empty,
            _ => row.Name,
        };
    }

    /// <summary>Format names such as Y'shtola Rhul as Y. R.</summary>
    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(name.Length + parts.Length);
        foreach (var part in parts)
        {
            builder.Append(char.ToUpperInvariant(part[0]));
            // Keep a surrogate pair intact when taking an initial.
            if (part.Length > 1 && char.IsHighSurrogate(part[0]) && char.IsLowSurrogate(part[1]))
            {
                builder.Append(part[1]);
            }

            builder.Append(". ");
        }

        return builder.ToString().TrimEnd();
    }

    private string? DeathsMarker(DpsRow row)
        => this.Config.DpsShowDeaths && row.Deaths > 0 ? $" x{row.Deaths}" : null;

    private int RankOf(int i)
        => this.keptRanks.Count == 0 ? i + 1 : this.keptRanks[i];

    private void DrawMeter(string title, string duration, double encDps, IReadOnlyList<DpsRow> rows)
    {
        // Horizon Overlay places the encounter header below its strip.
        if (this.Config.DpsStyle == DpsMeterStyle.HorizonOverlay)
        {
            this.DrawHorizonStrip(rows);
            this.DrawHeader(
                title, duration, encDps,
                centered: true, chip: true, topGap: 5.0f * ClampTextScale(this.TextScale));
            return;
        }

        this.DrawHeader(title, duration, encDps);
        this.DrawRows(rows);
    }

    private void DrawRows(IReadOnlyList<DpsRow> rows)
    {
        switch (this.Config.DpsStyle)
        {
            case DpsMeterStyle.Kagerou:
                for (var i = 0; i < rows.Count; i++)
                {
                    this.DrawKagerouRow(this.RankOf(i), i, rows[i]);
                }

                break;

            default:
                for (var i = 0; i < rows.Count; i++)
                {
                    this.DrawBarsRow(this.RankOf(i), i, rows[i]);
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
            // Fit the header background to its text and use the bar skew.
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

        this.DrawStyledText(drawList, origin, this.Config.DpsTextColor, text);

        // Reserve spacing after headers above rows. Horizon Overlay draws its header last.
        ImGui.Dummy(new Vector2(
            Math.Max(ImGui.GetContentRegionAvail().X, 1.0f),
            ImGui.GetTextLineHeight() + (chip ? 0.0f : Math.Max(this.Config.DpsBarSpacing, 0.0f))));
    }

    /// <summary>Collapse spaces left by omitted header tokens.</summary>
    private static readonly Regex HeaderSpaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Collapse adjacent separators left by omitted header tokens.</summary>
    private static readonly Regex HeaderSepRuns = new(
        @"\s*[·•|/\-–—]\s*(\s*[·•|/\-–—]\s*)+", RegexOptions.Compiled);

    /// <summary>Expand optional header tokens and remove separators left by omitted values.
    /// An empty format uses dot separators.</summary>
    private string FormatHeaderLine(string title, string duration, double encDps)
    {
        var titleText = string.IsNullOrWhiteSpace(title) ? string.Empty : title;
        var durationText = this.Config.DpsHeaderDuration && !string.IsNullOrWhiteSpace(duration)
            ? duration
            : string.Empty;
        var dpsText = this.Config.DpsHeaderTotalDps && encDps > 0.0 ? FormatDps(encDps) : string.Empty;

        var format = this.Config.DpsHeaderFormat;
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

        var text = format
            .Replace("{title}", titleText, StringComparison.OrdinalIgnoreCase)
            .Replace("{duration}", durationText, StringComparison.OrdinalIgnoreCase)
            .Replace("{dps}", dpsText, StringComparison.OrdinalIgnoreCase);
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

    private void DrawBarsRow(int rank, int index, DpsRow row)
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
            // Local highlight takes priority over top rank, then job colour. Job fills
            // retain the configured bar alpha.
            var fill = this.Config.DpsBarSelfHighlight && row.IsSelf
                ? this.Config.DpsBarSelfColor
                : this.Config.DpsBarTopHighlight && rank == 1
                    ? this.Config.DpsBarTopColor
                    : this.Config.DpsBarJobColors
                        ? WithAlpha(JobColors.Get(row.Job), Math.Clamp(this.Config.DpsBarColor.W, 0.0f, 1.0f))
                        : this.Config.DpsBarColor;
            AddBarFill(drawList, fillOrigin, fillOrigin + new Vector2(fillWidth, height),
                fill, rounding);
        }

        // Draw stripes over the fill and track so the whole row lightens evenly.
        if (this.Config.DpsRowStripes && (index & 1) == 1)
        {
            var stripe = Math.Clamp(this.Config.DpsRowStripeOpacity, 0.0f, 0.5f);
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(width, height),
                ToColor(new Vector4(1.0f, 1.0f, 1.0f, stripe)),
                rounding);
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

        var name = this.RowName(row);
        var label = this.Config.DpsRowsShowRank ? $"{rank}  " : string.Empty;
        if (name.Length > 0)
        {
            label += name;
        }

        if (!string.IsNullOrWhiteSpace(row.Job))
        {
            label += name.Length > 0 ? $" · {row.Job}" : row.Job;
        }

        var dpsText = this.Config.DpsBarsShowShare
            ? $"{this.FormatRowNumber(row.Dps)} · {FormatShare(row.Share)}"
            : this.FormatRowNumber(row.Dps);
        if (this.Config.DpsRowsShowHps && row.Hps > 0.0)
        {
            dpsText += $" · {this.FormatRowNumber(row.Hps)} hps";
        }

        var dpsWidth = ImGui.CalcTextSize(dpsText).X;
        var deaths = this.DeathsMarker(row);
        var deathsWidth = deaths == null ? 0.0f : ImGui.CalcTextSize(deaths).X;

        // Reserve the icon slot even when no texture is available to keep labels aligned.
        var textLeft = TextPadding;
        if (this.Config.DpsRowsShowIcons)
        {
            // Shrink icons to fit short rows.
            var iconSize = Math.Clamp(
                ImGui.GetTextLineHeight() + 2.0f, 1.0f, Math.Max(height - 4.0f, 1.0f));
            var icon = JobIcons.Get(row.Job);
            if (icon != null)
            {
                var iconTop = origin.Y + ((height - iconSize) * 0.5f);
                drawList.AddImage(
                    icon.Handle,
                    new Vector2(origin.X + TextPadding, iconTop),
                    new Vector2(origin.X + TextPadding + iconSize, iconTop + iconSize),
                    Vector2.Zero,
                    Vector2.One,
                    ToColor(new Vector4(1.0f, 1.0f, 1.0f, 1.0f)));
            }

            textLeft = TextPadding + iconSize + TextPadding;
        }

        // Reserve DPS and death marker widths before fitting the name.
        label = Elide(label, Math.Max(width - dpsWidth - textLeft - (TextPadding * 2.0f) - deathsWidth, 1.0f));
        var textY = origin.Y + ((height - ImGui.CalcTextSize(label).Y) * 0.5f);

        this.DrawStyledText(
            drawList, origin + new Vector2(textLeft, textY), this.Config.DpsTextColor, label);

        if (deaths != null)
        {
            var labelWidth = ImGui.CalcTextSize(label).X;
            this.DrawStyledText(
                drawList,
                origin + new Vector2(textLeft + labelWidth, textY),
                DeathsColor,
                deaths);
        }

        this.DrawStyledText(
            drawList,
            new Vector2(origin.X + Math.Max(width - dpsWidth - TextPadding, TextPadding), textY),
            this.Config.DpsTextColor,
            dpsText);

        // Draw list calls reserve no layout space, so advance past this row explicitly.
        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Config.DpsBarSpacing, 0.0f)));
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

        var statFont = this.Fonts.Get(this.TextPx * this.HorizonStatScale());
        if (statFont is { Available: true })
        {
            using (statFont.Push())
            {
                this.DrawHorizonCells(drawList, rows, origin, width, scale);
            }

            return;
        }

        // Match the target font size while it loads to keep layout stable.
        var fontSize = ImGui.GetFont().FontSize;
        if (fontSize <= 0.0f)
        {
            this.DrawHorizonCells(drawList, rows, origin, width, scale);
            return;
        }

        ImGui.SetWindowFontScale((this.TextPx * this.HorizonStatScale()) / fontSize);
        try
        {
            this.DrawHorizonCells(drawList, rows, origin, width, scale);
        }
        finally
        {
            // Restore body size for the header drawn after the strip.
            ImGui.SetWindowFontScale(this.TextPx / fontSize);
        }
    }

    private void DrawHorizonCells(
        ImDrawListPtr drawList, IReadOnlyList<DpsRow> rows, Vector2 origin, float width, float scale)
    {
        var lineHeight = ImGui.GetTextLineHeight();

        // Center the group and shrink cells equally when the available width is limited.
        var padding = Math.Max(this.Config.DpsHorizCellPadding, 0.0f) * scale;
        var maxBarWidth = Math.Clamp(this.Config.DpsHorizMaxBarWidth, 40.0f, 400.0f) * scale;
        var cellWidth = Math.Min(width / rows.Count, maxBarWidth + (2.0f * padding));
        var stripLeft = origin.X + Math.Max((width - (cellWidth * rows.Count)) * 0.5f, 0.0f);

        var showNames = this.Config.DpsHorizShowNames;
        var showRank = this.Config.DpsHorizShowRank;
        var showIcons = this.Config.DpsHorizShowIcons;
        var showHps = this.Config.DpsHorizShowHps;
        var showPercent = this.Config.DpsHorizShowPercent;
        var twoTone = this.Config.DpsHorizHighlight
            && this.Config.DpsHorizTheme != HorizonColorTheme.BlackWhite;

        var barHeight = Math.Clamp(this.Config.DpsHorizBarHeight, 10.0f, 60.0f) * scale;
        var barSkew = this.HorizonSkew() * barHeight;
        var iconSize = Math.Clamp(this.Config.DpsHorizIconSize, 8.0f, 64.0f) * scale;

        // Reserve the icon overhang when no name line provides that space.
        var iconOverhang = showIcons ? iconSize * 0.25f : 0.0f;
        var nameBand = showNames ? lineHeight + (3.0f * scale) : 0.0f;
        var barTop = origin.Y + Math.Max(nameBand, iconOverhang + scale);
        var stripTop = barTop + barHeight + scale;
        var stripHeight = Math.Max(2.0f * scale, 1.5f);

        // Include the icon bottom in cell height so large icons cannot overlap the share
        // strip or be clipped.
        var iconBottom = showIcons ? barTop - iconOverhang + iconSize : barTop;
        var cellBottom = Math.Max(barTop + barHeight, iconBottom);
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

            // Emphasize HPS for healers with HPS above DPS, otherwise DPS. Local and
            // unknown job bars use a uniform tint.
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

            // Omit text and icons when the cell is too narrow to fit them.
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
                    this.Config.DpsTextColor,
                    name);

                // Center the name and death marker as a single group.
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

            // Keep HPS and DPS inside separate regions below the icon. Clip to the bar
            // bounds and show the job acronym when HPS is disabled.
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

            // Keep the shifted damage share strip inside the content area.
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
                this.DrawSmallText(drawList, pct, pctRight, pctTop, this.Config.DpsTextColor) + (2.0f * scale));
        }

        // The header adds its own gap after the strip.
        ImGui.Dummy(new Vector2(width, cellBottom - origin.Y));
    }

    /// <summary>Fit a stat within its assigned region by dropping the unit label, trying
    /// smaller fonts, then truncating. Local player stats use their own colour without a
    /// text effect.</summary>
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
        var color = self ? this.Config.DpsHorizSelfTextColor : this.Config.DpsTextColor;
        var statPx = this.TextPx * this.HorizonStatScale();
        var withLabel = !string.IsNullOrEmpty(label);

        foreach (var ratio in StatFitRatios)
        {
            // Reuse the current font at full size. Skip smaller sizes that are still
            // loading.
            var handle = ratio >= 1.0f ? null : this.Fonts.Get(statPx * ratio);
            if (ratio < 1.0f && handle is not { Available: true })
            {
                continue;
            }

            using (handle?.Push())
            {
                var numberWidth = ImGui.CalcTextSize(number).X;
                IFontHandle? labelFont = null;
                var labelWidth = 0.0f;
                if (withLabel)
                {
                    labelFont = this.Fonts.Get(statPx * ratio * HorizonLabelRatio);
                    if (labelFont is { Available: true })
                    {
                        using (labelFont.Push())
                        {
                            labelWidth = ImGui.CalcTextSize(label).X;
                        }
                    }
                    else
                    {
                        labelFont = null;
                    }
                }

                var fitsLabel = labelFont != null
                    && numberWidth + gap + labelWidth <= maxWidth;
                if (!fitsLabel && numberWidth > maxWidth)
                {
                    continue;
                }

                this.PaintHorizonStat(
                    drawList, zoneLeft, zoneRight, bottom, rightAligned,
                    number, label, fitsLabel ? labelFont : null,
                    fitsLabel ? labelWidth : 0.0f, numberWidth, gap, color, self);
                return;
            }
        }

        // Fall back to truncating at the current strip font size.
        var trimmed = Elide(number, maxWidth);
        this.PaintHorizonStat(
            drawList, zoneLeft, zoneRight, bottom, rightAligned,
            trimmed, string.Empty, null, 0.0f, ImGui.CalcTextSize(trimmed).X,
            gap, color, self);
    }

    /// <summary>Use the current number font and align the unit label to its bottom
    /// edge.</summary>
    private void PaintHorizonStat(
        ImDrawListPtr drawList, float zoneLeft, float zoneRight, float bottom,
        bool rightAligned, string number, string label, IFontHandle? labelFont,
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

        if (labelFont is not { Available: true } || string.IsNullOrEmpty(label))
        {
            return;
        }

        using (labelFont.Push())
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

    /// <summary>Draw a caption aligned to the right and return its bottom edge for
    /// layout.</summary>
    private float DrawSmallText(ImDrawListPtr drawList, string text, float right, float top, Vector4 color)
    {
        var small = this.Fonts.Get(this.TextPx * this.HorizonPercentScale());
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

        // Reserve the target font height while it loads to keep cell height stable.
        return top + (this.TextPx * this.HorizonPercentScale());
    }

    /// <summary>Local colour takes priority in both themes. Apply configured opacity to
    /// role colours and use the dim colour for unknown jobs or the black &amp; white
    /// theme.</summary>
    private Vector4 HorizonBarColor(DpsRow row)
    {
        if (row.IsSelf)
        {
            return this.Config.DpsHorizSelfColor;
        }

        if (this.Config.DpsHorizTheme == HorizonColorTheme.BlackWhite)
        {
            return this.Config.DpsHorizDimColor;
        }

        var opacity = Math.Clamp(this.Config.DpsHorizBarOpacity, 0.05f, 1.0f);
        return JobColors.RoleOf(row.Job) switch
        {
            JobRole.Tank => WithAlpha(this.Config.DpsHorizTankColor, opacity),
            JobRole.Healer => WithAlpha(this.Config.DpsHorizHealerColor, opacity),
            JobRole.Dps => WithAlpha(this.Config.DpsHorizDpsColor, opacity),
            _ => this.Config.DpsHorizDimColor,
        };
    }

    private static Vector4 HorizonStripColor(Vector4 barColor, bool self, bool foreground)
    {
        var alpha = foreground ? (self ? 1.00f : 0.70f) : (self ? 0.50f : 0.30f);
        return new Vector4(barColor.X, barColor.Y, barColor.Z, alpha);
    }

    /// <summary>Horizontal shift per pixel of bar height, derived from the skew
    /// angle.</summary>
    private float HorizonSkew()
        => (float)Math.Tan(Math.Clamp(this.Config.DpsHorizSkew, 0.0f, 45.0f) * Math.PI / 180.0);

    private float HorizonStatScale() => Math.Clamp(this.Config.DpsHorizStatScale, 0.4f, 1.5f);

    private float HorizonPercentScale() => Math.Clamp(this.Config.DpsHorizPercentScale, 0.4f, 1.5f);

    private string FormatRowDps(double dps)
    {
        if (this.Config.DpsHorizCompact)
        {
            return FormatDps(dps);
        }

        return Math.Clamp(this.Config.DpsHorizDecimals, 0, 2) switch
        {
            0 => dps.ToString("0", CultureInfo.InvariantCulture),
            1 => dps.ToString("0.0", CultureInfo.InvariantCulture),
            _ => dps.ToString("0.00", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Draw a parallelogram whose bottom edge is shifted left by k.</summary>
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

    private void DrawKagerouRow(int rank, int index, DpsRow row)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1.0f);
        var lineHeight = ImGui.GetTextLineHeight();

        var underline = Math.Clamp(this.Config.DpsBarHeight * 0.15f, 2.0f, 4.0f);

        // Draw stripes behind the text while preserving the underline colour.
        if (this.Config.DpsRowStripes && (index & 1) == 1)
        {
            var stripe = Math.Clamp(this.Config.DpsRowStripeOpacity, 0.0f, 0.5f);
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(width, lineHeight),
                ToColor(new Vector4(1.0f, 1.0f, 1.0f, stripe)));
        }

        var numbers = $"{this.FormatRowNumber(row.Dps)} · {FormatShare(row.Share)}";
        if (this.Config.DpsRowsShowHps && row.Hps > 0.0)
        {
            numbers += $" · {this.FormatRowNumber(row.Hps)} hps";
        }

        var numbersWidth = ImGui.CalcTextSize(numbers).X;
        var deaths = this.DeathsMarker(row);
        var deathsWidth = deaths == null ? 0.0f : ImGui.CalcTextSize(deaths).X;
        var leftBudget = origin.X +
            Math.Max(width - numbersWidth - (TextPadding * 2.0f) - deathsWidth, 1.0f);

        var x = origin.X;
        if (this.Config.DpsRowsShowRank)
        {
            var rankText = $"{rank}.";
            this.DrawStyledText(drawList, new Vector2(x, origin.Y), this.Config.DpsTextColor, rankText);
            x += ImGui.CalcTextSize(rankText + " ").X;
        }

        if (this.Config.DpsRowsShowIcons)
        {
            var icon = JobIcons.Get(row.Job);
            if (icon != null)
            {
                drawList.AddImage(
                    icon.Handle,
                    new Vector2(x, origin.Y),
                    new Vector2(x + lineHeight, origin.Y + lineHeight),
                    Vector2.Zero,
                    Vector2.One,
                    ToColor(new Vector4(1.0f, 1.0f, 1.0f, 1.0f)));
            }

            // Reserve missing icon space to keep rows aligned.
            x += lineHeight + 4.0f;
        }

        if (!string.IsNullOrWhiteSpace(row.Job))
        {
            this.DrawStyledText(drawList, new Vector2(x, origin.Y), JobColors.Get(row.Job), row.Job);
            x += ImGui.CalcTextSize($"{row.Job}  ").X;
        }

        var name = this.RowName(row);
        if (name.Length > 0)
        {
            var elided = Elide(name, Math.Max(leftBudget - x, 1.0f));
            this.DrawStyledText(drawList, new Vector2(x, origin.Y), this.Config.DpsTextColor, elided);
            x += ImGui.CalcTextSize(elided).X;
        }

        if (deaths != null)
        {
            this.DrawStyledText(drawList, new Vector2(x, origin.Y), DeathsColor, deaths);
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

    private static string FormatShare(double share)
        => share.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private string FormatRowNumber(double value)
        => this.Config.DpsRowsCompact
            ? FormatDps(value)
            : value.ToString("0.0", CultureInfo.InvariantCulture);

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
