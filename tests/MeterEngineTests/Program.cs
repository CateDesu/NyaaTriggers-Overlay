using System;
using System.Collections.Generic;
using NyaaTriggers.Plugin.Meter;

// Exercise the meter with synthetic log lines and a controllable clock.

var passes = 0;
var failures = 0;

void Check(bool cond, string name)
{
    if (cond)
    {
        passes++;
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL: {name}");
    }
}

void CheckNear(double actual, double expected, string name)
    => Check(Math.Abs(actual - expected) < 1e-9, $"{name}: expected {expected}, got {actual}");

static List<string> Pad(List<string> f, int len)
{
    while (f.Count < len)
    {
        f.Add("");
    }

    return f;
}

// 21 line: src at 2/3, ability at 4/5, tgt at 6/7, effect pairs from 8.
static List<string> Ability(string srcId, string srcName, string tgtId, string tgtName, params string[] pairs)
{
    var f = new List<string> { "21", "ts", srcId, srcName, "07", "True Thrust", tgtId, tgtName };
    f.AddRange(pairs);
    return Pad(f, 24);
}

// 03 line: id at 2, name at 3, hex job at 4, owner id at 6.
static List<string> AddCombatant(string id, string name, string jobHex, string ownerId)
    => new() { "03", "ts", id, name, jobHex, "90", ownerId };

// 24 line: target at 2/3, DoT-or-HoT at 4, hex amount at 6, applier at 17/18.
static List<string> Tick(string which, string tgtId, string tgtName, string amountHex, string appId, string appName)
{
    var f = new List<string> { "24", "ts", tgtId, tgtName, which, "0A", amountHex };
    Pad(f, 17);
    f.Add(appId);
    f.Add(appName);
    return f;
}

static List<string> Death(string id, string name) => new() { "25", "ts", id, name };

const string Player = "10000001";
const string PlayerTwo = "10000002";
const string Enemy = "40000010";

// Initial state
{
    var eng = new MeterEngine(() => 0.0);
    Check(!eng.HasLiveEncounter, "initial: no live encounter");
    Check(eng.LiveSnapshot() == null, "initial: snapshot is null");
}

// Effect decoding examples from LogGuide
foreach (var (dmgHex, expected) in new[]
{
    ("47280000", 18216.0),
    ("423F400F", 999999.0),
    ("426B4001", 82539.0),
})
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", dmgHex));
    var snap = eng.LiveSnapshot();
    Check(snap != null, $"decode {dmgHex}: snapshot exists");
    Check(snap!.Rows.Count == 1, $"decode {dmgHex}: one row");
    CheckNear(snap.Rows[0].EncDps, expected, $"decode {dmgHex}");
}

// Critical and direct hit flags do not change the decoded damage amount.
foreach (var flags in new[] { "2003", "4003", "6003" })
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", flags, "47280000"));
    var snap = eng.LiveSnapshot();
    Check(snap != null && snap.Rows.Count == 1, $"flags {flags}: one row");
    CheckNear(snap!.Rows[0].EncDps, 18216.0, $"flags {flags}: amount unchanged");
}

// The invulnerability mask prevents damage credit.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280100"));
    var snap = eng.LiveSnapshot();
    Check(snap != null && snap.Rows.Count == 1, "hallowed: row exists");
    CheckNear(snap!.Rows[0].EncDps, 0.0, "hallowed: zero damage");
}

// Small literal heals remain unshifted. Packed heals use the high word.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Player, "Player One", "0004", "00000FA0"));
    eng.Process(Ability(Player, "Player One", Player, "Player One", "0004", "01F40000"));
    var snap = eng.LiveSnapshot();
    Check(snap != null && snap.Rows.Count == 1, "heals: one row");
    CheckNear(snap!.Rows[0].Hps, 4500.0, "heals: literal 4000 plus shifted 500");
}

// A ninth effect pair past index 23 is not read.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    var line = Ability(Player, "Player One", Enemy, "Striking Dummy");
    line.Add("0003");
    line.Add("47280000");
    eng.Process(line);
    var snap = eng.LiveSnapshot();
    Check(snap != null && snap.Rows.Count == 1, "9th pair: row exists");
    CheckNear(snap!.Rows[0].EncDps, 0.0, "9th pair: ignored");
}

