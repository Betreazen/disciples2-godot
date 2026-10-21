using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>
/// Пассивные партии 13.10 <c>ai-r2a-ai-orders</c> и <c>ai-r2b-ai-orders-lvl10</c> (корпус <c>20261014-replay</c>, разбор
/// <c>.analysis/replay/ai-orders.md</c>): срок жизни призванных и уровень сценария из сохранения. Сверка кандидатов тактики, путей и
/// живого дня по этим партиям — строки <c>20261014-replay</c> в <see cref="AiTacticFromMapHarnessTests"/> и
/// <see cref="PathExpandHarnessTests"/>.
/// </summary>
[Trait("Category", "Corpus")]
public class ReplayOrdersTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<GameBook> Book = new(() => GameBook.Load(Path.Combine(Data, "rules")));

    private static string? Save(string stem, int turn)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20261014-replay");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"{stem}-human-t{turn:D2}-*.sg").Order(StringComparer.Ordinal).LastOrDefault()
            : null;
    }

    private static GameSession Session(MapLooks looks) =>
        new(new GameState(GameInfo.FromMap("replay", looks), looks), SharedContent.Load(Data).MapArt, Book.Value);

    /// <summary>Ent KC0094 с карты (создание 0, <c>LIFE_TIME</c> 1) уходит в начале первого хода Эльфов: у движка после
    /// <c>ap_restore</c> (r2a seq 240630) ни одной строки. Остальные отряды Эльфов и чужие — на месте.</summary>
    [SkippableFact]
    public void Summon_FromMap_LeavesAtOwnerFirstTurnStart()
    {
        string? sg = Save("ai-r2a-ai-orders", 1);
        Skip.If(sg is null, "нет корпуса");
        var looks = MapLooksLoader.Load(sg!);
        var map = looks.Map;
        var ent = map.Stacks["S143KC0094"];
        Assert.Equal(0, map.Units[ent.LeaderId].Creation);

        var after = Session(looks).ExpireSummons(map, ent.Owner);

        Assert.False(after.Stacks.ContainsKey(ent.Id));
        Assert.False(after.Units.ContainsKey(ent.LeaderId));
        Assert.DoesNotContain(after.Plan.Values, ids => ids.Contains(ent.Id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(map.Stacks.Count - 1, after.Stacks.Count);
        // CEffectKillStack без убийцы: запись в уничтоженные (по ней L_KILL_STACK), как у прочих гибелей.
        var destroyed = Assert.Single(after.Destroyed.Except(map.Destroyed));
        Assert.Equal((ent.Id, ""), (destroyed.StackId, destroyed.KillerId));
        // У чужого хода призванный остаётся: уходит только в начале хода хозяина.
        var other = map.Players.Keys.First(p => !string.Equals(p, ent.Owner, StringComparison.OrdinalIgnoreCase));
        Assert.Same(map, Session(looks).ExpireSummons(map, other));
    }

    /// <summary>Призыв Нежити KC00a5 (<c>G000SS0031</c> в день 8, создание 8): в начале её хода дня 9 уходит (r2b seq 2282826), а в
    /// день 8 ещё жив (8 + 1 &gt; 8).</summary>
    [SkippableFact]
    public void Summon_FromSpell_LivesOneDay()
    {
        string? sg = Save("ai-r2b-ai-orders-lvl10", 9);
        Skip.If(sg is null, "нет корпуса");
        var looks = MapLooksLoader.Load(sg!);
        var map = looks.Map;
        var summon = map.Stacks["S143KC00a5"];
        Assert.Equal((8, 9), (map.Units[summon.LeaderId].Creation, map.Turn));

        Assert.False(Session(looks).ExpireSummons(map, summon.Owner).Stacks.ContainsKey(summon.Id));
        var dayEight = GameSession.With(map, turn: 8);
        Assert.True(Session(looks with { Map = dayEight }).ExpireSummons(dayEight, summon.Owner).Stacks.ContainsKey(summon.Id));
    }

    /// <summary>Уровень сценария (<c>SUGG_LVL</c>) читается из <c>.sg</c> и попадает в сведения о партии: r2b — 10, r2a — 1. Прежде
    /// загрузка его теряла (0).</summary>
    [SkippableFact]
    public void SuggestedLevel_ReadFromSave()
    {
        string? a = Save("ai-r2a-ai-orders", 1), b = Save("ai-r2b-ai-orders-lvl10", 1);
        Skip.If(a is null || b is null, "нет корпуса");
        var lvl10 = MapLooksLoader.Load(b!);
        Assert.Equal(10, lvl10.Map.Limits!.SuggestedLevel);
        Assert.Equal(10, GameInfo.FromMap("r2b", lvl10).SuggestedLevel);
        Assert.Equal(1, GameInfo.FromMap("r2a", MapLooksLoader.Load(a!)).SuggestedLevel);
    }
}
