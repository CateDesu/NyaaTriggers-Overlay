using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;

internal static class RestartTests
{
    internal static async Task Run()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new BridgeHost(new Configuration { Port = port });
        host.Start();
        ClientWebSocket? previous = null;
        try
        {
            for (var i = 0; i < 20; i++)
            {
                host.Restart();
                if (host.Timeline.Count != 0) throw new Exception("Restart retained the retired timeline");
                previous?.Dispose();
                previous = null;
                if (host.LastError != null) throw new Exception($"Same-port restart {i}: {host.LastError}");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var client = previous = new ClientWebSocket();
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
                var bytes = new byte[4096];
                var result = await client.ReceiveAsync(bytes, timeout.Token);
                using var hello = JsonDocument.Parse(bytes.AsMemory(0, result.Count));
                if (hello.RootElement.GetProperty("protocol").GetInt32() != BridgeHost.ProtocolVersion)
                    throw new Exception("Restarted listener did not send its greeting");
                await ApplyTimeline(client, host, $"restart {i}");
            }
        }
        finally
        {
            previous?.Dispose();
        }
        host.Stop();
        using (var blocker = new TcpListener(IPAddress.Loopback, port))
        {
            blocker.Start();
            host.Start();
            if (host.LastError == null || host.IsConnected)
                throw new Exception("Occupied IPv4 port did not report a failed start");
            blocker.Stop();
        }
        host.Restart();
        if (host.LastError != null) throw new Exception($"Bind retry failed: {host.LastError}");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var recovered = new ClientWebSocket())
        {
            await recovered.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            var greeting = await recovered.ReceiveAsync(new byte[4096], timeout.Token);
            if (greeting.MessageType != WebSocketMessageType.Text)
                throw new Exception("Recovered listener did not send a greeting");
            await ApplyTimeline(recovered, host, "recovered");
        }
        host.Stop();
        using (var released = new TcpListener(IPAddress.Loopback, port))
            released.Start();
        Console.WriteLine("20 same-port restarts with command delivery, failed-bind recovery and port release passed");
    }

    private static async Task ApplyTimeline(ClientWebSocket client, BridgeHost host, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = JsonSerializer.Serialize(new { c = "timeline", v = new object[][] { new object[] { 1, label } } });
        await client.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, timeout.Token);
        await client.SendAsync("{\"c\":\"ping\"}"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[4096];
        var pong = client.ReceiveAsync(buffer, timeout.Token);
        while (!pong.IsCompleted)
        {
            host.Update();
            await Task.Delay(10, timeout.Token);
        }
        var result = await pong;
        using var message = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        if (message.RootElement.GetProperty("ev").GetString() != "pong"
            || !host.IsConnected || host.Timeline.Single().Label != label)
            throw new Exception("Replacement session did not apply commands and answer ping");
    }
}
