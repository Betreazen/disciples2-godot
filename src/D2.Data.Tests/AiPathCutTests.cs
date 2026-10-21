using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>
/// Путь ИИ, оборванный событием на последней клетке перед целью (<c>docs/map-path.md</c> §7, «Обрыв отменяет действие»): генератор
/// хода <c>0x43fc3c</c> при обрыве (выход <c>0x440284</c> = 1) не ставит цель нападения (<c>0x43ff9d</c>) и возвращает 0, так что
/// эффекты хода не зовут объект за концом пути (<c>0x43fc13</c> → <c>0x4408cb</c>). Остаётся только ход до клетки обрыва.
/// Сохранение human, день 8: <c>S073KC0002</c> (Легионы, ИИ) с 16,79 идёт на запад; зона <c>S073LO0007</c> (событие <c>S073EV0015</c>
/// «вход в локацию» для Легионов; обрыв в первой её клетке — <see cref="AiPathEventsTests"/>). Цель ставится в 6,74 — путь сегодня
/// подходит к ней с 7,75, первой клетки зоны на этом пути, и обрыв приходится ровно на последнюю клетку.
/// </summary>
[Trait("Category", "Corpus")]
public sealed class AiPathCutTests
{
    private const string Mover = "S073KC0002";
    private const string Target = "S073KC0027";
    private const string Bag = "S073BG0007";
    private const string ZoneEvent = "S073EV0015";

    /// <summary>Статус атаки после перемещения: 5 отдаётся как 2 (<c>0x4545d8</c>, <c>+0x58</c>).</summary>
    private const int AttackStatus = 2;

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Атака отряда с подходом (N = 0): событие на последней клетке — боя нет, нападающий стоит в 7,75, цель цела, статус атаки
    /// тот же (2, <c>0x4545d8</c>). Без события — бой по прибытии.</summary>
    [SkippableFact]
    public void AttackCutAtLastCell_NoBattle()
    {
        var (session, battles) = Session(eventOn: true, MoveTarget);
        var attack = new AiTacticOption(AiActionType.AttackStack, Mover, Target, 6, 74);
        var path = AiTactics.Route(session, session.Map.Stacks[Mover], attack);
        Assert.Equal((7, 75), (path.Today[^1].X, path.Today[^1].Y));
        var (walk, _, cut) = session.PathEvents(Mover, path.Today);
        Assert.True(cut);
        Assert.Equal(path.Today.Count, walk);   // обрыв именно на последней клетке

        var (status, _) = session.ExecuteTactic(attack);

        Assert.Equal(AttackStatus, status);
        Assert.Equal((7, 75), (session.Map.Stacks[Mover].X, session.Map.Stacks[Mover].Y));
        Assert.Empty(battles);
        Assert.True(session.Map.Stacks.ContainsKey(Target));
        Assert.False(session.State.EventProgress.IsEnabled(ZoneEvent));   // событие применено ходом

        var (free, freeBattles) = Session(eventOn: false, MoveTarget);
        Assert.Equal(AttackStatus, free.ExecuteTactic(attack).Status);
        Assert.Single(freeBattles);
    }

    /// <summary>Мешок-цель за клеткой обрыва: путь к мешку (сам мешок непроходим — встаёт рядом, 7,75) оборван событием на последней
    /// клетке — мешок не взят (<c>0x4408cb</c> не зовётся), статус перемещения 5. Без события — взят с соседней клетки.</summary>
    [SkippableFact]
    public void TakeBagCutAtLastCell_BagStays()
    {
        var (session, _) = Session(eventOn: true, MoveBag);
        var take = new AiTacticOption(AiActionType.TakeBag, Mover, Bag, 6, 74);
        var path = AiTactics.Route(session, session.Map.Stacks[Mover], take);
        Assert.Equal((7, 75), (path.Today[^1].X, path.Today[^1].Y));
        var (walk, _, cut) = session.PathEvents(Mover, path.Today);
        Assert.True(cut);
        Assert.Equal(path.Today.Count, walk);

        var (status, _) = session.ExecuteTactic(take);

        Assert.Equal(AiTactic.StatusProgress, status);
        Assert.Equal((7, 75), (session.Map.Stacks[Mover].X, session.Map.Stacks[Mover].Y));
        Assert.False(BagSite(session).Gone);

        var (free, _) = Session(eventOn: false, MoveBag);
        free.ExecuteTactic(take);
        Assert.True(BagSite(free).Gone);
    }

