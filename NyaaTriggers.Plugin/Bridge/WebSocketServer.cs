using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NyaaTriggers.Plugin.Bridge;

/// <summary>
/// Minimal RFC 6455 server for exactly one trusted local client.
///
/// Why hand-rolled rather than <see cref="HttpListener"/>: HttpListener's
/// WebSocket support sits on http.sys, which is not something to rely on with
/// the game running under Wine. A raw <see cref="TcpListener"/> plus the
/// handshake and framing works anywhere a socket does.
///
/// Deliberately narrow: no extensions, no subprotocols, no fragmentation on
/// send, text frames only. Anything outside that ends the session rather than
/// being interpreted.
/// </summary>
internal sealed class WebSocketServer : IDisposable
{
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Refuse anything larger. The app sends short JSON lines; a
    /// multi-megabyte "message" is a bug or a stranger, and either way is not
    /// worth allocating for.</summary>
    private const int MaxMessageBytes = 1 << 20;

    private const int MaxHandshakeBytes = 8 << 10;

    /// <summary>RFC 6455 caps control frame payloads at 125 bytes.</summary>
    private const int MaxControlPayload = 125;

    /// <summary>Only one client is ever wanted. A couple of slots absorb a
    /// reconnect racing the old session's teardown; past that, something is
    /// wrong and unbounded sessions are not worth the memory.</summary>
    private const int MaxSessions = 4;

    /// <summary>A peer that opens a socket and says nothing must not hold a
    /// session slot forever.</summary>
    private const int HandshakeTimeoutMs = 5000;

    /// <summary>Outbound backlog before messages start dropping. Sends must
    /// never block the draw thread, so a wedged peer is dropped, not waited on.</summary>
    private const int OutboxCapacity = 256;

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    private readonly int port;
    private readonly Action<string> onMessage;
    private readonly Action<bool> onConnectionChanged;

    private readonly List<TcpListener> listeners = new();

    /// <summary>Every accepted session, including ones still in the handshake.
    /// Dispose walks this: a session left running after Dalamud tears down the
    /// plugin's load context executes freed code and takes the game with it.</summary>
    private readonly ConcurrentDictionary<Session, byte> sessions = new();

    private CancellationTokenSource? cts;

    /// <summary>The session that owns the link. Not marked volatile: it is
    /// passed by ref to Interlocked, which rejects volatile fields, so reads go
    /// through Volatile.Read instead.</summary>
    private Session? peer;

    internal WebSocketServer(int port, Action<string> onMessage, Action<bool> onConnectionChanged)
    {
        this.port = port;
        this.onMessage = onMessage;
        this.onConnectionChanged = onConnectionChanged;
    }

    internal bool IsConnected => Volatile.Read(ref this.peer) != null;

    /// <summary>Set when the loopback listener could not bind, for the config
    /// window to show instead of leaving the user staring at a dead toggle.</summary>
    internal string? LastError { get; private set; }

    internal void Start()
    {
        this.cts = new CancellationTokenSource();
        var token = this.cts.Token;

        // Both loopback families. Binding only 127.0.0.1 leaves a client that
        // resolved "localhost" to ::1 connecting to nothing, which presents as
        // "the app says connected but nothing ever draws".
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
                // No IPv6 stack is normal. A failure on 127.0.0.1 is the one the
                // user needs to see, and it must survive ::1 binding fine: the
                // app connects over IPv4 and would otherwise get no explanation.
                if (address.Equals(IPAddress.Loopback))
                {
                    ipv4Error = ex.Message;
                }

                Services.Log.Debug($"listener on {address}:{this.port} failed: {ex.Message}");
                continue;
            }

