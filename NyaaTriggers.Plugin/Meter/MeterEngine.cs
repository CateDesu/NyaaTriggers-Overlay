using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NyaaTriggers.Plugin.Meter;

// Ported from NyaaTriggers dps_meter.py. Effect decoding follows cactbot LogGuide.
// Encounter totals follow ACT. The display resets when damage resumes after idle.

internal sealed record CombatStats
{
    internal double? Damage { get; init; }
    internal double? Healed { get; init; }
    internal double? HealShare { get; init; }
    internal double? Crit { get; init; }
    internal double? Direct { get; init; }
    internal double? CritDirect { get; init; }
    internal double? Taken { get; init; }
    internal double? HealingTaken { get; init; }
    internal double? Heals { get; init; }
    internal double? Overheal { get; init; }
    internal double? Hits { get; init; }
}

internal readonly record struct MeterRow(string Name, string Job, double EncDps, double Share, double Hps, bool IsSelf, int Deaths, int Rank = 0)
{
    internal CombatStats? Stats { get; init; }
}

internal sealed class OverlaySnapshot
{
    internal string Id { get; init; } = string.Empty;
    internal string Zone { get; init; } = string.Empty;
    internal double EncHps { get; init; }
    internal int Participants { get; init; }
    internal required string Title { get; init; }

    internal required string Duration { get; init; }

    internal required double EncDps { get; init; }

    internal bool HasDamage { get; init; }

    internal required IReadOnlyList<MeterRow> Rows { get; init; }
}

internal sealed class MeterEngine
{
    /// <summary>ActorControl, line 33, command for a wipe or reset.</summary>
    private const string WipeCommand = "4000000F";

    /// <summary>Seconds without damage before pausing the display. Encounter totals stay intact.</summary>
    private const double DefaultIdleTimeout = 120.0;

    /// <summary>Include the local player even if ranked below the limit.</summary>
    private const int MaxOverlayRows = 24;

    // Keep local and party actors. Retired damage stays in totals with a reduced history marker.
    private const int MaxEncounterActors = 1024;

    private const int HealType = 0x04;
    private const int MaxNameChars = 256;

    private static string BoundName(string name)
        => name.Length <= MaxNameChars ? name : name[..MaxNameChars];

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

    /// <summary>Called synchronously with final display rows, or null for an empty display.</summary>
    internal Action<OverlaySnapshot?>? OnEncounterEnd { get; set; }

    internal bool HasLiveEncounter => this.current != null;

    internal bool HasLiveDamage => this.view?.LastDamage != null;

    /// <summary>Cached zone replay must initialize a new engine without clearing identity.</summary>
    internal bool HasZone => this.zone.Length > 0;

