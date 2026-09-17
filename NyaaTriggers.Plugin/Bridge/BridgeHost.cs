using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using NyaaTriggers.Plugin.Meter;

namespace NyaaTriggers.Plugin.Bridge;

internal enum Severity
{
    Info,
    Alert,
    Alarm,
}

internal readonly record struct TimelineEntry(float Time, string Label, string Kind);

internal readonly record struct DpsRow(string Name, string Job, double Dps, double Share, double Hps, bool IsSelf, int Deaths, int Rank = 0);

/// <summary>Replaced as a whole so drawing never sees a partially updated DPS
/// frame.</summary>
internal sealed class DpsState
{
    internal bool Show { get; init; }

    /// <summary>Keeps the final rows available to the hold option, even if the last live
    /// frame and ending arrive in one update. A later clear hides them. Wipes send clear
    /// before the ending so the final rows remain visible.</summary>
    internal bool Ended { get; init; }

    internal string Title { get; init; } = string.Empty;

    internal string Duration { get; init; } = string.Empty;

    internal double EncDps { get; init; }

    internal IReadOnlyList<DpsRow> Rows { get; init; } = Array.Empty<DpsRow>();
}

internal sealed class ActiveAlert
{
    internal required string Text { get; init; }

    internal required Severity Severity { get; init; }

    /// <summary>Expiry in monotonic milliseconds. Repeated alerts can extend it.</summary>
    internal required long ExpiresAt { get; set; }

    /// <summary>Reset on a merged repeat to restart the entry animation.</summary>
    internal required long ShownAt { get; set; }

    /// <summary>Repeat count for the current top callout. The counter is displayed only
    /// above one.</summary>
    internal int Count { get; set; } = 1;
}

/// <summary>Socket threads queue messages. <see cref="Update"/> applies them under the UI
/// state lock shared with drawing and settings.</summary>
internal sealed class BridgeHost : IDisposable
{
    /// <summary>Increment for incompatible protocol changes. The program checks this in the
    /// greeting.</summary>
    internal const int ProtocolVersion = 1;

    /// <summary>Limits message processing to avoid stalling the render thread.</summary>
    private const int MaxMessagesPerFrame = 64;

    private const int MaxAlerts = 8;

    /// <summary>Allows complete timelines with hundreds of entries while bounding retained
    /// state.</summary>
    private const int MaxTimelineEntries = 1024;

    /// <summary>A full alliance. The display setting may show fewer rows.</summary>
    private const int MaxDpsRows = 24;

    /// <summary>Bounds text measured and drawn every frame, independently of the wire size
    /// limit.</summary>
    private const int MaxTextChars = 256;

    /// <summary>Maximum wait in milliseconds for background server disposal during
    /// unload.</summary>
    private const int DrainWaitMs = 4000;

    internal object StateLock { get; } = new();

    private long? messageStamp;
    private readonly Configuration config;
    private readonly MessageInbox inbox = new();
    private long droppedMessages;
    private long nextWarning;
    private readonly List<TimelineEntry> timeline = new();
    private readonly List<ActiveAlert> alerts = new();

    /// <summary>Runs when no program session is connected. Updated under the UI state
    /// lock.</summary>
    private readonly StandaloneMeter standalone;

    /// <summary>Makes server replacement, source checks and inbox writes atomic with
    /// shutdown.</summary>
    private readonly object serverLock = new();

    /// <summary>Unload waits for these server tasks so their callbacks can
    /// finish.</summary>
    private readonly List<Task> pendingDrains = new();

    /// <summary>Read by socket threads without taking serverLock.</summary>
    private volatile WebSocketServer? server;

    /// <summary>Current session sequence, guarded by serverLock. Rejects late messages and
    /// disconnects from replaced sessions on the same server.</summary>
    private long currentSession;

    /// <summary>Fight time at <see cref="clockStamp"/>, interpolated between program
    /// ticks.</summary>
    private double clockBase;
    private long clockStamp;
    private bool clockRunning;

    internal BridgeHost(Configuration config)
    {
        this.config = config;
        this.standalone = new StandaloneMeter(config, () => this.IsConnected, this.ApplyLocalDps, this.ClearLocalDps);
    }