            bound++;
            this.listeners.Add(listener);
            _ = Task.Run(() => this.AcceptLoopAsync(listener, token), token);
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
                return;   // listener stopped under us: we are shutting down
            }
            catch (SocketException ex)
            {
                // A peer that resets between SYN and accept, or a momentary
                // descriptor shortage, must not permanently stop us listening.
                Services.Log.Debug($"accept failed, still listening: {ex.SocketErrorCode}");
                continue;
            }

            if (this.sessions.Count >= MaxSessions)
            {
                Services.Log.Warning("refusing connection: too many sessions already open");
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => this.ServeAsync(client, token), token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        Session? session = null;
        try
        {
            client.NoDelay = true;   // callouts are latency-critical and tiny
            var stream = client.GetStream();

            // A peer that connects and then says nothing must not hold its slot.
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeCts.CancelAfter(HandshakeTimeoutMs);
            if (!await PerformHandshakeAsync(stream, handshakeCts.Token).ConfigureAwait(false))
            {
                return;
            }

            session = new Session(client, stream);
            this.sessions[session] = 0;

            // One client at a time: a reconnect after the app restarted would
            // otherwise leave two sessions both thinking they own the overlay.
            var previous = Interlocked.Exchange(ref this.peer, session);
            previous?.Dispose();

            _ = Task.Run(() => PumpAsync(session), session.Token);

            try
            {
                this.onConnectionChanged(true);
            }
            catch (Exception ex)
            {
                // Must not escape: the finally below is what releases the slot.
                Services.Log.Warning($"connect handler threw: {ex.Message}");
            }

            Services.Log.Information("NyaaTriggers app connected");
            await this.ReadLoopAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or the handshake timed out. Not worth a log line.
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"session ended: {ex.Message}");
        }
        finally
        {
            if (session != null)
            {
                this.sessions.TryRemove(session, out _);

                // Only clear the shared slot if we are still the current
                // session: a newer client may have replaced us already.
                if (Interlocked.CompareExchange(ref this.peer, null, session) == session)
                {
                    try
                    {
                        this.onConnectionChanged(false);
                    }
                    catch (Exception ex)
                    {
                        Services.Log.Warning($"disconnect handler threw: {ex.Message}");
                    }

                    Services.Log.Information("NyaaTriggers app disconnected");
                }

                session.Dispose();
            }
            else
            {
                client.Dispose();
            }
        }
    }

    // ── handshake ─────────────────────────────────────────────────────────
    private static async Task<bool> PerformHandshakeAsync(Stream stream, CancellationToken token)
    {
        var request = await ReadRequestHeadAsync(stream, token).ConfigureAwait(false);
        if (request == null)
        {
            return false;
        }

        var key = FindHeader(request, "Sec-WebSocket-Key");
        var upgrade = FindHeader(request, "Upgrade");
        var version = FindHeader(request, "Sec-WebSocket-Version");

        // WebSocket is exempt from the same-origin policy, so any page the user
        // happens to be browsing could otherwise open this socket and inject or
        // clear callouts. Browsers always send Origin; the app never does, so
        // refusing any request that carries one costs nothing and closes it.
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

        // No extension is negotiated, so Sec-WebSocket-Extensions is simply not
        // echoed back; per spec the client must then not use one.
        var accept = Convert.ToBase64String(
            Sha1OfHandshakeKey(key! + HandshakeGuid));

        await WriteAsciiAsync(
            stream,
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n",
            token).ConfigureAwait(false);
        return true;
    }

    // SHA-1 is what RFC 6455 specifies for the handshake. It is not being used
    // as a security primitive here, so the weak-hash analysers are suppressed
    // rather than "fixed" into a handshake no client would accept.
#pragma warning disable CA5350, CA5351
    private static byte[] Sha1OfHandshakeKey(string value)
        => SHA1.HashData(Encoding.ASCII.GetBytes(value));
