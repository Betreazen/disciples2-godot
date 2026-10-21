using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Наёмники и «храм» (торговец-миссия) на живой партии: общее действие найма у наёмника, решение ИИ в лагере
/// (<c>0x459570</c>) и покупки зелий в храме (<c>0x46c9f8</c>) — <c>docs/ai-tactic-candidates.md</c> §13–§14.</summary>
[Trait("Category", "Corpus")]
public class AiMercsTempleTests
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

    private static GameSession With(GameSession session, MapPlayer? player = null, IEnumerable<MapUnit>? units = null,
                                    MapSite? site = null)
    {
        var map = session.Map;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (player is not null) players[player.Id] = player;
        var all = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var u in units ?? []) all[u.Id] = u;
        var sites = site is null ? null : map.Sites.Select(s => s.Id == site.Id ? site : s).ToList();
        return new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, units: all, players: players, sites: sites) } },
                               session.Art, session.Book);
    }

    /// <summary>Отряд ИИ (не нейтрал), оставленный с одним лидером в клетке 2 — чтобы были свободные клетки.</summary>
    private static (GameSession Session, MapStack Stack) LoneLeader(GameSession session)
    {
        var map = session.Map;
        var stack = map.Stacks.Values.First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman
                                                 && !AiTactics.NeutralOwner(session, s.Owner) && s.LeaderAlive
                                                 && session.Book!.Rules.Units.TryGetValue(map.Units[s.LeaderId].TypeId, out var t) && t.SizeSmall);
        var lone = stack with { UnitIds = [stack.LeaderId], Slots = [-1, -1, 0, -1, -1, -1] };
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = lone;
        var next = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, stacks: stacks) } },
                                   session.Art, session.Book);
        Assert.True(next.FreeCells(lone) >= 1, $"у {stack.Id} лидерство {next.Leadership(lone)}");
        return (next, lone);
    }

    /// <summary>Найм у наёмника: юнит уровня предложения с полным здоровьем встаёт в клетку, казна платит <c>ENROLL_C</c> уровня
    /// (<see cref="GameBook.HirePrice"/>), уникальный уходит из ростера; в занятую клетку — отказ.</summary>
    [SkippableFact]
    public void HireMercenary_PlacesUnit_PaysLevelPrice()
    {
        var session = Game();
        var book = session.Book!;
        var site = session.Map.Sites.First(s => s.Kind == SiteKind.Mercenaries && s.Roster.Count > 0);
        var offer = site.Roster.First(o => book.Rules.Units.TryGetValue(o.UnitType, out var t) && t.SizeSmall);
        (session, var stack) = LoneLeader(session);
        var owner = session.Map.Players[stack.Owner] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        session = With(session, owner);
        var cells = UnitCells.Of(stack.UnitIds, stack.Slots);
        int cell = Enumerable.Range(0, UnitCells.CellCount).First(c => cells.Cells[c].Length == 0);
        int taken = Enumerable.Range(0, UnitCells.CellCount).First(c => cells.Cells[c].Length > 0);
        var price = book.HirePrice(offer.UnitType, offer.Level)!;

        Assert.NotNull(session.PlaceRefusal(stack.Id, offer.UnitType, taken));
        Assert.Null(session.HireMercenary(stack.Id, site.Id, offer.UnitType, cell));
        var after = session.Map.Stacks[stack.Id];
        string hired = after.UnitIds[^1];
        var unit = session.Map.Units[hired];
        Assert.Equal(stack.UnitIds.Count + 1, after.UnitIds.Count);
        Assert.Equal(after.UnitIds.Count - 1, after.Slots[cell]);
        Assert.Equal(book.Grown(offer.UnitType, offer.Level)!.Level, unit.Level);
        Assert.Equal(session.HpMax(unit), unit.Hp);
        Assert.Equal(Treasury.Pay(owner.Bank, price), session.Map.Players[owner.Id].Bank);
        var roster = session.Map.Sites.First(s => s.Id == site.Id).Roster;
        Assert.Equal(offer.Unique ? site.Roster.Count - 1 : site.Roster.Count, roster.Count);
    }

    /// <summary>Скидка лидера (<c>0x5d7aa1</c>: <c>getLowerCost</c> лидера, золото × (100 − скидка) / 100): лидер с модификатором
    /// <c>G000UM7548</c> (<c>L_LOWER_COST</c> 10 %) платит 270 вместо 300 за <c>G000UU5003</c> в <c>S073SI0004</c>; окно берёт ту же цену
    /// (<see cref="GameSession.MercenaryPrice"/>). Чит ИИ идёт уже после скидки.</summary>
    [SkippableFact]
    public void HireMercenary_LeaderLowerCost_DiscountsGold()
    {
        var session = Game();
        var site = session.Map.Sites.First(s => s.Id.Equals("S073SI0004", StringComparison.OrdinalIgnoreCase));
        var offer = site.Roster.First(o => o.UnitType.Equals("G000UU5003", StringComparison.OrdinalIgnoreCase));
        (session, var stack) = LoneLeader(session);
        var leader = session.Map.Units[stack.LeaderId];
        session = With(session, session.Map.Players[stack.Owner] with { Bank = new Bank(1000, 0, 0, 0, 0, 0) },
                       [leader with { Modifiers = [.. leader.Modifiers, "G000UM7548"] }]);

        Assert.Equal(10, session.LowerCostPercent(stack));
        Assert.Equal(270, session.MercenaryPrice(stack, offer)!.Gold);
        Assert.Null(session.HireMercenary(stack.Id, site.Id, offer.UnitType));
        Assert.Equal(730, session.Map.Players[stack.Owner].Bank.Gold);
        Assert.Equal(new Bank(33, 5, 0, 0, 0, 0), SiteTrade.LeaderDiscount(new Bank(37, 5, 0, 0, 0, 0), 10));   // отброс дроби, мана не трогается
    }

    /// <summary>Цена найма у наёмника против журнала human: оба найма (<c>site_buy_unit</c> seq 7727504 и 10435307, лагерь
    /// <c>S073SI0004</c>, <c>G000UU5003</c>) списали с казны 300 золота (1560 → 1260, 782 → 482); у порта найм того же предложения
    /// общим действием стоит столько же, и юнит встаёт в отряд.</summary>
    [SkippableFact]
    public void HireMercenary_PriceMatchesJournal()
    {
        var session = Game();
        var site = session.Map.Sites.First(s => s.Id.Equals("S073SI0004", StringComparison.OrdinalIgnoreCase));
        var offer = site.Roster.First(o => o.UnitType.Equals("G000UU5003", StringComparison.OrdinalIgnoreCase));
        (session, var stack) = LoneLeader(session);
        var owner = session.Map.Players[stack.Owner] with { Bank = new Bank(1560, 120, 870, 2170, 4720, 0) };
        session = With(session, owner);

        Assert.Null(session.HireMercenary(stack.Id, site.Id, offer.UnitType));
        Assert.Equal(new Bank(1260, 120, 870, 2170, 4720, 0), session.Map.Players[owner.Id].Bank);
    }

    /// <summary>Решение в лагере (<c>0x459570</c>): у каждой пустой клетки — перемешивание копии ростера (n − 1 бросков), найм
    /// первого по кошельку; кошелёк — ровно на самого дешёвого, так что нанят один, а цена ушла из кошелька.</summary>
    [SkippableFact]
    public void HireAtMercs_ShufflesPerEmptyCell_HiresCheapestByPurse()
    {
        var session = Game();
        var book = session.Book!;
        var site = session.Map.Sites.First(s => s.Kind == SiteKind.Mercenaries && s.Roster.Count >= 2);
        (session, var stack) = LoneLeader(session);
        var owner = session.Map.Players[stack.Owner] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        session = With(session, owner);
        var cheapest = site.Roster.MinBy(o => book.HirePrice(o.UnitType, o.Level)!.Gold)!;
        var price = book.HirePrice(cheapest.UnitType, cheapest.Level)!;
        var turn = new AiTacticTurn(owner.Id, book.AiWeights.Values.First(), [], session.Map.Turn, AiTacticMemo.Empty) { Purse = price };
        var rng = new ScriptedRng(Enumerable.Repeat(0, 100));

        Assert.True(AiTactics.HireAtMercs(session, turn, rng, stack.Id, site.Id));
        var before = UnitCells.Of(stack.UnitIds, stack.Slots);
        var after = session.Map.Stacks[stack.Id];
        var now = UnitCells.Of(after.UnitIds, after.Slots);
        int first = Enumerable.Range(0, UnitCells.CellCount).First(c => before.Cells[c].Length == 0);
        int empties = 1 + Enumerable.Range(first + 1, UnitCells.CellCount - first - 1).Count(c => now.Cells[c].Length == 0);
        Assert.Equal(empties * (site.Roster.Count - 1), rng.Used);
        Assert.Equal(Bank.Zero, turn.Purse);
        Assert.Equal(stack.UnitIds.Count + 1, after.UnitIds.Count);
        Assert.Equal(book.Grown(cheapest.UnitType, cheapest.Level)!.Type.Id, book.Grown(session.Map.Units[after.UnitIds[^1]].TypeId, 0)!.Type.Id);
    }

    /// <summary>Ветки решения в лагере, которых нет у «одного найма»: (1) уникальный наёмник — копия ростера о найме не знает, в каждой
    /// следующей пустой клетке ИИ снова берёт его, сервер отказывает, а цена всё равно уходит из кошелька; (2) место не годно (большому — нечётная
    /// клетка) — попытка этой клетки кончена без найма и без траты, следующая чётная клетка с пустой соседней годится.</summary>
    [SkippableFact]
    public void HireAtMercs_UniqueRefusedStillPaid_BadCellEndsAttempt()
    {
        var session = Game();
        var book = session.Book!;
        var site = session.Map.Sites.First(s => s.Kind == SiteKind.Mercenaries);
        (session, var stack) = LoneLeader(session);
        var owner = session.Map.Players[stack.Owner] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };

        string small = book.Economy.Hire.Keys.First(t => book.Rules.Units.TryGetValue(t, out var u) && u.SizeSmall && book.HirePrice(t, u.Level) is { Gold: > 0 });
        var unique = site with { Roster = [new MercenaryOffer(small, book.Rules.Units[small].Level, Unique: true)] };
        var game = With(session, owner, site: unique);
        var price = book.HirePrice(small, book.Rules.Units[small].Level)!;
        var turn = new AiTacticTurn(owner.Id, book.AiWeights.Values.First(), [], game.Map.Turn, AiTacticMemo.Empty)
        {
            Purse = new Bank(9999, 9999, 9999, 9999, 9999, 9999),
        };
        Assert.True(AiTactics.HireAtMercs(game, turn, new ScriptedRng([]), stack.Id, site.Id));
        Assert.Equal(stack.UnitIds.Count + 1, game.Map.Stacks[stack.Id].UnitIds.Count);
        Assert.Empty(game.Map.Sites.First(s => s.Id == site.Id).Roster);
        // Пустых клеток у одинокого лидера пять: в первой — найм, в остальных четырёх сервер отказывает, а кошелёк платит каждый раз.
        Assert.Equal(Enumerable.Range(0, 5).Aggregate(new Bank(9999, 9999, 9999, 9999, 9999, 9999), (b, _) => Treasury.Pay(b, price)), turn.Purse);

        // Большой, лидер в клетке 1: пустые 0, 2, 3, 4, 5.
        string big = book.Economy.Hire.Keys.First(t => book.Rules.Units.TryGetValue(t, out var u) && !u.SizeSmall && book.HirePrice(t, u.Level) is { Gold: > 0 });
        var bigSite = site with { Roster = [new MercenaryOffer(big, book.Rules.Units[big].Level, Unique: false)] };
        var lone = session.Map.Stacks[stack.Id] with { Slots = [-1, 0, -1, -1, -1, -1] };   // лидер в клетке 1: клетка 0 — пара занята
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = lone;
        var odd = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(session.Map, stacks: stacks) } },
                                  session.Art, session.Book);
        odd = With(odd, owner, site: bigSite);
        Skip.If(odd.FreeCells(lone) < 2, "лидерства не хватает на большого");
        var bigPrice = book.HirePrice(big, book.Rules.Units[big].Level)!;
        var turn2 = new AiTacticTurn(owner.Id, book.AiWeights.Values.First(), [], odd.Map.Turn, AiTacticMemo.Empty)
        {
            Purse = new Bank(9999, 9999, 9999, 9999, 9999, 9999),
        };
        Assert.True(AiTactics.HireAtMercs(odd, turn2, new ScriptedRng([]), stack.Id, site.Id));
        var after = odd.Map.Stacks[stack.Id];
        // Клетка 0 (пара с занятой 1) — отказ места без траты; клетка 2 — найм в колонку 2–3; 4 — снова годна, если хватает места.
        Assert.Equal(after.UnitIds.Count - 1, after.Slots[2]);
        Assert.Equal(after.Slots[2], after.Slots[3]);
        int hired = after.UnitIds.Count - 1;
        Assert.Equal(Enumerable.Range(0, hired).Aggregate(new Bank(9999, 9999, 9999, 9999, 9999, 9999), (b, _) => Treasury.Pay(b, bigPrice)),
                     turn2.Purse);
    }

    /// <summary>Высокий уровень (<c>SUGG_LVL ≥ 10</c>), отряд полон: лагерь годен, если есть кем заменить слабейшего
    /// (<c>0x458f8a</c>); в лагере слабейший (<c>0x4590ba</c>) увольняется и в пустую клетку нанимается сильнее (<c>0x459570</c>).</summary>
    [SkippableFact]
    public void HireAtMercs_HighLevel_ReplacesWeakest()
    {
        var session = Game();
        var book = session.Book!;
        session = new GameSession(session.State with { Info = session.State.Info with { SuggestedLevel = 10, Difficulty = 0 } },
                                  session.Art, session.Book);
        var site = session.Map.Sites.First(s => s.Kind == SiteKind.Mercenaries);
        (session, var stack) = LoneLeader(session);
        var smalls = book.Economy.Hire.Keys
            .Where(t => book.Rules.Units.TryGetValue(t, out var u) && u.SizeSmall && book.HirePrice(t, u.Level) is { Gold: > 0 })
            .Select(t => book.Rules.Units[t]).OrderBy(u => u.XpKilled).ThenBy(u => u.Id, StringComparer.Ordinal).ToList();
        var weak = smalls[0];
        var strong = smalls.Last(u => u.XpKilled > weak.XpKilled);
        // Заполнить отряд слабыми до лидерства.
        var units = new List<MapUnit>();
        var slots = stack.Slots.ToArray();
        var ids = stack.UnitIds.ToList();
        for (int cell = 0; cell < UnitCells.CellCount && ids.Count < session.Leadership(stack); cell++)
        {
            if (slots[cell] >= 0) continue;
            var u = new MapUnit { Id = $"S073UNf{cell:x3}", TypeId = weak.Id.ToUpperInvariant(), Level = weak.Level, Hp = weak.HitPoints };
            units.Add(u);
            slots[cell] = ids.Count;
            ids.Add(u.Id);
        }
        Skip.If(units.Count == 0, "лидерство — один лидер");
        var owner = session.Map.Players[stack.Owner] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        session = With(session, owner, units, site with { Roster = [new MercenaryOffer(strong.Id, strong.Level, Unique: false)] });
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = stack with { UnitIds = ids, Slots = slots };
        session = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(session.Map, stacks: stacks) } },
                                  session.Art, session.Book);
        var full = session.Map.Stacks[stack.Id];
        Assert.Equal(0, session.FreeCells(full));
        var turn = new AiTacticTurn(owner.Id, book.AiWeights.Values.First(), [], session.Map.Turn, AiTacticMemo.Empty)
        {
            Purse = new Bank(9999, 9999, 9999, 9999, 9999, 9999),
        };
        var camp = session.Map.Sites.First(s => s.Id == site.Id);
        Assert.True(AiTactics.MercsAffordable(session, turn, full, camp, 0));
        Assert.Equal((units[0].Id, weak.XpKilled), AiTactics.Weakest(session, full));

        Assert.True(AiTactics.HireAtMercs(session, turn, new ScriptedRng([]), stack.Id, site.Id));
        var after = session.Map.Stacks[stack.Id];
        Assert.Equal(full.UnitIds.Count, after.UnitIds.Count);
        Assert.DoesNotContain(units[0].Id, after.UnitIds);
        var types = after.UnitIds.Where(id => id != after.LeaderId).Select(id => session.Map.Units[id].TypeId).ToList();
        // Одна замена: клеток больше, чем лидерства, — после увольнения первого слабейшего новый встаёт в первую пустую клетку, а
        // попытка следующей пустой не проходит проверку места (0x5e78f7), и неудачная попытка кончает замену.
        Assert.Equal(1, types.Count(t => t.Equals(strong.Id, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(units.Count - 1, types.Count(t => t.Equals(weak.Id, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Храм (<c>0x46c755</c>, <c>0x46c9f8</c>): павшему нужно зелье воскрешения, раненому — лечения; покупки же оба прохода
    /// делают зельями воскрешения (ошибка оригинала): павший — одно, затем павший и раненый — ещё по одному, пока есть на складе.</summary>
    [SkippableFact]
    public void Temple_HelpsByPotionKind_BuysOnlyRevivePotions()
    {
        var session = Game();
        var book = session.Book!;
        var revive = book.Items.Values.First(i => i.Category == ItemCategory.PotionRevive);
        var heal = book.Items.Values.First(i => i.Category == ItemCategory.PotionHeal);
        var merchant = session.Map.Sites.First(s => s.Kind == SiteKind.Merchant) with
        {
            Mission = true, Stock = [new MerchantStock(heal.Id, 2), new MerchantStock(revive.Id, 2)],
        };
        var stack = session.Map.Stacks.Values.First(s => session.Map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman
                                                         && !AiTactics.NeutralOwner(session, s.Owner) && s.UnitIds.Count >= 3);
        var owner = session.Map.Players[stack.Owner] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        var units = stack.UnitIds.Select(u => session.Map.Units[u]).ToList();
        var dead = units[1] with { Hp = 0 };
        var hurt = units[2] with { Hp = 1 };
        var price = Treasury.ItemBuyPrice(revive);

        // Только раненый, на складе только зелья воскрешения — помочь нечем.
        var onlyRevive = merchant with { Stock = [new MerchantStock(revive.Id, 2)] };
        var hurtOnly = With(session, owner, [hurt], onlyRevive);
        Assert.False(AiTactics.TempleHelps(hurtOnly, price, hurtOnly.Map.Stacks[stack.Id], onlyRevive));
        Assert.True(AiTactics.TempleHelps(hurtOnly, Treasury.ItemBuyPrice(heal), hurtOnly.Map.Stacks[stack.Id], merchant));

        session = With(session, owner, [dead, hurt], merchant);
        Assert.True(AiTactics.TempleHelps(session, price, session.Map.Stacks[stack.Id], merchant));
        Assert.False(AiTactics.TempleHelps(session, Bank.Zero, session.Map.Stacks[stack.Id], merchant));

        var turn = new AiTacticTurn(owner.Id, book.AiWeights.Values.First(), [], session.Map.Turn, AiTacticMemo.Empty)
        {
            Purse = new Bank(9999, 9999, 9999, 9999, 9999, 9999),
        };
        var bought = AiTactics.BuyAtTemple(session, turn, stack.Id, merchant.Id);
        Assert.Equal([revive.Id.ToUpperInvariant(), revive.Id.ToUpperInvariant()], bought.Select(b => b.ToUpperInvariant()));
        Assert.Equal(Treasury.Pay(Treasury.Pay(new Bank(9999, 9999, 9999, 9999, 9999, 9999), price), price), turn.Purse);
        var stock = session.Map.Sites.First(s => s.Id == merchant.Id).Stock;
        Assert.Equal(2, stock.Single(r => r.Item.Equals(heal.Id, StringComparison.OrdinalIgnoreCase)).Count);
        Assert.DoesNotContain(stock, r => r.Item.Equals(revive.Id, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>Смена приказа (<c>CAiChangeOrderAction</c>): у отряда с приказом L_ATTACK_STACK, чья цель исчезла, кандидат смены —
    /// и исполнение ставит приказ L_BEZERK без цели, статус 1 (журналы: 4 из 4).</summary>
    [SkippableFact]
    public void ChangeOrder_AttackTargetGone_Bezerk()
    {
        var session = Game();
        var map = session.Map;
        var stack = map.Stacks.Values.OrderBy(s => s.Id).First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman
                                                                    && !AiTactics.NeutralOwner(session, s.Owner));
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = stack with { Order = (int)D2.Rules.Battle.StackOrder.AttackStack, OrderTarget = "S073KCffff" };
        session = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, stacks: stacks) } },
                                  session.Art, session.Book);
        var option = AiTactics.ChangeOrderOption(session, session.Map.Stacks[stack.Id], "S073KCffff");
        Assert.NotNull(option);
        Assert.Equal(AiTacticEval.ChangeOrderScore, option.Value.Item2);
        var (status, _) = session.ExecuteTactic(option.Value.Item1);
        Assert.Equal(AiTactic.StatusStuck, status);
        Assert.Equal((int)D2.Rules.Battle.StackOrder.Bezerk, session.Map.Stacks[stack.Id].Order);
        Assert.Equal("", session.Map.Stacks[stack.Id].OrderTarget);
    }
}
