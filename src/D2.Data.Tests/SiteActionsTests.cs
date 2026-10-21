using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Услуги площадок на живой партии (<c>GameSession.Sites.cs</c>): обучение у тренера и его отказы; кошелёк
/// тактики ИИ.</summary>
[Trait("Category", "Corpus")]
public class SiteActionsTests
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

    private static GameSession With(GameSession session, MapPlayer? player = null, MapUnit? unit = null)
    {
        var map = session.Map;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (player is not null) players[player.Id] = player;
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (unit is not null) units[unit.Id] = unit;
        return new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, units: units, players: players) } },
                               session.Art, session.Book);
    }

    /// <summary>Обучение: очки — <see cref="Treasury.Training"/> от казны хозяина, опыт растёт, казна платит
    /// <c>TRAINING_C</c> × очки; не у тренера — отказ.</summary>
    [SkippableFact]
    public void Train_PaysTrainingRate_AndGrowsXp()
    {
        var session = Game();
        var map = session.Map;
        var trainer = map.Sites.First(s => s.Kind == SiteKind.Trainer);
        var stack = map.Stacks.Values.First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman
                                                 && s.UnitIds.Any(u => session.TrainRefusal(s.Owner, u) is null));
        string unitId = stack.UnitIds.First(u => session.TrainRefusal(stack.Owner, u) is null);
        var owner = map.Players[stack.Owner] with { Bank = new Bank(100, 0, 0, 0, 0, 0) };
        session = With(session, owner);
        var unit = session.Map.Units[unitId];
        var rate = session.TrainingRate(unitId)!;
        int xpNext = session.Book!.XpNextOf(unit.TypeId, unit.Level);
        var want = Treasury.Training(rate, xpNext, unit.Xp, owner.Bank);
        Assert.True(want.Points > 0);

        Assert.NotNull(session.Train(stack.Id, map.Sites.First(s => s.Kind == SiteKind.Merchant).Id, unitId, out _));
        Assert.Null(session.Train(stack.Id, trainer.Id, unitId, out var paid));
        Assert.Equal(want.Cost, paid);
        Assert.Equal(unit.Xp + want.Points, session.Map.Units[unitId].Xp);
        Assert.Equal(Treasury.Pay(owner.Bank, want.Cost), session.Map.Players[stack.Owner].Bank);
    }

    /// <summary>Отказы <c>0x5e0372</c>: у хозяина нет золота; опыт на <c>XP_NEXT − 1</c> («курс пройден»); юнит пал.</summary>
    [SkippableFact]
    public void TrainRefusal_NoGold_CourseDone_Dead()
    {
        var session = Game();
        var map = session.Map;
        var stack = map.Stacks.Values.First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman
                                                 && s.UnitIds.Any(u => session.TrainRefusal(s.Owner, u) is null));
        string unitId = stack.UnitIds.First(u => session.TrainRefusal(stack.Owner, u) is null);
        var owner = map.Players[stack.Owner];
        var unit = map.Units[unitId];
        int xpNext = session.Book!.XpNextOf(unit.TypeId, unit.Level);

        Assert.Contains("золота", With(session, owner with { Bank = Bank.Zero }).TrainRefusal(owner.Id, unitId));
        Assert.Contains("курс пройден", With(session, unit: unit with { Xp = xpNext - 1 }).TrainRefusal(owner.Id, unitId));
        Assert.Contains("пал", With(session, unit: unit with { Hp = 0 }).TrainRefusal(owner.Id, unitId));
        Assert.Null(With(session, unit: unit with { Xp = xpNext - 2 }).TrainRefusal(owner.Id, unitId));
    }

    /// <summary>Покупка у торговца: цена <c>VALUE</c>, со склада — экземпляр, в сумку — новый предмет; заклинание у мага —
    /// <c>BUY_C</c>, в книгу игрока, из башни уходит.</summary>
    [SkippableFact]
    public void BuyItemAndSpell_PayAndMoveGoods()
    {
        var session = Game();
        var map = session.Map;
        var book = session.Book!;
        var stack = map.Stacks.Values.First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman && !AiTactics.NeutralOwner(session, s.Owner));
        var owner = map.Players[stack.Owner] with { Bank = new Bank(5000, 5000, 5000, 5000, 5000, 5000) };
        session = With(session, owner);
        var merchant = session.Map.Sites.First(s => s.Kind == SiteKind.Merchant && s.Stock.Count > 0);
        var row = merchant.Stock[0];
        var item = book.Items[row.Item];

        Assert.Null(session.BuyItem(stack.Id, merchant.Id, row.Item));
        var after = session.Map.Stacks[stack.Id];
        Assert.Equal(stack.Items.Count + 1, after.Items.Count);
        Assert.Equal(row.Item.ToUpperInvariant(), session.Map.ItemTypes[after.Items[^1]]);
        Assert.Equal(Treasury.Pay(owner.Bank, item.Value), session.Map.Players[owner.Id].Bank);
        Assert.Equal(row.Count - 1, session.Map.Sites.First(s => s.Id == merchant.Id).Stock.FirstOrDefault(r => r.Item == row.Item)?.Count ?? 0);

        var mage = session.Map.Sites.First(s => s.Kind == SiteKind.Mage && s.Spells.Any(x => !owner.KnownSpells.Contains(x, StringComparer.OrdinalIgnoreCase)));
        string spell = mage.Spells.First(x => !owner.KnownSpells.Contains(x, StringComparer.OrdinalIgnoreCase));
        var bank = session.Map.Players[owner.Id].Bank;
        Assert.Null(session.BuySpell(stack.Id, mage.Id, spell));
        Assert.Contains(spell, session.Map.Players[owner.Id].KnownSpells, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(spell, session.Map.Sites.First(s => s.Id == mage.Id).Spells, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Treasury.Pay(bank, Treasury.SpellBuyPrice(book.Spells[spell])), session.Map.Players[owner.Id].Bank);
        Assert.NotNull(session.BuySpell(stack.Id, mage.Id, spell));   // второй раз — уже известно и в башне нет
    }

    /// <summary>Решение у торговца (<c>0x46b6f8</c>): броски выбирают экземпляр развёрнутого склада, не по кошельку — пропуск
    /// (экземпляр уходит из списка), куплено не больше «юниты − предметы».</summary>
    [SkippableFact]
    public void BuyAtMerchant_DrawsFromExpandedStock_SkipsUnaffordable()
    {
        var session = Game();
        var map = session.Map;
        var book = session.Book!;
        var stack = map.Stacks.Values.First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman && !AiTactics.NeutralOwner(session, s.Owner)
                                                 && AiTactics.MerchantWants(session, s) >= 1);
        var merchant = map.Sites.First(s => s.Kind == SiteKind.Merchant && s.Stock.Select(r => r.Item).Distinct().Count() >= 2);
        var pool = merchant.Stock.SelectMany(r => Enumerable.Repeat(r.Item, r.Count)).ToList();
        var cheap = pool.MinBy(i => book.Items[i].Value.Gold)!;
        long price = book.Items[cheap].Value.Gold;
        // Кошелёк — ровно на один самый дешёвый; первый бросок — последний экземпляр, второй — первый.
        var owner = map.Players[stack.Owner] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        session = With(session, owner);
        var turn = new AiTacticTurn(owner.Id, book.AiWeights.Values.First(), [], map.Turn, AiTacticMemo.Empty) { Purse = new Bank(price, 0, 0, 0, 0, 0) };
        var draws = new List<int> { pool.Count - 1 };
        draws.AddRange(Enumerable.Range(0, pool.Count).Select(_ => 0));
        var bought = AiTactics.BuyAtMerchant(session, turn, new D2.Rules.Rng.ScriptedRng(draws), stack.Id, merchant.Id);
        Assert.Single(bought);
        Assert.Equal(price, book.Items[bought[0]].Value.Gold);
        Assert.Equal(Bank.Zero, turn.Purse);
    }

    /// <summary>Кошелёк тактики (<c>0x4659e2</c>): 70 % (<c>GAI.B_ISO</c>) казны по каждой валюте, с отбрасыванием дробной части.</summary>
    [SkippableFact]
    public void TacticPurse_IsBIsoShareOfBank()
    {
        var session = Game();
        var ai = session.Map.Players.Values.First(p => !p.IsHuman && !AiTactics.NeutralOwner(session, p.Id))
                 with { Bank = new Bank(145, 10, 3, 0, 1, 9999) };
        Assert.Equal(new Bank(101, 7, 2, 0, 0, 6999), AiTactics.TacticPurse(session, ai));
    }
}
