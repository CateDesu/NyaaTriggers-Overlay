using System.Numerics;
using System.Diagnostics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class TransitionTests
{
    internal static Task HeaderProfiles()
    {
        foreach (var format in new[] { "First\nSecond", "First\r\nSecond",
            string.Concat(Enumerable.Repeat("{title} ", 8000)) })
        {
            var config = new Configuration { Locked = true, DpsTextEffect = TextEffectStyle.Off };
            var blob = Configuration.ValidateProfileBlob(JsonSerializer.Serialize(new { DpsHeaderFormat = format }));
            Check(blob != null && config.ApplyAppearanceProfile(blob), "Header profile passes the supported import path");
            using var host = new BridgeHost(config);
            Call(host, "Apply", Dps(new string('X', 256)));
            var window = new DpsWindow(config, host, new ScaledFonts());
            ImGui.Reset();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            window.Draw();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var header = ImGui.Commands.First(c => c.Kind == "text");
            var row = ImGui.Commands.First(c => c.Kind == "text" && c.Text!.Contains("Player"));
            Check(config.DpsHeaderFormat.Length <= 128 && !header.Text!.Contains('\n')
                && !header.Text.Contains('\r') && header.B.Y <= row.A.Y,
                "Imported header formats respect the single line editor and its length limit",
                new { inputLength = format.Length, storedLength = config.DpsHeaderFormat.Length,
                    drawnLength = header.Text!.Length, headerBottom = header.B.Y, rowTop = row.A.Y,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = bytes });
        }

        var literalConfig = new Configuration { DpsHeaderFormat = "{TITLE} / {duration} / {dps}" };
        using var literalHost = new BridgeHost(literalConfig);
        var literalWindow = new DpsWindow(literalConfig, literalHost, new ScaledFonts());
        var text = (string)Call(literalWindow, "FormatHeaderLine", "{duration}", "{dps}", 1000d)!;
        Check(text == "{duration} / {dps} / 1.0k", "Header values stay literal after token expansion", text);

        return Task.CompletedTask;
    }

    internal static Task Fonts()
    {
        foreach (var bodyReady in new[] { false, true })
        foreach (var statReady in new[] { false, true })
        foreach (var captionReady in new[] { false, true })
        {
            var config = new Configuration
            {
                Locked = true, DpsStyle = DpsMeterStyle.HorizonOverlay,
                DpsTextEffect = TextEffectStyle.Off, DpsTextScale = 3,
                DpsHorizStatScale = 0.5f, DpsHorizPercentScale = 0.4f,
                DpsHorizShowIcons = false, DpsHorizMaxBarWidth = 400,
            };
            using var host = new BridgeHost(config);
            Call(host, "Apply", Dps("Font Arena"));
            using var fonts = new ScaledFonts
            {
                AvailableSize = pixels => pixels == 48 ? bodyReady ? 48 : null
                    : pixels == 24 ? statReady ? 24 : null
                    : Math.Abs(pixels - 19.2f) < 0.01f ? captionReady ? 20 : null : null,
            };
            var window = new DpsWindow(config, host, fonts);
            ImGui.Reset();
            window.Draw();
            var name = ImGui.Commands.Single(c => c.Kind == "text" && c.Text!.Contains("Player"));
            var percent = ImGui.Commands.Single(c => c.Kind == "text" && c.Text == "100%");
            var header = ImGui.Commands.Single(c => c.Kind == "text" && c.Text!.Contains("Font Arena"));
            Check(Near(name.B.Y - name.A.Y, 24) && Near(percent.B.Y - percent.A.Y, 19.2f)
                && Near(header.B.Y - header.A.Y, 48),
                "Horizon font availability does not change configured text sizes",
                new { bodyReady, statReady, captionReady, nameHeight = name.B.Y - name.A.Y,
                    captionHeight = percent.B.Y - percent.A.Y, headerHeight = header.B.Y - header.A.Y });
            Check(ImGui.FontScale == 1 && ImGui.FontBaseSize == 16,
                "Nested Horizon fonts restore the original font and window scale");
        }

        foreach (var bodyReady in new[] { false, true })
        foreach (var alarmReady in new[] { false, true })
        {
            var config = new Configuration
            {
                Locked = true, AlertsTextScale = 3, AlertsAlarmScale = 2,
                AlertsTextEffect = TextEffectStyle.Off, AlertsAnimate = false,
                AlertsAnchorBottom = false, AlertsAlarmFlash = false,
            };
            using var host = new BridgeHost(config);
            Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Alarm text\",\"sev\":\"alarm\"}");
            using var fonts = new ScaledFonts
            {
                AvailableSize = pixels => pixels == 48 ? bodyReady ? 48 : null
                    : pixels == 96 ? alarmReady ? 97 : null : null,
            };
            var window = new AlertsWindow(config, host, fonts);
            ImGui.Reset();
            window.Draw();
            var text = ImGui.Commands.Single(c => c.Kind == "text");
            Check(Near(text.B.Y - text.A.Y, 96), "Cached alarm fonts keep the requested size while body fonts load",
                new { bodyReady, alarmReady, height = text.B.Y - text.A.Y });
            Check(ImGui.Cursor.Y >= text.B.Y && ImGui.FontScale == 1 && ImGui.FontBaseSize == 16,
                "Alarm layout reserves its drawn height and restores font state");
        }

        return Task.CompletedTask;
    }

    internal static Task Geometry()
    {
        foreach (var style in Enum.GetValues<DpsMeterStyle>())
        foreach (var uiScale in new[] { 1f, 2f })
        {
            var config = new Configuration
            {
                Locked = true, DpsStyle = style,
                DpsTextEffect = TextEffectStyle.Off, DpsPos = new Vector2(80, 620),
                DpsSize = new Vector2(600, 400), ShowTimeline = false, ShowAlerts = false,
            };
            using var host = new BridgeHost(config);
            Call(host, "Apply", Dps());
            using var ui = new PluginUi(config, host, new ScaledFonts());
            var window = ((DpsWindow[])Field(ui, "meters")).Single(item => item.Meter.DpsStyle == style);
            var meter = window.Meter;
            var initialSize = meter.DpsSize;
            var appeared = false;
            ImGuiHelpers.GlobalScale = uiScale;
            WindowSystem.AfterPreDraw = active =>
            {
                if (!ReferenceEquals(active, window)) return;
                if (!appeared || window.SizeCondition == ImGuiCond.Always)
                    ImGui.WindowSize = window.Size!.Value * ImGuiHelpers.GlobalScale;
                if (!appeared || window.PositionCondition == ImGuiCond.Always)
                    ImGui.WindowPosition = window.Position!.Value;
                appeared = true;
                ImGui.Reset();
                ImGui.Cursor = ImGui.WindowPosition + new Vector2(8);
                ImGui.Available = ImGui.WindowSize - new Vector2(16);
            };
            try
            {
                ui.Draw();
                ui.Draw();
                var fittedHeight = ImGui.WindowSize.Y;
                Check((style != DpsMeterStyle.HorizonOverlay || fittedHeight < initialSize.Y) && meter.DpsSize == initialSize,
                    "Meters preserve placement while Horizon fits its content", new { uiScale, fittedHeight });
                meter.ShowDps = false;
                ui.Draw();
                ui.Draw();
                ui.SetLocked(false);
                ui.Draw();
                Check(!window.IsOpen && ImGui.WindowSize.Y == fittedHeight,
                    "The hidden meter retains its fitted geometry while settings change");
                meter.ShowDps = true;
                ui.Draw();
                Check(meter.DpsSize == initialSize && ImGui.WindowSize == initialSize,
                    "Unlocking a hidden meter restores its saved placement size",
                    new { style = style.ToString(), uiScale, expectedHeight = initialSize.Y, actualHeight = meter.DpsSize.Y });
                var resized = new Vector2(500, 350);
                ImGui.WindowSize = resized;
                ui.Draw();
                Check(meter.DpsSize == resized, "Restoring the placement size still allows the next manual resize");
            }
            finally
            {
                ImGuiHelpers.GlobalScale = 1;
                WindowSystem.AfterPreDraw = null;
                ImGui.WindowPosition = Vector2.Zero;
                ImGui.WindowSize = new Vector2(1600, 900);
                ImGui.Available = new Vector2(1600, 900);
                ImGui.Reset();
            }

        }

        return Task.CompletedTask;
    }

    internal static async Task CombatState()
    {
        using var f = await Fixture.Open();
        await f.Burst(Me, Zone, Party);
        foreach (var invalid in new[]
        {
            "{\"type\":\"InCombat\"}",
            "{\"type\":\"InCombat\",\"inACTCombat\":true}",
            "{\"type\":\"InCombat\",\"inGameCombat\":true}",
            "{\"type\":\"InCombat\",\"inACTCombat\":null,\"inGameCombat\":true}",
            "{\"type\":\"InCombat\",\"inACTCombat\":true,\"inGameCombat\":\"false\"}",
            "{\"type\":\"InCombat\",\"inACTCombat\":0,\"inGameCombat\":0}",
        })
        {
            await f.Burst(Combat, Damage(), invalid, Damage(), End);
            Check(f.Host.Dps.Ended && f.Host.Dps.EncDps == 20000,
                "Malformed combat flags cannot split a valid encounter",
                new { invalid = JsonDocument.Parse(invalid).RootElement.Clone(), f.Host.Dps.EncDps });
        }

        await f.Burst(Combat, Damage(), End, Combat, Damage(amount: "00010000"), End);
        Check(f.Host.Dps.Ended && f.Host.Dps.EncDps == 1,
            "Valid combat edges still separate consecutive pulls");
    }

    internal static async Task Adversarial()
    {
        FontBoundaries();
        AlarmBoundaries();
        HeaderBoundaries();
        foreach (var ready in new[] { false, true })
        {
            using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
            var config = new Configuration
            {
                Locked = true, DpsStyle = DpsMeterStyle.HorizonOverlay,
                DpsTextScale = 1.3f, DpsTextEffect = TextEffectStyle.Off,
            };
            using var host = new BridgeHost(config);
            Call(host, "Apply", Dps());
            var window = new DpsWindow(config, host, fonts);
            ImGui.Reset();
            ImGui.FontScale = 0.75f;
            ImGui.ThrowForText = "100%";
            try { window.Draw(); }
            catch (ArithmeticException) { }
            finally { ImGui.ThrowForText = null; }
            Check(Near(ImGui.FontScale, 0.75f) && ImGui.FontBaseSize == 16,
                "A failed draw restores nested fonts and the previous window scale", new { ready, ImGui.FontScale });
            ImGui.Reset();
        }

        using var f = await Fixture.Open();
        await f.Burst(Me, Zone, Party);
        foreach (var act in new[] { false, true })
        foreach (var game in new[] { false, true })
        {
            await f.Burst(Combat, Damage(), JsonSerializer.Serialize(new
            {
                type = "InCombat", inACTCombat = act, inGameCombat = game,
            }));
            Check(((MeterEngine)Field(f.Meter, "engine")).HasLiveEncounter == (act && game),
                "Each valid falling combat edge still ends its encounter", new { act, game });
            await f.Burst(End);
        }

        foreach (var invalid in new object?[] { null, 0, 1, "true", "false", new object(), Array.Empty<bool>() })
        foreach (var actInvalid in new[] { false, true })
        {
            var message = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "InCombat",
                ["inACTCombat"] = actInvalid ? invalid : false,
                ["inGameCombat"] = actInvalid ? false : invalid,
            });
            await f.Burst(Combat, Damage(), message, Damage(), End);
            Check(f.Host.Dps.EncDps == 20000,
                "A malformed flag cannot apply the other flag's falling edge", new { message, f.Host.Dps.EncDps });
        }
    }

    private static void FontBoundaries()
    {
        foreach (var defaultPixels in new[] { 16f, 48f })
        foreach (var scale in new[] { 0.5f, 1.3f, 6f })
        foreach (var statScale in new[] { 0.4f, 1.5f })
        {
            var config = new Configuration
            {
                Locked = true, DpsStyle = DpsMeterStyle.HorizonOverlay,
                DpsTextScale = scale, DpsTextEffect = TextEffectStyle.Off,
                DpsHorizStatScale = statScale, DpsHorizPercentScale = 1.9f - statScale,
                DpsHorizShowIcons = false, DpsHorizMaxBarWidth = 400,
            };
            using var host = new BridgeHost(config);
            Call(host, "Apply", Dps("Boundary Arena"));
            var bodyPixels = defaultPixels * scale;
            using var fonts = new ScaledFonts();
            var window = new DpsWindow(config, host, fonts);
            foreach (var availability in new[] { 0, 1, 2, 3, 0 })
            {
                fonts.AvailableSize = pixels =>
                    ((availability & (Near(pixels, bodyPixels) ? 1 : 2)) != 0)
                        ? Math.Max(8, MathF.Ceiling(pixels)) : null;
                ImGui.Reset();
                ImGui.FontBaseSize = defaultPixels;
                ImGui.Cursor = new Vector2(90, 150);
                window.Draw();
                var texts = ImGui.Commands.Where(c => c.Kind == "text").ToArray();
                var name = texts.Single(c => c.Text!.Contains("Player"));
                var header = texts.Single(c => c.Text!.Contains("Boundary Arena"));
                var caption = texts.Single(c => c.Text == "100%");
                Check(Near(name.B.Y - name.A.Y, bodyPixels * statScale)
                    && Near(header.B.Y - header.A.Y, bodyPixels)
                    && Near(caption.B.Y - caption.A.Y, bodyPixels * config.DpsHorizPercentScale)
                    && header.A.Y >= caption.B.Y && Near(ImGui.FontScale, 1)
                    && ImGui.FontBaseSize == defaultPixels,
                    "Text sizes and header spacing survive loading changes at supported scale boundaries",
                    new { defaultPixels, scale, statScale, availability,
                        nameHeight = name.B.Y - name.A.Y, captionHeight = caption.B.Y - caption.A.Y });
            }
        }

        ImGui.Reset();
    }

    private static void AlarmBoundaries()
    {
        foreach (var alignment in Enum.GetValues<TextAlign>())
        foreach (var wrap in new[] { false, true })
        {
            var config = new Configuration
            {
                Locked = true, AlertsTextScale = 1.3f, AlertsAlarmScale = 1.6f,
                AlertsTextEffect = TextEffectStyle.Off, AlertsAnimate = false,
                AlertsAnchorBottom = false, AlertsAlarmFlash = false,
                AlertsAlign = alignment, AlertsWrap = wrap, AlertsLifeline = true,
                AlertOrder = AlertOrder.OldestFirst,
            };
            using var host = new BridgeHost(config);
            Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Alarm wraps over several lines\",\"sev\":\"alarm\",\"ttl\":30}");
            Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Next alert\",\"sev\":\"alert\",\"ttl\":30}");
            using var fonts = new ScaledFonts();
            var window = new AlertsWindow(config, host, fonts);
            try
            {
                foreach (var ready in new[] { false, true, false, true })
                {
                    fonts.AvailableSize = pixels => ready ? Math.Max(8, MathF.Ceiling(pixels)) : null;
                    ImGui.Reset();
                    ImGui.Available = new Vector2(180, 900);
                    window.Draw();
                    var texts = ImGui.Commands.Where(c => c.Kind == "text").ToArray();
                    var next = texts.Single(c => c.Text == "Next alert");
                    var alarm = texts.TakeWhile(c => c != next).ToArray();
                    Check(alarm.Length > 0 && alarm.All(c => Near(c.B.Y - c.A.Y, 16 * 1.3f * 1.6f))
                        && Near(next.B.Y - next.A.Y, 16 * 1.3f)
                        && alarm.Max(c => c.B.Y) < next.A.Y
                        && texts.All(c => c.A.X >= -0.01f && c.B.X <= 180.01f),
                        "Alarm wrapping alignment and the following alert remain correct as fonts rebuild",
                        new { alignment = alignment.ToString(), wrap, ready, lines = alarm.Length, nextY = next.A.Y });
                }
            }
            finally { ImGui.Available = new Vector2(1600, 900); ImGui.Reset(); }
        }
    }

    private static void HeaderBoundaries()
    {
        foreach (var format in new string?[] { null, "", new string('x', 127) + "😀z",
            new string('x', 126) + "😀z", new string('x', 128), new string('x', 129) })
        {
            var config = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(new { DpsHeaderFormat = format }))!;
            Check(config.DpsHeaderFormat is { Length: <= 128 } header && !header.EndsWith('\ud83d'),
                "Saved header formats are bounded without splitting a Unicode character",
                new { inputLength = format?.Length, resultLength = config.DpsHeaderFormat?.Length });
            var roundTrip = new Configuration();
            roundTrip.ApplyAppearanceProfile(config.SnapshotAppearance());
            Check(config.DpsHeaderFormat == roundTrip.DpsHeaderFormat,
                "Header limits survive profile export and import");
        }

        var cfg = new Configuration { DpsHeaderFormat = "{TITLE} | {DURATION} | {DPS} | {unknown}" };
        using var host = new BridgeHost(cfg);
        var window = new DpsWindow(cfg, host, new ScaledFonts());
        foreach (var duration in new[] { false, true })
        foreach (var dps in new[] { false, true })
        {
            cfg.DpsHeaderDuration = duration;
            cfg.DpsHeaderTotalDps = dps;
            var text = (string)Call(window, "FormatHeaderLine", "Arena", "00:10", 1000d)!;
            var expected = string.Join(" | ", new[] { "Arena", duration ? "00:10" : null,
                dps ? "1.0k" : null, "{unknown}" }.Where(part => part != null));
            Check(text == expected, "Header token case optional values and unknown tokens keep their meaning", text);
        }

        cfg.DpsHeaderFormat = "{title}";
        cfg.DpsHeaderDuration = cfg.DpsHeaderTotalDps = true;
        var expanded = (string)Call(window, "FormatHeaderLine", string.Concat(Enumerable.Repeat("{duration}", 25)),
            string.Concat(Enumerable.Repeat("{dps}", 51)), double.MaxValue)!;
        Check(expanded.Length < 8192 && expanded.Contains("{duration}"),
            "Inserted values cannot multiply template expansion", new { expandedLength = expanded.Length });
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;
}
