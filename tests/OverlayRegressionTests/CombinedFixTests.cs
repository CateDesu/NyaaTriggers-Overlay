using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class CombinedFixTests
{
    internal static Task GlobalFonts()
    {
        try
        {
            foreach (var global in new[] { 0.8f, 1f, 1.5f, 2f, 3f })
            foreach (var ready in new[] { false, true })
            {
                ImGuiHelpers.GlobalScale = global;
                var config = new Configuration
                {
                    Locked = true, DpsStyle = DpsMeterStyle.HorizonOverlay,
                    DpsTextEffect = TextEffectStyle.Off, DpsTextScale = 2,
                    DpsHorizStatScale = 0.5f, DpsHorizPercentScale = 0.4f,
                    DpsHorizShowIcons = false, DpsHorizMaxBarWidth = 400,
                };
                using var host = new BridgeHost(config);
                Call(host, "Apply", Dps("Global Arena"));
                using var fonts = new ScaledFonts { AvailableSize = pixels => ready ? MathF.Ceiling(pixels) : null };
                var window = new DpsWindow(config, host, fonts);
                ImGui.Reset();
                for (var frame = 0; frame < 3; frame++)
                {
                    ImGui.Commands.Clear();
                    ImGui.Cursor = new Vector2(80, 100);
                    window.Draw();
                    var name = ImGui.Commands.Single(c => c.Kind == "text" && c.Text!.Contains("Player"));
                    var percent = ImGui.Commands.Single(c => c.Kind == "text" && c.Text == "100%");
                    var header = ImGui.Commands.Single(c => c.Kind == "text" && c.Text!.Contains("Global Arena"));
                    Check(Near(name.B.Y - name.A.Y, 16 * global) && Near(percent.B.Y - percent.A.Y, 12.8f * global)
                        && Near(header.B.Y - header.A.Y, 32 * global) && Near(ImGui.FontScale, 1),
                        "Real global font scaling preserves nested sizes and the next frame",
                        new { global, ready, frame, nameHeight = name.B.Y - name.A.Y,
                            percentHeight = percent.B.Y - percent.A.Y, headerHeight = header.B.Y - header.A.Y,
                            restoredScale = ImGui.FontScale });
                }
            }
        }
        finally { ImGuiHelpers.GlobalScale = 1; ImGui.Reset(); }
        return Task.CompletedTask;
    }

    internal static async Task PartialReconnects()
    {
        foreach (var nameFirst in new[] { true, false })
        {
            using var f = await Fixture.Open();
            await f.Burst(Me, Zone, Party, Combat, Damage(), End);
            await f.Disconnect();
            await Until(() => !((IinactClient)Field(f.Meter, "client")).IsConnected && Queued(f.Meter) > 0);
            f.Tick();
            await f.Accept();
            var first = nameFirst ? "{\"type\":\"ChangeZone\",\"zoneName\":\"Arena\"}"
                : "{\"type\":\"ChangeZone\",\"zoneID\":100}";
            var supplement = nameFirst ? "{\"type\":\"ChangeZone\",\"zoneID\":200,\"zoneName\":\"Arena\"}"
                : "{\"type\":\"ChangeZone\",\"zoneName\":\"New Arena\"}";
            await f.Burst(Me, Party, first, Combat, Damage(amount: "00010000"));
            await f.Burst(supplement);
            var engine = (MeterEngine)Field(f.Meter, "engine");
            Check(engine.HasLiveEncounter && f.Host.Dps.Rows.Count == 1 && f.Host.Dps.Rows[0].IsSelf,
                "Partial replay metadata cannot use a prior session to erase current identity and damage",
                new { nameFirst, engine.HasLiveEncounter, f.Host.Dps.Title, f.Host.Dps.Rows });
            await f.Burst(Damage(amount: "00020000"), End);
            Check(f.Host.Dps.EncDps == 3 && f.Host.Dps.Rows.Count == 1 && f.Host.Dps.Rows[0].IsSelf,
                "The reconnected pull retains both hits after zone metadata is completed",
                new { nameFirst, f.Host.Dps.EncDps, f.Host.Dps.Rows });
        }
    }

    internal static Task Boundaries()
    {
        MixedFonts();
        ScaledAlarms();
        ZoneSequences();
        return Task.CompletedTask;
    }

    private static void MixedFonts()
    {
        try
        {
            foreach (var global in new[] { 0.8f, 1.5f, 3f })
            foreach (var textScale in new[] { 0.5f, 6f })
            for (var mask = 0; mask < 8; mask++)
            {
                ImGuiHelpers.GlobalScale = global;
                JobIcons.Available = true;
                var bodyPx = 16 * 1.25f * global * textScale;
                var config = new Configuration
                {
                    Locked = true, DpsStyle = DpsMeterStyle.HorizonOverlay,
                    DpsTextEffect = TextEffectStyle.Off, DpsTextScale = textScale,
                    DpsHorizStatScale = 0.5f, DpsHorizPercentScale = 0.4f,
                    DpsHorizIconSize = 64, DpsHorizBarHeight = 10, DpsHorizMaxBarWidth = 400,
                };
                using var host = new BridgeHost(config);
                Call(host, "Apply", Dps("Mixed Arena"));
                using var fonts = new ScaledFonts
                {
                    AvailableSize = pixels => (mask & (Near(pixels, bodyPx) ? 1 : Near(pixels, bodyPx / 2) ? 2 : 4)) != 0
                        ? MathF.Ceiling(pixels) : null,
                };
                var window = new DpsWindow(config, host, fonts);
                ImGui.Reset();
                ImGui.FontMultiplier = 1.25f;
                ImGui.FontScale = 0.75f;
                ImGui.Cursor = new Vector2(-120, 620);
                window.Draw();
                var name = ImGui.Commands.Single(c => c.Kind == "text" && c.Text!.Contains("Player"));
                var percent = ImGui.Commands.Single(c => c.Text == "100%");
                var header = ImGui.Commands.Single(c => c.Text?.Contains("Mixed Arena") == true);
                var icon = ImGui.Commands.Single(c => c.Kind == "image");
                Check(Near(name.B.Y - name.A.Y, bodyPx / 2) && Near(percent.B.Y - percent.A.Y, bodyPx * 0.4f)
                    && Near(header.B.Y - header.A.Y, bodyPx) && header.A.Y >= percent.B.Y - 0.02f
                    && percent.A.Y >= icon.B.Y && Near(ImGui.FontScale, 0.75f) && ImGui.FontMultiplier == 1.25f,
                    "Mixed font readiness respects global scale custom fonts icons and header clearance",
                    new { global, textScale, mask, bodyPx, nameHeight = name.B.Y - name.A.Y,
                        percentHeight = percent.B.Y - percent.A.Y, headerHeight = header.B.Y - header.A.Y,
                        iconBottom = icon.B.Y, percentTop = percent.A.Y, restoredScale = ImGui.FontScale });
                ImGui.ThrowForText = "100%";
                try { window.Draw(); }
                catch (ArithmeticException) { }
                finally { ImGui.ThrowForText = null; }
                Check(Near(ImGui.FontScale, 0.75f) && ImGui.FontBaseSize == 16 && ImGui.FontMultiplier == 1.25f,
                    "Drawing failure restores scaled custom fonts after nested scopes", new { global, textScale, mask });
            }
        }
        finally { JobIcons.Available = false; ImGuiHelpers.GlobalScale = 1; ImGui.ThrowForText = null; ImGui.Reset(); }
    }

    private static void ScaledAlarms()
    {
        try
        {
            foreach (var global in new[] { 0.8f, 1.5f, 3f })
            for (var mask = 0; mask < 4; mask++)
            {
                ImGuiHelpers.GlobalScale = global;
                var config = new Configuration
                {
                    Locked = true, AlertsTextScale = 2, AlertsAlarmScale = 2,
                    AlertsTextEffect = TextEffectStyle.Off, AlertsAnimate = false,
                    AlertsAnchorBottom = false, AlertsAlarmFlash = false,
                };
                using var host = new BridgeHost(config);
                Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Alarm\",\"sev\":\"alarm\"}");
                using var fonts = new ScaledFonts
                {
                    AvailableSize = pixels => (mask & (Near(pixels, 32 * global) ? 1 : 2)) != 0
                        ? MathF.Ceiling(pixels) : null,
                };
                var window = new AlertsWindow(config, host, fonts);
                ImGui.Reset();
                for (var frame = 0; frame < 3; frame++)
                {
                    ImGui.Commands.Clear();
                    ImGui.Cursor = new Vector2(50, 100);
                    window.Draw();
                    var text = ImGui.Commands.Single(c => c.Kind == "text");
                    Check(Near(text.B.Y - text.A.Y, 64 * global) && ImGui.Cursor.Y >= text.B.Y - 0.02f
                        && Near(ImGui.FontScale, 1) && ImGui.FontBaseSize == 16,
                        "Alarm measurement and drawing agree under global scaling across frames",
                        new { global, mask, frame, height = text.B.Y - text.A.Y, restoredScale = ImGui.FontScale });
                }
            }
        }
        finally { ImGuiHelpers.GlobalScale = 1; ImGui.Reset(); }
    }

    private static void ZoneSequences()
    {
        foreach (var nameFirst in new[] { true, false })
        foreach (var changed in new[] { true, false })
        foreach (var damageBeforeMetadata in new[] { true, false })
        {
            var states = new List<DpsState>();
            using var meter = new StandaloneMeter(new Configuration(), () => false, states.Add, () => { });
            Call(meter, "ResetEngine");
            void Handle(params string[] messages) { foreach (var message in messages) Call(meter, "Handle", message); }
            Handle(Me, Zone, Party, Combat, Damage(), End);
            for (var session = 0; session < 3; session++)
            {
                Call(meter, "ResetEngine");
                Handle(Me, Party);
                var zoneId = nameFirst && changed ? 200 : 100;
                var zoneName = !nameFirst && changed ? "New Arena" : "Arena";
                var first = nameFirst ? $"{{\"type\":\"ChangeZone\",\"zoneName\":\"{zoneName}\"}}"
                    : $"{{\"type\":\"ChangeZone\",\"zoneID\":{zoneId}}}";
                var complete = $"{{\"type\":\"ChangeZone\",\"zoneID\":{zoneId},\"zoneName\":\"{zoneName}\"}}";
                Handle(first);
                var beforeSupplement = states.Count;
                if (damageBeforeMetadata) Handle(Combat, Damage(amount: "00010000"));
                Handle(complete, complete);
                if (!damageBeforeMetadata) Handle(Combat, Damage(amount: "00010000"));
                Handle("{\"type\":\"InCombat\",\"inACTCombat\":false}", Damage(amount: "00020000"), End);
                var result = states[^1];
                Check(result.EncDps == 3 && result.Title == zoneName && result.Rows.Count == 1 && result.Rows[0].IsSelf,
                    "Repeated partial sessions retain totals and malformed combat flags cannot end them",
                    new { nameFirst, changed, damageBeforeMetadata, session, result.Title, result.EncDps, result.Rows });
                if (!damageBeforeMetadata && session == 0)
                {
                    Check(states.Count == beforeSupplement + (nameFirst && changed ? 2 : 1),
                        "Held pulls use known zone IDs before comparing names", new { nameFirst, changed, states.Count, beforeSupplement });
                }
                Handle(Combat, Damage(amount: "00010000"));
                Handle($"{{\"type\":\"ChangeZone\",\"zoneID\":{zoneId + 1}}}");
                Check(!((MeterEngine)Field(meter, "engine")).HasLiveEncounter && states[^1].Rows.Count == 0,
                    "A known current session zone change still clears a live pull", new { nameFirst, changed, session });
            }
        }
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 0.02f;
}