#pragma warning restore CA5350, CA5351

    private static async Task<string?> ReadRequestHeadAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHandshakeBytes];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), token)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                return null;   // peer hung up mid-handshake
            }

            used += read;

            var end = buffer.AsSpan(0, used).IndexOf(HeaderTerminator);
            if (end < 0)
            {
                continue;
            }

            // Anything after the blank line would be frame bytes read into this
            // buffer and then dropped, desyncing the read loop. The app does not
            // pipeline, so refuse rather than carry a pushback buffer around.
            if (used > end + HeaderTerminator.Length)
            {
                Services.Log.Debug("refusing a handshake with pipelined data");
                return null;
            }

            return Encoding.ASCII.GetString(buffer, 0, end);
        }

        return null;   // no blank line within the cap: not a handshake
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

    // ── frames ────────────────────────────────────────────────────────────
    private async Task ReadLoopAsync(Session session)
    {
        var stream = session.Stream;
        var token = session.Token;
        var header = new byte[8];
        var mask = new byte[4];

        // Continuation frames accumulate here until the FIN frame arrives.
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

            // Reserved bits set means an extension we never negotiated; an
            // unmasked client frame is a protocol violation; control frames may
            // not be fragmented or exceed 125 bytes; and nothing may exceed the
            // message cap. Each of these ends the session rather than being
            // guessed at.
            if (reserved != 0 || !masked || length < 0 || length > MaxMessageBytes ||
                (control && (!fin || length > MaxControlPayload)) ||
                assembled.Length + length > MaxMessageBytes)
            {
                this.SendClose(session, 1002);
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
                        this.SendClose(session, 1002);
                        return;
                    }

                    assembled.Write(payload);
                    break;

                case 0x1:   // text
                    // A new text frame while a fragmented one is still open
                    // would splice two JSON documents into one "message".
                    if (assembling)
                    {
                        this.SendClose(session, 1002);
                        return;
                    }

                    assembling = true;
                    assembled.Write(payload);
                    break;

                case 0x8:   // close
                    this.SendClose(session, 1000);
                    return;

                case 0x9:   // ping
                    session.Enqueue(BuildFrame(0xA, payload));
                    continue;

                case 0xA:   // pong
                    continue;

                default:    // binary or reserved: not part of this protocol
                    this.SendClose(session, 1003);
                    return;
            }

            if (!fin)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length);
            try
            {
                this.onMessage(text);
            }
            catch (Exception ex)
            {
                // A bad message must not kill the session, or one typo in a
                // callout takes the whole link down mid-pull.
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

    /// <summary>Queue a text message to the app. Safe from the draw thread:
    /// returns immediately, preserves order, and never throws.</summary>
    internal void Send(string text)
        => Volatile.Read(ref this.peer)?.Enqueue(BuildFrame(0x1, Encoding.UTF8.GetBytes(text)));

    private void SendClose(Session session, ushort status)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, status);
        session.Enqueue(BuildFrame(0x8, payload));

        // Let the pump flush the close frame before the socket goes away; the
        // peer otherwise sees a bare reset instead of a reason.
        session.CloseAfterFlush();
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

    /// <summary>Single writer per session: the channel is what guarantees the
    /// hello reaches the app before anything queued after it.</summary>
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
            // Session is going away.
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"send failed: {ex.Message}");
        }
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken token)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    public void Dispose()
    {
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
        Interlocked.Exchange(ref this.peer, null);

        // Every session, not just the current one: a socket read in flight does
        // not honour a token, so the only way to end those tasks before the
        // plugin's load context goes away is to close the sockets under them.
        foreach (var session in this.sessions.Keys)
        {
            this.sessions.TryRemove(session, out _);
            session.Dispose();
        }

        this.cts?.Dispose();
        this.cts = null;
    }

    /// <summary>One accepted connection: its socket, its outbound queue, and the
    /// token that ends both.</summary>
    private sealed class Session : IDisposable
    {
        private readonly TcpClient client;
        private readonly CancellationTokenSource cts = new();
        private int disposed;

        internal Session(TcpClient client, Stream stream)
        {
            this.client = client;
            this.Stream = stream;
            this.Outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboxCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        }

        internal Stream Stream { get; }

        internal Channel<byte[]> Outbox { get; }

        internal CancellationToken Token => this.cts.Token;

        internal void Enqueue(byte[] frame)
        {
            // Bounded and drop-oldest, so a wedged peer costs a stale callout
            // rather than unbounded memory or a blocked caller.
            this.Outbox.Writer.TryWrite(frame);
        }

        /// <summary>Stop accepting new frames and let the pump drain what is
        /// already queued, so a close frame actually goes out.</summary>
        internal void CloseAfterFlush() => this.Outbox.Writer.TryComplete();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
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

            this.client.Dispose();
            this.cts.Dispose();
        }
    }
}
