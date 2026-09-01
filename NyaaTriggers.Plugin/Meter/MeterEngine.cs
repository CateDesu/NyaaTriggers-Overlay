using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NyaaTriggers.Plugin.Meter;

// ACT-style DPS meter, parsed straight from the combat log feed. Ported
// behavior-faithful from NyaaTriggers dps_meter.py, same wire decode, same
// encounter lifecycle, same ACT aggregation rules. Pure System.* so tests
// can run it headless.
//
// Effect pair decode follows cactbot's LogGuide, cross-checked against real
// captures. Flags byte 0 is the effect type, 0x03/0x05/0x06/0x33 damage,
// 0x04 heal, 0x01/0x02 miss or dodge. Byte 1 is the severity, 0x20 crit,
// 0x40 direct hit. Damage value: the 0x0100 mask means hallowed, amount 0.
// The 0x4000 mask means "a lot" of damage, bytes ABCD become DAB as a
// 3-byte integer, the low byte shifted left 16 or the high word. Otherwise
// the amount is the high word. A shifted literal heal under 0x10000, the
// Plenary family, is taken as-is since shifting it right 16 reads 0.
//
// Encounter lifecycle mirrors ACT. Begins on a combat flag rising, either
// InCombat bool, or lazily on the first hostile effect involving a player,
// so attaching mid-fight still meters the pull. Finalizes on a combat flag
// dropping, on a wipe, or on any 01 zone line. Pulls with no player damage
// and no player damage taken finalize silently, no callback.
//
// Every event lands twice, on the encounter and on the display view. Only
// the view resets when damage resumes past the idle timeout, so the overlay
// draws a fresh segment while the encounter itself is never split.
//
// Pets merge into their owner like ACT's combine pets with owner. Owner ids
// arrive on 03 lines and on the trailing owner fields of 21/22 lines.
//
// Deliberate simplifications vs the Python original. The overlay only draws
// name, job, encdps, share, hps, is-self and deaths, so dropped: the swings,
// hits, crits, dhits, cdhits and maxhit counters, the own-activity first and
// last stamps with touch, wall_start, the _last_final between-pulls
// retention and the ACT-style snapshot dict. Crit and dh are still decoded
// per pair, nothing counts them anymore. damagetaken stays since the
// empty-pull check needs it, enc.last and enc.last_damage stay for the
// duration math and the idle logic.

internal readonly record struct MeterRow(string Name, string Job, double EncDps, double Share, double Hps, bool IsSelf, int Deaths);

/// <summary>The live display frame. Rebuilt on every read rather than
/// mutated, so the UI never reads a half-updated meter.</summary>
internal sealed class OverlaySnapshot
{
    internal required string Title { get; init; }

    internal required string Duration { get; init; }

    internal required double EncDps { get; init; }

    internal required IReadOnlyList<MeterRow> Rows { get; init; }
}

/// <summary>Feed combat log lines in, read overlay snapshots out. Never
/// raises on malformed input, a bad line is skipped not fatal.</summary>
internal sealed class MeterEngine
{
    /// <summary>ActorControl, line 33, command for a wipe or reset.</summary>
    private const string WipeCommand = "4000000F";

    /// <summary>Default damage-idle timeout for the on-screen meter. After
    /// this long with no damage the live view pauses, the next hit starts a
    /// fresh segment. Display only, the recorded pull is never split.</summary>
    private const double DefaultIdleTimeout = 120.0;

    /// <summary>Most player rows the overlay carries. Alliance raids run to
    /// 24, more would only ever be a bug.</summary>
    private const int MaxOverlayRows = 24;

    private const int HealType = 0x04;

    // ClassJob id to acronym. Ids 8 to 18 are crafting and gathering classes
    // and map to "", no combat row worth labelling. 0 is NPC or none.
    // Unknown future jobs degrade to "" rather than a wrong guess.
    private static readonly IReadOnlyDictionary<int, string> JobAcronyms = new Dictionary<int, string>
    {
        { 1, "GLA" }, { 2, "PGL" }, { 3, "MRD" }, { 4, "LNC" }, { 5, "ARC" }, { 6, "CNJ" }, { 7, "THM" },
        { 19, "PLD" }, { 20, "MNK" }, { 21, "WAR" }, { 22, "DRG" }, { 23, "BRD" }, { 24, "WHM" },
        { 25, "BLM" }, { 26, "ACN" }, { 27, "SMN" }, { 28, "SCH" }, { 29, "ROG" }, { 30, "NIN" },
        { 31, "MCH" }, { 32, "DRK" }, { 33, "AST" }, { 34, "SAM" }, { 35, "RDM" }, { 36, "BLU" },
        { 37, "GNB" }, { 38, "DNC" }, { 39, "RPR" }, { 40, "SGE" }, { 41, "VPR" }, { 42, "PCT" },
    };

