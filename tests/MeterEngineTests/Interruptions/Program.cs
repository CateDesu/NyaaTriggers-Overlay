using System.Reflection;
using NyaaTriggers.Plugin;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;

const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
object Field(object target, string name) => target.GetType().GetField(name, Hidden)!.GetValue(target)!;
void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
void Call(object target, string name, params object?[] values)
    => target.GetType().GetMethod(name, Hidden)!.Invoke(target, values);
IinactClient Attach(StandaloneMeter meter)
{
    Call(meter, "ResetEngine");
    var source = new IinactClient(new Uri("ws://127.0.0.1:1"), _ => { });
    Set(meter, "client", source);
    return source;
}
void Receive(StandaloneMeter meter, IinactClient source, string? raw)
    => Call(meter, "Receive", source, raw);
const string Party = """{"type":"PartyChanged","party":[{"id":"10000001","job":31}]}""";
const string Combat = """{"type":"InCombat","inACTCombat":true,"inGameCombat":true}""";
const string Damage = "21|ts|10000001|Player|07|Attack|40000010|Enemy|0003|00640000||||||||||||||";
const string Heal = "21|ts|10000001|Player|07|Heal|10000001|Player|0004|00640000||||||||||||||";
const string End = """{"type":"InCombat","inACTCombat":false,"inGameCombat":false}""";

var config = new Configuration { StandaloneMeter = true };
config.GetMeter(DpsMeterStyle.LMeter).DpsHoldLast = true;
config.GetMeter(DpsMeterStyle.HorizonOverlay).DpsHoldLast = false;
using (var host = new BridgeHost(config))
{
    var meter = (StandaloneMeter)Field(host, "standalone");
    var source = Attach(meter);
    foreach (var raw in new[] { Party, Combat, Damage }) Receive(meter, source, raw);
    host.Update();
    Check(host.Dps.Show && host.Dps.Rows.Single().Dps == 100, "live feed reaches bridge");
    var liveId = host.Dps.Id;
    Receive(meter, source, null);
    foreach (var raw in new[] { Party, Combat, Heal, End }) Receive(meter, source, raw);
    host.Update();
    Check(host.Dps.Ended && host.Dps.Id == liveId && host.Dps.HasDamage, "disconnect retains damaged result across healing replay");
    Check(host.UnheldDps.Ended && !host.UnheldDps.HasDamage && host.UnheldDps.Rows.Single().Hps == 100,
        "nonretaining meter receives healing ending after replay");
    Check(host.DpsHistory.Count == 1, "healing replay cannot duplicate history");
    foreach (var raw in new[] { Combat, Damage }) Receive(meter, source, raw);
    host.Update();
    Check(host.Dps.Show && host.Dps.Id != liveId && host.Dps.Rows.Single().Dps == 100,
        "new damage replaces held results without merging disconnected damage");
    for (var i = 0; i < 520; i++) Receive(meter, source, Damage);
    host.Update();
    Check(host.Dps.Ended && host.Dps.Title.Contains("incomplete"), "overload reaches bridge as an incomplete final");
    Check(host.DpsHistory.Count == 2 && host.Dps.Rows.Single().Dps == 100,
        "overload discards queued damage and records the prior valid total once");
    var final = host.Dps;
    Receive(meter, source, Damage);
    Receive(meter, source, null);
    host.Update();
    Check(ReferenceEquals(host.Dps, final) && host.DpsHistory.Count == 2,
        "retired source callbacks cannot overwrite an overload final");
    var replacement = Attach(meter);
    Parallel.For(0, 1024, i => Receive(meter, source, i % 2 == 0 ? Damage : null));
    var budget = MessageInbox.FrameBytes;
    Check(!((MessageInbox)Field(meter, "inbox")).TryDequeue(ref budget, true, out _, out _),
        "concurrent retired callbacks cannot queue data or disconnect a replacement");
    foreach (var raw in new[] { Party, Combat, Damage }) Receive(meter, replacement, raw);
    host.Update();
    Check(host.Dps.Show && host.Dps.Rows.Single().Dps == 100, "replacement recovers after overload");
    Receive(meter, replacement, null);
    Receive(meter, replacement, null);
    host.Update();
    Check(host.Dps.Ended && host.DpsHistory.Count == 3, "duplicate loss markers finalize only once");
}

{
    var paused = false;
    var states = new List<DpsState>();
    var clears = 0;
    using var meter = new StandaloneMeter(config, () => paused, states.Add, () => clears++);
    var source = Attach(meter);
    foreach (var raw in new[] { Party, Combat, Damage }) Receive(meter, source, raw);
    meter.Update();
    Receive(meter, source, null);
    paused = true;
    meter.Update();
    Check(clears == 1 && states.Count == 1, "program takeover discards queued feed ending and clears local display");
    Receive(meter, source, Damage);
    paused = false;
    var replacement = Attach(meter);
    foreach (var raw in new[] { Party, Combat, Damage }) Receive(meter, replacement, raw);
    meter.Update();
    Check(states.Count == 2 && states.Last().Show && states.Last().Rows.Single().Dps == 100,
        "standalone resume starts fresh after program takeover");
}
Console.WriteLine($"{checks} interruption checks passed");
