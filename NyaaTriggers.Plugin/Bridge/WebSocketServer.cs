using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NyaaTriggers.Plugin.Bridge;

/// <summary>RFC 6455 server for one active local client. Uses TCP directly to avoid
/// HttpListener dependencies under Wine. Supports text messages without extensions or
/// subprotocols and sends each message as one frame.</summary>
internal sealed class WebSocketServer : IDisposable
{
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Bounds allocation for incoming messages.</summary>
    private const int MaxMessageBytes = 1 << 20;

    private const int MaxHandshakeBytes = 8 << 10;

    /// <summary>RFC 6455 caps control frame payloads at 125 bytes.</summary>
    private const int MaxControlPayload = 125;

    /// <summary>Allows reconnects to overlap older sessions while bounding open
    /// sockets.</summary>
    private const int MaxSessions = 4;

    /// <summary>Reclaim sockets that do not complete a handshake.</summary>
    private const int HandshakeTimeoutMs = 5000;

    /// <summary>Maximum wait in milliseconds for the close frame to be sent.</summary>
    private const int CloseFlushMs = 500;

    /// <summary>Drain incoming data after close to avoid a TCP reset discarding the close
    /// frame.</summary>
    private const int CloseDrainMs = 300;

    /// <summary>Maximum wait in milliseconds for session tasks during plugin
    /// unload.</summary>
    private const int DisposeDrainMs = 2000;

    /// <summary>Bounds queued sends. Overflow drops the oldest frame without blocking the
    /// draw thread.</summary>
    private const int OutboxCapacity = 256;

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>Reject invalid UTF-8 instead of substituting replacement
    /// characters.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly int port;

    /// <summary>Callbacks include a session sequence so the host can reject late events
    /// from replaced sessions.</summary>
    private readonly Action<long, string> onMessage;
    private readonly Action<long, bool> onConnectionChanged;

    /// <summary>Queued before the session is published so other messages cannot precede the
    /// greeting.</summary>
    private readonly Func<string?> onGreeting;

    private readonly List<TcpListener> listeners = new();
    private readonly List<Task> acceptTasks = new();

    /// <summary>Accepted sessions and their tasks, retained until both the
    /// receive loop and the send pump have finished.</summary>
    private readonly ConcurrentDictionary<Session, Task> sessions = new();

    /// <summary>Makes session registration atomic with disposal so new tasks cannot miss
    /// the shutdown wait.</summary>
    private readonly object gate = new();

    private bool disposed;
    private CancellationTokenSource? cts;

    /// <summary>Read through Volatile.Read and updated through Interlocked.</summary>
    private Session? peer;
    private long newestEstablished;

    internal WebSocketServer(
        int port,
        Action<long, string> onMessage,
        Action<long, bool> onConnectionChanged,
        Func<string?> onGreeting)
    {
        this.port = port;
        this.onMessage = onMessage;
        this.onConnectionChanged = onConnectionChanged;
        this.onGreeting = onGreeting;
    }

    internal bool IsConnected => Volatile.Read(ref this.peer) != null;

    internal string? LastError { get; private set; }

    internal void Start()
    {
        if (this.port is < 1 or > 65535)
        {
            this.LastError = "Port must be between 1 and 65535.";
            Services.Log.Error($"NyaaTriggers link: {this.LastError}");
            return;
        }

        this.cts = new CancellationTokenSource();
        var token = this.cts.Token;

        // Support localhost resolving to either IPv4 or IPv6.
        string? ipv4Error = null;
        var bound = 0;
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(address, this.port);
                listener.Start();
            }
            catch (Exception ex)
            {
                // IPv6 may be unavailable. Preserve IPv4 errors because the program
                // connects to 127.0.0.1.
                if (address.Equals(IPAddress.Loopback))
                {
                    ipv4Error = ex.Message;
                }

                Services.Log.Debug($"listener on {address}:{this.port} failed: {ex.Message}");
                continue;
            }

