using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

internal sealed partial class DpsWindow
{
    private static readonly DpsState LMeterSample = new()
    {
        Show = true, Title = "Preview", Duration = "00:30", EncDps = 30462, EncHps = 5688, Participants = 8,
        Rows = new DpsRow[]
        {
            new("Firstname Lastname", "GNB", 7041, 23.11, 777, true, 1),
            new("Firstname Lastname", "MNK", 6659, 21.86, 79, false, 0),
            new("Firstname Lastname", "DRK", 5124, 16.82, 723, false, 0),
            new("Firstname Lastname", "SMN", 4220, 13.85, 627, false, 1),
            new("Firstname Lastname", "SGE", 3926, 12.89, 586, false, 1),
            new("Firstname Lastname", "SGE", 1797, 5.90, 760, false, 1),
            new("Firstname Lastname", "MCH", 1598, 5.25, 903, false, 0),
            new("Firstname Lastname", "WHM", 97, .32, 1233, false, 1),
        },
    };

    private void DrawLMeter(DpsState state)
    {
        this.DrawLMeterHeader(state);
        var top = state.Rows.Count == 0 ? 0 : state.Rows.Max(row => row.Dps);
        var rows = this.FilterRows(state.Rows);
        for (var i = 0; i < rows.Count; i++) this.DrawLMeterRow(this.RankOf(i), i, rows[i], top);
    }

    private string LMeterNumber(double value) => this.Meter.DpsRowsCompact
        ? FormatDps(value) : value.ToString("N0", CultureInfo.InvariantCulture);

    private void DrawLMeterHeader(DpsState state)
    {
        if (!this.Meter.DpsShowHeader) return;
        if (!string.IsNullOrWhiteSpace(this.Meter.DpsHeaderFormat))
        {
            this.DrawHeader(state.Title, state.Duration, state.EncDps);
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1);
        var height = Math.Max(24 * ClampTextScale(this.TextScale), ImGui.GetTextLineHeight());
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + new Vector2(width, height), ToColor(this.Meter.DpsLMeterHeaderColor));
        var totals = new List<string>();
        if (this.Meter.DpsHeaderTotalDps) totals.Add($"{this.LMeterNumber(state.EncDps)}rdps");
        if (this.Meter.DpsLMeterHeaderHps)
        {
            var hps = EncounterHps(state) is { } totalHps ? this.LMeterNumber(totalHps) : "-";
            totals.Add($"{hps}rhps");
        }
        if (this.Meter.DpsLMeterHeaderDeaths)
        {
            var deaths = state.Participants > state.Rows.Count ? "-"
                : state.Rows.Sum(row => (long)row.Deaths).ToString(CultureInfo.InvariantCulture);
            totals.Add($"Deaths: {deaths}");
        }

