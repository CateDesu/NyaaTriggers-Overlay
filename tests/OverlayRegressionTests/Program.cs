using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Ui;

internal static class Program
{
    internal const string Me = "{\"type\":\"ChangePrimaryPlayer\",\"charID\":268435457,\"charName\":\"Self Player\"}";
    internal const string Zone = "{\"type\":\"ChangeZone\",\"zoneID\":100,\"zoneName\":\"Arena\"}";
    internal const string Party = "{\"type\":\"PartyChanged\",\"party\":[{\"id\":\"10000001\",\"job\":31}]}";
    internal const string Combat = "{\"type\":\"InCombat\",\"inACTCombat\":true,\"inGameCombat\":true}";
    internal const string End = "{\"type\":\"InCombat\",\"inACTCombat\":false,\"inGameCombat\":false}";
    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static int checks;
    private static int failures;

    internal static object Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    internal static object? Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    internal static int Queued(object owner)
    {
        var inbox = Field(owner, "inbox");
        lock (Field(inbox, "gate")) return ((System.Collections.ICollection)Field(inbox, "messages")).Count;
    }
    internal static async Task Until(Func<bool> condition, Action? tick = null)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            tick?.Invoke();
            if (condition()) return;
            if (watch.Elapsed.TotalSeconds > 12) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
    internal static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    internal static string Damage(string id = "10000001", string amount = "27100000", string name = "Self Player")
    {
        var fields = new List<string> { "21", DateTimeOffset.UtcNow.ToString("O"), id, name, "07", "Hit", "40000010", "Target", "0003", amount };
        while (fields.Count < 24) fields.Add("");
        return JsonSerializer.Serialize(new { type = "LogLine", rawLine = string.Join('|', fields) });
    }
    private static IReadOnlyList<string> Ability(string id, string amount)
    {
        using var doc = JsonDocument.Parse(Damage(id, amount));
        return doc.RootElement.GetProperty("rawLine").GetString()!.Split('|');
    }
    internal static string Dps(string name = "Old Arena") => "{\"c\":\"dps\",\"show\":true,\"enc\":{\"t\":\"" + name + "\",\"d\":\"00:10\",\"dps\":1000},\"rows\":[[\"Player\",\"MCH\",1000,100,0,true,0]]}";
    internal static void Check(bool condition, string label, object? evidence = null)
    {
        checks++;
        if (!condition) failures++;
        Console.WriteLine(JsonSerializer.Serialize(new { verified = condition, label, evidence }, JsonOptions));
    }

    private static async Task SubscriptionOrder()
    {
        using var f = await Fixture.Open();
        await f.Burst(Me, Zone, Party, Combat, Damage());
        Check(f.Host.Dps.Rows.Count == 1 && f.Host.Dps.Rows[0].IsSelf,
            "Cold cached subscription preserves local player identity", f.Host.Dps.Rows);
        f.Config.DpsSoloOnly = true;
        f.Draw();
        var window = new DpsWindow(f.Config, f.Host, new ScaledFonts());
        var filtered = (IReadOnlyList<DpsRow>)Call(window, "FilterRows", f.Host.Dps.Rows)!;
        Check(filtered.Count == 1 && ImGui.Commands.Count(c => c.Kind == "text") > 1,
            "Solo mode draws the local player after the actual subscription order", ImGui.Commands.Select(c => c.Text));
        await f.Burst(Me, End);
        Check(f.Host.Dps.Rows[0].IsSelf, "Control a later identity event restores self detection");
        f.Draw();
        Check(ImGui.Commands.Count(c => c.Kind == "text") > 1, "Control restored identity draws the personal row in solo mode");
    }

    private static async Task NpcRoster()
    {
        using var f = await Fixture.Open();
        await f.Burst(Zone, Me, Party,
            "03|ts|40000123|NPC Fighter|13|90|0000", Damage("40000123", name: "NPC Fighter"));
        Check(!f.Host.Dps.Show, "Control a nonplayer spawn line does not create player damage");
        await f.Burst("{\"combatants\":[{\"ID\":1073742115,\"Job\":19,\"Type\":2,\"Name\":\"NPC Fighter\"}]}",
            Damage("40000123", name: "NPC Fighter"));
        Check(f.Host.Dps.Rows.Count == 0 && !f.Host.Dps.Show,
            "Nonplayer getCombatants entry cannot create a player DPS row", f.Host.Dps.Rows);
    }

