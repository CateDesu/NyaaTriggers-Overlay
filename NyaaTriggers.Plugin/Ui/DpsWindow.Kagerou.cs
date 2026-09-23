using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;

namespace NyaaTriggers.Plugin.Ui;

internal sealed partial class DpsWindow
{
    private static readonly Vector4 KagerouAccent = new(0.15f, 0.78f, 0.85f, 1);
    private static readonly Vector4 KagerouMuted = new(0.6f, 0.6f, 0.6f, 1);
    private static readonly Vector4 KagerouPanel = new(0.10f, 0.10f, 0.10f, 0.9f);
    private static readonly string[] KagerouTabs = { "DPS", "Tank", "Heal", "24" };
    private DpsState? kagerouHistory;
    private bool kagerouCollapsed;

    private readonly record struct KagerouColumn(
        string Label, float Width, Func<DpsRow, double?> Read, string Format = "rate");

    private static DpsState KagerouSample()
    {
        var rows = SampleRows.Select((row, i) => row with
        {
            Share = row.Dps / SampleRows.Sum(item => item.Dps) * 100,
            Stats = new CombatStats
            {
                Damage = row.Dps * 192, Healed = row.Hps * 192,
                HealShare = row.Hps / SampleRows.Sum(item => item.Hps) * 100,
                Crit = 24.5 + i, Direct = 18.2 + i, CritDirect = 5.4 + i,
                Taken = 45000 + i * 19000, HealingTaken = 49000 + i * 17000,
                Heals = i * 12, Hits = 150 + i * 15,
            },
        }).ToArray();
        return new DpsState
        {
            Title = "Sample Encounter", Zone = "Training arena", Duration = "03:12",
            EncDps = rows.Sum(row => row.Dps), EncHps = rows.Sum(row => row.Hps),
            Participants = rows.Length, Rows = rows,
        };
    }

    private void DrawKagerou(DpsState live)
    {
        var state = this.kagerouHistory ?? live;
        if (state.Rows.Count == 0 && !this.Config.Locked) state = KagerouSample();
        if (state.Rows.Count == 0) return;

        var tab = this.Meter.DpsKagerouTab;
        var sorted = state.Rows.OrderByDescending(row => KagerouValue(row, tab))
            .Select((row, i) => row with
            {
                Rank = (tab is KagerouTab.Dps or KagerouTab.Alliance) && row.Rank > 0 ? row.Rank : i + 1,
            }).ToArray();
        var rows = this.FilterRows(sorted);
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var line = ImGui.GetTextLineHeight();
        if (this.Meter.DpsShowHeader) this.DrawKagerouHeader(state, width, line);
        if (!this.kagerouCollapsed)
        {
            if (tab == KagerouTab.Alliance)
            {
                this.DrawKagerouAlliance(rows, width, line);
            }
            else
            {
                this.DrawKagerouTable(rows, width, line, sorted.Length > 0 ? KagerouValue(sorted[0], tab) : 0);
            }
        }

        var self = Array.FindIndex(sorted, row => row.IsSelf);
        this.DrawKagerouFooter(state, self >= 0 ? sorted[self].Rank : 0, width, line);
    }

    private static double KagerouValue(DpsRow row, KagerouTab tab) => tab switch
    {
        KagerouTab.Tank => row.Stats?.Taken ?? 0,
        KagerouTab.Heal => row.Hps,
        _ => row.Dps,
    };

    private bool KagerouButton(string id, string label, Vector2 origin, Vector2 size,
                               bool selected = false, string? tooltip = null)
    {
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(origin);
        var clicked = ImGui.InvisibleButton(id, size);
        var draw = ImGui.GetWindowDrawList();
        if (selected || ImGui.IsItemHovered())
            draw.AddRectFilled(origin, origin + size, this.ToColor(new Vector4(0, 0, 0, .4f)));
        var text = Elide(label, Math.Max(1, size.X - 4));
        var textSize = ImGui.CalcTextSize(text);
        this.DrawStyledText(draw, origin + (size - textSize) * .5f,
                            selected ? KagerouAccent : this.Meter.DpsTextColor, text);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.SetCursorScreenPos(cursor);
        return clicked;
    }

