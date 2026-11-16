using System.Net.WebSockets;
using System.Text.Json;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Ui;

internal static partial class Program
{
    private const string BareEnd = """{"c":"dps","show":false}""";
    private const string BareClear = """{"c":"clear"}""";
    private const string WipeClear = """{"c":"clear","keepDps":true}""";
    private const string FullClear = """{"c":"clear","keepDps":false}""";

    private static Configuration LegacyConfig()
    {
        var config = new Configuration { Port = FreePort(), Locked = true, ShowTimeline = false, ShowAlerts = false };
        foreach (var style in Enum.GetValues<DpsMeterStyle>())
        {
            var meter = config.GetMeter(style);
            meter.ShowDps = true;
            meter.DpsHoldLast = style != DpsMeterStyle.HorizonOverlay;
            meter.DpsTextEffect = TextEffectStyle.Off;
        }
        return config;
    }

    private static async Task ConnectProgram(ClientWebSocket client, Configuration config, BridgeHost host, PluginUi ui)
    {
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{config.Port}"), CancellationToken.None);
        await client.ReceiveAsync(new byte[4096], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Draw(ui);
        Check(host.IsConnected && Queued(host) == 0, "Program connection reset is applied before encounter frames");
    }

    private static async Task LegacyEndingsScenario()
    {
        foreach (var healing in new[] { true, false })
        {
            var kind = healing ? "healing" : "empty";
            var config = LegacyConfig();
            using var host = new BridgeHost(config);
            using var fonts = new ScaledFonts();
            using var ui = new PluginUi(config, host, fonts);
            var windows = (DpsWindow[])Field(ui, "meters");
            var retained = windows.Where(window => window.Meter.DpsHoldLast).ToArray();
            var unheld = windows.Single(window => !window.Meter.DpsHoldLast);
            host.Start();
            using var client = new ClientWebSocket();
            await ConnectProgram(client, config, host, ui);
            var damage = BridgeDps("Held damage");
            var next = healing
                ? """{"c":"dps","show":true,"enc":{"t":"healing","hps":500,"hasDamage":false},"rows":[["Healer","WHM",0,0,500,true,0]]}"""
                : """{"c":"dps","show":true,"enc":{"t":"empty","hasDamage":false},"rows":[]}""";

            await ProgramBurst(client, host, ui, damage, BareEnd);
            var held = host.Dps;
            var historyKey = held.HistoryKey;
            Check(held.Ended && held.HasDamage && host.DpsHistory.Count == 1,
                $"{kind}: damage ending enters history in the same update");
            await ProgramBurst(client, host, ui, next);
            Check(retained.All(window => ReferenceEquals(window.CurrentDps, held) && window.IsOpen)
                && unheld.CurrentDps.Title == kind && unheld.CurrentDps.Show && unheld.IsOpen == healing,
                $"{kind}: mixed meters show held damage and the latest live frame independently");
            await ProgramBurst(client, host, ui, BareEnd);
            Check(ReferenceEquals(host.Dps, held) && host.UnheldDps.Ended && host.UnheldDps.Title == kind
                && !host.UnheldDps.HasDamage && host.UnheldDps.Rows.Count == (healing ? 1 : 0)
                && host.UnheldDps.EncHps == (healing ? 500 : 0) && !unheld.IsOpen,
                $"{kind}: bare end finishes the latest unheld frame and retains damage");

            await ProgramBurst(client, host, ui, BareClear);
            Check(windows.All(window => !window.IsOpen && window.CurrentDps.Rows.Count == 0),
                $"{kind}: bare clear hides every meter before the legacy ending");
            await ProgramBurst(client, host, ui, BareEnd, BareEnd);
            Check(retained.All(window => window.IsOpen && window.CurrentDps.Title == held.Title
                    && window.CurrentDps.EncDps == held.EncDps)
                && ReferenceEquals(host.Dps.HistoryKey, historyKey)
                && host.UnheldDps.Ended && host.UnheldDps.Title == kind && !host.UnheldDps.HasDamage
                && host.UnheldDps.Rows.Count == (healing ? 1 : 0) && !unheld.IsOpen,
                $"{kind}: bare clear and repeated bare endings restore held damage while ending the latest unheld frame");
            await ProgramBurst(client, host, ui, WipeClear, BareEnd, BareClear, BareEnd);
            Check(host.Dps.Title == held.Title && host.UnheldDps.Title == kind
                && host.DpsHistory.Count == 1 && ReferenceEquals(host.DpsHistory[0].HistoryKey, historyKey),
                $"{kind}: repeated modern and legacy wipes preserve both snapshots and one history entry");

            var beforeFullClear = host.DpsHistory.Select(state => state.HistoryKey).ToArray();
            await ProgramBurst(client, host, ui, FullClear, BareClear, BareEnd, WipeClear, BareEnd);
            Check(windows.All(window => !window.IsOpen && window.CurrentDps.Rows.Count == 0
                    && window.CurrentDps.Title == string.Empty)
                && host.DpsHistory.Select(state => state.HistoryKey).SequenceEqual(beforeFullClear),
                $"{kind}: explicit full clear invalidates both fallbacks without deleting history");

            await ProgramBurst(client, host, ui, damage, BareEnd, next, BareEnd);
            var keys = host.DpsHistory.Select(state => state.HistoryKey).ToArray();
            client.Abort();
            await Until(() => !host.IsConnected && Queued(host) > 0);
            Draw(ui);
            Check(windows.All(window => !window.IsOpen && window.CurrentDps.Rows.Count == 0),
                $"{kind}: disconnect clears both meter states");
            using var replacement = new ClientWebSocket();
            await ConnectProgram(replacement, config, host, ui);
            await ProgramBurst(replacement, host, ui, BareClear, BareEnd, WipeClear, BareEnd);
            Check(windows.All(window => !window.IsOpen && window.CurrentDps.Rows.Count == 0
                    && window.CurrentDps.Title == string.Empty)
                && host.DpsHistory.Select(state => state.HistoryKey).SequenceEqual(keys),
                $"{kind}: a replacement connection cannot restore either disconnected snapshot");
            await ProgramBurst(replacement, host, ui, BridgeDps("Fresh damage"), BareClear, BareEnd);
            Check(host.Dps.Title == "Fresh damage" && host.UnheldDps.Title == "Fresh damage"
                && host.Dps.Ended && host.UnheldDps.Ended && host.DpsHistory.Count == keys.Length + 1,
                $"{kind}: new damage resumes normal legacy endings after disconnect");
            replacement.Abort();
        }
    }

    private static async Task LegacyHistoryScenario()
    {
        var config = LegacyConfig();
        using var host = new BridgeHost(config);
        using var fonts = new ScaledFonts();
        using var ui = new PluginUi(config, host, fonts);
        host.Start();
        using var client = new ClientWebSocket();
        await ConnectProgram(client, config, host, ui);
        for (var i = 0; i < 20; i++)
            await ProgramBurst(client, host, ui, BridgeDps($"Pull {i}"), BareEnd);
        var history = host.DpsHistory.ToArray();
        var keys = history.Select(state => state.HistoryKey).ToArray();
        Check(history.Length == 20 && keys.All(key => key != null) && keys.Distinct().Count() == 20,
            "Twenty distinct ID-less pulls fill history with distinct identities");
        var wipes = Enumerable.Range(0, 25).SelectMany(_ => new[] { BareClear, BareEnd }).ToArray();
        await ProgramBurst(client, host, ui, wipes);
        Check(host.DpsHistory.Select(state => state.HistoryKey).SequenceEqual(keys)
            && host.DpsHistory.Select(state => state.Title).SequenceEqual(history.Select(state => state.Title)),
            "Repeated ID-less bare wipes at capacity preserve every distinct history entry and its order");
        Check(ReferenceEquals(host.Dps.HistoryKey, keys[0]) && ReferenceEquals(host.UnheldDps.HistoryKey, keys[0]),
            "Restored ID-less snapshots retain the identity of their completed encounter");

        var beforeIdentical = host.DpsHistory.Select(state => state.HistoryKey).ToArray();
        var identical = BridgeDps("Identical pull");
        await ProgramBurst(client, host, ui, identical, BareClear, BareEnd);
        var firstKey = host.Dps.HistoryKey;
        await ProgramBurst(client, host, ui, identical, BareClear, BareEnd);
        var secondKey = host.Dps.HistoryKey;
        Check(firstKey != null && secondKey != null && !ReferenceEquals(firstKey, secondKey)
            && host.DpsHistory.Count == 20 && host.DpsHistory[0].Title == "Identical pull"
            && host.DpsHistory[1].Title == "Identical pull"
            && ReferenceEquals(host.DpsHistory[1].HistoryKey, firstKey)
            && host.DpsHistory.Skip(2).Select(state => state.HistoryKey).SequenceEqual(beforeIdentical.Take(18)),
            "Identical new ID-less pulls remain distinct and each evicts exactly one oldest encounter");
        var identicalKeys = host.DpsHistory.Select(state => state.HistoryKey).ToArray();
        await ProgramBurst(client, host, ui, BareClear, BareEnd, BareEnd);
        Check(host.DpsHistory.Select(state => state.HistoryKey).SequenceEqual(identicalKeys),
            "A restored identical pull cannot duplicate itself or merge with its identical predecessor");

        string Stable(string id, string title, double dps)
            => JsonSerializer.Serialize(new
            {
                c = "dps", show = false, enc = new { id, t = title, d = "00:10", dps },
                rows = new object[][] { new object[] { "Program Player", "MCH", dps, 100, 0, true, 0 } },
            });
        await ProgramBurst(client, host, ui, Stable("A", "Original A", 1000));
        var stableKey = host.Dps.HistoryKey;
        await ProgramBurst(client, host, ui, Stable("B", "Original B", 2000));
        var stableKeys = host.DpsHistory.Select(state => state.HistoryKey).ToArray();
        await ProgramBurst(client, host, ui, Stable("A", "Revised A", 3000), BareClear, BareEnd);
        Check(host.DpsHistory.Select(state => state.HistoryKey).SequenceEqual(stableKeys)
            && host.DpsHistory[0].Id == "B" && host.DpsHistory[1].Id == "A"
            && host.DpsHistory[1].Title == "Revised A" && host.DpsHistory[1].EncDps == 3000
            && ReferenceEquals(host.Dps.HistoryKey, stableKey) && host.UnheldDps.EncDps == 3000,
            "Stable ID revisions and legacy restoration update the original history slot without reordering");

        await ProgramBurst(client, host, ui, BridgeDps("Legacy live"), BridgeDps("Legacy final", 4000, show: false));
        var revisionKeys = host.DpsHistory.Select(state => state.HistoryKey).ToArray();
        await ProgramBurst(client, host, ui, BridgeDps("Legacy revised", 5000, show: false), BareClear, BareEnd);
        Check(host.DpsHistory.Select(state => state.HistoryKey).SequenceEqual(revisionKeys)
            && host.DpsHistory[0].Title == "Legacy revised" && host.DpsHistory[0].EncDps == 5000,
            "Consecutive complete ID-less finals still revise their existing history entry");
        client.Abort();
    }
}
