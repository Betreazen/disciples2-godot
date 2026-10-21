using D2.Data.Content;
using D2.Data.Gfx;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Точка «полученные предметы и заклинания» (<see cref="TurnSightKind.ItemsGiven"/>/<see cref="TurnSightKind.SpellsGiven"/>,
/// сообщения 44/43 движка): мешок (и пустой), трофей руины, кража у торговца, кража заклинания у мага; кража предмета из отряда —
/// без точки (<c>CEffectExchange</c>).</summary>
[Trait("Category", "Corpus")]
public class GivenItemsPointTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private sealed class Sights : ITurnObserver
    {
        public List<TurnSight> All { get; } = [];

        public void PlayerTurn(string playerId)
        {
        }

        public void StackMoving(StackMove move)
        {
        }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;

        public void Happened(TurnSight sight) => All.Add(sight);

        public List<TurnSight> Given => [.. All.Where(s => s.Kind is TurnSightKind.ItemsGiven or TurnSightKind.SpellsGiven)];
    }

    private static (GameSession Session, Sights Sights) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var sights = new Sights();
        session.Watch(sights);
        return (session, sights);
    }

    private static MapStack Player(GameSession session) =>
        session.Map.Stacks.Values.OrderBy(s => s.Id).First(s => !AiTactics.NeutralOwner(session, s.Owner) && s.Inside.Length == 0 && s.LeaderAlive);

    [SkippableFact]
    public void StealMerchant_And_StealMage_ReportWhatCame()
    {
        var (session, sights) = Game();
        var thief = Player(session);
        var shop = session.Map.Sites.First(s => s.Kind == SiteKind.Merchant && s.Stock.Any(r => r.Count > 0));
        var report = session.Thief(thief.Id, shop.Id, ThiefAction.StealMerchant, new ScriptedRng([0, 0]), AiTactics.DearestStock(session, shop));
        Assert.True(report.Success);
        var item = Assert.Single(sights.Given);
        Assert.Equal((TurnSightKind.ItemsGiven, thief.Owner, shop.Id), (item.Kind, item.PlayerId, item.Id));
        Assert.Equal([session.Map.Stacks[thief.Id].Items[^1]], item.Ids);

        var mage = session.Map.Sites.FirstOrDefault(s => s.Spells.Count > 0);
        Skip.If(mage is null, "нет мага");
        var known = session.Map.Players[thief.Owner].KnownSpells;
        string? spell = mage!.Spells.FirstOrDefault(sp => !known.Contains(sp, StringComparer.OrdinalIgnoreCase));
        Skip.If(spell is null, "всё известно");
        session.StealFromMage(thief.Owner, mage.Id, spell);
        var given = sights.Given[^1];
        Assert.Equal((TurnSightKind.SpellsGiven, thief.Owner, mage.Id, false), (given.Kind, given.PlayerId, given.Id, given.Library));
        Assert.Equal([spell!.ToUpperInvariant()], given.Ids);
    }

    [SkippableFact]
    public void Bag_EvenEmpty_And_RuinOnlyWithTrophy()
    {
        var (session, sights) = Game();
        var map = session.Map;
        var stack = Player(session);
        var bag = map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Bag && !s.Gone);
        var ruins = map.Sites.Where(s => s.Kind == SiteKind.Ruin && s.LooterId.Length == 0).ToList();
        Skip.If(bag is null || ruins.Count == 0, "нет мешка или руины");
        var empty = ruins[0] with { Guards = [], GuardSlots = [], ItemId = "" };
        var sites = map.Sites.Select(s => s.Id == bag!.Id ? s with { Items = [] } : s.Id == empty.Id ? empty : s).ToList();
        session = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, sites: sites) } },
                                  session.Art, session.Book);
        session.Watch(sights);

        Assert.Null(session.Enter(stack.Id, bag!.Id).Refused);
        var fromBag = Assert.Single(sights.Given);
        Assert.Equal((TurnSightKind.ItemsGiven, stack.Owner, bag.Id), (fromBag.Kind, fromBag.PlayerId, fromBag.Id));
        Assert.Empty(fromBag.Ids);   // пустой мешок — «Ничего не найдено»

        Assert.Null(session.Enter(stack.Id, empty.Id).Refused);
        Assert.Single(sights.Given);   // руина без трофея — сообщения 44 нет

        var trophy = ruins.FirstOrDefault(r => r.ItemId.Length > 0 && r.Id != empty.Id);
        Skip.If(trophy is null, "нет руины с трофеем");
        var cleared = session.Map.Sites.Select(s => s.Id == trophy!.Id ? s with { Guards = [], GuardSlots = [] } : s).ToList();
        session = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(session.Map, sites: cleared) } },
                                  session.Art, session.Book);
        session.Watch(sights);
        Assert.Null(session.Enter(stack.Id, trophy!.Id).Refused);
        var loot = sights.Given[^1];
        Assert.Equal(trophy.Id, loot.Id);
        string instance = Assert.Single(loot.Ids);
        Assert.Equal(trophy.ItemId, session.Map.ItemTypes[instance], StringComparer.OrdinalIgnoreCase);
        Assert.Contains(instance, session.Map.Stacks[stack.Id].Items);
    }

    /// <summary>Подобранный мешок уходит с карты целиком: из плана, из обликов (<see cref="MapLooks.Bags"/>), из картинок сцены и из
    /// записанного сохранения. Прежде облик оставался, и сундук стоял на месте до конца партии (пометка 3 партии 14.10: <c>S001BG000f</c>
    /// у отряда человека, <c>S001BG000d</c> у ИИ).</summary>
    [SkippableFact]
    public void TakenBag_LeavesPlanLooksAndScene()
    {
        var (session, _) = Game();
        var stack = Player(session);
        var bag = session.Map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Bag && !s.Gone);
        Skip.If(bag is null, "нет мешка");
        var content = SharedContent.Load(Data);
        bool Same(string a) => a.Equals(bag!.Id, StringComparison.OrdinalIgnoreCase);
        bool Drawn() => MapScene.Build(session.Looks, content.MapArt, content.Gfx).Sprites.Any(s => Same(s.Source));
        bool drawnBefore = Drawn();
        Assert.Contains(session.Looks.Bags, b => Same(b.Id));

        Assert.Null(session.Enter(stack.Id, bag!.Id).Refused);

        Assert.True(session.Map.Sites.Single(s => Same(s.Id)).Gone);
        Assert.DoesNotContain(session.Map.Plan.Values.SelectMany(ids => ids), Same);
        Assert.DoesNotContain(session.Looks.Bags, b => Same(b.Id));
        Assert.DoesNotContain(GameSave.FromJson(GameSave.ToJson(session.Snapshot())).Looks.Bags, b => Same(b.Id));
        Skip.IfNot(drawnBefore, "нет описи графики");
        Assert.False(Drawn());
    }

    [SkippableFact]
    public void StealItemFromStack_HasNoPoint()
    {
        var (session, sights) = Game();
        var map = session.Map;
        var thief = Player(session);
        var target = map.Stacks.Values.OrderBy(s => s.Id).FirstOrDefault(s => s.Items.Count > 0 && s.Owner != thief.Owner && s.Inside.Length == 0);
        Skip.If(target is null, "нет отряда с предметами");
        session.Thief(thief.Id, target!.Id, ThiefAction.StealItem, new ScriptedRng([0, 0]), target.Items[0]);
        Assert.Empty(sights.Given);
    }
}
