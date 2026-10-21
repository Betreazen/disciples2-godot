using D2.Data;
using D2.Data.Corpus;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Задача 3 Ф7 — торговля у сайтов (<see cref="SiteTrade"/>). Харнесс сверяет каждое событие
/// <c>site_buy_item</c>, <c>site_sell_item</c>, <c>site_buy_spell</c>, <c>site_buy_unit</c>, <c>site_train</c> со следующим
/// сохранением: склад, книга заклинаний, ростер, сумка и отряд, опыт.</summary>
[Trait("Category", "Corpus")]
public class SitesHarnessTests(ITestOutputHelper output)
{
    private static Bank G(int gold) => Bank.Parse($"g{gold:0000}");

    // ---- модульные ------------------------------------------------------------------------------------

    /// <summary>Прогон 4 (run4 §7): Squire 26 очков при казне 105; Archer до 69; Squire 26 → 79 за 212; Knight 22 по
    /// ставке 5 при казне 113. Вторая большая партия: <c>UU0052</c> 94 очка. На потолке — отказ.</summary>
    [Fact]
    public void Train_Run4AndBig2Numbers()
    {
        Assert.Equal(new Trade<int>(26, G(1)), SiteTrade.Train(0, 80, G(4), G(105)));
        Assert.Equal(new Trade<int>(69, G(25)), SiteTrade.Train(0, 70, G(4), G(301)));
        Assert.Equal(new Trade<int>(79, G(13)), SiteTrade.Train(26, 80, G(4), G(225)));
        Assert.Equal(new Trade<int>(22, G(3)), SiteTrade.Train(0, 475, G(5), G(113)));
        Assert.Equal(94, SiteTrade.Train(0, 95, G(4), G(9889)).State);
        Assert.False(SiteTrade.Train(79, 80, G(4), G(1000)).Done);
    }

    /// <summary>Обучение по команде ИИ (<c>0x61eb4a</c>): ниже <c>SUGG_LVL</c> 10 и на лёгкой — как есть; на средней, сложной
    /// и очень сложной — цена 75/50/25 %, очки 125/150/200 %, ненулевая валюта не дешевле 1.</summary>
    [Fact]
    public void AiTraining_HighLevelCheat()
    {
        Assert.Equal((G(100), 25), SiteTrade.AiTraining(G(100), 25, highLevel: false, difficulty: 3));
        Assert.Equal((G(100), 25), SiteTrade.AiTraining(G(100), 25, highLevel: true, difficulty: 0));
        Assert.Equal((G(75), 31), SiteTrade.AiTraining(G(100), 25, highLevel: true, difficulty: 1));
        Assert.Equal((G(50), 37), SiteTrade.AiTraining(G(100), 25, highLevel: true, difficulty: 2));
        Assert.Equal((G(25), 50), SiteTrade.AiTraining(G(100), 25, highLevel: true, difficulty: 3));
        Assert.Equal((G(1), 2), SiteTrade.AiTraining(G(3), 1, highLevel: true, difficulty: 3));   // ⌊3·25/100⌋ = 0 → 1
        Assert.Equal((G(4), 0), SiteTrade.AiTraining(G(4), 0, highLevel: true, difficulty: 3));   // очков нет — как есть
    }

    [SkippableFact]
    public void BuySellSpellHire_StateAndPrice_FromTables()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var prices = PriceBook.Load(GamePaths.Globals);
        var potion = prices.Items["g000ig0001"];
        var stock = new List<MerchantStock> { new("G000IG0001", 1), new("G000IG0006", 2) };
        var bought = SiteTrade.BuyItem(stock, potion, G(9999));
        Assert.Equal([new MerchantStock("G000IG0006", 2)], bought.State);
        Assert.Equal(Treasury.Pay(G(9999), potion.Value), bought.Purse);
        Assert.False(SiteTrade.BuyItem(bought.State, potion, G(9999)).Done);

