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

/// <summary>Program connections take priority. Socket callbacks queue for Update under the UI state lock.</summary>
internal sealed class StandaloneMeter : IDisposable
{
    private const int MaxMessagesPerFrame = 64;

    private const int DrainWaitMs = 4000;

    private readonly Configuration config;
    private readonly Func<bool> appConnected;
    private readonly Action<DpsState> applyLocal;
    private readonly Action clearLocal;
    private readonly MessageInbox inbox = new();
    private long droppedMessages;
    private long nextWarning;

    /// <summary>Keep source checks and inbox writes atomic with replacement.</summary>
    private readonly object gate = new();

    private readonly List<Task> pendingDrains = new();

    private double? messageTime;
    private MeterEngine engine = new();
    private IinactClient? client;
    private bool endpointBad;
    private bool feeding;
    private bool wasLive;
    private long nextPush;
    private long retryAt;

    /// <summary>IINACT replays the zone on subscribe.</summary>
    private string lastZone = string.Empty;
    private long lastZoneId;
    private string sessionZone = string.Empty;
    private long sessionZoneId;
    private bool hasSessionResult;

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
            this.inbox.Clear();

            if (this.feeding)
            {
                this.feeding = false;
                this.wasLive = false;

                // An idle program may take over without sending a replacement DPS frame.
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
                    this.ResetEngine();
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

        this.ResetEngine();

        IinactClient? created = null;
        created = new IinactClient(uri, raw => this.Receive(created!, raw),
            () => this.Receive(created!, null));
        lock (this.gate)
        {
            this.client = created;
        }

        created.Start();
    }

    private void ResetEngine()
    {
        this.sessionZone = string.Empty;
        this.sessionZoneId = 0;
        this.hasSessionResult = false;
        this.engine = new MeterEngine(() => this.messageTime ?? Environment.TickCount64 / 1000.0);
        this.engine.OnEncounterEnd = snap =>
        {
            // Apply each ending in order so a later empty pull cannot erase it.
            this.feeding = true;
            this.wasLive = false;
            this.hasSessionResult |= snap is { HasDamage: true };
            this.applyLocal(ToEndState(snap));
        };

        this.wasLive = false;
        this.nextPush = 0;
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

        this.inbox.Clear();

        if (old == null)
        {
            return;
        }

        // Unload waits for background cleanup.
        old.Stop();
        var drain = Task.Run(old.Dispose);
        lock (this.gate)
        {
            this.pendingDrains.RemoveAll(t => t.IsCompleted);
            this.pendingDrains.Add(drain);
        }
    }

    /// <summary>Reject stale frames atomically with client replacement.</summary>
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
                    if (TryReadBool(root, "inACTCombat", out var inAct) &&
                        TryReadBool(root, "inGameCombat", out var inGame))
                    {
                        this.engine.SetInCombat(inAct, inGame);
                    }
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
                    var heldZoneChanged = zoneId != 0 && this.lastZoneId != 0
                        ? zoneId != this.lastZoneId
                        : zoneName.Length > 0 && this.lastZone.Length > 0 && zoneName != this.lastZone;
                    var zoneChanged = zoneId != 0 && this.sessionZoneId != 0
                        ? zoneId != this.sessionZoneId
                        : zoneName.Length > 0 && this.sessionZone.Length > 0 && zoneName != this.sessionZone;
                    if (zoneChanged)
                    {
                        this.engine.Process(new[] { "01", "", "", zoneName });
                        this.sessionZoneId = zoneId;
                        this.sessionZone = zoneName;
                    }
                    else if (zoneName.Length > 0 && !this.engine.HasZone)
                    {
                        // The new engine still needs zone metadata after replay deduplication.
                        this.engine.SetInitialZone(zoneName);
                        this.nextPush = 0;
                    }

                    if (zoneChanged || (heldZoneChanged && !this.engine.HasLiveDamage && !this.hasSessionResult))
                    {
                        this.ClearDisplay();
                    }

                    if (zoneChanged || heldZoneChanged)
                    {
                        this.lastZoneId = zoneId;
                        this.lastZone = zoneName;
                    }

                    if (zoneId != 0)
                    {
                        this.lastZoneId = zoneId;
                        this.sessionZoneId = zoneId;
                    }

                    if (zoneName.Length > 0)
                    {
                        this.lastZone = zoneName;
                        this.sessionZone = zoneName;
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
                    // Reply type casing varies, so identify combatants by their list.
                    if (root.TryGetProperty("combatants", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        this.HandleCombatants(root);
                    }

                    break;
            }
        }
    }

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

    /// <summary>Apply the encounter ending before clearing for a zone change.</summary>
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
            // Deduplicate the matching ChangeZone event.
            this.lastZone = fields[3].Trim();
            this.sessionZone = this.lastZone;
            if (long.TryParse(
                    fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var zoneId))
            {
                this.lastZoneId = zoneId;
            }
            else
            {
                this.lastZoneId = 0;
            }

            this.sessionZoneId = this.lastZoneId;
            this.ClearDisplay();
        }
    }

    private void ClearDisplay()
    {
        this.hasSessionResult = false;
        this.feeding = true;
        this.wasLive = false;
        this.applyLocal(new DpsState());
    }

    private static DpsState ToState(OverlaySnapshot snap, bool ended = false)
    {
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
                row.Rank) { Stats = row.Stats });
        }

        return new DpsState
        {
            Id = snap.Id,
            Zone = BridgeHost.SanitizeText(snap.Zone, MaxTextChars),
            EncHps = snap.EncHps,
            Participants = snap.Participants,
            Show = !ended,
            Ended = ended,
            Title = BridgeHost.SanitizeText(snap.Title, MaxTextChars),
            Duration = snap.Duration,
            EncDps = snap.EncDps,
            HasDamage = snap.HasDamage,
            Rows = rows,
        };
    }

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

    private static bool TryReadBool(JsonElement root, string name, out bool result)
    {
        result = false;
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = value.GetBoolean();
        return true;
    }

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
