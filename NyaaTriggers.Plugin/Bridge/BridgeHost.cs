using System;
using System.Collections.Generic;
using System.Linq;
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

internal readonly record struct DpsRow(string Name, string Job, double Dps, double Share, double Hps, bool IsSelf, int Deaths, int Rank = 0)
{
    internal CombatStats? Stats { get; init; }
}

internal sealed class DpsState
{
    internal string Id { get; init; } = string.Empty;
    internal string Zone { get; init; } = string.Empty;
    internal double EncHps { get; init; }
    internal int Participants { get; init; }
    internal bool Show { get; init; }

    /// <summary>Retain final rows even when live and ending frames arrive in one update.</summary>
    internal bool Ended { get; init; }

    internal string Title { get; init; } = string.Empty;

    internal string Duration { get; init; } = string.Empty;

    internal double EncDps { get; init; }

    internal bool HasDamage { get; init; }

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

    internal int Count { get; set; } = 1;
}

/// <summary>Socket threads queue messages. <see cref="Update"/> applies them under the UI
/// state lock shared with drawing and settings.</summary>
internal sealed class BridgeHost : IDisposable
{
    /// <summary>Increment for incompatible protocol changes.</summary>
    internal const int ProtocolVersion = 1;

    private const int MaxMessagesPerFrame = 64;

    private const int MaxAlerts = 8;

    private const int MaxTimelineEntries = 1024;

    private const int MaxDpsRows = 24;

    /// <summary>Bound layout work independently of wire size.</summary>
    private const int MaxTextChars = 256;

    private const int DrainWaitMs = 4000;

    internal object StateLock { get; } = new();

    private long? messageStamp;
    private readonly Configuration config;
    private readonly MessageInbox inbox = new();
    private long droppedMessages;
    private long nextWarning;
    private readonly List<TimelineEntry> timeline = new();
    private readonly List<ActiveAlert> alerts = new();

    private readonly StandaloneMeter standalone;

    /// <summary>Keep replacement, source checks and inbox writes atomic with shutdown.</summary>
    private readonly object serverLock = new();

    private readonly List<Task> pendingDrains = new();

    /// <summary>Read by socket threads without taking serverLock.</summary>
    private volatile WebSocketServer? server;

    /// <summary>Reject late events from replaced sessions on the same server.</summary>
    private long currentSession;

    private double clockBase;
    private long clockStamp;
    private bool clockRunning;

    internal BridgeHost(Configuration config)
    {
        this.config = config;
        this.standalone = new StandaloneMeter(config, () => this.IsConnected, this.ApplyLocalDps, this.ClearLocalDps);
    }

    /// <summary>Read current state so old callbacks cannot leave a stale flag.</summary>
    internal bool IsConnected => this.server?.IsConnected ?? false;

    internal string? LastError => this.server?.LastError;

    internal IReadOnlyList<TimelineEntry> Timeline => this.timeline;

    internal IReadOnlyList<ActiveAlert> Alerts => this.alerts;

    private DpsState dps = new();
    internal DpsState UnheldDps { get; private set; } = new();
    private readonly List<DpsState> dpsHistory = new();
    internal IReadOnlyList<DpsState> DpsHistory => this.dpsHistory;

    internal DpsState Dps
    {
        get => this.dps;
        private set
        {
            if (value.Ended && value.HasDamage && value.Rows.Count > 0)
            {
                var previous = this.dpsHistory.Count > 0 ? this.dpsHistory[0] : null;
                if (previous != null && ((!string.IsNullOrEmpty(value.Id) && previous.Id == value.Id)
                    || (string.IsNullOrEmpty(value.Id) && this.dps.Ended)))
                {
                    this.dpsHistory[0] = value;
                }
                else
                {
                    this.dpsHistory.Insert(0, value);
                    if (this.dpsHistory.Count > 20) this.dpsHistory.RemoveAt(20);
                }
            }

            this.dps = value;
            this.UnheldDps = value;
        }
    }

