using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Упреждающая проверка пути событиями у хода человека (<see cref="PlayerMove.Plan"/>, <c>docs/map-path.md</c> §7): команда
/// хода обрывает путь там, где сработало бы событие, и события применяются в <see cref="GameSession.MoveStack"/>.</summary>
[Trait("Category", "Corpus")]
public class PlayerPathEventsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Сохранение human дня 8 глазами Легионов (человек — их игрок): <c>S073KC0002</c> идёт к <c>S073KC0027</c> (4,73) и встаёт
    /// в 7,74 — первой клетке зоны <c>S073LO0007</c> (событие <c>S073EV0015</c> «вход в локацию» для Легионов, у движка так встаёт отряд
    /// ИИ, журнал human день 8). После хода событие выключено (одноразовое) — второй раз путь не обрывает. Выключенное заранее
    /// событие путь не обрывает вовсе.</summary>
    [SkippableFact]
    public void EventZone_CutsHumanPath_AndFiresOnMove()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t08-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        string legions = looks.Map.Stacks["S073KC0002"].Owner;
        var info = new GameInfo("PtC", "PtC", [legions], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        GameSession Session(EventProgress progress)
        {
            var session = new GameSession(new GameState(info, looks) { Events = events, EventProgress = progress }, content.MapArt, book);
            session.BeginFirstDay(legions);   // запас хода дня: в сохранении у отряда 0
            return session;
        }

        var session = Session(EventProgress.From(events));
        var stack = session.Map.Stacks["S073KC0002"];
        var path = session.PathTo(stack, 4, 73);
        var move = PlayerMove.Plan(session, stack.Id, path);
        Assert.True(move.Cut);
        Assert.Equal((7, 74), (move.Today[^1].X, move.Today[^1].Y));
        Assert.Contains(move.Fired, f => f.Event.Id.Equals("S073EV0015", StringComparison.OrdinalIgnoreCase));

        var end = move.Today[^1];
        var before = move.Today.Count > 1 ? move.Today[^2] : path.Points[0];
        session.MoveStack(stack.Id, end.X, end.Y, PathFinder.Facing(before, end), path.Points[move.Today.Count].Cost, move.Fired, move.Today);
        Assert.Equal((7, 74), (session.Map.Stacks[stack.Id].X, session.Map.Stacks[stack.Id].Y));
        Assert.False(session.State.EventProgress.IsEnabled("S073EV0015"));

        var off = Session(EventProgress.From(events).Enable("S073EV0015", false));
        var whole = PlayerMove.Plan(off, stack.Id, off.PathTo(off.Map.Stacks[stack.Id], 4, 73));
        Assert.False(whole.Cut);
        Assert.Empty(whole.Fired);
    }

    /// <summary>Невидимый отряд и обрыв пути событием (<c>0x43fe65</c> → <c>CEffectShow</c> с 1, <c>0x43fe8c</c>): та же зона
    /// <c>S073LO0007</c> — оборванный ход отряд показывает (<see cref="GameSession.MoveStack"/>), тот же ход без события — нет. Обрыв на
    /// последней клетке сегодняшнего пути (путь прямо в 7,74) — тоже обрыв (выход <c>0x440284</c> = 1): прежде
    /// <see cref="PlayerMove.Cut"/> считался только по укороченному пути, и после такого хода отряд человека ещё нападал или входил.</summary>
    [SkippableFact]
    public void EventZone_CutRevealsInvisible_PlainMoveDoesNot()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t08-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        const string id = "S073KC0002";
        string legions = looks.Map.Stacks[id].Owner;
        looks = looks with { Stacks = [.. looks.Stacks.Select(s => s.Id == id ? s with { Invisible = true } : s)] };
        var info = new GameInfo("PtC", "PtC", [legions], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        GameSession Session(EventProgress progress)
        {
            var session = new GameSession(new GameState(info, looks) { Events = events, EventProgress = progress }, content.MapArt, book);
            session.BeginFirstDay(legions);
            return session;
        }
        static void Walk(GameSession session, StackPath path, PlayerMove move)
        {
            var end = move.Today[^1];
            var before = move.Today.Count > 1 ? move.Today[^2] : path.Points[0];
            session.MoveStack(id, end.X, end.Y, PathFinder.Facing(before, end), path.Points[move.Today.Count].Cost, move.Fired, move.Today);
        }

        var cut = Session(EventProgress.From(events));
        Assert.True(cut.Invisible(id));
        var path = cut.PathTo(cut.Map.Stacks[id], 4, 73);
        var move = PlayerMove.Plan(cut, id, path);
        Assert.True(move.Cut);
        Walk(cut, path, move);
        Assert.False(cut.Invisible(id));

        var plain = Session(EventProgress.From(events).Enable("S073EV0015", false));
        var free = plain.PathTo(plain.Map.Stacks[id], 4, 73);
        var walk = PlayerMove.Plan(plain, id, free);
        Assert.False(walk.Cut);
        Walk(plain, free, walk);
        Assert.True(plain.Invisible(id));

        var last = Session(EventProgress.From(events));
        var toZone = last.PathTo(last.Map.Stacks[id], 7, 74);
        Assert.Equal((7, 74), (toZone.Today[^1].X, toZone.Today[^1].Y));
        var atLast = PlayerMove.Plan(last, id, toZone);
        Assert.True(atLast.Cut);
        Assert.Equal(toZone.Today.Count, atLast.Today.Count);
    }
}
