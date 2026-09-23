using System.Numerics;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class DisplayAndFeedTests
{
    internal static async Task Run()
    {
        MovedBars();
        ManualSettings();
        HorizonLayout();
        CurrentJobs();
        ReflectedDamage();
        await ZoneBoundaries();
        await ReconnectIdentity();
    }

    internal static async Task Boundaries()
    {
        ReflectionBoundaries();
        JobCachePressure();
        LayoutBoundaries();
        await PartialZoneMetadata();
        await SameZoneReconnect();
    }

    private static void ReflectionBoundaries()
    {
        foreach (var pet in new[] { false, true })
        foreach (var incoming in new[] { false, true })
        {
            var engine = new MeterEngine(() => 0);
            engine.SetMe(0x10000001);
            engine.Process(new[] { "02", "ts", "10000001", "Owner" });
            engine.Process(new[] { "03", "ts", "40000002", "Pet", "0", "100", "10000001" });
            var player = pet ? "40000002" : "10000001";
            engine.Process(Ability(incoming ? "40000010" : player, incoming ? player : "40000010",
                "03", "00010000", "1D", "00050000", "03", "00050000"));
            engine.Process(Ability(player, "40000011", "03", "00070000", "0104", "00020000"));
            var snapshot = engine.LiveSnapshot()!;
            Check(snapshot.HasDamage && snapshot.EncDps == (incoming ? 12 : 8) && snapshot.Rows.Single().Hps == 2,
                "Reflection stays within one target and preserves pet damage and healing",
                new { pet, incoming, snapshot.EncDps, snapshot.Rows });
        }

        var reflectedOnly = new MeterEngine(() => 0);
        reflectedOnly.SetMe(0x10000001);
        reflectedOnly.Process(new[] { "03", "ts", "40000002", "Pet", "0", "100", "10000001" });
        reflectedOnly.Process(Ability("40000010", "40000002", "03", "00050000"));
        Check(!reflectedOnly.HasLiveDamage, "Damage taken by a pet does not start display activity");
        reflectedOnly.Process(Ability("40000010", "40000002", "1D", "00050000", "03", "00050000"));
        Check(reflectedOnly.HasLiveDamage && reflectedOnly.LiveSnapshot()!.EncDps == 5,
            "Damage reflected by a pet starts display activity and belongs to its owner");
        foreach (var flags in new[] { "FFFFFFFF0000001D", "100000003", "not hex" })
        {
            var engine = new MeterEngine(() => 0);
            engine.SetMe(0x10000001);
            engine.Process(Ability("10000001", "40000010", flags, "00050000", "03", "00010000"));
            Check(engine.LiveSnapshot()!.EncDps == 1,
                "Malformed effect flags cannot change valid damage attribution", flags);
        }
    }

    private static void JobCachePressure()
    {
        var now = 0.0;
        var engine = new MeterEngine(() => now);
        engine.SetMe(0x10000001);
        engine.NoteJob(0x10000001, 31);
        engine.Process(Ability("10000001", "40000010", "03", "00010000"));
        engine.NoteJob(0x10000001, 24);
        for (var i = 0; i < 1100; i++) engine.NoteJob(0x10001000 + i, 31);
        now = 121;
        engine.Process(Ability("10000001", "40000010", "03", "00020000"));
        Check(engine.LiveSnapshot()!.Rows.Single().Job == "WHM",
            "The encounter fallback retains the latest job after cache eviction", engine.LiveSnapshot()!.Rows);
    }

    private static void LayoutBoundaries()
    {
        var config = DrawingConfig();
        config.DpsStyle = DpsMeterStyle.HorizonOverlay;
        using var host = new BridgeHost(config);
        Call(host, "Apply", Dps());
        var window = new DpsWindow(config, host, new ScaledFonts());
        JobIcons.Available = true;
        try
        {
            foreach (var scale in new[] { 0.5f, 1f, 6f })
            foreach (var iconSize in new[] { 8f, 20f, 64f })
            foreach (var height in new[] { 10f, 32f, 60f })
            {
                config.DpsTextScale = scale;
                config.DpsHorizIconSize = iconSize;
                config.DpsHorizBarHeight = height;
                ImGui.Reset();
                ImGui.Cursor = new Vector2(80, 620);
                window.Draw();
                var icon = ImGui.Commands.Single(c => c.Kind == "image");
                var percent = ImGui.Commands.Single(c => c.Text == "100%");
                Check(percent.A.Y >= icon.B.Y && ImGui.FontScale == 1,
                    "Horizon size combinations clear icons and restore the font scale",
                    new { scale, iconSize, height, iconBottom = icon.B.Y, percentTop = percent.A.Y });
            }
        }
        finally { JobIcons.Available = false; }
    }

    private static async Task PartialZoneMetadata()
    {
        foreach (var nameFirst in new[] { false, true })
        {
            using var f = await Fixture.Open();
            await f.Burst(nameFirst ? "{\"type\":\"ChangeZone\",\"zoneName\":\"Arena\"}"
                : "{\"type\":\"ChangeZone\",\"zoneID\":100}", Me, Party, Combat, Damage());
            await f.Burst(nameFirst ? Zone : "{\"type\":\"ChangeZone\",\"zoneName\":\"Arena\"}");
            Check(((MeterEngine)Field(f.Meter, "engine")).HasLiveEncounter && f.Host.Dps.Rows.Count == 1,
                "Supplemental zone metadata preserves an already running encounter", new { nameFirst, f.Host.Dps.Title });
            await f.Burst(End);
            Check(f.Host.Dps.Title == "Arena" && f.Host.Dps.EncDps == 10000 && f.Host.Dps.Rows.Single().IsSelf,
                "Supplemental zone metadata preserves final totals and identity", new { nameFirst, f.Host.Dps.Title });
        }

        using var known = await Fixture.Open();
        await known.Burst(Me, Zone, Party, Combat, Damage(), End);
        await known.Burst("{\"type\":\"ChangeZone\",\"zoneID\":200,\"zoneName\":\"Arena\"}");
        Check(known.Host.Dps.Rows.Count == 0,
            "A different known zone ID still clears a held pull when its name repeats");
        await known.Burst(Me, Party, Combat, Damage(), End);
        await known.Burst("01|ts|C8|Arena");
        Check(known.Host.Dps.Rows.Count == 0,
            "A raw zone line still clears a held pull on reentry to the same instance");
    }

    private static async Task SameZoneReconnect()
    {
        using var f = await Fixture.Open();
        await f.Burst(Me, Zone, Party, Combat, Damage(), End);
        var held = f.Host.Dps;
        await f.Disconnect();
        await Until(() => !((IinactClient)Field(f.Meter, "client")).IsConnected && Queued(f.Meter) > 0);
        f.Tick();
        await f.Accept();
        await f.Burst(Me, Zone, Party, Combat);
        Check(ReferenceEquals(held, f.Host.Dps), "An empty same-zone reconnect retains the completed pull");
        await f.Burst(Damage(amount: "00010000"), End);
        Check(f.Host.Dps.EncDps == 1 && f.Host.Dps.Rows.Single().IsSelf,
            "New damage after reconnect uses only the new session and its local identity");
    }

    private static Configuration DrawingConfig() => new()
    {
        Locked = true, DpsTextEffect = TextEffectStyle.Off,
        DpsShowHeader = false, DpsShowDeaths = true
    };

    private static void MovedBars()
    {
        var config = DrawingConfig();
        using var host = new BridgeHost(config);
        Call(host, "Apply", Dps().Replace("true,0]", "true,2]"));
        var window = new DpsWindow(config, host, new ScaledFonts());
        foreach (var origin in new[] { new Vector2(80, 620), new Vector2(-120, -200), Vector2.Zero })
        {
            ImGui.Reset();
            ImGui.Cursor = origin;
            window.Draw();
            var label = ImGui.Commands.Single(command => command.Text?.Contains("Player") == true);
            var number = ImGui.Commands.Single(command => command.Text == "DPS:1.0k Deaths:2");
            var deaths = number;
            Check(label.A.Y == number.A.Y && deaths.A.Y == number.A.Y,
                "Moved LMeter labels and death counts stay aligned with their DPS",
                new { originY = origin.Y, labelY = label.A.Y, numberY = number.A.Y, deathsY = deaths.A.Y });
        }
    }

    private static void ManualSettings()
    {
        var config = DrawingConfig();
        config.DpsTextScale = 6;
        using var host = new BridgeHost(config);
        using var ui = new PluginUi(config, host, new ScaledFonts());
        var settings = new ConfigWindow(config, host, ui);
        Call(host, "Apply", Dps());
        var window = new DpsWindow(config, host, new ScaledFonts());
        foreach (var value in new[] { float.MaxValue, -100, float.PositiveInfinity, float.NaN, 30 })
        {
            ImGui.FloatInput = value;
            Call(settings, "Slider", "Bar height", 12f, 48f, "%.0f px",
                (Func<float>)(() => config.DpsBarHeight), (Action<float>)(v => config.DpsBarHeight = v));
            ImGui.Reset();
            window.Draw();
            Check(float.IsFinite(config.DpsBarHeight) && config.DpsBarHeight is >= 12 and <= 48
                && ImGui.Commands.All(c => float.IsFinite(c.A.Y) && float.IsFinite(c.B.Y)),
                "Manual slider input stays within drawable dimensions",
                new { input = value.ToString(), config.DpsBarHeight, nonfinite = ImGui.Commands.Count(c => !float.IsFinite(c.A.Y) || !float.IsFinite(c.B.Y)) });
        }

        ImGui.IntInput = int.MaxValue;
        Call(settings, "SliderInt", "Rows", 1, 24,
            (Func<int>)(() => config.DpsMaxRows), (Action<int>)(v => config.DpsMaxRows = v));
        ImGui.FloatInput = -200;
        Call(settings, "PercentSlider", "Opacity",
            (Func<float>)(() => config.DpsFade), (Action<float>)(v => config.DpsFade = v), 5f, 100f);
        Check(config.DpsMaxRows == 24 && config.DpsFade == 0.05f,
            "Integer and percentage manual edits obey their own settings ranges",
            new { config.DpsMaxRows, config.DpsFade });
    }

    private static void HorizonLayout()
    {
        var config = DrawingConfig();
        config.DpsStyle = DpsMeterStyle.HorizonOverlay;
        config.DpsHorizIconSize = 64;
        config.DpsHorizBarHeight = 10;
        config.DpsHorizShowNames = false;
        config.DpsHorizShowPercent = true;
        using var host = new BridgeHost(config);
        Call(host, "Apply", Dps());
        var window = new DpsWindow(config, host, new ScaledFonts());
        JobIcons.Available = true;
        try
        {
            ImGui.Reset();
            window.Draw();
            var icon = ImGui.Commands.Single(c => c.Kind == "image");
            var strip = ImGui.Commands.First(c => c.Kind == "quad" && c.A.Y > icon.A.Y + 16);
            Check(strip.A.Y >= icon.B.Y,
                "Horizon share strip clears a large job icon above a short bar",
                new { iconBottom = icon.B.Y, stripTop = strip.A.Y });
        }
        finally { JobIcons.Available = false; }

        config.DpsHorizStatScale = 1.5f;
        config.DpsHorizPercentScale = 0.4f;
        config.DpsHorizShowIcons = false;
        config.DpsShowHeader = true;
        ImGui.Reset();
        window.Draw();
        var percentage = ImGui.Commands.Single(c => c.Text == "100%");
        var header = ImGui.Commands.Single(c => c.Text?.StartsWith("Old Arena") == true);
        var renderedPercentHeight = percentage.B.Y - percentage.A.Y;
        Check(header.A.Y >= percentage.A.Y + renderedPercentHeight,
            "Horizon header clears percentage text while fonts are loading",
            new { percentY = percentage.A.Y, renderedPercentHeight, headerY = header.A.Y });
    }

    private static string[] Ability(string source, string target, params string[] effects)
    {
        var fields = new List<string> { "21", "ts", source, "Source", "7", "Hit", target, "Target" };
        fields.AddRange(effects);
        while (fields.Count < 24) fields.Add("");
        return fields.ToArray();
    }

    private static void CurrentJobs()
    {
        foreach (var roster in new[] { false, true })
        {
            var now = 0.0;
            var engine = new MeterEngine(() => now);
            engine.SetMe(0x10000001);
            if (roster) engine.SetRoster(new[] { KeyValuePair.Create(0x10000001, 31) });
            else engine.NoteJob(0x10000001, 31);
            engine.Process(Ability("10000001", "40000010", "03", "00010000"));
            now = 121;
            engine.Process(new[] { "03", "ts", "10000001", "Self Player", "18", "100", "0" });
            engine.Process(Ability("10000001", "40000010", "03", "00020000"));
            var row = engine.LiveSnapshot()!.Rows.Single();
            Check(row.Job == "WHM" && row.EncDps == 2,
                "An idle display segment uses a newly reported job", new { roster, row.Job, row.EncDps });
        }
    }

    private static void ReflectedDamage()
    {
        foreach (var type in new[] { "21", "22" })
        foreach (var incoming in new[] { false, true })
        {
            var engine = new MeterEngine(() => 0);
            engine.SetMe(0x10000001);
            var source = incoming ? "40000010" : "10000001";
            var target = incoming ? "10000001" : "40000010";
            var line = Ability(source, target, "03", "00010000", "1D", "00050000", "03", "00050000");
            line[0] = type;
            engine.Process(line);
            var snapshot = engine.LiveSnapshot()!;
            Check(snapshot.HasDamage && snapshot.EncDps == (incoming ? 5 : 1),
                "Reflected damage is credited to the reflecting player", new { type, incoming, snapshot.EncDps });
        }
    }

    private static async Task ZoneBoundaries()
    {
        using var f = await Fixture.Open();
        await f.Burst(Me, Party, Combat, Damage());
        await f.Burst(Zone);
        Check(f.Host.Dps.Title == "Arena" && f.Host.Dps.Rows.Single().IsSelf,
            "Late initial zone metadata labels the live encounter without erasing it",
            new { f.Host.Dps.Title, f.Host.Dps.EncDps });
        await f.Burst("{\"type\":\"ChangeZone\",\"zoneID\":200}");
        Check(!((MeterEngine)Field(f.Meter, "engine")).HasLiveEncounter && f.Host.Dps.Rows.Count == 0,
            "A changed zone ID clears the encounter even before its name arrives",
            new { f.Host.Dps.Title, f.Host.Dps.EncDps, f.Host.Dps.Show });
    }

    private static async Task ReconnectIdentity()
    {
        using var f = await Fixture.Open();
        await f.Burst(Me, Zone, Party, Combat, Damage(), End);
        await f.Disconnect();
        await Until(() => !((IinactClient)Field(f.Meter, "client")).IsConnected && Queued(f.Meter) > 0);
        f.Tick();
        await f.Accept();
        await f.Burst(Me, "{\"type\":\"ChangeZone\",\"zoneID\":200,\"zoneName\":\"Other Arena\"}", Party, Combat, Damage(), End);
        Check(f.Host.Dps.Rows.Single().IsSelf && f.Host.Dps.Title == "Other Arena",
            "A reconnect into another zone preserves the replayed local identity", f.Host.Dps.Rows);
    }
}
