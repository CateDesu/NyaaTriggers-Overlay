using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NyaaTriggers.Plugin.Meter;

/// <summary>Instances can start only once. Queue complete messages through the owner.</summary>
internal sealed class IinactClient : IDisposable
{
    private const int MaxMessageBytes = 4 << 20;

    private const int ConnectTimeoutSeconds = 10;

    private const int StopWaitMs = 4000;

    private const string Subscribe =
        "{\"call\":\"subscribe\",\"events\":[" +
        "\"LogLine\",\"ChangePrimaryPlayer\",\"ChangeZone\",\"PartyChanged\",\"InCombat\"]}";

    /// <summary>Fetch jobs because earlier spawn lines may be unavailable.</summary>
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

    /// <summary>The config window accepts delayed status updates without locking.</summary>
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

    /// <summary>Cancel immediately. Dispose waits for cleanup.</summary>
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
                // The loop may still read the token source. Leave it alive until collected.
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