    private static async Task HiddenUi()
    {
        using var f = await Fixture.Open();
        await f.Burst(Zone, Me, Party, Combat, Damage());
        ImGui.Reset();
        for (var batch = 0; batch < 12; batch++)
        {
            for (var i = 0; i < 50; i++) await f.Send(Damage());
            await Until(() => Queued(f.Meter) == 50);
            f.Tick();
        }
        var beforeEnd = Combatants(Field(f.Meter, "engine"));
        var damage = beforeEnd.Values.Cast<object>().Sum(actor => (long)Property(actor, "Damage"));
        await f.Send(End);
        await Until(() => f.Host.Dps.Ended, f.Tick);
        Check(Queued(f.Meter) == 0 && (long)Field(f.Meter, "droppedMessages") == 0,
            "Framework callbacks drain six hundred hits while drawing stays suppressed");
        Check(f.Host.Dps.Ended && !f.Host.Dps.Title.Contains("incomplete") && damage == 6010000,
            "Hidden overlay counts every hit without drawing", new { f.Host.Dps.Title, f.Host.Dps.EncDps, damage });
        Check(ImGui.Commands.Count == 0 && Services.Framework.Subscribers == 1,
            "The production plugin subscribes a heartbeat independent of rendering");
        var pendingTick = Services.Framework.Capture()!;
        f.Dispose();
        pendingTick();
        Check(Services.Framework.Subscribers == 0 && f.Store.UiBuilder.DrawSubscribers == 0,
            "Unload detaches both callbacks and tolerates a captured late heartbeat");
    }

    private static async Task DelayedFight()
    {
        using var f = await Fixture.Open();
        await f.Burst(Zone, Me, Party);
        var watch = Stopwatch.StartNew();
        await f.Send(Combat);
        await f.Send(Damage());
        await Task.Delay(1500);
        await f.Send(Damage());
        await f.Send(End);
        await Until(() => Queued(f.Meter) == 4);
        f.Draw();
        Check(watch.Elapsed.TotalSeconds > 1.5 && f.Host.Dps.Duration == "00:01" && f.Host.Dps.EncDps is > 10000 and < 15000,
            "Queued combat events retain their receipt duration and damage rate",
            new { elapsedSeconds = watch.Elapsed.TotalSeconds, f.Host.Dps.Duration, f.Host.Dps.EncDps });
        await f.Burst(Combat, Damage());
        await Task.Delay(1500);
        await f.Burst(Damage(), End);
        Check(f.Host.Dps.Duration == "00:01" && f.Host.Dps.EncDps < 15000,
            "Control processing the same events as they arrive preserves elapsed time",
            new { f.Host.Dps.Duration, f.Host.Dps.EncDps });
    }

    private static async Task EndpointRestart()
    {
        using var f = await Fixture.Open();
        await f.Burst(Zone, Me, Party, Combat, Damage());
        var endpoint = f.Config.IinactEndpoint;
        f.Config.IinactEndpoint = $"ws://127.0.0.1:{Port()}/unavailable";
        f.Host.RestartStandalone();
        for (var i = 0; i < 200; i++) { f.Draw(); await Task.Delay(5); }
        var engine = (MeterEngine)Field(f.Meter, "engine");
        Check(!engine.HasLiveEncounter && !f.Host.Dps.Show && f.Host.Dps.Rows.Count == 0,
            "Endpoint restart retires rows before replacing the engine",
            new { engine.HasLiveEncounter, f.Host.Dps.Show, f.Host.Dps.EncDps, status = f.Host.StandaloneStatusText });
        f.Config.StandaloneMeter = false;
        f.Draw();
        Check(!f.Host.Dps.Show && f.Host.Dps.Rows.Count == 0, "Control disabling standalone clears the stale endpoint result");
        f.Config.StandaloneMeter = true;
        f.Config.IinactEndpoint = endpoint;
        f.Host.RestartStandalone();
        f.Tick();
        await f.Accept();
        await f.Burst(Me, Zone, Party, Combat, Damage());
        Check(f.Host.Dps.Title == "Arena" && f.Host.Dps.Rows.Single().IsSelf,
            "A cold engine after endpoint restart learns the same cached zone without losing identity");
    }

