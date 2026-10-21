using System.Text.Json;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 9 <c>port-synth-9-events2</c> (<c>tools/synth-maps/maps_events2.py</c>): оставшиеся условия и эффекты событий и обрыв
/// пути человека событием на клетке перед целью — по памятке в модели порта.</summary>
public partial class PortSynthMapsTests
{
    private const string Events2Map = "port-synth-9-events2";

    /// <summary>Ворота и условия карты: каждый эффект (кроме победы — она последней) и каждое условие срабатывает, состояние после — как
    /// обещает памятка (смена хозяина, включённое и выключенное событие, горы, усиление триггерера, призыв, развеянная карта, предмет в
    /// столице, перенос ходока, туман, земля, переменная 12, гибель иллюзии без боя, окна «TRI» и «ALL», отряды по шаблонам).</summary>
    [SkippableFact]
    public void Events2_EveryGateAndCondition_Fires()
    {
        Skip.IfNot(MapIds().Contains(Events2Map));
        var facts = Facts(Events2Map);
        var session = Begin(Events2Map, 3);
        session.EventBattlesOnScreen = false;
        var seen = new Recorder();
        session.Watch(seen);
        string human = session.ActiveHuman;
        var zones = facts.GetProperty("zones").EnumerateArray().ToDictionary(z => z.GetProperty("key").GetString()!);
        // Сработавшее — в записи наблюдателя; условия дипломатии верны с начала и срабатывают ещё в NewGame.Begin, до подписки, —
        // у них след — само одноразовое событие, выключенное в начале партии (больше их ничто не выключает).
        var atStart = session.State.EventProgress.Disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Fired(string key) => seen.Events.Any(e => Same(e.Event.Id, Str(zones[key], "event")))
                                  || (key is "C8w" or "C8p" && atStart.Contains(Str(zones[key], "event")));

        foreach (var (key, z) in zones.Where(p => p.Value.TryGetProperty("gate_at", out _) && p.Key != "E00"))
        {
            var gate = z.GetProperty("gate_at");
            session.MoveStack(Str(z, "stack"), gate[0].GetInt32(), gate[1].GetInt32(), 0, 0);
        }
        string walker = session.Map.Stacks[Str(zones["E02b"], "stack")].LeaderId, strength = session.Book!.Spells["G000SS0003"].ModifierId;
        bool boosted = session.Map.SpellEffects.Any(e => Same(e.UnitId, walker) && Same(e.ModifierId, strength));   // до конца дня
        Out.WriteLine("эффекты заклинаний: " + string.Join(", ", session.Map.SpellEffects.Select(e => $"{e.UnitId} {e.ModifierId} до {e.TurnStop}")));
        // призванный уходит в начале следующего хода хозяина (LIFE_TIME 1) — смотреть до конца дня
        var summonAt = zones["E03b"].GetProperty("summon_at");
        bool summoned = session.Map.Stacks.Values.Any(s => Same(s.Owner, human) && Math.Abs(s.X - summonAt[0].GetInt32()) <= 3
                                                          && Math.Abs(s.Y - summonAt[1].GetInt32()) <= 3
                                                          && session.Book.CategoryOf(session.Map.Units[s.LeaderId].TypeId) == D2.Rules.Battle.UnitCategory.Summon);
        foreach (string key in new[] { "C3", "C4", "C12", "C15" })
        {
            var visit = session.Enter(Str(zones[key], "stack"), Str(zones[key], "target"));
            Out.WriteLine($"{key}: {(visit.Refused ?? "вошёл")}");
        }
        var ruinFight = session.StartBattle(Str(zones["C10"], "stack"), Str(zones["C10"], "target"));
        var fight = new D2.Data.Battle.BattleSession(ruinFight, new D2.Rules.Battle.BattleSim(ruinFight, session.Rng, session.Book!.Rules.Constants),
                                                     session.Book!.Rules.Constants) { Auto = true };
        fight.Resolve();
        session.FinishBattle(fight.Log);
        // созданный по шаблону нейтрал — у ворот E01t; бьёт его ходок тех ворот (L_KILL_STACK по шаблону)
        string tn = zones["E01t"].GetProperty("templates")[1].GetString()!;
        var created = session.Map.Stacks.Values.Single(s => Same(s.TemplateId, tn));
        var kill = session.StartBattle(Str(zones["E01t"], "stack"), created.Id);
        var killFight = new D2.Data.Battle.BattleSession(kill, new D2.Rules.Battle.BattleSim(kill, session.Rng, session.Book!.Rules.Constants),
                                                         session.Book!.Rules.Constants) { Auto = true };
        killFight.Resolve();
        session.FinishBattle(killFight.Log);
        session.EndTurn();   // день 2: частота 2, включённый счётчик, доля земли после E22

        var map = session.Map;
        var notFired = zones.Where(p => p.Value.TryGetProperty("event", out _) && p.Key is not ("E00" or "C0f") && !p.Key.StartsWith("CUT", StringComparison.Ordinal)
                                        && !Fired(p.Key)).Select(p => p.Key).ToList();
        Out.WriteLine("не сработали: " + string.Join(", ", notFired));
        Assert.Empty(notFired);
        var vars = facts.GetProperty("variables");
        int Var(string name) => session.State.EventProgress.Variables.GetValueOrDefault(vars.GetProperty(name).GetInt32());
        Out.WriteLine($"PS_OPS {Var("PS_OPS")}, PS_FREQ {Var("PS_FREQ")}, PS_ON {Var("PS_ON")}, PS_OFF {Var("PS_OFF")}");
        Assert.Equal(12, Var("PS_OPS"));
        Assert.Equal(1, Var("PS_FREQ"));
        Assert.True(Var("PS_ON") >= 1);
        Assert.Equal(1, Var("PS_OFF"));
        Assert.Equal(human, map.Stacks[Str(zones["E04a"], "target")].Owner, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(human, map.Stacks[Str(zones["E04b"], "target")].Owner, StringComparer.OrdinalIgnoreCase);
        var zone = zones["E14"].GetProperty("zone_at");
        int cx = zone[0].GetInt32(), cy = zone[1].GetInt32();
        Assert.All(from dx in Enumerable.Range(-1, 3) from dy in Enumerable.Range(-1, 3) select (cx + dx, cy + dy),
                   c => Assert.NotEqual(Ground.Mountain, map.Tiles[c.Item1, c.Item2].Ground));
        Assert.Equal(Ground.Mountain, map.Tiles[28, 29].Ground);   // гора вне зоны осталась
        Assert.True(boosted, "«Сила» на ходока E02b — модификатора со сроком нет");
        Assert.True(summoned, "призыва E03b у места нет");
        var capital = map.Sites.Single(s => s.Kind == SiteKind.Capital && Same(s.Owner, human));
        Assert.Contains(capital.Items, i => Same(map.ItemTypes[i], "G000IG7003"));
        var moveTo = zones["E10b"].GetProperty("move_to");
        Assert.Equal((moveTo[0].GetInt32(), moveTo[1].GetInt32()), (map.Stacks[Str(zones["E10b"], "stack")].X, map.Stacks[Str(zones["E10b"], "stack")].Y));
        Assert.False(map.Stacks.ContainsKey(Str(zones["E06i"], "stack")));   // иллюзия во главе — гибель без боя
        Assert.True(map.Stacks.ContainsKey(Str(zones["E06i"], "target")));
        var all = session.State.EventProgress.Log.Where(l => Same(l.EventId, Str(zones["E17a"], "event"))).Select(l => l.PlayerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tri = session.State.EventProgress.Log.Where(l => Same(l.EventId, Str(zones["E17t"], "event"))).Select(l => l.PlayerId).ToList();
        Out.WriteLine($"окно ALL: {string.Join(", ", all)}; окно TRI: {string.Join(", ", tri)}");
        Assert.Equal(4, all.Count);
        Assert.Equal([human], tri);
        string tt = zones["E01t"].GetProperty("templates")[0].GetString()!;
        Assert.Contains(map.Stacks.Values, s => Same(s.TemplateId, tt) && Same(s.Owner, human));
        // земля: квадрат E22 — Кланов, E22b — Легионов
        Assert.Equal(Economy.TerrainOfRace(3), map.Tiles[60, 20].Terrain);
        Assert.Equal(Economy.TerrainOfRace(2), map.Tiles[60, 40].Terrain);
        // развеяно всё — затем туман E13b закрыл свою область (обзор своих отрядов — снова открыт)
        var fog = map.Fog[map.Players[human].FogId];
        var fogAt = zones["E13b"].GetProperty("fog_at");
        Assert.False(fog[100, 100]);
        Assert.True(fog[fogAt[0].GetInt32() + 1, fogAt[1].GetInt32() + 1]);
    }

    /// <summary>Обрыв пути человека событием на клетке перед целью (<c>0x440284</c> → 1, <c>PlayerMove.Cut</c>): отряд встаёт на клетку
    /// вплотную к цели — ни боя, ни входа, ни подбора; повторный ход (событие одноразовое) — действие.</summary>
    [SkippableFact]
    public void Events2_PathCutOnTheCellBeforeTheTarget()
    {
        Skip.IfNot(MapIds().Contains(Events2Map));
        var facts = Facts(Events2Map);
        var session = Begin(Events2Map, 3);
        foreach (string key in facts.GetProperty("cuts").EnumerateArray().Select(c => c.GetString()!))
        {
            var z = Zone(facts, key);
            string stack = Str(z, "stack"), target = Str(z, "target");
            // цель — отряд (его клетка), мешок, крепость или площадка (вход — правый нижний угол пятна плана)
            var aim = session.Map.Stacks.TryGetValue(target, out var t) ? (t.X, t.Y) : PathFinder.EntranceOf(session.Map, target)!.Value;
            var path = session.PathTo(session.Map.Stacks[stack], aim.Item1, aim.Item2);
            var move = PlayerMove.Plan(session, stack, path);
            Assert.True(move.Cut, $"{key}: путь не оборван");
            Assert.Contains(move.Fired, f => Same(f.Event.Id, Str(z, "event")));
            var end = move.Today[^1];
            Assert.Equal(1, Math.Max(Math.Abs(end.X - aim.Item1), Math.Abs(end.Y - aim.Item2)));
            session.MoveStack(stack, end.X, end.Y, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
            Assert.True(session.Map.Stacks.ContainsKey(target) || session.Map.Sites.Any(s => Same(s.Id, target) && !s.Gone));
            var again = PlayerMove.Plan(session, stack, session.PathTo(session.Map.Stacks[stack], aim.Item1, aim.Item2));
            Assert.False(again.Cut, $"{key}: второй ход снова оборван");
            Out.WriteLine($"{key}: оборван на ({end.X},{end.Y}), цель ({aim.Item1},{aim.Item2})");
        }
    }

    /// <summary>Последние ворота — <c>L_WIN</c>: партия кончается победой Кланов.</summary>
    [SkippableFact]
    public void Events2_LastGate_WinsTheScenario()
    {
        Skip.IfNot(MapIds().Contains(Events2Map));
        var facts = Facts(Events2Map);
        var session = Begin(Events2Map, 3);
        var win = Zone(facts, "E00");
        var gate = win.GetProperty("gate_at");
        session.MoveStack(Str(win, "stack"), gate[0].GetInt32(), gate[1].GetInt32(), 0, 0);
        Assert.True(session.Over);
        Assert.True(session.Won(session.ActiveHuman));
    }
}
