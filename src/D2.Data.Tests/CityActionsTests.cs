using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Действия в укреплении (<see cref="GameSession"/>, <c>GameSession.City.cs</c>) на живой партии: постройка,
/// найм в гарнизон, рост деревни, лечение.</summary>
[Trait("Category", "Corpus")]
public class CityActionsTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static (GameSession Session, MapPlayer Human) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        return (session, looks.Map.Players.Values.First(p => p.IsHuman));
    }

    /// <summary>Постройка: цена списана, постройка у игрока, вторая в тот же день — «уже строил сегодня».</summary>
    [SkippableFact]
    public void Build_PaysOnce_AndOnlyOncePerDay()
    {
        var (session, human) = Game();
        var book = session.Book!;
        var rich = session.Map.Players[human.Id] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999), ConstructionTurn = 0 };
        session = Replace(session, rich);
        var candidates = session.Buildable(human.Id).Select(id => book.Economy.Buildings[id])
            .Where(b => Economy.CanBuild(session.Map, human.Id, b.Id, book.Economy) == BuildStatus.CanBeBuilt).ToList();
        Assert.True(candidates.Count >= 2);
        var first = candidates[0];

        Assert.Equal(BuildStatus.CanBeBuilt, session.Build(human.Id, first.Id));
        var after = session.Map.Players[human.Id];
        output.WriteLine($"построено {first.Id} за {first.Cost}");
        Assert.Contains(first.Id, after.Buildings, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Economy.Clamp(rich.Bank - first.Cost), after.Bank);
        Assert.Equal(BuildStatus.AlreadyBuiltThisDay, session.Build(human.Id, candidates.First(c => c.Id != first.Id).Id));
        Assert.Throws<ArgumentException>(() => session.Build(human.Id, "G000BB0010"));   // Империи, не Кланов
    }

    /// <summary>Постройка ИИ: храм, магия и гильдия бесплатны (флаг «без цены» <c>+0xc</c> действия столицы, <c>0x446e2a</c> → команда
    /// <c>0x41ea0f</c>; корпус — 207 построек с флагом из 207 без списания), здание юнитов оплачивается.</summary>
    [SkippableFact]
    public void Build_AiUtilityIsFree_UnitBuildingPaid()
    {
        var (session, _) = Game();
        var book = session.Book!;
        const string ai = "S073PL000c";
        var start = session.Map.Players[ai] with { Bank = new Bank(1000, 100, 100, 100, 100, 0), ConstructionTurn = 0, Buildings = [] };
        session = Replace(session, start);
        var utility = session.Buildable(ai).Select(id => book.Economy.Buildings[id])
            .First(b => AiStrategicRules.IgnoresCost(b) && Economy.CanBuild(session.Map, ai, b.Id, book.Economy, ignoreCost: true) == BuildStatus.CanBeBuilt);

        Assert.Equal(BuildStatus.CanBeBuilt, session.Build(ai, utility.Id, ai: true));
        Assert.Equal(start.Bank, session.Map.Players[ai].Bank);

        session = Replace(session, session.Map.Players[ai] with { ConstructionTurn = 0 });
        var units = session.Buildable(ai).Select(id => book.Economy.Buildings[id])
            .First(b => !AiStrategicRules.IgnoresCost(b) && Economy.CanBuild(session.Map, ai, b.Id, book.Economy) == BuildStatus.CanBeBuilt);
        Assert.Equal(BuildStatus.CanBeBuilt, session.Build(ai, units.Id, ai: true));
        Assert.Equal(Economy.Clamp(start.Bank - units.Cost), session.Map.Players[ai].Bank);
    }

    /// <summary>Найм в гарнизон своей деревни: юнит базового уровня с полным здоровьем, клетка занята, цена списана;
    /// тип не из списка найма — отказ.</summary>
    [SkippableFact]
    public void HireUnit_IntoOwnVillage()
    {
        var (session, human) = Game();
        var book = session.Book!;
        var rich = session.Map.Players[human.Id] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        session = Replace(session, rich);
        var map = session.Map;
        var fort = map.Sites.First(s => s.Kind is SiteKind.Capital or SiteKind.Village && s.Owner == human.Id
                                        && s.GuardSlots.Count(c => c >= 0) < (s.Kind == SiteKind.Capital ? 6 : s.Size ?? 1));
        string type = Economy.HireList(map, human.Id, book.Economy)
            .First(t => book.Rules.Units[t].SizeSmall);

        Assert.Null(session.HireUnit(fort.Id, type));

        var after = session.Map.Sites.First(s => s.Id == fort.Id);
        string hired = after.Guards[^1];
        var unit = session.Map.Units[hired];
        output.WriteLine($"{fort.Id}: нанят {hired} {unit.TypeId} ур.{unit.Level} HP {unit.Hp}");
        Assert.Equal(fort.Guards.Count + 1, after.Guards.Count);
        Assert.Contains(after.Guards.Count - 1, after.GuardSlots);
        Assert.Equal(book.Rules.Units[type].HitPoints, unit.Hp);
        Assert.Equal(Economy.Clamp(rich.Bank - Economy.HireCost(type, book.Economy)), session.Map.Players[human.Id].Bank);
        Assert.NotNull(session.HireUnit(fort.Id, "G000UU0001"));   // Империя — не в списке Кланов
    }

    /// <summary>Оценка найма юнита стратегией ИИ (<c>evaluate 0x44c3ab</c>) даёт действие, только если проходит <c>hire_check 0x5deea7</c>
    /// на казне сейчас (<see cref="GameSession.HireUnitCheck"/>, проверка без найма): бюджет типа больше казны — действия нет (human, день 38:
    /// бюджет 190, казна 40 — у порта неисполнимый найм повторялся до предела проходов).</summary>
    [SkippableFact]
    public void AiHireUnit_NeedsBankNow_NotOnlyBudget()
    {
        var (session, _) = Game();
        const string ai = "S073PL0009";
        var budget = new AiBudget(AiStrategic.ListOrder.ToDictionary(t => t, _ => new Bank(9999, 9999, 9999, 9999, 9999, 9999)), Bank.Zero);
        IReadOnlyList<AiStepAction> Actions() =>
            [.. AiEconomy.Lists(session, ai, AiStepType.HireUnit, budget).SelectMany(e => e.Evaluate())];

        // Столица полна (6 из 6) — последний юнит гарнизона уходит, место свободно.
        var capital = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == ai);
        int last = capital.Guards.Count - 1;
        var freed = capital with { Guards = [.. capital.Guards.Take(last)], GuardSlots = [.. capital.GuardSlots.Select(c => c == last ? -1 : c)] };
        var map = GameSession.With(session.Map, sites: [.. session.Map.Sites.Select(s => ReferenceEquals(s, capital) ? freed : s)]);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);
        session = Replace(session, session.Map.Players[ai] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) });
        var rich = Actions();
        Assert.NotEmpty(rich);
        output.WriteLine($"{ai}: {string.Join(", ", rich.Select(a => $"{a.Target}@{a.Id}"))}");
        var guards = session.Map.Sites.First(s => s.Id == rich[0].Id).Guards;
        Assert.Null(session.HireUnitCheck(rich[0].Id, rich[0].Target));
        Assert.Equal(guards, session.Map.Sites.First(s => s.Id == rich[0].Id).Guards);   // проверка не нанимает

        session = Replace(session, session.Map.Players[ai] with { Bank = new Bank(1, 0, 0, 0, 0, 0) });
        Assert.Equal("не хватает казны", session.HireUnitCheck(rich[0].Id, rich[0].Target));
        Assert.Empty(Actions());
    }

    /// <summary>Рост своей деревни: уровень +1, день роста — сегодня, второй раз в тот же день — отказ.</summary>
    [SkippableFact]
    public void GrowCity_OncePerDay()
    {
        var (session, human) = Game();
        session = Replace(session, session.Map.Players[human.Id] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) });
        // Деревня 1-го уровня, отданная человеку, без бунта и без роста сегодня.
        var any = session.Map.Sites.First(s => s.Kind == SiteKind.Village);
        var village = any with { Owner = human.Id, Size = 1, RiotTurn = 0, GrowthTurn = 0 };
        var map = GameSession.With(session.Map, sites: [.. session.Map.Sites.Select(s => s.Id == any.Id ? village : s)]);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);

        Assert.Equal(CityGrowCheck.Allowed, session.GrowCity(village.Id));
        var after = session.Map.Sites.First(s => s.Id == village.Id);
        Assert.Equal((village.Size + 1, session.Map.Turn), (after.Size, after.GrowthTurn));
        Assert.Equal(CityGrowCheck.GrewToday, session.GrowCity(village.Id));
    }

    /// <summary>Конец мятежа в начале хода владельца (<c>CEffectRmvRiot</c>, <see cref="Fortifications.EndRiot"/>): при
    /// <c>ход ≥ RIOT_T</c> бунт снимается, гарнизон и гость теряют <c>RIOT_DMG</c> % текущего HP — после прохода лечения, в котором бунтующая
    /// деревня не лечит никого (прогон 13.10, ai-q11: 150 → 120, 90 → 72, 7/7), запас хода гостя — 0. Деревня, чей мятеж ещё идёт, бунтует
    /// дальше. Прежде сессия мятеж не снимала вовсе — деревня бунтовала вечно (аудит 13.10).</summary>
    [SkippableFact]
    public void Riot_EndsAtOwnerTurnStart_HitsGarrisonAndGuest()
    {
        var (session, human) = Game();
        var book = session.Book!;
        var map = session.Map;
        var villages = map.Sites.Where(s => s.Kind == SiteKind.Village && s.Guards.Count > 0).Take(2).ToList();
        Assert.Equal(2, villages.Count);
        var guest = map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);
        var ending = villages[0] with { Owner = human.Id, RiotTurn = map.Turn + 1, StackId = guest.Id };
        var going = villages[1] with { Owner = human.Id, RiotTurn = map.Turn + 3, StackId = "" };
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Key == guest.Id
            ? p.Value with { Inside = ending.Id, X = ending.X, Y = ending.Y } : p.Value, StringComparer.OrdinalIgnoreCase);
        map = GameSession.With(map, stacks: stacks,
                               sites: [.. map.Sites.Select(s => s.Id == ending.Id ? ending : s.Id == going.Id ? going : s)]);
        // Половина здоровья: на полном урон 20 % тут же закрыло бы лечение 25 % до максимума — проверять было бы нечего.
        var hit = ending.Guards.Concat(guest.UnitIds).Where(id => map.Units[id].Hp > 0)
                               .ToDictionary(id => id, id => session.HpMax(map.Units[id]) / 2);
        Assert.Equal(ending.Guards.Count + guest.UnitIds.Count(id => map.Units[id].Hp > 0), hit.Count);
        var units = map.Units.ToDictionary(p => p.Key, p => hit.TryGetValue(p.Key, out int hp) ? p.Value with { Hp = hp } : p.Value,
                                           StringComparer.OrdinalIgnoreCase);
        map = GameSession.With(map, units: units);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, book);
        // Лечение дня — как у деревни без бунта: сброс RIOT_T идёт до прохода лечения (20261016b, карта 2, R1).
        var calm = GameSession.With(map, sites: [.. map.Sites.Select(s => s.Id == ending.Id ? ending with { RiotTurn = 0 } : s)]);
        var terms = GameSession.RegenTerms(calm, human.Id, book).ToLookup(t => t.UnitId, StringComparer.OrdinalIgnoreCase);

        session.EndTurn();

        var after = session.Map;
        Assert.Equal(0, after.Sites.First(s => s.Id == ending.Id).RiotTurn);
        Assert.Equal(going.RiotTurn, after.Sites.First(s => s.Id == going.Id).RiotTurn);
        Assert.Equal(0, after.Stacks[guest.Id].Move);
        foreach (var (id, hp) in hit)
        {
            // Лечение есть (бунт снят до него), удар — после него и от HP до лечения (20261016b R1: 80 → 132 → 116; прогон 13.10).
            int healed = terms[id].Aggregate(hp, (h, t) => Regeneration.Tick(h, t.HpMax ?? h, t.UnitPercent, t.FortPercent));
            int expected = Math.Max(healed - Regeneration.RiotDamage(hp, book.Thieves.RiotDamage), 0);
            output.WriteLine($"{id}: {hp} → {after.Units[id].Hp} (ждём {expected})");
            Assert.Equal(expected, after.Units[id].Hp);
        }
    }

    /// <summary>Купленный талисман сразу с записью зарядов <c>GVars.TALIS_CHRG</c> (движок пишет её при создании, <c>[0x6ea178]</c> = 5), и
    /// подарок ИИ оценивает его как <c>VALUE × 5</c>; прежде записи не было, и <c>0x5eb9b3</c> давал 0 зарядов — ценность 0 (аудит 13.10).</summary>
    [SkippableFact]
    public void BoughtTalisman_HasCharges_AndGiftValue()
    {
        var (session, human) = Game();
        var book = session.Book!;
        var talisman = book.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.Talisman);
        var map = session.Map;
        var merchant = map.Sites.First(s => s.Kind == SiteKind.Merchant) with { Stock = [new MerchantStock(talisman.Id, 1)] };
        var stack = map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive);
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[human.Id] = players[human.Id] with { Bank = new Bank(99_999, 9999, 9999, 9999, 9999, 9999) };
        map = GameSession.With(map, players: players, sites: [.. map.Sites.Select(s => s.Id == merchant.Id ? merchant : s)]);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, book);

        Assert.Null(session.BuyItem(stack.Id, merchant.Id, talisman.Id));

        string instance = session.Map.Stacks[stack.Id].Items[^1];
        Assert.Equal(book.Vars.TalismanCharges, session.Map.TalismanCharges[instance]);
        Assert.Equal(talisman.Value.Gold * book.Vars.TalismanCharges, Treasury.ItemValue(talisman, session.Map.TalismanCharges[instance]).Gold);
    }

    /// <summary>Базы бюджета дня ИИ — из строки <c>GAI</c> отношения игрока (<c>B_UNITS</c>, <c>B_LEADERS</c>, <c>B_CAP</c>, <c>B_CITIES</c>), а не
    /// зашитая таблица: в ванили значения те же, мод с другими долями теперь действует (аудит 13.10).</summary>
    [SkippableFact]
    public void AiBudgetBases_ComeFromGaiRow()
    {
        var (session, _) = Game();
        var book = session.Book!;
        Assert.Equal(7, book.AiBudgetBases.Count);
        var ai = session.Map.Players.Values.First(p => !p.IsHuman && book.AiBudgetBases.ContainsKey(p.Attitude));
        var row = book.AiBudgetBases[ai.Attitude];
        Assert.Equal((75, 15, 50, 30), (row[AiStepType.HireUnit], row[AiStepType.HireLeader], row[AiStepType.Capital], row[AiStepType.GrowCities]));

        var rules = AiEconomy.BudgetRules(session, ai.Id);

        Assert.Equal(row[AiStepType.GrowCities], rules.Base[AiStepType.GrowCities]);
        Assert.Equal(row[AiStepType.HireUnit], rules.Base[AiStepType.HireUnit]);   // день 5: не первый день
    }

    /// <summary>Свою бунтующую деревню движок не открывает и отряд внутрь не пускает (прогон 13.10, ai-q22: «Пока город бунтует…»
    /// <c>X005TA0689</c>, 7 раз): <see cref="GameSession.Enter"/> отказывает с <see cref="GameSession.RiotRefusal"/>, после конца бунта — пускает.</summary>
    [SkippableFact]
    public void Enter_OwnRiotingVillage_Refused()
    {
        var (session, human) = Game();
        var map = session.Map;
        var village = map.Sites.First(s => s.Kind == SiteKind.Village) with { Owner = human.Id, StackId = "", Guards = [], GuardSlots = [], RiotTurn = map.Turn + 2 };
        map = GameSession.With(map, sites: [.. map.Sites.Select(s => s.Id == village.Id ? village : s)]);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);
        var stack = map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);

        var report = session.Enter(stack.Id, village.Id);

        Assert.Equal(GameSession.RiotRefusal, report.Refused);
        Assert.Equal("", session.Map.Stacks[stack.Id].Inside);
    }

    private static GameSession Replace(GameSession session, MapPlayer player)
    {
        var players = session.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[player.Id] = player;
        var map = GameSession.With(session.Map, players: players);
        return new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);
    }
}
