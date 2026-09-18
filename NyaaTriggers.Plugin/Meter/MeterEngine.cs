using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NyaaTriggers.Plugin.Meter;

// Combat log meter ported from NyaaTriggers dps_meter.py. Effect decoding follows cactbot's
// LogGuide. Encounter totals follow ACT, with pet contributions assigned to their owners. A
// separate display view resets after damage resumes beyond the idle timeout. The engine has
// no game or UI dependencies so it can be tested without either.

internal readonly record struct MeterRow(string Name, string Job, double EncDps, double Share, double Hps, bool IsSelf, int Deaths, int Rank = 0);

/// <summary>A new snapshot is created for each read so consumers never see partial
/// updates.</summary>
internal sealed class OverlaySnapshot
{
    internal required string Title { get; init; }

    internal required string Duration { get; init; }

    internal required double EncDps { get; init; }

    internal bool HasDamage { get; init; }

    internal required IReadOnlyList<MeterRow> Rows { get; init; }
}

/// <summary>Parses combat log lines into meter snapshots, skipping malformed
/// lines.</summary>
internal sealed class MeterEngine
{
    /// <summary>ActorControl, line 33, command for a wipe or reset.</summary>
    private const string WipeCommand = "4000000F";

    /// <summary>Seconds without damage before the display pauses. The next hit starts a new
    /// display segment without splitting encounter totals.</summary>
    private const double DefaultIdleTimeout = 120.0;

    /// <summary>Top damage rows. Include the local player as an additional row if ranked
    /// lower.</summary>
    private const int MaxOverlayRows = 24;

    // Keep local and party records under pressure. Retired damage remains
    // in the encounter total and the title marks reduced actor history.
    private const int MaxEncounterActors = 1024;

    private const int HealType = 0x04;
    private const int MaxNameChars = 256;

    private static string BoundName(string name)
        => name.Length <= MaxNameChars ? name : name[..MaxNameChars];

    // Map ClassJob IDs to acronyms. NPCs, crafting, gathering and unknown jobs use an empty
    // label.
    private static readonly IReadOnlyDictionary<int, string> JobAcronyms = new Dictionary<int, string>
    {
        { 1, "GLA" }, { 2, "PGL" }, { 3, "MRD" }, { 4, "LNC" }, { 5, "ARC" }, { 6, "CNJ" }, { 7, "THM" },
        { 19, "PLD" }, { 20, "MNK" }, { 21, "WAR" }, { 22, "DRG" }, { 23, "BRD" }, { 24, "WHM" },
        { 25, "BLM" }, { 26, "ACN" }, { 27, "SMN" }, { 28, "SCH" }, { 29, "ROG" }, { 30, "NIN" },
        { 31, "MCH" }, { 32, "DRK" }, { 33, "AST" }, { 34, "SAM" }, { 35, "RDM" }, { 36, "BLU" },
        { 37, "GNB" }, { 38, "DNC" }, { 39, "RPR" }, { 40, "SGE" }, { 41, "VPR" }, { 42, "PCT" },
    };

    private readonly Func<double> clock;
    private readonly BoundedMap<int> jobs = new();      // Actor ID to ClassJob ID.
    private readonly Dictionary<int, int> rosterJobs = new();
    private readonly BoundedMap<int> owners = new();    // Pet or summon ID to owner ID.
    private readonly BoundedMap<string> names = new();

    private string zone = string.Empty;
    private int? meId;
    private bool inAct;
    private bool inGame;
    private Encounter? current;
    private Encounter? view;   // Display segment that resets when damage resumes after idle.
    private double idleTimeout = DefaultIdleTimeout;

    internal MeterEngine(Func<double>? clock = null)
    {
        this.clock = clock ?? DefaultClock;
    }

    private static double DefaultClock() => Environment.TickCount64 / 1000.0;

    /// <summary>Called synchronously on encounter end with the final display snapshot,
    /// including events after the last live push. Null when the display has no
    /// rows.</summary>
    internal Action<OverlaySnapshot?>? OnEncounterEnd { get; set; }