    private void DrawKagerouHeader(DpsState state, float width, float line)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var height = line * 2 + 4;
        var button = Math.Min(line * 1.6f, width / 5);
        draw.AddRectFilled(origin, origin + new Vector2(width, height), this.ToColor(KagerouPanel));
        if (this.KagerouButton("##kagerouHistory", "", origin, new Vector2(button, height),
                               this.kagerouHistory != null, "Encounter history"))
            ImGui.OpenPopup("Kagerou history");
        var clock = origin + new Vector2(button * .5f, height * .5f);
        var radius = button * .28f;
        draw.AddCircle(clock, radius, this.ToColor(KagerouAccent));
        draw.AddLine(clock, clock + new Vector2(0, -radius * .6f), this.ToColor(KagerouAccent));
        draw.AddLine(clock, clock + new Vector2(radius * .5f, radius * .25f), this.ToColor(KagerouAccent));
        if (ImGui.BeginPopup("Kagerou history"))
        {
            if (ImGui.Selectable("Live encounter")) this.kagerouHistory = null;
            for (var i = 0; i < this.bridge.DpsHistory.Count; i++)
            {
                var encounter = this.bridge.DpsHistory[i];
                if (ImGui.Selectable($"{encounter.Duration}  {encounter.Title}##history{i}"))
                    this.kagerouHistory = encounter;
            }

            ImGui.EndPopup();
        }

