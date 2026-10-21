using System.Text.Json;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>Карта 6 <c>port-synth-6-actions</c> (<c>tools/synth-maps/maps_actions.py</c>): действия человека, которых нет в журналах
/// корпуса, — каждое проходит в модели порта на полигоне карты, а зонд порта получает строку, по которой разбор закрывает вопрос
/// (<c>sight</c> <c>Dismissed</c>, <c>BagDropped</c>, <c>Sold</c>, <c>Trained</c>, <c>Bought</c>, <c>Healed</c>, <c>Revived</c>, <c>ItemsGiven</c>,
/// <c>PotionDrunk</c>).</summary>
public partial class PortSynthMapsTests
{
    private const string ActionsMap = "port-synth-6-actions";

    private static JsonElement Zone(JsonElement facts, string key) =>
        facts.GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == key);

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    /// <summary>Клетка из поля описания: <c>[x, y]</c>.</summary>
    private static (int X, int Y) XY(JsonElement zone, string field) => (zone.GetProperty(field)[0].GetInt32(), zone.GetProperty(field)[1].GetInt32());

    /// <summary>Отпуск: воин из отряда и из гарнизона — можно; лидер один в деревне — отряд распущен, вещи на складе деревни; лидер с
    /// воином в деревне (5), лидер вне города (6), страж столицы (2) — отказ; герой, на которого ссылается условие события с победой, —
    /// окно отказывает (<c>X005TA0883</c>, <see cref="GameSession.NeededByQuest"/>).</summary>
    [SkippableFact]
    public void Actions_Dismiss_AllCodes()
    {
        Skip.IfNot(MapIds().Contains(ActionsMap));
        var facts = Facts(ActionsMap);
        var session = Begin(ActionsMap, 1);
        var seen = new Recorder();
        session.Watch(seen);
        var map = session.Map;
        string d1 = Str(Zone(facts, "D1"), "stack");
        string soldier = map.Stacks[d1].UnitIds.First(u => !Same(u, map.Stacks[d1].LeaderId));
        Assert.Equal(0, session.DismissUnit(d1, soldier));
        Assert.DoesNotContain(soldier, session.Map.Stacks[d1].UnitIds);
        string d5 = Str(Zone(facts, "D5"), "stack");
        Assert.Equal(6, session.DismissUnit(d5, session.Map.Stacks[d5].LeaderId));
        var d3 = Zone(facts, "D3");
        var lone = session.Map.Stacks[Str(d3, "stack")];
        var carried = lone.Items.ToList();
        Assert.Equal(0, session.DismissUnit(lone.Id, lone.LeaderId));
        Assert.False(session.Map.Stacks.ContainsKey(lone.Id));
        var village = session.Map.Sites.Single(s => Same(s.Id, Str(d3, "target")));
        Assert.Equal("", village.StackId);
        Assert.All(carried, i => Assert.Contains(i, village.Items));
        string d4 = Str(Zone(facts, "D4"), "stack");
        Assert.Equal(5, session.DismissUnit(d4, session.Map.Stacks[d4].LeaderId));
        string d6 = Str(Zone(facts, "D6"), "stack");
        Assert.True(session.NeededByQuest(d6, session.Map.Stacks[d6].LeaderId));
        Assert.False(session.NeededByQuest(d4, session.Map.Stacks[d4].LeaderId));
        var capital = facts.GetProperty("capital");
        string cap = Str(capital, "id");
        Assert.Equal(0, session.DismissUnit(cap, capital.GetProperty("garrison")[0].GetString()!));
        Assert.Equal(2, session.DismissUnit(cap, Str(capital, "guardian")));
        var dismissed = seen.Sights.Where(s => s.Kind == TurnSightKind.Dismissed).Select(s => s.Id).ToList();
        Assert.Equal(3, dismissed.Count);
        Assert.All(seen.Sights.Where(s => s.Kind == TurnSightKind.Dismissed), s => Assert.Equal(session.ActiveHuman, s.PlayerId));
    }

    /// <summary>Мешки: бросок на пустую клетку — новый мешок, предметы кладутся окном мешка, пустой — исчезает при закрытии; бросок на
    /// клетку с мешком — тот же мешок (взять и положить); подбор мешка ходом — предметы отряду; пустой мешок — подбор без предметов.</summary>
    [SkippableFact]
    public void Actions_Bags_DropExchangePickup()
    {
        Skip.IfNot(MapIds().Contains(ActionsMap));
        var facts = Facts(ActionsMap);
        var session = Begin(ActionsMap, 1);
        var seen = new Recorder();
        session.Watch(seen);
        var b1 = Zone(facts, "B1");
        string s1 = Str(b1, "stack");
        var drop = b1.GetProperty("drop_at");
        Assert.Null(session.DropBag(s1, drop[0].GetInt32(), drop[1].GetInt32(), out string bag));
        foreach (string item in session.Map.Stacks[s1].Items.Take(2).ToList()) Assert.Null(session.ExchangeItem(s1, bag, item));
        Assert.Equal(2, session.ItemsOf(bag)!.Count);
        var empty = b1.GetProperty("empty_at");
        Assert.Null(session.DropBag(s1, empty[0].GetInt32(), empty[1].GetInt32(), out string blank));
        session.CloseBag(blank);
        Assert.DoesNotContain(session.Map.Sites, s => Same(s.Id, blank));
        var b2 = Zone(facts, "B2");
        string s2 = Str(b2, "stack"), bag2 = Str(b2, "target");
        var at2 = b2.GetProperty("target_at");
        Assert.Null(session.DropBag(s2, at2[0].GetInt32(), at2[1].GetInt32(), out string same));
        Assert.Equal(bag2, same, StringComparer.OrdinalIgnoreCase);
        string taken = session.ItemsOf(bag2)![0], given = session.Map.Stacks[s2].Items[0];
        Assert.Null(session.ExchangeItem(bag2, s2, taken));
        Assert.Null(session.ExchangeItem(s2, bag2, given));
        Assert.Contains(given, session.ItemsOf(bag2)!);
        Assert.Contains(taken, session.Map.Stacks[s2].Items);
        foreach (string key in new[] { "B3", "B4" })
        {
            var z = Zone(facts, key);
            int before = session.Map.Stacks[Str(z, "stack")].Items.Count, inBag = session.ItemsOf(Str(z, "target"))!.Count;
            var visit = session.Enter(Str(z, "stack"), Str(z, "target"));
            Assert.Null(visit.Refused);
            Assert.Equal(before + inBag, session.Map.Stacks[Str(z, "stack")].Items.Count);
            Assert.Contains(seen.Sights, s => s.Kind == TurnSightKind.ItemsGiven && Same(s.Id, Str(z, "target")));
        }
        Assert.Equal(3, seen.Sights.Count(s => s.Kind == TurnSightKind.BagDropped));
    }

    /// <summary>Обмен: два своих отряда рядом — воин и предмет туда и обратно; рядом с отрядом павшего лидера обмен открывается; вход в свою
    /// деревню с гарнизоном и складом — воины и предметы меняются в окне армии.</summary>
    [SkippableFact]
    public void Actions_Exchange_StacksAndGarrison()
    {
        Skip.IfNot(MapIds().Contains(ActionsMap));
        var facts = Facts(ActionsMap);
        var session = Begin(ActionsMap, 1);
        var x1 = Zone(facts, "X1");
        string a = Str(x1, "stack"), b = Str(x1, "target");
        var at = x1.GetProperty("target_at");
        Assert.Null(session.ExchangeCheck(a, at[0].GetInt32(), at[1].GetInt32(), out string other));
        Assert.Equal(b, other, StringComparer.OrdinalIgnoreCase);
        int free = Enumerable.Range(0, 6).First(c => session.Map.Stacks[b].Slots[c] < 0);
        int from = Enumerable.Range(0, 6).First(c => session.Map.Stacks[a].Slots[c] >= 0
                                                     && !Same(session.Map.Stacks[a].UnitIds[session.Map.Stacks[a].Slots[c]], session.Map.Stacks[a].LeaderId));
        Assert.Null(session.ExchangeUnits(a, from, b, free));
        Assert.Null(session.ExchangeItem(a, b, session.Map.Stacks[a].Items[0]));
        Assert.Null(session.ExchangeItem(b, a, session.Map.Stacks[b].Items[0]));
        var x2 = Zone(facts, "X2");
        var at2 = x2.GetProperty("target_at");
        Assert.Null(session.ExchangeCheck(Str(x2, "stack"), at2[0].GetInt32(), at2[1].GetInt32(), out _));
        Assert.False(session.Map.Stacks[Str(x2, "stack")].LeaderAlive);
        var g1 = Zone(facts, "G1");
        string g = Str(g1, "stack"), vg = Str(g1, "target");
        Assert.True(session.Enter(g, vg).Entered);
        var site = session.Map.Sites.Single(s => Same(s.Id, vg));
        Assert.Null(session.ExchangeItem(vg, g, site.Items[0]));
        Assert.Null(session.ExchangeItem(g, vg, session.Map.Stacks[g].Items[0]));
        int guardCell = Enumerable.Range(0, 6).First(c => site.GuardSlots[c] >= 0);
        int stackFree = Enumerable.Range(0, 6).First(c => session.Map.Stacks[g].Slots[c] < 0);
        Assert.Null(session.SwapUnits(vg, guardCell, g, stackFree));
    }

    /// <summary>Лечение и воскрешение: Храм в столице, герой выходит из неё, отряд H1 входит — раненые лечатся, павшие воскресают; в
    /// деревне H2 — лечение. Зонду — <c>Healed</c>/<c>Revived</c>.</summary>
    [SkippableFact]
    public void Actions_HealAndRevive_InCapitalAndVillage()
    {
        Skip.IfNot(MapIds().Contains(ActionsMap));
        var facts = Facts(ActionsMap);
        var session = Begin(ActionsMap, 1);
        var seen = new Recorder();
        session.Watch(seen);
        var capital = facts.GetProperty("capital");
        // Постройки сценария движок на старте снимает (0x421f2f, NewGame.FreeBuilding) — Храм строится первым действием дня 1.
        Assert.Equal(D2.Rules.Map.BuildStatus.CanBeBuilt, session.Build(session.ActiveHuman, Str(capital, "temple")));
        Assert.Null(session.LeaveFort(Str(capital, "hero")));
        foreach (var (key, fort) in new[] { ("H1", Str(capital, "id")), ("H2", Str(Zone(facts, "H2"), "target")) })
        {
            string sid = Str(Zone(facts, key), "stack");
            var visit = session.Enter(sid, fort);
            Assert.True(visit.Entered, $"{key}: {visit.Refused}");
            foreach (string unit in session.Map.Stacks[sid].UnitIds)
            {
                var u = session.Map.Units[unit];
                string? why = u.Hp == 0 ? session.Revive(fort, unit) : u.Hp < session.HpMax(u) ? session.Heal(fort, unit) : null;
                Out.WriteLine($"{key} {unit} {u.TypeId} {u.Hp}/{session.HpMax(u)}: {why ?? "да"} → {session.Map.Units[unit].Hp}");
                if (key == "H1") Assert.Null(why);
            }
        }
        Assert.Contains(seen.Sights, s => s.Kind == TurnSightKind.Revived);
        Assert.True(seen.Sights.Count(s => s.Kind == TurnSightKind.Healed) >= 3);
    }

    /// <summary>Площадки: у лавки M1 покупается предмет каждой категории, продаются доспех, ценность и свиток, особый — отказ; лавка M2 с
    /// флагами <c>BUY_*</c> 0 (кроме ценностей) — продажа; маг — заклинания; наёмники — три наёмника (единственный уходит из списка);
    /// тренер — обучение; зелья вне боя — четыре вида.</summary>
    [SkippableFact]
    public void Actions_Sites_BuySellHireTrain_AndPotions()
    {
        Skip.IfNot(MapIds().Contains(ActionsMap));
        var facts = Facts(ActionsMap);
        var session = Begin(ActionsMap, 1);
        var seen = new Recorder();
        session.Watch(seen);
        var m1 = Zone(facts, "M1");
        string buyer = Str(m1, "stack"), shop = Str(m1, "target");
        foreach (var good in m1.GetProperty("goods").EnumerateObject())
        {
            string? refused = session.BuyItem(buyer, shop, good.Value.GetString()!);
            Assert.True(refused is null, $"категория {good.Name}: {refused}");
        }
        string ItemOfType(string stack, string type) =>
            session.Map.Stacks[stack].Items.First(i => Same(session.Map.ItemTypes[i], type));
        foreach (string type in new[] { "G000IG2001", "G000IG7002", "G000IG5003" })
            Assert.Null(session.SellItem(buyer, shop, ItemOfType(buyer, type)));
        Assert.NotNull(session.SellItem(buyer, shop, ItemOfType(buyer, "G000IG7101")));
        var m2 = Zone(facts, "M2");
        var picky = session.Map.Sites.Single(s => Same(s.Id, Str(m2, "target")));
        Assert.Equal(new D2.Rules.Map.MerchantBuys(false, false, false, false, false, false, false, true), picky.Buys);
        string armor = ItemOfType(Str(m2, "stack"), "G000IG2001");
        // Флаги BUY_* продажу не решают — так у движка (реестр Г12, Г13): проверка продажи 0x5dfe1a — площадка-торговец, L_VALUABLE —
        // да, L_SPECIAL — нет, прочее — CVisitorMerchantAddItem::canApply 0x612e7f (торговец и тип предмета есть); множество категорий
        // BUY_* (CMidSiteMerchant +0x8c) читает только запись/чтение площадки 0x60151d. Журнал 20261016e-av: Runestone (L_ARMOR) лавке M2
        // S143SI0001 продан, site_sell_item ret true, +100 (seq 12971530).
        Assert.Null(session.SellItem(Str(m2, "stack"), picky.Id, armor));
        Assert.Null(session.SellItem(Str(m2, "stack"), picky.Id, ItemOfType(Str(m2, "stack"), "G000IG7001")));
        var mg = Zone(facts, "MG");
        foreach (var spell in mg.GetProperty("spells").EnumerateArray())
            Assert.True(session.BuySpell(Str(mg, "stack"), Str(mg, "target"), spell.GetString()!) is null, spell.GetString());
        var mc = Zone(facts, "MC");
        var camp = session.Map.Sites.Single(s => Same(s.Id, Str(mc, "target")));
        foreach (var offer in camp.Roster.ToList())
            Assert.True(session.HireMercenary(Str(mc, "stack"), camp.Id, offer.UnitType) is null, offer.UnitType);
        Assert.DoesNotContain(session.Map.Sites.Single(s => Same(s.Id, camp.Id)).Roster, o => o.Unique);
        var tr = Zone(facts, "TR");
        string pupil = Str(tr, "stack");
        foreach (string unit in session.Map.Stacks[pupil].UnitIds.Where(u => !Same(u, session.Map.Stacks[pupil].LeaderId)))
            Assert.Null(session.Train(pupil, Str(tr, "target"), unit, out _));
        var p1 = Zone(facts, "P1");
        string holder = Str(p1, "stack");
        var units = session.Map.Stacks[holder].UnitIds.Select(u => session.Map.Units[u]).ToList();
        string dead = units.First(u => u.Hp == 0).Id, hurt = units.First(u => u.Hp > 0 && u.Hp < session.HpMax(u)).Id;
        Assert.Null(session.DrinkPotion(holder, ItemOfType(holder, "G000IG0001"), dead));
        Assert.True(session.Map.Units[dead].Hp > 0);
        int hurtHp = session.Map.Units[hurt].Hp;
        Assert.Null(session.DrinkPotion(holder, ItemOfType(holder, "G000IG0005"), hurt));
        Assert.True(session.Map.Units[hurt].Hp > hurtHp);
        Assert.Null(session.DrinkPotion(holder, ItemOfType(holder, "G000IG0002"), session.Map.Stacks[holder].LeaderId));
        Assert.Null(session.DrinkPotion(holder, ItemOfType(holder, "G000IG0004"), session.Map.Stacks[holder].LeaderId));
        var kinds = seen.Sights.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        Out.WriteLine(string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}")));
        int goods = m1.GetProperty("goods").EnumerateObject().Count(), spells = mg.GetProperty("spells").GetArrayLength();
        Assert.Equal(goods + spells + camp.Roster.Count, kinds.GetValueOrDefault(TurnSightKind.Bought));
        Assert.Equal(5, kinds.GetValueOrDefault(TurnSightKind.Sold));
        Assert.Equal(2, kinds.GetValueOrDefault(TurnSightKind.Trained));
        Assert.Empty(session.Map.Stacks[holder].Items);   // все четыре зелья выпиты (PotionDrunk зонду шлёт окно отряда)
    }
}