// Spawn lines and player classification
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "22", "0"));   // 0x22 = 34
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    var row = eng.LiveSnapshot()!.Rows[0];
    Check(row.Name == "Player One", "03: name noted");
    Check(row.Job == "SAM", "03: hex job maps to acronym");
}

// NPCs with combat jobs must not become player rows.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Enemy, "Striking Dummy", "22", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Enemy, "Striking Dummy", "40000011", "Other Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows.Count == 0, "03: non-10 id never becomes a row");
}

// Case variants of the same id resolve to one actor.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant("10ABCD01", "Hex Case", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability("10abcd01", "Hex Case", Enemy, "Striking Dummy", "0003", "47280000"));
    var row = eng.LiveSnapshot()!.Rows[0];
    Check(row.Job == "MCH", "lowercase id resolves the same actor");
    CheckNear(row.EncDps, 18216.0, "lowercase id credits damage");
}

// Enemy damage credits only the player's damage taken.
{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Enemy, "Striking Dummy", Player, "Player One", "0003", "47280000"));
    var snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1, "enemy hit: only the player row");
    Check(snap.Rows[0].Name == "Player One", "enemy hit: player row named");
    CheckNear(snap.Rows[0].EncDps, 0.0, "enemy hit: player deals nothing");
    eng.SetInCombat(false, false);
    Check(ends == 1, "enemy hit: damagetaken makes the pull non-empty");
}

// Pet contributions
{
    var eng = new MeterEngine(() => 0.0);
    eng.SetMe(0x10000001);
    eng.Process(AddCombatant(Player, "Player One", "1C", "0"));       // 0x1C = 28 SCH
    eng.Process(AddCombatant("40000050", "Eos", "00", Player));       // pet, owner at 6
    eng.SetInCombat(true, true);
    eng.Process(Ability("40000050", "Eos", Enemy, "Striking Dummy", "0003", "47280000"));
    var snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1, "pet merge: no pet row");
    Check(snap.Rows[0].Name == "Player One", "pet merge: owner row named from 03");
    CheckNear(snap.Rows[0].EncDps, 18216.0, "pet merge: damage lands on the owner");
    eng.Process(Death("40000050", "Eos"));
    Check(eng.LiveSnapshot()!.Rows[0].Deaths == 0, "pet death credits no one");
    eng.Process(Death(Player, "Player One"));
    Check(eng.LiveSnapshot()!.Rows[0].Deaths == 1, "player death counts");
    eng.Process(Ability(Enemy, "Striking Dummy", "40000050", "Eos", "0003", "423F400F"));
    CheckNear(eng.LiveSnapshot()!.Rows[0].EncDps, 18216.0, "pet as target credits no one");
}

// Ability lines can identify pet owners through fields 47 and 48.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    var line = Ability("40000051", "Carbuncle", Enemy, "Striking Dummy", "0003", "47280000");
    Pad(line, 49);
    line[47] = Player;
    line[48] = "Player One";
    eng.Process(line);
    var snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1, "trailing owner: no pet row");
    CheckNear(snap.Rows[0].EncDps, 18216.0, "trailing owner: damage lands on the owner");
}

// Encounter lifecycle
{
    var t = 0.0;
    var ends = 0;
    var eng = new MeterEngine(() => t);
    eng.OnEncounterEnd = _ => ends++;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, false);
    Check(eng.HasLiveEncounter, "lifecycle: combat flag opens");
    Check(eng.LiveSnapshot() != null, "lifecycle: snapshot while open");
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows.Count == 1, "lifecycle: damage rows appear");
    eng.SetInCombat(false, false);
    Check(!eng.HasLiveEncounter, "lifecycle: flags down closes");
    Check(eng.LiveSnapshot() == null, "lifecycle: no snapshot once closed");
    Check(ends == 1, "lifecycle: end fired exactly once");
    eng.SetInCombat(false, false);
    Check(ends == 1, "lifecycle: no double finalize");
}

// Empty encounters still send an end marker.
{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    eng.SetInCombat(true, true);
    eng.SetInCombat(false, false);
    Check(ends == 1, "empty pull sends an end marker");
}

// A falling combat flag ends the current pull before a simultaneous rising flag starts
// another.
{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, false);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    eng.SetInCombat(false, true);
    Check(ends == 1, "mixed message finalizes the first pull");
    Check(eng.HasLiveEncounter, "mixed message begins the next pull");
    Check(eng.LiveSnapshot()!.Rows.Count == 0, "mixed message starts clean");
}

