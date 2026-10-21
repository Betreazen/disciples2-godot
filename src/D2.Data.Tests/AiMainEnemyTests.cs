using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Главный враг тактики (<c>CAiTactic +0x51c0</c>, <c>0x465fc9</c>) и лидеры-бойцы (<c>0x466f57</c>) —
/// <c>docs/ai-attack.md</c> §10.</summary>
[Trait("Category", "Corpus")]
public class AiMainEnemyTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static GameSession Game()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260920-legions");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        var looks = MapLooksLoader.Load(Directory.GetFiles(dir, "*-t10-*.sg").Order(StringComparer.Ordinal).Last());
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }

    private static GameSession WithForts(GameSession session, Func<MapSite, MapSite> change)
    {
        var map = session.Map;
        var sites = map.Sites.Select(s => s.Kind is SiteKind.Capital or SiteKind.Village ? change(s) : s).ToList();
        return new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, sites: sites) } },
                               session.Art, session.Book);
    }

    /// <summary>legions день 10: у нейтрала больше 40 % крепостей и кристаллов на нейтральной земле — он главный враг (с ним счёт
    /// берсерка и атак на нейтралов сходится с журналом), у него больше одного лидера-бойца; все крепости у одного игрока — главный
    /// он.</summary>
    [SkippableFact]
    public void MainEnemy_ShareOfFortsAndCrystals()
    {
        var session = Game();
        Assert.Equal("S073PL0000", AiTactics.MainEnemy(session), ignoreCase: true);
        Assert.True(AiTactics.FieldLeaders(session, "S073PL0000") > 1);

        string rich = session.Map.Players.Keys.First(id => !AiTactics.NeutralOwner(session, id));
        Assert.Equal(rich, AiTactics.MainEnemy(WithForts(session, f => f with { Owner = rich })), ignoreCase: true);
    }
}