    internal void SetInitialZone(string name)
    {
        if (this.HasZone) return;
        this.zone = BoundName(name);
        if (!this.HasZone) return;
        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null) enc.Title = this.zone;
        }
    }

    internal void SetIdleTimeout(double secs)
    {
        if (double.IsNaN(secs) || double.IsInfinity(secs))
        {
            return;
        }

        this.idleTimeout = Math.Clamp(secs, 15.0, 600.0);
    }


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

        // Preserve high bits when casting valid wire IDs.
        if (v <= 0 || v == 0xE0000000L || v > 0xFFFFFFFFL)
        {
            return null;
        }

        return unchecked((int)v);
    }

    internal void NoteJob(int aid, int job)
    {
        if (job <= 0 || (uint)aid >> 24 != 0x10)
        {
            return;
        }

        this.jobs.Set(aid, job);
        if (this.rosterJobs.ContainsKey(aid)) this.rosterJobs[aid] = job;
        // Pet actions may create owner rows before the roster arrives.
        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null && enc.Combatants.TryGetValue(aid, out var actor))
            {
                actor.Job = job;
                this.CombatantFor(enc, aid);
            }
        }
    }

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

        if (this.jobs.TryGet(id, out job)) return job;

        if (this.current?.Combatants.TryGetValue(id, out var active) == true && active.Job != 0)
        {
            return active.Job;
        }

        return 0;
    }

    // Retired players can return after their job cache entry expires.
    private bool IsPlayer(int? aid)
        => aid is int id && (id == this.meId || this.JobFor(id) != 0 ||
            (this.current?.ActorsLimited == true && (uint)id >> 24 == 0x10));

    private static bool CreditsPlayer(int? srcKey, int? tgtKey, int? tid)
        => srcKey != null || (tgtKey != null && tgtKey == tid);

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
                    enc.RetiredHealing += retired.Healed;
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

            // A pet action may create the owner row before its name arrives.
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
            // A late tick may reopen an idle encounter without a matching combat end.
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

    /// <summary>Notify even for empty encounters, using rows captured before clearing.</summary>
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

    /// <summary>ACT can stay in combat between pulls. Process falling edges before rising ones.</summary>
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

        // Actor IDs can be reassigned even on entry to the same instance.
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
            this.NoteJob(aid.Value, job);
        }

        var owner = ActorInt(fields[6]);
        if (owner is int ownerId && ownerId != aid.Value)
        {
            this.owners.Set(aid.Value, ownerId);
        }

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
        var reflected = false;
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

            if (!uint.TryParse(fields[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags))
            {
                continue;
            }

            if ((flags & 0xFF) == 0x1D)
            {
                reflected = true;
                continue;
            }

            var effect = UnpackEffect(fields[i], fields[i + 1]);
            effects.Add(effect with { Reflected = reflected && effect.Kind == EffectKind.Damage });
        }

        // Prepull healing and buffs must not start the clock.
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
        if (effects.Any(e => e.Kind == EffectKind.Damage && e.Amount > 0 &&
            (e.Reflected ? CreditsPlayer(tgtKey, srcKey, sid) : CreditsPlayer(srcKey, tgtKey, tid))))
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
        if (effects.Any(e => e.Kind != EffectKind.None &&
            (e.Reflected ? CreditsPlayer(tgtKey, srcKey, sid) : CreditsPlayer(srcKey, tgtKey, tid))))
        {
            enc.Last = now;
        }

        Combatant? src = null;
        if (srcKey is int sk)
        {
            src = this.CombatantFor(enc, sk, srcKey == sid ? fields[3] : ownerName);
        }

        foreach (var e in effects)
        {
            if (e.Kind == EffectKind.Damage)
            {
                var dealerKey = e.Reflected ? tgtKey : srcKey;
                var victimKey = e.Reflected ? srcKey : tgtKey;
                var victimId = e.Reflected ? sid : tid;
                var victimName = e.Reflected ? fields[3] : fields[7];
                var dealer = src;
                if (e.Reflected)
                {
                    dealer = tgtKey is int reflector
                        ? this.CombatantFor(enc, reflector, tgtKey == tid ? fields[7] : string.Empty)
                        : null;
                }

                if (dealer != null)
                {
                    dealer.Damage += e.Amount;
                    if (e.Amount > 0)
                    {
                        dealer.Hits++;
                        if (e.Crit) dealer.Crits++;
                        if (e.Dh) dealer.Directs++;
                        if (e.Crit && e.Dh) dealer.CritDirects++;
                    }
                }

                if (victimKey is int tk && tk != dealerKey && tk == victimId)
                {
                    // Match ACT by excluding self damage and pet targets from damage taken.
                    this.CombatantFor(enc, tk, victimName).DamageTaken += e.Amount;
                }
            }
            else if (e.Kind == EffectKind.Heal)
            {
                if (src != null)
                {
                    src.Healed += e.Amount;
                    if (e.Amount > 0) src.Heals++;
                }

                if (tgtKey is int receiver && tgtKey == tid)
                {
                    this.CombatantFor(enc, receiver, fields[7]).HealingTaken += e.Amount;
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
            amount = 0;
        }

        var appId = ActorInt(fields[17]);
        var appKey = this.PlayerKey(appId);
        var tgtKey = this.PlayerKey(tid);
        if (this.current == null)
        {
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
                this.CombatantFor(enc, tk, fields[3]).DamageTaken += amount;
            }
        }
        else
        {
            if (appKey is int ak)
            {
                this.CombatantFor(enc, ak, appKey == appId ? fields[18] : string.Empty).Healed += amount;
            }

            if (tgtKey is int receiver && tgtKey == tid)
            {
                this.CombatantFor(enc, receiver, fields[3]).HealingTaken += amount;
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
            return;
        }

        if (this.current == null)
        {
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


    /// <summary>Pause on damage inactivity. Use encounter start when only misses occurred.</summary>
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
        long totalHealing = enc.RetiredHealing;
        foreach (var c in enc.Combatants.Values)
        {
            totalDamage += c.Damage;
            totalHealing += c.Healed;
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
                c.Deaths)
            {
                Stats = new CombatStats
                {
                    Damage = c.Damage,
                    Healed = c.Healed,
                    HealShare = totalHealing > 0 ? c.Healed * 100.0 / totalHealing : 0,
                    Crit = c.Hits > 0 ? c.Crits * 100.0 / c.Hits : 0,
                    Direct = c.Hits > 0 ? c.Directs * 100.0 / c.Hits : 0,
                    CritDirect = c.Hits > 0 ? c.CritDirects * 100.0 / c.Hits : 0,
                    Taken = c.DamageTaken,
                    HealingTaken = c.HealingTaken,
                    Heals = c.Heals,
                    Hits = c.Hits,
                },
            });
        }

        var sorted = rows.OrderByDescending(r => r.EncDps)
            .Select((row, index) => row with { Rank = index + 1 })
            .Where(row => row.Rank <= MaxOverlayRows || row.IsSelf)
            .ToList();
        return new OverlaySnapshot
        {
            Id = enc.Id,
            Zone = this.zone,
            EncHps = Math.Round(totalHealing / encPer, 2),
            Participants = enc.Combatants.Count,
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

    /// <summary>Decode 21/22 effects, ignoring combo and positional bytes.</summary>
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

    private readonly record struct Effect(EffectKind Kind, int Amount, bool Crit, bool Dh, bool Reflected = false);

    /// <summary>Duration ends at last activity. LastDamage controls only display idle handling.</summary>
    private sealed class Encounter
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal Encounter(string title, double start)
        {
            this.Title = title;
            this.Start = start;
        }

        internal string Title { get; set; }

        internal double Start { get; }

        internal double? Last { get; set; }

        internal double? LastDamage { get; set; }

        internal Dictionary<int, Combatant> Combatants { get; } = new();
        internal LinkedList<int> ActorOrder { get; } = new();
        internal long RetiredDamage { get; set; }
        internal long RetiredHealing { get; set; }
        internal bool ActorsLimited { get; set; }
    }

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
        internal long HealingTaken { get; set; }
        internal long Heals { get; set; }
        internal long Hits { get; set; }
        internal long Crits { get; set; }
        internal long Directs { get; set; }
        internal long CritDirects { get; set; }

        internal long DamageTaken { get; set; }

        internal int Deaths { get; set; }
    }

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