// A wipe line finalizes, other ActorControl lines do not.
{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    eng.Process(new List<string> { "33", "ts", Player, "40000010" });
    Check(eng.HasLiveEncounter, "33: non-wipe command ignored");
    eng.Process(new List<string> { "33", "ts", Player, "4000000f" });
    Check(!eng.HasLiveEncounter, "33: wipe command finalizes, case-insensitive");
    Check(ends == 1, "33: end fired once");
}

// Zone lines end the encounter and reset actor identity and jobs.
{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    eng.Process(new List<string> { "01", "ts", "1E1", "Limsa Lominsa" });
    Check(!eng.HasLiveEncounter, "zone: finalizes the pull");
    Check(ends == 1, "zone: end fired once");
    eng.SetInCombat(false, false);
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows.Count == 0, "zone: actor knowledge reset, no rows");
    // A later 02 line restores local identity after zoning.
    eng.Process(new List<string> { "02", "ts", Player, "Player One" });
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    var snap = eng.LiveSnapshot();
    Check(snap!.Title == "Limsa Lominsa", "zone: title follows the zone");
    Check(snap.Rows[0].Job == "", "zone: jobs forgotten, acronym falls back");
    Check(snap.Rows[0].IsSelf, "zone: 02 re-pins the local player");
}

// A new begin closes an idle encounter. An active encounter within the timeout remains
// open.
{
    var t = 0.0;
    var ends = 0;
    var eng = new MeterEngine(() => t);
    eng.OnEncounterEnd = _ => ends++;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t = 50.0;
    eng.SetInCombat(true, false);
    Check(ends == 0, "begin within timeout keeps the open pull");
    Check(eng.LiveSnapshot()!.Rows.Count == 1, "kept pull still shows its damage");

    var t2 = 0.0;
    var ends2 = 0;
    var eng2 = new MeterEngine(() => t2);
    eng2.OnEncounterEnd = _ => ends2++;
    eng2.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t2 = 200.0;
    eng2.SetInCombat(true, false);
    Check(ends2 == 1, "stale pull closed on new begin");
    Check(eng2.HasLiveEncounter, "stale close begins the fresh pull");
    Check(eng2.LiveSnapshot()!.Rows.Count == 0, "fresh pull starts empty");
}

// Starting encounters from damage
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.HasLiveEncounter, "lazy begin on a hostile line");
}
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(Ability(Player, "Player One", Player, "Player One", "0004", "01F40000"));
    Check(!eng.HasLiveEncounter, "no lazy begin on a lone heal");
    eng.Process(Tick("HoT", Player, "Player One", "000001F4", Player, "Player One"));
    Check(!eng.HasLiveEncounter, "no lazy begin on a HoT");
    eng.Process(Tick("DoT", Enemy, "Striking Dummy", "00000000", Player, "Player One"));
    Check(!eng.HasLiveEncounter, "no lazy begin on a zero tick");
    eng.Process(Tick("DoT", Enemy, "Striking Dummy", "000001F4", Player, "Player One"));
    Check(eng.HasLiveEncounter, "lazy begin on a DoT tick");
    CheckNear(eng.LiveSnapshot()!.Rows[0].EncDps, 500.0, "DoT tick credits the applier");
}
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0001", ""));
    Check(eng.HasLiveEncounter, "lazy begin on a miss");
}

// A death alone must not start an encounter.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(Death(Player, "Player One"));
    Check(!eng.HasLiveEncounter, "no lazy begin on a death");
}

// HoTs credit the applier's healing. Enemy DoTs credit only the player's damage taken.
{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Tick("HoT", Player, "Player One", "000001F4", Player, "Player One"));
    CheckNear(eng.LiveSnapshot()!.Rows[0].Hps, 500.0, "HoT tick credits the applier");
    eng.Process(Tick("DoT", Player, "Player One", "000003E8", Enemy, "Striking Dummy"));
    var snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1, "enemy DoT: no enemy row");
    CheckNear(snap.Rows[0].EncDps, 0.0, "enemy DoT: applier was no player");
    eng.SetInCombat(false, false);
    Check(ends == 1, "enemy DoT: damagetaken makes the pull non-empty");
}

