using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Meter;

/// <summary>How the standalone meter is doing, for the config window.</summary>
internal enum StandaloneState
{
    Off,
    Paused,
    Connecting,
    Connected,
    Error,
}

/// <summary>Runs the dps meter off IINACT directly while the program is away.
///
/// Owns the engine and the feed client. The socket thread only ever enqueues;
/// everything is applied in <see cref="Update"/> on the draw thread, the same
/// discipline the bridge uses, so the UI never reads a half-updated meter.
/// The program's feed always wins: while an app session is live the client
/// stays off and this writes nothing.
/// </summary>
internal sealed class StandaloneMeter : IDisposable
{
    /// <summary>Messages applied per frame, same bound as the bridge inbox.</summary>
    private const int MaxMessagesPerFrame = 64;

    /// <summary>Inbox depth before frames drop. A stalled draw thread must not
    /// grow it without limit, and a flood of log lines is exactly what a pull
    /// opening looks like.</summary>
    private const int MaxInboxDepth = 512;

    /// <summary>How long unload waits for background client drains, matching
    /// the bridge's own drain bound.</summary>
    private const int DrainWaitMs = 4000;

    /// <summary>What the drain decided the meter state should become. One
    /// slot, last write wins: a zone change overwrites the end its own
    /// finalize just queued, so only the clear is applied. That matches the
    /// net effect of the program's show:false then clear pair on a zone, and
    /// hold-last never survives zoning on either feed.</summary>
    private enum Pending
    {
        None,
        Ended,
        Cleared,
    }

    private readonly Configuration config;
    private readonly Func<bool> appConnected;
    private readonly Action<DpsState> applyLocal;
    private readonly Action clearLocal;
    private readonly ConcurrentQueue<string> inbox = new();

    /// <summary>Guards the client handle and the drain list, so a feed frame
    /// or a background drain cannot race the live client being swapped.</summary>
    private readonly object gate = new();

    /// <summary>Old clients draining in the background; unload waits on them,
    /// since a receive loop outliving the load context runs freed code.</summary>
    private readonly List<Task> pendingDrains = new();

    private MeterEngine engine = new();
    private IinactClient? client;
    private bool endpointBad;
    private bool feeding;
    private bool wasLive;
    private long nextPush;
    private Pending pending;

    /// <summary>Last zone seen, from the 01 line or the ChangeZone event.
    /// IINACT replays the current zone on every subscribe, so the clear keys
    /// on the zone actually moving, not on the event arriving.</summary>
    private string lastZone = string.Empty;
    private long lastZoneId;

    internal StandaloneMeter(
        Configuration config, Func<bool> appConnected, Action<DpsState> applyLocal, Action clearLocal)
    {
        this.config = config;
        this.appConnected = appConnected;
        this.applyLocal = applyLocal;
        this.clearLocal = clearLocal;
    }

    /// <summary>Draw thread heartbeat: run or stop the client per the toggle
    /// and the app session, apply what the feed queued, push the meter.</summary>
    internal void Update()
    {
        var wanted = this.config.StandaloneMeter && !this.appConnected();
        if (wanted && this.client == null)
        {
            this.StartClient();
        }
        else if (!wanted && this.client != null)
        {
            this.StopClient();
        }

        if (!wanted)
        {
            // Toggled off, or the app took over. Frames the stopped feed left
            // queued belong to a source that no longer owns the meter, so
            // they are discarded, not applied: applying one could resurrect
            // the rows the transition clear just dropped.
            while (this.inbox.TryDequeue(out _))
            {
            }

            this.pending = Pending.None;
            if (this.feeding)
            {
                this.feeding = false;
                this.wasLive = false;

                // The teardown clear goes around the app-wins guard on
                // purpose: an idle app sends no dps frames, so deferring to
                // it would freeze the standalone's last rows on screen.
                this.clearLocal();
            }

            return;
        }

        var budget = MaxMessagesPerFrame;
        while (budget-- > 0 && this.inbox.TryDequeue(out var raw))
        {
            try
            {
                this.Handle(raw);
            }
            catch (Exception ex)
            {
                Services.Log.Warning($"bad IINACT message: {ex.Message}");
            }
        }

        if (this.pending == Pending.Cleared)
        {
            this.feeding = true;
            this.applyLocal(new DpsState());
        }
        else if (this.pending == Pending.Ended)
        {
            // Encounter over: an ending, not a clear, so hold-last can keep
            // the final rows up, same frame the program sends.
            this.feeding = true;
            this.applyLocal(new DpsState { Ended = true });
        }

        this.pending = Pending.None;

        var live = this.engine.HasLiveEncounter;
        var now = Environment.TickCount64;
        if (live && (!this.wasLive || now >= this.nextPush))
        {
            var snap = this.engine.LiveSnapshot();
            if (snap != null)
            {
                this.feeding = true;
                this.nextPush = now + 1000;
                this.applyLocal(ToState(snap));
            }
        }

        this.wasLive = live;
    }