    private DpsState? lastLocal;

    /// <summary>Snapshot for legacy endings. Survives legacy wipe clears, but not explicit zone clears.</summary>
    private DpsState? lastLive;

    internal double Clock => this.clockRunning
        ? this.clockBase + ((Environment.TickCount64 - this.clockStamp) / 1000.0)
        : this.clockBase;

    internal bool ClockRunning => this.clockRunning;

    internal void Start()
    {
        this.Stop();

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
        // ClearState must keep queued frames because zone changes send a timeline after clear.
        this.inbox.Clear();

        if (old == null)
        {
            return;
        }

        // Keep disposal off the render thread. Unload waits, but a port rebind may need a retry.
        var drain = Task.Run(old.Dispose);
        lock (this.serverLock)
        {
            this.pendingDrains.RemoveAll(t => t.IsCompleted);
            this.pendingDrains.Add(drain);
        }
    }

    /// <summary>Reject stale frames atomically with replacement and inbox clearing.</summary>
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

    internal void Restart() => this.Start();

    internal void RestartStandalone() => this.standalone.Restart();

    internal StandaloneState StandaloneStatus => this.standalone.State;

    internal string StandaloneStatusText => this.standalone.Status;

    private void ApplyLocalDps(DpsState state)
    {
        if (this.IsConnected) return;
        this.UnheldDps = state;
        if (this.KeepFinalDps(state)) return;
        this.Dps = state;
        this.lastLocal = state;
    }

    private bool KeepFinalDps(DpsState next)
        => this.config.HoldFinalMeter && this.Dps.Ended && this.Dps.Rows.Count > 0
            && (next.Show || next.Ended) && !next.HasDamage;

