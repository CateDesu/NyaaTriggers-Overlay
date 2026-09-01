using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NyaaTriggers.Plugin.Meter;

/// <summary>Client for IINACT's ACT-compatible WebSocket feed.
///
/// The standalone meter's data source. Dials IINACT, subscribes to the events
/// the meter needs and hands whole text frames to the owner, which applies
/// them on the draw thread. Owns a reconnect loop with backoff, so IINACT
/// being installed later or restarted under us heals on its own.
///
/// Single use: Start once, Stop once. A changed endpoint gets a fresh
/// instance, the cancelled token and the socket cannot be re-armed.
/// </summary>
internal sealed class IinactClient : IDisposable
{
    /// <summary>Inbound frame cap, same as the program's. Real ACT frames are
    /// kilobytes; a peer sending more is broken, not chatty.</summary>
    private const int MaxMessageBytes = 4 << 20;

    /// <summary>Connect plus handshake budget. A host that accepts then goes
    /// silent mid handshake must not park the retry loop on a stuck socket.</summary>
    private const int ConnectTimeoutSeconds = 10;

    /// <summary>How long Stop's background drain and unload wait for the loop
    /// to unwind. Bounded: a wedged socket must not hang plugin teardown.</summary>
    private const int StopWaitMs = 4000;

    /// <summary>The event set the meter needs. CombatData is skipped: the
    /// program only subscribes it for its sidecar tee, and the meter reads
    /// everything off the log lines themselves.</summary>
    private const string Subscribe =
        "{\"call\":\"subscribe\",\"events\":[" +
        "\"LogLine\",\"ChangePrimaryPlayer\",\"ChangeZone\",\"PartyChanged\",\"InCombat\"]}";

    /// <summary>One roster fetch right after subscribing. The 03 burst is long
    /// gone on a mid-instance start, and this backfills the party's jobs.</summary>
    private const string GetCombatants = "{\"call\":\"getCombatants\"}";

    private readonly Uri endpoint;
    private readonly Action<string> onMessage;

    /// <summary>Guards the loop task handle, so a Stop on the draw thread
    /// cannot race the loop being published.</summary>
    private readonly object gate = new();

    private readonly CancellationTokenSource stop = new();

    private Task? loop;
    private volatile bool running;
    private volatile bool connected;
    private volatile string status = "Connecting to IINACT.";

    internal IinactClient(Uri endpoint, Action<string> onMessage)
    {
        this.endpoint = endpoint;
        this.onMessage = onMessage;
    }

    /// <summary>Read unsynchronized by the config window on the draw thread
    /// while the loop thread writes it. Reference writes are atomic, and a
    /// frame of lag in a status line is harmless.</summary>
    internal string Status => this.status;

    internal bool IsConnected => this.connected;

    internal void Start()
    {
        lock (this.gate)
        {
            if (this.loop != null)
            {
                return;
            }

            this.running = true;
            this.loop = Task.Run(this.RunAsync);
        }
    }

    /// <summary>Cancel the loop and unwind it in the background. Never blocks:
    /// Stop runs on the draw thread (toggle off, app connected, endpoint
    /// change), where waiting on a socket would freeze the game. Dispose
    /// still waits, bounded, so the task cannot outlive the load context.</summary>
    internal void Stop()
    {
        this.running = false;
        this.stop.Cancel();
    }

    private async Task RunAsync()
    {
        var backoffMs = 5000;
        while (this.running)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(this.stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
                this.status = "Connecting to IINACT.";
                await ws.ConnectAsync(this.endpoint, timeout.Token).ConfigureAwait(false);
                this.connected = true;
                this.status = "Connected to IINACT.";
                backoffMs = 5000;   // a good connect re-arms the short retry
                await ws.SendAsync(
                    Encoding.UTF8.GetBytes(Subscribe), WebSocketMessageType.Text, true, this.stop.Token)
                    .ConfigureAwait(false);
                await ws.SendAsync(
                    Encoding.UTF8.GetBytes(GetCombatants), WebSocketMessageType.Text, true, this.stop.Token)
                    .ConfigureAwait(false);
                await this.ReceiveLoop(ws).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!this.running)
            {
                break;   // our own Stop, nothing to report
            }
            catch (OperationCanceledException)
            {
                // The connect timeout fired. The generic retry text applies,
                // but the raw cancellation message would read as noise.
                this.status = "IINACT did not answer in time.";
            }
            catch (Exception ex)
            {
                this.status = $"No IINACT feed: {ex.Message}";
            }
            finally
            {
                this.connected = false;
            }

            if (!this.running)
            {
                break;
            }

            this.status = $"{this.status} Retrying in {backoffMs / 1000}s.";
            try
            {
                await Task.Delay(backoffMs, this.stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoffMs = Math.Min(backoffMs * 2, 60000);
        }
    }

    /// <summary>Read whole text frames until the peer closes or the socket
    /// faults. Binary frames are drained and dropped, IINACT only sends text.
    /// Returns on a clean close too; the outer loop treats both as retry.</summary>
    private async Task ReceiveLoop(ClientWebSocket ws)
    {
        var chunk = new byte[8192];
        while (this.running && ws.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(chunk, this.stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    message.Write(chunk, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        // Over the cap the peer is broken. Abort, not a polite
                        // close: a flooding peer may never read our close, and
                        // this loop must not outlive Stop's bounded wait.
                        ws.Abort();
                        return;
                    }
                }
            }
            while (!result.EndOfMessage);

            if (message.Length > 0)
            {
                this.onMessage(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
    }

    public void Dispose()
    {
        this.Stop();

        Task? loop;
        lock (this.gate)
        {
            loop = this.loop;
            this.loop = null;
        }

        if (loop == null)
        {
            this.stop.Dispose();
            return;
        }

        try
        {
            if (!loop.Wait(StopWaitMs))
            {
                // Leave stop undisposed: the loop is still out there reading
                // its Token, and a disposed source turns that read into an
                // ObjectDisposedException instead of the clean cancel. The
                // cancel is already in flight, so the loop still unwinds and
                // the GC reclaims the source once it is done.
                Services.Log.Warning("an IINACT feed session did not stop in time");
                return;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"IINACT feed drain: {ex.Message}");
        }

        this.stop.Dispose();
    }
}