        var right = string.Join(" ", totals);
        var padding = 3 * ClampTextScale(this.TextScale);
        var available = Math.Max(width - padding * 2, 0);
        var hasLeft = this.Meter.DpsHeaderDuration || this.Meter.DpsLMeterHeaderTitle;
        var rightWidth = Math.Min(ImGui.CalcTextSize(right).X, available * (hasLeft ? .62f : 1));
        this.LMeterText(right, origin + new Vector2(width - padding - rightWidth, 0), rightWidth,
            height, this.Meter.DpsLMeterTotalsColor, true, true);
        var leftWidth = Math.Max(available - rightWidth - (rightWidth > 0 ? padding : 0), 0);
        var left = origin + new Vector2(padding, 0);
        if (this.Meter.DpsHeaderDuration)
        {
            var duration = state.Duration.Replace('\r', ' ').Replace('\n', ' ');
            var durationWidth = Math.Min(ImGui.CalcTextSize(duration).X, leftWidth);
            this.LMeterText(duration, left, durationWidth, height, this.Meter.DpsLMeterDurationColor);
            left.X += durationWidth + padding;
            leftWidth = Math.Max(leftWidth - durationWidth - padding, 0);
        }
        if (this.Meter.DpsLMeterHeaderTitle)
            this.LMeterText(state.Title, left, leftWidth, height, this.Meter.DpsTextColor);
        ImGui.Dummy(new Vector2(width, height + this.Meter.DpsBarSpacing));
    }

    private void DrawLMeterRow(int rank, int index, DpsRow row, double top)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 1);
        var height = Math.Max(this.Meter.DpsBarHeight * ClampTextScale(this.TextScale), MathF.Ceiling(ImGui.GetTextLineHeight()));
        var rounding = Math.Min(this.Meter.DpsBarRounding, height * .5f);
        drawList.AddRectFilled(origin, origin + new Vector2(width, height),
            ToColor(WithAlpha(this.Meter.DpsBarTrackColor, this.Meter.DpsBarTrackOpacity)), rounding);
        var fillWidth = top > 0 ? width * (float)Math.Clamp(row.Dps / top, 0, 1) : 0;
        if (fillWidth > 0)
        {
            var fillOrigin = this.Meter.DpsBarRightToLeft ? origin + new Vector2(width - fillWidth, 0) : origin;
            var color = this.Meter.DpsBarSelfHighlight && row.IsSelf ? this.Meter.DpsBarSelfColor
                : this.Meter.DpsBarTopHighlight && rank == 1 ? this.Meter.DpsBarTopColor
                : this.Meter.DpsBarJobColors ? WithAlpha(LMeterJobColor(row.Job), this.Meter.DpsBarColor.W)
                : this.Meter.DpsBarColor;
            drawList.AddRectFilled(fillOrigin, fillOrigin + new Vector2(fillWidth, height), ToColor(color), rounding);
        }
        if (this.Meter.DpsRowStripes && (index & 1) == 1)
            drawList.AddRectFilled(origin, origin + new Vector2(width, height),
                ToColor(new Vector4(1, 1, 1, Math.Clamp(this.Meter.DpsRowStripeOpacity, 0, .5f))), rounding);
        if (this.Meter.DpsBarBorderThickness > 0)
            drawList.AddRect(origin, origin + new Vector2(width, height), ToColor(this.Meter.DpsBarBorderColor),
                rounding, ImDrawFlags.None, this.Meter.DpsBarBorderThickness);

        var padding = 3 * ClampTextScale(this.TextScale);
        var textLeft = padding;
        if (this.Meter.DpsRowsShowIcons)
        {
            var iconSize = Math.Min(height, width);
            var icon = JobIcons.Get(row.Job, lmeter: true);
            if (icon != null)
                drawList.AddImage(icon.Handle, origin, origin + new Vector2(iconSize),
                    Vector2.Zero, Vector2.One, ToColor(Vector4.One));
            textLeft = iconSize + padding;
        }
        var stats = new List<string>();
        if (this.Meter.DpsLMeterShowDps) stats.Add($"DPS:{this.LMeterNumber(row.Dps)}");
        if (this.Meter.DpsRowsShowHps) stats.Add($"HPS:{this.LMeterNumber(row.Hps)}");
        if (this.Meter.DpsShowDeaths) stats.Add($"Deaths:{row.Deaths}");
        if (this.Meter.DpsBarsShowShare) stats.Add(FormatShare(row.Share));
        var right = string.Join(" ", stats);
        var name = (this.Meter.DpsRowsShowRank ? $"{rank}  " : string.Empty) + this.RowName(row);
        var available = Math.Max(width - textLeft - padding, 0);
        var rightWidth = Math.Min(ImGui.CalcTextSize(right).X, available * (name.Length > 0 ? .70f : 1));
        this.LMeterText(right, origin + new Vector2(width - padding - rightWidth, 0),
            rightWidth, height, this.Meter.DpsTextColor, true, true);
        this.LMeterText(name, origin + new Vector2(textLeft, 0),
            Math.Max(available - rightWidth - (rightWidth > 0 ? padding : 0), 0), height, this.Meter.DpsTextColor);
        ImGui.Dummy(new Vector2(width, height + Math.Max(this.Meter.DpsBarSpacing, 0)));
    }

    private void LMeterText(string text, Vector2 origin, float width, float height, Vector4 color,
        bool right = false, bool fit = false)
    {
        if (width <= 0 || text.Length == 0) return;
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        var ratio = fit ? Math.Clamp(width / Math.Max(ImGui.CalcTextSize(text).X, 1), .65f, 1) : 1;
        using (this.UseFont(this.TextPx * ratio))
        {
            text = Elide(text, width);
            var size = ImGui.CalcTextSize(text);
            var position = origin + new Vector2(right ? Math.Max(width - size.X, 0) : 0, (height - size.Y) * .5f);
            var drawList = ImGui.GetWindowDrawList();
            drawList.PushClipRect(origin, origin + new Vector2(width, height), true);
            try { this.DrawStyledText(drawList, position, color, text); }
            finally { drawList.PopClipRect(); }
        }
    }

    private static Vector4 LMeterJobColor(string job) => job.ToUpperInvariant() switch
    {
        "SGE" => new(144 / 255f, 176 / 255f, 1, 1),
        "VPR" => new(16 / 255f, 130 / 255f, 16 / 255f, 1),
        "PCT" => new(252 / 255f, 146 / 255f, 225 / 255f, 1),
        "BLU" => new(0, 185 / 255f, 247 / 255f, 1),
        _ => JobColors.Get(job),
    };
}
