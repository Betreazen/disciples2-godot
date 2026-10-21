using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Допуски найма лидера «жезл» (<c>0x44a207</c>) и «обычный вор» (<c>0x44b282</c>), ранг 1 по листингу
/// (<see cref="AiEconomy.LeaderFlags"/>, <c>docs/ai-economy.md</c> §2). В корпусе оба всегда 1 — проверка на правленой карте.</summary>
[Trait("Category", "Corpus")]
public class AiLeaderFlagsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Ai = "S073PL0009";

    private static GameSession Load()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t62-102.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var looks = MapLooksLoader.Load(sg);
        return new GameSession(new GameState(new GameInfo("", "", ["S073PL000a"], 1, 1), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }

    private static AiLeaderFlags Flags(GameSession game) =>
        AiEconomy.LeaderFlags(game, Ai, AiEconomy.LeaderCounts(game, Ai));

    /// <summary>Жезл — от карты, не от расы: есть кристалл (<c>0x44b545</c>, объекты типа 0x2c); нет кристаллов — только событие
    /// с последним эффектом <c>L_WIN</c> и условием <c>L_TRANSFORM_LAND</c> (<c>0x5f2701</c>).</summary>
    [SkippableFact]
    public void Rod_CrystalsOrTransformLandVictory()
    {
        var game = Load();
        Assert.Contains(game.Map.Objects.Values, o => o.Kind == "CR");
        Assert.True(Flags(game).Rod);

        var map = game.Map;
        var noCrystals = GameSession.With(map, objects: map.Objects.Where(p => p.Value.Kind != "CR").ToDictionary(p => p.Key, p => p.Value));
        var bare = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, noCrystals, game.Looks.Stacks), Events = ScenarioEventSet.Empty },
                                   game.Art, game.Book);
        Assert.False(Flags(bare).Rod);

        bool[] races = [true, true, true, true, true, true];
        ScenarioEventSet Set(params ScenarioEvent[] events) => ScenarioEventSet.Empty with { Events = events };
        var notLast = new ScenarioEvent("S073EV0f00", true, true, 100, 0, races, [new TransformLandCondition(50)],
                                        [new WinEffect(true, Ai), new EnableEventEffect("S073EV0f01", true)]);
        Assert.False(Flags(new GameSession(bare.State with { Events = Set(notLast) }, bare.Art, bare.Book)).Rod);
        var victory = notLast with { Effects = [new WinEffect(true, Ai)] };
        Assert.True(Flags(new GameSession(bare.State with { Events = Set(victory) }, bare.Art, bare.Book)).Rod);
    }

    /// <summary>Обычный подприказ вора — есть другой не нейтральный игрок, которого игрок ещё не разведал (<see cref="MapPlayer.Spied"/>).</summary>
    [SkippableFact]
    public void ThiefNormal_WhileSomeoneUnspied()
    {
        var game = Load();
        var others = game.Map.Players.Keys.Where(p => p != Ai && !AiTactics.NeutralOwner(game, p)).ToList();
        Assert.NotEmpty(others);
        Assert.True(Flags(With(game, others.Skip(1).ToList())).ThiefNormal);
        Assert.False(Flags(With(game, others)).ThiefNormal);
    }

    private static GameSession With(GameSession game, IReadOnlyList<string> spied)
    {
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Ai] = players[Ai] with { Spied = spied };
        return new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);
    }
}