    /// <summary>Read from the current server so an old callback cannot leave a stale
    /// connection flag.</summary>
    internal bool IsConnected => this.server?.IsConnected ?? false;

    internal string? LastError => this.server?.LastError;

    internal IReadOnlyList<TimelineEntry> Timeline => this.timeline;

    internal IReadOnlyList<ActiveAlert> Alerts => this.alerts;

    internal DpsState Dps { get; private set; } = new();

    private DpsState? lastLocal;

    /// <summary>Retains the latest rows even if no draw occurs before the ending. Survives
    /// clear so a wipe followed by show:false can preserve the final rows.</summary>
    private DpsState? lastLive;

    internal double Clock => this.clockRunning
        ? this.clockBase + ((Environment.TickCount64 - this.clockStamp) / 1000.0)
        : this.clockBase;

    /// <summary>Keeps the timeline clock hidden until the first tick.</summary>
    internal bool ClockRunning => this.clockRunning;

    internal void Start()
    {
        this.Stop();

        // Capture the source to reject callbacks from a replaced server.
        WebSocketServer? created = null;
        created = new WebSocketServer(
            this.config.Port,
            (seq, raw) => this.Receive(created!, seq, raw),
            (seq, connected) => this.OnConnectionChanged(created!, seq, connected),
            () => this.Greeting(created!));
        this.server = created;
        created.Start();
    }

    internal void Stop()
    {
        WebSocketServer? old;
        lock (this.serverLock)
        {
            old = this.server;
            this.server = null;
        }

        // The listener may have disconnected before its queued clear runs.
        // Preserve only rows still owned by the independent standalone feed.
        this.ClearState(resetDps: !ReferenceEquals(this.Dps, this.lastLocal));
        this.lastLive = null;
        // Discard queued frames from the old server. Keep this out of ClearState because a
        // zone change queues a fresh timeline immediately after clear.
        this.inbox.Clear();

        if (old == null)
        {
            return;
        }

        // Dispose in the background because settings apply on the render thread. Old
        // callbacks are already rejected, and unload waits for disposal. A port rebind may
        // need a retry if the old listener has not closed yet.
        var drain = Task.Run(old.Dispose);
        lock (this.serverLock)
        {
            this.pendingDrains.RemoveAll(t => t.IsCompleted);
            this.pendingDrains.Add(drain);
        }
    }

    /// <summary>Queue socket messages under serverLock. Source and session checks must be
    /// atomic with replacement and inbox clearing so stale frames cannot arrive after a
    /// reset.</summary>
    private void Receive(WebSocketServer source, long sequence, string raw)
    {
        var overloaded = false;
        lock (this.serverLock)
        {
            if (!ReferenceEquals(source, this.server) ||
                sequence != this.currentSession)
            {
                return;
            }

            if (!this.inbox.TryEnqueue(raw))
            {
                this.droppedMessages++;
                this.inbox.Clear();
                this.inbox.TryEnqueue(null);
                overloaded = true;
            }
        }

        if (overloaded)
        {
            // A reconnect makes the program resend its complete schedule.
            source.Disconnect(sequence);
        }
    }

    /// <summary>Rebind after a port change.</summary>
    internal void Restart() => this.Start();

    internal void RestartStandalone() => this.standalone.Restart();

    internal StandaloneState StandaloneStatus => this.standalone.State;

    internal string StandaloneStatusText => this.standalone.Status;

    /// <summary>Apply standalone frames only while the program is disconnected.</summary>
    private void ApplyLocalDps(DpsState state)
    {
        if (!this.IsConnected)
        {
            this.Dps = state;
            this.lastLocal = state;
        }
    }

    /// <summary>Clear only the state the standalone feed still owns.
    /// A program frame applied earlier in this update must survive.</summary>
    private void ClearLocalDps()
    {
        if (ReferenceEquals(this.Dps, this.lastLocal))
        {
            this.Dps = new DpsState();
        }

        this.lastLocal = null;
    }

