using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class UiReviewTests
{
    internal static Task Run()
    {
        var available = ImGui.Available;
        try
        {
            LongCallouts();
            UnicodeEllipsis();
            CollapsedHeader();
            RevisedHistory();
            HorizonOpacity();
            LiteralHeaderValues();
            LMeterCustomHeader();
        }
        finally
        {
            ImGui.Available = available;
            ImGui.ClickLabel = null;
            ImGui.Popups.Clear();
            ImGui.Reset();
        }
        return Task.CompletedTask;
    }

    private static void LongCallouts()
    {
        foreach (var text in new[]
        {
            "ボスから離れてから南側の安全な場所に集合してください",
            new string('A', 37),
            string.Concat(Enumerable.Repeat("e\u0301👩‍💻", 12)),
        })
        foreach (var alignment in Enum.GetValues<TextAlign>())
        foreach (var ready in new[] { false, true })
        {
            var config = new Configuration
            {
                Locked = true, AlertsWrap = true, AlertsAlign = alignment,
                AlertsTextEffect = TextEffectStyle.Off, AlertsAnimate = false,
                AlertsAlarmFlash = false, AlertsAlarmScale = 2,
            };
            using var host = new BridgeHost(config);
            Call(host, "Apply", JsonSerializer.Serialize(new { c = "alert", text, sev = "alarm", ttl = 30 }));
            using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? pixels : null };
            var window = new AlertsWindow(config, host, fonts);
            ImGui.Reset();
            ImGui.Available = new Vector2(140, 900);
            window.Draw();
            var lines = ImGui.Commands.Where(command => command.Kind == "text").ToArray();
            Check(lines.Length > 1 && string.Concat(lines.Select(line => line.Text)) == text
                && lines.All(line => line.A.X >= 0 && line.B.X <= 140.01f)
                && lines.Zip(lines.Skip(1), (a, b) => a.B.Y <= b.A.Y).All(value => value),
                "Long Japanese words and Unicode callouts wrap without losing text or overlapping lines",
                new { text, alignment, ready, lines = lines.Select(line => line.Text) });
            var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
            var offset = 0;
            Check(lines.All(line => boundaries.Contains(offset += line.Text!.Length)),
                "Callout wrapping keeps combining marks and emoji sequences together");
        }
        var wrapped = (List<string>)typeof(AlertsWindow).GetMethod("WrapLines", System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { "Stay together then spread", 70f })!;
        Check(wrapped.SequenceEqual(new[] { "Stay", "together", "then", "spread" }),
            "Space separated callouts still wrap at word boundaries", wrapped);
        wrapped = (List<string>)typeof(AlertsWindow).GetMethod("WrapLines", System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { "👩‍💻👩‍💻", 1f })!;
        Check(wrapped.SequenceEqual(new[] { "👩‍💻", "👩‍💻" }),
            "A grapheme wider than the window still makes progress without splitting");
    }

    private static void CollapsedHeader()
    {
        var config = new Configuration { Locked = true, DpsStyle = DpsMeterStyle.Kagerou };
        var meter = config.GetMeter(DpsMeterStyle.Kagerou);
        meter.DpsTextEffect = TextEffectStyle.Off;
        using var host = new BridgeHost(config);
        Call(host, "Apply", Dps());
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts, meter);
        ImGui.Available = new Vector2(600, 400);
        ImGui.Reset();
        ImGui.ClickLabel = "##kagerouCollapse";
        window.Draw();
        ImGui.Reset();
        window.Draw();
        Check(!ImGui.Commands.Any(command => command.Text == "Player"), "Collapsing Kagerou hides its rows");
        using var ui = new PluginUi(config, host, new ScaledFonts());
        var settings = (ConfigWindow)Field(ui, "configWindow");
        ImGui.ClickLabel = "Kagerou/Encounter line";
        settings.Draw();
        ImGui.Reset();
        window.Draw();
        Check(!meter.DpsShowHeader && ImGui.Commands.Any(command => command.Text == "Player"),
            "Disabling a collapsed Kagerou header restores its table");
        ImGui.ClickLabel = "Kagerou/Encounter line";
        settings.Draw();
        ImGui.Reset();
        window.Draw();
        Check(ImGui.Commands.Any(command => command.Text == "v")
            && !ImGui.Commands.Any(command => command.Text == "Player"),
            "Restoring the Kagerou header preserves its collapse choice and expand control");
        ImGui.Popups.Clear();
    }

    private static void UnicodeEllipsis()
    {
        foreach (var text in new[] { "👩‍💻abc", "e\u0301abc", "A👩‍💻bc" })
        foreach (var width in new[] { 7f, 14f, 21f, 28f, 42f })
        {
            ImGui.Reset();
            var trimmed = (string)typeof(OverlayWindow).GetMethod("Elide", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { text, width })!;
            var prefixLength = trimmed.EndsWith('…') ? trimmed.Length - 1 : trimmed.Length;
            var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length);
            Check(boundaries.Contains(prefixLength) && ImGui.CalcTextSize(trimmed).X <= width,
                "Ellipsis keeps combining marks and emoji sequences intact within the text width",
                new { text, width, trimmed });
        }
    }

    private static void HorizonOpacity()
    {
        var config = new Configuration();
        var meter = config.GetMeter(DpsMeterStyle.HorizonOverlay);
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts, meter);
        foreach (var opacity in new[] { 0f, .005f, .3f, 1f })
        foreach (var job in new[] { "WAR", "WHM", "MCH" })
        {
            meter.DpsHorizBarOpacity = opacity;
            var color = (Vector4)Call(window, "HorizonBarColor", new DpsRow("Other", job, 1, 100, 0, false, 0))!;
            Check(Math.Abs(color.W - opacity) < .0001f,
                "Horizon role bars respect the full opacity slider range", new { opacity, job, alpha = color.W });
        }
    }

    private static void RevisedHistory()
    {
        var config = new Configuration { Locked = true };
        var meter = config.GetMeter(DpsMeterStyle.Kagerou);
        meter.DpsTextEffect = TextEffectStyle.Off;
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts, meter);
        void Finish(string id, string title, double dps) => Call(host, "Apply", JsonSerializer.Serialize(new
        {
            c = "dps", show = false, enc = new { id, t = title, d = "00:10", dps },
            rows = new object[][] { new object[] { "Player", "MCH", dps, 100, 0, true, 0 } },
        }));
        Finish("A", "First finish", 1000);
        ImGui.Popups.Clear();
        ImGui.Reset();
        ImGui.ClickLabel = "##kagerouHistory";
        window.Draw();
        ImGui.ClickLabel = "00:10  First finish##history0";
        ImGui.Reset();
        window.Draw();
        ImGui.Popups.Clear();
        Finish("B", "Second finish", 3000);
        ImGui.Reset();
        window.Draw();
        Check(ImGui.Commands.Any(command => command.Text == "First finish"),
            "Selecting Kagerou history keeps its encounter when another fight finishes");
        Finish("A", "Updated finish", 2000);
        ImGui.Reset();
        window.Draw();
        Check(ImGui.Commands.Any(command => command.Text == "Updated finish")
            && ImGui.Commands.Any(command => command.Text == "2000.00")
            && !ImGui.Commands.Any(command => command.Text == "First finish"),
            "Revised final snapshots refresh the selected Kagerou history encounter");
    }

    private static void LiteralHeaderValues()
    {
        var config = new Configuration { DpsHeaderFormat = " | {TITLE} | {DURATION} | {DPS} | " };
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts);
        foreach (var title in new[] { "-Phase -- 2-", "| Arena / / Boss |", "{duration}  {dps}" })
        foreach (var duration in new[] { false, true })
        foreach (var dps in new[] { false, true })
        {
            config.DpsHeaderDuration = duration;
            config.DpsHeaderTotalDps = dps;
            const string Time = "00:10 / /";
            var formatted = (string)Call(window, "FormatHeaderLine", title, Time, 1000d)!;
            var expected = string.Join(" | ", new[] { title, duration ? Time : null, dps ? "1.0k" : null }
                .Where(part => part != null));
            Check(formatted == expected, "Header cleanup preserves literal token values and omits disabled fields", formatted);
        }
    }

    private static void LMeterCustomHeader()
    {
        var config = new Configuration { Locked = true };
        var meter = config.GetMeter(DpsMeterStyle.LMeter);
        meter.DpsHeaderFormat = "{title} | {duration} | {dps}";
        meter.DpsTextEffect = TextEffectStyle.Off;
        meter.DpsLMeterHeaderTitle = false;
        using var host = new BridgeHost(config);
        Call(host, "Apply", Dps("Encounter title"));
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts, meter);
        ImGui.Available = new Vector2(600, 400);
        ImGui.Reset();
        window.Draw();
        Check(ImGui.Commands.Any(command => command.Text == "00:10 | 1.0k")
            && !ImGui.Commands.Any(command => command.Text?.Contains("Encounter title") == true),
            "LMeter custom headers respect the encounter name checkbox");
        meter.DpsLMeterHeaderTitle = true;
        ImGui.Reset();
        window.Draw();
        Check(ImGui.Commands.Any(command => command.Text == "Encounter title | 00:10 | 1.0k"),
            "LMeter custom headers restore the title when enabled");
    }
}
