using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Выселение гостя из своей крепости (<c>CAiKickOutStackAction</c>, <c>AiTactics.KickOut</c>): отряд рядом с
/// занятой крепостью вместо входа строит выселение, гость выходит в первую свободную клетку у входа.</summary>
[Trait("Category", "Corpus")]
public class AiKickOutTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Журнал human, день 30: <c>S073KC0085</c> пришёл лечиться к столице <c>S073FT0000</c> (вход 15,15) и встал на 15,16,
    /// а в столице гость <c>S073KC008f</c>. Движок выбирает выселение (счёт лечения ×0,9 «занята» — 464), гость выходит на 14,16 —
    /// первый сдвиг (−1, 1) таблицы <c>0x6d1050</c>, статус 2 пишется на гостя; выселяющий остаётся на месте.</summary>
    [SkippableFact]
    public void HealerNextToOccupiedCapital_KicksTheGuestOut()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t30-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var session = new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        const string healer = "S073KC0085", guest = "S073KC008f", capital = "S073FT0000";
        Assert.Equal(guest, session.Map.Sites.Single(s => s.Id == capital).StackId, ignoreCase: true);
        session.MoveStack(healer, 15, 16, 0, 0);
        var turn = AiTactics.Begin(session, session.Map.Stacks[healer].Owner, AiTacticMemo.Empty)!;

        var kick = AiTactics.Candidates(session, turn, session.Map.Stacks[healer])
                            .Select(c => c.Action).Single(a => a.Fort == AiFortStep.KickOut);

        Assert.Equal((AiActionType.HealRevive, capital, 14, 16), (kick.Type, kick.Target, kick.X, kick.Y));
        Assert.Equal(guest, kick.Guest, ignoreCase: true);
        Assert.Equal(guest, kick.KeyStack, ignoreCase: true);
        var (status, key) = session.ExecuteTactic(kick, turn);
        Assert.Equal(2, status);
        Assert.Equal(guest, key.Stack, ignoreCase: true);
        var after = session.Map.Stacks[guest];
        Assert.Equal((14, 16, ""), (after.X, after.Y, after.Inside));
        Assert.Equal("", session.Map.Sites.Single(s => s.Id == capital).StackId);
        Assert.Equal((15, 16), (session.Map.Stacks[healer].X, session.Map.Stacks[healer].Y));
    }

    /// <summary>Клетки у входа по порядку таблицы: занятая другим отрядом пропускается — гость выходит в следующую.</summary>
    [SkippableFact]
    public void OccupiedFirstCell_KickPointMovesOn()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t30-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var session = new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        session.MoveStack("S073KC0085", 14, 16, 0, 0);   // встал в первую клетку выселения сам
        var turn = AiTactics.Begin(session, session.Map.Stacks["S073KC0085"].Owner, AiTacticMemo.Empty)!;

        var kick = AiTactics.Candidates(session, turn, session.Map.Stacks["S073KC0085"])
                            .Select(c => c.Action).Single(a => a.Fort == AiFortStep.KickOut);

        Assert.Equal((15, 16), (kick.X, kick.Y));
    }
}
