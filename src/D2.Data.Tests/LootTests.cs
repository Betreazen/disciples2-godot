using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Вещи погибшего отряда и взятой столицы (<c>GameSession.Loot.cs</c>, <c>CEffectKillStack</c> <c>0x43895c</c>,
/// <c>CEffectCaptureCapital</c> <c>0x43496a</c>): победителю — в конец сумки в прежнем порядке, без убийцы — мешком на клетке;
/// склад столицы — захватившему отряду, заклинания — все недостающие одним броском.</summary>
[Trait("Category", "Corpus")]
public class LootTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private sealed class Sights : ITurnObserver
    {
        public List<TurnSight> All { get; } = [];
        public void PlayerTurn(string playerId) { }
        public void StackMoving(StackMove move) { }
        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;
        public void Happened(TurnSight sight) => All.Add(sight);
    }

    private static GameSession Game(string save = "rastlennaya-vlast-dwarf-t24-045.sg")
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", save);
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }

    private static GameSession WithMap(GameSession s, MapState map) =>
        new(s.State with { Looks = s.Looks with { Map = map } }, s.Art, s.Book);

    /// <summary>Отряду <paramref name="victim"/> — предметы другого отряда (в сумку, не надетыми).</summary>
    private static (GameSession Session, IReadOnlyList<string> Items) Give(GameSession s, string victim)
    {
        var map = s.Map;
        var donor = map.Stacks.Values.OrderBy(x => x.Id).First(x => x.Items.Count >= 2 && x.Id != victim);
        var items = donor.Items.Take(2).ToList();
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[donor.Id] = donor with { Items = [.. donor.Items.Skip(2)], Equipment = [] };
        stacks[victim] = stacks[victim] with { Items = items };
        return (WithMap(s, GameSession.With(map, stacks: stacks)), items);
    }

    [SkippableFact]
    public void Assassinate_LoneStack_ItemsToThief_AtEndInOrder_AndPoint()
    {
        var session = Game();
        var map = session.Map;
        var lone = map.Stacks.Values.OrderBy(s => s.Id).First(s => s.UnitIds.Count(id => map.Units[id].Hp > 0) == 1 && s.Inside.Length == 0);
        var thief = map.Stacks.Values.OrderBy(s => s.Id).First(s => s.Owner != lone.Owner && !AiTactics.NeutralOwner(session, s.Owner) && s.Id != lone.Id);
        var (given, items) = Give(session, lone.Id);
        var sights = new Sights();
        given.Watch(sights);
        var before = given.Map.Stacks[thief.Id].Items;

        given.Assassinate(lone.Id, thief.Id);

        Assert.False(given.Map.Stacks.ContainsKey(lone.Id));
        Assert.Equal([.. before, .. items], given.Map.Stacks[thief.Id].Items);
        var point = Assert.Single(sights.All, s => s.Kind == TurnSightKind.ItemsGiven);
        Assert.Equal((thief.Owner, thief.Id), (point.PlayerId, point.Id));
        Assert.Equal(items, point.Ids);
    }

    /// <summary>Вор погиб о неподкупного (<c>0x442f00</c>): убийца — отряд-цель, вещи вора — в конец его сумки, сообщение 44 — его хозяину.</summary>
    [SkippableFact]
    public void ThiefIncorruptible_ItemsGoToTheTargetStack()
    {
        var session = Game();
        var map = session.Map;
        var target = map.Stacks.Values.OrderBy(s => s.Id).First(s => !AiTactics.NeutralOwner(session, s.Owner) && s.Inside.Length == 0);
        var thief = map.Stacks.Values.OrderBy(s => s.Id).First(s => s.Owner != target.Owner && s.Id != target.Id);
        var (given, items) = Give(session, thief.Id);
        var incorruptible = given.Book!.Rules.Modifiers!.Values.First(m => m.Elements.Any(e => e.Type == D2.Rules.Units.ModifierElementType.LeaderAbility
                                                                                            && e.Ability == D2.Rules.Map.Thieves.IncorruptibleAbility));
        var units = given.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var leader = units[target.LeaderId];
        units[target.LeaderId] = leader with { Modifiers = [.. leader.Modifiers, incorruptible.Id] };
        var game = WithMap(given, GameSession.With(given.Map, units: units));
        var sights = new Sights();
        game.Watch(sights);
        var before = game.Map.Stacks[target.Id].Items;

        Assert.True(game.ThiefIncorruptible(thief.Id, target.Id));

        Assert.False(game.Map.Stacks.ContainsKey(thief.Id));
        Assert.Equal([.. before, .. items], game.Map.Stacks[target.Id].Items);
        var point = Assert.Single(sights.All, s => s.Kind == TurnSightKind.ItemsGiven);
        Assert.Equal((target.Owner, target.Id), (point.PlayerId, point.Id));
    }

    [SkippableFact]
    public void EventRemoval_NoKiller_ItemsStayAsBagOnTheCell_NoPoint()
    {
        var session = Game();
        var victim = session.Map.Stacks.Values.OrderBy(s => s.Id).First(s => s.Inside.Length == 0);
        var (given, items) = Give(session, victim.Id);
        var sights = new Sights();
        given.Watch(sights);
        int bags = given.Looks.Bags.Count;

        given.RemoveStack(victim.Id, killer: "");

        Assert.False(given.Map.Stacks.ContainsKey(victim.Id));
        var bag = given.Map.Sites.Single(s => s.Kind == SiteKind.Bag && s.X == victim.X && s.Y == victim.Y && s.Items.SequenceEqual(items));
        Assert.Contains(bag.Id, given.Map.Plan[(victim.X, victim.Y)]);
        Assert.Equal(bags + 1, given.Looks.Bags.Count);
        Assert.DoesNotContain(sights.All, s => s.Kind == TurnSightKind.ItemsGiven);
    }

    [SkippableFact]
    public void Battle_LoserItemsGoToTheWinningStack()
    {
        var session = Game();
        var map = session.Map;
        var book = session.RequireBook();
        // Сильный отряд против отряда из одного живого воина рядом не нужен: бой собирается по id.
        var strong = map.Stacks.Values.Where(s => !AiTactics.NeutralOwner(session, s.Owner) && s.Inside.Length == 0)
                                      .OrderByDescending(s => s.UnitIds.Sum(id => map.Units[id].Hp)).First();
        var weak = map.Stacks.Values.Where(s => s.Owner != strong.Owner && s.Inside.Length == 0 && s.UnitIds.Count(id => map.Units[id].Hp > 0) == 1)
                                    .OrderBy(s => s.UnitIds.Sum(id => map.Units[id].Hp)).First();
        var (given, items) = Give(session, weak.Id);
        var before = given.Map.Stacks[strong.Id].Items;
        var state = given.StartBattle(strong.Id, weak.Id, duel: true);
        var battle = new D2.Data.Battle.BattleSession(state, new BattleSim(state, given.Rng, book.Rules.Constants), book.Rules.Constants) { Auto = true };
        battle.PlayOut();
        given.FinishBattle(battle.Log);

        Skip.If(given.Map.Stacks.ContainsKey(weak.Id), "слабый отряд выжил");
        Assert.Equal([.. before, .. items], given.Map.Stacks[strong.Id].Items);
    }

    [SkippableFact]
    public void CaptureCapital_StoreToTheStack_SpellsAllOrNothing()
    {
        var session = Game();
        var map = session.Map;
        var capital = map.Sites.First(s => s.Kind == SiteKind.Capital && !AiTactics.NeutralOwner(session, s.Owner) && !map.Players[s.Owner].IsHuman);
        var stack = map.Stacks.Values.OrderBy(s => s.Id).First(s => s.Owner != capital.Owner && !AiTactics.NeutralOwner(session, s.Owner)
                                                                    && s.Inside.Length == 0 && s.LeaderAlive);
        var store = map.Stacks.Values.First(s => s.Items.Count >= 2 && s.Id != stack.Id).Items.Take(2).ToList();
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value.Items.Intersect(store).Any() ? p.Value with { Items = [.. p.Value.Items.Except(store)], Equipment = [] } : p.Value,
                                             StringComparer.OrdinalIgnoreCase);
        var sites = map.Sites.Select(s => s.Id == capital.Id ? s with { Guards = [], GuardSlots = [], StackId = "", Items = store } : s).ToList();
        var fresh = WithMap(session, GameSession.With(map, stacks: stacks, sites: sites));
        var sights = new Sights();
        fresh.Watch(sights);
        var missing = map.Players[capital.Owner].KnownSpells.Except(map.Players[stack.Owner].KnownSpells, StringComparer.OrdinalIgnoreCase).ToList();
        var before = fresh.Map.Stacks[stack.Id].Items;

        var report = fresh.Enter(stack.Id, capital.Id);

        Assert.Null(report.Refused);
        Skip.If(report.Victory, "взятие столицы кончило партию");
        Assert.Equal([.. before, .. store], fresh.Map.Stacks[stack.Id].Items);
        Assert.Empty(fresh.Map.Sites.First(s => s.Id == capital.Id).Items);
        Assert.Equal(store, sights.All.Single(s => s.Kind == TurnSightKind.ItemsGiven).Ids);
        var known = fresh.Map.Players[stack.Owner].KnownSpells;
        int learned = missing.Count(sp => known.Contains(sp, StringComparer.OrdinalIgnoreCase));
        Assert.True(learned == 0 || learned == missing.Count, $"узнано {learned} из {missing.Count}");
        if (learned > 0) Assert.True(sights.All.Single(s => s.Kind == TurnSightKind.SpellsGiven).Library);
    }
}
