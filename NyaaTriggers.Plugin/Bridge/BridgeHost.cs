using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace NyaaTriggers.Plugin.Bridge;

internal enum Severity
{
    Info,
    Alert,
    Alarm,
}

internal readonly record struct TimelineEntry(float Time, string Label, string Kind);

internal readonly record struct DpsRow(string Name, string Job, double Dps, double Share, double Hps, bool IsSelf, int Deaths);

/// <summary>The app's latest dps frame. Replaced whole on every update rather
/// than mutated, so the UI never reads a half-updated meter.</summary>
internal sealed class DpsState
{
    internal bool Show { get; init; }

    /// <summary>The encounter ended, as opposed to a clear wiping the state:
    /// the meter's hold-last option keeps showing the final rows on this one.
    /// The last frame of a fight wins, so a clear landing after the show:false
    /// still wipes the state and the hold never engages. That is a wipe whose
    /// ActorControl follows the combat drop, and by design the app's
    /// sample-fight reset, which sends clear after the end frame.</summary>
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
/// <see cref="Update"/> on the draw thread, so the UI never reads a list that
/// is being mutated underneath it.
/// </summary>
internal sealed class BridgeHost : IDisposable
{
    /// <summary>Bumped when the wire format changes incompatibly. The app
    /// checks it in the hello and refuses to drive a plugin it does not
    /// understand, rather than sending commands into the void.</summary>
    internal const int ProtocolVersion = 1;

    /// <summary>Messages applied per frame. Draining an unbounded queue in one
    /// frame lets a chatty peer stall the render thread.</summary>
    private const int MaxMessagesPerFrame = 64;

    /// <summary>Inbox depth before messages are dropped. Reached only if the
    /// draw thread has stopped running or the peer is flooding; either way,
    /// growing without limit is the wrong answer.</summary>
    private const int MaxInboxDepth = 512;

    /// <summary>Alerts on screen at once. Beyond this the oldest goes: a wall
    /// of stale callouts is worse than none.</summary>
    private const int MaxAlerts = 8;

    /// <summary>Timeline entries kept. The app pushes its whole schedule, and
    /// the stock timelines run past 300 entries for twenty-minute fights; the
    /// window walks the list each frame, so the cap only bounds memory.</summary>
    private const int MaxTimelineEntries = 1024;

    /// <summary>DPS rows kept. The app caps at a full alliance of 24; more
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

    private readonly Configuration config;
    private readonly ConcurrentQueue<string> inbox = new();
    private readonly List<TimelineEntry> timeline = new();
    private readonly List<ActiveAlert> alerts = new();

    /// <summary>Guards server swaps, the drain list and the source check in
    /// Receive, so an old server's background teardown cannot race a new one
    /// being published and its frames cannot land after Stop drains.</summary>
    private readonly object serverLock = new();

    /// <summary>Old servers draining in the background; unload waits on them,
    /// since a session task outliving the load context runs freed code.</summary>
    private readonly List<Task> pendingDrains = new();

    /// <summary>Read unsynchronized from socket threads; volatile so a
    /// detached server is seen as superseded at once.</summary>
    private volatile WebSocketServer? server;

    /// <summary>Fight clock as of <see cref="clockStamp"/>, interpolated from
    /// there so bars move smoothly between the app's ticks.</summary>
    private double clockBase;
    private long clockStamp;
    private bool clockRunning;

    internal BridgeHost(Configuration config)
    {
        this.config = config;
    }

    /// <summary>Read straight off the server rather than mirrored into a field,
    /// so a callback from a superseded server cannot leave this stuck on.</summary>
    internal bool IsConnected => this.server?.IsConnected ?? false;

    internal string? LastError => this.server?.LastError;

    internal IReadOnlyList<TimelineEntry> Timeline => this.timeline;

    internal IReadOnlyList<ActiveAlert> Alerts => this.alerts;

    internal DpsState Dps { get; private set; } = new();

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
            raw => this.Receive(created!, raw),
            connected => this.OnConnectionChanged(created!, connected),
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

        this.ClearState();
        // Drain anything the old server queued (including a synthesised clear
        // from its disconnect) so a Restart / port change does not re-apply stale
        // timeline or dps frames onto the freshly-cleared state next Update.
        // This lives here, not in ClearState: ClearState also runs for the
        // "clear" command, and the app sends clear + new timeline back-to-back
        // on a zone change, so draining there would discard the fresh frames.
        while (this.inbox.TryDequeue(out _))
        {
        }

        if (old == null)
        {
            return;
        }

        // Dispose waits up to DisposeDrainMs for the old sessions to unwind,
        // and Stop runs on the render thread (port Apply), where that wait
        // would freeze the game — so the teardown drains in the background.
        // Detaching above already silenced it: its callbacks all check the
        // source against the live server. Unload still waits, in Dispose.
        // Apply is the only Restart caller and only fires on a port change.
        // A rapid A→B→A flip can still outrun the drain and fail the rebind;
        // that surfaces as a visible LastError and the next Apply heals it.
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
    /// cleared state.</summary>
    private void Receive(WebSocketServer source, string raw)
    {
        lock (this.serverLock)
        {
            if (!ReferenceEquals(source, this.server) || this.inbox.Count >= MaxInboxDepth)
            {
                return;
            }

            this.inbox.Enqueue(raw);
        }
    }

    /// <summary>Rebind after a port change.</summary>
    internal void Restart() => this.Start();

