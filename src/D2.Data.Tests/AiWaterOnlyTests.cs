using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>«Только по воде» у ИИ (<c>docs/ai-attack.md</c> §9): водный лидер (<c>WATER_ONLY</c>) берёт в кэш отрядов только
/// цели на воде (<c>0x45e9db</c>), а нейтрал — и нейтралов других подрас (<c>0x45ea2e</c>).</summary>
[Trait("Category", "Corpus")]
public class AiWaterOnlyTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableFact]
    public void WaterBezerk_CachesOnlyStacksOnWater()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260920-legions");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t02-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt, book);
        var bezerk = session.Map.Stacks["S073KC0029"];
        Assert.True(session.WaterOnly(bezerk));
        Assert.True(session.MoveOptionsOf(bezerk).WaterOnly);
        Assert.Contains(AiActionType.AttackStackBezerk, AiTactics.Types(session, bezerk));
        Assert.DoesNotContain(AiActionType.AttackCityBezerk, AiTactics.Types(session, bezerk));

        var turn = AiTactics.Begin(session, bezerk.Owner, AiTacticMemo.Empty)!;
        var cache = AiTactics.StackTargets(session, turn, bezerk);

        Assert.NotEmpty(cache);
        Assert.All(cache, s => Assert.True(session.IsWater(s.X, s.Y), $"{s.Id} не на воде"));
        // Движок в этот день бьёт нейтрала S073KC004e (другая подраса того же хозяина) — он в кэше и у порта.
        Assert.Contains(cache, s => s.Id.Equals("S073KC004E", StringComparison.OrdinalIgnoreCase));
    }
}