        // big2 §2: IG7004 VALUE 1000 — продажа +200; склад не меняется; при полной казне излишек теряется.
        var sold = SiteTrade.SellItem(bought.State, prices.Items["g000ig7004"], prices.Economy.SellRatio, G(5629));
        Assert.Equal(G(5829).Gold, sold.Purse.Gold);
        Assert.Same(bought.State, sold.State);
        Assert.Equal(9999, SiteTrade.SellItem(stock, prices.Items["g000ig7004"], 20, G(9999)).Purse.Gold);

        // big2 §2: человек под Повелителем Воров купил G000SS0005 за 200 — казна 4806 → 4606.
        var books = new SpellBooks(["G000SS0005", "G000SS0016"], ["G000SS0001"]);
        var spell = SiteTrade.BuySpell(books, prices.Spells["g000ss0005"], G(4806));
        Assert.Equal(["G000SS0016"], spell.State.Site);
        Assert.Equal(["G000SS0001", "G000SS0005"], spell.State.Known.Select(s => s.ToUpperInvariant()));
        Assert.Equal(4606, spell.Purse.Gold);
        Assert.False(SiteTrade.BuySpell(books, prices.Spells["g000ss0001"], G(9999)).Done);

        var roster = new List<MercenaryOffer> { new("G000UU5003", 1, true), new("G000UU5004", 1, false) };
        var hire = SiteTrade.HireUnit(roster, "G000UU5003", true, 6, Bank.Parse("g0300"), G(1000));
        Assert.Equal([roster[1]], hire.State);
        Assert.Same(roster, SiteTrade.HireUnit(roster, "G000UU5004", true, 6, Bank.Parse("g0300"), G(1000)).State);
    }

    // ---- харнесс ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void SiteEvents_MatchNextSave()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var book = ProgressBook.Load(GamePaths.Globals);
        var c = new Counters();
        var (unique, _) = JournalSet.Distinct(CorpusFiles.Journals());
        foreach (var path in unique.Where(TreasuryJournal.HasBank))
        {
            var j = TreasuryJournal.Load(path);
            var trade = j.Events.Where(IsTrade).ToList();
            if (trade.Count == 0) continue;
            if (j.Saves.Count == 0) { foreach (var e in trade) c.Skip(NameOf(e), "у журнала нет сохранений в манифесте"); continue; }
            new SiteAudit(book, c, j, JournalSaves.Of(j)).Run(trade);
        }
        // Единственный наём журнала (вторая большая партия, t = 2 811 656) лёг после последнего сохранения партии
        // (t28-032, t = 2 792 596) — сравнивать не с чем; наймы прогона sites сверяются по сохранениям без журнала.
        foreach (var (a, b) in LooseSaves.Pairs(sameDay: true)) LooseHires(book, c, a, b);
        c.Print(output, "торговля у сайтов (задача 3 Ф7)");
        c.Require("site_buy_item", "site_sell_item", "site_buy_spell", "site_buy_unit", "site_train");
    }

    /// <summary><c>site_buy_unit</c> по сохранениям без журнала (прогон <c>sites</c>): новый юнит в отряде игрока из списка
    /// посетителей наёмника (<c>VISITER</c> хранит id игрока — во всех сохранениях прогона <c>S143PL0001</c>), чей тип есть в
    /// ростере до, — найм. Модель (<see cref="SiteTrade.HireUnit"/>, цена <c>ENROLL_C</c>, казна сохранения до) убирает
    /// уникального из ростера; сверка — ростер после, и модель найм не отвергла.</summary>
    private static void LooseHires(ProgressBook book, Counters c, SaveAt a, SaveAt b)
    {
        foreach (var site in a.Sites.Where(s => s.Kind == SiteKind.Mercenaries && s.Roster.Count > 0))
        {
            if (b.Sites.FirstOrDefault(s => JournalSaves.Same(s.Id, site.Id)) is not { } after) continue;
            IReadOnlyList<MercenaryOffer> roster = site.Roster;
            var hired = new List<string>();
            string? refused = null;
            foreach (var (stack, unit) in NewUnits(a, b).Where(x => after.Visitors.Contains(x.Stack.Owner, StringComparer.OrdinalIgnoreCase)
                                                                     && roster.Any(o => JournalSaves.Same(o.UnitType, x.Unit.TypeId))))
            {
                if (!book.Prices.Economy.Hire.TryGetValue(unit.TypeId, out var price)) { refused = $"{unit.TypeId} нет в Gunits"; break; }
                var trade = SiteTrade.HireUnit(roster, unit.TypeId, true, SiteTrade.MaxCells, price.Cost, a.Map.Players[stack.Owner].Bank);
                refused ??= trade.Refused;
                roster = trade.State;
                hired.Add($"{unit.Id} {unit.TypeId} ур. {unit.Level}");
            }
            if (hired.Count == 0) continue;
            string detail = $"{Path.GetFileName(a.Path)} → {Path.GetFileName(b.Path)} {site.Id} (без журнала): наняты {string.Join(", ", hired)}; " +
                            $"ростер до {ShowRoster(site.Roster)}, модели {ShowRoster(roster)}, сохранения {ShowRoster(after.Roster)}" +
                            (refused is null ? "" : $"; модель отказала: {refused}");
            bool ok = refused is null && SiteAudit.SameRoster(roster, after.Roster);
            c.Count("site_buy_unit", ok, detail);
            c.Note($"   site_buy_unit {(ok ? "сошлось" : "разошлось")}: {detail}");
        }
    }

    /// <summary>Юниты, которых в сохранении до не было нигде, с отрядом, где они стоят после.</summary>
    private static IEnumerable<(MapStack Stack, MapUnit Unit)> NewUnits(SaveAt a, SaveAt b) =>
        b.Map.Stacks.Values.SelectMany(s => s.UnitIds.Where(u => !a.Map.Units.ContainsKey(u) && b.Map.Units.ContainsKey(u))
                                                     .Select(u => (s, b.Map.Units[u])));

    private static string ShowRoster(IEnumerable<MercenaryOffer> roster) =>
        string.Join(",", roster.Select(o => $"{o.UnitType}{(o.Unique ? "*" : "")}"));

    internal static bool IsTrade(CorpusEvent e) =>
        e is SiteBuyItemEvent or SiteSellItemEvent or SiteBuySpellEvent or SiteBuyUnitEvent or SiteTrainEvent;

    internal static string NameOf(CorpusEvent e) => e switch
    {
        SiteBuyItemEvent => "site_buy_item", SiteSellItemEvent => "site_sell_item", SiteBuySpellEvent => "site_buy_spell",
        SiteBuyUnitEvent => "site_buy_unit", _ => "site_train",
    };
}

