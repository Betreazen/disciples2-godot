using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Укрепления и руины в игровом цикле: кто защищается, броня укрепления, захват деревни и разграбление руины
/// (<see cref="MapBattle"/>, <see cref="GameSession.Enter"/>).</summary>
[Trait("Category", "Corpus")]
public class SiegeTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static (GameSession Session, MapStack Stack) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var stack = looks.Map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0)
                                          .OrderByDescending(s => s.UnitIds.Count).First();
        return (session, stack);
    }

    /// <summary>Чужая деревня без визитёра: защищается гарнизон (группа <c>FT</c>) с бронёй укрепления, бежать нельзя.</summary>
    [SkippableFact]
    public void Village_GarrisonDefends_WithFortArmor()
    {
        var (session, stack) = Game();
        var map = session.Map;
        var village = map.Sites.First(s => s.Kind == SiteKind.Village && s.Owner != stack.Owner && s.StackId.Length == 0
                                           && s.Guards.Any(id => map.Units[id].Hp > 0));
        Assert.Equal(village.Id, MapBattle.DefenderOf(map, village.Id));
        var battle = MapBattle.Build(map, stack.Id, village.Id, session.Book!, 1, 0);
        var defenders = battle.Units.Where(u => !u.Attacker).ToList();
        output.WriteLine($"{village.Id} ур.{village.Size}: защитников {defenders.Count}, броня {defenders[0].FortificationArmor}");
        Assert.Equal(village.Guards.Count(id => map.Units[id].Hp > 0), defenders.Count);
        Assert.All(defenders, u => Assert.True(u.FortificationArmor >= 10));
        Assert.All(battle.Units.Where(u => u.Attacker), u => Assert.Equal(0, u.FortificationArmor));
        Assert.False(battle.DefendersCanRetreat);
        Assert.Equal("CITY", MapBattle.PlaceOf(map, village.Id));
    }

    /// <summary>Пустая чужая деревня берётся заходом: владелец и подраса — отряда, он встаёт в неё визитёром.</summary>
    [SkippableFact]
    public void EmptyVillage_IsCapturedByEntering()
    {
        var (session, stack) = Game();
        var map = session.Map;
        var village = map.Sites.First(s => s.Kind == SiteKind.Village && s.Owner != stack.Owner && s.StackId.Length == 0);
        var cleared = GameSession.With(map, sites: [.. map.Sites.Select(s => s.Id == village.Id ? s with { Guards = [], GuardSlots = [] } : s)]);
        var empty = new GameSession(session.State with { Looks = session.Looks with { Map = cleared } }, session.Art, session.Book);
        Assert.Null(MapBattle.DefenderOf(empty.Map, village.Id));

        var report = empty.Enter(stack.Id, village.Id);

        Assert.Null(report.Refused);
        Assert.True(report.Captured);
        var after = empty.Map.Sites.First(s => s.Id == village.Id);
        Assert.Equal((stack.Owner, stack.Id), (after.Owner, after.StackId));
        Assert.Equal((village.X, village.Y, village.Id), (empty.Map.Stacks[stack.Id].X, empty.Map.Stacks[stack.Id].Y, empty.Map.Stacks[stack.Id].Inside));
        Assert.Equal(stack.Owner, empty.Looks.Forts.First(f => f.Id == village.Id).Owner);
    }

    /// <summary>Неохраняемая неразграбленная руина: добыча в казну владельца, предмет в отряд, руина помечена.</summary>
    [SkippableFact]
    public void UnguardedRuin_GivesLoot()
    {
        var (session, stack) = Game();
        var map = session.Map;
        var ruin = map.Sites.First(s => s.Kind == SiteKind.Ruin && s.LooterId.Length == 0);
        Assert.Equal(ruin.Guards.Count > 0 ? ruin.Id : null, MapBattle.DefenderOf(map, ruin.Id));
        var cleared = GameSession.With(map, sites: [.. map.Sites.Select(s => s.Id == ruin.Id ? s with { Guards = [], GuardSlots = [] } : s)]);
        var game = new GameSession(session.State with { Looks = session.Looks with { Map = cleared } }, session.Art, session.Book);
        var bank = game.Map.Players[stack.Owner].Bank;

        var report = game.Enter(stack.Id, ruin.Id);

        output.WriteLine($"{ruin.Id}: добыча {ruin.Cash}, предмет {ruin.ItemId}");
        Assert.Null(report.Refused);
        Assert.Equal(Economy.Clamp(bank + ruin.Cash), game.Map.Players[stack.Owner].Bank);
        Assert.Equal(stack.Owner, game.Map.Sites.First(s => s.Id == ruin.Id).LooterId);
        if (ruin.ItemId.Length > 0)
        {
            string instance = game.Map.Stacks[stack.Id].Items[^1];
            Assert.Equal(ruin.ItemId, game.Map.ItemTypes[instance], ignoreCase: true);
            Assert.DoesNotContain(instance, map.ItemTypes.Keys);
        }
        Assert.True(game.Looks.Ruins.First(r => r.Id == ruin.Id).Looted);
        Assert.NotNull(game.Enter(stack.Id, ruin.Id).Refused);   // второй раз — уже разграблена
    }

    /// <summary>Путь к чужой деревне и к руине кончается у входа (правый нижний угол пятна) — клетку объекта
    /// искатель пути снимает, как клетку врага: живая партия, отряды человека.</summary>
    [SkippableFact]
    public void PathToSites_EndsAtTheEntrance()
    {
        var (session, _) = Game();
        var map = session.Map;
        var opt = new MoveOptions(true, false, false, false, false, false);
        var human = map.Players.Values.First(p => p.IsHuman);
        int found = 0;
        foreach (var stack in map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive))
            foreach (var site in map.Sites.Where(s => s.Kind is SiteKind.Village or SiteKind.Ruin && s.Owner != human.Id))
            {
                var entrance = PathFinder.EntranceOf(map, site.Id)!.Value;
                var f = PathFinder.Find(map, stack, site.X, site.Y, opt, MovementRules.Vanilla, 20);
                if (f.Points.Count == 0) continue;
                found++;
                Assert.True(Math.Abs(f.Points[^1].X - entrance.X) <= 1 && Math.Abs(f.Points[^1].Y - entrance.Y) <= 1,
                            $"{stack.Id} → {site.Id}: конец ({f.Points[^1].X},{f.Points[^1].Y}), вход {entrance}");
            }
        output.WriteLine($"путей к деревням и руинам: {found}");
        Assert.True(found > 0);
    }
}
