using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;
using NyaaTriggers.Plugin.Ui;

internal static class Program
{
    private static int passes;
    private static int failures;
    private const string Combat = "{\"type\":\"InCombat\",\"inACTCombat\":true,\"inGameCombat\":true}";
    private const string End = "{\"type\":\"InCombat\",\"inACTCombat\":false,\"inGameCombat\":false}";
    private const string Zone = "{\"type\":\"ChangeZone\",\"zoneID\":100,\"zoneName\":\"Headless Arena\"}";
    private const string Me = "{\"type\":\"ChangePrimaryPlayer\",\"charID\":268435457}";
    private const string Party = "{\"type\":\"PartyChanged\",\"party\":[{\"id\":\"10000001\",\"job\":31}]}";
    private const string Roster = "{\"combatants\":[{\"ID\":268435458,\"Job\":24}]}";

    private static void Check(bool condition, string label)
    {
        if (condition) passes++; else failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {label}");
    }

    private static object Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static int Queued(object owner)
    {
        var inbox = Field(owner, "inbox");
        lock (Field(inbox, "gate")) return ((System.Collections.ICollection)Field(inbox, "messages")).Count;
    }

    internal static async Task Until(Func<bool> condition, Action? update = null)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            update?.Invoke();
            if (condition()) return;
            if (watch.Elapsed.TotalSeconds > 12) throw new TimeoutException("Condition did not become true");
            await Task.Delay(5);
        }
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Hit(string id, string name, string amount, string flags = "0003", string target = "40000010")
    {
        var fields = new List<string> { "21", "ts", id, name, "07", "Hit", target, "Target", flags, amount };
        while (fields.Count < 24) fields.Add("");
        return string.Join('|', fields);
    }

    private static string Log(string raw) => JsonSerializer.Serialize(new { type = "LogLine", rawLine = raw });
    private static string Dot()
    {
        var fields = new List<string> { "24", "ts", "40000010", "Dummy", "DoT", "0A", "3E8" };
        while (fields.Count < 17) fields.Add("");
        fields.AddRange(new[] { "10000001", "Player One" });
        return string.Join('|', fields);
    }

    private static void Draw(PluginUi ui)
    {
        ImGui.Reset();
        ui.Update();
        ui.Draw();
    }

    private static void Capture(string label, BridgeHost host)
    {
        var dps = host.Dps;
        var snapshot = new
        {
            label, programConnected = host.IsConnected, standalone = host.StandaloneStatus.ToString(),
            dps.Show, dps.Ended, dps.Title, dps.Duration, dps.EncDps, dps.Rows,
            text = ImGui.Commands.Where(c => c.Kind == "text").Select(c => c.Text).Distinct().ToArray(),
            shapes = ImGui.Commands.Count(c => c.Kind != "text")
        };
        Console.WriteLine("SNAPSHOT " + JsonSerializer.Serialize(snapshot));
    }

    private static async Task Burst(Feed feed, BridgeHost host, PluginUi ui, params string[] messages)
    {
        var meter = Field(host, "standalone");
        if (Queued(meter) != 0) throw new Exception("Feed queue must be drained before the next batch");
        foreach (var message in messages) await feed.Send(message);
        await Until(() => Queued(meter) >= messages.Length);
        Draw(ui);
    }

    private static string BridgeDps(string title, double dps = 1234)
        => JsonSerializer.Serialize(new
        {
            c = "dps", show = true, enc = new { t = title, d = "00:10", dps },
            rows = new object[][] { new object[] { "Program Player", "MCH", dps, 100, 0, true, 0 } }
        });

    private static async Task SendProgram(ClientWebSocket client, string message)
    {
        await client.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task MainScenario()
    {
        using var feed = new Feed();
        var config = new Configuration
        {
            Port = FreePort(), StandaloneMeter = true, IinactEndpoint = feed.Endpoint,
            Locked = true, ShowTimeline = false, ShowAlerts = false,
            DpsHoldLast = true, DpsTextEffect = TextEffectStyle.Off,
            DpsShowDeaths = true, DpsRowsShowHps = true,
            DpsHorizCompact = true, DpsHorizShowIcons = false, DpsHorizMaxBarWidth = 400
        };
        using var host = new BridgeHost(config);
        using var ui = new PluginUi(config, host, new ScaledFonts());
        var window = (DpsWindow)Field(ui, "dps");
        host.Start();
        Draw(ui);
        await feed.Accept();
        await Until(() => host.StandaloneStatus == StandaloneState.Connected, () => Draw(ui));
        Check(!host.IsConnected, "No NyaaTriggers program connection is present");
        Check(!window.IsOpen, "Locked idle DPS window starts hidden");
        Check(feed.Requests[0].Contains("subscribe") && feed.Requests[1].Contains("getCombatants"),
            "Real WebSocket client subscribes and fetches combatants");
        using (var subscription = JsonDocument.Parse(feed.Requests[0]))
        {
            var events = subscription.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ToArray();
            Check(events.SequenceEqual(new[] { "LogLine", "ChangePrimaryPlayer", "ChangeZone", "PartyChanged", "InCombat" }),
                "Subscription includes all five required feed events");
        }
        await Burst(feed, host, ui, Zone, Me, Party, Roster,
            Log(Hit("40000050", "Unrelated Enemy", "4E200000")));
        Check(!host.Dps.Show, "Unrelated enemy damage does not open the meter");

        await Burst(feed, host, ui, Combat,
            Log(Hit("10000001", "Player One", "4E200000")),
            JsonSerializer.Serialize(new { type = "LogLine", line = Hit("10000002", "Player Two", "27100000").Split('|') }),
            "03|ts|40000052|Pet|00|90|10000001",
            JsonSerializer.Serialize(new { type = "broadcast", msgtype = "LogLine", msg = Hit("40000052", "Pet", "13880000") }),
            JsonSerializer.Serialize(new { type = "LogLine", raw_line = Dot() }),
            Log(Hit("10000002", "Player Two", "01F40000", "0004", "10000001")),
            "25|ts|10000002|Player Two");
        Capture("standalone live", host);
        Check(host.Dps.Show && host.Dps.Rows.Count == 2 && window.IsOpen, "Live feed opens the real DPS window logic with two rows");
        Check(host.Dps.EncDps == 36000, "20000 plus 10000 plus 5000 pet plus 1000 DoT equals 36000 DPS at the one second floor");
        Check(host.Dps.Rows[0].Dps == 26000 && host.Dps.Rows[1].Dps == 10000, "Pet damage and DoT merge into the correct owner and rows sort by DPS");
        Check(host.Dps.Rows[0].IsSelf && host.Dps.Rows[0].Job == "MCH" && host.Dps.Rows[1].Job == "WHM",
            "Local player identity and jobs resolve from party and getCombatants without player spawn lines");
        Check(host.Dps.Rows[1].Hps == 500 && host.Dps.Rows[1].Deaths == 1, "Healing and deaths reach the window state");
        Check(Math.Abs(host.Dps.Rows.Sum(r => r.Share) - 100) < 0.01, "Damage shares sum to 100 percent");
        Check(ImGui.Commands.Any(c => c.Text?.Contains("Player One") == true), "Draw commands contain the live player name rather than preview rows");

        foreach (var style in Enum.GetValues<DpsMeterStyle>())
        {
            config.DpsStyle = style;
            ImGui.Reset();
            window.PreDraw();
            window.Draw();
            Capture("draw " + style, host);
            Check(ImGui.Commands.Any(c => c.Text?.Contains("26.0k") == true), style + " emits the expected 26000 DPS text");
            Check(ImGui.Commands.Any(c => c.Kind != "text"), style + " emits meter geometry");
            Check(ImGui.Commands.All(c => float.IsFinite(c.A.X) && float.IsFinite(c.A.Y) && float.IsFinite(c.B.X) && float.IsFinite(c.B.Y)),
                style + " emits finite draw coordinates");
        }
        config.DpsStyle = DpsMeterStyle.Bars;
        var live = host.Dps;
        await Until(() => !ReferenceEquals(live, host.Dps), () => Draw(ui));
        Capture("live clock update", host);
        Check(host.Dps.EncDps > 0 && host.Dps.EncDps < 36000, "Live publication advances on the real clock and lowers DPS without new damage");
        await Burst(feed, host, ui, End);
        Capture("normal end", host);
        Check(host.Dps.Ended && !host.Dps.Show && host.Dps.EncDps == 36000, "Final DPS removes the inactive combat tail");
        Check(window.HasHeldContent && window.IsOpen, "Hold last keeps the final rows visible");

        config.DpsOnlyInCombat = true;
        Services.Condition[ConditionFlag.InCombat] = false;
        Draw(ui);
        Check(window.IsOpen, "Held encounter survives the only in combat filter");
        config.DpsHoldLast = false;
        Draw(ui);
        Check(!window.IsOpen && ImGui.Commands.Count == 0, "Disabling hold last hides an ended encounter");
        config.DpsHoldLast = true;
        config.DpsOnlyInDuty = true;
        Draw(ui);
        Check(!window.IsOpen, "Duty filter still hides held content outside a duty");
        Services.Condition[ConditionFlag.BoundByDuty] = true;
        Draw(ui);
        Check(window.IsOpen, "Held content is visible inside a duty");
        Services.Condition[ConditionFlag.WatchingCutscene] = true;
        Draw(ui);
        Check(!window.IsOpen, "Cutscenes suppress the DPS window");
        Services.Condition[ConditionFlag.WatchingCutscene] = false;
        config.DpsOnlyInDuty = config.DpsOnlyInCombat = false;

        await Burst(feed, host, ui, Zone);
        Check(host.Dps.Ended && window.IsOpen, "Same zone subscribe replay preserves held final rows");
        await Burst(feed, host, ui, "{\"type\":\"ChangeZone\",\"zoneID\":101,\"zoneName\":\"Next Arena\"}");
        Check(!host.Dps.Ended && host.Dps.Rows.Count == 0 && !window.IsOpen, "Zone change clears final rows and hides the window");

        await Burst(feed, host, ui, Combat, End);
        Capture("empty fight after zone clear", host);
        Check(!window.IsOpen && ImGui.Commands.Count == 0,
            "An empty fight after zoning cannot resurrect held rows from the previous zone");

        await Burst(feed, host, ui, Me, Party, Combat, Log(Hit("10000001", "Player One", "27100000")), End);
        Capture("short encounter without live draw", host);
        Check(host.Dps.Ended && host.Dps.EncDps == 10000 && window.IsOpen, "A complete fight between draws still displays its final 10000 DPS");
        await Burst(feed, host, ui, Combat, Log(Hit("10000001", "Player One", "13880000")), "33|ts|10000001|4000000F");
        Check(host.Dps.Ended && host.Dps.EncDps == 5000 && window.IsOpen, "Wipe preserves the current fight final values without old damage");

        await Burst(feed, host, ui, Combat, Log(Hit("10000001", "Player One", "27100000")));
        await feed.Socket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test disconnect", CancellationToken.None);
        await Until(() => host.Dps.Ended, () => Draw(ui));
        Check(host.Dps.EncDps == 10000 && window.IsOpen, "Real socket loss finalizes and holds the interrupted fight");
        var reconnect = feed.Accept();
        await Until(() => reconnect.IsCompleted, () => Draw(ui));
        await reconnect;
        await Burst(feed, host, ui,
            "{\"type\":\"ChangeZone\",\"zoneID\":101,\"zoneName\":\"Next Arena\"}", Me, Party, Combat,
            Log(Hit("10000001", "Player One", "13880000")));
        Capture("real reconnect", host);
        Check(host.Dps.Show && host.Dps.EncDps == 5000 && host.Dps.Title == "Next Arena",
            "Automatic WebSocket reconnect resubscribes and starts a fresh 5000 DPS encounter");
        Check(feed.Requests.Count == 4, "Reconnect repeats subscription and getCombatants");

        config.StandaloneMeter = false;
        Draw(ui);
        Check(host.StandaloneStatus == StandaloneState.Off && host.Dps.Rows.Count == 0 && !window.IsOpen,
            "Disabling standalone stops its feed and clears its rows");

        using var program = new ClientWebSocket();
        await program.ConnectAsync(new Uri($"ws://127.0.0.1:{config.Port}"), CancellationToken.None);
        await SendProgram(program, BridgeDps("Program Arena"));
        await Until(() => host.Dps.Title == "Program Arena", () => Draw(ui));
        Capture("program bridge", host);
        Check(host.IsConnected && window.IsOpen && host.Dps.EncDps == 1234, "Program WebSocket DPS reaches the same visible window headless");
        await SendProgram(program, "{\"c\":\"dps\",\"show\":false}");
        await Until(() => host.Dps.Ended, () => Draw(ui));
        Check(window.IsOpen && host.Dps.EncDps == 1234, "Program end marker preserves its final rows");
        await SendProgram(program, "{\"c\":\"clear\"}");
        await Until(() => host.Dps.Rows.Count == 0, () => Draw(ui));
        Check(!window.IsOpen, "Program clear hides the meter");
        await SendProgram(program, "{\"c\":\"dps\",\"show\":false}");
        await Until(() => host.Dps.Ended, () => Draw(ui));
        Check(window.IsOpen && host.Dps.EncDps == 1234, "Program wipe end after clear retains the final fight");
        await SendProgram(program, "{\"c\":\"clear\"}");
        await Until(() => host.Dps.Rows.Count == 0, () => Draw(ui));
        await SendProgram(program, "{\"c\":\"dps\",\"show\":true,\"enc\":{\"t\":\"Empty Arena\",\"d\":\"00:01\",\"dps\":0},\"rows\":[]}");
        await SendProgram(program, "{\"c\":\"dps\",\"show\":false}");
        await Until(() => host.Dps.Ended, () => Draw(ui));
        Capture("empty program encounter after clear", host);
        Check(host.Dps.Rows.Count == 0 && !window.IsOpen, "Empty program encounter cannot restore the previous fight");
        program.Abort();
    }

    private static async Task TakeoverScenario(bool endsOnArrival = false, bool idle = false)
    {
        using var feed = new Feed();
        var config = new Configuration
        {
            Port = FreePort(), StandaloneMeter = true, IinactEndpoint = feed.Endpoint,
            Locked = true, ShowTimeline = false, ShowAlerts = false, DpsTextEffect = TextEffectStyle.Off,
            DpsHoldLast = true
        };
        using var host = new BridgeHost(config);
        using var ui = new PluginUi(config, host, new ScaledFonts());
        host.Start();
        Draw(ui);
        await feed.Accept();
        await Burst(feed, host, ui, Zone, Me, Party, Combat, Log(Hit("10000001", "Player One", "27100000")));
        using var program = new ClientWebSocket();
        await program.ConnectAsync(new Uri($"ws://127.0.0.1:{config.Port}"), CancellationToken.None);
        if (!idle) await SendProgram(program, BridgeDps("Takeover Arena"));
        if (endsOnArrival) await SendProgram(program, "{\"c\":\"dps\",\"show\":false}");
        await Until(() => Queued(host) >= (idle ? 1 : endsOnArrival ? 3 : 2));
        Draw(ui);
        Capture(idle ? "idle program takeover" : endsOnArrival ? "final program frame during takeover" : "first program frame during takeover", host);
        Check(host.StandaloneStatus == StandaloneState.Paused, "Program connection pauses the standalone client");
        if (idle)
        {
            Check(!host.Dps.Show && host.Dps.Rows.Count == 0 && ImGui.Commands.Count == 0,
                "Idle program takeover clears the standalone rows");
        }
        else
        {
            Check((endsOnArrival ? host.Dps.Ended : host.Dps.Show) && host.Dps.Title == "Takeover Arena",
                endsOnArrival ? "Final program DPS frame survives takeover from standalone" : "First program DPS frame survives takeover from standalone");
        }
        if (endsOnArrival)
        {
            for (var i = 0; i < 60; i++) Draw(ui);
            Capture("sixty idle draws after final frame", host);
            Check(host.Dps.Ended && host.Dps.EncDps == 1234 && ImGui.Commands.Count > 0,
                "Held program final frame survives sixty idle draws");
        }
        await SendProgram(program, BridgeDps("Takeover Arena"));
        await Until(() => host.Dps.Show && host.Dps.Title == "Takeover Arena", () => Draw(ui));
        Check(host.Dps.Show && host.Dps.EncDps == 1234, "Next program DPS frame recovers the takeover display");
        program.Abort();
    }

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Headless DPS tests using production source and a recording UI adapter");
        Check(Environment.GetEnvironmentVariable("DISPLAY") == null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null,
            "DISPLAY and WAYLAND_DISPLAY are unset");
        try
        {
            if (args.Contains("--takeover-only"))
            {
                for (var i = 0; i < 3; i++)
                {
                    Console.WriteLine("TAKEOVER REPETITION " + (i + 1));
                    await TakeoverScenario();
                    await TakeoverScenario(endsOnArrival: true);
                    await TakeoverScenario(idle: true);
                }
            }
            else
            {
                await MainScenario();
                await TakeoverScenario();
                await TakeoverScenario(endsOnArrival: true);
                await TakeoverScenario(idle: true);
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("HARNESS ERROR " + ex);
        }
        Console.WriteLine($"{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}

internal sealed class Feed : IDisposable
{
    private readonly HttpListener listener = new();
    internal readonly List<string> Requests = new();
    internal WebSocket? Socket;
    internal string Endpoint { get; }

    internal Feed()
    {
        var port = Program.FreePort();
        Endpoint = $"ws://127.0.0.1:{port}/ws/";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
    }

    internal async Task Accept()
    {
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(12));
        if (context.Request.Url!.AbsolutePath != "/ws/") throw new Exception("Unexpected feed path");
        Socket?.Dispose();
        Socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        for (var i = 0; i < 2; i++)
        {
            var bytes = new byte[4096];
            using var message = new MemoryStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            WebSocketReceiveResult result;
            do
            {
                result = await Socket.ReceiveAsync(bytes, timeout.Token);
                if (result.MessageType != WebSocketMessageType.Text) throw new Exception("Expected a text request");
                message.Write(bytes, 0, result.Count);
                if (message.Length > 4096) throw new Exception("Unexpected request size");
            }
            while (!result.EndOfMessage);
            var text = Encoding.UTF8.GetString(message.ToArray());
            Requests.Add(text);
        }
    }

    internal async Task Send(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var middle = bytes.Length / 2;
        await Socket!.SendAsync(bytes.AsMemory(0, middle), WebSocketMessageType.Text, false, CancellationToken.None);
        await Socket.SendAsync(bytes.AsMemory(middle), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
        Socket?.Dispose();
        listener.Close();
    }
}
