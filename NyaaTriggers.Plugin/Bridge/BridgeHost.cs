using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace NyaaTriggers.Plugin.Bridge;

internal enum Severity
{
    Info,
    Alert,
    Alarm,
}

internal readonly record struct TimelineEntry(float Time, string Label);

internal readonly record struct DpsRow(string Name, string Job, double Dps, double Share);

/// <summary>The app's latest dps frame. Replaced whole on every update rather
/// than mutated, so the UI never reads a half-updated meter.</summary>
internal sealed class DpsState
{
    internal bool Show { get; init; }

    internal string Title { get; init; } = string.Empty;

    internal string Duration { get; init; } = string.Empty;

    internal double EncDps { get; init; }

    internal IReadOnlyList<DpsRow> Rows { get; init; } = Array.Empty<DpsRow>();
}

internal sealed class ActiveAlert
{
    internal required string Text { get; init; }

    internal required Severity Severity { get; init; }

    /// <summary>Monotonic milliseconds at which this alert stops drawing.</summary>
    internal required long ExpiresAt { get; init; }

    internal required long ShownAt { get; init; }
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

    /// <summary>Timeline entries kept. The window only ever draws a handful,
    /// and the whole list is walked each frame.</summary>
    private const int MaxTimelineEntries = 256;

    /// <summary>DPS rows kept. The app caps at a full party of eight; more
    /// would only ever be a bug, and the window walks the list each frame.</summary>
    private const int MaxDpsRows = 8;

    private readonly Configuration config;
    private readonly ConcurrentQueue<string> inbox = new();
    private readonly List<TimelineEntry> timeline = new();
    private readonly List<ActiveAlert> alerts = new();

    private WebSocketServer? server;

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
        this.server?.Dispose();
        this.server = null;
        this.ClearState();
    }

    /// <summary>Socket thread: queue only, never touch the state the UI reads.
    /// Guarded on the source so a leaked or superseded server cannot keep
    /// injecting into the live session's inbox.</summary>
    private void Receive(WebSocketServer source, string raw)
    {
        if (!ReferenceEquals(source, this.server) || this.inbox.Count >= MaxInboxDepth)
        {
            return;
        }

        this.inbox.Enqueue(raw);
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
            // draw thread with everything else.
            this.Receive(source, "{\"c\":\"clear\"}");
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
                this.clockBase = ReadDouble(root, "t");
                this.clockStamp = Environment.TickCount64;
                this.clockRunning = true;
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
            // [time, label] pairs, matching what the app's timeline engine
            // already produces.
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

            var text = label.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                this.timeline.Add(new TimelineEntry((float)time.GetDouble(), text));
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

        var text = textElement.GetString();
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

        var seconds = this.config.AlertSeconds;
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

        // Encounter over: hide the meter and drop the rows with it.
        if (show.ValueKind == JsonValueKind.False)
        {
            this.Dps = new DpsState();
            return;
        }

        var title = string.Empty;
        var duration = string.Empty;
        var encDps = 0.0;
        if (root.TryGetProperty("enc", out var enc) && enc.ValueKind == JsonValueKind.Object)
        {
            title = ReadString(enc, "t");
            duration = ReadString(enc, "d");
            encDps = ReadDouble(enc, "dps");
        }

        var rows = new List<DpsRow>();
        if (root.TryGetProperty("rows", out var rowsElement) &&
            rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in rowsElement.EnumerateArray())
            {
                // [name, job, encdps, share] rows, sorted by encdps desc,
                // matching what the app's meter already produces.
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

                rows.Add(new DpsRow(
                    name.GetString() ?? string.Empty,
                    job.GetString() ?? string.Empty,
                    dps.GetDouble(),
                    share.GetDouble()));

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

    /// <summary>Sample content for the unlocked state, so an overlay being
    /// placed is never an invisible empty box.</summary>
    internal void ShowPlaceholder()
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
    /// limit. Every alert goes through here so no path can skip the trim.</summary>
    private void Push(ActiveAlert alert)
    {
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

    public void Dispose() => this.Stop();
}

internal static class PluginVersion
{
    internal static readonly string Value =
        typeof(PluginVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