        var textX = origin.X + button + 2;
        var textRight = origin.X + width - button * 2 - 4;
        var time = this.Meter.DpsHeaderDuration ? state.Duration : string.Empty;
        var timeWidth = Math.Min(ImGui.CalcTextSize(time + " ").X, Math.Max(0, textRight - textX));
        this.KagerouCell(time, new Vector2(textX, origin.Y + 1), timeWidth, line, KagerouAccent, TextAlign.Left);
        this.KagerouCell(state.Title, new Vector2(textX + timeWidth, origin.Y + 1),
                         Math.Max(1, textRight - textX - timeWidth), line, this.Meter.DpsTextColor, TextAlign.Left);
        this.KagerouCell(state.Zone, new Vector2(textX, origin.Y + line + 1),
                         Math.Max(1, textRight - textX), line, KagerouMuted, TextAlign.Left);
        if (this.KagerouButton("##kagerouCollapse", this.kagerouCollapsed ? "v" : "^",
                               origin + new Vector2(width - 2 * button, 0), new Vector2(button, height),
                               tooltip: "Collapse or expand the table"))
            this.kagerouCollapsed = !this.kagerouCollapsed;
        if (this.KagerouButton("##kagerouMenu", "", origin + new Vector2(width - button, 0),
                               new Vector2(button, height), tooltip: "Meter options"))
            ImGui.OpenPopup("Kagerou options");
        for (var i = -1; i <= 1; i++)
            draw.AddCircleFilled(origin + new Vector2(width - button * .5f, height * .5f + i * line * .35f),
                                 Math.Max(1, line * .075f), this.ToColor(KagerouMuted));
        if (ImGui.BeginPopup("Kagerou options"))
        {
            if (ImGui.Selectable("Return to live")) this.kagerouHistory = null;
            this.KagerouOption("Only show yourself", this.Meter.DpsSoloOnly, value => this.Meter.DpsSoloOnly = value);
            this.KagerouOption("Compact numbers", this.Meter.DpsKagerouCompact, value => this.Meter.DpsKagerouCompact = value);
            this.KagerouOption("Call yourself YOU", this.Meter.DpsSelfNameYou, value => this.Meter.DpsSelfNameYou = value);
            this.KagerouOption("Hide other names", this.Meter.DpsNamePrivacy == NamePrivacyStyle.Hidden,
                               value => this.Meter.DpsNamePrivacy = value ? NamePrivacyStyle.Hidden : NamePrivacyStyle.Shown);
            if (ImGui.CollapsingHeader("Column headings"))
                DrawKagerouHeadingOptions(this.Meter, this.Config.Save);
            ImGui.EndPopup();
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    private void KagerouOption(string label, bool value, Action<bool> save)
    {
        if (!ImGui.Checkbox(label, ref value)) return;
        save(value);
        this.Config.Save();
    }

    private List<KagerouColumn> KagerouColumns()
    {
        var columns = new List<KagerouColumn>();
        switch (this.Meter.DpsKagerouTab)
        {
            case KagerouTab.Tank:
                columns.Add(new("DPS", 5, row => row.Dps));
                columns.Add(new("Taken", 5, row => row.Stats?.Taken, "total"));
                columns.Add(new("HealR", 5, row => row.Stats?.HealingTaken, "total"));
                columns.Add(new("Dead", 2.5f, row => row.Deaths, "count"));
                break;
            case KagerouTab.Heal:
                columns.Add(new("H%", 3, row => row.Stats?.HealShare, "share"));
                columns.Add(new("HPS", 5, row => row.Hps));
                columns.Add(new("Healed", 5, row => row.Stats?.Healed, "total"));
                columns.Add(new("OH%", 3, row => row.Stats?.Overheal, "share"));
                columns.Add(new("Heals", 3, row => row.Stats?.Heals, "count"));
                break;
            default:
                if (this.Meter.DpsKagerouDeaths) columns.Add(new("Dead", 2.5f, row => row.Deaths, "count"));
                columns.Add(new("DPS", 5.5f, row => row.Dps));
                columns.Add(new("D%", 3, row => row.Share, "share"));
                if (this.Meter.DpsKagerouHealingShare) columns.Add(new("H%", 3, row => row.Stats?.HealShare, "share"));
                if (this.Meter.DpsKagerouCrit) columns.Add(new("Crit", 3.5f, row => row.Stats?.Crit, "percent"));
                if (this.Meter.DpsKagerouDirect) columns.Add(new("DH", 3.5f, row => row.Stats?.Direct, "percent"));
                if (this.Meter.DpsKagerouCritDirect) columns.Add(new("CDH", 3.5f, row => row.Stats?.CritDirect, "percent"));
                break;
        }

        return columns;
    }

    internal static void DrawKagerouHeadingOptions(MeterSettings meter, Action save)
    {
        var changed = false;
        void Heading(string label, bool value, Action<bool> set)
        {
            if (!ImGui.Checkbox(label, ref value)) return;
            set(value);
            changed = true;
        }

        Heading("Show column headings", meter.DpsKagerouShowHeadings, value => meter.DpsKagerouShowHeadings = value);
        var deathHeading = (int)meter.DpsKagerouDeathHeading;
        var deathNames = new[] { "Dead", "Deaths", "D", "No letters" };
        if (ImGui.Combo("Deaths heading", ref deathHeading, deathNames, deathNames.Length))
        {
            meter.DpsKagerouDeathHeading = (KagerouDeathHeading)deathHeading;
            changed = true;
        }
        var alignment = (int)meter.DpsKagerouDeathsAlign;
        var alignments = new[] { "Left", "Center", "Right" };
        if (ImGui.Combo("Deaths alignment", ref alignment, alignments, alignments.Length))
        {
            meter.DpsKagerouDeathsAlign = (TextAlign)alignment;
            changed = true;
        }
        Heading("Name heading", meter.DpsKagerouNameHeading, value => meter.DpsKagerouNameHeading = value);
        Heading("DPS heading", meter.DpsKagerouDpsHeading, value => meter.DpsKagerouDpsHeading = value);
        Heading("D% heading", meter.DpsKagerouDamageShareHeading, value => meter.DpsKagerouDamageShareHeading = value);
        Heading("H% heading", meter.DpsKagerouHealShareHeading, value => meter.DpsKagerouHealShareHeading = value);
        Heading("Crit heading", meter.DpsKagerouCritHeading, value => meter.DpsKagerouCritHeading = value);
        if (changed) save();
    }

    private string KagerouHeading(string label)
    {
        if (!this.Meter.DpsKagerouShowHeadings) return string.Empty;
        return label switch
        {
            "Dead" => this.Meter.DpsKagerouDeathHeading switch
            {
                KagerouDeathHeading.Deaths => "Deaths",
                KagerouDeathHeading.D => "D",
                KagerouDeathHeading.Hidden => string.Empty,
                _ => "Dead",
            },
            "Name" when !this.Meter.DpsKagerouNameHeading => string.Empty,
            "DPS" when !this.Meter.DpsKagerouDpsHeading => string.Empty,
            "D%" when !this.Meter.DpsKagerouDamageShareHeading => string.Empty,
            "H%" when !this.Meter.DpsKagerouHealShareHeading => string.Empty,
            "Crit" when !this.Meter.DpsKagerouCritHeading => string.Empty,
            _ => label,
        };
    }

    private string KagerouNumber(double? value, string format)
    {
        if (value is not double number) return "—";
        if (format == "share") return number.ToString("0", CultureInfo.InvariantCulture) + "%";
        if (format == "percent") return number.ToString("0.0", CultureInfo.InvariantCulture);
        if (format == "count") return number.ToString("0", CultureInfo.InvariantCulture);
        if (this.Meter.DpsKagerouCompact || format == "total") return FormatDps(number);
        return number.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void KagerouCell(string text, Vector2 origin, float width, float height, Vector4 color, TextAlign alignment = TextAlign.Right)
    {
        if (width <= 1) return;
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + new Vector2(width, height), true);
        try
        {
            var clipped = Elide(text, Math.Max(1, width - 4));
            var textWidth = ImGui.CalcTextSize(clipped).X;
            var x = alignment switch
            {
                TextAlign.Center => Math.Max(2, (width - textWidth) * .5f),
                TextAlign.Right => Math.Max(2, width - textWidth - 2),
                _ => 2,
            };
            this.DrawStyledText(draw, origin + new Vector2(x, 0), color, clipped);
        }
        finally
        {
            draw.PopClipRect();
        }
    }

    private void KagerouName(DpsRow row, Vector2 origin, float width, float line)
    {
        var iconWidth = this.Meter.DpsKagerouIcons ? Math.Min(line + 3, width * .3f) : 0;
        var name = this.RowName(row);
        if (this.Meter.DpsKagerouRank) name = $"{row.Rank}. {name}";
        this.KagerouCell(name, origin, width - iconWidth, line,
                         row.IsSelf ? KagerouAccent : this.Meter.DpsTextColor, TextAlign.Left);
        if (iconWidth <= 0) return;
        var icon = JobIcons.Get(row.Job);
        if (icon != null)
        {
            var size = Math.Min(line, iconWidth);
            var at = origin + new Vector2(width - iconWidth, 0);
            ImGui.GetWindowDrawList().AddImage(icon.Handle, at, at + new Vector2(size),
                                              Vector2.Zero, Vector2.One, this.ToColor(Vector4.One));
        }
        else
        {
            using (this.UseFont(this.TextPx * .65f))
                this.KagerouCell(row.Job, origin + new Vector2(width - iconWidth, 2), iconWidth, line,
                                 JobColors.Get(row.Job));
        }
    }

    private void DrawKagerouTable(IReadOnlyList<DpsRow> rows, float width, float line, double maximum)
    {
        var columns = this.KagerouColumns();
        var unit = ImGui.CalcTextSize("0").X;
        var widths = columns.Select(column => Math.Max(column.Width * unit,
            Math.Max(ImGui.CalcTextSize(this.KagerouHeading(column.Label)).X,
                rows.Count > 0 ? rows.Max(row => ImGui.CalcTextSize(this.KagerouNumber(column.Read(row), column.Format)).X) : 0)) + 4).ToArray();
        var desired = widths.Sum();
        var available = Math.Min(Math.Max(desired, width * .56f), width * .70f);
        var nameWidth = width - available;
        var deathsFirst = columns[0].Format == "count";
        var compactFirst = deathsFirst && available > desired;
        var fixedWidth = compactFirst ? widths[0] : 0;
        var factor = desired > fixedWidth ? (available - fixedWidth) / (desired - fixedWidth) : 1;
        for (var c = 0; c < columns.Count; c++)
            if (c != 0 || !compactFirst) widths[c] *= factor;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var x = nameWidth;
        if (this.KagerouHeading("Name").Length > 0 || columns.Any(column => this.KagerouHeading(column.Label).Length > 0))
        {
            draw.AddRectFilled(origin, origin + new Vector2(width, line + 2), this.ToColor(new Vector4(0, 0, 0, .65f)));
            this.KagerouCell(this.KagerouHeading("Name"), origin, nameWidth, line, this.Meter.DpsTextColor, TextAlign.Left);
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = widths[c];
                this.KagerouCell(this.KagerouHeading(columns[c].Label), origin + new Vector2(x, 0), cell, line,
                                 this.Meter.DpsTextColor, alignment: columns[c].Label == "Dead" ? this.Meter.DpsKagerouDeathsAlign : TextAlign.Right);
                x += cell;
            }
            ImGui.Dummy(new Vector2(width, line + 2));
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            origin = ImGui.GetCursorScreenPos();
            var alpha = this.Meter.DpsRowStripes && (i & 1) == 1 ? this.Meter.DpsRowStripeOpacity : 0;
            draw.AddRectFilled(origin, origin + new Vector2(width, line + 2),
                               this.ToColor(new Vector4(alpha, alpha, alpha, .35f)));
            this.KagerouName(row, origin, nameWidth, line);
            x = nameWidth;
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                var cell = widths[c];
                this.KagerouCell(this.KagerouNumber(column.Read(row), column.Format),
                                 origin + new Vector2(x, 0), cell, line,
                                 this.Meter.DpsTextColor, alignment: columns[c].Label == "Dead" ? this.Meter.DpsKagerouDeathsAlign : TextAlign.Right);
                x += cell;
            }

            var fill = maximum > 0 ? Math.Clamp(KagerouValue(row, this.Meter.DpsKagerouTab) / maximum, 0, 1) : 0;
            if (fill > 0)
                draw.AddRectFilled(origin + new Vector2(0, line), origin + new Vector2(width * (float)fill, line + 2),
                                   this.ToColor(WithAlpha(JobColors.Get(row.Job), .7f)));
            ImGui.Dummy(new Vector2(width, line + 2));
        }
    }

    private void DrawKagerouAlliance(IReadOnlyList<DpsRow> rows, float width, float line)
    {
        using var font = this.UseFont(this.TextPx * .75f);
        line = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorScreenPos();
        var columns = width >= line * 24 ? 3 : width >= line * 14 ? 2 : 1;
        var cellWidth = width / columns;
        var height = line + 3;
        var maximum = rows.Count > 0 ? rows.Max(row => row.Dps) : 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var at = origin + new Vector2(i % columns * cellWidth, i / columns * height);
            var statWidth = Math.Min(cellWidth * .45f, line * 4);
            this.KagerouName(row, at, cellWidth - statWidth, line);
            this.KagerouCell(this.KagerouNumber(row.Dps, "rate"), at + new Vector2(cellWidth - statWidth, 0),
                             statWidth, line, this.Meter.DpsTextColor);
            if (maximum > 0)
                ImGui.GetWindowDrawList().AddRectFilled(at + new Vector2(0, line),
                    at + new Vector2((cellWidth - 3) * (float)(row.Dps / maximum), line + 2),
                    this.ToColor(WithAlpha(JobColors.Get(row.Job), .7f)));
        }

        ImGui.Dummy(new Vector2(width, MathF.Ceiling(rows.Count / (float)columns) * height));
    }

    private void DrawKagerouFooter(DpsState state, int selfRank, float width, float line)
    {
        var origin = ImGui.GetCursorScreenPos();
        var height = line + 8;
        var button = Math.Min(line * 3, width / 6);
        ImGui.GetWindowDrawList().AddRectFilled(origin, origin + new Vector2(width, height), this.ToColor(KagerouPanel));
        for (var i = 0; i < KagerouTabs.Length; i++)
        {
            if (!this.KagerouButton($"##kagerouTab{i}", KagerouTabs[i], origin + new Vector2(button * i, 0),
                                    new Vector2(button, height), i == (int)this.Meter.DpsKagerouTab)) continue;
            this.Meter.DpsKagerouTab = (KagerouTab)i;
            this.Config.Save();
        }

        var rank = selfRank > 0 ? selfRank.ToString(CultureInfo.InvariantCulture) : "—";
        var total = Math.Max(state.Participants, state.Rows.Count);
        var text = $"{rank}/{total}";
        if (this.Meter.DpsHeaderTotalDps)
        {
            var healing = this.Meter.DpsKagerouTab == KagerouTab.Heal;
            text += $"  {this.KagerouNumber(healing ? EncounterHps(state) : state.EncDps, "rate")}{(healing ? "hps" : "dps")}";
        }

        this.KagerouCell(text, origin + new Vector2(button * 4, 4), width - button * 4, line,
                         this.Meter.DpsTextColor);
        ImGui.Dummy(new Vector2(width, height));
    }
}