            bound++;
            this.listeners.Add(listener);
            this.acceptTasks.Add(Task.Run(() => this.AcceptLoopAsync(listener, token), token));
        }

        // The program requires IPv4. Close any IPv6 listener if IPv4 binding failed.
        if (ipv4Error != null)
        {
            this.cts.Cancel();
            foreach (var listener in this.listeners)
            {
                listener.Stop();
            }

            this.listeners.Clear();
        }

        this.LastError = bound == 0
            ? ipv4Error ?? $"could not bind port {this.port}"
            : ipv4Error;

        if (this.LastError != null)
        {
            Services.Log.Error($"NyaaTriggers link: {this.LastError}");
            return;
        }

        Services.Log.Information($"NyaaTriggers link listening on 127.0.0.1:{this.port}");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // Retry transient accept failures with a delay to avoid spinning on
                // persistent faults.
                Services.Log.Debug($"accept failed, still listening: {ex.SocketErrorCode}");
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }
            catch (Exception ex)
            {
                // Keep listening after unexpected errors, with a delay between retries.
                Services.Log.Warning($"accept trouble, still listening: {ex.Message}");
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                lock (this.gate)
                {
                    if (this.disposed || !this.TryMakeRoomForNewcomer())
                    {
                        client.Dispose();
                        continue;
                    }

                    this.Register(client);
                }
            }
            catch (Exception ex)
            {
                client.Dispose();
                Services.Log.Warning($"could not register link connection: {ex.Message}");
            }
        }
    }

    /// <summary>Evict only sessions with incomplete handshakes. Refuse the newcomer if
    /// every slot is established so idle connection floods cannot disconnect the
    /// program.</summary>
    private bool TryMakeRoomForNewcomer()
    {
        while (true)
        {
            var live = this.sessions.Keys.Where(s => !s.IsDisposed).ToArray();
            if (live.Length < MaxSessions)
            {
                return true;
            }

            var pending = live.Where(s => !s.Established)
                .OrderBy(s => s.Sequence)
                .FirstOrDefault();
            if (pending == null)
            {
                return false;
            }

            Services.Log.Debug($"evicting handshake-pending session {pending.Sequence} to admit a new connection");
            try
            {
                pending.Dispose();
            }
            catch (Exception ex)
            {
                // Disposal is marked before any operation that can throw, so the slot is
                // available.
                Services.Log.Debug($"eviction failed: {ex.Message}");
            }

            // The session removes itself from the dictionary when its task finishes.
        }
    }

    /// <summary>Register under the disposal lock so accepted sockets cannot outlive server
    /// shutdown.</summary>
    private void Register(TcpClient client)
    {
        Session session;
        try
        {
            client.NoDelay = true;
            // Detect dead connections after 30 seconds idle and three probes 10 seconds
            // apart, instead of waiting for the OS default timeout.
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (SocketException)
            {
                // Keep the default keepalive settings if the platform rejects custom probe
                // timing.
            }
            session = new Session(client, this.gate);
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"could not adopt connection: {ex.Message}");
            client.Dispose();
            return;
        }

        lock (this.gate)
        {
            if (this.disposed)
            {
                session.Dispose();
                return;
            }

            // Register the task under the disposal lock. Do not pass a cancellation token
            // to Task.Run because skipping the body would skip socket cleanup.
            this.sessions[session] = Task.Run(() => this.ServeAsync(session));
        }
    }

    private async Task ServeAsync(Session session)
    {
        try
        {
            // Read Token inside the try because earlier eviction may have disposed its
            // source. Cleanup must still run.
            var token = session.Token;

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeCts.CancelAfter(HandshakeTimeoutMs);
            if (!await PerformHandshakeAsync(session.Stream, handshakeCts.Token).ConfigureAwait(false))
            {
                return;
            }

            Session? previous;
            lock (this.gate)
            {
                if (this.disposed || session.IsDisposed ||
                    session.Sequence <= this.newestEstablished)
                {
                    return;
                }

                this.newestEstablished = session.Sequence;
                session.MarkEstablished();
                var greeting = this.onGreeting();
                if (greeting != null)
                {
                    session.Enqueue(BuildFrame(0x1, Encoding.UTF8.GetBytes(greeting)));
                }

                session.Pump = Task.Run(() => PumpAsync(session));
                previous = Interlocked.Exchange(ref this.peer, session);
            }

            if (previous != null)
            {
                // Notify the replaced peer with close code 1001.
                await CloseAsync(previous, 1001).ConfigureAwait(false);
                previous.Dispose();
            }

            try
            {
                this.onConnectionChanged(session.Sequence, true);
            }
            catch (Exception ex)
            {
                // Contain callback errors so session cleanup still runs.
                Services.Log.Warning($"connect handler threw: {ex.Message}");
            }

            Services.Log.Information("NyaaTriggers program connected");
            await this.ReadLoopAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown or handshake timeout.
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"session ended: {ex.Message}");
        }
        finally
        {
            // Clear ownership only if this session has not been replaced.
            if (Interlocked.CompareExchange(ref this.peer, null, session) == session)
            {
                try
                {
                    this.onConnectionChanged(session.Sequence, false);
                }
                catch (Exception ex)
                {
                    Services.Log.Warning($"disconnect handler threw: {ex.Message}");
                }

                Services.Log.Information("NyaaTriggers program disconnected");
            }

            session.Dispose();
            await session.Pump.ConfigureAwait(false);
            this.sessions.TryRemove(session, out _);
        }
    }

    private static async Task<bool> PerformHandshakeAsync(Stream stream, CancellationToken token)
    {
        var (request, worthAnswering) = await ReadRequestHeadAsync(stream, token).ConfigureAwait(false);
        if (request == null)
        {
            // Send an HTTP error only if the peer supplied a request and is still
            // connected.
            if (worthAnswering)
            {
                await WriteAsciiAsync(
                    stream,
                    "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n",
                    token).ConfigureAwait(false);
            }

            return false;
        }

        var key = FindHeader(request, "Sec-WebSocket-Key");
        var upgrade = FindHeader(request, "Upgrade");
        var version = FindHeader(request, "Sec-WebSocket-Version");

        // Reject Origin headers to prevent browser pages from injecting callouts. The
        // program does not send this header.
        var origin = FindHeader(request, "Origin");

        var ok = !string.IsNullOrEmpty(key)
                 && origin == null
                 && request.StartsWith("GET ", StringComparison.Ordinal)
                 && string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
                 && version == "13";

        if (!ok)
        {
            if (origin != null)
            {
                Services.Log.Warning($"refused a browser connection from origin {origin}");
            }

            await WriteAsciiAsync(
                stream,
                "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n",
                token).ConfigureAwait(false);
            return false;
        }

        // Omitting Sec-WebSocket-Extensions leaves all extensions disabled.
        var accept = Convert.ToBase64String(Sha1OfHandshakeKey(key! + HandshakeGuid));

        await WriteAsciiAsync(
            stream,
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n",
            token).ConfigureAwait(false);
        return true;
    }

    // RFC 6455 requires SHA-1 for the handshake. This computes the protocol response, not a
    // security check.