    private readonly Func<double> clock;
    private readonly BoundedMap<int> jobs = new();      // actor id to ClassJob id, nonzero means a player
    private readonly BoundedMap<int> owners = new();    // pet or summon id to owner id
    private readonly BoundedMap<string> names = new();  // actor id to last seen name

    private string zone = string.Empty;
    private int? meId;
    private bool inAct;
    private bool inGame;
    private Encounter? current;
    private Encounter? view;   // display segment, resets on idle
    private double idleTimeout = DefaultIdleTimeout;

    internal MeterEngine(Func<double>? clock = null)
    {
        this.clock = clock ?? DefaultClock;
    }

    private static double DefaultClock() => Environment.TickCount64 / 1000.0;

    /// <summary>Fired synchronously when a non-empty encounter finalizes.</summary>
    internal Action? OnEncounterEnd { get; set; }

    internal bool HasLiveEncounter => this.current != null;

    /// <summary>How long the meter keeps ticking after the last damage before
    /// it pauses, resetting on the next hit. Display only, the recorded pull
    /// is never split or shortened by this. Bad input is ignored.</summary>
    internal void SetIdleTimeout(double secs)
    {
        if (double.IsNaN(secs) || double.IsInfinity(secs))
        {
            return;
        }

        this.idleTimeout = Math.Clamp(secs, 15.0, 600.0);
    }

    // ------------------------------------------------------------------
    // actor bookkeeping
    // ------------------------------------------------------------------

    /// <summary>Actor id as an int, hex string with a decimal fallback, so
    /// padded or case variants of the same id resolve to one key. Null for
    /// blank or invalid ids and the no-target sentinels 0 and E0000000.</summary>
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

        // Ids wider than the 32-bit wire value are a bad line, and the bit
        // pattern survives the int cast so high-bit ids stay one key.
        if (v <= 0 || v == 0xE0000000L || v > 0xFFFFFFFFL)
        {
            return null;
        }

