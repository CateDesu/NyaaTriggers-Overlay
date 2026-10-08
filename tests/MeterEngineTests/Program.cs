using System;
using System.Collections.Generic;
using System.Linq;
using NyaaTriggers.Plugin.Meter;

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

{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "22", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    var row = eng.LiveSnapshot()!.Rows[0];
    Check(row.Name == "Player One", "03: name noted");
    Check(row.Job == "SAM", "03: hex job maps to acronym");
}

{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Enemy, "Striking Dummy", "22", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Enemy, "Striking Dummy", "40000011", "Other Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows.Count == 0, "03: non-10 id never becomes a row");
}

{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant("10ABCD01", "Hex Case", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability("10abcd01", "Hex Case", Enemy, "Striking Dummy", "0003", "47280000"));
    var row = eng.LiveSnapshot()!.Rows[0];
    Check(row.Job == "MCH", "lowercase id resolves the same actor");
    CheckNear(row.EncDps, 18216.0, "lowercase id credits damage");
}

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

{
    var eng = new MeterEngine(() => 0.0);
    eng.SetMe(0x10000001);
    eng.Process(AddCombatant(Player, "Player One", "1C", "0")); // SCH
    eng.Process(AddCombatant("40000050", "Eos", "00", Player));
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

{
    var ends = 0;
    var eng = new MeterEngine(() => 0.0);
    eng.OnEncounterEnd = _ => ends++;
    eng.SetInCombat(true, true);
    eng.SetInCombat(false, false);
    Check(ends == 1, "empty pull sends an end marker");
}

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
    eng.Process(new List<string> { "02", "ts", Player, "Player One" });
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    var snap = eng.LiveSnapshot();
    Check(snap!.Title == "Limsa Lominsa", "zone: title follows the zone");
    Check(snap.Rows[0].Job == "", "zone: jobs forgotten, acronym falls back");
    Check(snap.Rows[0].IsSelf, "zone: 02 re-pins the local player");
}

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

{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(Death(Player, "Player One"));
    Check(!eng.HasLiveEncounter, "no lazy begin on a death");
}

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

{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetIdleTimeout(5.0);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "423F400F"));
    t = 20.0;
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    CheckNear(eng.LiveSnapshot()!.Rows[0].EncDps, 18216.0, "timeout clamp low: view reset at 15s");

    var t2 = 0.0;
    var eng2 = new MeterEngine(() => t2);
    eng2.SetIdleTimeout(9999.0);
    eng2.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng2.SetInCombat(true, true);
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t2 = 200.0;
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    CheckNear(eng2.LiveSnapshot()!.Rows[0].EncDps, 182.2, "timeout clamp high: no reset at 200s");

    var t3 = 0.0;
    var eng3 = new MeterEngine(() => t3);
    eng3.SetIdleTimeout(double.NaN);
    eng3.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng3.SetInCombat(true, true);
    eng3.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    t3 = 200.0;
    eng3.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    CheckNear(eng3.LiveSnapshot()!.Rows[0].EncDps, 18216.0, "timeout NaN ignored: default still applies");
}

{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.Process(AddCombatant(PlayerTwo, "Player Two", "18", "0")); // WHM
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

{
    var eng = new MeterEngine(() => 0.0);
    eng.Process(new List<string> { "02", "ts", Player, "Player One" });
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.LiveSnapshot()!.Rows[0].IsSelf, "02 line pins the local player");
}

{
    var eng = new MeterEngine(() => 0.0);
    eng.NoteJob(0x10000001, 31);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng.HasLiveEncounter, "NoteJob: roster alone makes a player");
    Check(eng.LiveSnapshot()!.Rows[0].Job == "MCH", "NoteJob: acronym from roster");

    var eng2 = new MeterEngine(() => 0.0);
    eng2.NoteJob(0x10000001, 0);
    eng2.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(!eng2.HasLiveEncounter, "NoteJob: job 0 ignored");

    var eng3 = new MeterEngine(() => 0.0);
    eng3.SetMe(0x10000001);
    eng3.Process(AddCombatant(Player, "Player One", "00", "0"));
    eng3.Process(AddCombatant("40000052", "Eos", "00", Player));
    eng3.SetInCombat(true, true);
    eng3.Process(Ability("40000052", "Eos", Enemy, "Striking Dummy", "0003", "47280000"));
    Check(eng3.LiveSnapshot()!.Rows[0].Job == "", "NoteJob: pet-opened row starts jobless");
    eng3.NoteJob(0x10000001, 31);
    Check(eng3.LiveSnapshot()!.Rows[0].Job == "MCH", "NoteJob: late upgrade of an open record");

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

// Synthetic zone lines omit timestamp and ID.
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

