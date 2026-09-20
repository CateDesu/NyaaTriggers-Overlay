using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using NyaaTriggers.Plugin.Bridge;
using static Program;

internal static class WebSocketValidationTests
{
    private const string Request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";

    internal static async Task Run()
    {
        foreach (var (label, request) in new[]
        {
            ("invalid nonce", Request.Replace("dGhlIHNhbXBsZSBub25jZQ==", "invalid")),
            ("short nonce", Request.Replace("dGhlIHNhbXBsZSBub25jZQ==", "YQ==")),
            ("old HTTP version", Request.Replace("HTTP/1.1", "HTTP/1.0")),
            ("missing upgrade token", Request.Replace("Connection: Upgrade", "Connection: keep-alive")),
            ("missing host", Request.Replace("Host: localhost\r\n", "")),
            ("duplicate key", Request.Replace("Host: localhost", "Host: localhost\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==")),
            ("browser origin", Request.Replace("Host: localhost", "Host: localhost\r\nOrigin: null")),
            ("empty browser origin", Request.Replace("Host: localhost", "Host: localhost\r\norigin:")),
            ("header without a colon", Request.Replace("Host: localhost", "Host: localhost\r\nInvalid header")),
            ("whitespace before header colon", Request.Replace("Host: localhost", "Host : localhost")),
            ("control character in host", Request.Replace("Host: localhost", "Host: local\0host")),
            ("control character in request target", Request.Replace("GET / ", "GET /\t ")),
        })
        {
            var port = Port();
            using var server = new WebSocketServer(port, (_, _) => { }, (_, _) => { }, () => null);
            server.Start();
            using var owner = new ClientWebSocket();
            await owner.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
            await Until(() => server.IsConnected);
            using var invalid = new TcpClient();
            await invalid.ConnectAsync(IPAddress.Loopback, port);
            var response = await Handshake(invalid, request);
            if (response.StartsWith("HTTP/1.1 101")) await Task.Delay(400);
            server.Send("owner still connected");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buffer = new byte[256];
            var reply = await owner.ReceiveAsync(buffer, timeout.Token);
            Check(response.StartsWith("HTTP/1.1 400") && reply.MessageType == WebSocketMessageType.Text
                && Encoding.UTF8.GetString(buffer, 0, reply.Count) == "owner still connected",
                "Invalid handshake leaves the established program connected",
                new { label, status = response.Split('\r')[0], reply = reply.MessageType.ToString() });
            owner.Abort();
        }

        foreach (var (label, bytes, expected) in new (string, byte[], ushort)[]
        {
            ("one byte close body", Frame(8, new byte[] { 1 }), 1002),
            ("reserved close status", Frame(8, new byte[] { 3, 237 }), 1002),
            ("close status out of range", Frame(8, new byte[] { 19, 136 }), 1002),
            ("invalid close UTF8", Frame(8, new byte[] { 3, 232, 255 }), 1007),
            ("private close status", Frame(8, new byte[] { 15, 160 }), 1000),
            ("close reason UTF8", Frame(8, new byte[] { 3, 232, 0xc3, 0xa9 }), 1000),
            ("empty close", Frame(8, Array.Empty<byte>()), 1000),
            ("invalid text UTF8", Frame(1, new byte[] { 255 }), 1007),
            ("unsupported binary", Frame(2, Array.Empty<byte>()), 1003),
            ("reserved opcode", Frame(3, Array.Empty<byte>()), 1002),
            ("orphan continuation", Frame(0, new byte[] { 120 }), 1002),
            ("overlapping fragmented messages", Frame(1, new byte[] { 120 }, false).Concat(Frame(1, new byte[] { 120 })).ToArray(), 1002),
            ("unmasked text", new byte[] { 0x81, 0 }, 1002),
            ("fragmented ping", new byte[] { 9, 0x80, 0, 0, 0, 0 }, 1002),
            ("nonminimal length", new byte[] { 0x81, 0xfe, 0, 1, 0, 0, 0, 0, 120 }.Concat(Frame(8, Array.Empty<byte>())).ToArray(), 1002),
            ("nonminimal long length", new byte[] { 0x81, 0xff, 0, 0, 0, 0, 0, 0, 255, 255 }, 1002),
            ("reserved frame bits", new byte[] { 0xc1, 0x80, 0, 0, 0, 0 }, 1002),
            ("negative long length", new byte[] { 0x81, 0xff, 128, 0, 0, 0, 0, 0, 0, 0 }, 1002),
            ("oversized control", new byte[] { 0x89, 0xfe, 0, 126 }, 1002),
            ("registered close status", Frame(8, new byte[] { 3, 246 }), 1000),
            ("reserved close range", Frame(8, new byte[] { 7, 208 }), 1002),
            ("application close range", Frame(8, new byte[] { 11, 184 }), 1000),
            ("highest private close", Frame(8, new byte[] { 19, 135 }), 1000),
            ("oversized text", new byte[] { 0x81, 0xff, 0, 0, 0, 0, 0, 16, 0, 1 }, 1009),
            ("oversized fragments", Frame(1, new byte[1 << 20], false).Concat(Frame(0, new byte[] { 120 })).ToArray(), 1009),
        })
        {
            var delivered = 0;
            var port = Port();
            using var server = new WebSocketServer(port, (_, _) => Interlocked.Increment(ref delivered), (_, _) => { }, () => null);
            server.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await Handshake(client, Request.Replace("Connection: Upgrade", "Connection: keep-alive, UpGrAdE"));
            await client.GetStream().WriteAsync(bytes);
            var close = new byte[4];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.GetStream().ReadExactlyAsync(close, timeout.Token);
            var code = BinaryPrimitives.ReadUInt16BigEndian(close.AsSpan(2));
            Check(close[0] == 0x88 && code == expected && delivered == 0,
                "Client frame validation reports the correct close status",
                new { label, expected, actual = code, delivered });
        }

        await ValidFrames();
    }

