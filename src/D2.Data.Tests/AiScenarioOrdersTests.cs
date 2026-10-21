using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Сценарные приказы ИИ (<c>AiTactics.Defend.cs</c>, <c>docs/ai-tactic-candidates.md</c> §18): «защищать отряд», «защищать
/// место», «идти к месту». В корпусе таких приказов нет — отряды и места ставятся поверх сохранения human дня 30.</summary>
[Trait("Category", "Corpus")]
public class AiScenarioOrdersTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private const string Zone = "S073LOfff0";

    private static GameSession Game()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t30-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        return new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
    }

    /// <summary>Два отряда одного ИИ одной подрасы в поле: первый охраняет второй.</summary>
    private static (MapStack Guard, MapStack Guarded) Pair(GameSession session)
    {
        var map = session.Map;
        var field = map.Stacks.Values.Where(s => !map.Players[s.Owner].IsHuman && s.Inside.Length == 0 && s.LeaderAlive)
                                     .OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        foreach (var a in field)
            if (field.FirstOrDefault(b => b != a && b.Owner == a.Owner && b.Subrace == a.Subrace) is { } b) return (a, b);
        Skip.If(true, "нет пары отрядов");
        throw new InvalidOperationException();
    }

    /// <summary>Отряд и места событий; место кладётся и в план карты клеткой центра — как у редактора (движок ищет место в конце пути
    /// по плану, <c>0x460fa0</c> → <c>0x5f54e2</c>).</summary>
    private static GameSession With(GameSession session, MapStack stack, IReadOnlyDictionary<string, EventZone>? zones = null)
    {
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = stack;
        Dictionary<(int X, int Y), IReadOnlyList<string>>? plan = null;
        if (zones is not null)
        {
            plan = session.Map.Plan.ToDictionary(p => p.Key, p => p.Value);
            foreach (var z in zones.Values)
                if (plan.GetValueOrDefault((z.X, z.Y)) is not { } ids || !ids.Contains(z.Id, StringComparer.OrdinalIgnoreCase))
                    plan[(z.X, z.Y)] = [.. plan.GetValueOrDefault((z.X, z.Y)) ?? [], z.Id];
        }
        var state = session.State with { Looks = session.Looks with { Map = GameSession.With(session.Map, stacks: stacks, plan: plan) } };
        if (zones is not null) state = state with { Events = state.Events with { Zones = zones } };
        return new GameSession(state, session.Art, session.Book);
    }

    private static IReadOnlyList<AiTacticOption> Candidates(GameSession session, string stackId)
    {
        var turn = AiTactics.Begin(session, session.Map.Stacks[stackId].Owner, AiTacticMemo.Empty)!;
        return [.. AiTactics.Candidates(session, turn, session.Map.Stacks[stackId]).Select(c => c.Action)];
    }

    /// <summary><c>L_DEFEND_STACK</c>: охранник вдали от охраняемого — одно действие <c>DefendParty</c>: к охраняемому (N = 2, встать
    /// рядом) или на врага у него (атака); охраняемого нет — смена приказа.</summary>
    [SkippableFact]
    public void DefendStack_GoesToGuardedOrItsEnemy_ElseChangesOrder()
    {
        var session = Game();
        var (guard, guarded) = Pair(session);
        var map = session.Map;
        (int X, int Y) far = Enumerable.Range(5, 6).SelectMany(d => new[] { (guarded.X + d, guarded.Y), (guarded.X - d, guarded.Y),
                                                                              (guarded.X, guarded.Y + d), (guarded.X, guarded.Y - d) })
            .First(c => map.Contains(c.Item1, c.Item2) && !map.Plan.ContainsKey(c) && map.Tiles[c.Item1, c.Item2].Passable);
        var ordered = With(session, guard with { Order = (int)StackOrder.DefendStack, OrderTarget = guarded.Id, X = far.X, Y = far.Y });
        var options = Candidates(ordered, guard.Id);
        var defend = Assert.Single(options);
        Assert.Equal(AiActionType.DefendParty, defend.Type);
        if (defend.Target.Equals(guarded.Id, StringComparison.OrdinalIgnoreCase))
            Assert.Equal((2, AiMoveAct.Move), (defend.Reach, defend.Act));
        else Assert.Contains(defend.Act, new[] { AiMoveAct.Attack, AiMoveAct.Bag, AiMoveAct.Move });

        var orphan = With(session, guard with { Order = (int)StackOrder.DefendStack, OrderTarget = "S073KCfff0" });
        Assert.Equal(AiActionType.ChangeOrder, Assert.Single(Candidates(orphan, guard.Id)).Type);
    }

    /// <summary><c>L_DEFEND_LOCATION</c>: врагов в радиусе места нет — идти к месту (цель — место, режим пути 2); стоим на нём —
    /// действия нет.</summary>
    [SkippableFact]
    public void DefendLocation_WalksToTheSpot_AndStaysThere()
    {
        var session = Game();
        var (guard, _) = Pair(session);
        var map = session.Map;
        (int X, int Y) spot = Enumerable.Range(2, 6).SelectMany(d => new[] { (guard.X + d, guard.Y), (guard.X - d, guard.Y), (guard.X, guard.Y + d) })
            .First(c => map.Contains(c.Item1, c.Item2) && !map.Plan.ContainsKey(c) && map.Tiles[c.Item1, c.Item2].Passable);
        var zones = new Dictionary<string, EventZone>(StringComparer.OrdinalIgnoreCase) { [Zone] = new EventZone(Zone, spot.X, spot.Y, 0) };
        var ordered = With(session, guard with { Order = (int)StackOrder.DefendLocation, OrderTarget = Zone }, zones);
        var go = Assert.Single(Candidates(ordered, guard.Id));
        Assert.Equal((AiActionType.DefendLocation, Zone, spot.X, spot.Y), (go.Type, go.Target, go.X, go.Y));
        Assert.Equal((2, 0), AiTactics.MoveMode(ordered, go));

        var there = With(ordered, ordered.Map.Stacks[guard.Id] with { X = spot.X, Y = spot.Y });
        Assert.Empty(Candidates(there, guard.Id));
    }

    /// <summary><c>L_MOVE_TO_LOCATION</c> по коридору в горах, посреди коридора мешок: путь с непроходимыми мешками не находится, путь
    /// с проходимыми обрывается на мешке (<see cref="D2.Rules.Map.StackPath.CutAt"/>) — действие у цели (<c>0x460fa0</c>) берёт мешок
    /// из цели искателя +0x10 (клетки обрыва). Оценщик при этом держит место приказа (фильтр приказа пропускает). До 13.10 порт искал
    /// мешок по цели поиска и точкам обрезанного списка — кандидата не было.</summary>
    [SkippableFact]
    public void MoveToLocation_BagMidCorridor_TakesBag()
    {
        var session = Game();
        var (walker, _) = Pair(session);
        var map = session.Map;
        (int X, int Y) start = (map.Size / 2, map.Size / 2);
        const int Length = 6;
        var tiles = (Tile[,])map.Tiles.Clone();
        var plan = map.Plan.ToDictionary(p => p.Key, p => p.Value);
        for (int x = start.X - 1; x <= start.X + Length + 1; x++)
            for (int y = start.Y - 1; y <= start.Y + 1; y++)
            {
                bool corridor = y == start.Y && x >= start.X && x <= start.X + Length;
                tiles[x, y] = new Tile((byte)((int)(corridor ? Ground.Plain : Ground.Mountain) << 3 | 1));
                plan.Remove((x, y));
            }
        (int X, int Y) bagAt = (start.X + 2, start.Y), zoneAt = (start.X + Length, start.Y);
        const string Bag = "S073BGfff0";
        foreach (var (cell, ids) in plan.Where(p => p.Value.Contains(walker.Id)).ToList())
            plan[cell] = [.. ids.Where(id => id != walker.Id)];
        plan[start] = [walker.Id];
        plan[bagAt] = [Bag];
        // Чужих отрядов рядом нет — зона контроля коридор не трогает.
        var stacks = map.Stacks.Values.Where(s => s.Id == walker.Id || Math.Max(Math.Abs(s.X - start.X), Math.Abs(s.Y - start.Y)) > 12)
            .ToDictionary(s => s.Id, s => s.Id == walker.Id
                ? s with { X = start.X, Y = start.Y, Inside = "", Move = 40, Order = (int)StackOrder.MoveToLocation, OrderTarget = Zone } : s,
                StringComparer.OrdinalIgnoreCase);
        var zones = new Dictionary<string, EventZone>(StringComparer.OrdinalIgnoreCase) { [Zone] = new EventZone(Zone, zoneAt.X, zoneAt.Y, 0) };
        var changed = GameSession.WithTiles(GameSession.With(map, stacks: stacks, plan: plan), tiles);
        var state = session.State with { Looks = session.Looks with { Map = changed }, Events = session.State.Events with { Zones = zones } };
        var ordered = new GameSession(state, session.Art, session.Book);

        var bag = Assert.Single(Candidates(ordered, walker.Id));
        Assert.Equal((AiActionType.MoveToLocation, AiMoveAct.Bag, Bag, bagAt.X, bagAt.Y), (bag.Type, bag.Act, bag.Target, bag.X, bag.Y));
        Assert.Equal(new AiEvalTarget(Zone, zoneAt.X, zoneAt.Y), bag.Eval);
    }

    /// <summary><c>L_MOVE_TO_LOCATION</c>: стоим на месте — приказ «стоять» без цели (статус 1).</summary>
    [SkippableFact]
    public void MoveToLocation_OnTheSpot_Stands()
    {
        var session = Game();
        var (walker, _) = Pair(session);
        var zones = new Dictionary<string, EventZone>(StringComparer.OrdinalIgnoreCase) { [Zone] = new EventZone(Zone, walker.X, walker.Y, 0) };
        var ordered = With(session, walker with { Order = (int)StackOrder.MoveToLocation, OrderTarget = Zone }, zones);
        var stand = Assert.Single(Candidates(ordered, walker.Id));
        Assert.Equal((AiActionType.MoveToLocation, AiMoveAct.Stand), (stand.Type, stand.Act));

        var (status, _) = ordered.ExecuteTactic(stand);
        Assert.Equal(1, status);
        Assert.Equal(((int)StackOrder.Stand, ""), (ordered.Map.Stacks[walker.Id].Order, ordered.Map.Stacks[walker.Id].OrderTarget));
    }
}