    private static void ProfileAndGeometry()
    {
        var config = new Configuration { IinactEndpoint = "wss://feed.example/ws?token=PRIVATE_TEST_TOKEN", Port = 30000 };
        var export = config.SnapshotAppearance();
        Check(!export.Contains("PRIVATE_TEST_TOKEN") && !export.Contains("IinactEndpoint") && !export.Contains("TimelinePos") && !export.Contains("Port"),
            "Appearance export excludes connection credentials and placement",
            new { endpointIncluded = export.Contains("IinactEndpoint"), portIncluded = export.Contains("Port"), placementIncluded = export.Contains("TimelinePos") });
        var clean = new Configuration();
        clean.ApplyAppearanceProfile(export);
        Check(clean.IinactEndpoint == new Configuration().IinactEndpoint,
            "Control profile apply excludes the same connection settings that export reveals");
        const string input = "{\"DpsBarHeight\":3.4e38,\"DpsTextScale\":6}";
        var blob = Configuration.ValidateProfileBlob(input);
        config.ApplyAppearanceProfile(blob!);
        config.Locked = true;
        using var host = new BridgeHost(config);
        Call(host, "Apply", Dps());
        var window = new DpsWindow(config, host, new ScaledFonts());
        ImGui.Reset();
        window.Draw();
        Check(blob != null && float.IsFinite(config.DpsBarHeight) && ImGui.Commands.All(c => float.IsFinite(c.A.Y) && float.IsFinite(c.B.Y)),
            "Extreme finite profile values produce bounded drawing coordinates",
            new { config.DpsBarHeight, config.DpsTextScale, badCommands = ImGui.Commands.Count(c => !float.IsFinite(c.A.Y) || !float.IsFinite(c.B.Y)) });
        config.ResetAppearance();
        ImGui.Reset();
        window.Draw();
        Check(ImGui.Commands.All(c => float.IsFinite(c.A.Y) && float.IsFinite(c.B.Y)), "Control default appearance emits finite coordinates");
    }

