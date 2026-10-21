using System.Text.Json;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 12 <c>port-synth-12-events3</c> (<c>tools/synth-maps/maps_events3.py</c>): включение и выключение событий, «убит отряд» по
/// шаблону, ворота с последствиями на день 2 и обрыв пути со вторым щелчком — по памятке в модели порта; карта 9 — сверка эффектов ворот с
/// сохранением оригинала.</summary>
public partial class PortSynthMapsTests
{
    private const string Events3Map = "port-synth-12-events3";

    /// <summary>Сохранение оригинала после ворот карты 9 (попытка 1, до победы E00): горы E14, земля E22/E22b, ходок E10b, иллюзия E06i,
    /// туман E13b (<c>corpus/20261016g-port-synth</c>, разбор <c>analysis/20261017.md</c>).</summary>
    private const string Events2Run = "20261016g-port-synth", Events2Gates = "port-synth-9-events2-dwarf-t01-022.sg";

    /// <summary>Памятка карты 12 в порте: E07 включает ворота, событие у стоящего отряда и счётчик и выключает ворота и счётчик; выключенные
    /// ворота молчат; созданный по шаблону Гоблин убит — <c>L_KILL_STACK</c> по шаблону; ворота дня 2 срабатывают; к дню 3 включённый счётчик работал. Когда именно включённое срабатывает — печать (ответ движка).</summary>
    [SkippableFact]
    public void Events3_EnableKillAndDayTwo()
    {
        Skip.IfNot(MapIds().Contains(Events3Map));
        var facts = Facts(Events3Map);
        var session = Begin(Events3Map, 3);
        session.EventBattlesOnScreen = false;
        var seen = new EventDays(() => session.Map.Turn);
        session.Watch(seen);
        var zones = facts.GetProperty("zones").EnumerateArray().ToDictionary(z => Str(z, "key"));
        string Event(string key) => Str(zones[key], "event");
        string When(string key)
        {
            var days = seen.Fired.Where(f => Same(f.Id, Event(key))).Select(f => $"день {f.Day}").ToList();
            return days.Count == 0 ? "нет" : string.Join(", ", days);
        }
        bool Fired(string key) => seen.Fired.Any(f => Same(f.Id, Event(key)));
        void Step(string key)
        {
            var (gx, gy) = XY(zones[key], "gate_at");
            session.MoveStack(Str(zones[key], "stack"), gx, gy, 0, 0);
        }

        Step("E07");
        Assert.True(Fired("E07"));
        Out.WriteLine($"после E07: событие у стоящего отряда — {When("E07-pre")}");
        Step("E07-on");
        Step("E07-off");
        Out.WriteLine($"включённые ворота — {When("E07-on")}");
        Assert.False(Fired("E07-off"), "выключенные ворота сработали");

        Step("E01n");
        string tn = Str(zones["E01n"], "template");
        var made = session.Map.Stacks.Values.Single(s => Same(s.TemplateId, tn));
        var battle = session.StartBattle(Str(zones["E01n"], "stack"), made.Id);
        var fight = new D2.Data.Battle.BattleSession(battle, new D2.Rules.Battle.BattleSim(battle, session.Rng, session.Book!.Rules.Constants),
                                                     session.Book!.Rules.Constants) { Auto = true };
        fight.Resolve();
        session.FinishBattle(fight.Log);
        Assert.False(session.Map.Stacks.ContainsKey(made.Id));
        Assert.True(Fired("C5t"), "L_KILL_STACK по шаблону не сработало");

        foreach (string key in new[] { "E02c", "E03d", "E04c", "E03e", "E13c" })
        {
            Step(key);
            Assert.True(Fired(key), $"{key} не сработали");
        }
        string human = session.ActiveHuman;
        string walker = session.Map.Stacks[Str(zones["E02c"], "stack")].LeaderId, strength = session.Book!.Spells["G000SS0003"].ModifierId;
        var vars = facts.GetProperty("variables");
        int Var(string name) => session.State.EventProgress.Variables.GetValueOrDefault(vars.GetProperty(name).GetInt32());
        var summonAt = zones["E03d"].GetProperty("summon_at");
        string Day()
        {
            bool strong = session.Map.SpellEffects.Any(e => Same(e.UnitId, walker) && Same(e.ModifierId, strength));
            int summons = session.Map.Stacks.Values.Count(s => Same(s.Owner, human) && Math.Abs(s.X - summonAt[0].GetInt32()) <= 3
                                                             && Math.Abs(s.Y - summonAt[1].GetInt32()) <= 3
                                                             && session.Book.CategoryOf(session.Map.Units[s.LeaderId].TypeId) == D2.Rules.Battle.UnitCategory.Summon);
            var gob = session.Map.Stacks.GetValueOrDefault(Str(zones["E04c"], "target"));
            var (fx, fy) = XY(zones["E13c"], "fog_at");
            bool fogged = session.Map.Fog[session.Map.Players[human].FogId][fx, fy];
            return $"день {session.Map.Turn}: «Сила» {(strong ? "есть" : "нет")}, призванных {summons}, Гоблин E04c {gob?.Owner ?? "нет"} ход {gob?.Move}, " +
                   $"(30,47) {(fogged ? "в тумане" : "открыта")}, PS_ON {Var("PS_ON")}, PS_OFF {Var("PS_OFF")}, E07-pre {When("E07-pre")}";
        }
        Out.WriteLine(Day());
        session.EndTurn();
        Out.WriteLine(Day());
        session.EndTurn();
        Out.WriteLine(Day());
        Assert.Equal(3, session.Map.Turn);
        Assert.True(Var("PS_ON") >= 1, "включённый счётчик не работал");
    }

