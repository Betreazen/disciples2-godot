using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Упреждающая проверка пути событиями сценария (<c>0x43fc3c</c> → <c>0x440284</c>, <see cref="GameSession.PathEvents"/>):
/// отряд ИИ встаёт на клетке, где сработало бы событие.</summary>
[Trait("Category", "Corpus")]
public class AiPathEventsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Журнал human, день 8: <c>S073KC0002</c> (Легионы) идёт атаковать <c>S073KC0027</c> и встаёт с 3 очками в 7,74 —
    /// первой клетке зоны <c>S073LO0007</c> (радиус 2, событие <c>S073EV0015</c> «вход в локацию» для Легионов). Выключенное
    /// событие путь не обрывает.</summary>
    [SkippableFact]
    public void EnteringEventZone_StopsAiPathInFirstZoneCell()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t08-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        GameSession Session(EventProgress progress) =>
            new(new GameState(info, looks) { Events = events, EventProgress = progress }, content.MapArt, book);

        var session = Session(EventProgress.From(events));
        var stack = session.Map.Stacks["S073KC0002"];
        stack = stack with { Move = session.DayAllowance(stack) };   // в сохранении запас 0: его ставит начало хода ИИ
        var path = AiTactics.Route(session, stack, new AiTacticOption(AiActionType.AttackStack, stack.Id, "S073KC0027", 4, 73));
        var (walk, fired, _) = session.PathEvents(stack.Id, path.Today);
        Assert.True(walk < path.Today.Count);
        Assert.Equal((7, 74), (path.Today[walk - 1].X, path.Today[walk - 1].Y));
        Assert.Contains(fired, f => f.Event.Id.Equals("S073EV0015", StringComparison.OrdinalIgnoreCase));

        var off = Session(EventProgress.From(events).Enable("S073EV0015", false));
        Assert.Equal(path.Today.Count, off.PathEvents(stack.Id, path.Today).Walk);
    }

    /// <summary>Журнал human, день 10: <c>S073KC0076</c> идёт помогать <c>S073KC0000</c> (точка 20,26), путь ведёт через мешок
    /// <c>S073BG0027</c> на 23,33 — отряд встаёт перед ним на 24,34 (мешок не в первой клетке шага) и берёт его с соседней клетки
    /// (<c>CEffectTakeBag</c>): к следующей оценке предмет <c>S073IM0043</c> уже в отряде, мешка в сохранении дня 11 нет.</summary>
    [SkippableFact]
    public void StopBeforeBag_TakesItFromNextCell()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t10-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var stacks = looks.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        // Как у движка к ходу KC0076: лидер KC0000 уже ушёл с 24,34 на 20,24, у помощника запас 25.
        stacks["S073KC0076"] = stacks["S073KC0076"] with { Move = 25 };
        looks = looks with { Map = GameSession.With(looks.Map, stacks: stacks) };
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var session = new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        session.MoveStack("S073KC0000", 20, 24, 0, 0);
        var helper = session.Map.Stacks["S073KC0076"];
        var assist = new AiTacticOption(AiActionType.AssistMove, helper.Id, "S073KC0000", 20, 26);
        var path = AiTactics.Route(session, helper, assist);
        Assert.Equal([(22, 36), (23, 35), (24, 34), (23, 33)], path.Points.Take(4).Select(p => (p.X, p.Y)));   // как в журнале
        Assert.Equal((24, 34), (path.Today[^1].X, path.Today[^1].Y));

        session.ExecuteTactic(assist);

        Assert.Equal((24, 34), (session.Map.Stacks[helper.Id].X, session.Map.Stacks[helper.Id].Y));
        Assert.Contains("S073IM0043", session.Map.Stacks[helper.Id].Items, StringComparer.OrdinalIgnoreCase);
        Assert.True(session.Map.Sites.Single(s => s.Id.Equals("S073BG0027", StringComparison.OrdinalIgnoreCase)).Gone);
    }

    /// <summary>Ход выводит отряд из укрепления — он больше не его гость (<c>MapSite.StackId</c>): раньше гостя снимала только сцена
    /// карты человека, и отряд ИИ, ушедший из города, числился в нём (заметка сессии Б).</summary>
    [SkippableFact]
    public void MoveOutOfFort_ClearsGuest()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t05-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var session = new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        var fort = session.Map.Sites.First(s => s.StackId.Length > 0 && session.Map.Stacks.ContainsKey(s.StackId));
        var gate = PathFinder.EntranceOf(session.Map, fort.Id)!.Value;
        session.MoveStack(fort.StackId, gate.X + 1, gate.Y + 1, 0, 0);
        Assert.Equal("", session.Map.Sites.First(s => s.Id == fort.Id).StackId);
    }
}
