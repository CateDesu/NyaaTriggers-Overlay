using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task Until(Func<bool> condition, Action? update = null)
{
    var watch = Stopwatch.StartNew();
    while (true)
    {
        update?.Invoke();
        if (condition()) return;
        if (watch.Elapsed.TotalSeconds > 8) throw new TimeoutException();
        await Task.Delay(10);
    }
}

const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
static object Field(object target, string name)
    => target.GetType().GetField(name, Private)!.GetValue(target)!;
static void Receive(StandaloneMeter meter, string? text)
    => typeof(StandaloneMeter).GetMethod("Receive", Private)!.Invoke(meter, new[] { Field(meter, "client"), text });
static string Damage(string flags = "0003")
{
    var fields = new List<string> { "21", "ts", "10000001", "Player One", "07", "Hit", "40000010", "Dummy", flags, "47280000" };
    while (fields.Count < 24) fields.Add("");
    return JsonSerializer.Serialize(new { type = "LogLine", rawLine = string.Join('|', fields) });
}
const string Party = "{\"type\":\"PartyChanged\",\"party\":[{\"id\":\"10000001\",\"job\":31}]}";
const string Combat = "{\"type\":\"InCombat\",\"inACTCombat\":true,\"inGameCombat\":true}";

// Counts, retained bytes and frame budgets all apply to the same queue.
{
    var queue = new MessageInbox();
    var large = new string('x', 1 << 20);
    Check(queue.TryEnqueue(large) && queue.TryEnqueue(large), "two MiB strings fit the four MiB byte budget");
    Check(!queue.TryEnqueue("x"), "byte limit refuses the next character");
    var budget = MessageInbox.FrameBytes;
    Check(queue.TryDequeue(ref budget, true, out _) && !queue.TryDequeue(ref budget, false, out _), "large messages drain across frames");
    queue.Clear();
    Check(Enumerable.Range(0, 512).All(_ => queue.TryEnqueue(null)), "control marker admission stays bounded");
    Check(!queue.TryEnqueue(null), "control markers cannot bypass the count limit");
    queue.Clear();
    Check(!queue.TryEnqueue(new string('x', (MessageInbox.MaxBytes / 2) + 1)), "oversize single message refused");
    Check(queue.TryEnqueue("fresh"), "clear restores the byte budget");
}

// Recovery keeps the exact damaged bytes and finite settings survive sanitation.
{
    var folder = Path.Combine(Path.GetTempPath(), "nyaa-config-test-" + Guid.NewGuid());
    Directory.CreateDirectory(folder);
    try
    {
        var path = Path.Combine(folder, "NyaaTriggers.json");
        const string broken = "{ broken JSON";
        File.WriteAllText(path, broken);
        var config = Configuration.Load(() => JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path)), path);
        Check(config.Port == 27080 && !File.Exists(path), "corrupt config recovers to defaults");
        var backups = Directory.GetFiles(folder, "*.broken.*");
        Check(backups.Length == 1 && File.ReadAllText(backups[0]) == broken, "damaged config preserved exactly");
        config.TimelinePos = new Vector2(float.NaN, 1);
        config.AlertsFade = float.PositiveInfinity;
        config.ColorAlarm = new Vector4(float.NaN, 0.3f, float.NegativeInfinity, 0.8f);
        config.DpsTextScale = 1.7f;
        config.Sanitize();
        Check(config.TimelinePos == new Configuration().TimelinePos && config.AlertsFade == 1, "nonfinite geometry and fade restored");
        Check(config.ColorAlarm.Y == 0.3f && config.ColorAlarm.W == 0.8f && float.IsFinite(config.ColorAlarm.X), "valid color components preserved");
        Check(config.DpsTextScale == 1.7f, "valid setting preserved");
        Check(config.ApplyAppearanceProfile("{\"AlertsFade\":1e50}"), "overflowing profile number accepted for sanitation");
        Check(float.IsFinite(config.AlertsFade), "profile cannot introduce infinity");
        Directory.CreateDirectory(path);
        var blocked = Configuration.Load(() => throw new IOException("unreadable config"), path);
        Check((bool)Field(blocked, "saveDisabled"), "failed quarantine leaves saving disabled");
    }
    finally { Directory.Delete(folder, true); }
}

foreach (var port in new[] { 0, -1, 65536 })
{
    using var server = new WebSocketServer(port, (_, _) => { }, (_, _) => { }, () => null);
    server.Start();
    Check(server.LastError != null && !server.IsConnected, "invalid port fails visibly");
}

