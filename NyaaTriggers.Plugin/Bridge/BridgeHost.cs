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

/// <summary>The program's latest dps frame. Replaced whole on every update rather
/// than mutated, so the UI never reads a half-updated meter.</summary>
internal sealed class DpsState
{
    internal bool Show { get; init; }

    /// <summary>The encounter ended, as opposed to a clear wiping the state:
    /// the meter's hold-last option keeps showing the final rows on this one.
    /// The last frame of a fight wins. A clear landing after the show:false
    /// still wipes the state and the hold never engages, which is the program's
    /// sample-fight reset and its zone change by design. On a wipe the program
    /// re-sends the end frame after its clear, so the hold survives it.
    /// The final rows ride the ended state itself: an end marker consumed in
    /// the same drain batch as its last live frame still keeps them.</summary>
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

    /// <summary>Monotonic milliseconds at which this alert stops drawing.
    /// Settable so a merged repeat can push the expiry out.</summary>
    internal required long ExpiresAt { get; set; }

    /// <summary>Settable so a merged repeat re-runs the rise-in and reads as
    /// fired again rather than sitting unchanged.</summary>
    internal required long ShownAt { get; set; }

    /// <summary>How many times this callout has fired while it stayed on top.
    /// One means shown as-is; above one the window appends a times counter.</summary>
    internal int Count { get; set; } = 1;
}

/// <summary>
/// Owns the link and the state it feeds.
///
/// The socket threads only ever enqueue; everything is applied in
/// <see cref="Update"/> under the UI state lock, so the UI never reads a list that
/// is being mutated underneath it.
/// </summary>
internal sealed class BridgeHost : IDisposable
{
    /// <summary>Bumped when the wire format changes incompatibly. The program
    /// checks it in the hello and refuses to drive a plugin it does not
    /// understand, rather than sending commands into the void.</summary>
    internal const int ProtocolVersion = 1;

    /// <summary>Messages applied per frame. Draining an unbounded queue in one
    /// frame lets a chatty peer stall the render thread.</summary>
    private const int MaxMessagesPerFrame = 64;

    /// <summary>Alerts on screen at once. Beyond this the oldest goes: a wall
    /// of stale callouts is worse than none.</summary>
    private const int MaxAlerts = 8;

    /// <summary>Timeline entries kept. The program pushes its whole schedule, and
    /// the stock timelines run past 300 entries for twenty-minute fights; the
    /// window walks the list each frame, so the cap only bounds memory.</summary>
    private const int MaxTimelineEntries = 1024;

    /// <summary>DPS rows kept. The program caps at a full alliance of 24; more
    /// would only ever be a bug, and the window walks the list each frame.
    /// The user's Max combatants setting narrows this down for display.</summary>
    private const int MaxDpsRows = 24;

    /// <summary>Longest name, label or title kept from a frame. The wire cap
    /// is 1 MiB, but every stored string is measured and drawn every frame,
    /// and nothing legit is past a couple of lines.</summary>
    private const int MaxTextChars = 256;

    /// <summary>How long unload waits for background server drains. Bounded:
    /// a wedged socket must not hang plugin teardown either.</summary>
    private const int DrainWaitMs = 4000;

    internal object StateLock { get; } = new();

    private long? messageStamp;
    private readonly Configuration config;
    private readonly MessageInbox inbox = new();
    private long droppedMessages;
    private long nextWarning;
    private readonly List<TimelineEntry> timeline = new();
    private readonly List<ActiveAlert> alerts = new();

    /// <summary>The IINACT-fed meter that runs while no program session is live.
    /// Ticked from Update under the same lock as drawing and settings.</summary>
    private readonly StandaloneMeter standalone;

    /// <summary>Guards server swaps, the drain list and the source check in
    /// Receive, so an old server's background teardown cannot race a new one
    /// being published and its frames cannot land after Stop drains.</summary>
    private readonly object serverLock = new();

    /// <summary>Old servers draining in the background; unload waits on them,
    /// so background callbacks finish before plugin teardown.</summary>
    private readonly List<Task> pendingDrains = new();

    /// <summary>Read unsynchronized from socket threads; volatile so a
    /// detached server is seen as superseded at once.</summary>
    private volatile WebSocketServer? server;

    /// <summary>Sequence of the session that currently owns the link, under
    /// serverLock. Frames and disconnect callbacks from any other session are
    /// stale: a replaced session can outlive its replacement long enough to
    /// land both, and the server object cannot tell them apart.</summary>
    private long currentSession;

    /// <summary>Fight clock as of <see cref="clockStamp"/>, interpolated from
    /// there so bars move smoothly between the program's ticks.</summary>
    private double clockBase;
    private long clockStamp;
    private bool clockRunning;