    private void OnConnectionChanged(WebSocketServer source, long sequence, bool connected)
    {
        // Keep the source check and enqueue atomic with Stop so an old disconnect cannot
        // clear the new server state.
        lock (this.serverLock)
        {
            if (!ReferenceEquals(source, this.server))
            {
                return;
            }

            if (connected)
            {
                // Reject a session start that was overtaken by a newer session.
                if (sequence <= this.currentSession)
                {
                    return;
                }

                this.currentSession = sequence;

                // New session frames arrive after this callback, so the old backlog can be
                // cleared.
                this.inbox.Clear();
                this.inbox.TryEnqueue(null);
                return;
            }

            // An old disconnect must not clear the current session.
            if (sequence != this.currentSession)
            {
                return;
            }

            this.inbox.Clear();
            this.inbox.TryEnqueue(null);
        }
    }

    /// <summary>Return the greeting so the server can queue it before publishing the
    /// session. This guarantees it is the first frame.</summary>
    private string? Greeting(WebSocketServer source)
        => ReferenceEquals(source, this.server)
            ? $"{{\"ev\":\"hello\",\"protocol\":{ProtocolVersion}," +
              $"\"plugin\":{JsonSerializer.Serialize(PluginVersion.Value)}}}"
            : null;

    /// <summary>Drain the inbox and expire stale alerts. Serialized with drawing and settings.</summary>
    internal void Update()
    {
        lock (this.StateLock) this.UpdateState();
    }

    private void UpdateState()
    {
        var bytes = MessageInbox.FrameBytes;
        for (var count = 0; count < MaxMessagesPerFrame &&
             this.inbox.TryDequeue(ref bytes, count == 0, out var raw, out var receivedAt); count++)
        {
            try
            {
                this.messageStamp = receivedAt;
                if (raw == null)
                {
                    this.lastLive = null;
                    this.ClearState(resetDps: true);
                }
                else
                {
                    this.Apply(raw);
                }
            }
            catch (Exception ex)
            {
                this.Warn($"bad message from the program: {ex.Message}");
            }
            finally
            {
                this.messageStamp = null;
            }
        }

        lock (this.serverLock)
        {
            if (this.droppedMessages > 0 && Environment.TickCount64 >= this.nextWarning)
            {
                this.Warn($"program inbox overloaded {this.droppedMessages} times, reconnecting to refresh overlay state");
                this.droppedMessages = 0;
            }
        }

        var now = Environment.TickCount64;
        this.alerts.RemoveAll(a => a.ExpiresAt <= now);

        // Process standalone updates last. ApplyLocalDps rejects them while a program
        // session is connected.
        this.standalone.Update();
    }

    private void Warn(string message)
    {
        var now = Environment.TickCount64;
        if (now >= this.nextWarning)
        {
            this.nextWarning = now + 5000;
            Services.Log.Warning(message);
        }
    }

    private void Apply(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("c", out var command) ||
            command.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (command.GetString())
        {
            case "tick":
                // Invalid ticks must not reset the fight clock to zero.
                if (root.TryGetProperty("t", out var tick) && TryFinite(tick, out var time))
                {
                    this.clockBase = time;
                    this.clockStamp = this.messageStamp ?? Environment.TickCount64;
                    this.clockRunning = true;
                }

                break;

            case "timeline":
                this.ApplyTimeline(root);
                break;

            case "alert":
                this.ApplyAlert(root);
                break;

            case "dps":
                this.ApplyDps(root);
                break;

            case "clear":
                // The program owns the meter for this session, so clear its DPS rows too.
                this.ClearState(resetDps: true);
                break;

            case "ping":
                this.server?.Send("{\"ev\":\"pong\"}");
                break;

            default:
                // Ignore unknown commands for compatibility with newer senders.
                break;
        }
    }