// The display pauses after inactivity and resets when damage resumes.
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t = 150.0;
    var snap = eng.LiveSnapshot();
    Check(snap!.Duration == "02:00", "idle: duration clamps at the timeout");
    CheckNear(snap.Rows[0].EncDps, 151.8, "idle: rates divide by the clamped span");

    var t2 = 0.0;
    var eng2 = new MeterEngine(() => t2);
    eng2.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng2.SetInCombat(true, true);
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "423F400F"));
    t2 = 200.0;
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t2 = 205.0;
    var snap2 = eng2.LiveSnapshot();
    Check(snap2!.Duration == "00:05", "idle reset: duration measured from the new hit");
    CheckNear(snap2.Rows[0].EncDps, 3643.2, "idle reset: only the new hit shows");
    CheckNear(snap2.EncDps, 3643.2, "idle reset: old damage gone from the view");
}

// Idle timeout is bounded to 15 through 600 seconds. Invalid input is ignored.
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetIdleTimeout(5.0);   // clamps to 15
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "423F400F"));
    t = 20.0;
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    CheckNear(eng.LiveSnapshot()!.Rows[0].EncDps, 18216.0, "timeout clamp low: view reset at 15s");

    var t2 = 0.0;
    var eng2 = new MeterEngine(() => t2);
    eng2.SetIdleTimeout(9999.0);   // clamps to 600
    eng2.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng2.SetInCombat(true, true);
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t2 = 200.0;
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    CheckNear(eng2.LiveSnapshot()!.Rows[0].EncDps, 182.2, "timeout clamp high: no reset at 200s");

    var t3 = 0.0;
    var eng3 = new MeterEngine(() => t3);
    eng3.SetIdleTimeout(double.NaN);   // ignored, stays 120
    eng3.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng3.SetInCombat(true, true);
    eng3.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t3 = 200.0;
    eng3.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    CheckNear(eng3.LiveSnapshot()!.Rows[0].EncDps, 18216.0, "timeout NaN ignored: default still applies");
}

// Row shares, order, local identity and rounding
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(AddCombatant(PlayerTwo, "Player Two", "18", "0"));   // 0x18 = 24 WHM
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    eng.Process(Ability(PlayerTwo, "Player Two", Enemy, "Striking Dummy", "0003", "11CA0000"));
    eng.Process(Ability(PlayerTwo, "Player Two", Player, "Player One", "0004", "01F40000"));
    eng.Process(Death(PlayerTwo, "Player Two"));
    t = 10.0;
    var snap = eng.LiveSnapshot()!;
    Check(snap.Title == "Encounter", "rows: title falls back without a zone");
    Check(snap.Duration == "00:10", "rows: duration mm:ss");
    CheckNear(snap.EncDps, 2277.0, "rows: encounter dps");
    Check(snap.Rows.Count == 2, "rows: both players listed");
    Check(snap.Rows[0].Name == "Player One", "rows: sorted by encdps desc");
    CheckNear(snap.Rows[0].EncDps, 1821.6, "rows: encdps rounded to 1 decimal");
    CheckNear(snap.Rows[1].EncDps, 455.4, "rows: second row encdps");
    CheckNear(snap.Rows[0].Share + snap.Rows[1].Share, 100.0, "rows: shares sum to 100");
    CheckNear(snap.Rows[0].Share, 80.0, "rows: first share");
    CheckNear(snap.Rows[1].Hps, 50.0, "rows: hps");
    Check(snap.Rows[0].IsSelf && !snap.Rows[1].IsSelf, "rows: is-self from SetMe");
    Check(snap.Rows[1].Deaths == 1, "rows: deaths counted");
}

// SetMe rejects invalid IDs without replacing known identity.
{
    var eng = new MeterEngine(() => 0.0);
    eng.SetMe(0);
    eng.SetMe(-5);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(!eng.LiveSnapshot()!.Rows[0].IsSelf, "SetMe: non-positive ids ignored");
    eng.SetMe(0x10000001);
    Check(eng.LiveSnapshot()!.Rows[0].IsSelf, "SetMe: valid id pins");
}

// The 02 line also sets local identity.
{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(new List<string> { "02", "ts", Player, "Player One" });
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows[0].IsSelf, "02 line pins the local player");
}