    private void OnConnectionChanged(WebSocketServer source, bool connected)
    {
        // A superseded server tearing down must not touch the live one's state.
        if (!ReferenceEquals(source, this.server))
        {
            return;
        }

        if (!connected)
        {
            // The app going away must not leave a frozen timeline on screen
            // pretending the pull is still running. Queued so it lands on the
            // draw thread with everything else. Enqueued directly, past the
            // depth cap: that cap exists to bound a flooding peer, and this
            // one frame comes from us — dropping it would leave the peer's
            // last frames frozen on screen forever.
            this.inbox.Enqueue("{\"c\":\"clear\"}");
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

    /// <summary>Drain the inbox and expire stale alerts. Draw thread only.</summary>
    internal void Update()
    {
        var budget = MaxMessagesPerFrame;
        while (budget-- > 0 && this.inbox.TryDequeue(out var raw))
        {
            try
            {
                this.Apply(raw);
            }
            catch (Exception ex)
            {
                Services.Log.Warning($"bad message from the app: {ex.Message}");
            }
        }

        var now = Environment.TickCount64;
        this.alerts.RemoveAll(a => a.ExpiresAt <= now);
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
                if (root.TryGetProperty("t", out var tick) && tick.ValueKind == JsonValueKind.Number)
                {
                    this.clockBase = tick.GetDouble();
                    this.clockStamp = Environment.TickCount64;
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
                this.ClearState();
                break;

            case "ping":
                this.server?.Send("{\"ev\":\"pong\"}");
                break;

            default:
                // Forward-compatible: a newer app sending a command this build
                // does not know is ignored, not an error.
                break;
        }
    }

    private void ApplyTimeline(JsonElement root)
    {
        this.timeline.Clear();
        if (!root.TryGetProperty("v", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            // [time, label] pairs, optionally [time, label, kind], matching
            // what the app's timeline engine produces. The kind is a free
            // string ("tankbuster", "raidwide", "mechanic"); an old app's
            // 2-field entries and kinds we do not know both draw as plain
            // mechanics.
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
            {
                continue;
            }

            var time = entry[0];
            var label = entry[1];
            if (time.ValueKind != JsonValueKind.Number || label.ValueKind != JsonValueKind.String)
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
                this.timeline.Add(new TimelineEntry((float)time.GetDouble(), text, kind));
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

        if (root.TryGetProperty("ttl", out var ttl) && ttl.ValueKind == JsonValueKind.Number)
        {
            seconds = (float)ttl.GetDouble();
        }

        // Clamped: a zero would flicker and never be read, and an app bug
        // sending a huge value would pin a stale callout on screen all fight.
        seconds = Math.Clamp(seconds, 0.5f, 30.0f);

        var now = Environment.TickCount64;
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

        // Encounter over: hide the meter and drop the rows with it. Marked
        // as an ending rather than a clear, so the hold-last option can tell
        // "fight done" apart from "zone changed" and keep the final rows up.
        if (show.ValueKind == JsonValueKind.False)
        {
            this.Dps = new DpsState { Ended = true };
            return;
        }

        var title = string.Empty;
        var duration = string.Empty;
        var encDps = 0.0;
        if (root.TryGetProperty("enc", out var enc) && enc.ValueKind == JsonValueKind.Object)
        {
            title = SanitizeText(ReadString(enc, "t"), MaxTextChars);
            duration = SanitizeText(ReadString(enc, "d"), MaxTextChars);
            encDps = ReadDouble(enc, "dps");
        }

        var rows = new List<DpsRow>();
        if (root.TryGetProperty("rows", out var rowsElement) &&
            rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in rowsElement.EnumerateArray())
            {
                // [name, job, encdps, share, hps, isSelf, deaths] rows, sorted
                // by encdps desc, matching what the app's meter produces. The
                // trailing fields arrived one version at a time; an old app's
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
                    dps.ValueKind != JsonValueKind.Number ||
                    share.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var hps = 0.0;
                var isSelf = false;
                var deaths = 0;
                if (entry.GetArrayLength() > 4 && entry[4].ValueKind == JsonValueKind.Number)
                {
                    hps = entry[4].GetDouble();
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
                    dps.GetDouble(),
                    share.GetDouble(),
                    hps,
                    isSelf,
                    deaths));

                if (rows.Count >= MaxDpsRows)
                {
                    Services.Log.Debug($"dps rows truncated at {MaxDpsRows}");
                    break;
                }
            }
        }

        this.Dps = new DpsState
        {
            Show = true,
            Title = title,
            Duration = duration,
            EncDps = encDps,
            Rows = rows,
        };
    }

    internal void ClearState()
    {
        this.timeline.Clear();
        this.alerts.Clear();
        this.Dps = new DpsState();
        this.clockBase = 0;
        this.clockRunning = false;
    }

    /// <summary>The Test callout button in the config window: push one sample
    /// alert so the box and its colours can be checked outside a fight.</summary>
    internal void PushTestAlert()
    {
        var now = Environment.TickCount64;
        this.Push(new ActiveAlert
        {
            Text = "Sample callout",
            Severity = Severity.Alarm,
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
            if (last.Text == alert.Text && last.Severity == alert.Severity)
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

    private static double ReadDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0.0;

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Bound and flatten a wire string. Newlines go first: the
    /// windows reserve one row per string, so an embedded one would draw
    /// over the next row. Then the length cap, so a flood of max-length
    /// frames cannot keep the render thread measuring novels.</summary>
    private static string SanitizeText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    public void Dispose()
    {
        this.Stop();

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