    internal bool HasLiveEncounter => this.current != null;

    internal bool HasLiveDamage => this.view?.LastDamage != null;

    /// <summary>Lets the feed initialize a new engine from cached zone metadata without
    /// treating a replay as a zone change.</summary>
    internal bool HasZone => this.zone.Length > 0;

    /// <summary>Install cached zone metadata without erasing cached identity.</summary>
    internal void SetInitialZone(string name)
    {
        if (!this.HasZone) this.zone = BoundName(name);
    }

    /// <summary>Set the display idle timeout in seconds. Invalid values are ignored and
    /// encounter totals are unaffected.</summary>
    internal void SetIdleTimeout(double secs)
    {
        if (double.IsNaN(secs) || double.IsInfinity(secs))
        {
            return;
        }

        this.idleTimeout = Math.Clamp(secs, 15.0, 600.0);
    }


    /// <summary>Normalize hexadecimal IDs with a decimal fallback. Reject invalid IDs and
    /// the no-target values 0 and E0000000.</summary>
    private static int? ActorInt(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        long v;
        if (!long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) &&
            !long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
        {
            return null;
        }

        // Reject values outside the wire's 32 bits. The cast preserves IDs with the high
        // bit set.
        if (v <= 0 || v == 0xE0000000L || v > 0xFFFFFFFFL)
        {
            return null;
        }

        return unchecked((int)v);
    }