// Roster jobs and late updates
{
    var eng = new MeterEngine(() => 0.0);
    eng.NoteJob(0x10000001, 31);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.HasLiveEncounter, "NoteJob: roster alone makes a player");
    Check(eng.LiveSnapshot()!.Rows[0].Job == "MCH", "NoteJob: acronym from roster");

    var eng2 = new MeterEngine(() => 0.0);
    eng2.NoteJob(0x10000001, 0);   // job 0 ignored
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(!eng2.HasLiveEncounter, "NoteJob: job 0 ignored");

    // A late roster updates the owner row created by an earlier pet action.
    var eng3 = new MeterEngine(() => 0.0);
    eng3.SetMe(0x10000001);
    eng3.Process(AddCombatant(Player, "Player One", "00", "0"));
    eng3.Process(AddCombatant("40000052", "Eos", "00", Player));
    eng3.SetInCombat(true, true);
    eng3.Process(Ability("40000052", "Eos", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng3.LiveSnapshot()!.Rows[0].Job == "", "NoteJob: pet-opened row starts jobless");
    eng3.NoteJob(0x10000001, 31);
    Check(eng3.LiveSnapshot()!.Rows[0].Job == "MCH", "NoteJob: late upgrade of an open record");

    // Unnamed owners use their uppercase hexadecimal ID.
    var eng4 = new MeterEngine(() => 0.0);
    eng4.NoteJob(0x10000001, 31);
    eng4.Process(AddCombatant("40000053", "Eos", "00", Player));
    eng4.SetInCombat(true, true);
    eng4.Process(Ability("40000053", "Eos", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng4.LiveSnapshot()!.Rows[0].Name == "10000001", "nameless row falls back to hex id");
}

// Limit ordinary rows to a full alliance.
{
    var eng = new MeterEngine(() => 0.0);
    eng.SetInCombat(true, true);
    for (var i = 0; i < 25; i++)
    {
        var id = (0x10000100 + i).ToString("X");
        eng.NoteJob(0x10000100 + i, 31);
        eng.Process(Ability(id, $"Player {i}", Enemy, "Striking Dummy", "0003", "47280000"));
    }

    Check(eng.LiveSnapshot()!.Rows.Count == 24, "rows cap at 24");
}

// Malformed input neither throws nor credits damage.
{
    var eng = new MeterEngine(() => 0.0);
    try
    {
        eng.Process(new List<string>());
        eng.Process(new List<string> { "21" });
        eng.Process(new List<string> { "03", "ts" });
        eng.Process(new List<string> { "24", "ts", Player });
        eng.Process(new List<string> { "25" });
        eng.Process(new List<string> { "33" });
        eng.Process(new List<string> { "99", "ts", "whatever" });
        eng.Process(AddCombatant("ZZ", "Garbage", "GG", "0"));
        eng.Process(Tick("DoT", Enemy, "Striking Dummy", "ZZ", Player, "Player One"));
        Check(true, "malformed: nothing throws");
    }
    catch (Exception ex)
    {
        Check(false, $"malformed: threw {ex.GetType().Name}");
    }

    Check(!eng.HasLiveEncounter, "malformed: nothing opens an encounter");

    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "ZZZZ", "GGGG"));
    var snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1, "malformed: junk pair still records the actor");
    CheckNear(snap.Rows[0].EncDps, 0.0, "malformed: junk hex credits nothing");
}

// Synthetic zone lines can contain only a name, without a timestamp or ID.
{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    Check(!eng.HasZone, "synthetic 01: HasZone starts false");
    eng.Process(new List<string> { "01", "", "", "Limsa Lominsa" });
    Check(eng.HasZone, "synthetic 01: HasZone once fed");
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Title == "Limsa Lominsa", "synthetic 01: title follows the fed zone");
    eng.Process(new List<string> { "01", "", "", "Gridania" });
    Check(!eng.HasLiveEncounter, "synthetic 01: finalizes the open pull");
    Check(ends == 1, "synthetic 01: end fired once");
    eng.SetInCombat(false, false);
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows.Count == 0, "synthetic 01: actor knowledge reset");
    eng.Process(new List<string> { "02", "ts", Player, "Player One" });
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Title == "Gridania", "synthetic 01: re-fed zone titles the next pull");
    var snap = eng.LiveSnapshot()!;
    Check(snap.Rows.Count == 1 && snap.Rows[0].Job == "", "synthetic 01: jobs stay forgotten until re-noted");
}

