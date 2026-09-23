using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class LMeterTests
{
    internal static Task Run()
    {
        var available = ImGui.Available;
        try
        {
            Migration();
            Rendering();
            Preview();
        }
        finally { ImGui.Available = available; JobIcons.Available = false; }
        return Task.CompletedTask;
    }

    private static void Migration()
    {
        var config = JsonSerializer.Deserialize<Configuration>("""
            {"Version":6,"DpsStyle":0,"BarsMeter":{"ShowDps":false,"DpsOnlyInDuty":true,"DpsHoldLast":false,
             "DpsPos":{"X":313,"Y":217},"DpsSize":{"X":555,"Y":333},"DpsBarHeight":31},
             "HorizonMeter":{"DpsRowsShowRank":true,"DpsBarSpacing":4},
             "KagerouMeter":{"DpsKagerouDeathsAlign":2}}
            """, new JsonSerializerOptions { IncludeFields = true })!;
        config.MigrateFromV6();
        var meter = config.GetMeter(DpsMeterStyle.LMeter);
        Check(config.Version == 7 && (int)meter.DpsStyle == 0 && !meter.ShowDps && meter.DpsOnlyInDuty
            && !meter.DpsHoldLast && meter.DpsPos == new Vector2(313, 217) && meter.DpsSize == new Vector2(555, 333),
            "LMeter replaces the saved Bars slot without changing placement or visibility");
        Check(meter.DpsBarHeight == 31 && meter.DpsBarSpacing == 1 && meter.DpsBarRounding == 0
            && meter.DpsBarJobColors && meter.DpsRowsShowIcons && !meter.DpsRowsShowRank
            && meter.DpsShowDeaths && meter.DpsRowsShowHps && !meter.DpsRowsCompact,
            "LMeter migration applies its appearance and preserves a custom row height");
        Check(config.HorizonMeter!.DpsRowsShowRank && config.HorizonMeter.DpsBarSpacing == 4
            && config.KagerouMeter!.DpsKagerouDeathsAlign == TextAlign.Right,
            "Replacing Bars leaves Horizon and Kagerou preferences intact");
        meter.DpsRowsShowHps = false;
        meter.DpsLMeterShowDps = false;
        meter.DpsLMeterHeaderDeaths = false;
        meter.DpsLMeterDurationColor = new Vector4(.2f, .3f, .4f, .5f);
        config.MigrateFromV6();
        Check(!meter.DpsRowsShowHps, "Repeated migration cannot reset LMeter choices");
        var target = new Configuration();
        var settings = target.GetMeter(DpsMeterStyle.LMeter);
        target.ApplyAppearanceProfile(config.SnapshotAppearance());
        Check(ReferenceEquals(settings, target.GetMeter(DpsMeterStyle.LMeter)) && !settings.DpsLMeterShowDps
            && !settings.DpsRowsShowHps && !settings.DpsLMeterHeaderDeaths
            && settings.DpsLMeterDurationColor == meter.DpsLMeterDurationColor,
            "LMeter appearance profiles retain metric toggles and header colors");
        target.ResetAppearance();
        Check(settings.DpsLMeterShowDps && settings.DpsRowsShowHps && settings.DpsShowDeaths
            && settings.DpsRowsShowIcons && !settings.DpsRowsShowRank && settings.DpsBarJobColors,
            "Reset restores the LMeter style in the existing window");
    }

    private static void Rendering()
    {
        var config = new Configuration { Locked = true };
        var meter = config.GetMeter(DpsMeterStyle.LMeter);
        meter.DpsTextEffect = TextEffectStyle.Off;
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts, meter);
        ImGui.Available = new Vector2(600, 400);
        JobIcons.Available = true;
        Apply(host);
        Draw(window);
        Check(Text().Contains("DPS:7,041 HPS:777 Deaths:1") && Text().Contains("DPS:3,520 HPS:0 Deaths:0"),
            "LMeter labels every row metric including zero healing and deaths");
        Check(Text().Contains("10,561rdps 777rhps Deaths: 1") && Text().Contains("00:30") && Text().Contains("Preview"),
            "LMeter header separates duration encounter name and full encounter totals");
        var duration = ImGui.Commands.Single(c => c.Text == "00:30");
        Check(duration.Color == ImGui.ColorConvertFloat4ToU32(meter.DpsLMeterDurationColor)
            && ImGui.Commands.Count(c => c.Kind == "image") == 2
            && Text().Contains("Leader") && !Text().Any(s => s.Contains("GNB")),
            "LMeter uses the cyan duration and icon beside each plain player name");
        var fills = Fills(meter);
        Check(fills.Length == 2 && fills[0].B.X - fills[0].A.X == 600
            && Math.Abs((fills[1].B.X - fills[1].A.X) / 600 - 3520d / 7041) < .0001,
            "LMeter bars scale to the highest DPS rather than damage share");
        meter.DpsSelfFirst = true;
        Draw(window);
        fills = Fills(meter);
        Check(fills[0].B.X - fills[0].A.X < 301 && fills[1].B.X - fills[1].A.X == 600,
            "Pinning yourself first does not change the top DPS scale");
        meter.DpsSoloOnly = true;
        Draw(window);
        Check(Fills(meter).Length == 1 && Fills(meter)[0].B.X - Fills(meter)[0].A.X < 301
            && Text().Contains("10,561rdps 777rhps Deaths: 1"),
            "Solo display retains encounter totals and the full party bar scale");
        meter.DpsBarRightToLeft = true;
        Draw(window);
        Check(Fills(meter)[0].B.X == 600 && Fills(meter)[0].A.X > 299,
            "LMeter right anchored bars end at the window edge");
        meter.DpsSoloOnly = false;
        meter.DpsLMeterShowDps = meter.DpsRowsShowHps = meter.DpsShowDeaths = false;
        Draw(window);
        Check(!Text().Any(s => s.Contains("DPS:") || s.Contains("HPS:") || s.Contains("Deaths:0") || s.Contains("Deaths:1")),
            "Row metric toggles hide only their labels and values");
        meter.DpsLMeterShowDps = meter.DpsRowsShowHps = meter.DpsShowDeaths = true;
        meter.DpsHeaderDuration = meter.DpsLMeterHeaderTitle = meter.DpsHeaderTotalDps = false;
        Draw(window);
        Check(!Text().Contains("00:30") && !Text().Contains("Preview") && !Text().Any(s => s.Contains("rdps"))
            && Text().Contains("777rhps Deaths: 1"), "LMeter header sections can be hidden independently");
        meter.DpsHeaderDuration = meter.DpsLMeterHeaderTitle = meter.DpsHeaderTotalDps = true;
        Apply(host, participants: 24, hps: 0);
        Draw(window);
        Check(Text().Contains("10,561rdps -rhps Deaths: -"),
            "Incomplete party data does not invent encounter healing or death totals");
        Apply(host, hps: 0);
        Draw(window);
        Check(Text().Contains("10,561rdps 777rhps Deaths: 1"),
            "Older feeds recover total healing from a complete roster");
        Apply(host, zero: true);
        Draw(window);
        Check(Fills(meter).Length == 0 && Text().Contains("DPS:0 HPS:777 Deaths:1")
            && ImGui.Commands.All(c => float.IsFinite(c.A.X) && float.IsFinite(c.B.X)),
            "Healing only encounters draw zero DPS without a division by zero");
        Apply(host);
        foreach (var width in new[] { 40f, 160f, 320f, 600f })
        foreach (var scale in new[] { .5f, 1f, 6f })
        {
            ImGui.Available = new Vector2(width, 1000);
            meter.DpsTextScale = scale;
            Draw(window);
            Check(ImGui.Commands.All(c => float.IsFinite(c.A.X) && float.IsFinite(c.B.Y))
                && ImGui.ClipRects.All(c => c.Min.X >= 0 && c.Max.X <= width && c.Max.X >= c.Min.X)
                && ImGui.FontScale == 1,
                "Narrow and scaled LMeter text stays clipped to its own space", new { width, scale });
        }
        ImGui.Available = new Vector2(600, 400);
        meter.DpsTextScale = 1;
        JobIcons.Available = false;
        Draw(window);
        var nameX = ImGui.Commands.Single(c => c.Text == "Leader").A.X;
        JobIcons.Available = true;
        Draw(window);
        Check(ImGui.Commands.Single(c => c.Text == "Leader").A.X == nameX,
            "Loading job textures does not shift LMeter names");
    }

    private static DrawCommand[] Fills(MeterSettings meter) => ImGui.Commands
        .Where(c => c.Kind == "rect" && c.Color != ImGui.ColorConvertFloat4ToU32(meter.DpsLMeterHeaderColor)
            && c.Color >> 24 != 0).ToArray();

    private static string[] Text() => ImGui.Commands.Where(c => c.Kind == "text").Select(c => c.Text!).ToArray();
    private static void Draw(DpsWindow window) { ImGui.Reset(); window.Draw(); }

    private static void Apply(BridgeHost host, int participants = 2, double hps = 777, bool zero = false)
        => Call(host, "Apply", JsonSerializer.Serialize(new
        {
            c = "dps", show = true, hasDamage = !zero,
            enc = new { id = "lmeter", t = "Preview", d = "00:30", dps = zero ? 0 : 10561, hps, participants },
            rows = new object[][] {
                new object[] { "Leader", "GNB", zero ? 0 : 7041, 66.67, 777, false, 1 },
                new object[] { "Player", "MNK", zero ? 0 : 3520, 33.33, 0, true, 0 },
            },
        }));

    private static void Preview()
    {
        var folder = Environment.GetEnvironmentVariable("NYAA_LMETER_PREVIEW_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var config = new Configuration();
        var meter = config.GetMeter(DpsMeterStyle.LMeter);
        meter.DpsTextEffect = TextEffectStyle.Outline;
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        var window = new DpsWindow(config, host, fonts, meter);
        ImGui.Available = new Vector2(403, 500);
        JobIcons.Available = true;
        Draw(window);
        static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var svg = new StringBuilder($"<svg xmlns='http://www.w3.org/2000/svg' width='403' height='{Num(ImGui.Cursor.Y)}' viewBox='0 0 403 {Num(ImGui.Cursor.Y)}'>");
        svg.Append("<rect width='100%' height='100%' fill='#363735'/>");
        var jobs = new[] { "GNB", "MNK", "DRK", "SMN", "SGE", "SGE", "MCH", "WHM" };
        var icon = 0;
        foreach (var command in ImGui.Commands)
        {
            var color = command.Color;
            var fill = $"#{color & 255:X2}{color >> 8 & 255:X2}{color >> 16 & 255:X2}";
            var alpha = Num((color >> 24) / 255f);
            var width = Num(command.B.X - command.A.X);
            var height = Num(command.B.Y - command.A.Y);
            if (command.Kind == "rect")
                svg.Append($"<rect x='{Num(command.A.X)}' y='{Num(command.A.Y)}' width='{width}' height='{height}' fill='{fill}' opacity='{alpha}'/>");
            else if (command.Kind == "text")
                svg.Append($"<text x='{Num(command.A.X)}' y='{Num(command.A.Y + command.FontSize * .82f)}' fill='{fill}' opacity='{alpha}' font-family='Arial,sans-serif' font-size='{Num(command.FontSize)}' textLength='{width}' lengthAdjust='spacingAndGlyphs'>{SecurityElement.Escape(command.Text)}</text>");
            else if (command.Kind == "image")
                svg.Append($"<text x='{Num(command.A.X + 2)}' y='{Num(command.A.Y + 16)}' fill='#fff0bd' font-family='Arial,sans-serif' font-size='9'>{jobs[icon++]}</text>");
        }
        svg.Append("</svg>");
        File.WriteAllText(Path.Combine(folder, "lmeter.svg"), svg.ToString());
    }
}