    private void ApplyTimeline(JsonElement root)
    {
        // Validate before replacing the live schedule.
        if (!root.TryGetProperty("v", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        this.timeline.Clear();

        foreach (var entry in entries.EnumerateArray())
        {
            // Entries are [time, label] or [time, label, kind]. Missing and unknown kinds
            // use the default mechanic appearance.
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
            {
                continue;
            }

            var time = entry[0];
            var label = entry[1];
            if (!TryFinite(time, out var at) || !float.IsFinite((float)at) || label.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var kind = string.Empty;
            if (entry.GetArrayLength() > 2 && entry[2].ValueKind == JsonValueKind.String)
            {
                kind = SanitizeText(entry[2].GetString(), 32);
            }

            var text = SanitizeText(label.GetString(), MaxTextChars);
            if (!string.IsNullOrWhiteSpace(text))
            {
                this.timeline.Add(new TimelineEntry((float)at, text, kind));
            }

            if (this.timeline.Count >= MaxTimelineEntries)
            {
                Services.Log.Debug($"timeline truncated at {MaxTimelineEntries} entries");
                break;
            }
        }

        this.timeline.Sort(static (a, b) => a.Time.CompareTo(b.Time));
    }

    private void ApplyAlert(JsonElement root)
    {
        if (!root.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        // Limit text before wrapping to bound allocations and layout work on each frame.
        const int MaxAlertTextChars = 4096;
        var text = SanitizeText(textElement.GetString(), MaxAlertTextChars);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var severity = Severity.Info;
        if (root.TryGetProperty("sev", out var sev) && sev.ValueKind == JsonValueKind.String)
        {
            severity = sev.GetString() switch
            {
                "alarm" => Severity.Alarm,
                "alert" => Severity.Alert,
                _ => Severity.Info,
            };
        }

        // Use the configured duration for this severity unless the message supplies a ttl.
        var seconds = severity switch
        {
            Severity.Alarm => this.config.AlertSecondsAlarm,
            Severity.Alert => this.config.AlertSecondsAlert,
            _ => this.config.AlertSeconds,
        };

        if (root.TryGetProperty("ttl", out var ttl))
        {
            if (!TryFinite(ttl, out var ttlSeconds)) return;
            seconds = (float)Math.Clamp(ttlSeconds, 0.5, 30.0);
        }

        // Keep callouts readable without allowing them to remain indefinitely.
        seconds = Math.Clamp(seconds, 0.5f, 30.0f);

        var now = this.messageStamp ?? Environment.TickCount64;
        if (now + (long)(seconds * 1000) <= Environment.TickCount64) return;
        this.Push(new ActiveAlert
        {
            Text = text,
            Severity = severity,
            ShownAt = now,
            ExpiresAt = now + (long)(seconds * 1000),
        });
    }

    private void ApplyDps(JsonElement root)
    {
        if (!root.TryGetProperty("show", out var show) ||
            show.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        // Attach final rows to the ending so the hold option works even if no draw saw the
        // last live frame.
        if (show.ValueKind == JsonValueKind.False)
        {
            var last = this.lastLive;
            this.Dps = last == null
                ? new DpsState { Ended = true }
                : new DpsState
                {
                    Ended = true,
                    Title = last.Title,
                    Duration = last.Duration,
                    EncDps = last.EncDps,
                    Rows = last.Rows,
                };

            return;
        }

        var title = string.Empty;
        var duration = string.Empty;
        var encDps = 0.0;
        if (root.TryGetProperty("enc", out var enc) && enc.ValueKind == JsonValueKind.Object)
        {
            title = SanitizeText(ReadString(enc, "t"), MaxTextChars);
            duration = SanitizeText(ReadString(enc, "d"), MaxTextChars);
            if (enc.TryGetProperty("dps", out var total) && !TryFinite(total, out encDps)) return;
        }

        var rows = new List<DpsRow>();
        if (root.TryGetProperty("rows", out var rowsElement) &&
            rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in rowsElement.EnumerateArray())
            {
                // Rows are [name, job, encdps, share, hps, isSelf, deaths] in descending
                // DPS order. Missing trailing fields from older senders use defaults.
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 4)
                {
                    continue;
                }

                var name = entry[0];
                var job = entry[1];
                var dps = entry[2];
                var share = entry[3];
                if (name.ValueKind != JsonValueKind.String ||
                    job.ValueKind != JsonValueKind.String ||
                    !TryFinite(dps, out var rowDps) ||
                    !TryFinite(share, out var rowShare))
                {
                    continue;
                }

                var hps = 0.0;
                var isSelf = false;
                var deaths = 0;
                if (entry.GetArrayLength() > 4 && entry[4].ValueKind == JsonValueKind.Number)
                {
                    if (!TryFinite(entry[4], out hps)) continue;
                }

                if (entry.GetArrayLength() > 5 && entry[5].ValueKind == JsonValueKind.True)
                {
                    isSelf = true;
                }

                if (entry.GetArrayLength() > 6 && entry[6].ValueKind == JsonValueKind.Number &&
                    entry[6].TryGetInt32(out var parsedDeaths))
                {
                    deaths = Math.Max(parsedDeaths, 0);
                }

                rows.Add(new DpsRow(
                    SanitizeText(name.GetString(), MaxTextChars),
                    SanitizeText(job.GetString(), MaxTextChars),
                    Math.Max(0, rowDps),
                    Math.Clamp(rowShare, 0, 100),
                    Math.Max(0, hps),
                    isSelf,
                    deaths));

                if (rows.Count >= MaxDpsRows)
                {
                    Services.Log.Debug($"dps rows truncated at {MaxDpsRows}");
                    break;
                }
            }
        }

        var state = new DpsState
        {
            Show = true,
            Title = title,
            Duration = duration,
            EncDps = Math.Max(0, encDps),
            Rows = rows,
        };

        this.lastLive = state;
        this.Dps = state;
    }

    /// <summary>Clear program state. Clear DPS only when resetDps is true because
    /// standalone rows must survive server restarts.</summary>
    internal void ClearState(bool resetDps)
    {
        this.timeline.Clear();
        this.alerts.Clear();
        if (resetDps)
        {
            this.Dps = new DpsState();
        }

        this.clockBase = 0;
        this.clockRunning = false;
    }

    /// <summary>Use distinct text for each test severity so the samples do not
    /// merge.</summary>
    internal void PushTestAlert(Severity severity)
    {
        var now = Environment.TickCount64;
        this.Push(new ActiveAlert
        {
            Text = $"Sample {severity.ToString().ToLowerInvariant()}",
            Severity = severity,
            ShownAt = now,
            ExpiresAt = now + 3000,
        });
    }

    /// <summary>Merge repeats of the top callout when enabled. Otherwise append and remove
    /// the oldest alerts above the limit.</summary>
    private void Push(ActiveAlert alert)
    {
        if (this.config.AlertsCollapseDupes && this.alerts.Count > 0)
        {
            var last = this.alerts[^1];
            if (last.Text == alert.Text && last.Severity == alert.Severity && last.ExpiresAt > alert.ShownAt)
            {
                last.Count++;
                last.ShownAt = alert.ShownAt;

                // A repeat with a shorter duration must not shorten the current alert.
                last.ExpiresAt = Math.Max(last.ExpiresAt, alert.ExpiresAt);
                return;
            }
        }

        this.alerts.Add(alert);
        while (this.alerts.Count > MaxAlerts)
        {
            this.alerts.RemoveAt(0);
        }
    }

    private static bool TryFinite(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Remove line breaks to prevent row overlap and cap text length to bound
    /// layout work. Shared with the standalone meter.</summary>
    internal static string SanitizeText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.Replace('\n', ' ').Replace('\r', ' ');
        if (text.Length <= maxChars)
        {
            return text;
        }

        // Avoid truncating inside a surrogate pair.
        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]))
        {
            cut--;
        }

        return text[..cut];
    }

    public void Dispose()
    {
        this.Stop();
        this.standalone.Dispose();

        // Wait for background server disposal before unload, with time for those tasks to
        // start.
        Task[] drains;
        lock (this.serverLock)
        {
            drains = this.pendingDrains.ToArray();
        }

        if (drains.Length == 0)
        {
            return;
        }

        try
        {
            if (!Task.WhenAll(drains).Wait(DrainWaitMs))
            {
                Services.Log.Warning("a link session did not stop in time");
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"link drain: {ex.Message}");
        }
    }
}

internal static class PluginVersion
{
    /// <summary>Include the fourth version component to distinguish rolling builds in the
    /// program status.</summary>
    internal static readonly string Value =
        typeof(PluginVersion).Assembly.GetName().Version?.ToString(4)
        ?? "0.0.0";
}