// Encounter endings carry the final display snapshot.
{
    var t = 0.0;
    var ends = 0;
    OverlaySnapshot? ended = null;
    var eng = new MeterEngine(() => t);
    eng.OnEncounterEnd = snap =>
    {
        ends++;
        ended = snap;
    };
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t = 5.0;
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    eng.Process(Death(Player, "Player One"));
    eng.Process(new List<string> { "33", "ts", Player, "4000000f" });
    Check(ends == 1, "final snapshot: end fired once");
    Check(ended != null, "final snapshot: the callback carries it");
    Check(ended!.Rows.Count == 1, "final snapshot: the row survives finalization");
    Check(ended.Rows[0].Deaths == 1, "final snapshot: the late death counts");
    CheckNear(ended.Rows[0].EncDps, 7286.4, "final snapshot: the late hit counts");
    Check(eng.LiveSnapshot() == null, "final snapshot: encounter still gone");
}

// A pull that opens and ends between two publications still reports its rows.
{
    OverlaySnapshot? ended = null;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = snap => ended = snap;
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    eng.SetInCombat(false, false);
    Check(ended != null && ended.Rows.Count == 1, "short pull: rows ride the end callback");
}

// Unrelated damage leaves the display segment unchanged.
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "423F400F"));

    // NPC on NPC damage before the timeout must not extend the segment.
    t = 100.0;
    eng.Process(Ability("40000020", "Kobold", "40000030", "Kobold", "0003", "47280000"));
    t = 130.0;
    var snap = eng.LiveSnapshot();
    Check(snap!.Duration == "02:00", "unrelated ability: idle pause holds");
    CheckNear(snap.Rows[0].EncDps, 8333.3, "unrelated ability: no activity extension");

    // Unrelated damage after the timeout must not reset the segment.
    eng.Process(Ability("40000020", "Kobold", "40000030", "Kobold", "0003", "47280000"));
    snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1, "unrelated ability: player row survives");
    Check(snap.Duration == "02:00", "unrelated ability: view not reset");
    CheckNear(snap.Rows[0].EncDps, 8333.3, "unrelated ability: old numbers intact");

    // A DoT between unknown actors must not reset the segment.
    eng.Process(Tick("DoT", "40000030", "Kobold", "000003E8", "40000020", "Kobold"));
    snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1 && snap.Duration == "02:00", "unrelated DoT: view untouched");

    // A DoT on the player restarts the display segment.
    eng.Process(Tick("DoT", Player, "Player One", "000003E8", Enemy, "Striking Dummy"));
    t = 135.0;
    snap = eng.LiveSnapshot();
    Check(snap!.Duration == "00:05", "player DoT: segment restarts on relevant damage");
    CheckNear(snap.Rows[0].EncDps, 0.0, "player DoT: new segment starts empty");
}

// Damage to pets credits no player and must not advance display activity.
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.Process(AddCombatant(Player, "Player One", "1C", "0"));
    eng.Process(AddCombatant("40000050", "Eos", "00", Player));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t = 130.0;
    eng.Process(Ability(Enemy, "Striking Dummy", "40000050", "Eos", "0003", "423F400F"));
    var snap = eng.LiveSnapshot();
    Check(snap!.Duration == "02:00", "pet target: segment untouched");
    CheckNear(snap.Rows[0].EncDps, 151.8, "pet target: old numbers intact");
}

// Final duration includes healing but excludes the later combat flag.
foreach (var heal in new[] { false, true })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.NoteJob(0x10000001, 31);
    eng.SetInCombat(true, true);
    t = 10;
    eng.Process(Ability(Player, "Player One", Enemy, "Dummy", "0003", "47280000"));
    if (heal)
    {
        t = 20;
        eng.Process(Ability(Player, "Player One", Player, "Player One", "0004", "01F40000"));
    }

    t = 100;
    Check(eng.LiveSnapshot()!.Duration == "01:40", "live clock keeps the active tail");
    eng.SetInCombat(false, false);
    Check(ended!.Duration == (heal ? "00:20" : "00:10"), "final clock uses last recorded activity");
    CheckNear(ended.EncDps, heal ? 910.8 : 1821.6, "final DPS drops the combat tail");
}