    internal BridgeHost(Configuration config)
    {
        this.config = config;
        this.standalone = new StandaloneMeter(config, () => this.IsConnected, this.ApplyLocalDps, this.ClearLocalDps);
    }

    /// <summary>Read straight off the server rather than mirrored into a field,
    /// so a callback from a superseded server cannot leave this stuck on.</summary>
    internal bool IsConnected => this.server?.IsConnected ?? false;

    internal string? LastError => this.server?.LastError;

    internal IReadOnlyList<TimelineEntry> Timeline => this.timeline;

    internal IReadOnlyList<ActiveAlert> Alerts => this.alerts;

    internal DpsState Dps { get; private set; } = new();

    private DpsState? lastLocal;

    /// <summary>The newest accepted live frame, kept in the state
    /// layer rather than only in the window: an end marker landing in the
    /// same drain batch as its final live frame would otherwise leave
    /// hold-last showing older numbers. A clear deliberately keeps it, the
    /// wipe sequence is clear then show:false and the hold must survive it.</summary>
    private DpsState? lastLive;

    internal double Clock => this.clockRunning
        ? this.clockBase + ((Environment.TickCount64 - this.clockStamp) / 1000.0)
        : this.clockBase;

    /// <summary>Whether a tick has ever landed, so the timeline box's clock
    /// line can stay hidden until a fight clock actually exists.</summary>
    internal bool ClockRunning => this.clockRunning;

    internal void Start()
    {
        this.Stop();

        // The callback needs to know which server it came from, so a late
        // callback from one we already disposed can be ignored.
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
        // Drain anything the old server queued (including a synthesised clear
        // from its disconnect) so a Restart / port change does not re-apply stale
        // timeline or dps frames onto the freshly-cleared state next Update.
        // This lives here, not in ClearState: ClearState also runs for the
        // "clear" command, and the program sends clear + new timeline back-to-back
        // on a zone change, so draining there would discard the fresh frames.
        this.inbox.Clear();

        if (old == null)
        {
            return;
        }

        // Dispose waits up to DisposeDrainMs for the old sessions to unwind,
        // and Stop runs on the render thread (port Apply), where that wait
        // would freeze the game — so the teardown drains in the background.
        // Detaching above already silenced it: its callbacks all check the
        // source against the live server. Unload still waits, in Dispose.
        // A rapid port change can outrun the drain and fail the rebind.
        // The visible error keeps Apply enabled for another attempt.
        var drain = Task.Run(old.Dispose);
        lock (this.serverLock)
        {
            this.pendingDrains.RemoveAll(t => t.IsCompleted);
            this.pendingDrains.Add(drain);
        }
    }

    /// <summary>Socket thread: queue only, never touch the state the UI reads.
    /// Guarded on the source under serverLock so the check and the enqueue are
    /// atomic with Stop's detach and drain: a frame from a superseded server
    /// lands before the drain or not at all, never after it onto freshly
    /// cleared state. The sequence guard does the same within one server: a
    /// session being replaced can still be mid-read-loop, and its late frames
    /// must not land behind the replacement's session-start reset.</summary>
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

    /// <summary>Re-dial IINACT after the standalone endpoint was applied.</summary>
    internal void RestartStandalone() => this.standalone.Restart();

    /// <summary>Standalone meter state for the config window's link section.</summary>
    internal StandaloneState StandaloneStatus => this.standalone.State;

    internal string StandaloneStatusText => this.standalone.Status;