{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.Process(AddCombatant(Player, "Player One", "1F", "0"));
    eng.SetInCombat(true, true);
    eng.Process(Ability(Player, "Player One", Enemy, "Striking Dummy", "0003", "423F400F"));

    t = 100.0;
    eng.Process(Ability("40000020", "Kobold", "40000030", "Kobold", "0003", "47280000"));
    t = 130.0;
    var snap = eng.LiveSnapshot();
    Check(snap!.Duration == "02:00", "unrelated ability: idle pause holds");
    CheckNear(snap.Rows[0].EncDps, 8333.3, "unrelated ability: no activity extension");

    eng.Process(Ability("40000020", "Kobold", "40000030", "Kobold", "0003", "47280000"));
    snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1, "unrelated ability: player row survives");
    Check(snap.Duration == "02:00", "unrelated ability: view not reset");
    CheckNear(snap.Rows[0].EncDps, 8333.3, "unrelated ability: old numbers intact");

    eng.Process(Tick("DoT", "40000030", "Kobold", "000003E8", "40000020", "Kobold"));
    snap = eng.LiveSnapshot();
    Check(snap!.Rows.Count == 1 && snap.Duration == "02:00", "unrelated DoT: view untouched");

    eng.Process(Tick("DoT", Player, "Player One", "000003E8", Enemy, "Striking Dummy"));
    t = 135.0;
    snap = eng.LiveSnapshot();
    Check(snap!.Duration == "00:05", "player DoT: segment restarts on relevant damage");
    CheckNear(snap.Rows[0].EncDps, 0.0, "player DoT: new segment starts empty");
}

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

{
    var eng = new MeterEngine(() => 0.0);
    eng.SetMe(0x10827569);
    eng.NoteJob(0x10827569, 30);
    eng.Process(Ability("10827569", "Player", Enemy, "Hegemone",
        "44714003", "38FD0000", "104", "AA68000"));
    var stats = eng.LiveSnapshot()!.Rows[0].Stats!;
    CheckNear(stats.Healed!.Value, 2726, "Bloodbath credits outgoing healing");
    CheckNear(stats.HealingTaken!.Value, 2726, "Bloodbath credits the caster's incoming healing");
}

foreach (var source in new[] { Player, "40000050", Enemy })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    eng.NoteJob(0x10000001, 28);
    eng.Process(AddCombatant("40000050", "Pet", "00", Player));
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", "0003", "00640000"));
    t = 10;
    eng.Process(Ability(source, "Source", source == Player ? Enemy : Player, "Target", "0104", "01F48000"));
    var stats = eng.LiveSnapshot()!.Rows[0].Stats!;
    CheckNear(stats.Healed!.Value, source == Enemy ? 0 : 500, $"{source} source healing keeps owner credit");
    CheckNear(stats.HealingTaken!.Value, source == Player ? 500 : 0, $"{source} source healing uses the actual recipient");
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.FeedLost();
    Check(ended!.Duration == (source == Enemy ? "00:00" : "00:10"),
        $"{source} source healing extends duration only for player contributions");
}

foreach (var (word, amount) in new[]
{
    ("00000FA0", 4000.0), ("00003D74", 15732.0), ("00008000", 32768.0), ("00009000", 36864.0), ("0000FFFF", 65535.0),
})
{
    var eng = new MeterEngine(() => 0.0);
    eng.SetMe(0x10000001);
    eng.NoteJob(0x10000002, 24);
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", "0003", "00640000"));
    eng.Process(Ability(Player, "Player", PlayerTwo, "Patient", "0004", word));
    var rows = eng.LiveSnapshot()!.Rows;
    CheckNear(rows.Single(row => row.IsSelf).Stats!.Healed!.Value, amount,
        $"Literal healing {word} keeps its full amount");
    CheckNear(rows.Single(row => row.IsSelf).Stats!.HealingTaken!.Value, 0,
        $"Literal healing {word} does not interpret amount bits as recipient flags");
    CheckNear(rows.FirstOrDefault(row => row.Name == "Patient").Stats?.HealingTaken ?? 0, amount,
        $"Literal healing {word} reaches the line target");
}

foreach (var combatFlag in new[] { false, true })
foreach (var delay in new[] { 10.0, 200.0 })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    if (combatFlag) eng.SetInCombat(true, true);
    else eng.Process(Ability(Player, "Player", Enemy, "Enemy", "0001", "0"));
    var originalId = eng.LiveSnapshot()!.Id;
    t = delay;
    var before = eng.LiveSnapshot()!;
    Check(before.Duration == (delay > 120 ? "02:00" : "00:10") && !before.HasDamage,
        "A segment without positive damage uses its start for the idle cap");
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", "0003", "00640000"));
    var after = eng.LiveSnapshot()!;
    Check(after.Duration == (delay > 120 ? "00:00" : "00:10"),
        "First damage restarts an idle segment and preserves an active segment");
    CheckNear(after.EncDps, delay > 120 ? 100 : 10,
        "First damage uses the correct display segment duration");
    Check((after.Id != originalId) == (delay > 120),
        "An idle segment gets a new history identity on first damage");
}