    private static void UnboundedAndSelf()
    {
        var small = new MeterEngine(() => 0);
        small.SetMe(0x10000001);
        small.NoteJob(0x10000001, 31);
        small.Process(Ability("10000001", "00010000"));
        for (var i = 0; i < 23; i++)
        {
            var id = 0x10001000 + i;
            small.NoteJob(id, 31);
            small.Process(Ability(id.ToString("X"), "27100000"));
        }
        Check(small.LiveSnapshot()!.Rows.Any(row => row.IsSelf),
            "Control the local player at rank twenty four remains available");
        small.NoteJob(0x10002000, 31);
        small.Process(Ability("10002000", "27100000"));
        Check(small.LiveSnapshot()!.Rows.Single(row => row.IsSelf).Rank == 25,
            "The local player at rank twenty five survives snapshot selection with the correct rank");
        var engine = new MeterEngine(() => 0);
        engine.SetMe(0x10000001);
        engine.NoteJob(0x10000001, 31);
        engine.Process(Ability("10000001", "00010000"));
        for (var i = 0; i < 5000; i++)
        {
            var id = 0x10001000 + i;
            engine.NoteJob(id, 31);
            engine.Process(Ability(id.ToString("X"), "27100000"));
        }
        var current = Field(engine, "current");
        var combatants = (System.Collections.IDictionary)current.GetType().GetProperty("Combatants", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;
        var snap = engine.LiveSnapshot()!;
        Check(combatants.Count <= 1024 && snap.Rows.Count == 25 && snap.EncDps == 50000001 && snap.Title.Contains("limited actors"),
            "Actor retention is bounded while the encounter total includes evicted actors", new { retained = combatants.Count, returned = snap.Rows.Count });
        Check(snap.Rows.Single(r => r.IsSelf).EncDps == 1, "Actor pressure preserves the local player total");
        engine.Process(Ability("10000001", "423F400F"));
        Check(engine.LiveSnapshot()!.Rows.Any(r => r.IsSelf), "Control a high ranking local player survives the same cap");
        engine.FeedLost();
        Check(!engine.HasLiveEncounter, "Control explicit feed loss releases the encounter");
    }

    private static async Task LateHandshake()
    {
        using var host = new BridgeHost(new Configuration { Port = Port() });
        host.Start();
        var server = Field(host, "server");
        var port = (int)Field(server, "port");
        using var old = new TcpClient();
        await old.ConnectAsync(IPAddress.Loopback, port);
        await Until(() => ((System.Collections.ICollection)Field(server, "sessions")).Count == 1);
        using (var newer = new ClientWebSocket())
        {
            await newer.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
            await Until(() => (long)Field(host, "currentSession") > 0);
            newer.Abort();
        }
        await Until(() => !host.IsConnected);
        host.Update();
        var request = $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";
        await old.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(old.GetStream());
        await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
        host.Update();
        Check(!host.IsConnected && !host.ClockRunning,
            "A delayed older handshake stays rejected after the newer peer disconnects");
        using var fresh = new ClientWebSocket();
        await fresh.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
        await Send(fresh, "{\"c\":\"tick\",\"t\":42}");
        await Until(() => host.ClockRunning, host.Update);
        Check(host.Clock >= 42, "Control a newer fresh connection updates the clock");
        fresh.Abort();
    }

    internal static Task Send(ClientWebSocket client, string message)
        => client.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task BridgeInputs()
    {
        var config = new Configuration { Port = Port(), Locked = true, DpsHoldLast = true };
        using var host = new BridgeHost(config);
        host.Start();
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{config.Port}"), CancellationToken.None);
        await Until(() => Queued(host) > 0);
        host.Update();
        await Send(client, "{\"c\":\"tick\",\"t\":1e999}");
        await Until(() => Queued(host) == 1);
        host.Update();
        Check(!host.ClockRunning && double.IsFinite(host.Clock), "Overflowing clock value is refused");
        await Send(client, "{\"c\":\"dps\",\"show\":true,\"enc\":{\"dps\":1e999},\"rows\":[[\"Player\",\"MCH\",1e999,1e999]]}");
        await Until(() => Queued(host) == 1);
        host.Update();
        Check(!host.Dps.Show && double.IsFinite(host.Dps.EncDps), "Overflowing encounter DPS cannot replace live state");
        await Send(client, Dps());
        await Until(() => host.Dps.Title == "Old Arena", host.Update);
        await Send(client, "{\"c\":\"clear\"}");
        await Send(client, "{\"c\":\"dps\",\"show\":false}");
        await Until(() => host.Dps.Ended, host.Update);
        Check(host.Dps.Title == "Old Arena" && host.Dps.Rows.Count == 1,
            "Control a same session wipe keeps its own final rows");
        client.Abort();
        await Until(() => !host.IsConnected);
        host.Update();
        using var fresh = new ClientWebSocket();
        await fresh.ConnectAsync(new Uri($"ws://127.0.0.1:{config.Port}"), CancellationToken.None);
        await Send(fresh, "{\"c\":\"dps\",\"show\":false}");
        await Until(() => host.Dps.Ended, host.Update);
        Check(host.Dps.Rows.Count == 0 && host.Dps.Title.Length == 0,
            "A new program session cannot revive the previous session final rows",
            new { host.Dps.Title, host.Dps.EncDps });
        fresh.Abort();
    }

    private static void BindRetry()
    {
        var port = Port();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        var config = new Configuration { Port = port };
        using var host = new BridgeHost(config);
        using var ui = new PluginUi(config, host, new ScaledFonts());
        host.Start();
        blocker.Stop();
        var window = new ConfigWindow(config, host, ui);
        ImGui.Buttons.Clear();
        Call(window, "DrawLink");
        Check(host.LastError != null && ImGui.Buttons.Any(b => b.Label == "Apply" && !b.Disabled),
            "A failed listener enables same port retry", new { host.LastError });
        ImGui.ClickLabel = "Apply";
        Call(window, "DrawLink");
        Check(host.LastError == null, "Clicking Apply binds the same port after the conflict clears");
    }

    private static void MultilineAlerts()
    {
        var config = new Configuration
        {
            Locked = true, AlertsTextEffect = TextEffectStyle.Off, AlertsAnimate = false,
            AlertsAlarmScale = 1, AlertsLifeline = false, AlertsSeverityTint = false,
            AlertOrder = AlertOrder.OldestFirst, AlertsWrap = true, AlertsAlign = TextAlign.Left
        };
        using var host = new BridgeHost(config);
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"First\\nSecond\",\"ttl\":10}");
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Next\",\"ttl\":10}");
        var window = new AlertsWindow(config, host, new ScaledFonts());
        ImGui.Reset();
        window.Draw();
        var first = ImGui.Commands.Single(c => c.Text == "First Second");
        var next = ImGui.Commands.Single(c => c.Text == "Next");
        Check(next.A.Y >= first.A.Y + 16 && !host.Alerts[0].Text.Contains('\n'),
            "The bridge still flattens newlines before callout layout",
            new { host.Alerts[0].Text, firstY = first.A.Y, nextY = next.A.Y });
        Call(host, "Apply", "{\"c\":\"clear\"}");
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"First\",\"ttl\":10}");
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Next\",\"ttl\":10}");
        ImGui.Reset(); window.Draw();
        first = ImGui.Commands.Single(c => c.Text == "First");
        next = ImGui.Commands.Single(c => c.Text == "Next");
        Check(next.A.Y >= first.A.Y + 16, "Control single line callouts occupy separate rows");
    }

    private static async Task DelayedAlert()
    {
        using var host = new BridgeHost(new Configuration { Port = Port() });
        host.Start();
        var server = Field(host, "server");
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{Field(server, "port")}"), CancellationToken.None);
        await Until(() => Queued(host) > 0);
        host.Update();
        await Send(client, "{\"c\":\"tick\",\"t\":10}");
        await Send(client, "{\"c\":\"alert\",\"text\":\"Outdated\",\"ttl\":1}");
        await Until(() => Queued(host) == 2);
        await Task.Delay(1500);
        host.Update();
        Check(host.Alerts.Count == 0, "Expired queued alerts are not revived");
        Check(host.Clock >= 11.4, "A delayed tick advances from receipt time rather than restarting its clock", new { host.Clock });
        await Send(client, "{\"c\":\"alert\",\"text\":\"Current\",\"ttl\":1}");
        await Until(() => host.Alerts.Count == 1, host.Update);
        Check(host.Alerts[0].Text == "Current", "A fresh alert still appears immediately");
        await Task.Delay(1100);
        host.Update();
        Check(host.Alerts.Count == 0, "Fresh alert expires on its original receipt deadline");
        client.Abort();
    }

    private static object Property(object value, string name)
        => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static System.Collections.IDictionary Combatants(object engine, string encounter = "current")
        => (System.Collections.IDictionary)Property(Field(engine, encounter), "Combatants");

    private static async Task AdversarialTransitions()
    {
        using var f = await Fixture.Open();
        await f.Burst(Me, Zone, Party, Combat, Damage());
        var local = f.Host.Dps;
        f.Config.Port = Port();
        f.Host.Restart();
        Check(ReferenceEquals(local, f.Host.Dps), "Listener restart preserves standalone owned rows");
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{f.Config.Port}"), CancellationToken.None);
        await Send(client, Dps("Program owns DPS"));
        await Until(() => f.Host.Dps.Title == "Program owns DPS", f.Tick);
        f.Host.RestartStandalone();
        Check(f.Host.Dps.Title == "Program owns DPS", "Feed endpoint restart cannot clear program owned rows");
        client.Abort();
        await Until(() => !f.Host.IsConnected && Queued(f.Host) > 0);
        f.Config.Port = Port();
        f.Host.Restart();
        Check(f.Host.Dps.Rows.Count == 0, "Listener restart applies ownership cleanup before discarding a queued disconnect");
        using var next = new ClientWebSocket();
        await next.ConnectAsync(new Uri($"ws://127.0.0.1:{f.Config.Port}"), CancellationToken.None);
        await Send(next, "{\"c\":\"dps\",\"show\":false}");
        await Until(() => f.Host.Dps.Ended, f.Tick);
        Check(f.Host.Dps.Rows.Count == 0, "An end marker after rebinding cannot recover the old program fight");
        next.Abort();
    }

    private static void AdversarialProfiles()
    {
        var config = new Configuration { Port = Port(), IinactEndpoint = "wss://example/ws?token=PRIVATE_TEST_TOKEN" };
        var oldBlob = JsonSerializer.Serialize(config, new JsonSerializerOptions { IncludeFields = true });
        config.AppearanceProfiles["Old saved profile"] = oldBlob;
        using var host = new BridgeHost(config);
        using var ui = new PluginUi(config, host, new ScaledFonts());
        var settings = new ConfigWindow(config, host, ui);
        ImGui.ClickLabel = "Copy";
        Call(settings, "DrawProfiles");
        Check(!ImGui.Clipboard.Contains("PRIVATE_TEST_TOKEN") && !ImGui.Clipboard.Contains("IinactEndpoint"),
            "Copy sanitizes profiles saved before appearance export was restricted");
        var source = new Configuration
        {
            Port = 31000, IinactEndpoint = "wss://example/private", Locked = true,
            DpsTextScale = 2.3f, DpsBarHeight = 40, DpsSoloOnly = true,
            ColorAlarm = new Vector4(0.1f, 0.2f, 0.3f, 0.4f), TimelinePos = new(500, 600)
        };
        var target = new Configuration { Port = 32000, DpsPos = new(700, 800), DpsOnlyInDuty = true };
        target.ApplyAppearanceProfile(source.SnapshotAppearance());
        Check(target.DpsTextScale == 2.3f && target.DpsBarHeight == 40 && target.ColorAlarm == source.ColorAlarm && target.DpsSoloOnly,
            "Appearance round trip preserves colors and supported settings");
        Check(target.Port == 32000 && target.DpsPos == new Vector2(700, 800) && target.DpsOnlyInDuty && !target.Locked,
            "Appearance round trip preserves destination connection placement and visibility");
        foreach (var extreme in new[] { float.MaxValue, -float.MaxValue, float.NaN, float.PositiveInfinity })
        {
            foreach (var property in typeof(Configuration).GetProperties().Where(property => property.PropertyType == typeof(float)))
                property.SetValue(config, extreme);
            config.Sanitize();
            config.Locked = true;
            Call(host, "Apply", Dps());
            Call(host, "Apply", "{\"c\":\"timeline\",\"v\":[[10,\"Mechanic\"]]}");
            Call(host, "Apply", "{\"c\":\"tick\",\"t\":0}");
            Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Alarm\",\"sev\":\"alarm\",\"ttl\":10}");
            foreach (var style in Enum.GetValues<DpsMeterStyle>())
            {
                config.DpsStyle = style;
                ImGui.Reset();
                ui.Draw();
                Check(ImGui.Commands.Count > 0 && ImGui.Commands.All(c => float.IsFinite(c.A.X) && float.IsFinite(c.A.Y) && float.IsFinite(c.B.X) && float.IsFinite(c.B.Y)),
                    $"All windows emit finite coordinates with {extreme} settings and {style}");
            }
        }
    }

    private static void AdversarialActors()
    {
        var now = 0.0;
        var engine = new MeterEngine(() => now);
        engine.SetMe(0x10000001);
        engine.SetRoster(Enumerable.Range(1, 24).Select(id => new KeyValuePair<int, int>(0x10000000 + id, 31)));
        for (var i = 1; i <= 24; i++) engine.Process(Ability((0x10000000 + i).ToString("X"), "00010000"));
        for (var i = 0; i < 5000; i++)
        {
            var id = 0x10001000 + i;
            engine.NoteJob(id, 31);
            engine.Process(Ability(id.ToString("X"), "27100000"));
        }
        foreach (var encounter in new[] { "current", "view" })
        {
            var actors = Combatants(engine, encounter);
            Check(actors.Count == 1024 && Enumerable.Range(1, 24).All(id => actors.Contains(0x10000000 + id)),
                $"{encounter} actor pressure preserves every party member and stays bounded");
        }
        engine.Process(Ability("10001000", "27100000"));
        Check(engine.LiveSnapshot()!.EncDps == 50010024, "A returning evicted player still counts after its job lookup expires");
        engine.NoteJob(0x40009999, 19);
        engine.Process(Ability("40009999", "27100000"));
        Check(engine.LiveSnapshot()!.EncDps == 50010024, "Actor pressure cannot classify a returning NPC as a player");
        now = 121;
        engine.Process(Ability("10000001", "00010000"));
        Check(engine.LiveSnapshot()!.Rows.Count == 1 && engine.LiveSnapshot()!.EncDps == 1 && !engine.LiveSnapshot()!.Title.Contains("limited actors"),
            "Idle view reset retires the limited history and starts a clean display segment");
        var small = new MeterEngine(() => 0);
        small.SetMe(0x10000001);
        small.NoteJob(0x10000001, 31);
        small.Process(Ability("10000001", "00010000"));
        for (var i = 0; i < 24; i++)
        {
            var id = 0x10001000 + i;
            small.NoteJob(id, 31);
            small.Process(Ability(id.ToString("X"), "27100000"));
        }
        var state = (DpsState)typeof(StandaloneMeter).GetMethod("ToState", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { small.LiveSnapshot()!, false })!;
        var config = new Configuration { Locked = true, DpsSoloOnly = true, DpsTextEffect = TextEffectStyle.Off, DpsHorizShowIcons = false };
        using var host = new BridgeHost(config);
        Call(host, "ApplyLocalDps", state);
        var window = new DpsWindow(config, host, new ScaledFonts());
        foreach (var style in Enum.GetValues<DpsMeterStyle>())
        {
            config.DpsStyle = style;
            ImGui.Reset(); window.Draw();
            Check(ImGui.Commands.Any(c => c.Text?.StartsWith("25") == true), $"Solo {style} retains the actual rank beyond the top twenty four");
        }
        config.DpsSoloOnly = false;
        config.DpsSelfFirst = true;
        config.DpsMaxRows = 1;
        var selected = (IReadOnlyList<DpsRow>)Call(window, "FilterRows", state.Rows)!;
        Check(selected.Count == 1 && selected[0].IsSelf && selected[0].Rank == 25,
            "Self first retains the local player and actual rank with a one row cap");
    }

    private static async Task AdversarialScheduling()
    {
        using var f = await Fixture.Open();
        await f.Burst(Me, Zone, Party, Combat, Damage());
        using var drawing = new ManualResetEventSlim();
        using var releaseDraw = new ManualResetEventSlim();
        using var updating = new ManualResetEventSlim();
        Dalamud.Interface.Windowing.WindowSystem.BeforeDraw = () => { drawing.Set(); releaseDraw.Wait(TimeSpan.FromSeconds(5)); };
        try
        {
            var render = Task.Run(f.Store.UiBuilder.Render);
            Check(drawing.Wait(TimeSpan.FromSeconds(3)), "Render callback entered the production drawing path");
            var update = Task.Run(() => { updating.Set(); f.Tick(); });
            updating.Wait(TimeSpan.FromSeconds(3));
            Check(!update.Wait(50), "Framework processing cannot mutate state during a concurrent draw");
            releaseDraw.Set();
            await Task.WhenAll(render, update).WaitAsync(TimeSpan.FromSeconds(3));
            Check(true, "Drawing and processing both finish after releasing the state lock");
        }
        finally
        {
            releaseDraw.Set();
            Dalamud.Interface.Windowing.WindowSystem.BeforeDraw = null;
        }
    }

    private static void AdversarialWire()
    {
        using var host = new BridgeHost(new Configuration { AlertsCollapseDupes = true });
        Call(host, "Apply", "{\"c\":\"tick\",\"t\":42}");
        var clock = host.Clock;
        Call(host, "Apply", "{\"c\":\"tick\",\"t\":1e999}");
        Check(host.Clock >= clock && host.Clock < clock + 1, "Invalid tick preserves the running clock");
        Call(host, "Apply", Dps());
        var dps = host.Dps;
        Call(host, "Apply", "{\"c\":\"dps\",\"show\":true,\"enc\":{\"dps\":1e999}}");
        Check(ReferenceEquals(dps, host.Dps), "Invalid encounter total preserves the last valid DPS snapshot");
        Call(host, "Apply", "{\"c\":\"dps\",\"show\":true,\"rows\":[[\"Bad DPS\",\"MCH\",1e999,1],[\"Bad share\",\"MCH\",1,1e999],[\"Bad HPS\",\"MCH\",1,1,1e999],[\"Valid\",\"MCH\",100,-2,-1]]}");
        Check(host.Dps.Rows.Count == 1 && host.Dps.Rows[0].Name == "Valid" && host.Dps.Rows[0].Share == 0 && host.Dps.Rows[0].Hps == 0,
            "Each row numeric boundary rejects overflow and bounds shares");
        Call(host, "Apply", "{\"c\":\"timeline\",\"v\":[[1e999,\"Bad double\"],[1e39,\"Bad float\"],[42,\"Valid\"]]}");
        Check(host.Timeline.Count == 1 && host.Timeline[0].Time == 42, "Timeline refuses both double and float overflow");
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Bad TTL\",\"ttl\":1e999}");
        Check(host.Alerts.Count == 0, "Overflowing alert lifetime is refused");
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Repeat\",\"ttl\":1}");
        host.Alerts[0].ExpiresAt = Environment.TickCount64 - 1;
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Repeat\",\"ttl\":1}");
        host.Update();
        Check(host.Alerts.Count == 1 && host.Alerts[0].Count == 1, "Expired repeat cannot inflate the fresh alert counter");
        Call(host, "Apply", "{\"c\":\"alert\",\"text\":\"Repeat\",\"ttl\":2}");
        Check(host.Alerts.Count == 1 && host.Alerts[0].Count == 2, "A still live repeat continues to merge");
    }

    private static async Task RetainedMeterBoundaries()
    {
        using var f = await Fixture.Open();
        await f.Burst(Zone);
        foreach (var zone in new[]
        {
            "{\"type\":\"ChangeZone\",\"zoneID\":200,\"zoneName\":\"Other Arena\"}",
            "01|ts|C9|Another Arena",
        })
        {
            await f.Burst(Me, Party, Combat, Damage(), End);
            Check(f.Host.Dps.Ended && f.Host.Dps.EncDps == 10000, "Zone boundary test starts with a held pull");
            await f.Burst(zone, Combat, End);
            Check(f.Host.Dps.Rows.Count == 0 && ImGui.Commands.Count == 0,
                "A zone change followed by an empty encounter in one update clears the held pull", zone);
            f.Draw();
            Check(ImGui.Commands.Count == 0, "Another draw cannot restore the old zone result");
        }

        await f.Burst(Me, Party, Combat, Damage(), End);
        await f.Burst(Combat, Damage(amount: "13880000"), End, Combat, End);
        Check(f.Host.Dps.Ended && f.Host.Dps.EncDps == 5000,
            "An empty encounter in the same update retains the newest completed pull");
        await f.Burst("01|ts|CA|Final Arena", Me, Party, Combat, Damage(), End, Combat, End);
        Check(f.Host.Dps.Ended && f.Host.Dps.Title == "Final Arena" && f.Host.Dps.EncDps == 10000,
            "An encounter after a zone change survives a later empty encounter in the same update");
        await f.Burst(Combat, Damage(), End, "01|ts|CB|Empty Arena");
        Check(f.Host.Dps.Rows.Count == 0 && ImGui.Commands.Count == 0,
            "A zone change after a completed encounter in the same update clears its result");
    }

    private static void RetainedMeterMigration()
    {
        Check(new Configuration().DpsHoldLast, "New settings retain the last pull by default");
        var config = new Configuration { Port = Port(), Version = 4, DpsHoldLast = false };
        var store = new TestConfigStore { Config = config };
        using (var plugin = new Plugin(store))
        {
            Check(config.Version == 5 && config.DpsHoldLast, "Existing settings enable the retained meter on upgrade");
            config.DpsHoldLast = false;
        }

        using (var plugin = new Plugin(store))
        {
            Check(!config.DpsHoldLast, "Disabling the retained meter after upgrading survives reload");
        }
    }

    private static async Task<int> Main(string[] args)
    {
        Check(Environment.GetEnvironmentVariable("DISPLAY") == null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null,
            "Headless environment");
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("subscription", SubscriptionOrder), ("npc", NpcRoster), ("hidden-ui", HiddenUi),
            ("delayed-fight", DelayedFight), ("endpoint", EndpointRestart),
            ("retained-meter", () => { RetainedMeterMigration(); return Task.CompletedTask; }),
            ("retained-boundaries", RetainedMeterBoundaries),
            ("profile", () => { ProfileAndGeometry(); return Task.CompletedTask; }),
            ("bounds", () => { UnboundedAndSelf(); return Task.CompletedTask; }),
            ("late-handshake", LateHandshake), ("bridge-inputs", BridgeInputs),
            ("bind-retry", () => { BindRetry(); return Task.CompletedTask; }),
            ("multiline-alert", () => { MultilineAlerts(); return Task.CompletedTask; }),
            ("delayed-alert", DelayedAlert),
            ("adversarial-transitions", AdversarialTransitions),
            ("adversarial-profiles", () => { AdversarialProfiles(); return Task.CompletedTask; }),
            ("adversarial-actors", () => { AdversarialActors(); return Task.CompletedTask; }),
            ("adversarial-scheduling", AdversarialScheduling),
            ("adversarial-wire", () => { AdversarialWire(); return Task.CompletedTask; })
        };
        foreach (var test in cases.Where(test => args.Length == 0 || args.Contains(test.Name)))
        {
            Console.WriteLine("CASE " + test.Name);
            try { await test.Run(); }
            catch (Exception ex) { Check(false, test.Name + " harness exception", ex.ToString()); }
        }
        Console.WriteLine($"{checks - failures} verified, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}

internal sealed class Fixture : IDisposable
{
    private bool disposed;
    private readonly HttpListener listener = new();
    private WebSocket socket = null!;
    internal readonly Configuration Config;
    internal readonly BridgeHost Host;
    private readonly PluginUi ui;
    private readonly Plugin plugin;
    internal readonly TestConfigStore Store;
    internal object Meter => Program.Field(Host, "standalone");

    private Fixture()
    {
        var port = Program.Port();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Config = new Configuration
        {
            Port = Program.Port(), StandaloneMeter = true, IinactEndpoint = $"ws://127.0.0.1:{port}/ws/",
            Locked = true, DpsHoldLast = true, ShowAlerts = false, ShowTimeline = false, DpsTextEffect = TextEffectStyle.Off
        };
        Store = new TestConfigStore { Config = Config };
        plugin = new Plugin(Store);
        Host = (BridgeHost)Program.Field(plugin, "bridge");
        ui = (PluginUi)Program.Field(plugin, "ui");
    }
    internal static async Task<Fixture> Open()
    {
        var f = new Fixture();
        try
        {
            f.Draw();
            await f.Accept();
            return f;
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }
    internal async Task Accept()
    {
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
        socket?.Dispose();
        socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        for (var i = 0; i < 2; i++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buffer = new byte[4096];
            WebSocketReceiveResult result;
            do { result = await socket.ReceiveAsync(buffer, timeout.Token); }
            while (!result.EndOfMessage);
        }
    }
    internal void Tick() => Services.Framework.Tick();
    internal void Draw() { Tick(); ImGui.Reset(); Store.UiBuilder.Render(); }
    internal Task Send(string message) => socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    internal async Task Burst(params string[] messages)
    {
        var count = Program.Queued(Meter);
        foreach (var message in messages) await Send(message);
        await Program.Until(() => Program.Queued(Meter) >= count + messages.Length);
        Draw();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        plugin.Dispose(); socket?.Dispose(); listener.Close();
    }
}