// An older handshake completing late cannot replace the healthy newer session.
{
    var port = FreePort();
    using var server = new WebSocketServer(port, (_, _) => { }, (_, _) => { }, () => "hello");
    server.Start();
    using var old = new TcpClient();
    await old.ConnectAsync(IPAddress.Loopback, port);
    await Until(() => ((System.Collections.ICollection)Field(server, "sessions")).Count == 1);
    using var healthy = new ClientWebSocket();
    await healthy.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
    var buffer = new byte[1024];
    var hello = await healthy.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
    Check(Encoding.UTF8.GetString(buffer, 0, hello.Count) == "hello", "greeting is first");
    var request = $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";
    await old.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
    using var reader = new StreamReader(old.GetStream());
    await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
    server.Send("still healthy");
    var reply = await healthy.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
    Check(reply.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(buffer, 0, reply.Count) == "still healthy", "late handshake leaves healthy peer alive");
    healthy.Abort();
    server.Dispose();
    var tasks = (List<Task>)Field(server, "acceptTasks");
    Check(tasks.All(t => t.IsCompleted), "accept loops drain during disposal");
}

// An overloaded program session reconnects to resend its complete schedule.
{
    var port = FreePort();
    using var host = new BridgeHost(new Configuration { Port = port });
    host.Start();
    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
    await Until(() => (long)Field(host, "currentSession") > 0);
    var server = (WebSocketServer)Field(host, "server");
    var sequence = (long)Field(host, "currentSession");
    var receive = typeof(BridgeHost).GetMethod("Receive", Private)!;
    for (var i = 0; i < 3; i++) receive.Invoke(host, new object[] { server, sequence, new string('x', 1 << 20) });
    await Until(() => !host.IsConnected);
    host.Update();
    Check(host.Timeline.Count == 0 && !host.ClockRunning, "overflow retires the old program state");
    using var replacement = new ClientWebSocket();
    await replacement.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
    await Until(() => (long)Field(host, "currentSession") > sequence);
    await replacement.SendAsync(Encoding.UTF8.GetBytes("{\"c\":\"timeline\",\"v\":[[10,\"Fresh\",\"mechanic\"]]}"),
        WebSocketMessageType.Text, true, CancellationToken.None);
    await Until(() => host.Timeline.Count == 1, host.Update);
    Check(host.Timeline[0].Label == "Fresh", "replacement session supplies a fresh schedule after overflow");
    replacement.Abort();
    client.Abort();
}

// A real feed disconnect closes the engine on Update. Replayed edges start fresh.
{
    var port = FreePort();
    using var server = new WebSocketServer(port, (_, _) => { }, (_, _) => { }, () => null);
    server.Start();
    var config = new Configuration { StandaloneMeter = true, IinactEndpoint = $"ws://127.0.0.1:{port}" };
    var states = new List<DpsState>();
    using var meter = new StandaloneMeter(config, () => false, states.Add, () => { });
    await Until(() => server.IsConnected && meter.State == StandaloneState.Connected, meter.Update);
    server.Send(Party);
    server.Send(Combat);
    server.Send(Damage());
    await Until(() => states.Any(s => s.Show && s.Rows.Count == 1), meter.Update);
    server.Dispose();
    await Until(() => states.Any(s => s.Ended), meter.Update);
    Check(!((MeterEngine)Field(meter, "engine")).HasLiveEncounter, "socket loss closes the live encounter");
    Receive(meter, Party);
    Receive(meter, Combat);
    Receive(meter, Damage());
    meter.Update();
    Check(((MeterEngine)Field(meter, "engine")).HasLiveEncounter, "reconnect replay starts fresh");
    Check(states.Last().Show && states.Last().Rows[0].Dps == 18216, "reconnect does not merge old damage");
    for (var i = 0; i < 520; i++) Receive(meter, Damage());
    meter.Update();
    Check(states.Last().Ended && states.Last().Title.Contains("incomplete"), "overflow finalizes with an incomplete marker");
    Check(!((MeterEngine)Field(meter, "engine")).HasLiveEncounter, "overflow never resumes a corrupt total");
    meter.Update();
    Check(Field(meter, "client") == null, "overflow retry waits instead of reconnecting every frame");
}

// Valid nonobjects, ordered disconnect bursts and miss-only endings all close cleanly.
{
    var port = FreePort();
    using var server = new WebSocketServer(port, (_, _) => { }, (_, _) => { }, () => null);
    server.Start();
    var config = new Configuration { StandaloneMeter = true, IinactEndpoint = $"ws://127.0.0.1:{port}" };
    var states = new List<DpsState>();
    using var meter = new StandaloneMeter(config, () => false, states.Add, () => { });
    await Until(() => server.IsConnected && meter.State == StandaloneState.Connected, meter.Update);
    foreach (var raw in new[] { "[]", "null", "42", Party, Combat, Damage("0001") }) Receive(meter, raw);
    meter.Update();
    Check(states.Last().Show, "miss-only pull is initially live");
    Receive(meter, "33|ts|10000001|4000000F");
    meter.Update();
    Check(states.Last().Ended && !states.Last().Show, "miss-only wipe retires the live row");
    Receive(meter, Combat);
    Receive(meter, null);
    Receive(meter, Party);
    Receive(meter, Combat);
    Receive(meter, Damage());
    meter.Update();
    Check(states.Last().Show && states.Last().Rows.Count == 1, "ordered loss marker precedes replay in one frame");
}

Console.WriteLine($"{checks} bridge and configuration checks passed");