foreach (var periodic in new[] { false, true })
foreach (var recipient in new[] { Player, "40000050" })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    eng.Process(AddCombatant("40000050", "Pet", "00", Player));
    eng.Process(Ability(Player, "Player", Enemy, "NPC", "0003", "00640000"));
    t = 10;
    eng.Process(periodic ? Tick("HoT", recipient, "Target", "01F4", Enemy, "NPC")
        : Ability(Enemy, "NPC", recipient, "Target", "0004", "01F40000"));
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.FeedLost();
    Check(ended!.Rows.Count == 1, "Incoming NPC healing does not create an NPC or pet row");
    CheckNear(ended.Rows[0].Stats!.HealingTaken!.Value, recipient == Player ? 500 : 0,
        "Incoming NPC healing excludes pet recipients");
    Check(ended.Duration == (recipient == Player ? "00:10" : "00:00"),
        "Direct and periodic incoming healing share the final activity clock");
    CheckNear(ended.EncDps, recipient == Player ? 10 : 100,
        "Incoming healing preserves a consistent final DPS denominator");
}

foreach (var zoneChange in new[] { false, true })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    eng.Process(AddCombatant(Player, "Known Owner", "1C", "0"));
    eng.Process(AddCombatant("40000050", "Eos", "00", Player));
    eng.Process(Ability("40000050", "Eos", Enemy, "Enemy", "0003", "00640000"));
    Check(eng.LiveSnapshot()!.Rows[0].Name == "Known Owner", "Pet actions start with the known owner's name");
    for (var i = 0; i < 1100; i++)
    {
        eng.Process(AddCombatant((0x40001000 + i).ToString("X"), $"NPC {i}", "00", "0"));
    }

    t = 200;
    eng.Process(Ability("40000050", "Eos", Enemy, "Enemy", "0003", "00640000"));
    var row = eng.LiveSnapshot()!.Rows[0];
    Check(row.Name == "Known Owner" && row.Job == "SCH" && row.IsSelf,
        "An idle display reset preserves known owner metadata after name cache eviction");
    CheckNear(row.EncDps, 100, "Metadata fallback does not restore earlier segment damage");
    if (zoneChange) eng.Process(new[] { "01", "ts", "1", "New Zone" });
    else eng.FeedLost();
    eng.SetMe(0x10000001);
    eng.Process(AddCombatant("40000050", "Eos", "00", Player));
    eng.Process(Ability("40000050", "Eos", Enemy, "Enemy", "0003", "00640000"));
    row = eng.LiveSnapshot()!.Rows[0];
    Check(row.Name == Player && row.Job == string.Empty,
        "A feed or zone reset retires prior owner metadata");
}

foreach (var flags in new[] { "0003", "0004" })
foreach (var amount in new[] { "not hex", "100000000", "FFFFFFFFFFFFFFFF", "" })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", flags, amount));
    Check(!eng.HasLiveEncounter, $"Malformed amount {amount} cannot start an encounter");
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", "0003", "00640000"));
    t = 10;
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", flags, amount));
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.FeedLost();
    Check(ended!.Duration == "00:00", $"Malformed amount {amount} cannot extend final activity");
    CheckNear(ended.EncDps, 100, $"Malformed amount {amount} preserves valid final DPS");
}

foreach (var (flags, amount) in new[] { ("0003", "00000000"), ("0004", "00000000"), ("0001", "") })
{
    var t = 0.0;
    var eng = new MeterEngine(() => t);
    eng.SetMe(0x10000001);
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", "0003", "00640000"));
    t = 10;
    eng.Process(Ability(Player, "Player", Enemy, "Enemy", flags, amount));
    OverlaySnapshot? ended = null;
    eng.OnEncounterEnd = snap => ended = snap;
    eng.FeedLost();
    Check(ended!.Duration == "00:10", "Valid zero effects and misses still record encounter activity");
}

{
    var eng = new MeterEngine(() => 0.0);
    eng.SetMe(0x10000001);
    eng.Process(Ability(Player, "Player", Enemy, "Enemy",
        "0003", "00640000", "0003", "not hex", "0003", "00C80000"));
    var stats = eng.LiveSnapshot()!.Rows[0].Stats!;
    CheckNear(stats.Damage!.Value, 300, "Malformed amounts leave neighboring effect pairs intact");
    CheckNear(stats.Hits!.Value, 2, "Malformed amounts cannot create an extra damaging hit");
}

Console.WriteLine($"{passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;
