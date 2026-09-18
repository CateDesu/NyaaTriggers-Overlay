using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Meter;

internal enum StandaloneState
{
    Off,
    Paused,
    Connecting,
    Connected,
    Error,
}

/// <summary>Runs the meter from IINACT while the program is disconnected. Socket callbacks
/// queue messages for <see cref="Update"/> under the UI state lock. A program connection
/// stops the client and takes priority.</summary>
internal sealed class StandaloneMeter : IDisposable
{
    private const int MaxMessagesPerFrame = 64;

    /// <summary>Maximum wait in milliseconds for background client disposal during
    /// unload.</summary>
    private const int DrainWaitMs = 4000;

    private readonly Configuration config;
    private readonly Func<bool> appConnected;
    private readonly Action<DpsState> applyLocal;
    private readonly Action clearLocal;
    private readonly MessageInbox inbox = new();
    private long droppedMessages;
    private long nextWarning;

    /// <summary>Makes source checks and inbox writes atomic with client
    /// replacement.</summary>
    private readonly object gate = new();

    /// <summary>Unload waits for these client tasks so their callbacks can
    /// finish.</summary>
    private readonly List<Task> pendingDrains = new();

    private double? messageTime;
    private MeterEngine engine = new();
    private IinactClient? client;
    private bool endpointBad;
    private bool feeding;
    private bool wasLive;
    private long nextPush;
    private long retryAt;

    /// <summary>Deduplicate zone notifications because IINACT replays the current zone on
    /// subscribe.</summary>
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

    internal void Update()
    {
        var wanted = this.config.StandaloneMeter && !this.appConnected();
        if (wanted && this.client == null && Environment.TickCount64 >= this.retryAt)
        {
            this.StartClient();
        }
        else if (!wanted && this.client != null)
        {
            this.StopClient();
        }

        if (!wanted)
        {
            // Discard the old feed backlog so it cannot restore rows after the ownership
            // change.
            this.inbox.Clear();

            if (this.feeding)
            {
                this.feeding = false;
                this.wasLive = false;

                // Clear standalone rows even when the program takes over. An idle program
                // may send no replacement DPS frame.
                this.clearLocal();
            }

            return;
        }

        long dropped;
        lock (this.gate)
        {
            dropped = this.droppedMessages;
        }

        if (dropped > 0)
        {
            this.StopClient();
            this.retryAt = Environment.TickCount64 + 5000;
            this.engine.FeedLost(incomplete: true);
            this.Warn($"IINACT inbox lost at least {dropped} messages, encounter incomplete, reconnecting");
        }

        var bytes = MessageInbox.FrameBytes;
        for (var count = 0; count < MaxMessagesPerFrame &&
             this.inbox.TryDequeue(ref bytes, count == 0, out var raw, out var receivedAt); count++)
        {
            try
            {
                this.messageTime = receivedAt / 1000.0;
                if (raw == null)
                {
                    this.engine.FeedLost();
                }
                else
                {
                    this.Handle(raw);
                }
            }
            catch (Exception ex)
            {
                this.Warn($"bad IINACT message: {ex.Message}");
            }
            finally
            {
                this.messageTime = null;
            }
        }

        var live = this.engine.HasLiveEncounter && this.engine.HasLiveDamage;
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

    /// <summary>The next Update connects to the newly configured endpoint.</summary>
    internal void Restart()
    {
        this.StopClient();
        this.clearLocal();
        this.feeding = false;
        this.wasLive = false;
        this.engine = new MeterEngine();
        this.retryAt = 0;
    }

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

    internal string Status
    {
        get
        {
            if (this.endpointBad)
            {
                return "Feed URL must start with ws:// or wss://.";
            }

            return this.client?.Status ?? (Environment.TickCount64 < this.retryAt
                ? "Meter feed overloaded. Retrying shortly." : "Connecting to IINACT.");
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

        // Start with fresh actor and zone state for each connection. Subscription events
        // repopulate it.
        this.engine = new MeterEngine(() => this.messageTime ?? Environment.TickCount64 / 1000.0);
        this.engine.OnEncounterEnd = snap =>
        {
            // Apply each ending in order so a later empty pull cannot erase it.
            this.feeding = true;
            this.wasLive = false;
            this.applyLocal(ToEndState(snap));
        };

        this.wasLive = false;
        this.nextPush = 0;

        // Capture the source to reject callbacks from a replaced client.
        IinactClient? created = null;
        created = new IinactClient(uri, raw => this.Receive(created!, raw),
            () => this.Receive(created!, null));
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
            this.droppedMessages = 0;
        }

        // Clear the old backlog before the next Update creates a new engine. Receive
        // already rejects further old client messages.
        this.inbox.Clear();

        if (old == null)
        {
            return;
        }

        // Stop without blocking this thread. Dispose waits for background cleanup during
        // unload.
        old.Stop();
        var drain = Task.Run(old.Dispose);
        lock (this.gate)
        {
            this.pendingDrains.RemoveAll(t => t.IsCompleted);
            this.pendingDrains.Add(drain);
        }
    }

    /// <summary>Queue messages under gate so source checks are atomic with client
    /// replacement. Stale frames must not reach the next engine.</summary>
    private void Receive(IinactClient source, string? raw)
    {
        lock (this.gate)
        {
            if (!ReferenceEquals(source, this.client))
            {
                return;
            }

            if (this.droppedMessages > 0 || !this.inbox.TryEnqueue(raw))
            {
                this.droppedMessages++;
            }
        }
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

    private void Handle(string raw)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            // Some feeds send raw log lines without JSON.
        }

        using (doc)
        {
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                this.TreatLine(raw.Trim());
                return;
            }

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
                    // A replayed zone must not clear held rows after a reconnect.
                    var zoneId = ReadLong(root, "zoneID");
                    if (zoneId == 0)
                    {
                        zoneId = ReadLong(root, "zoneId");
                    }

                    var zoneName = ReadString(root, "zoneName").Trim();
                    if ((zoneId != 0 && zoneId != this.lastZoneId) ||
                        (zoneId == 0 && zoneName.Length > 0 && zoneName != this.lastZone))
                    {
                        // Initial zone metadata may arrive after identity, which must be
                        // preserved.
                        if (zoneName.Length > 0)
                        {
                            if (!this.engine.HasZone)
                            {
                                this.engine.SetInitialZone(zoneName);
                            }
                            else
                            {
                                this.engine.Process(new[] { "01", "", "", zoneName });
                            }
                        }

                        this.ClearDisplay();
                    }
                    else if (zoneName.Length > 0 && !this.engine.HasZone)
                    {
                        // Initialize the new engine's zone even when this replay was
                        // already deduplicated by the feed.
                        this.engine.SetInitialZone(zoneName);
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
                        var members = new List<KeyValuePair<int, int>>();
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
                                members.Add(new KeyValuePair<int, int>(aid.Value, (int)job));
                            }
                        }