// Nearby NPC combat cannot extend a player's final duration.
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.NoteJob(0x10000001, 31);
    eng.SetInCombat(true, true);
    t = 10;
    eng.Process(Ability(Player, "Player One", Enemy, "Dummy", "0003", "47280000"));
    t = 30;
    eng.Process(Ability(Enemy, "Dummy", "40000020", "Other Dummy", "0003", "47280000"));
    eng.Process(Tick("DoT", "40000020", "Other Dummy", "1F4", Enemy, "Dummy"));
    t = 100;
    eng.SetInCombat(false, false);
    Check(ended!.Duration == "00:10", "unrelated damage cannot extend the final clock");
}

// Finalization still reports the current display segment after an idle reset.
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.NoteJob(0x10000001, 31);
    eng.Process(Ability(Player, "Player One", Enemy, "Dummy", "0003", "47280000"));
    t = 200;
    eng.Process(Ability(Player, "Player One", Enemy, "Dummy", "0003", "47280000"));
    t = 210;
    eng.Process(Ability(Player, "Player One", Player, "Player One", "0004", "01F40000"));
    t = 300;
    eng.FeedLost();
    Check(ended!.Duration == "00:10", "final clock uses the display segment start");
    CheckNear(ended.EncDps, 1821.6, "final rows contain only the current display segment");
}

// Both healing paths leave an idle display unchanged.
foreach (var hot in new[] { false, true })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.NoteJob(0x10000001, 31);
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Dummy", "0003", "47280000"));
    t = 200;
    var before = eng.LiveSnapshot()!;
    eng.Process(hot ? Tick("HoT", Player, "Player One", "1F4", Player, "Player One")
                    : Ability(Player, "Player One", Player, "Player One", "0004", "01F40000"));
    var after = eng.LiveSnapshot()!;
    Check(before.Duration == after.Duration && after.Rows[0].Hps == 0, "late healing leaves the paused segment alone");
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.FeedLost();
    Check(ended!.Duration == "00:00", "paused healing does not extend final duration");
}

// Feed loss resets combat flags and actor identity before subscription replay.
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    var ended = new List<OverlaySnapshot?>();
    eng.OnEncounterEnd = ended.Add;
    eng.SetRoster(new[] { new KeyValuePair<int, int>(0x10000001, 31) });
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Old Player", Enemy, "Dummy", "0003", "47280000"));
    t = 50;
    eng.FeedLost(incomplete: true);
    Check(!eng.HasLiveEncounter && ended.Count == 1, "feed loss finalizes once");
    Check(ended[0]!.Title.Contains("incomplete"), "lost events are marked incomplete");
    eng.FeedLost();
    Check(ended.Count == 1, "repeated loss does not duplicate the end");
    eng.SetInCombat(true, true);
    Check(eng.HasLiveEncounter, "replayed true flags start a new encounter");
    eng.Process(Ability(Player, "Old Player", Enemy, "Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows.Count == 0, "feed loss retires old jobs");
}

// Roster members remain recognized despite other actor cache updates.
{
    var eng = new MeterEngine(() => 0.0);
    eng.SetRoster(new[] { new KeyValuePair<int, int>(0x10000001, 31) });
    for (var i = 0; i < 1500; i++)
    {
        eng.NoteJob(0x10001000 + i, 22);
    }

    eng.Process(Ability(Player, new string('x', 10000), Enemy, "Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows[0].Job == "MCH", "roster job survives churn");
    Check(eng.LiveSnapshot()!.Rows[0].Name.Length <= 256, "names are bounded in combatant storage");
    eng.SetRoster(Array.Empty<KeyValuePair<int, int>>());
    for (var i = 0; i < 1500; i++)
    {
        eng.NoteJob(0x10002000 + i, 22);
    }

    eng.Process(Ability(Player, "Player One", Enemy, "Dummy", "0003", "47280000"));
    CheckNear(eng.LiveSnapshot()!.Rows[0].EncDps, 36432, "active combatant stays known after roster removal");
    eng.FeedLost();
    eng.Process(Ability(Player, "Old Player", Enemy, "Dummy", "0003", "47280000"));
    Check(!eng.HasLiveEncounter, "retired roster cannot open the next session");
}

Console.WriteLine($"{passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;
