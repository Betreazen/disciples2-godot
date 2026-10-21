using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Наблюдатель хода видит отряд, созданный событием (<c>L_CREATE_STACK</c>), и смену владельца отряда (<c>L_CHANGE_OWNER</c>) —
/// точки расширения в эффектах событий для показа (<c>docs/ai-screen.md</c> §8); сами эффекты те же, что без наблюдателя.</summary>
[Trait("Category", "Corpus")]
public class TurnObserverEventsTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private sealed class Sights : ITurnObserver
    {
        public readonly List<TurnSight> Seen = [];
        public void PlayerTurn(string playerId) { }
        public void StackMoving(StackMove move) { }
        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;
        public void Happened(TurnSight sight) => Seen.Add(sight);
    }

    /// <summary>Действия в окнах сообщаются наблюдателю для звука (<see cref="TurnSightKind.Built"/>, <see cref="TurnSightKind.Bought"/>),
    /// только при успехе: сохранение человека дня 8, столица Кланов — постройка и найм воина в гарнизон; повторная постройка в тот же
    /// день — отказ без уведомления.</summary>
    [SkippableFact]
    public void WindowActions_AreSeen_OnlyOnSuccess()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t08-018.sg");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var players = looks.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[human.Id] = human with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };   // казна — не помеха
        looks = looks with { Map = GameSession.With(looks.Map, players: players) };
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var sights = new Sights();
        session.Observer = sights;
        var player = session.Map.Players[human.Id];
        string? built = session.Buildable(player.Id).FirstOrDefault(b => session.Build(player.Id, b) == BuildStatus.CanBeBuilt);
        Skip.If(built is null, "строить нечего");
        Assert.Equal(built, Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.Built).Id, StringComparer.OrdinalIgnoreCase);
        foreach (string again in session.Buildable(player.Id)) session.Build(player.Id, again);   // в тот же день — отказ
        Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.Built);

        var capital = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == player.Id);
        string? hired = Economy.HireList(session.Map, player.Id, session.Book!.Economy)
            .FirstOrDefault(t => session.HireUnit(capital.Id, t) is null);
        if (hired is not null) Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.Bought);
        output.WriteLine($"построено {built}, нанят {hired ?? "-"}");
    }

    /// <summary>«A Return To Simpler Times»: событие начала хода создаёт отряд из шаблона <c>S001TM0000</c> — наблюдатель получает
    /// <see cref="TurnSightKind.Created"/> с его клеткой.</summary>
    [SkippableFact]
    public void CreateStack_IsSeen()
    {
        string path = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "'A Return To Simpler Times'");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var data = ScenarioFile.Read(path);
        bool[] all = [true, true, true, true, true, true];
        var events = data.Events with
        {
            Events = [new ScenarioEvent("S999EV0000", true, true, 100, -1, all, [new FrequencyCondition(1)],
                                        [new CreateStackEffect("S001TM0000", "S001LO0009")], "проверка", all)],
        };
        var looks = data.Looks with { Map = GameSession.With(data.Looks.Map, turn: 1) };
        var state = new GameState(new GameInfo("x", "x", ["S001PL0001"], 1), looks) { Events = events, EventProgress = EventProgress.From(events) };
        var session = new GameSession(state, SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        var sights = new Sights();
        session.Observer = sights;
        session.RunEvents("S001PL0001", "");
        var created = Assert.Single(session.Map.Stacks.Values, s => s.TemplateId == "S001TM0000");
        var seen = Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.Created);
        Assert.Equal((created.Id, created.X, created.Y, created.Owner), (seen.Id, seen.X, seen.Y, seen.PlayerId));
    }

    /// <summary>Корпус human, день 46: шаг отряда Кланов в 67,48 — событие <c>EV001b</c> передаёт нейтральный <c>KC0048</c> Кланам;
    /// наблюдатель получает <see cref="TurnSightKind.OwnerChanged"/> с новым владельцем.</summary>
    [SkippableFact]
    public void ChangeOwner_IsSeen()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t46-079.sg");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        var events = EventLoader.Load(path);
        var state = new GameState(GameInfo.FromMap("x", looks), looks) { Events = events, EventProgress = EventProgress.From(events) };
        var session = new GameSession(state, SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        var sights = new Sights();
        session.Observer = sights;
        var dwarf = session.Map.Stacks.Values.First(s => s.Owner.Equals("S073PL000a", StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        output.WriteLine($"отряд Кланов {dwarf.Id} в {dwarf.X},{dwarf.Y}");
        session.MoveStack(dwarf.Id, 67, 48, 0, 0);
        var seen = Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.OwnerChanged);
        Assert.Equal("S073KC0048", seen.Id.ToUpperInvariant());
        Assert.Equal("S073PL000A", seen.PlayerId.ToUpperInvariant());
    }

    /// <summary>Сохранение человека дня 5 (Кланы, Power to Corrupt) с наблюдателем.</summary>
    private static (GameSession Session, MapPlayer Human, Sights Sights) Day5()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var players = looks.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[human.Id] = human with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        looks = looks with { Map = GameSession.With(looks.Map, players: players) };
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var sights = new Sights();
        session.Observer = sights;
        return (session, human, sights);
    }

    /// <summary>Точки для звука сессии Г (<c>docs/ai-screen.md</c> §11): рост деревни (<c>CITYGROW</c>) и разграбление руины
    /// (<c>LOOTRUIN</c>) — по одному уведомлению с клеткой и объектом, только при успехе; модель та же.</summary>
    [SkippableFact]
    public void CityGrown_And_RuinLooted_AreSeen()
    {
        var (session, human, sights) = Day5();
        var any = session.Map.Sites.First(s => s.Kind == SiteKind.Village);
        var village = any with { Owner = human.Id, Size = 1, RiotTurn = 0, GrowthTurn = 0 };
        var ruin = session.Map.Sites.First(s => s.Kind == SiteKind.Ruin && s.LooterId.Length == 0);
        var map = GameSession.With(session.Map, sites: [.. session.Map.Sites.Select(s => s.Id == any.Id ? village
            : s.Id == ruin.Id ? s with { Guards = [], GuardSlots = [] } : s)]);
        var game = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book) { Observer = sights };

        Assert.Equal(CityGrowCheck.Allowed, game.GrowCity(village.Id));
        Assert.Equal(CityGrowCheck.GrewToday, game.GrowCity(village.Id));
        var grown = Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.CityGrown);
        Assert.Equal((village.Id, village.X, village.Y, human.Id), (grown.Id, grown.X, grown.Y, grown.PlayerId));

        var stack = game.Map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0).First();
        Assert.Null(game.Enter(stack.Id, ruin.Id).Refused);
        Assert.NotNull(game.Enter(stack.Id, ruin.Id).Refused);   // второй раз — разграблена, уведомления нет
        var looted = Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.Looted);
        Assert.Equal((ruin.Id, ruin.X, ruin.Y, human.Id), (looted.Id, looted.X, looted.Y, looted.PlayerId));
        Assert.Equal(ruin.Cash, looted.Bank);   // золото руины — окну «Вы победили!» (CCmdLootRuinMsg)
    }

    /// <summary>Растекание земли в начале хода (<c>CMidAnim2Terrain</c>, <c>OCCUPY</c>): у каждого уведомления — клетки, которые уже
    /// в земле его игрока, и они же группами по источникам (без повторов, в пределах клеток); точка показа — место единственного
    /// источника или столица игрока; день проходит так же, как без наблюдателя.</summary>
    [SkippableFact]
    public void LandSpread_IsSeen_WithCells()
    {
        var (session, _, sights) = Day5();
        session.EndTurn();
        var spread = sights.Seen.Where(s => s.Kind == TurnSightKind.LandSpread).ToList();
        output.WriteLine($"растеканий {spread.Count}: {string.Join(", ", spread.Select(s =>
            $"{s.PlayerId}:{s.Cells.Count}[{string.Join("+", s.Groups.Select(g => g.Count))}]@{s.X},{s.Y}"))}");
        Assert.NotEmpty(spread);
        var races = session.Book!.Economy.Races;
        foreach (var sight in spread)
        {
            int terrain = Economy.TerrainOfRace(races[session.Map.Players[sight.PlayerId].RaceId].RaceType);
            Assert.NotEmpty(sight.Cells);
            Assert.All(sight.Cells, c => Assert.Equal(terrain, session.Map.Tiles[c.X, c.Y].Terrain));
            var grouped = sight.Groups.SelectMany(g => g).ToList();
            Assert.NotEmpty(grouped);
            Assert.Equal(grouped.Count, grouped.Distinct().Count());
            Assert.Subset(sight.Cells.ToHashSet(), grouped.ToHashSet());
            if (sight.Groups.Count > 1)
            {
                var capital = session.Map.Sites.Single(s => s.Kind == SiteKind.Capital && s.Owner == sight.PlayerId);
                Assert.Equal((capital.X, capital.Y), (sight.X, sight.Y));
            }
        }
    }
}