    /// <summary>Мешок на пути к дальней цели (<c>S073KC0027</c> на месте, 4,73): мешок в 6,74 обрывает путь перед собой
    /// (<c>0x5cda67</c>), а событие — на той же последней клетке. Мешок не взят (эффекты хода объект за концом не зовут), без
    /// события — взят с соседней клетки (<c>0x43fbac</c> → <c>0x4408cb</c>).</summary>
    [SkippableFact]
    public void BagAheadCutAtLastCell_BagStays()
    {
        var (session, _) = Session(eventOn: true, MoveBag);
        var attack = new AiTacticOption(AiActionType.AttackStack, Mover, Target, 4, 73);
        var path = AiTactics.Route(session, session.Map.Stacks[Mover], attack);
        Assert.Equal((6, 74), path.CutAt is { } at ? (at.X, at.Y) : default);
        var (walk, _, cut) = session.PathEvents(Mover, path.Today);
        Assert.True(cut);
        Assert.Equal(path.Today.Count, walk);

        session.ExecuteTactic(attack);

        Assert.False(BagSite(session).Gone);

        var (free, _) = Session(eventOn: false, MoveBag);
        free.ExecuteTactic(attack);
        Assert.True(BagSite(free).Gone);
    }

    private static MapSite BagSite(GameSession session) =>
        session.Map.Sites.Single(s => s.Id.Equals(Bag, StringComparison.OrdinalIgnoreCase));

    private static MapState MoveTarget(MapState map)
    {
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var target = stacks[Target];
        stacks[Target] = target with { X = 6, Y = 74 };
        return GameSession.With(map, stacks: stacks, plan: Moved(map, Target, (target.X, target.Y)));
    }

    private static MapState MoveBag(MapState map)
    {
        var bag = map.Sites.Single(s => s.Id.Equals(Bag, StringComparison.OrdinalIgnoreCase));
        return GameSession.With(map, plan: Moved(map, Bag, (bag.X, bag.Y)), sites: [.. map.Sites.Select(s => s == bag ? s with { X = 6, Y = 74 } : s)]);
    }

    /// <summary>План карты, где объект <paramref name="id"/> перенесён из <paramref name="from"/> в 6,74.</summary>
    private static Dictionary<(int X, int Y), IReadOnlyList<string>> Moved(MapState map, string id, (int X, int Y) from)
    {
        var plan = map.Plan.ToDictionary(p => p.Key, p => p.Value);
        plan[from] = [.. plan[from].Where(o => !o.Equals(id, StringComparison.OrdinalIgnoreCase))];
        plan[(6, 74)] = [.. plan.GetValueOrDefault((6, 74)) ?? [], id];
        return plan;
    }

    private static (GameSession Session, List<BattleOutcome> Battles) Session(bool eventOn, Func<MapState, MapState> arrange)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t08-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var art = SharedContent.Load(Data).MapArt;
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var map = arrange(looks.Map);
        // В сохранении запас 0: его ставит начало хода ИИ.
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[Mover] = stacks[Mover] with { Move = new GameSession(new GameState(info, looks), art, book).DayAllowance(stacks[Mover]) };
        looks = looks with { Map = GameSession.With(map, stacks: stacks) };
        var progress = eventOn ? EventProgress.From(events) : EventProgress.From(events).Enable(ZoneEvent, false);
        var session = new GameSession(new GameState(info, looks) { Events = events, EventProgress = progress }, art, book);
        var battles = new List<BattleOutcome>();
        session.Watch(new Battles(battles));
        return (session, battles);
    }

    private sealed class Battles(List<BattleOutcome> list) : ITurnObserver
    {
        public void PlayerTurn(string playerId)
        {
        }

        public void StackMoving(StackMove move)
        {
        }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;

        public void BattleEnded(BattleOutcome outcome) => list.Add(outcome);
    }
}
