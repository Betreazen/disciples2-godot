using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Бегство к столице (<c>Retreat</c>, оценщик <c>0x462cda</c>, <c>AiTactics.Retreat</c>): отряд в поле, рядом (ближе 7) сильный
/// отряд человека из чужих сильнейших, до своей столицы не меньше 7 — действие к её входу со счётом 2000.</summary>
[Trait("Category", "Corpus")]
public class AiRetreatTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Журнал human, день 40: <c>S073KC0082</c> (Империя, 66,67) в 6 клетках от отряда человека <c>S073KC0075</c>, до
    /// столицы <c>S073FT0003</c> (вход 81,81) 15 — движок выбирает бегство к 81,81 со счётом 2000 и идёт к ней.</summary>
    [SkippableFact]
    public void ThreatenedStack_RetreatsToCapital()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t40-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var session = new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        var stack = session.Map.Stacks["S073KC0082"];
        var threat = session.Map.Stacks["S073KC0075"];
        var turn = AiTactics.Begin(session, stack.Owner, AiTacticMemo.Empty)!;
        Assert.Contains(threat.Id, turn.Forces.Others, StringComparer.OrdinalIgnoreCase);
        var p = AiTactics.AttackOn(session, threat, stack);
        output.WriteLine($"угроза {threat.Id}: A {p.A:0.##} B {p.B:0.##} C {p.C:0.##} p {p.P:0.###}");

        var retreat = AiTactics.Candidates(session, turn, stack).Single(c => c.Action.Type == AiActionType.Retreat);

        Assert.True(p.P >= AiTacticEval.RetreatStrength);
        Assert.Equal(("S073FT0003", 81, 81), (retreat.Action.Target.ToUpperInvariant(), retreat.Action.X, retreat.Action.Y));
        Assert.Equal(2000, retreat.Score);
    }
}