/// <summary>Разбор харнесса задачи 3: события одного окна сохранений применяются по порядку к состоянию сохранения до,
/// итог сравнивается с сохранением после.</summary>
internal sealed class SiteAudit(ProgressBook book, Counters c, TreasuryJournal j, IReadOnlyList<SavePoint> points)
{
    private readonly Dictionary<string, IReadOnlyList<MerchantStock>> _stock = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpellBooks> _books = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<MercenaryOffer>> _roster = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Stack, string Type), int> _bought = [], _hired = [];

    /// <summary>Строка ростера, которую нанял <c>site_buy_unit</c> (по <c>seq</c> события).</summary>
    private readonly Dictionary<long, MercenaryOffer> _offers = [];

    /// <summary>Экземпляры, купленные в этом окне: экземпляр → тип. Купленный и проданный в одном окне (20261016e-av, карта 6:
    /// <c>S143IM0027</c> G000IG2001, куплен seq 12891525, продан 12924598) в сохранении до ещё не был, после — уже не был.</summary>
    private readonly Dictionary<string, string> _created = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Stack, string Type), int> _resold = [];

    /// <summary>Строки <c>id_alloc</c> предметов (вид 38) журнала — id нового экземпляра внутри <c>item_create</c> покупки.</summary>
    private IReadOnlyList<IdAllocEvent>? _itemIds;
    private IReadOnlyList<ItemCreateEvent>? _creates;
    private const long ItemIdType = 38;

    public void Run(List<CorpusEvent> trade)
    {
        foreach (var group in trade.GroupBy(e => (JournalSaves.Before(points, e.Seq, j.EpochOf(TreasuryJournal.TimeOf(e)!.Value)),
                                                  JournalSaves.After(points, e.Seq, j.EpochOf(TreasuryJournal.TimeOf(e)!.Value)))))
        {
            var (a, b) = group.Key;
            if (a is null || b is null) { foreach (var e in group) c.Skip(SitesHarnessTests.NameOf(e), "нет сохранения до или после в той же загрузке карты"); continue; }
            Window(a, b, group.OrderBy(e => e.Seq).ToList());
        }
    }

    private void Window(SavePoint a, SavePoint b, List<CorpusEvent> events)
    {
        _stock.Clear(); _books.Clear(); _roster.Clear(); _bought.Clear(); _hired.Clear(); _offers.Clear(); _created.Clear(); _resold.Clear();
        var outcomes = new List<(CorpusEvent E, string? Refused)>();
        foreach (var e in events.Where(e => e is not SiteTrainEvent).Concat(MerchantThefts(a.Cut, b.Cut)).OrderBy(e => e.Seq))
        {
            // Кража у торговца (действие вора 10, CVisitorMerchantDelItem, как GameSession.StealFromMerchant): экземпляр склада уходит.
            // Карта 23 (20261020-port-synth): вор Легионов KC0025 у своей лавки S143SI0004 — G000IG1001 (seq 1239796) и G000IG0001.
            if (e is NobleActionEvent theft) { _stock[theft.Target] = Without(Stock(a.Save, theft.Target), theft.Id); continue; }
            if (e is SiteBuyItemEvent { Ret: false } or SiteSellItemEvent { Ret: false } or SiteBuySpellEvent { Ret: false } or SiteBuyUnitEvent { Ret: false })
            { c.Skip(SitesHarnessTests.NameOf(e), "ret = false: движок отказал, состояние не менялось"); continue; }
            outcomes.Add((e, Apply(a.Save, e)));
        }
        foreach (var (e, refused) in outcomes) Compare(a.Save, b.Save, e, refused);
        Trainings(a, b, events.OfType<SiteTrainEvent>().Where(t => t.Ret).ToList());
    }

    /// <summary>Применяет событие к модели; возвращает причину отказа модели или null.</summary>
    private string? Apply(SaveAt a, CorpusEvent e)
    {
        if (!TryPurse(e, out var purse)) return "у события нет строки bank до следующей причины";
        switch (e)
        {
            case SiteBuyItemEvent x:
                if (!book.Prices.Items.TryGetValue(x.ItemType, out var item)) return $"{x.ItemType} нет в GItem";
                var buy = SiteTrade.BuyItem(Stock(a, x.Site), item, purse);
                _stock[x.Site] = buy.State;
                _bought[(x.Stack, x.ItemType)] = _bought.GetValueOrDefault((x.Stack, x.ItemType)) + 1;
                if (BoughtInstance(x) is { } instance) _created[instance] = x.ItemType;
                return buy.Refused;
            case SiteSellItemEvent x:
                string? type = a.ItemTypes.GetValueOrDefault(x.Item);
                if (type is null && _created.TryGetValue(x.Item, out var fresh))
                {
                    type = fresh;
                    _resold[(x.Stack, fresh)] = _resold.GetValueOrDefault((x.Stack, fresh)) + 1;
                }
                if (type is null || !book.Prices.Items.TryGetValue(type, out var sold))
                    return $"тип экземпляра {x.Item} не найден ни в сохранении до, ни среди купленных в окне";
                _stock[x.Site] = SiteTrade.SellItem(Stock(a, x.Site), sold, book.Prices.Economy.SellRatio, purse).State;
                return null;
            case SiteBuySpellEvent x:
                if (!book.Prices.Spells.TryGetValue(x.Spell, out var spell)) return $"{x.Spell} нет в Gspells";
                var learn = SiteTrade.BuySpell(Books(a, x.Site, x.Player), spell, purse);
                _books[$"{x.Site}|{x.Player}"] = learn.State;
                return learn.Refused;
            case SiteBuyUnitEvent x:
                if (Offer(a, x) is not { } offer) return $"{x.UnitType}: строки ростера {x.Site} с этим типом и уровнем нет";
                if (book.Prices.HirePrice(offer.UnitType, offer.Level) is not { } price) return $"{offer.UnitType} нет в Gunits";
                // ponytail: место в отряде харнесс не считает (найм в сохранении уже случился) — правило места
                // сверено модульно (SiteTradeTests.HireUnit_RefusesWithoutRoom).
                var hired = SiteTrade.HireUnit(Roster(a, x.Site), offer.UnitType, true, SiteTrade.MaxCells, price, purse);
                _roster[x.Site] = hired.State;
                _offers[x.Seq] = offer;
                _hired[(x.Stack, offer.UnitType)] = _hired.GetValueOrDefault((x.Stack, offer.UnitType)) + 1;
                return hired.Refused;
        }
        return null;
    }

    /// <summary>Id экземпляра, созданного покупкой: <c>id_alloc</c> вида 38 внутри первого <c>item_create</c> с причиной покупки после
    /// <c>site_buy_item</c> (<c>seq</c> … <c>seq_end</c>); зонд до 7.2 <c>id_alloc</c> не пишет — null.</summary>
    private string? BoughtInstance(SiteBuyItemEvent x)
    {
        _creates ??= [.. JournalCache.Read(j.Path, ["item_create"]).OfType<ItemCreateEvent>().OrderBy(e => e.Seq)];
        _itemIds ??= [.. JournalCache.Read(j.Path, ["id_alloc"]).OfType<IdAllocEvent>().Where(e => e.Type == ItemIdType).OrderBy(e => e.Seq)];
        if (_creates.FirstOrDefault(e => e.Seq > x.Seq && e.Cause == "site_buy_item") is not { } create
            || !create.Ids.TryGetValue("item_type", out var type) || !JournalSaves.Same(type, x.ItemType)) return null;
        return _itemIds.FirstOrDefault(e => e.Seq >= create.Seq && e.Seq <= create.SeqEnd)?.Id;
    }

    private void Compare(SaveAt a, SaveAt b, CorpusEvent e, string? refused)
    {
        string name = SitesHarnessTests.NameOf(e);
        var (ok, what) = e switch
        {
            SiteBuyItemEvent x when NewItems(a, b, x.Stack, x.ItemType) < _bought[(x.Stack, x.ItemType)] && UsedItemsAfter(x, b) =>
                (true, "SKIP"),
            // «Появился в сумке» — не меньше новых экземпляров, чем покупок: лишние законно приходят добычей боёв того же
            // окна (в корпусе дважды «новых 2, покупок 1» у одного отряда), а склад торговца сверяется точно.
            // Купленные и проданные в том же окне в сохранение после не попадают — их из покупок вычесть.
            SiteBuyItemEvent x => (SameStock(_stock[x.Site], SiteIn(b, x.Site)?.Stock)
                                   && NewItems(a, b, x.Stack, x.ItemType) >= _bought[(x.Stack, x.ItemType)] - _resold.GetValueOrDefault((x.Stack, x.ItemType)),
                                   $"{x.Stack} купил {x.ItemType} у {x.Site}: склад модели {Show(_stock[x.Site])}, сохранения {Show(SiteIn(b, x.Site)?.Stock)}; новых {x.ItemType} в отряде {NewItems(a, b, x.Stack, x.ItemType)}, покупок {_bought[(x.Stack, x.ItemType)]}"),
            SiteSellItemEvent x => (_stock.ContainsKey(x.Site) && SameStock(_stock[x.Site], SiteIn(b, x.Site)?.Stock) && !Carries(b, x.Stack, x.Item),
                                    $"{x.Stack} продал {x.Item} у {x.Site}: склад модели {(_stock.TryGetValue(x.Site, out var s) ? Show(s) : "—")}, сохранения {Show(SiteIn(b, x.Site)?.Stock)}; предмет в отряде после: {Carries(b, x.Stack, x.Item)}"),
            SiteBuySpellEvent x => (_books.TryGetValue($"{x.Site}|{x.Player}", out var m) && SameSet(m.Site, SiteIn(b, x.Site)?.Spells)
                                    && (PlayerIn(b, x.Player)?.KnownSpells.Contains(x.Spell, StringComparer.OrdinalIgnoreCase) ?? false),
                                    $"{x.Player} купил {x.Spell} у {x.Site}: список башни после {string.Join(",", SiteIn(b, x.Site)?.Spells ?? [])}"),
            SiteBuyUnitEvent x => Hire(a, b, x),
            _ => (false, ""),
        };
        if (what == "SKIP") { c.Skip(name, "новых экземпляров меньше покупок, а отряд после покупки надевал или снимал предметы (выпил зелье?)"); return; }
        c.Count(name, refused is null && ok,$"{Path.GetFileName(a.Path)} → {Path.GetFileName(b.Path)} {what}{(refused is null ? "" : $"; модель отказала: {refused}")}");
    }

    /// <summary><c>site_train</c>: опыт юнита от сохранения до через строки <c>xp</c> и обучение по казне
    /// <c>bank.before</c> (<see cref="UnitLedger"/>) — против опыта, типа и уровня в сохранении после.</summary>
    private void Trainings(SavePoint a, SavePoint b, List<SiteTrainEvent> trains)
    {
        foreach (var unit in trains.GroupBy(t => t.Unit, StringComparer.OrdinalIgnoreCase))
        {
            if (!a.Save.Map.Units.TryGetValue(unit.Key, out var ua) || !b.Save.Map.Units.TryGetValue(unit.Key, out var ub)
                || OwnerOf(a.Save, unit.Key) is not { } owner)
            { foreach (var _ in unit) c.Skip("site_train", "юнита нет в сохранении до или после"); continue; }
            if (Cheats.Between(Cheats.JumpSeqs(j), a.Cut, b.Cut))
            { foreach (var _ in unit) c.Skip("site_train", "в окне набран чит jump"); continue; }
            var built = a.Save.Map.Players[owner].Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var start = new UnitProgress(ua.TypeId, ua.Level, ua.Xp, ua.DynLevel);
            var model = UnitLedger.Run(book, j, start, unit.Key, JournalSaves.Between(j, a.Cut, b.Cut), built, out var refused, a.Save);
            bool ok = refused is null && JournalSaves.Same(model.TypeId, ub.TypeId) && model.Level == ub.Level && model.Xp == ub.Xp;
            string detail = $"{Path.GetFileName(a.Save.Path)} → {Path.GetFileName(b.Save.Path)} {unit.Key}: было {UnitLedger.Show(start)}, " +
                            $"модель {UnitLedger.Show(model)}{(refused is null ? "" : $" ({refused})")}, сохранение {UnitLedger.Show(new UnitProgress(ub.TypeId, ub.Level, ub.Xp, ub.DynLevel))}";
            foreach (var _ in unit) c.Count("site_train", ok, detail);
            c.Note($"   site_train {(ok ? "сошлось" : "разошлось")}: {detail}");
        }
    }

    /// <summary>Надевал или снимал ли отряд предметы между покупкой и сохранением после (зелье выпивается снятием),
    /// или пил зелье на карте: в схеме 6.0 это отдельное событие <c>potion_use</c> (<c>group</c> — отряд), строки
    /// <c>item_equip</c> при нём нет (T07 §5.6: <c>G000IG0004</c> seq 1211192 выпита seq 1211201, <c>destroy</c>).</summary>
    private bool UsedItemsAfter(SiteBuyItemEvent buy, SaveAt b) =>
        points.FirstOrDefault(p => p.Save == b) is { } after
        && j.Events.Any(e => e.Seq > buy.Seq && e.Seq < after.Cut && e switch
        {
            ItemEquipEvent x => x.Apply && JournalSaves.Same(x.Stack, buy.Stack),
            PotionUseEvent x => x.Ret && JournalSaves.Same(x.Group, buy.Stack),
            _ => false,
        });

    private bool TryPurse(CorpusEvent e, out Bank purse)
    {
        purse = Bank.Zero;
        if (j.NestedBank(e.Seq, null) is not { } b) return false;
        purse = b.Before;
        return true;
    }

    private IReadOnlyList<NobleActionEvent>? _thefts;

    /// <summary>Удачные кражи у торговца (<c>noble_action</c> действия <see cref="ThiefAction.StealMerchant"/>) окна сохранений.</summary>
    private IEnumerable<NobleActionEvent> MerchantThefts(long from, long to)
    {
        _thefts ??= [.. JournalCache.Read(j.Path, ["noble_action"]).OfType<NobleActionEvent>()
                                    .Where(n => n.Success && n.Action == (long)ThiefAction.StealMerchant)];
        return _thefts.Where(n => n.Seq >= from && n.Seq < to);
    }

    private static IReadOnlyList<MerchantStock> Without(IReadOnlyList<MerchantStock> stock, string item)
    {
        var row = stock.FirstOrDefault(s => JournalSaves.Same(s.Item, item) && s.Count > 0);
        return row is null ? stock : [.. stock.Select(s => ReferenceEquals(s, row) ? s with { Count = s.Count - 1 } : s).Where(s => s.Count > 0)];
    }

    private IReadOnlyList<MerchantStock> Stock(SaveAt a, string site) =>
        _stock.TryGetValue(site, out var s) ? s : SiteIn(a, site)?.Stock ?? [];

    private SpellBooks Books(SaveAt a, string site, string player) =>
        _books.TryGetValue($"{site}|{player}", out var s) ? s
            : new SpellBooks(SiteIn(a, site)?.Spells ?? [], PlayerIn(a, player)?.KnownSpells ?? []);

    private IReadOnlyList<MercenaryOffer> Roster(SaveAt a, string site) =>
        _roster.TryGetValue(site, out var r) ? r : SiteIn(a, site)?.Roster ?? [];

    private static int NewItems(SaveAt a, SaveAt b, string stack, string type)
    {
        var before = Carried(a, stack).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Carried(b, stack).Count(i => !before.Contains(i) && b.ItemTypes.TryGetValue(i, out var t) && JournalSaves.Same(t, type));
    }

    /// <summary>Строка ростера под <c>site_buy_unit</c>. Обычный тип — строка того же типа. Порождённый (<c>UG</c>) —
    /// строка базового типа и уровня, на которые его разбирает <see cref="ProgressBook.TryGlobal"/>
    /// (<see cref="UnitGenerator.TryDecode"/>): движок пишет в событие id реализации, а ростер хранит тип и уровень.
    /// Ранг 1: ai-q53 (passive-002 seq 874244) — <c>G000UG8165</c> при строке ростера <c>G000UU5039</c> ур. 4 (Волк).</summary>
    private const int AnyLevel = -1;

    private MercenaryOffer? Offer(SaveAt a, SiteBuyUnitEvent x)
    {
        if (!book.TryGlobal(x.UnitType, AnyLevel, out var type, out int level)) return null;
        return Roster(a, x.Site).FirstOrDefault(o => JournalSaves.Same(o.UnitType, type) && (level == AnyLevel || o.Level == level));
    }

    /// <summary>Найм против сохранения после: ростер тот же, что у модели, а в отряде ровно столько новых юнитов типа
    /// строки ростера, сколько наймов этого типа в окне, и все — её уровня.</summary>
    private (bool Ok, string What) Hire(SaveAt a, SaveAt b, SiteBuyUnitEvent x)
    {
        if (!_offers.TryGetValue(x.Seq, out var offer)) return (false, $"{x.Stack} нанял {x.UnitType} у {x.Site}: строки ростера нет");
        // Юнит в сохранении может нести и базовый тип, и порождённый id — сравниваются разобранные тип и уровень.
        var fresh = NewUnits(a, b, x.Stack)
            .Select(u => book.TryGlobal(u.TypeId, u.Level, out var type, out int level) ? (Type: type, Level: level) : (Type: u.TypeId, Level: u.Level))
            .Where(u => JournalSaves.Same(u.Type, offer.UnitType)).ToList();
        bool ok = _roster.TryGetValue(x.Site, out var r) && SameRoster(r, SiteIn(b, x.Site)?.Roster)
                  && fresh.Count == _hired[(x.Stack, offer.UnitType)] && fresh.All(u => u.Level == offer.Level);
        return (ok, $"{x.Stack} нанял {x.UnitType} ({offer.UnitType} ур. {offer.Level}) у {x.Site}: ростер после " +
                    $"{string.Join(",", SiteIn(b, x.Site)?.Roster.Select(o => o.UnitType) ?? [])}, новых юнитов типа " +
                    $"{fresh.Count} (уровни {string.Join(",", fresh.Select(u => u.Level))})");
    }

    private static List<MapUnit> NewUnits(SaveAt a, SaveAt b, string stack)
    {
        var before = a.Map.Stacks.TryGetValue(stack, out var sa) ? sa.UnitIds.ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        return b.Map.Stacks.TryGetValue(stack, out var sb)
            ? sb.UnitIds.Where(u => !before.Contains(u)).Select(u => b.Map.Units.GetValueOrDefault(u)).OfType<MapUnit>().ToList()
            : [];
    }

    private static IEnumerable<string> Carried(SaveAt s, string stack) =>
        s.Map.Stacks.TryGetValue(stack, out var st) ? st.Items.Concat(st.Equipment.Where(x => x.Length > 0)) : [];

    private static bool Carries(SaveAt s, string stack, string item) => Carried(s, stack).Contains(item, StringComparer.OrdinalIgnoreCase);

    private static bool SameStock(IReadOnlyList<MerchantStock> model, IReadOnlyList<MerchantStock>? saved) =>
        saved is not null && Normal(model).SequenceEqual(Normal(saved));

    private static IEnumerable<string> Normal(IEnumerable<MerchantStock> s) =>
        s.Where(x => x.Count > 0).Select(x => $"{x.Item.ToUpperInvariant()}×{x.Count}").Order(StringComparer.Ordinal);

    private static bool SameSet(IEnumerable<string> x, IEnumerable<string>? y) =>
        y is not null && x.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(y);

    internal static bool SameRoster(IReadOnlyList<MercenaryOffer> x, IReadOnlyList<MercenaryOffer>? y) =>
        y is not null && x.Select(o => o.UnitType.ToUpperInvariant()).Order().SequenceEqual(y.Select(o => o.UnitType.ToUpperInvariant()).Order());

    private static string Show(IEnumerable<MerchantStock>? s) => s is null ? "—" : string.Join(",", Normal(s));

    private static MapSite? SiteIn(SaveAt s, string id) => s.Sites.FirstOrDefault(x => JournalSaves.Same(x.Id, id));

    private static MapPlayer? PlayerIn(SaveAt s, string id) => s.Map.Players.TryGetValue(id, out var p) ? p : null;

    private static string? OwnerOf(SaveAt s, string unitId) =>
        s.Map.Stacks.Values.FirstOrDefault(x => x.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner;
}