    /// <summary>The standalone meter's write path. The program owns the meter
    /// while it is connected, so a local frame landing during a session is
    /// dropped rather than fighting the program's feed.</summary>
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
        // A superseded server tearing down must not touch the live one's state.
        // The check and the enqueue stay under serverLock so they are atomic
        // with Stop's swap and drain, same as Receive: without it a callback
        // preempted between the two could land a stale clear on the fresh
        // server's inbox.
        lock (this.serverLock)
        {
            if (!ReferenceEquals(source, this.server))
            {
                return;
            }

            if (connected)
            {
                // A superseded session can still be queued behind its
                // replacement: only the newest session earns the reset.
                if (sequence <= this.currentSession)
                {
                    return;
                }

                this.currentSession = sequence;

                // The new session starts with a clear. Its frames arrive only
                // after this callback, so old work can be discarded in order.
                this.inbox.Clear();
                this.inbox.TryEnqueue(null);
                return;
            }

            // A replaced session's late disconnect must not clear its
            // replacement's freshly pushed schedule.
            if (sequence != this.currentSession)
            {
                return;
            }

            // Retire the old session backlog before clearing its display.
            this.inbox.Clear();
            this.inbox.TryEnqueue(null);
        }
    }

    /// <summary>The session's first frame. Returned rather than sent so the
    /// server can queue it before publishing the session, which is what makes
    /// "hello arrives first" true rather than merely likely.</summary>
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

        // The standalone meter ticks after the program's frames, and its writer
        // refuses to touch Dps while a session is live, so the program's feed
        // always wins a same-frame race.
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
                // A tick without a real time is dropped, not applied as zero:
                // a malformed frame must not rewind the fight clock.
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
                // Arrives over a live program session, so the program owns
                // the dps rows and this reset covers them too.
                this.ClearState(resetDps: true);
                break;

            case "ping":
                this.server?.Send("{\"ev\":\"pong\"}");
                break;

            default:
                // Forward-compatible: a newer program sending a command this build
                // does not know is ignored, not an error.
                break;
        }
    }

    private void ApplyTimeline(JsonElement root)
    {
        // Validate before touching the live schedule: a malformed frame is
        // dropped whole, the same discipline the tick and dps handlers follow.
        if (!root.TryGetProperty("v", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        this.timeline.Clear();

        foreach (var entry in entries.EnumerateArray())
        {
            // [time, label] pairs, optionally [time, label, kind], matching
            // what the program's timeline engine produces. The kind is a free
            // string ("tankbuster", "raidwide", "mechanic"); an old program's
            // 2-field entries and kinds we do not know both draw as plain
            // mechanics.
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

        // Clamp length and flatten: the wire cap is 1 MiB, but no callout
        // needs that. AlertsWindow.WrapLines would otherwise Split(' ') the
        // whole string every frame for the alert's lifetime, a GC-pressure
        // foot-gun under a flood of max-length frames.
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

        // Each severity falls back to its own configured time. An explicit ttl
        // on the wire still wins over all three.
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

        // Clamped: a zero would flicker and never be read, and a program bug
        // sending a huge value would pin a stale callout on screen all fight.
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
        // The contract always carries "show"; a frame without it is malformed
        // and ignored like any other bad frame.
        if (!root.TryGetProperty("show", out var show) ||
            show.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        // Encounter over: hide the meter, with the final rows riding the
        // marker so hold-last keeps the newest numbers even when no draw
        // observed them live. Marked as an ending rather than a clear, so
        // the hold-last option can tell "fight done" apart from "zone
        // changed" and keep the final rows up.
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
                // [name, job, encdps, share, hps, isSelf, deaths] rows, sorted
                // by encdps desc, matching what the program's meter produces. The
                // trailing fields arrived one version at a time; an old program's
                // shorter rows just get the defaults.
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

    /// <summary>Wipe the state the program pushed. The dps rows only when
    /// resetDps: they can belong to the standalone meter, which is fed
    /// independent of this server and must survive its restarts.</summary>
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

    /// <summary>The test buttons in the config window: push one sample alert
    /// so the box, its colours and the per severity knobs can be checked
    /// outside a fight. The severity names the text, so testing several at
    /// once does not fold them into one merged repeat.</summary>
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

    /// <summary>Add an alert and hold the stack to its cap, oldest out first: a
    /// burst inside one alert's lifetime must not grow the display without
    /// limit. Every alert goes through here so no path can skip the trim.
    /// With merge repeats on, a repeat of the callout already on top bumps its
    /// counter and expiry instead of stacking another row.</summary>
    private void Push(ActiveAlert alert)
    {
        if (this.config.AlertsCollapseDupes && this.alerts.Count > 0)
        {
            var last = this.alerts[^1];
            if (last.Text == alert.Text && last.Severity == alert.Severity && last.ExpiresAt > alert.ShownAt)
            {
                last.Count++;
                last.ShownAt = alert.ShownAt;

                // Max, not a straight take: the wire allows a per-alert ttl,
                // so a repeat carrying a shorter one must not clip the life
                // the showing callout has left.
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

    /// <summary>Bound and flatten a wire string. Newlines go first: the
    /// windows reserve one row per string, so an embedded one would draw
    /// over the next row. Then the length cap, so a flood of max-length
    /// frames cannot keep the render thread measuring novels. Internal so
    /// the standalone meter can hold its feed to the same hygiene.</summary>
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

        // Never split a surrogate pair at the cap: a lone half draws as a
        // replacement glyph, so the cut backs off the leading half.
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

        // The background drains Stop started must finish before the load
        // context goes away: a session task outliving it runs freed code.
        // Bounded like the server's own drain, plus slack for the drain
        // task to be scheduled at all.
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
    /// <summary>All four components: rolling builds differ only in the last
    /// one, and the program shows this string in its status label.</summary>
    internal static readonly string Value =
        typeof(PluginVersion).Assembly.GetName().Version?.ToString(4)
        ?? "0.0.0";
}
