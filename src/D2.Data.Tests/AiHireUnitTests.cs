using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Построитель <c>HireUnit</c> (<c>0x459f0d</c>, <c>AiTactics.HireUnit</c>): отряд с местом идёт к своей крепости за юнитами
/// гарнизона. Эталон — зонд 7.1 (<c>ai_hire_score</c>, корпус <c>20260928-shots</c>, ai-q09).</summary>
[Trait("Category", "Corpus")]
public class AiHireUnitTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>ai-q09, день 1: жезлоносица Легионов <c>S143KC008b</c> (7,81; лидерство 2, один юнит — одно место) — движок строит
    /// оценку найма к своей столице <c>S143FT0001</c>: счёт −102, свободно 1, снимаемых 2, «можно» — да (строка <c>ai_hire_score</c>
    /// seq 3092222). Порт — тот же кандидат с тем же счётом.</summary>
    [SkippableFact]
    public void RodBearerWithOneFreeCell_HireUnitToCapital_MatchesProbe()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260928-shots");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "ai-q09-ai-ai-diplomacy-human-t01-*.sg").Order(StringComparer.Ordinal).First();
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("q09", "q09", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var session = new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        const string rod = "S143KC008b", capital = "S143FT0001";
        var stack = session.Map.Stacks[rod];
        Assert.Equal((7, 81, 1), (stack.X, stack.Y, session.FreeCells(stack)));
        var turn = AiTactics.Begin(session, stack.Owner, AiTacticMemo.Empty)!;

        var hire = AiTactics.Candidates(session, turn, stack).Single(c => c.Action.Type == AiActionType.HireUnit);

        Assert.Equal(capital, hire.Action.Target, ignoreCase: true);
        Assert.Equal(-102, hire.Score, 6);
    }
}
