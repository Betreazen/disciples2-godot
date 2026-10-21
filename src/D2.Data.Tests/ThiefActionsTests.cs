using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Действие вора на живой партии (<c>GameSession.Thieves.cs</c>): доступность, бросок, списание хода, гибель вора,
/// разведанные игроки, кража у торговца, союз.</summary>
[Trait("Category", "Corpus")]
public class ThiefActionsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static GameSession Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }

    /// <summary>Вор игрока ИИ и отряд другого играбельного игрока, с которым нет союза и которого вор ещё не разведал.</summary>
    private static (MapStack Thief, MapStack Target) Pair(GameSession session)
    {
        var map = session.Map;
        var thief = map.Stacks.Values.OrderBy(s => s.Id).First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman
                                                                    && !AiTactics.NeutralOwner(session, s.Owner) && s.Inside.Length == 0);
        var target = map.Stacks.Values.OrderBy(s => s.Id).First(s => !s.Owner.Equals(thief.Owner, StringComparison.OrdinalIgnoreCase)
                                                                     && !AiTactics.NeutralOwner(session, s.Owner)
                                                                     && !AiTactics.Allied(session, thief.Owner, s.Owner)
                                                                     && s.Inside.Length == 0 && s.LeaderAlive);
        return (thief, target);
    }

    [SkippableFact]
    public void Spy_Success_MarksOwnerSpied_AndCostsHalfAllowance()
    {
        var session = Game();
        var (thief, target) = Pair(session);
        Assert.Contains(ThiefAction.Spy, session.ThiefActions(thief.Id, target.Id));
        int allowance = session.DayAllowance(thief);

        var report = session.Thief(thief.Id, target.Id, ThiefAction.Spy, new ScriptedRng([10, 20]));

        Assert.Null(report.Refused);
        Assert.True(report.Success);
        Assert.Contains(target.Owner, session.Map.Players[thief.Owner].Spied, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Thieves.Spend(thief.Move, Thieves.MoveCost(allowance)), session.Map.Stacks[thief.Id].Move);
        Assert.DoesNotContain(ThiefAction.Spy, session.ThiefActions(thief.Id, target.Id));
    }

    /// <summary>Осмотр руины: <c>CEffectVisitRuin</c> (vtable <c>0x6cf23c</c>, применение <c>0x43f5fa</c>) создаёт
    /// <c>CVisitorRuinAddVisiter</c> (<c>0x5e7d8c</c> → <c>0x613164</c>, RTTI <c>0x7b1b38</c>; REA 13.10) — игрок вора в списке посетивших руину,
    /// его читает <c>L_VISIT_SITE</c>. Прежде исход уходил в <c>default</c> и ничего не делал (аудит 13.10).</summary>
    [SkippableFact]
    public void SpyRuin_AddsThiefOwnerToRuinVisitors()
    {
        var session = Game();
        var (thief, _) = Pair(session);
        var ruin = session.Map.Sites.First(s => s.Kind == SiteKind.Ruin && s.LooterId.Length == 0
                                                && !s.Visitors.Contains(thief.Owner, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(ThiefAction.SpyRuin, session.ThiefActions(thief.Id, ruin.Id));

        var report = session.Thief(thief.Id, ruin.Id, ThiefAction.SpyRuin, new ScriptedRng([10, 20]));

        Assert.True(report.Success);
        Assert.Equal([.. ruin.Visitors, thief.Owner], session.Map.Sites.First(s => s.Id == ruin.Id).Visitors);
    }

    [SkippableFact]
    public void Failure_KillsTheThief()
    {
        var session = Game();
        var (thief, target) = Pair(session);
        int chance = session.ThiefChance(target.Id, ThiefAction.PoisonStack);
        Assert.True(chance < 99);

        var report = session.Thief(thief.Id, target.Id, ThiefAction.PoisonStack, new ScriptedRng([99, 99]));

        Assert.True(report.ThiefDied);
        Assert.False(session.Map.Stacks.ContainsKey(thief.Id));
        Assert.DoesNotContain(thief.UnitIds[0], session.Map.Units.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void StealMerchant_TakesTheDearestItem()
    {
        var session = Game();
        var (thief, _) = Pair(session);
        var shop = session.Map.Sites.First(s => s.Kind == SiteKind.Merchant && s.Stock.Any(r => r.Count > 0));
        string dearest = AiTactics.DearestStock(session, shop)!;
        long top = shop.Stock.Where(r => r.Count > 0).Max(r => Treasury.ItemBuyPrice(session.Book!.Items[r.Item]).Gold);
        Assert.Equal(top, Treasury.ItemBuyPrice(session.Book!.Items[dearest]).Gold);

        var report = session.Thief(thief.Id, shop.Id, ThiefAction.StealMerchant, new ScriptedRng([0, 0]), dearest);

        Assert.True(report.Success);
        Assert.Equal(dearest.ToUpperInvariant(), report.Taken);
        string stolen = session.Map.Stacks[thief.Id].Items[^1];
        Assert.Equal(dearest, session.Map.ItemTypes[stolen], StringComparer.OrdinalIgnoreCase);
        int before = shop.Stock.Where(r => r.Item.Equals(dearest, StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
        int after = session.Map.Sites.First(s => s.Id == shop.Id).Stock.Where(r => r.Item.Equals(dearest, StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
        Assert.Equal(before - 1, after);
    }

    /// <summary>Вход <c>0x5d32b8</c>: против отряда союзника (<c>0x5f7f5a</c>) действий нет вовсе.</summary>
    [SkippableFact]
    public void Ally_HasNoActions()
    {
        var session = Game();
        var (thief, target) = Pair(session);
        var book = session.Book!;
        var map = session.Map;
        int a = book.Economy.Races[map.Players[thief.Owner].RaceId].RaceType, b = book.Economy.Races[map.Players[target.Owner].RaceId].RaceType;
        var relations = map.Relations.Select(r => r.Pairs(a, b) ? r with { Raw = r.Raw | 1u } : r).ToList();
        var allied = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, relations: relations) } },
                                     session.Art, book);

        Assert.True(AiTactics.Allied(allied, thief.Owner, target.Owner));
        Assert.Empty(allied.ThiefActions(thief.Id, target.Id));
    }
    /// <summary>Убийство (<c>0x41d926</c>/<c>0x41dae2</c>): жертва — живой не-лидер с наименьшим <c>XP_KILLED</c>; у отряда с одним живым —
    /// гибель отряда.</summary>
    [SkippableFact]
    public void Assassinate_KillsWeakestNonLeader_OrTheLoneStack()
    {
        var session = Game();
        var book = session.Book!;
        var map = session.Map;
        var target = map.Stacks.Values.OrderBy(s => s.Id).First(s => s.UnitIds.Count(id => map.Units[id].Hp > 0 && id != s.LeaderId) >= 2);
        string want = target.UnitIds.Where(id => id != target.LeaderId && map.Units[id].Hp > 0)
                                    .MinBy(id => book.Grown(map.Units[id].TypeId, map.Units[id].Level)!.Type.XpKilled)!;
        Assert.Equal(want, session.Assassinate(target.Id));
        Assert.Equal(0, session.Map.Units[want].Hp);
        Assert.True(session.Map.Stacks.ContainsKey(target.Id));

        var lone = session.Map.Stacks.Values.OrderBy(s => s.Id).First(s => s.UnitIds.Count(id => session.Map.Units[id].Hp > 0) == 1);
        session.Assassinate(lone.Id);
        Assert.False(session.Map.Stacks.ContainsKey(lone.Id));
    }

    /// <summary>Убийство последнего стража деревни (генератор захвата <c>0x440ee0</c>: <c>CEffectCapture</c> и <c>CEffectEnterCity</c>):
    /// гарнизон снимается, но деревню вор не берёт — убийство зовёт генератор с флагом 0 (<c>0x41db93</c>: <c>push ebx</c>, <c>ebx</c> = 0 с
    /// <c>0x41daee</c>; флаг 1 — только у подкупа, <c>0x41dd00</c>), и <c>CEffectCapture</c> (<c>0x433e2e</c>) отклоняет лидера
    /// <c>IUsNoble</c> (<c>0x41b399</c>); <c>CEffectEnterCity</c> при чужом хозяине ничего не делает. Хозяину вора — X005TA0458. До 16.10
    /// порт отдавал деревню вору.</summary>
    [SkippableFact]
    public void Assassinate_LastGuard_ThiefDoesNotTakeVillage()
    {
        var session = Game();
        var map = session.Map;
        var (thief, _) = Pair(session);
        var village = map.Sites.OrderBy(s => s.Id).FirstOrDefault(s => s.Kind == SiteKind.Village && s.StackId.Length == 0
            && !s.Owner.Equals(thief.Owner, StringComparison.OrdinalIgnoreCase)
            && s.Guards.Count(id => map.Units.TryGetValue(id, out var u) && u.Hp > 0) >= 1);
        Skip.If(village is null, "нет чужой деревни со стражей");
        var alive = village!.Guards.Where(id => map.Units[id].Hp > 0).ToList();
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string id in alive.Skip(1)) units[id] = units[id] with { Hp = 0 };
        // Во главе — настоящий вор (Gunits.UNIT_CAT 1, IUsNoble): Pair берёт первый отряд ИИ, а не вора.
        Assert.Equal(D2.Rules.Battle.UnitCategory.Noble, session.Book!.CategoryOf(ThiefType));
        units[thief.LeaderId] = units[thief.LeaderId] with { TypeId = ThiefType };
        var at = AiTactics.Entrance(village);
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[thief.Id] = thief with { X = at.X - 1, Y = at.Y, Inside = "" };
        session = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, units: units, stacks: stacks) } },
                                  session.Art, session.Book);

        var sights = new List<TurnSight>();
        session.Watch(new SightLog(sights));
        Assert.Equal(alive[0], session.Assassinate(village.Id, thief.Id));
        var after = session.Map.Sites.First(s => s.Id == village.Id);
        Assert.Equal(village.Owner, after.Owner, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("", after.StackId);
        Assert.Empty(after.Guards);
        Assert.Equal("", session.Map.Stacks[thief.Id].Inside);
        Assert.Contains(sights, s => s.Kind == TurnSightKind.CaptureRefused && s.Id == village.Id && s.PlayerId == thief.Owner);
    }

    /// <summary>Вор Империи (<c>G000UU0023</c>).</summary>
    private const string ThiefType = "G000UU0023";

    private sealed class SightLog(List<TurnSight> sights) : ITurnObserver
    {
        public void PlayerTurn(string playerId) { }

        public void StackMoving(StackMove move) { }

        public IReadOnlyList<D2.Rules.Battle.TurnRecord>? Battle(D2.Rules.Battle.BattleState state) => null;

        public void Happened(TurnSight sight) => sights.Add(sight);
    }

    /// <summary>Подложный приказ (<c>CEffectMisfit</c> <c>0x4392e2</c>): позиции перемешиваются R250(i), i = 1…5, юнит i встаёт в клетку
    /// i-й позиции.</summary>
    [SkippableFact]
    public void Misfit_ShufflesPositions()
    {
        var session = Game();
        var map = session.Map;
        var target = map.Stacks.Values.OrderBy(s => s.Id).First(s => s.UnitIds.Count >= 2 && s.UnitIds.All(id => session.Book!.Rules.Units.TryGetValue(
                                                                          session.Book!.Grown(map.Units[id].TypeId, map.Units[id].Level)!.Type.Id, out var t) && t.SizeSmall));
        var rng = new ScriptedRng([0, 0, 0, 0, 0]);   // i=1: swap(1,0); i=2: swap(2,0); … → [5,0,1,2,3,4]
        session.Misfit(target.Id, rng);
        Assert.Equal(5, rng.Used);
        var cells = UnitCells.Of(session.Map.Stacks[target.Id].UnitIds, session.Map.Stacks[target.Id].Slots);
        int[] perm = [5, 0, 1, 2, 3, 4];
        // Второй юнит встаёт в клетку 0, и позже в неё никто не переставляется (юниты 2…5 идут в 1…4).
        Assert.Equal(target.UnitIds[1], cells.Cells[perm[1]]);
    }

    /// <summary>Кража у мага (<c>CEffectStealMage</c>, <c>CEffectGiveSpell</c>): заклинание уходит из лавки и становится известным вору.</summary>
    [SkippableFact]
    public void StealMage_MovesSpellToThief()
    {
        var session = Game();
        var (thief, _) = Pair(session);
        var mage = session.Map.Sites.FirstOrDefault(s => s.Spells.Count > 0);
        Skip.If(mage is null, "нет мага");
        var known = session.Map.Players[thief.Owner].KnownSpells;
        string? spell = mage!.Spells.FirstOrDefault(sp => !known.Contains(sp, StringComparer.OrdinalIgnoreCase));
        Skip.If(spell is null, "всё известно");
        Assert.Equal(spell!.ToUpperInvariant(), session.StealFromMage(thief.Owner, mage.Id, spell));
        Assert.DoesNotContain(spell, session.Map.Sites.First(s => s.Id == mage.Id).Spells, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(spell, session.Map.Players[thief.Owner].KnownSpells, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>И45: «что украсть из заклинаний» (<c>0x5cdde7</c>) — известные владельцу крепости, кроме призывов (<c>0x5cdf6f</c>:
    /// категория не <c>L_SUMMON</c>) и известных вору. Один список у признака доступности, окна выбора (<c>0x4b935b</c>) и кражи
    /// (<c>0x41d786</c>: выбранного нет в списке — кражи нет). У владельца одни призывы — красть нечего.</summary>
    [SkippableFact]
    public void StealSpell_OffersNoSummons()
    {
        var session = Game();
        var book = session.RequireBook();
        var (thief, _) = Pair(session);
        var fort = session.Map.Sites.FirstOrDefault(s => s.Kind is SiteKind.Capital or SiteKind.Village && s.StackId.Length == 0
                                                         && !s.Owner.Equals(thief.Owner, StringComparison.OrdinalIgnoreCase)
                                                         && session.Map.Players.ContainsKey(s.Owner) && !AiTactics.NeutralOwner(session, s.Owner)
                                                         && !AiTactics.Allied(session, thief.Owner, s.Owner));
        Skip.If(fort is null, "нет чужой крепости без гостя");
        string summon = book.Spells.Values.First(s => s.Category == D2.Rules.Magic.SpellCategory.Summon).Id.ToUpperInvariant();
        string other = book.Spells.Values.First(s => s.Category == D2.Rules.Magic.SpellCategory.Attack).Id.ToUpperInvariant();

        // Лорд-Дипломат (гильдия) у хозяина вора — иначе кражи заклинания в списке нет.
        const string diplomat = "G000LR0002";   // Glord: CATEGORY 2 — гильдмастер
        Assert.Equal((int)LordCategory.Diplomat, book.LordCategory(diplomat));
        var mixed = Knowing(session, fort!.Owner, [summon, other], thief.Owner, diplomat);
        Assert.Equal([other], mixed.StealableSpells(thief.Owner, fort.Owner));
        Assert.True(mixed.ThiefTarget(thief.Owner, fort.Id)!.HasSpellToSteal);
        var context = new D2.Data.Ui.Windows.WindowContext(mixed, thief.Owner, D2.Data.Ui.UiStrings.Load(Path.Combine(Data, "ui"), "ru"),
                                                           D2.Data.Ui.Windows.UnitFaces.Load(Path.Combine(Data, "rules")));
        var steal = new D2.Data.Ui.Windows.StealLogic(context, thief.Id, fort.Id, ThiefAction.StealSpell, _ => { });
        Assert.Equal([other], steal.Choices.Select(c => c.Pick));
        Assert.Contains(ThiefAction.StealSpell, mixed.ThiefActions(thief.Id, fort.Id));
        var report = mixed.Thief(thief.Id, fort.Id, ThiefAction.StealSpell, new ScriptedRng([0, 0, 0, 0]), pick: summon);
        Assert.True(report.Success);   // бросок 0 — успех
        Assert.Equal("", report.Taken);   // призыва нет в списке 0x5cdde7 — кражи нет (0x41d786)
        Assert.DoesNotContain(summon, mixed.Map.Players[thief.Owner].KnownSpells, StringComparer.OrdinalIgnoreCase);

        var summonsOnly = Knowing(session, fort.Owner, [summon], thief.Owner, diplomat);
        Assert.Empty(summonsOnly.StealableSpells(thief.Owner, fort.Owner));
        Assert.False(summonsOnly.ThiefTarget(thief.Owner, fort.Id)!.HasSpellToSteal);
        Assert.DoesNotContain(ThiefAction.StealSpell, summonsOnly.ThiefActions(thief.Id, fort.Id));
    }

    /// <summary>Партия, где <paramref name="ownerId"/> знает ровно <paramref name="spells"/>, а <paramref name="thiefOwner"/> — ничего
    /// и его лорд — <paramref name="lordId"/>.</summary>
    private static GameSession Knowing(GameSession session, string ownerId, string[] spells, string thiefOwner, string lordId)
    {
        var map = session.Map;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[ownerId] = players[ownerId] with { KnownSpells = spells };
        players[thiefOwner] = players[thiefOwner] with { KnownSpells = [], LordId = lordId };
        map = GameSession.With(map, players: players);
        return new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.RequireBook());
    }
}