    /// <summary>Re-dial after the endpoint field was applied. The next Update
    /// starts a fresh client on the new address.</summary>
    internal void Restart() => this.StopClient();

    internal StandaloneState State
    {
        get
        {
            if (!this.config.StandaloneMeter)
            {
                return StandaloneState.Off;
            }

            if (this.appConnected())
            {
                return StandaloneState.Paused;
            }

            if (this.endpointBad)
            {
                return StandaloneState.Error;
            }

            var client = this.client;
            if (client == null)
            {
                return StandaloneState.Connecting;
            }

            return client.IsConnected ? StandaloneState.Connected : StandaloneState.Connecting;
        }
    }

    /// <summary>Detail for the state, the client's own status line. Only
    /// meaningful past Off.</summary>
    internal string Status
    {
        get
        {
            if (this.endpointBad)
            {
                return "Feed URL must start with ws:// or wss://.";
            }

            return this.client?.Status ?? "Connecting to IINACT.";
        }
    }

    private void StartClient()
    {
        var uri = ParseEndpoint(this.config.IinactEndpoint);
        if (uri == null)
        {
            this.endpointBad = true;
            return;
        }

        this.endpointBad = false;

        // A fresh engine per session: identity, jobs and zone are all
        // relearned from the burst IINACT sends on subscribe, and nothing
        // stale can leak in from the last run.
        this.engine = new MeterEngine();
        this.engine.OnEncounterEnd = () => this.pending = Pending.Ended;
        this.wasLive = false;
        this.nextPush = 0;

        // The callback needs to know which client it came from, so a late
        // frame from one already stopped can be ignored.
        IinactClient? created = null;
        created = new IinactClient(uri, raw => this.Receive(created!, raw));
        lock (this.gate)
        {
            this.client = created;
        }

        created.Start();
    }

    private void StopClient()
    {
        IinactClient? old;
        lock (this.gate)
        {
            old = this.client;
            this.client = null;
        }

        // Drain what the detached client queued before the swap. The Receive
        // guard keeps new frames out from here on, but a backlog already in
        // the inbox would be applied onto the fresh engine the next Update
        // builds. Same drain the bridge's Stop does after a server swap.
        while (this.inbox.TryDequeue(out _))
        {
        }

        if (old == null)
        {
            return;
        }

        // Dispose waits on the receive loop, so it drains in the background
        // like the bridge's server swaps. Unload still waits, in Dispose.
        old.Stop();
        var drain = Task.Run(old.Dispose);
        lock (this.gate)
        {
            this.pendingDrains.RemoveAll(t => t.IsCompleted);
            this.pendingDrains.Add(drain);
        }
    }

    /// <summary>Socket thread: queue only, never touch the state the UI reads.
    /// Guarded on the source under gate so the check and the enqueue are atomic
    /// with StopClient's detach: a frame from a superseded client lands before
    /// the swap or not at all, never after it onto the fresh engine.</summary>
    private void Receive(IinactClient source, string raw)
    {
        lock (this.gate)
        {
            if (!ReferenceEquals(source, this.client) || this.inbox.Count >= MaxInboxDepth)
            {
                return;
            }

            this.inbox.Enqueue(raw);
        }
    }

