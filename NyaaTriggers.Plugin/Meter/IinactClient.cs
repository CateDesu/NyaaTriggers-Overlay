using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NyaaTriggers.Plugin.Meter;

/// <summary>Connects to IINACT, subscribes to meter events and passes complete text
/// messages to the owner for queued processing. Reconnects with backoff. Each instance can
/// be started only once.</summary>
internal sealed class IinactClient : IDisposable
{
    /// <summary>Bounds incoming message allocation.</summary>
    private const int MaxMessageBytes = 4 << 20;

    /// <summary>Bounds connection and handshake time so an unresponsive host cannot stop
    /// retries.</summary>
    private const int ConnectTimeoutSeconds = 10;

    /// <summary>Maximum wait in milliseconds for the receive loop during
    /// disposal.</summary>
    private const int StopWaitMs = 4000;

    /// <summary>Subscribe to raw log and state events. The meter does not use CombatData
    /// summaries.</summary>
    private const string Subscribe =
        "{\"call\":\"subscribe\",\"events\":[" +
        "\"LogLine\",\"ChangePrimaryPlayer\",\"ChangeZone\",\"PartyChanged\",\"InCombat\"]}";

    /// <summary>Fetch jobs on connect because earlier spawn lines may no longer be
    /// available.</summary>
    private const string GetCombatants = "{\"call\":\"getCombatants\"}";

    private readonly Uri endpoint;
    private readonly Action<string> onMessage;
    private readonly Action onSessionEnd;

    /// <summary>Makes loop task registration atomic with Stop.</summary>
    private readonly object gate = new();

    private readonly CancellationTokenSource stop = new();

    private Task? loop;
    private volatile bool running;
    private volatile bool connected;
    private volatile string status = "Connecting to IINACT.";

    internal IinactClient(Uri endpoint, Action<string> onMessage, Action? onSessionEnd = null)
    {
        this.endpoint = endpoint;
        this.onMessage = onMessage;
        this.onSessionEnd = onSessionEnd ?? (() => { });
    }

    /// <summary>Read without a lock by the config window. A delayed status update is
    /// acceptable.</summary>
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

    /// <summary>Cancel without blocking the caller. Dispose separately waits for the loop
    /// with a deadline.</summary>
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
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(90);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(this.stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
                this.status = "Connecting to IINACT.";
                await ws.ConnectAsync(this.endpoint, timeout.Token).ConfigureAwait(false);
                this.connected = true;
                this.status = "Connected to IINACT.";
                backoffMs = 5000;   // Reset retry delay after a successful connection.
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
                break;
            }
            catch (OperationCanceledException)
            {
                this.status = "IINACT did not answer in time.";
            }
            catch (Exception ex)
            {
                this.status = $"No IINACT feed: {ex.Message}";
            }
            finally
            {
                this.connected = false;
                this.onSessionEnd();
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

    /// <summary>Read complete text messages and discard binary messages. The outer loop
    /// reconnects after both errors and clean closes.</summary>
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
                        // Abort oversized messages without waiting for the peer to read a
                        // close frame.
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
                // Leave the token source alive while the loop may still read it.
                // Cancellation is already requested, and the source can be collected when
                // the loop exits.
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