                        this.engine.SetRoster(members);
                    }

                    break;

                case "combatants":
                    this.HandleCombatants(root);
                    break;

                default:
                    // Identify combatant replies by their list because the type casing
                    // varies.
                    if (root.TryGetProperty("combatants", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        this.HandleCombatants(root);
                    }

                    break;
            }
        }
    }

    /// <summary>Fill player jobs from the roster when earlier spawn lines are
    /// unavailable.</summary>
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

            if (id is >= 0x10000000 and <= 0x10FFFFFF && job is > 0 and <= int.MaxValue)
            {
                this.engine.NoteJob((int)id, (int)job);
            }
        }
    }

    /// <summary>Process a zone line before clearing the display so the encounter ending
    /// precedes the clear.</summary>
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
            // Record this valid zone line so the matching ChangeZone event does not clear
            // twice.
            this.lastZone = fields[3].Trim();
            if (long.TryParse(
                    fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var zoneId))
            {
                this.lastZoneId = zoneId;
            }

            this.ClearDisplay();
        }
    }

    private void ClearDisplay()
    {
        this.feeding = true;
        this.wasLive = false;
        this.applyLocal(new DpsState());
    }

    private static DpsState ToState(OverlaySnapshot snap, bool ended = false)
    {
        // Bound actor text before it is measured and drawn on every frame.
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
                row.Deaths,
                row.Rank));
        }

        return new DpsState
        {
            Show = !ended,
            Ended = ended,
            Title = BridgeHost.SanitizeText(snap.Title, MaxTextChars),
            Duration = snap.Duration,
            EncDps = snap.EncDps,
            HasDamage = snap.HasDamage,
            Rows = rows,
        };
    }

    /// <summary>Attach final display rows to the ending when a snapshot exists.</summary>
    private static DpsState ToEndState(OverlaySnapshot? snap)
        => snap == null ? new DpsState { Ended = true } : ToState(snap, ended: true);

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

    /// <summary>Accept rawLine or a split line array, joining the array when
    /// necessary.</summary>
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

    /// <summary>Accept numeric or hexadecimal string actor IDs, with a decimal string
    /// fallback.</summary>
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
