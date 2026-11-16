using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;

internal static class RecoveryTests
{
    internal static async Task Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const string damage = """{"c":"dps","show":true,"enc":{"id":"old","dps":100},"rows":[["Player","MCH",100,100]]}""";
        const string ending = """{"c":"dps","show":false}""";
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            checks++;
        }
        object Field(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target)!;
        async Task Barrier(ClientWebSocket client, BridgeHost host, params string[] frames)
        {
            foreach (var frame in frames.Append("""{"c":"ping"}"""))
                await client.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);
            var receive = client.ReceiveAsync(new byte[4096], CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!receive.IsCompleted && DateTime.UtcNow < deadline)
            {
                host.Update();
                await Task.Delay(10);
            }
            var result = await receive.WaitAsync(TimeSpan.FromSeconds(1));
            Check(result.MessageType == WebSocketMessageType.Text, "valid traffic survives earlier frames");
        }
        async Task<ClientWebSocket> Connect(Configuration config)
        {
            var client = new ClientWebSocket();
            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{config.Port}"), CancellationToken.None);
                var bytes = new byte[4096];
                var result = await client.ReceiveAsync(bytes, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                using var hello = JsonDocument.Parse(bytes.AsMemory(0, result.Count));
                Check(hello.RootElement.GetProperty("protocol").GetInt32() == BridgeHost.ProtocolVersion
                    && hello.RootElement.GetProperty("dpsRetention").GetBoolean(), "greeting advertises native retention after each connection");
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var config = new Configuration { Port = FreePort(), ShowDps = true, DpsHoldLast = true };
        using var host = new BridgeHost(config);
        host.Start();
        using var first = await Connect(config);
        await Barrier(first, host, damage, ending);
        var historyKey = host.Dps.HistoryKey;
        var oldServer = Field(host, "server");
        var oldSequence = (long)Field(host, "currentSession");
        await Barrier(first, host, "{", """{"c":"clear","keepDps":"false"}""",
            """{"c":"dps","show":false,"enc":{}}""", """{"c":"new-command"}""", ending);
        Check(host.Dps.Id == "old" && host.Dps.Ended && host.DpsHistory.Count == 1
            && ReferenceEquals(host.Dps.HistoryKey, historyKey), "malformed and unknown frames cannot invalidate the legacy fallback or duplicate history");

        await Barrier(first, host, """{"c":"clear"}""", """{"c":"dps","show":false,"rows":[]}""", ending);
        Check(host.Dps.Id == "old" && host.UnheldDps.Id == "old" && host.DpsHistory.Count == 1,
            "legacy wipe restores its fallback after a rejected partial ending");
        await Barrier(first, host, """{"c":"clear","keepDps":false}""", "{", ending);
        Check(host.Dps.Rows.Count == 0 && host.UnheldDps.Rows.Count == 0 && host.DpsHistory.Count == 1,
            "full clear invalidates both fallbacks through malformed traffic");
        await Barrier(first, host, damage, ending);

        using var second = await Connect(config);
        await Barrier(second, host, ending);
        Check(host.IsConnected && host.Dps.Rows.Count == 0 && host.UnheldDps.Rows.Count == 0
            && host.DpsHistory.Count == 1, "replacement clears both fallbacks and preserves history");
        typeof(BridgeHost).GetMethod("Receive", flags)!.Invoke(host, new[] { oldServer, (object)oldSequence, damage });
        typeof(BridgeHost).GetMethod("OnConnectionChanged", flags)!.Invoke(host, new[] { oldServer, (object)oldSequence, false });
        await Barrier(second, host, """{"c":"timeline","v":[[1,"replacement"]]}""");
        Check(host.Timeline.Single().Label == "replacement" && host.Dps.Rows.Count == 0,
            "late replaced-session frames and disconnects cannot damage the active session");

        config.Port = FreePort();
        host.Restart();
        using var third = await Connect(config);
        typeof(BridgeHost).GetMethod("Receive", flags)!.Invoke(host, new[] { oldServer, (object)long.MaxValue, damage });
        typeof(BridgeHost).GetMethod("OnConnectionChanged", flags)!.Invoke(host, new[] { oldServer, (object)long.MaxValue, true });
        await Barrier(third, host, ending);
        Check(host.IsConnected && host.Dps.Rows.Count == 0 && host.DpsHistory.Count == 1,
            "restarted listener accepts traffic and rejects all callbacks from the retired server");
        await Barrier(third, host, damage.Replace("old", "fresh"), """{"c":"clear","keepDps":true}""", ending);
        Check(host.Dps.Id == "fresh" && host.UnheldDps.Id == "fresh" && host.Dps.Ended && host.DpsHistory.Count == 2,
            "fresh damage and legacy endings recover after a listener restart");
        Console.WriteLine($"{checks} focused wire recovery checks passed");
    }
}
