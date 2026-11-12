using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Ui;
using static Program;

internal static class MeterWindowsTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    internal static Task Run()
    {
        Migration();
        DiskConfiguration();
        Profiles();
        Windows();
        MenusAndHeadings();
        return Task.CompletedTask;
    }

    private static void Migration()
    {
        foreach (var selected in Enum.GetValues<DpsMeterStyle>())
        foreach (var enabled in new[] { false, true })
        {
            var old = new Configuration
            {
                Version = 5, DpsStyle = selected, ShowDps = enabled,
                DpsPos = new Vector2(313, 217), DpsSize = new Vector2(555, 333),
                DpsTextScale = 1.4f, DpsOnlyInDuty = true, DpsHoldLast = false,
                DpsHorizSkew = 17, DpsKagerouIcons = false,
            };
            var config = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(old, Json), Json)!;
            config.Sanitize();
            config.MigrateFromV5();
            var active = config.GetMeter(selected);
            Check(config.Version == 6 && active.DpsPos == old.DpsPos && active.DpsSize == old.DpsSize
                  && active.ShowDps == enabled && active.DpsTextScale == 1.4f && active.DpsOnlyInDuty && !active.DpsHoldLast,
                "Old meter settings retain their placement appearance and visibility", new { selected, enabled });
            var others = Enum.GetValues<DpsMeterStyle>().Where(style => style != selected).Select(config.GetMeter).ToArray();
            Check(others.All(meter => !meter.ShowDps && !ReferenceEquals(meter, active))
                  && active.DpsHorizSkew == 17 && !active.DpsKagerouIcons,
                "Migration leaves additional windows disabled and preserves style options");
            active.DpsTextScale = 2.2f;
            active.DpsKagerouDeathsAlign = TextAlign.Right;
            config.MigrateFromV5();
            Check(ReferenceEquals(active, config.GetMeter(selected)) && others.All(meter => meter.DpsTextScale == 1.4f),
                "Migration is repeatable without replacing saved meter settings");
            var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config, Json), Json)!;
            restored.Sanitize();
            Check(restored.GetMeter(selected).DpsTextScale == 2.2f
                  && restored.GetMeter(selected).DpsKagerouDeathsAlign == TextAlign.Right
                  && restored.GetMeter(selected).DpsPos == old.DpsPos,
                "Separate settings and placement survive a settings file round trip");
        }

        foreach (var style in Enum.GetValues<DpsMeterStyle>())
        {
            var config = new Configuration();
            var meter = config.GetMeter(style);
            meter.DpsStyle = style == DpsMeterStyle.LMeter ? DpsMeterStyle.HorizonOverlay : DpsMeterStyle.LMeter;
            meter.DpsBarHeight = float.NaN;
            meter.DpsSize = new Vector2(float.NaN);
            var restored = Configuration.Load(() => config, "unused-config-path");
            var expected = MeterSettings.DefaultsFor(style);
            Check(ReferenceEquals(restored.GetMeter(style), meter)
                  && meter.DpsBarHeight == expected.DpsBarHeight && meter.DpsSize == expected.DpsSize,
                "Nested settings recover defaults for their window despite a stale serialized style", style);
            meter.DpsHoldLast = style != DpsMeterStyle.HorizonOverlay;
            var reloaded = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(restored, Json), Json)!;
            reloaded.Sanitize();
            var saved = reloaded.GetMeter(style);
            Check(saved.DpsStyle == style && saved.DpsSize == expected.DpsSize
                  && saved.DpsBarHeight == expected.DpsBarHeight && saved.DpsHoldLast == meter.DpsHoldLast,
                "Recovered window defaults and retention choices survive serialization", style);
        }

        var malformed = new Configuration
        {
            KagerouMeter = new MeterSettings
            {
                DpsTextScale = float.NaN, DpsSize = new Vector2(-1, float.PositiveInfinity),
                DpsKagerouDeathsAlign = (TextAlign)500, DpsKagerouDeathHeading = (KagerouDeathHeading)500,
                DpsTextColor = new Vector4(float.NaN, -1, 2, float.PositiveInfinity),
            },
        };
        malformed.Sanitize();
        var safe = malformed.GetMeter(DpsMeterStyle.Kagerou);
        Check(float.IsFinite(safe.DpsTextScale) && float.IsFinite(safe.DpsSize.Y)
              && safe.DpsKagerouDeathsAlign == TextAlign.Center && safe.DpsKagerouDeathHeading == KagerouDeathHeading.Dead
              && safe.DpsTextColor.Y == 0 && safe.DpsTextColor.Z == 1,
            "Malformed nested settings recover safe defaults including centered deaths");
    }

    private static void DiskConfiguration()
    {
        var previousStore = Services.PluginInterface;
        var folder = Directory.CreateTempSubdirectory("nyaa-meter-config-");
        try
        {
            var store = new TestConfigStore { FilePath = Path.Combine(folder.FullName, "NyaaTriggers.json") };
            Services.PluginInterface = store;
            foreach (var undefinedStyle in new[] { false, true })
            {
                var config = new Configuration { Port = 31234 };
                foreach (var style in Enum.GetValues<DpsMeterStyle>())
                {
                    var meter = config.GetMeter(style);
                    meter.DpsStyle = undefinedStyle ? (DpsMeterStyle)999 : (DpsMeterStyle)(((int)style + 1) % 3);
                    meter.DpsBarHeight = float.NaN;
                    meter.DpsSize = new Vector2(float.NaN, float.PositiveInfinity);
                    meter.DpsPos = new Vector2(711 + (int)style, 812);
                    meter.DpsTextScale = 1.75f;
                    meter.DpsHoldLast = style == DpsMeterStyle.LMeter;
                    meter.ShowDps = style != DpsMeterStyle.HorizonOverlay;
                    meter.DpsOnlyInDuty = true;
                }
                config.Save();
                var damaged = (Configuration)store.GetPluginConfig();
                foreach (var (style, meter) in NestedMeters(damaged))
                    Check(!ReferenceEquals(damaged, config) && meter != null && meter.DpsStyle != style
                          && float.IsNaN(meter.DpsBarHeight) && float.IsNaN(meter.DpsSize.X)
                          && float.IsPositiveInfinity(meter.DpsSize.Y),
                        "Json.NET reads malformed nested styles and geometry from the saved file", new { style, undefinedStyle });

                var loaded = Configuration.Load(store.GetPluginConfig, store.ConfigFile.FullName);
                CheckRecovered(loaded, "load");
                loaded.Save();
                CheckRecovered((Configuration)store.GetPluginConfig(), "saved file before sanitation");
                CheckRecovered(Configuration.Load(store.GetPluginConfig, store.ConfigFile.FullName), "reload");
                Check(Directory.GetFiles(folder.FullName).Length == 1,
                    "Recoverable nested settings keep the original config file without quarantine");
            }
        }
        finally
        {
            Services.PluginInterface = previousStore;
            folder.Delete(true);
        }
        Check(!Directory.Exists(folder.FullName), "Disk configuration regression removes its temporary files");

        static (DpsMeterStyle Style, MeterSettings? Meter)[] NestedMeters(Configuration config) =>
        [
            (DpsMeterStyle.LMeter, config.BarsMeter),
            (DpsMeterStyle.HorizonOverlay, config.HorizonMeter),
            (DpsMeterStyle.Kagerou, config.KagerouMeter),
        ];

        static void CheckRecovered(Configuration config, string phase)
        {
            Check(config.Port == 31234, "Disk recovery preserves unrelated configuration", phase);
            foreach (var (style, meter) in NestedMeters(config))
            {
                var expected = MeterSettings.DefaultsFor(style);
                Check(meter != null && meter.DpsStyle == style && meter.DpsBarHeight == expected.DpsBarHeight
                      && meter.DpsSize == expected.DpsSize,
                    "Disk recovery uses defaults for the owning window", new { style, phase });
                Check(meter != null && meter.DpsPos == new Vector2(711 + (int)style, 812)
                      && meter.DpsTextScale == 1.75f && meter.DpsHoldLast == (style == DpsMeterStyle.LMeter)
                      && meter.ShowDps == (style != DpsMeterStyle.HorizonOverlay) && meter.DpsOnlyInDuty,
                    "Disk recovery preserves each window's valid settings", new { style, phase });
            }
        }
    }

    private static void Profiles()
    {
        var source = new Configuration();
        var target = new Configuration();
        foreach (var style in Enum.GetValues<DpsMeterStyle>())
        {
            var meter = source.GetMeter(style);
            meter.DpsTextScale = 1 + (int)style;
            meter.DpsKagerouDeathHeading = KagerouDeathHeading.Deaths;
            meter.DpsKagerouDeathsAlign = TextAlign.Left;
            meter.DpsKagerouNameHeading = false;
            var destination = target.GetMeter(style);
            destination.ShowDps = true;
            destination.DpsOnlyInDuty = true;
            destination.DpsPos = new Vector2(777 + (int)style, 888);
            destination.DpsSize = new Vector2(333, 444);
        }
        var blob = Configuration.ValidateProfileBlob(source.SnapshotAppearance());
        Check(blob != null && target.ApplyAppearanceProfile(blob), "Appearance profiles accept separate meter settings");
        foreach (var style in Enum.GetValues<DpsMeterStyle>())
        {
            var meter = target.GetMeter(style);
            Check(meter.DpsTextScale == 1 + (int)style && !meter.DpsKagerouNameHeading
                  && meter.DpsKagerouDeathHeading == KagerouDeathHeading.Deaths && meter.DpsKagerouDeathsAlign == TextAlign.Left,
                "Profiles restore each meter appearance and heading choices", style);
            Check(meter.DpsStyle == style && meter.ShowDps && meter.DpsOnlyInDuty
                  && meter.DpsPos == new Vector2(777 + (int)style, 888) && meter.DpsSize == new Vector2(333, 444),
                "Profiles preserve each window identity visibility and placement", style);
        }
        source.GetMeter(DpsMeterStyle.LMeter).DpsTextScale = 5;
        Check(target.GetMeter(DpsMeterStyle.LMeter).DpsTextScale == 1, "Profiles do not share mutable meter settings");
        target.ResetAppearance();
        Check(Enum.GetValues<DpsMeterStyle>().Select(target.GetMeter).All(meter => meter.ShowDps
              && meter.DpsTextScale == 1 && meter.DpsKagerouDeathsAlign == TextAlign.Center),
            "Reset appearance restores each style without disabling windows");
        Check(target.ApplyAppearanceProfile("{\"DpsTextScale\":1.75,\"DpsHorizSkew\":21}"),
            "Legacy appearance profiles remain importable");
        Check(Enum.GetValues<DpsMeterStyle>().Select(target.GetMeter).All(meter => meter.DpsTextScale == 1.75f && meter.DpsHorizSkew == 21),
            "Legacy shared appearance settings populate every meter independently");
    }

    private static void Windows()
    {
        var config = new Configuration { Locked = true, ShowTimeline = false, ShowAlerts = false, DpsTextEffect = TextEffectStyle.Off };
        using var host = new BridgeHost(config);
        Apply(host);
        using var ui = new PluginUi(config, host, new ScaledFonts());
        var windows = (DpsWindow[])Field(ui, "meters");
        foreach (var window in windows) window.Meter.ShowDps = true;
        WindowSystem.AfterPreDraw = window =>
        {
            ImGui.Reset();
            ImGui.WindowPosition = window.Position!.Value;
            ImGui.WindowSize = window.Size!.Value;
            ImGui.Available = ImGui.WindowSize - new Vector2(16);
            ImGui.Cursor = ImGui.WindowPosition + new Vector2(8);
        };
        try
        {
            ui.Draw();
            Check(windows.All(window => window.IsOpen) && windows.Select(window => window.WindowName).Distinct().Count() == 3,
                "All three meter windows open together with separate window IDs");
            var bars = windows.Single(window => window.Meter.DpsStyle == DpsMeterStyle.LMeter);
            var horizon = windows.Single(window => window.Meter.DpsStyle == DpsMeterStyle.HorizonOverlay);
            var kagerou = windows.Single(window => window.Meter.DpsStyle == DpsMeterStyle.Kagerou);
            bars.Meter.ShowDps = false;
            horizon.Meter.DpsOnlyInDuty = true;
            Services.Condition[ConditionFlag.BoundByDuty] = false;
            ui.Draw();
            Check(!bars.IsOpen && !horizon.IsOpen && kagerou.IsOpen, "Each meter obeys its own enable and duty settings");
            bars.Meter.ShowDps = true;
            horizon.Meter.DpsOnlyInDuty = false;
            config.DpsHoldLast = false;
            bars.Meter.DpsHoldLast = false;
            Apply(host, ended: true);
            ui.Draw();
            Check(!bars.IsOpen && horizon.IsOpen && kagerou.IsOpen, "Holding the completed encounter is independent per meter");
            var ended = host.Dps;
            var healing = new DpsState { Show = true, Rows = ended.Rows, EncHps = 123 };
            Call(host, "ApplyLocalDps", healing);
            Check(ReferenceEquals(host.Dps, ended), "An enabled meter can retain the final pull regardless of retired shared settings");
            ui.Draw();
            Check(bars.IsOpen && ReferenceEquals(bars.CurrentDps, healing) && ReferenceEquals(horizon.CurrentDps, ended)
                  && ReferenceEquals(kagerou.CurrentDps, ended),
                "A live meter can show recovery healing while other meters retain the last pull");
            horizon.Meter.DpsHoldLast = kagerou.Meter.DpsHoldLast = false;
            Call(host, "ApplyLocalDps", healing);
            Check(ReferenceEquals(host.Dps, healing), "Disabling retention on every meter accepts the next healing encounter");
            kagerou.Meter.DpsKagerouInteractive = true;
            Apply(host);
            ui.Draw();
            Check((kagerou.Flags & ImGuiWindowFlags.NoInputs) == 0
                  && (bars.Flags & ImGuiWindowFlags.NoInputs) != 0 && (horizon.Flags & ImGuiWindowFlags.NoInputs) != 0,
                "Interactive Kagerou leaves the other meters click-through");
            var barsSize = bars.Meter.DpsSize;
            var horizonSize = horizon.Meter.DpsSize;
            ui.SetLocked(false);
            ImGui.Reset();
            ImGui.WindowPosition = new Vector2(350, 250);
            ImGui.WindowSize = new Vector2(420, 290);
            ImGui.Available = ImGui.WindowSize - new Vector2(16);
            kagerou.Draw();
            Check(kagerou.Meter.DpsPos == new Vector2(350, 250) && kagerou.Meter.DpsSize == new Vector2(420, 290)
                  && bars.Meter.DpsSize == barsSize && horizon.Meter.DpsSize == horizonSize,
                "Dragging and resizing one meter preserves the other placements");
            kagerou.Meter.ShowDps = false;
            ui.Draw();
            kagerou.Meter.ShowDps = true;
            ui.Draw();
            Check(kagerou.Meter.DpsSize == new Vector2(420, 290), "Disabling and restoring a meter keeps its size");
        }
        finally
        {
            WindowSystem.AfterPreDraw = null;
            ImGui.WindowPosition = Vector2.Zero;
            ImGui.WindowSize = new Vector2(1600, 900);
            ImGui.Available = ImGui.WindowSize;
            ImGui.Reset();
        }
    }

    private static void MenusAndHeadings()
    {
        var config = new Configuration { Locked = true, DpsTextEffect = TextEffectStyle.Off };
        using var host = new BridgeHost(config);
        Apply(host);
        using var ui = new PluginUi(config, host, new ScaledFonts());
        var settings = (ConfigWindow)Field(ui, "configWindow");
        var meter = config.GetMeter(DpsMeterStyle.Kagerou);
        var window = ((DpsWindow[])Field(ui, "meters")).Single(item => item.Meter == meter);
        ImGui.Controls.Clear();
        settings.Draw();
        Check(new[] { "Kagerou/Enable Kagerou", "Horizon/Enable Horizon", "LMeter/Enable LMeter" }.All(ImGui.Controls.Contains),
            "Each collapsible meter section contains its own enable checkbox");
        Check(ImGui.Controls.Contains("Horizon/horizon/Skew") && ImGui.Controls.Contains("LMeter/Corner rounding")
              && !ImGui.Controls.Any(value => value.StartsWith("Kagerou/") && (value.EndsWith("Skew") || value.EndsWith("Corner rounding"))),
            "Horizon and LMeter controls stay out of the Kagerou section");
        ImGui.ClickLabel = "Kagerou/Enable Kagerou";
        settings.Draw();
        Check(meter.ShowDps && config.GetMeter(DpsMeterStyle.LMeter).ShowDps && !config.GetMeter(DpsMeterStyle.HorizonOverlay).ShowDps,
            "The Kagerou enable checkbox keeps the other meter choices");
        ImGui.ComboInput = ("Kagerou/Deaths heading", (int)KagerouDeathHeading.Deaths);
        settings.Draw();
        Draw(window);
        Check(Text().Contains("Deaths") && !Text().Contains("Dead"), "The settings menu changes Dead to Deaths in the rendered header");
        meter.DpsKagerouInteractive = true;
        ImGui.ClickLabel = "##kagerouMenu";
        Draw(window);
        ImGui.ComboInput = ("Deaths heading", (int)KagerouDeathHeading.D);
        Draw(window);
        Draw(window);
        Check(Text().Contains("D") && !Text().Contains("Deaths"), "The overlay options menu changes the death heading too");
        ImGui.Popups.Clear();
        var starts = new List<float>();
        foreach (var alignment in new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right })
        {
            meter.DpsKagerouDeathsAlign = alignment;
            Draw(window);
            starts.Add(ImGui.Commands.First(command => command.Kind == "text" && command.Text == "7").A.X);
        }
        Check(starts[0] < starts[1] && starts[1] < starts[2]
              && Math.Abs(starts[1] - (starts[0] + starts[2]) * .5f) < .01f,
            "Death counts support left center and right alignment", starts);
        foreach (var label in new[] { "Name heading", "DPS heading", "D% heading", "H% heading", "Crit heading" })
        {
            ImGui.ClickLabel = "Kagerou/" + label;
            settings.Draw();
        }
        ImGui.ComboInput = ("Kagerou/Deaths heading", (int)KagerouDeathHeading.Hidden);
        settings.Draw();
        Draw(window);
        var hiddenHeight = ImGui.Cursor.Y;
        Check(!Text().Any(value => value is "Name" or "Dead" or "Deaths" or "D" or "D%" or "H%" or "Crit")
              && ImGui.Commands.Count(command => command.Text == "DPS") == 1
              && Text().Contains("Player") && Text().Contains("2000.00") && Text().Contains("7") && Text().Contains("25.0"),
            "Hidden headings leave names death counts and combat values visible");
        meter.DpsKagerouNameHeading = true;
        Draw(window);
        Check(ImGui.Cursor.Y > hiddenHeight, "Hiding every heading removes the empty heading row");
        ImGui.Controls.Clear();
        ImGui.Popups.Clear();
    }

    private static void Draw(DpsWindow window)
    {
        ImGui.Reset();
        ImGui.Available = new Vector2(600, 500);
        window.Draw();
    }

    private static string[] Text() => ImGui.Commands.Where(command => command.Kind == "text")
        .Select(command => command.Text!).ToArray();

    private static void Apply(BridgeHost host, bool ended = false) => Call(host, "ApplyLocalDps", new DpsState
    {
        Show = !ended, Ended = ended, HasDamage = true, Id = "test", Title = "Training", Duration = "01:00", EncDps = 2000,
        Rows = new[] { new DpsRow("Player", "BLM", 2000, 100, 0, true, 7) { Stats = new CombatStats { Crit = 25 } } },
    });
}