    /// <summary>A program frame applied earlier in this update must survive.</summary>
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
        // An old disconnect must not clear state after Stop replaces the server.
        lock (this.serverLock)
        {
            if (!ReferenceEquals(source, this.server))
            {
                return;
            }

            if (connected)
            {
                if (sequence <= this.currentSession)
                {
                    return;
                }

                this.currentSession = sequence;

                // New session frames arrive after this callback.
                this.inbox.Clear();
                this.inbox.TryEnqueue(null);
                return;
            }

            if (sequence != this.currentSession)
            {
                return;
            }

            this.inbox.Clear();
            this.inbox.TryEnqueue(null);
        }
    }

    private string? Greeting(WebSocketServer source)
        => ReferenceEquals(source, this.server)
            ? $"{{\"ev\":\"hello\",\"protocol\":{ProtocolVersion}," +
              $"\"plugin\":{JsonSerializer.Serialize(PluginVersion.Value)},\"dpsRetention\":true}}"
            : null;

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
                var hasKeepDps = root.TryGetProperty("keepDps", out var keep);
                if (hasKeepDps && keep.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) break;
                var keepDps = hasKeepDps && keep.ValueKind == JsonValueKind.True;
                if (hasKeepDps && !keepDps) this.lastLive = null;
                this.ClearState(resetDps: !keepDps);
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
        if (!root.TryGetProperty("v", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        this.timeline.Clear();

        foreach (var entry in entries.EnumerateArray())
        {
            // Timeline entries are [time, label, optional kind].
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

        // Bound wrapping and layout work per frame.
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

        var ended = show.ValueKind == JsonValueKind.False;
        // Older programs send an ending without its own snapshot.
        if (ended && !root.TryGetProperty("enc", out _) && !root.TryGetProperty("rows", out _))
        {
            var last = this.lastLive;
            this.Dps = last == null
                ? new DpsState { Ended = true }
                : new DpsState
                {
                    Ended = true,
                    Id = last.Id,
                    Zone = last.Zone,
                    EncHps = last.EncHps,
                    Participants = last.Participants,
                    Title = last.Title,
                    Duration = last.Duration,
                    EncDps = last.EncDps,
                    HasDamage = last.HasDamage,
                    Rows = last.Rows,
                };

            return;
        }

        if (ended && (!root.TryGetProperty("enc", out var finalEnc) || finalEnc.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rows", out var finalRows) || finalRows.ValueKind != JsonValueKind.Array))
        {
            return;
        }

        var title = string.Empty;
        var duration = string.Empty;
        var encDps = 0.0;
        var encHps = 0.0;
        var id = string.Empty;
        var zone = string.Empty;
        var participants = 0;
        bool? hasDamage = null;
        if (root.TryGetProperty("enc", out var enc) && enc.ValueKind == JsonValueKind.Object)
        {
            title = SanitizeText(ReadString(enc, "t"), MaxTextChars);
            duration = SanitizeText(ReadString(enc, "d"), MaxTextChars);
            id = SanitizeText(ReadString(enc, "id"), MaxTextChars);
            zone = SanitizeText(ReadString(enc, "zone"), MaxTextChars);
            if (enc.TryGetProperty("dps", out var total) && !TryFinite(total, out encDps)) return;
            if (enc.TryGetProperty("hps", out var healing) && !TryFinite(healing, out encHps)) return;
            if (enc.TryGetProperty("participants", out var count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out var parsedCount))
                participants = Math.Clamp(parsedCount, 0, 1024);
            if (enc.TryGetProperty("hasDamage", out var damage))
            {
                if (damage.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
                hasDamage = damage.GetBoolean();
            }
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
                    deaths)
                {
                    Stats = entry.GetArrayLength() > 7 ? ReadCombatStats(entry[7]) : null,
                });

                if (rows.Count >= MaxDpsRows)
                {
                    Services.Log.Debug($"dps rows truncated at {MaxDpsRows}");
                    break;
                }
            }
        }

        var state = new DpsState
        {
            Show = !ended,
            Ended = ended,
            Title = title,
            Duration = duration,
            EncDps = Math.Max(0, encDps),
            Id = id,
            Zone = zone,
            EncHps = Math.Max(0, encHps),
            Participants = Math.Max(participants, rows.Count),
            HasDamage = hasDamage ?? (encDps > 0 || rows.Any(row => row.Dps > 0 || row.Share > 0)),
            Rows = rows,
        };

        this.UnheldDps = state;
        // Combat flags, healing and misses must not replace the previous pull.
        if (this.KeepFinalDps(state)) return;

        this.lastLive = state;
        this.Dps = state;
    }

    private static CombatStats? ReadCombatStats(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        double? Read(string name, bool percent = false)
        {
            if (!element.TryGetProperty(name, out var item) || !TryFinite(item, out var value)) return null;
            return Math.Clamp(value, 0, percent ? 100 : 1e18);
        }

        return new CombatStats
        {
            Damage = Read("damage"), Healed = Read("healed"), HealShare = Read("healShare", true),
            Crit = Read("crit", true), Direct = Read("direct", true), CritDirect = Read("critDirect", true),
            Taken = Read("taken"), HealingTaken = Read("healingTaken"), Heals = Read("heals"),
            Overheal = Read("overheal", true), Hits = Read("hits"),
        };
    }

    /// <summary>Standalone rows must survive server restarts.</summary>
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

    /// <summary>Distinct severity text prevents samples from merging.</summary>
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

    private void Push(ActiveAlert alert)
    {
        if (this.config.AlertsCollapseDupes && this.alerts.Count > 0)
        {
            var last = this.alerts[^1];
            if (last.Text == alert.Text && last.Severity == alert.Severity && last.ExpiresAt > alert.ShownAt)
            {
                last.Count++;
                last.ShownAt = alert.ShownAt;

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

    /// <summary>Prevent row overlap and bound layout work.</summary>
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
    /// <summary>The fourth component distinguishes rolling builds.</summary>
    internal static readonly string Value =
        typeof(PluginVersion).Assembly.GetName().Version?.ToString(4)
        ?? "0.0.0";
}