    /// <summary>Обрыв пути событием на клетке перед отрядом, мешком и своей деревней (<c>0x440284</c> → 1, как у карты 9); второй ход
    /// (событие одноразовое) не обрывается — по нему в памятке действие.</summary>
    [SkippableFact]
    public void Events3_PathCut_ThenSecondClickGoesThrough()
    {
        Skip.IfNot(MapIds().Contains(Events3Map));
        var facts = Facts(Events3Map);
        var session = Begin(Events3Map, 3);
        var zones = facts.GetProperty("zones").EnumerateArray().ToDictionary(z => Str(z, "key"));
        var keys = facts.GetProperty("cuts").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(3, keys.Count);
        foreach (string key in keys)
        {
            var z = zones[key];
            string stack = Str(z, "stack"), target = Str(z, "target");
            var aim = session.Map.Stacks.TryGetValue(target, out var t) ? (t.X, t.Y) : PathFinder.EntranceOf(session.Map, target)
                      ?? throw new InvalidOperationException($"{key}: у цели {target} нет входа");
            var path = session.PathTo(session.Map.Stacks[stack], aim.Item1, aim.Item2);
            var move = PlayerMove.Plan(session, stack, path);
            Assert.True(move.Cut, $"{key}: путь не оборван");
            var end = move.Today[^1];
            session.MoveStack(stack, end.X, end.Y, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
            Assert.False(PlayerMove.Plan(session, stack, session.PathTo(session.Map.Stacks[stack], aim.Item1, aim.Item2)).Cut, $"{key}: второй ход оборван");
        }
    }

    /// <summary>Карта 9: эффекты ворот против сохранения оригинала после них (<see cref="Events2Gates"/>) — горы после сноса E14 (снятые,
    /// оставшаяся и переложенные 1×1), земля квадратов E22 (Кланы, 15×15) и E22b (Легионы, 5×5), клетка ходока E10b после переноса, гибель
    /// ходока-иллюзии E06i, туман области E13b после «Света дня». Порт проходит ворота памятки по порядку зон (как
    /// <see cref="Events2_EveryGateAndCondition_Fires"/>).</summary>
    [SkippableFact]
    public void Events2_EffectsMatchOriginalSaves()
    {
        Skip.IfNot(MapIds().Contains(Events2Map));
        string orig = Path.Combine(GameSaveTests.CorpusDir(), Events2Run, Events2Gates);
        Skip.IfNot(File.Exists(orig), $"нет {Events2Run}/{Events2Gates}");
        var looks = MapLooksLoader.Load(orig);
        var engine = looks.Map;
        var facts = Facts(Events2Map);
        var session = Begin(Events2Map, 3);
        session.EventBattlesOnScreen = false;
        var zones = facts.GetProperty("zones").EnumerateArray().ToDictionary(z => Str(z, "key"));
        foreach (var z in facts.GetProperty("zones").EnumerateArray().Where(z => z.TryGetProperty("gate_at", out _) && Str(z, "key") != "E00"))
        {
            var (gx, gy) = XY(z, "gate_at");
            session.MoveStack(Str(z, "stack"), gx, gy, 0, 0);
        }
        var port = session.Map;
        static string Mountains(IEnumerable<MountainLook> m) => string.Join(" ", m.OrderBy(g => g.Id).Select(g => $"{g.Id}@{g.X},{g.Y}:{g.Width}"));
        string Land(MapState m, int cx, int cy, int half) =>
            string.Concat(from y in Enumerable.Range(cy - half, 2 * half + 1) from x in Enumerable.Range(cx - half, 2 * half + 1) select m.Tiles[x, y].Terrain);
        string At(MapState m, string key) => m.Stacks.TryGetValue(Str(zones[key], "stack"), out var s) ? $"({s.X},{s.Y})" : "нет";
        string human = session.ActiveHuman;
        var (fx, fy) = XY(zones["E13b"], "fog_at");
        string Fog(MapState m) => string.Concat(from y in Enumerable.Range(fy - 4, 9) from x in Enumerable.Range(fx - 4, 9)
                                                select m.Fog[m.Players[human].FogId][x, y] ? '#' : '.');
        var rows = new List<(string Key, string Engine, string Port)>
        {
            ("E14 горы", Mountains(looks.Mountains), Mountains(session.Looks.Mountains)),
            ("E22 земля 15×15 у (60,20)", Land(engine, 60, 20, 8), Land(port, 60, 20, 8)),
            ("E22b земля 5×5 у (60,40)", Land(engine, 60, 40, 3), Land(port, 60, 40, 3)),
            ("E10b ходок", At(engine, "E10b"), At(port, "E10b")),
            ("E06i ходок-иллюзия", At(engine, "E06i"), At(port, "E06i")),
            ("E06i цель", engine.Stacks.ContainsKey(Str(zones["E06i"], "target")).ToString(), port.Stacks.ContainsKey(Str(zones["E06i"], "target")).ToString()),
            ("E13b туман 9×9 у (30,47)", Fog(engine), Fog(port)),
        };
        foreach (var (key, e, p) in rows) Out.WriteLine($"{key}: движок {e}; порт {p}");
        Assert.Equal("(20,44)", rows.Single(r => r.Key == "E10b ходок").Engine);   // сохранение — после ворот: перенос виден у движка
        Assert.Equal("нет", rows.Single(r => r.Key == "E06i ходок-иллюзия").Engine);
        Assert.Contains('#', rows.Single(r => r.Key.StartsWith("E13b", StringComparison.Ordinal)).Engine);
        Assert.All(rows, r => Assert.True(r.Engine == r.Port, $"{r.Key}: движок {r.Engine}, порт {r.Port}"));
    }

    /// <summary>Сработавшие события с днём срабатывания (у <see cref="EventFiring"/> дня нет).</summary>
    private sealed class EventDays(Func<int> day) : ITurnObserver
    {
        public List<(string Id, int Day)> Fired { get; } = [];

        public void PlayerTurn(string playerId) { }

        public void StackMoving(StackMove move) { }

        public IReadOnlyList<D2.Rules.Battle.TurnRecord>? Battle(D2.Rules.Battle.BattleState state) => null;

        public void Happened(TurnSight sight) { }

        public void BattleEnded(BattleOutcome outcome) { }

        public void EventFired(EventFiring firing) => Fired.Add((firing.Event.Id, day()));
    }
}