#pragma warning disable CA5350, CA5351
    private static byte[] Sha1OfHandshakeKey(string value)
        => SHA1.HashData(Encoding.ASCII.GetBytes(value));
#pragma warning restore CA5350, CA5351

    /// <summary>Read the request headers and report whether the peer can receive an HTTP
    /// error response.</summary>
    private static async Task<(string? Head, bool WorthAnswering)> ReadRequestHeadAsync(
        Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHandshakeBytes];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), token)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                return (null, false);
            }

            used += read;

            var end = buffer.AsSpan(0, used).IndexOf(HeaderTerminator);
            if (end < 0)
            {
                continue;
            }

            // Reject pipelined frame bytes rather than discard them and desynchronize the
            // stream. The program does not pipeline.
            if (used > end + HeaderTerminator.Length)
            {
                Services.Log.Debug("refusing a handshake with pipelined data");
                return (null, true);
            }

            return (Encoding.ASCII.GetString(buffer, 0, end), true);
        }

        return (null, true);   // Request headers exceeded the size limit.
    }

    private static string? FindHeader(string request, string name)
    {
        foreach (var line in request.Split("\r\n", StringSplitOptions.None))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private async Task ReadLoopAsync(Session session)
    {
        var stream = session.Stream;
        var token = session.Token;
        var header = new byte[8];
        var mask = new byte[4];

        // Accumulate fragments until the final frame.
        using var assembled = new MemoryStream();
        var assembling = false;

        while (!token.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, header.AsMemory(0, 2), token).ConfigureAwait(false))
            {
                return;
            }

            var fin = (header[0] & 0x80) != 0;
            var reserved = header[0] & 0x70;
            var opcode = header[0] & 0x0F;
            var masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;

            if (length == 126)
            {
                if (!await ReadExactAsync(stream, header.AsMemory(0, 2), token).ConfigureAwait(false))
                {
                    return;
                }

                length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            }
            else if (length == 127)
            {
                if (!await ReadExactAsync(stream, header.AsMemory(0, 8), token).ConfigureAwait(false))
                {
                    return;
                }

                length = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
            }

            var control = (opcode & 0x8) != 0;

            // Reject reserved bits, unmasked frames and invalid control frames. Interleaved
            // control frames do not count toward the assembled message limit.
            if (reserved != 0 || !masked || length < 0 || length > MaxMessageBytes ||
                (control && (!fin || length > MaxControlPayload)) ||
                (!control && assembled.Length + length > MaxMessageBytes))
            {
                await CloseAsync(session, 1002).ConfigureAwait(false);
                return;
            }

            if (!await ReadExactAsync(stream, mask.AsMemory(0, 4), token).ConfigureAwait(false))
            {
                return;
            }

            var payload = new byte[length];
            if (length > 0 && !await ReadExactAsync(stream, payload, token).ConfigureAwait(false))
            {
                return;
            }

            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i & 3];
            }

            switch (opcode)
            {
                case 0x0:   // continuation
                    if (!assembling)
                    {
                        await CloseAsync(session, 1002).ConfigureAwait(false);
                        return;
                    }

                    assembled.Write(payload);
                    break;

                case 0x1:   // text
                    // A new text message cannot start before the fragmented message ends.
                    if (assembling)
                    {
                        await CloseAsync(session, 1002).ConfigureAwait(false);
                        return;
                    }

                    assembling = true;
                    assembled.Write(payload);
                    break;

                case 0x8:   // close
                    await CloseAsync(session, 1000).ConfigureAwait(false);
                    return;

                case 0x9:   // ping
                    session.Enqueue(BuildFrame(0xA, payload));
                    continue;

                case 0xA:   // pong
                    continue;

                default:    // binary or reserved: not part of this protocol
                    await CloseAsync(session, 1003).ConfigureAwait(false);
                    return;
            }

            if (!fin)
            {
                continue;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length);
            }
            catch (DecoderFallbackException)
            {
                // RFC 6455 requires close code 1007 for invalid UTF-8.
                await CloseAsync(session, 1007).ConfigureAwait(false);
                return;
            }

            try
            {
                this.onMessage(session.Sequence, text);
            }
            catch (Exception ex)
            {
                // A message handler error must not disconnect the session.
                Services.Log.Warning($"message handler threw: {ex.Message}");
            }

            assembled.SetLength(0);
            assembling = false;
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> into, CancellationToken token)
    {
        var read = 0;
        while (read < into.Length)
        {
            var got = await stream.ReadAsync(into[read..], token).ConfigureAwait(false);
            if (got <= 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }

    internal void Disconnect(long sequence)
    {
        Session? session;
        lock (this.gate)
        {
            session = this.peer?.Sequence == sequence ? this.peer : null;
        }

        session?.Dispose();
    }

    /// <summary>Queue text without blocking the draw thread. Preserves order among retained
    /// frames and never throws.</summary>
    internal void Send(string text)
        => Volatile.Read(ref this.peer)?.Enqueue(BuildFrame(0x1, Encoding.UTF8.GetBytes(text)));

    /// <summary>Wait briefly for the close frame to be sent before the caller disposes the
    /// socket.</summary>
    private static async Task CloseAsync(Session session, ushort status)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, status);
        session.Enqueue(BuildFrame(0x8, payload));
        session.StopAcceptingSends();

        try
        {
            await session.Drained.WaitAsync(TimeSpan.FromMilliseconds(CloseFlushMs))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Close the socket if the peer does not read the close frame in time.
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"close flush failed: {ex.Message}");
        }

        await DrainInboundAsync(session).ConfigureAwait(false);
    }

    /// <summary>Drain incoming data so closing the socket does not reset TCP and discard
    /// the close frame. Limit both time and bytes to ensure shutdown completes.</summary>
    private static async Task DrainInboundAsync(Session session)
    {
        var scratch = new byte[4096];
        var deadline = Environment.TickCount64 + CloseDrainMs;
        var budget = MaxMessageBytes + (MaxMessageBytes / 2);

        try
        {
            while (budget > 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return;
                }

                var read = await session.Stream.ReadAsync(scratch)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(left))
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                budget -= read;
            }
        }
        catch (Exception)
        {
            // The drain ends on timeout, reset or disposal.
        }
    }

    private static byte[] BuildFrame(int opcode, byte[] payload)
    {
        // Server frames are never masked. One frame per message, FIN always set.
        int headerLength;
        if (payload.Length <= 125)
        {
            headerLength = 2;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            headerLength = 4;
        }
        else
        {
            headerLength = 10;
        }

        var frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)(0x80 | opcode);
        if (headerLength == 2)
        {
            frame[1] = (byte)payload.Length;
        }
        else if (headerLength == 4)
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)payload.Length);
        }
        else
        {
            frame[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)payload.Length);
        }

        payload.CopyTo(frame, headerLength);
        return frame;
    }

    /// <summary>One writer preserves the order of queued frames.</summary>
    private static async Task PumpAsync(Session session)
    {
        try
        {
            await foreach (var frame in session.Outbox.Reader.ReadAllAsync(session.Token)
                               .ConfigureAwait(false))
            {
                await session.Stream.WriteAsync(frame, session.Token).ConfigureAwait(false);
                await session.Stream.FlushAsync(session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during session shutdown.
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"send failed: {ex.Message}");

            // Close the session on send failure so it cannot keep receiving and queueing
            // undeliverable replies.
            session.Dispose();
        }
        finally
        {
            session.MarkDrained();
        }
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken token)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    public void Dispose()
    {
        Session[] open;
        Task[] running;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            Interlocked.Exchange(ref this.peer, null);
            open = this.sessions.Keys.ToArray();
            // Wait for send, receive and accept tasks before unloading.
            running = this.sessions.Values.Concat(open.Select(s => s.Pump)).Concat(this.acceptTasks).ToArray();
        }

        try
        {
            this.cts?.Cancel();
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"cancel failed: {ex.Message}");
        }

        foreach (var listener in this.listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"listener stop failed: {ex.Message}");
            }
        }

        this.listeners.Clear();
        // Close every socket to unblock pending reads. Continue if one close fails so the
        // remaining sessions still stop.
        foreach (var session in open)
        {
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"session dispose failed: {ex.Message}");
            }
        }

        // Bound the wait for callbacks so plugin unload cannot hang.
        try
        {
            if (!Task.WhenAll(running).Wait(DisposeDrainMs))
            {
                Services.Log.Warning("a link session did not stop in time");
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"session drain: {ex.Message}");
        }

        this.sessions.Clear();
        this.cts?.Dispose();
        this.cts = null;
    }

    private sealed class Session : IDisposable
    {
        private static long counter;

        private readonly TcpClient client;
        private readonly object lifecycleGate;
        private readonly CancellationTokenSource cts = new();
        private readonly TaskCompletionSource drained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int disposedFlag;
        private int establishedFlag;

        internal Session(TcpClient client, object lifecycleGate)
        {
            this.lifecycleGate = lifecycleGate;
            this.client = client;
            this.Sequence = Interlocked.Increment(ref counter);
            this.Stream = client.GetStream();
            this.Outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboxCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        }

        /// <summary>Connection accept order, used for ownership and eviction.</summary>
        internal long Sequence { get; }

        internal Stream Stream { get; }

        internal Channel<byte[]> Outbox { get; }

        internal CancellationToken Token => this.cts.Token;

        /// <summary>Completes when the send pump exits or the session is
        /// disposed.</summary>
        internal Task Drained => this.drained.Task;

        /// <summary>Track the send task so disposal can wait for it alongside the receive
        /// task.</summary>
        internal Task Pump { get; set; } = Task.CompletedTask;

        internal bool IsDisposed => Volatile.Read(ref this.disposedFlag) != 0;

        /// <summary>Established sessions cannot be evicted to make room for an incomplete
        /// handshake.</summary>
        internal bool Established => Volatile.Read(ref this.establishedFlag) != 0;

        internal void MarkEstablished() => Volatile.Write(ref this.establishedFlag, 1);

        internal void Enqueue(byte[] frame)
        {
            this.Outbox.Writer.TryWrite(frame);
        }

        /// <summary>Complete the queue so the pump exits after sending retained
        /// frames.</summary>
        internal void StopAcceptingSends() => this.Outbox.Writer.TryComplete();

        internal void MarkDrained() => this.drained.TrySetResult();

        public void Dispose()
        {
            lock (this.lifecycleGate)
            {
                if (Interlocked.Exchange(ref this.disposedFlag, 1) != 0)
                {
                    return;
                }
            }

            this.Outbox.Writer.TryComplete();

            try
            {
                this.cts.Cancel();
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"session cancel failed: {ex.Message}");
            }

            // Closing the socket is what unblocks a read already in flight.
            try
            {
                this.client.Close();
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"session close failed: {ex.Message}");
            }

            try
            {
                this.client.Dispose();
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"session dispose failed: {ex.Message}");
            }

            // Release close waiters when the socket is disposed.
            this.drained.TrySetResult();
            this.cts.Dispose();
        }
    }
}
