using System.Numerics;
using System.Text.Json;
using System.Text;
using System.Globalization;
using System.Security;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class KagerouTests
{
    internal static Task Run()
    {
        var config = new Configuration
        {
            DpsStyle = DpsMeterStyle.Kagerou, Locked = false, DpsTextEffect = TextEffectStyle.Off,
            DpsSelfNameYou = true,
        };
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts);
        var originalAvailable = ImGui.Available;
        try
        {
            ImGui.Available = new Vector2(600, 500);
            ImGui.Popups.Clear();
            Apply(host, "one");
            Draw(window);
            var text = Text();
            Check(new[] { "Name", "Dead", "DPS", "D%", "H%", "Crit", "Tank", "Heal", "24", "YOU" }
                .All(text.Contains), "Kagerou draws the screenshot columns and all four view controls", text);
            Check(text.Contains("25.0") && text.Contains("75%") && text.Any(value => value.Contains("1/2")),
                "Kagerou displays transmitted rates and the local rank", text);
            ImGui.ClickLabel = "##kagerouTab1";
            Draw(window);
            Draw(window);
            text = Text();
            Check(config.DpsKagerouTab == KagerouTab.Tank && text.Contains("Taken") && text.Contains("HealR"),
                "The Tank button changes the displayed statistics", text);
            var names = ImGui.Commands.Where(command => command.Kind == "text" && command.Text is "YOU" or "Healer")
                .OrderBy(command => command.A.Y).Select(command => command.Text).ToArray();
            Check(names.SequenceEqual(new[] { "Healer", "YOU" }), "Tank rows sort by damage received", names);
            ImGui.ClickLabel = "##kagerouTab2";
            Draw(window);
            Draw(window);
            Check(Text().Contains("HPS") && Text().Contains("OH%") && Text().Contains("—"),
                "Heal view shows unavailable overheal without inventing a value", Text());
            foreach (var width in new[] { 160, 320, 800 })
            {
                ImGui.Available = new Vector2(width, 600);
                Draw(window);
                Check(ImGui.ClipRects.All(rect => rect.Min.X >= 0 && rect.Max.X <= width + .01f
                      && rect.Max.X >= rect.Min.X && float.IsFinite(rect.Max.X)),
                    "Kagerou columns stay inside narrow and wide windows", width);
            }

            ImGui.Available = new Vector2(800, 600);
            Apply(host, "raid", count: 24);
            config.DpsKagerouTab = KagerouTab.Alliance;
            Draw(window);
            Check(Text().Count(value => value.StartsWith("Member")) == 23 && Text().Contains("YOU"),
                "The 24 view includes the full alliance with the usual eight-row setting", Text());
            Apply(host, "finished", ended: true);
            Apply(host, "finished", ended: true);
            Check(host.DpsHistory.Count == 1, "Repeated final snapshots update one history entry");
            Apply(host, "live");
            ImGui.ClickLabel = "##kagerouHistory";
            Draw(window);
            ImGui.ClickLabel = "00:10  Encounter finished##history0";
            Draw(window);
            Draw(window);
            Check(Text().Contains("Encounter finished"), "The history menu displays a completed encounter", Text());
            ImGui.ClickLabel = "Live encounter";
            Draw(window);
            Draw(window);
            Check(Text().Contains("Encounter live"), "History can return to the current encounter", Text());
            for (var i = 0; i < 25; i++) Apply(host, "ended" + i, ended: true);
            Check(host.DpsHistory.Count == 20 && host.DpsHistory[0].Id == "ended24",
                "History retains the most recent twenty encounters");
            config.Locked = true;
            window.PreDraw();
            Check((window.Flags & ImGuiWindowFlags.NoInputs) != 0, "Locked Kagerou keeps click-through by default");
            config.DpsKagerouInteractive = true;
            window.PreDraw();
            Check((window.Flags & ImGuiWindowFlags.NoInputs) == 0 && (window.Flags & ImGuiWindowFlags.NoMove) != 0,
                "Optional locked controls accept clicks while position stays locked");
            Apply(host, "legacy", legacy: true);
            config.DpsKagerouTab = KagerouTab.Dps;
            Draw(window);
            Check(Text().Contains("—") && host.Dps.Rows[0].Stats == null,
                "Legacy senders keep rows and show unavailable new statistics");
            config.DpsKagerouTab = KagerouTab.Heal;
            config.DpsSoloOnly = true;
            foreach (var participants in new[] { 2, 3 })
            {
                Call(host, "Apply", JsonSerializer.Serialize(new
                {
                    c = "dps", show = true, enc = new { dps = 2000, participants },
                    rows = new object[][]
                    {
                        new object[] { "Self", "MCH", 1500, 75, 250, true, 0 },
                        new object[] { "Healer", "WHM", 500, 25, 750, false, 0 },
                    },
                }));
                Draw(window);
                Check(Text().Any(value => value.EndsWith(participants == 2 ? "1000.00hps" : "—hps")),
                    "Legacy healing totals include the full roster or show unavailable when truncated", Text());
            }
            config.DpsSoloOnly = false;
            Apply(host, "authoritative");
            Draw(window);
            Check(Text().Any(value => value.EndsWith("1001.00hps")),
                "Transmitted encounter healing remains authoritative", Text());
            config.DpsKagerouTab = KagerouTab.Dps;
            Call(host, "ApplyLocalDps", new DpsState
            {
                Show = true, HasDamage = true, Participants = 300,
                Rows = new[]
                {
                    new DpsRow("Leader", "MCH", 2000, 50, 0, false, 0, 1),
                    new DpsRow("Self", "MCH", 1, .1, 0, true, 0, 300),
                },
            });
            Draw(window);
            Check(Text().Any(value => value.StartsWith("300/300")),
                "The footer preserves a supplied rank beyond the transmitted leaders", Text());
            Call(host, "Apply", """
                {"c":"dps","show":true,"enc":{"participants":"bad","dps":1},
                 "rows":[["Player","MCH",1,100,0,false,0,{"crit":"bad","taken":[],"healed":12}]]}
                """);
            Check(host.Dps.Rows.Count == 1 && host.Dps.Rows[0].Stats is { Crit: null, Taken: null, Healed: 12 },
                "Malformed optional metrics cannot discard otherwise valid rows");
            CombatStatistics();
            Preview();
        }
        finally
        {
            ImGui.Available = originalAvailable;
            ImGui.ClickLabel = null;
            ImGui.Popups.Clear();
        }

        return Task.CompletedTask;
    }

    private static void CombatStatistics()
    {
        var meter = new MeterEngine(() => 0);
        meter.SetMe(0x10000001);
        meter.NoteJob(0x10000001, 31);
        meter.NoteJob(0x10000002, 24);
        static string[] Ability(string source, string target, string flags, string amount)
        {
            var fields = new List<string> { "21", "ts", source, "Source", "7", "Ability", target, "Target", flags, amount };
            while (fields.Count < 24) fields.Add("");
            return fields.ToArray();
        }

        meter.Process(Ability("10000001", "40000010", "6003", "00640000"));
        meter.Process(Ability("10000001", "40000010", "0003", "00640000"));
        meter.Process(Ability("40000010", "10000001", "0003", "00320000"));
        meter.Process(Ability("10000002", "10000001", "0104", "004B0000"));
        var snapshot = meter.LiveSnapshot()!;
        var player = snapshot.Rows.Single(row => row.IsSelf).Stats!;
        var healer = snapshot.Rows.Single(row => row.Job == "WHM").Stats!;
        Check(player.Crit == 50 && player.Direct == 50 && player.CritDirect == 50
              && player.Taken == 50 && player.HealingTaken == 75,
            "Standalone damage flags and incoming totals reach the overlay");
        Check(healer.HealShare == 100 && healer.Heals == 1 && snapshot.EncHps == 75,
            "Standalone healing share and encounter HPS agree with the program feed");
        Check(healer.Overheal == null, "The raw meter does not claim to know overheal");
    }

    private static void Preview()
    {
        var folder = Environment.GetEnvironmentVariable("NYAA_KAGEROU_PREVIEW_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var config = new Configuration
        {
            DpsStyle = DpsMeterStyle.Kagerou, Locked = false, DpsTextEffect = TextEffectStyle.Off,
            DpsTextScale = .75f, DpsSelfNameYou = true,
        };
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts);
        ImGui.Popups.Clear();
        ImGui.Available = new Vector2(380, 500);
        foreach (var tab in Enum.GetValues<KagerouTab>())
        {
            config.DpsKagerouTab = tab;
            Draw(window);
            static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
            var svg = new StringBuilder($"<svg xmlns='http://www.w3.org/2000/svg' width='380' height='{Num(ImGui.Cursor.Y)}' viewBox='0 0 380 {Num(ImGui.Cursor.Y)}'>");
            svg.Append("<rect width='100%' height='100%' fill='#202329'/>");
            foreach (var command in ImGui.Commands)
            {
                var color = command.Color;
                var fill = $"#{color & 255:X2}{color >> 8 & 255:X2}{color >> 16 & 255:X2}";
                var alpha = Num((color >> 24) / 255f);
                var width = Num(command.B.X - command.A.X);
                var height = Num(command.B.Y - command.A.Y);
                switch (command.Kind)
                {
                    case "text" when !string.IsNullOrEmpty(command.Text):
                        svg.Append($"<text x='{Num(command.A.X)}' y='{Num(command.A.Y + command.FontSize * .82f)}' fill='{fill}' opacity='{alpha}' font-family='Arial,sans-serif' font-size='{Num(command.FontSize)}' textLength='{width}' lengthAdjust='spacingAndGlyphs'>{SecurityElement.Escape(command.Text)}</text>");
                        break;
                    case "rect":
                        svg.Append($"<rect x='{Num(command.A.X)}' y='{Num(command.A.Y)}' width='{width}' height='{height}' fill='{fill}' opacity='{alpha}'/>");
                        break;
                    case "line":
                        svg.Append($"<line x1='{Num(command.A.X)}' y1='{Num(command.A.Y)}' x2='{Num(command.B.X)}' y2='{Num(command.B.Y)}' stroke='{fill}' opacity='{alpha}'/>");
                        break;
                    case "circle":
                    case "disc":
                        var center = (command.A + command.B) * .5f;
                        svg.Append($"<circle cx='{Num(center.X)}' cy='{Num(center.Y)}' r='{Num((command.B.X - command.A.X) * .5f)}' stroke='{fill}' fill='{(command.Kind == "disc" ? fill : "none")}' opacity='{alpha}'/>");
                        break;
                }
            }

            svg.Append("</svg>");
            File.WriteAllText(Path.Combine(folder, $"kagerou-{tab.ToString().ToLowerInvariant()}.svg"), svg.ToString());
        }
    }

    private static void Draw(DpsWindow window)
    {
        ImGui.Reset();
        window.Draw();
    }

    private static string[] Text() => ImGui.Commands.Where(command => command.Kind == "text")
        .Select(command => command.Text!).Distinct().ToArray();

    private static void Apply(BridgeHost host, string id, bool ended = false, int count = 2, bool legacy = false)
    {
        var rows = Enumerable.Range(0, count).Select(i =>
        {
            var values = new List<object> { i == 0 ? "Self" : count == 2 ? "Healer" : $"Member{i:D2}",
                i == 0 ? "MCH" : "WHM", 2000 - i, 50, 500 + i, i == 0, i };
            if (!legacy) values.Add(new { crit = 25, healShare = 75, taken = i * 1000, healingTaken = 2000, healed = 5000 });
            return values;
        });
        Call(host, "Apply", JsonSerializer.Serialize(new
        {
            c = "dps", show = !ended,
            enc = new { id, t = "Encounter " + id, zone = "Arena", d = "00:10", dps = 4000, hps = 1001, participants = count }, rows,
        }));
    }
}
