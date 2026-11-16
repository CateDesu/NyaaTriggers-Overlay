using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;

await KeepaliveChecks.Run();

internal static class KeepaliveChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Party = """{"type":"PartyChanged","party":[{"id":"10000001","job":31}]}""";
    private const string Combat = """{"type":"InCombat","inACTCombat":true,"inGameCombat":true}""";
    private const string Damage = "21|ts|10000001|Player|07|Attack|40000010|Enemy|0003|00640000||||||||||||||";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    private static int checks;

    internal static async Task Run()
    {
        await CompatibleKeepalive();
        await ConnectedRun();
        await Reconnect(false);
        await Reconnect(true);
        await ReceiveCancellation();
        await TransportFailure();
        await CancelRetry();
        Console.WriteLine($"{checks} port-free IINACT checks passed");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static object? Field(object target, string name)
        => target.GetType().GetField(name, Hidden)!.GetValue(target);

    private static void Set(object target, string name, object? value)
        => target.GetType().GetField(name, Hidden)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] values)
        => target.GetType().GetMethod(name, Hidden)!.Invoke(target, values);

    private static async Task CompatibleKeepalive()
    {
        var config = new Configuration { StandaloneMeter = true };
        config.GetMeter(DpsMeterStyle.LMeter).DpsHoldLast = true;
        using var host = new BridgeHost(config);
        var meter = (StandaloneMeter)Field(host, "standalone")!;
        await using var session = await Session.Open(meter);
        await session.Text(Party);
        await session.Text(Combat);
        await session.Text(Damage);
        host.Update();
        var id = host.Dps.Id;
        Check(host.Dps.Show && host.Dps.Rows.Single().Stats?.Damage == 100,
            "socket messages reach the standalone meter");

        var heartbeat = await session.Heartbeat();
        Console.WriteLine($"Production heartbeat opcode: {heartbeat.Opcode}");
        // Simulate NetCoreServer 8.0.7's prefix. No live IINACT runs in this test.
        session.Wire.Send(10, [0, 0, .. heartbeat.Payload]);
        await session.Text(Damage);
        session.ExpirePing();
        Check(session.Socket.State == WebSocketState.Open && !session.Receiving.IsCompleted,
            "healthy prefixed-pong session survives the former ping deadline");
        Check(heartbeat.Opcode == 10 && heartbeat.Payload.Length == 0,
            "production keepalive sends an unsolicited pong");
        Check(session.Socket.Options.KeepAliveInterval == TimeSpan.FromSeconds(30),
            "production heartbeat interval remains 30 seconds");
        Check(session.Socket.Options.KeepAliveTimeout <= TimeSpan.Zero,
            "production keepalive does not require a matching pong");

        await session.Text(Damage);
        Set(meter, "nextPush", 0L);
        host.Update();
        Check(host.Dps.Show && host.Dps.Id == id && host.Dps.Rows.Single().Stats?.Damage == 300,
            "damage after quiet keepalive stays in the same encounter");
        Check(host.DpsHistory.Count == 0, "keepalive cannot create a false encounter ending");

        byte[] peerPing = [1, 2, 3, 4, 5, 6, 7, 8];
        session.Wire.Send(9, peerPing);
        // ExpirePing sends another heartbeat in unsolicited pong mode.
        var continued = await session.Wire.ReadFrame();
        Check(continued.Opcode == 10 && continued.Payload.Length == 0,
            "a silent peer does not stop periodic pong writes");
        var response = await session.Wire.ReadFrame();
        Check(response.Opcode == 10 && response.Payload.SequenceEqual(peerPing),
            "peer pings still receive matching pong responses");

        session.Wire.Send(8, [3, 232]);
        await session.Receiving.WaitAsync(Deadline);
        Check(session.Socket.State == WebSocketState.CloseReceived &&
              session.Client.Status == "IINACT feed closed the connection.",
            "a peer close still ends the production receive loop");
        // Hand off the session marker that RunAsync's finally sends to the owner.
        ((Action)Field(session.Client, "onSessionEnd")!)();
        host.Update();
        Check(host.Dps.Ended && host.Dps.Id == id && host.Dps.Rows.Single().Stats?.Damage == 300 &&
              host.DpsHistory.Count == 1, "peer closure retains and finalizes the encounter once");

        await using var replacement = await Session.Open(meter);
        await replacement.Text(Party);
        await replacement.Text(Combat);
        await replacement.Text(Damage);
        host.Update();
        Check(host.Dps.Show && host.Dps.Id != id && host.Dps.Rows.Single().Stats?.Damage == 100 &&
              host.DpsHistory.Count == 1, "a replacement session recovers without merging old damage");
    }

    private static async Task ReceiveCancellation()
    {
        await using var session = await Session.Open();
        var first = await session.Heartbeat();
        Check(first.Opcode == 10, "idle receive uses compatible keepalive");
        session.Client.Stop();
        try
        {
            await session.Receiving.WaitAsync(Deadline);
            throw new InvalidOperationException("stopping must cancel the pending receive");
        }
        catch (OperationCanceledException)
        {
            Check(true, "stopping cancels a silent peer receive promptly");
        }
    }

    private static async Task TransportFailure()
    {
        await using var session = await Session.Open();
        session.Wire.Fail(new IOException("simulated transport failure"));
        try
        {
            await session.Receiving.WaitAsync(Deadline);
            throw new InvalidOperationException("transport failure must leave the receive loop");
        }
        catch (WebSocketException)
        {
            Check(true, "transport failure still reaches session recovery");
        }
    }

    private static async Task CancelRetry()
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endings = 0;
        // An invalid scheme exercises RunAsync failure and backoff without opening a socket.
        using var client = new IinactClient(new Uri("https://iinact.invalid/"), _ => { }, () =>
        {
            Interlocked.Increment(ref endings);
            ended.TrySetResult();
        });
        client.Start();
        await ended.Task.WaitAsync(Deadline);
        using var deadline = new CancellationTokenSource(Deadline);
        while (!client.Status.EndsWith("Retrying in 5s.", StringComparison.Ordinal))
            await Task.Delay(1, deadline.Token);
        Check(!client.IsConnected && client.Status.StartsWith("No IINACT feed:", StringComparison.Ordinal),
            "connection failure reaches the five-second retry backoff");
        client.Stop();
        await ((Task)Field(client, "loop")!).WaitAsync(Deadline);
        Check(endings == 1, "stop cancels retry backoff without starting another session");
    }

    private static async Task ConnectedRun()
    {
        using var wire = new MemoryTransport();
        using var invoker = new HttpMessageInvoker(new UpgradeHandler(wire));
        var messages = Channel.CreateUnbounded<string>();
        var endings = 0;
        using var client = new IinactClient(new Uri("ws://iinact.invalid/"),
            raw => messages.Writer.TryWrite(raw), () => Interlocked.Increment(ref endings), invoker);
        client.Start();
        try
        {
            var subscribe = await wire.ReadFrame();
            var combatants = await wire.ReadFrame();
            Check(subscribe.Opcode == 1 && Encoding.UTF8.GetString(subscribe.Payload).Contains("subscribe"),
                "RunAsync sends the subscription");
            Check(combatants.Opcode == 1 && Encoding.UTF8.GetString(combatants.Payload).Contains("getCombatants"),
                "RunAsync requests combatants");
            Check(client.IsConnected, "RunAsync reports its connected session");
            wire.Send(1, Encoding.UTF8.GetBytes("before"));
            Check(await messages.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == "before",
                "RunAsync delivers incoming messages");
            var heartbeat = await wire.ReadFrame(TimeSpan.FromSeconds(45));
            Check(heartbeat.Opcode == 10 && heartbeat.Payload.Length == 0,
                "RunAsync uses an unsolicited pong on its real socket");
            wire.Send(1, Encoding.UTF8.GetBytes("after"));
            Check(await messages.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == "after",
                "RunAsync continues receiving after its heartbeat");
        }
        finally
        {
            client.Stop();
            await ((Task)Field(client, "loop")!).WaitAsync(Deadline);
        }
        Check(!client.IsConnected && endings == 1, "stopping ends the connected session once");
    }

    private static async Task Reconnect(bool transportFailure)
    {
        var config = new Configuration { StandaloneMeter = true };
        config.GetMeter(DpsMeterStyle.LMeter).DpsHoldLast = true;
        using var host = new BridgeHost(config);
        var meter = (StandaloneMeter)Field(host, "standalone")!;
        using var first = new MemoryTransport();
        using var second = new MemoryTransport();
        var attempts = 0;
        using var invoker = new HttpMessageInvoker(new UpgradeHandler(() =>
            Interlocked.Increment(ref attempts) == 1 ? first : second));
        var delivered = Channel.CreateUnbounded<string?>();
        IinactClient? source = null;
        using var client = source = new IinactClient(new Uri("ws://iinact.invalid/"), raw =>
        {
            Call(meter, "Receive", source!, raw);
            delivered.Writer.TryWrite(raw);
        }, () =>
        {
            Call(meter, "Receive", source!, null);
            delivered.Writer.TryWrite(null);
        }, invoker);
        Call(meter, "ResetEngine");
        Set(meter, "client", client);
        client.Start();
        try
        {
            await first.ReadFrame();
            await first.ReadFrame();
            foreach (var raw in new[] { Party, Combat, Damage })
            {
                first.Send(1, Encoding.UTF8.GetBytes(raw));
                Check(await delivered.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == raw,
                    "initial session preserves message ordering");
            }
            host.Update();
            var id = host.Dps.Id;
            if (transportFailure) first.Fail(new IOException("simulated transport failure"));
            else first.Send(8, [3, 232]);
            Check(await delivered.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == null,
                "real RunAsync publishes a loss marker before retry");
            host.Update();
            Check(host.Dps.Ended && host.Dps.Id == id && host.DpsHistory.Count == 1 &&
                  host.Dps.Rows.Single().Stats?.Damage == 100,
                "real session loss retains its final total exactly once");
            var subscription = await second.ReadFrame(TimeSpan.FromSeconds(8));
            var combatants = await second.ReadFrame();
            Check(attempts == 2 && subscription.Opcode == 1 && combatants.Opcode == 1 &&
                  Encoding.UTF8.GetString(subscription.Payload).Contains("subscribe") &&
                  Encoding.UTF8.GetString(combatants.Payload).Contains("getCombatants"),
                "retry reconnects and requests subscription and combatants again");
            foreach (var raw in new[] { Party, Combat, Damage })
            {
                second.Send(1, Encoding.UTF8.GetBytes(raw));
                Check(await delivered.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == raw,
                    "recovered session follows its loss marker");
            }
            host.Update();
            Check(host.Dps.Show && host.Dps.Id != id && host.DpsHistory.Count == 1 &&
                  host.Dps.Rows.Single().Stats?.Damage == 100,
                "automatic recovery cannot merge damage across a lost session");
            client.Stop();
            await ((Task)Field(client, "loop")!).WaitAsync(Deadline);
            Check(await delivered.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == null && attempts == 2,
                "stop ends the recovered session without another retry");
            host.Update();
            Check(host.Dps.Ended && host.DpsHistory.Count == 2,
                "recovered encounter finalizes once on shutdown");
        }
        finally
        {
            client.Stop();
            await ((Task)Field(client, "loop")!).WaitAsync(Deadline);
            Set(meter, "client", null);
        }
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private readonly HttpMessageInvoker invoker;
        private readonly StandaloneMeter? owner;
        private object managed = null!;

        private Session(StandaloneMeter? meter)
        {
            this.owner = meter;
            this.Wire = new MemoryTransport();
            this.Socket = IinactClient.CreateWebSocket();
            this.Client = new IinactClient(new Uri("ws://iinact.invalid/"), raw =>
            {
                if (meter != null) Call(meter, "Receive", this.Client!, raw);
                this.messages.Writer.TryWrite(raw);
            }, () =>
            {
                if (meter != null) Call(meter, "Receive", this.Client!, null);
            });
            this.invoker = new HttpMessageInvoker(new UpgradeHandler(this.Wire));
        }

        internal MemoryTransport Wire { get; }
        internal ClientWebSocket Socket { get; }
        internal IinactClient Client { get; }
        internal Task Receiving { get; private set; } = Task.CompletedTask;

        internal static async Task<Session> Open(StandaloneMeter? meter = null)
        {
            var session = new Session(meter);
            try
            {
                await session.Socket.ConnectAsync(new Uri("ws://iinact.invalid/"), session.invoker,
                    CancellationToken.None).WaitAsync(Deadline);
                var handle = Field(session.Socket, "_innerWebSocket")!;
                session.managed = handle.GetType().GetProperty("WebSocket")!.GetValue(handle)!;
                if (meter != null)
                {
                    Call(meter, "ResetEngine");
                    Set(meter, "client", session.Client);
                }

                Set(session.Client, "running", true);
                session.Receiving = (Task)Call(session.Client, "ReceiveLoop", session.Socket)!;
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        internal async Task Text(string raw)
        {
            this.Wire.Send(1, Encoding.UTF8.GetBytes(raw));
            Check(await this.messages.Reader.ReadAsync().AsTask().WaitAsync(Deadline) == raw,
                "text is delivered without exposing control frames");
        }

        internal async Task<(int Opcode, byte[] Payload)> Heartbeat()
        {
            // Advance only .NET 10's scheduling deadlines, preserving production option values.
            var ping = Field(this.managed, "_keepAlivePingState");
            if (ping != null) Set(ping, "<NextPingRequestTimestamp>k__BackingField", 0L);
            Call(this.managed, "HeartBeat");
            return await this.Wire.ReadFrame();
        }

        internal void ExpirePing()
        {
            var ping = Field(this.managed, "_keepAlivePingState");
            if (ping != null) Set(ping, "<PingTimeoutTimestamp>k__BackingField", 0L);
            Call(this.managed, "HeartBeat");
        }

        public async ValueTask DisposeAsync()
        {
            this.Client.Stop();
            try
            {
                await this.Receiving.WaitAsync(Deadline);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
            finally
            {
                if (this.owner != null && ReferenceEquals(Field(this.owner, "client"), this.Client))
                    Set(this.owner, "client", null);
                this.Socket.Dispose();
                this.Client.Dispose();
                this.invoker.Dispose();
                this.Wire.Dispose();
            }
        }
    }

    private sealed class UpgradeHandler(Func<Stream> streamFactory) : HttpMessageHandler
    {
        internal UpgradeHandler(Stream stream) : this(() => stream) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.GetValues("Sec-WebSocket-Key").Single();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
            {
                Content = new DuplexContent(streamFactory()),
            };
            response.Headers.Add("Connection", "Upgrade");
            response.Headers.Add("Upgrade", "websocket");
            response.Headers.Add("Sec-WebSocket-Accept", accept);
            return Task.FromResult(response);
        }
    }

    private sealed class DuplexContent(Stream stream) : HttpContent
    {
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => stream;
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult(stream);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
            => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class MemoryTransport : Stream
    {
        private readonly Channel<byte[]> input = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> output = Channel.CreateUnbounded<byte[]>();
        private byte[] remaining = [];
        private int offset;

        internal void Send(int opcode, byte[] payload)
        {
            if (payload.Length >= 126) throw new ArgumentException("fixture requires a short frame");
            this.input.Writer.TryWrite([(byte)(0x80 | opcode), (byte)payload.Length, .. payload]);
        }

        internal void Fail(Exception error) => this.input.Writer.TryComplete(error);

        internal async Task<(int Opcode, byte[] Payload)> ReadFrame(TimeSpan? timeout = null)
        {
            var frame = await this.output.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? Deadline);
            var size = frame[1] & 0x7f;
            Check(size < 126 && (frame[1] & 0x80) != 0 && frame.Length == size + 6,
                "client control frame is complete and masked");
            var payload = new byte[size];
            for (var i = 0; i < size; i++) payload[i] = (byte)(frame[6 + i] ^ frame[2 + i % 4]);
            return (frame[0] & 0xf, payload);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.offset == this.remaining.Length)
            {
                if (!await this.input.Reader.WaitToReadAsync(cancellationToken)) return 0;
                this.remaining = await this.input.Reader.ReadAsync(cancellationToken);
                this.offset = 0;
            }

            var count = Math.Min(buffer.Length, this.remaining.Length - this.offset);
            this.remaining.AsMemory(this.offset, count).CopyTo(buffer);
            this.offset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.output.Writer.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            this.input.Writer.TryComplete();
            this.output.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