    private void Handle(string raw)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            // Not JSON: some feeds ship bare log lines. Fall through.
        }

        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            this.TreatLine(raw.Trim());
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = ReadString(root, "type");
            switch (type.ToLowerInvariant())
            {
                case "logline":
                    this.TreatLine(ExtractLogLine(root));
                    break;

                case "broadcast":
                    // Broadcast wrapper some IINACT versions use.
                    if (ReadString(root, "msgtype").Equals("logline", StringComparison.OrdinalIgnoreCase))
                    {
                        this.TreatLine(ReadString(root, "msg").Trim());
                    }

                    break;

                case "incombat":
                    this.engine.SetInCombat(ReadBool(root, "inACTCombat"), ReadBool(root, "inGameCombat"));
                    break;

                case "changeprimaryplayer":
                    var id = ReadLong(root, "charID");
                    if (id == 0)
                    {
                        id = ReadLong(root, "charId");
                    }

                    if (id is > 0 and <= int.MaxValue)
                    {
                        this.engine.SetMe((int)id);
                    }

                    break;

                case "changezone":
                    // IINACT replays the current zone on every subscribe.
                    // Only a real change may clear, or a feed reconnect would
                    // wipe a held meter with no zone change at all. The
                    // program dedups the same pair for the same reason.
                    var zoneId = ReadLong(root, "zoneID");
                    if (zoneId == 0)
                    {
                        zoneId = ReadLong(root, "zoneId");
                    }

                    var zoneName = ReadString(root, "zoneName").Trim();
                    if ((zoneId != 0 && zoneId != this.lastZoneId) ||
                        (zoneId == 0 && zoneName.Length > 0 && zoneName != this.lastZone))
                    {
                        this.pending = Pending.Cleared;
                    }

                    if (zoneId != 0)
                    {
                        this.lastZoneId = zoneId;
                    }

                    if (zoneName.Length > 0)
                    {
                        this.lastZone = zoneName;
                    }

                    break;

                case "partychanged":
                    if (root.TryGetProperty("party", out var party) && party.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var member in party.EnumerateArray())
                        {
                            if (member.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var aid = ReadActorId(member, "id");
                            var job = ReadLong(member, "job");
                            if (aid != null && job is > 0 and <= int.MaxValue)
                            {
                                this.engine.NoteJob(aid.Value, (int)job);
                            }
                        }
                    }

                    break;

                case "combatants":
                    this.HandleCombatants(root);
                    break;

                default:
                    // The getCombatants reply's type casing varies between
                    // builds; the list property is the reliable tell.
                    if (root.TryGetProperty("combatants", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        this.HandleCombatants(root);
                    }

                    break;
            }
        }
    }

    /// <summary>A getCombatants snapshot doubles as a job feed: on a
    /// mid-instance start the 03 burst is long gone, and this still resolves
    /// the party's jobs from live memory. Players only.</summary>
    private void HandleCombatants(JsonElement root)
    {
        if (!root.TryGetProperty("combatants", out var combs) || combs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in combs.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadLong(entry, "ID");
            if (id == 0)
            {
                id = ReadLong(entry, "id");
            }

            var job = ReadLong(entry, "Job");
            if (job == 0)
            {
                job = ReadLong(entry, "job");
            }

            if (id >= 0x10000000 && id <= int.MaxValue && job is > 0 and <= int.MaxValue)
            {
                this.engine.NoteJob((int)id, (int)job);
            }
        }
    }

    /// <summary>Feed one raw log line. A zone line ends the encounter inside
    /// the engine first, then queues the clear, so the two land in the same
    /// order the program sends them.</summary>
    private void TreatLine(string raw)
    {
        if (raw.Length == 0)
        {
            return;
        }

        var fields = raw.Split('|');
        this.engine.Process(fields);
        if (fields.Length > 3 && fields[0] == "01")
        {
            // Same length gate the engine and the program's zone handler use:
            // a short malformed 01 finalizes nothing and clears nothing. Keep
            // the zone dedup warm so the matching ChangeZone event does not
            // clear a second time.
            this.lastZone = fields[3].Trim();
            if (long.TryParse(
                    fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var zoneId))
            {
                this.lastZoneId = zoneId;
            }

            this.pending = Pending.Cleared;
        }
    }

    private static DpsState ToState(OverlaySnapshot snap)
    {
        // Feed strings get the same hygiene the bridge gives wire frames: the
        // endpoint is user-configurable, and a multi-hundred-KB actor name
        // would be measured and drawn every frame.
        const int MaxTextChars = 256;
        var rows = new List<DpsRow>(snap.Rows.Count);
        foreach (var row in snap.Rows)
        {
            rows.Add(new DpsRow(
                BridgeHost.SanitizeText(row.Name, MaxTextChars),
                BridgeHost.SanitizeText(row.Job, MaxTextChars),
                row.EncDps,
                row.Share,
                row.Hps,
                row.IsSelf,
                row.Deaths));
        }

        return new DpsState
        {
            Show = true,
            Title = BridgeHost.SanitizeText(snap.Title, MaxTextChars),
            Duration = snap.Duration,
            EncDps = snap.EncDps,
            Rows = rows,
        };
    }

    private static Uri? ParseEndpoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeWs || uri.Scheme == Uri.UriSchemeWss)
            ? uri
            : null;
    }

    /// <summary>The standard layout is rawLine plus a split line array; fall
    /// back to whichever exists, joining the array back into a raw line.</summary>
    private static string ExtractLogLine(JsonElement root)
    {
        var raw = ReadString(root, "rawLine");
        if (raw.Length == 0)
        {
            raw = ReadString(root, "raw_line");
        }

        if (raw.Length > 0)
        {
            return raw;
        }

        if (!root.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var joined = new StringBuilder();
        foreach (var field in line.EnumerateArray())
        {
            if (joined.Length > 0)
            {
                joined.Append('|');
            }

            joined.Append(field.ValueKind == JsonValueKind.String ? field.GetString() : field.GetRawText());
        }

        return joined.ToString();
    }

    /// <summary>An actor id off the wire, a hex string or a number with a
    /// decimal fallback, mirroring the engine's own id parsing.</summary>
    private static int? ReadActorId(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        long id;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out id))
            {
                return null;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                return null;
            }

            try
            {
                id = Convert.ToInt64(text, 16);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                if (!long.TryParse(text, out id))
                {
                    return null;
                }
            }
        }
        else
        {
            return null;
        }

        return id is > 0 and <= int.MaxValue ? (int)id : null;
    }

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private static long ReadLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose()
    {
        this.StopClient();

        Task[] drains;
        lock (this.gate)
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
                Services.Log.Warning("an IINACT feed session did not stop in time");
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"IINACT feed drain: {ex.Message}");
        }
    }
}
