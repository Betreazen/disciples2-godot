using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 18 <c>port-synth-18-ai-rows</c> (<c>tools/synth-maps/maps_airows.py</c>): полигоны ИИ складываются в модели порта — страж
/// воды на сушу не бросается, страж суши бросается (<c>0x45753f</c>), воры Кланов, иллюзии Нежити и сценарные приказы Легионов выбирают
/// тактики в первые дни. Что выберет движок (кража заклинания у города с одним призывом, захват иллюзией, путь защитника через мешок, вход
/// в деревню места) — печать: ответ — журнал оригинала.</summary>
public partial class PortSynthMapsTests
{
    private const string AiRowsMap = "port-synth-18-ai-rows";

    [SkippableFact]
    public void AiRows_GuardsWaterAndLand_AndPicksOfThePolygons()
    {
        Skip.IfNot(MapIds().Contains(AiRowsMap));
        var facts = Facts(AiRowsMap);
        var session = Begin(AiRowsMap, 1);
        var nb = Zone(facts, "NB");
        string village = Str(nb, "target");
        foreach (var t in nb.GetProperty("thieves").EnumerateArray())
            Out.WriteLine($"NB: действия вора {t.GetString()} у деревни Нежити — {string.Join(", ", session.ThiefActions(t.GetString()!, village))}");
        var trace = new TacticTrace();
        session.AiTrace = trace;
        var seen = new Recorder();
        session.Watch(seen);
        session.EndTurn();
        session.EndTurn();
        Assert.Equal(3, session.Map.Turn);
        foreach (var o in seen.Outcomes) Out.WriteLine($"бой: {o.Attacker} → {o.Defender}, победил {o.Winner}");
        Assert.DoesNotContain(seen.Outcomes, o => Same(o.Defender, Str(Zone(facts, "GW"), "target")));
        Assert.Contains(seen.Outcomes, o => Same(o.Defender, Str(Zone(facts, "GL"), "target")) && Same(o.Attacker, Str(Zone(facts, "GL"), "stack")));
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Str(Zone(facts, "IL"), "stack"), Str(Zone(facts, "IL2"), "stack"), Str(Zone(facts, "DP"), "stack"), Str(Zone(facts, "ML"), "stack"),
        };
        foreach (var t in nb.GetProperty("thieves").EnumerateArray()) watched.Add(t.GetString()!);
        foreach (var p in trace.Picks.Where(p => watched.Contains(p.Stack)))
            Out.WriteLine($"день {p.Day} {p.Player} {p.Stack}: {p.Action} → {p.Target} ({p.X},{p.Y}) {p.Score}");
        foreach (string key in new[] { "IL", "ML" })
        {
            var z = Zone(facts, key);
            string site = key == "IL" ? Str(z, "target") : Str(z, "village");
            var fort = session.Map.Sites.Single(x => Same(x.Id, site));
            Out.WriteLine($"{key}: деревня {site} — хозяин {fort.Owner}, гость {(string.IsNullOrEmpty(fort.StackId) ? "нет" : fort.StackId)}");
        }
        Assert.Contains(trace.Picks, p => Same(p.Stack, Str(Zone(facts, "DP"), "stack")));
        Assert.Contains(trace.Picks, p => Same(p.Stack, Str(Zone(facts, "ML"), "stack")));
    }
}