        return unchecked((int)v);
    }

    /// <summary>A roster job from outside the log stream, the PartyChanged
    /// burst. Same map the 03 lines fill, so a mid-instance connect stops
    /// reading the party as enemies once the roster lands.</summary>
    internal void NoteJob(int aid, int job)
    {
        if (job == 0)
        {
            return;
        }

        this.jobs.Set(aid, job);
        // The burst can land after a pet line already opened the owner's row
        // at job 0. Run the same late upgrade the 03 handler runs, or the job
        // cell stays blank until the owner personally acts.
        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null && enc.Combatants.ContainsKey(aid))
            {
                this.CombatantFor(enc, aid);
            }
        }
    }

    /// <summary>The local player, pinned before any 02 line arrives. A blank
    /// or malformed id changes nothing.</summary>
    internal void SetMe(int aid)
    {
        if (aid <= 0)
        {
            return;
        }

        this.meId = aid;
    }

    private bool IsPlayer(int? aid)
        => aid is int id && (id == this.meId || this.jobs.Get(id) != 0);

    /// <summary>The combatant record key for an actor. The owner id for
    /// player pets, the id itself for players, null for enemies and their
    /// minions.</summary>
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
        if (!enc.Combatants.TryGetValue(key, out var c))
        {
            c = new Combatant(
                key,
                name.Length > 0 ? name : this.names.Get(key) ?? string.Empty,
                this.jobs.Get(key));
            enc.Combatants[key] = c;
        }
        else
        {
            // Records created by a pet's line start nameless, the line only
            // names the pet. The owner's name lands once an 03 or an owner
            // line supplies it.
            if (c.Name.Length == 0)
            {
                c.Name = name.Length > 0 ? name : this.names.Get(key) ?? string.Empty;
            }

            if (c.Job == 0 && this.jobs.Get(key) != 0)
            {
                c.Job = this.jobs.Get(key);
            }
        }

        return c;
    }

    // ------------------------------------------------------------------
    // encounter lifecycle
    // ------------------------------------------------------------------

    private void Begin()
    {
        if (this.current != null)
        {
            // A stray late tick can reopen an encounter nobody finalizes.
            // Past the idle timeout it is dead weight. Close it out before
            // the fresh pull starts, or the two merge into one phantom.
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
        // The on-screen view runs alongside the encounter. Only the view
        // resets on damage idle. The encounter always logs the whole pull.
        this.view = new Encounter(title, now);
    }

    /// <summary>End the current encounter, if any, and fire OnEncounterEnd
    /// for non-empty ones. Safe to call with nothing in progress.</summary>
    private void FinalizeEncounter()
    {
        var enc = this.current;
        if (enc == null)
        {
            return;
        }

        this.current = null;
        this.view = null;
        var any = false;
        foreach (var c in enc.Combatants.Values)
        {
            if (c.Damage > 0 || c.DamageTaken > 0)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;   // empty pull, nothing worth keeping
        }

        try
        {
            this.OnEncounterEnd?.Invoke();
        }
        catch (Exception)
        {
            // A consumer bug must not kill the feed.
        }
    }

    /// <summary>Stamp damage activity on the display view. Damage landing
    /// more than the idle timeout after the previous hit resets the view
    /// first, the frozen numbers give way to a fresh segment starting with
    /// this hit. The encounter is never touched.</summary>
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

    // ------------------------------------------------------------------
    // feed
    // ------------------------------------------------------------------

    /// <summary>InCombat event, inACTCombat and inGameCombat. A rising edge
    /// on either flag begins the encounter, a falling edge on either ends it.
    /// ACT can hold inACTCombat high across back-to-back pulls of one
    /// instance, so keying only on it would merge pulls, and keying only on
    /// inGameCombat would miss ACT-only combat. A mixed message, one flag
    /// falling while the other rises, finalizes the open encounter before the
    /// new begin so the two pulls never merge.</summary>
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

    /// <summary>One log line pre-split on '|'. Anything outside the meter
    /// types returns right away.</summary>
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
            // Defensive, the caller wraps this too. A bad line is skipped.
        }
    }

    // ------------------------------------------------------------------
    // line handlers
    // ------------------------------------------------------------------

    private void OnZone(IReadOnlyList<string> fields)
    {
        if (fields.Count <= 3)
        {
            return;
        }

        // A zone change hard-ends any pull in progress, like ACT, including
        // re-entering the same instance for the next pull. Entity ids are
        // reassigned per entry, so actor knowledge must reset anyway, the
        // local player id too. The next 02 line pins it again.
        this.FinalizeEncounter();
        this.zone = fields[3].Trim();
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
            // A blank or garbage id must not wipe a known good one. The next
            // valid 02 line can still correct the pin.
            return;
        }

        this.meId = aid;
        var name = fields[3].Trim();
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

        var name = fields[3].Trim();
        if (name.Length > 0)
        {
            this.names.Set(aid.Value, name);
        }

        if (!int.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var job))
        {
            job = 0;
        }

        // Players only, the '10'-prefixed ids. Duty support and Trust NPCs
        // carry real ClassJob ids and would otherwise land as rows.
        if (job != 0 && fields[2].StartsWith("10", StringComparison.Ordinal))
        {
            this.jobs.Set(aid.Value, job);
        }

        var owner = ActorInt(fields[6]);   // "0000" or "00" parse to 0, unowned
        if (owner is int ownerId && ownerId != aid.Value)
        {
            this.owners.Set(aid.Value, ownerId);
        }

        // Late 03 lines can upgrade a record created by an earlier 21 line.
        // Both records, the encounter log and the on-screen view, or the
        // overlay keeps the stale nameless label until the owner acts again.
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

        // Only hostile action opens an encounter lazily. A pre-pull regen or
        // buff, status effects and heals, minutes before the engage must not
        // start the clock, or every pull's duration would include the
        // preamble. Damage and misses count. Heals alone do not.
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
        if (effects.Any(e => e.Kind == EffectKind.Damage && e.Amount > 0))
        {
            this.NoteDamage(now);
        }

        // Everything lands twice. On the encounter, the log, and on the
        // display view, what the meter shows right now.
        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null)
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
        if (effects.Any(e => e.Kind != EffectKind.None))
        {
            enc.Last = now;
        }

        Combatant? src = null;
        if (srcKey is int sk)
        {
            // A pet's line names the pet, not the owner it merges into. The
            // ownerName trailing the line, or a later 03, supplies the owner.
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
                    // Enemy damage on players is only tracked as taken. The
                    // enemy itself never becomes a meter row. Self-damage
                    // credits damage only, ACT excludes it from taken. A pet
                    // target resolves to its owner and credits no one, like
                    // ACT credits pet deaths to no one.
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
            // Same guard as the 21 path. Negative hex parses fine and 9+
            // digit fields overflow the wire value. A bad tick is skipped.
            amount = 0;
        }

        var appId = ActorInt(fields[17]);
        var appKey = this.PlayerKey(appId);
        var tgtKey = this.PlayerKey(tid);
        if (this.current == null)
        {
            // DoT ticks are hostile and can open an encounter. A pre-pull
            // regen, a HoT, cannot. A zero-amount tick carries no damage, so
            // it must not open a phantom one either.
            if (which != "DoT" || amount <= 0 || (appKey == null && tgtKey == null))
            {
                return;
            }

            this.Begin();
        }

        var now = this.clock();
        if (which == "DoT" && amount > 0)
        {
            this.NoteDamage(now);
        }

        foreach (var enc in new[] { this.current, this.view })
        {
            if (enc != null)
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
            // A tick with an unknown which field or no amount credits
            // nothing, so it must not bump the encounter clock either.
            return;
        }

        enc.Last = now;
        if (which == "DoT")
        {
            if (appKey is int ak)
            {
                this.CombatantFor(enc, ak, appKey == appId ? fields[18] : string.Empty).Damage += amount;
            }

            if (tgtKey is int tk && tk != appKey && tk == tid)
            {
                // A pet tick resolves to its owner and credits no one, same
                // as the ability path. A tick the applier lands on itself
                // credits damage only, ACT excludes self damage from taken.
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
            // Not a player, or a pet resolving to its owner. ACT credits pet
            // deaths to no one, so the owner's count stays untouched.
            return;
        }

        if (this.current == null)
        {
            // No lazy begin here, unlike the hostile-line paths. A real in
            // combat death always follows the damage that opened the pull,
            // so an open encounter already exists. An out-of-combat death
            // would otherwise start a phantom one with a running clock.
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

    // ------------------------------------------------------------------
    // reporting
    // ------------------------------------------------------------------

    /// <summary>What the overlay should draw right now. Null when no
    /// encounter is open. The live display view, paused at the idle timeout
    /// after the last damage and reset when damage resumes. A whiffed pull
    /// opens on a miss and never stamps last_damage, so the clamp falls back
    /// to the encounter start or the live clock would run unbounded.</summary>
    internal OverlaySnapshot? LiveSnapshot()
    {
        if (this.current == null)
        {
            return null;
        }

        var enc = this.view ?? this.current;
        var idleBase = enc.LastDamage ?? enc.Start;
        var spanEnd = Math.Min(this.clock(), idleBase + this.idleTimeout);
        var duration = Math.Max(0.0, spanEnd - enc.Start);
        var encPer = Math.Max(1.0, duration);
        long totalDamage = 0;
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

        var sorted = rows
            .OrderByDescending(r => r.EncDps)
            .Take(MaxOverlayRows)
            .ToList();
        return new OverlaySnapshot
        {
            Title = enc.Title,
            Duration = MmSs(duration),
            EncDps = Math.Round(totalDamage / Math.Max(1.0, duration), 1),
            Rows = sorted,
        };
    }

    private static string MmSs(double seconds)
    {
        var s = Math.Max(0, (int)seconds);
        return $"{s / 60:D2}:{s % 60:D2}";
    }

    /// <summary>Decode one [flags, damage] effect pair from a 21/22 line.
    /// "none" covers status applications and padding pairs. The two middle
    /// flag bytes are ability-specific combo and positional data, ignored.
    /// Heals never direct hit, so dh is always false for them.</summary>
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
            // Negative hex parses fine and 9+ digit fields overflow the
            // 32-bit wire value. Both are a bad line, credit nothing.
            v = 0;
        }

        long amount;
        if (kind == EffectKind.Heal && v > 0 && v < 0x10000)
        {
            // Shifted literal-value lines, the Plenary family, carry the
            // heal unshifted. A value this small shifted right by 16 reads 0.
            amount = v;
        }
        else if (kind == EffectKind.Damage && (v & 0x0100) != 0)
        {
            // Hallowed or invulnerable, the number is not damage.
            amount = 0;
        }
        else if ((v & 0x4000) != 0)
        {
            // "A lot" of damage, the low byte is the real top byte.
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

    /// <summary>One pull, ACT-style. Titled by the zone, holding every player
    /// who did or took anything. Last is the last recorded combat activity, a
    /// finalized encounter's duration ends there rather than at the finalize
    /// event. ACT trims the out-of-combat tail the same way. LastDamage only
    /// lives on the live display view, where it drives the idle pause and
    /// the segment reset.</summary>
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
    }

    /// <summary>One player's running totals for the current encounter. Pets
    /// never get a record of their own, their contribution lands on the
    /// owner's record.</summary>
    private sealed class Combatant
    {
        internal Combatant(int aid, string name, int job)
        {
            this.Aid = aid;
            this.Name = name;
            this.Job = job;
        }

        internal int Aid { get; }

        internal string Name { get; set; }

        internal int Job { get; set; }

        internal long Damage { get; set; }

        internal long Healed { get; set; }

        internal long DamageTaken { get; set; }

        internal int Deaths { get; set; }
    }

    /// <summary>Bounded insert into an actor map. 03 lines stream for every
    /// passer-by, so a city session would grow these maps without a cap. A
    /// re-note moves the id to the back, the trim drops who was seen longest
    /// ago instead of who arrived first, which can be the current party under
    /// a city worth of passers-by. Insert first, then trim back to the cap,
    /// or the map would rest at 1025.</summary>
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