    /// <summary>Update the job cache from roster events as well as spawn lines.</summary>
    internal void NoteJob(int aid, int job)
    {
        if (job <= 0 || (uint)aid >> 24 != 0x10)
        {
            return;
        }

        this.jobs.Set(aid, job);
        // Update existing rows too. A pet may have created the owner's row before the
        // roster arrived.
        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null && enc.Combatants.ContainsKey(aid))
            {
                this.CombatantFor(enc, aid);
            }
        }
    }

    /// <summary>Set local identity before the first 02 line. Invalid IDs leave it
    /// unchanged.</summary>
    internal void SetMe(int aid)
    {
        if (aid <= 0)
        {
            return;
        }

        this.meId = aid;
    }

    internal void SetRoster(IEnumerable<KeyValuePair<int, int>> members)
    {
        this.rosterJobs.Clear();
        foreach (var member in members.Take(MaxOverlayRows))
        {
            if ((uint)member.Key >> 24 == 0x10 && member.Value > 0)
            {
                this.rosterJobs[member.Key] = member.Value;
                this.NoteJob(member.Key, member.Value);
            }
        }
    }

    private int JobFor(int id)
    {
        if (this.rosterJobs.TryGetValue(id, out var job))
        {
            return job;
        }

        if (this.current?.Combatants.TryGetValue(id, out var active) == true && active.Job != 0)
        {
            return active.Job;
        }

        return this.jobs.Get(id);
    }

    // Retired players can return after their job cache entry expires.
    private bool IsPlayer(int? aid)
        => aid is int id && (id == this.meId || this.JobFor(id) != 0 ||
            (this.current?.ActorsLimited == true && (uint)id >> 24 == 0x10));

    /// <summary>Credit player and pet damage dealt, and damage taken directly by players.
    /// Damage to pets or unrelated actors must not advance display activity.</summary>
    private static bool CreditsPlayer(int? srcKey, int? tgtKey, int? tid)
        => srcKey != null || (tgtKey != null && tgtKey == tid);

    /// <summary>Resolve players to their own ID and player pets to their owner. Return null
    /// for other actors.</summary>
    private int? PlayerKey(int? aid)
    {
        if (aid is not int id)
        {
            return null;
        }

        if (this.owners.TryGet(id, out var owner) && owner != id)
        {
            return this.IsPlayer(owner) ? owner : null;
        }

        return this.IsPlayer(id) ? id : null;
    }

    private Combatant CombatantFor(Encounter enc, int key, string name = "")
    {
        name = BoundName(name);
        if (!enc.Combatants.TryGetValue(key, out var c))
        {
            if (enc.Combatants.Count >= MaxEncounterActors)
            {
                var oldest = enc.ActorOrder.First;
                while (oldest != null && (oldest.Value == this.meId || this.rosterJobs.ContainsKey(oldest.Value)))
                {
                    oldest = oldest.Next;
                }

                if (oldest != null)
                {
                    var retired = enc.Combatants[oldest.Value];
                    enc.RetiredDamage += retired.Damage;
                    enc.ActorsLimited = true;
                    enc.Combatants.Remove(oldest.Value);
                    enc.ActorOrder.Remove(oldest);
                }
            }

            c = new Combatant(
                key,
                name.Length > 0 ? name : this.names.Get(key) ?? string.Empty,
                this.JobFor(key));
            enc.Combatants[key] = c;
            c.OrderNode = enc.ActorOrder.AddLast(key);
        }
        else
        {
            enc.ActorOrder.Remove(c.OrderNode!);
            enc.ActorOrder.AddLast(c.OrderNode!);

            // A pet may create an unnamed owner row before a later line supplies the owner
            // name.
            if (c.Name.Length == 0)
            {
                c.Name = name.Length > 0 ? name : this.names.Get(key) ?? string.Empty;
            }

            if (c.Job == 0 && this.JobFor(key) != 0)
            {
                c.Job = this.JobFor(key);
            }
        }

        return c;
    }


    private void Begin()
    {
        if (this.current != null)
        {
            // Finalize an idle encounter before starting a new pull. A late tick may have
            // reopened it without a matching combat end.
            var enc = this.current;
            var last = enc.Last ?? enc.Start;
            if (this.clock() - last <= this.idleTimeout)
            {
                return;
            }

            this.FinalizeEncounter();
        }

        var now = this.clock();
        var title = this.zone.Length > 0 ? this.zone : "Encounter";
        this.current = new Encounter(title, now);
        this.view = new Encounter(title, now);
    }

    /// <summary>Capture final display values before clearing the encounter. Notify
    /// listeners even for an empty encounter. Safe when no encounter is open.</summary>
    private void FinalizeEncounter(bool incomplete = false)
    {
        var enc = this.current;
        if (enc == null)
        {
            return;
        }

        var snapshot = this.Snapshot(final: true, incomplete);
        this.current = null;
        this.view = null;
        try
        {
            this.OnEncounterEnd?.Invoke(snapshot);
        }
        catch (Exception)
        {
            // Contain consumer errors so the feed can continue.
        }
    }

    /// <summary>Reset the display segment if damage resumes after the idle timeout.
    /// Encounter totals remain intact.</summary>
    private void NoteDamage(double now)
    {
        var view = this.view;
        if (view == null)
        {
            return;
        }

        if (view.LastDamage is double lastDamage && now - lastDamage > this.idleTimeout)
        {
            this.view = view = new Encounter(view.Title, now);
        }

        view.LastDamage = now;
    }


    /// <summary>Close the feed session before replay can supply fresh identities.</summary>
    internal void FeedLost(bool incomplete = false)
    {
        this.FinalizeEncounter(incomplete);
        this.inAct = false;
        this.inGame = false;
        this.jobs.Clear();
        this.rosterJobs.Clear();
        this.owners.Clear();
        this.names.Clear();
        this.meId = null;
    }

    /// <summary>Either combat flag can begin or end an encounter. ACT may keep its flag set
    /// between pulls. Process a falling edge before a rising edge in the same message to
    /// keep those pulls separate.</summary>
    internal void SetInCombat(bool inAct, bool inGame)
    {
        if (this.current != null &&
            ((this.inAct && !inAct) || (this.inGame && !inGame)))
        {
            this.FinalizeEncounter();
        }

        if ((inAct && !this.inAct) || (inGame && !this.inGame))
        {
            this.Begin();
        }

        this.inAct = inAct;
        this.inGame = inGame;
    }

    /// <summary>Process a log line split on |. Ignore unrelated line types.</summary>
    internal void Process(IReadOnlyList<string> fields)
    {
        if (fields.Count == 0)
        {
            return;
        }

        try
        {
            switch (fields[0])
            {
                case "01":
                    this.OnZone(fields);
                    break;
                case "02":
                    this.OnPrimaryPlayer(fields);
                    break;
                case "03":
                    this.OnAddCombatant(fields);
                    break;
                case "21":
                case "22":
                    this.OnAbility(fields);
                    break;
                case "24":
                    this.OnDotHot(fields);
                    break;
                case "25":
                    this.OnDeath(fields);
                    break;
                case "33":
                    if (fields.Count > 3 && fields[3].ToUpperInvariant() == WipeCommand)
                    {
                        this.FinalizeEncounter();
                    }

                    break;
            }
        }
        catch (Exception)
        {
            // Skip malformed lines without stopping the feed.
        }
    }


    private void OnZone(IReadOnlyList<string> fields)
    {
        if (fields.Count <= 3)
        {
            return;
        }

        // Every zone line ends the encounter and clears actor identity because IDs can be
        // reassigned, including on entry to the same instance.
        this.FinalizeEncounter();
        this.zone = BoundName(fields[3].Trim());
        this.inAct = false;
        this.inGame = false;
        this.rosterJobs.Clear();
        this.jobs.Clear();
        this.owners.Clear();
        this.names.Clear();
        this.meId = null;
    }

    private void OnPrimaryPlayer(IReadOnlyList<string> fields)
    {
        if (fields.Count <= 3)
        {
            return;
        }

        var aid = ActorInt(fields[2]);
        if (aid == null)
        {
            // Keep the known identity until a valid replacement arrives.
            return;
        }

        this.meId = aid;
        var name = BoundName(fields[3].Trim());
        if (name.Length > 0)
        {
            this.names.Set(aid.Value, name);
        }
    }

    private void OnAddCombatant(IReadOnlyList<string> fields)
    {
        if (fields.Count <= 6)
        {
            return;
        }

        var aid = ActorInt(fields[2]);
        if (aid == null)
        {
            return;
        }

        var name = BoundName(fields[3].Trim());
        if (name.Length > 0)
        {
            this.names.Set(aid.Value, name);
        }

        if (!int.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var job))
        {
            job = 0;
        }

        // Require a player ID because Trust and duty support NPCs also have combat jobs.
        if (job != 0 && fields[2].StartsWith("10", StringComparison.Ordinal))
        {
            this.jobs.Set(aid.Value, job);
        }

        var owner = ActorInt(fields[6]);
        if (owner is int ownerId && ownerId != aid.Value)
        {
            this.owners.Set(aid.Value, ownerId);
        }

        // Update both encounter and display rows when a late spawn line supplies actor
        // details.
        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null && enc.Combatants.ContainsKey(aid.Value))
            {
                this.CombatantFor(enc, aid.Value);
            }
        }
    }

    private void OnAbility(IReadOnlyList<string> fields)
    {
        if (fields.Count < 24)
        {
            return;
        }

        var sid = ActorInt(fields[2]);
        var tid = ActorInt(fields[6]);

        // Pets are also identified by the owner fields trailing 21/22 lines.
        var ownerName = string.Empty;
        if (fields.Count > 47)
        {
            var owner = ActorInt(fields[47]);
            if (owner is int ownerId && sid is int srcId && ownerId != srcId)
            {
                this.owners.Set(srcId, ownerId);
                if (fields.Count > 48)
                {
                    ownerName = fields[48].Trim();
                }
            }
        }

        var srcKey = this.PlayerKey(sid);
        var tgtKey = this.PlayerKey(tid);

        var effects = new List<Effect>(8);
        for (var i = 8; i < 24; i += 2)
        {
            if (i + 1 >= fields.Count)
            {
                break;
            }

            if (string.IsNullOrEmpty(fields[i]) && string.IsNullOrEmpty(fields[i + 1]))
            {
                continue;
            }

            effects.Add(UnpackEffect(fields[i], fields[i + 1]));
        }

        // Damage and misses can start an encounter. Healing and buffs before a pull must
        // not start its clock.
        if (this.current == null)
        {
            if ((srcKey == null && tgtKey == null) ||
                !effects.Any(e => e.Kind is EffectKind.Damage or EffectKind.Miss))
            {
                return;
            }

            this.Begin();
        }

        var now = this.clock();
        if (effects.Any(e => e.Kind == EffectKind.Damage && e.Amount > 0) &&
            CreditsPlayer(srcKey, tgtKey, tid))
        {
            this.NoteDamage(now);
        }

        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null && (enc != this.view || !this.ViewPaused(now)))
            {
                this.ApplyAbility(enc, fields, effects, now, srcKey, tgtKey, sid, tid, ownerName);
            }
        }
    }

    private void ApplyAbility(
        Encounter enc,
        IReadOnlyList<string> fields,
        List<Effect> effects,
        double now,
        int? srcKey,
        int? tgtKey,
        int? sid,
        int? tid,
        string ownerName)
    {
        if (CreditsPlayer(srcKey, tgtKey, tid) && effects.Any(e => e.Kind != EffectKind.None))
        {
            enc.Last = now;
        }

        Combatant? src = null;
        if (srcKey is int sk)
        {
            // Use the owner name from the trailing fields, not the pet name.
            src = this.CombatantFor(enc, sk, srcKey == sid ? fields[3] : ownerName);
        }

        Combatant? tgt = null;
        foreach (var e in effects)
        {
            if (e.Kind == EffectKind.Damage)
            {
                if (src != null)
                {
                    src.Damage += e.Amount;
                }

                if (tgtKey is int tk && tk != srcKey && tk == tid)
                {
                    // Match ACT by excluding self damage and pet targets from damage taken.
                    // Enemies do not get meter rows.
                    tgt ??= this.CombatantFor(enc, tk, fields[7]);
                    tgt.DamageTaken += e.Amount;
                }
            }
            else if (e.Kind == EffectKind.Heal)
            {
                if (src != null)
                {
                    src.Healed += e.Amount;
                }
            }
        }
    }

    private void OnDotHot(IReadOnlyList<string> fields)
    {
        if (fields.Count < 19)
        {
            return;
        }

        var tid = ActorInt(fields[2]);
        var which = fields[4];
        long amount;
        if (!long.TryParse(fields[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out amount) &&
            !long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
        {
            amount = 0;
        }

        if (amount < 0 || amount > 0xFFFFFFFFL)
        {
            // Reject negative amounts and values wider than the wire's 32 bits.
            amount = 0;
        }

        var appId = ActorInt(fields[17]);
        var appKey = this.PlayerKey(appId);
        var tgtKey = this.PlayerKey(tid);
        if (this.current == null)
        {
            // Only a positive DoT involving a player can start an encounter here.
            if (which != "DoT" || amount <= 0 || (appKey == null && tgtKey == null))
            {
                return;
            }

            this.Begin();
        }

        var now = this.clock();
        if (which == "DoT" && amount > 0 && CreditsPlayer(appKey, tgtKey, tid))
        {
            this.NoteDamage(now);
        }

        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null && (enc != this.view || !this.ViewPaused(now)))
            {
                ApplyDotHot(enc, fields, which, amount, now, appKey, tgtKey, appId, tid);
            }
        }
    }

    private void ApplyDotHot(
        Encounter enc,
        IReadOnlyList<string> fields,
        string which,
        long amount,
        double now,
        int? appKey,
        int? tgtKey,
        int? appId,
        int? tid)
    {
        if (which is not ("DoT" or "HoT") || amount <= 0)
        {
            // Unsupported or empty ticks must not advance the encounter clock.
            return;
        }

        if (appKey != null || (which == "DoT" && tgtKey != null && tgtKey == tid))
        {
            enc.Last = now;
        }

        if (which == "DoT")
        {
            if (appKey is int ak)
            {
                this.CombatantFor(enc, ak, appKey == appId ? fields[18] : string.Empty).Damage += amount;
            }

            if (tgtKey is int tk && tk != appKey && tk == tid)
            {
                // Exclude self damage and pet targets from damage taken, as in the ability
                // path.
                this.CombatantFor(enc, tk, fields[3]).DamageTaken += amount;
            }
        }
        else
        {
            if (appKey is int ak)
            {
                this.CombatantFor(enc, ak, appKey == appId ? fields[18] : string.Empty).Healed += amount;
            }
        }
    }

    private void OnDeath(IReadOnlyList<string> fields)
    {
        if (fields.Count <= 3)
        {
            return;
        }

        var tid = ActorInt(fields[2]);
        var key = this.PlayerKey(tid);
        if (key == null || key != tid)
        {
            // Only player deaths count. Pet deaths do not count toward their owners.
            return;
        }

        if (this.current == null)
        {
            // Deaths outside an encounter must not start a new one.
            return;
        }

        var now = this.clock();
        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc == null)
            {
                continue;
            }

            enc.Last = now;
            this.CombatantFor(enc, key.Value, fields[3]).Deaths += 1;
        }
    }


    /// <summary>Return the current display snapshot or null when no encounter is open.
    /// Pause after damage inactivity, using encounter start as the fallback if only misses
    /// occurred.</summary>
    internal OverlaySnapshot? LiveSnapshot() => this.Snapshot(final: false);

    private bool ViewPaused(double now)
        => this.view != null && now - (this.view.LastDamage ?? this.view.Start) > this.idleTimeout;

    private OverlaySnapshot? Snapshot(bool final, bool incomplete = false)
    {
        if (this.current == null)
        {
            return null;
        }

        var enc = this.view ?? this.current;
        var idleBase = enc.LastDamage ?? enc.Start;
        var spanEnd = final
            ? enc.Last ?? enc.Start
            : Math.Min(this.clock(), idleBase + this.idleTimeout);
        var duration = Math.Max(0.0, spanEnd - enc.Start);
        var encPer = Math.Max(1.0, duration);
        long totalDamage = enc.RetiredDamage;
        foreach (var c in enc.Combatants.Values)
        {
            totalDamage += c.Damage;
        }

        var rows = new List<MeterRow>(enc.Combatants.Count);
        foreach (var c in enc.Combatants.Values)
        {
            var share = totalDamage > 0 ? c.Damage / (double)totalDamage * 100.0 : 0.0;
            rows.Add(new MeterRow(
                c.Name.Length > 0 ? c.Name : $"{c.Aid:X}",
                JobAcronyms.TryGetValue(c.Job, out var acronym) ? acronym : string.Empty,
                Math.Round(c.Damage / encPer, 1),
                Math.Round(share, 1),
                Math.Round(c.Healed / encPer, 1),
                this.meId != null && c.Aid == this.meId,
                c.Deaths));
        }

        var sorted = rows.OrderByDescending(r => r.EncDps)
            .Select((row, index) => row with { Rank = index + 1 })
            .Where(row => row.Rank <= MaxOverlayRows || row.IsSelf)
            .ToList();
        return new OverlaySnapshot
        {
            Title = enc.Title + (enc.ActorsLimited ? " [limited actors]" : string.Empty)
                + (incomplete ? " [incomplete feed]" : string.Empty),
            Duration = MmSs(duration),
            EncDps = Math.Round(totalDamage / Math.Max(1.0, duration), 1),
            HasDamage = enc.LastDamage != null,
            Rows = sorted,
        };
    }

    private static string MmSs(double seconds)
    {
        var s = Math.Max(0, (int)seconds);
        return $"{s / 60:D2}:{s % 60:D2}";
    }

    /// <summary>Decode a [flags, damage] pair from a 21 or 22 line. Ignore combo and
    /// positional bytes. Status effects and padding return none. Healing never counts as a
    /// direct hit.</summary>
    private static Effect UnpackEffect(string? flagsHex, string? dmgHex)
    {
        if (!long.TryParse(flagsHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var f))
        {
            f = 0;
        }

        var etype = (int)(f & 0xFF);
        var severity = (int)((f >> 8) & 0xFF);
        var crit = (severity & 0x20) != 0;
        var dh = (severity & 0x40) != 0 && etype != HealType;
        var kind = etype switch
        {
            0x03 or 0x05 or 0x06 or 0x33 => EffectKind.Damage,
            HealType => EffectKind.Heal,
            0x01 or 0x02 => EffectKind.Miss,
            _ => EffectKind.None,
        };

        if (!long.TryParse(dmgHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ||
            v < 0 || v > 0xFFFFFFFFL)
        {
            // Reject values outside the wire's 32 bits.
            v = 0;
        }

        long amount;
        if (kind == EffectKind.Heal && v > 0 && v < 0x10000)
        {
            // Small literal heals such as Plenary are already unshifted.
            amount = v;
        }
        else if (kind == EffectKind.Damage && (v & 0x0100) != 0)
        {
            // The invulnerability flag means no damage was dealt.
            amount = 0;
        }
        else if ((v & 0x4000) != 0)
        {
            // For extended damage, the low byte becomes the high byte of the amount.
            amount = ((v & 0xFF) << 16) | (v >> 16);
        }
        else
        {
            amount = v >> 16;
        }

        return new Effect(kind, (int)amount, crit, dh);
    }

    private enum EffectKind
    {
        None,
        Damage,
        Heal,
        Miss,
    }

    private readonly record struct Effect(EffectKind Kind, int Amount, bool Crit, bool Dh);

    /// <summary>Encounter duration ends at the last combat activity, excluding time until
    /// finalization. LastDamage is used only by the display view for idle
    /// handling.</summary>
    private sealed class Encounter
    {
        internal Encounter(string title, double start)
        {
            this.Title = title;
            this.Start = start;
        }

        internal string Title { get; }

        internal double Start { get; }

        internal double? Last { get; set; }

        internal double? LastDamage { get; set; }

        internal Dictionary<int, Combatant> Combatants { get; } = new();
        internal LinkedList<int> ActorOrder { get; } = new();
        internal long RetiredDamage { get; set; }
        internal bool ActorsLimited { get; set; }
    }

    /// <summary>Player totals include contributions from owned pets.</summary>
    private sealed class Combatant
    {
        internal Combatant(int aid, string name, int job)
        {
            this.Aid = aid;
            this.Name = name;
            this.Job = job;
        }

        internal LinkedListNode<int>? OrderNode { get; set; }

        internal int Aid { get; }

        internal string Name { get; set; }

        internal int Job { get; set; }

        internal long Damage { get; set; }

        internal long Healed { get; set; }

        internal long DamageTaken { get; set; }

        internal int Deaths { get; set; }
    }

    /// <summary>Updating an actor moves it to the most recently seen position. Trim after
    /// insertion so the map stays within its limit.</summary>
    private sealed class BoundedMap<T>
    {
        private const int Cap = 1024;

        private readonly LinkedList<int> order = new();
        private readonly Dictionary<int, (T Value, LinkedListNode<int> Node)> map = new();

        internal void Set(int key, T value)
        {
            if (this.map.TryGetValue(key, out var entry))
            {
                this.order.Remove(entry.Node);
            }

            var node = this.order.AddLast(key);
            this.map[key] = (value, node);
            while (this.map.Count > Cap)
            {
                var oldest = this.order.First!;
                this.order.RemoveFirst();
                this.map.Remove(oldest.Value);
            }
        }

        internal T Get(int key)
            => this.map.TryGetValue(key, out var entry) ? entry.Value : default!;

        internal bool TryGet(int key, out T value)
        {
            if (this.map.TryGetValue(key, out var entry))
            {
                value = entry.Value;
                return true;
            }

            value = default!;
            return false;
        }

        internal void Clear()
        {
            this.map.Clear();
            this.order.Clear();
        }
    }
}