    private static byte[] Frame(byte opcode, byte[] payload, bool final = true)
    {
        var headerLength = payload.Length <= 125 ? 2 : payload.Length <= ushort.MaxValue ? 4 : 10;
        var frame = new byte[headerLength + 4 + payload.Length];
        frame[0] = (byte)((final ? 0x80 : 0) | opcode);
        frame[1] = (byte)(0x80 | (headerLength == 2 ? payload.Length : headerLength == 4 ? 126 : 127));
        if (headerLength == 4) BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
        if (headerLength == 10) BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
        payload.CopyTo(frame, headerLength + 4);
        return frame;
    }

    private static async Task ValidFrames()
    {
        var messages = new ConcurrentQueue<string>();
        var port = Port();
        using var server = new WebSocketServer(port, (_, text) => messages.Enqueue(text), (_, _) => { }, () => null);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var request = Request.Replace("Connection: Upgrade", "Connection: keep-alive\r\nConnection: UpGrAdE")
            .Replace("Host: localhost", "Host: localhost\r\nX_!#$%&'*+-.^`|~: \tkeep: value\t");
        var response = await Handshake(client, request);
        Check(response.StartsWith("HTTP/1.1 101"), "Repeated Connection headers combine their upgrade tokens");
        var stream = client.GetStream();
        await stream.WriteAsync(Frame(1, new byte[] { 0xf0 }, false));
        await stream.WriteAsync(Frame(9, new byte[] { 1, 2, 3 }));
        await stream.WriteAsync(Frame(0, new byte[] { 0x9f, 0x98, 0x80 }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var pong = new byte[5];
        await stream.ReadExactlyAsync(pong, timeout.Token);
        await Until(() => messages.Count == 1);
        Check(pong.SequenceEqual(new byte[] { 0x8a, 3, 1, 2, 3 }) && messages.TryDequeue(out var text) && text == "😀",
            "An interleaved ping preserves UTF8 split across text fragments");
        foreach (var size in new[] { 0, 125, 126, 65535, 65536, 1 << 20 })
        {
            await stream.WriteAsync(Frame(1, Encoding.UTF8.GetBytes(new string('x', size))));
            await Until(() => messages.Count == 1);
            Check(messages.TryDequeue(out text) && text.Length == size,
                "Valid text survives frame length boundaries", size);
        }

        await stream.WriteAsync(Frame(1, Encoding.UTF8.GetBytes(new string('x', 1 << 20)), false));
        await stream.WriteAsync(Frame(9, new byte[125]));
        await stream.WriteAsync(Frame(0, Array.Empty<byte>()));
        var fullPong = new byte[127];
        await stream.ReadExactlyAsync(fullPong, timeout.Token);
        await Until(() => messages.Count == 1);
        Check(fullPong[0] == 0x8a && fullPong[1] == 125 && messages.TryDequeue(out text) && text.Length == 1 << 20,
            "A full message allows an interleaved maximum ping and empty final continuation");

        var pending = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var socket = new TcpClient();
                pending.Add(socket);
                await socket.ConnectAsync(IPAddress.Loopback, port);
            }
            await stream.WriteAsync(Frame(1, "healthy"u8.ToArray()));
            await Until(() => messages.Count == 1);
            Check(messages.TryDequeue(out text) && text == "healthy",
                "Incomplete handshake pressure cannot evict the established program");
        }
        finally { foreach (var socket in pending) socket.Dispose(); }
    }

    private static async Task<string> Handshake(TcpClient client, string request)
    {
        var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        var response = new List<byte>();
        var single = new byte[1];
        while (response.Count < 8192)
        {
            await stream.ReadExactlyAsync(single, timeout.Token);
            response.Add(single[0]);
            if (response.Count >= 4 && response.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                return Encoding.ASCII.GetString(response.ToArray());
        }
        throw new InvalidDataException("Oversized response headers");
    }
}
