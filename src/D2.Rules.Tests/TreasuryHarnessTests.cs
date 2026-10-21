using D2.Data;
using D2.Data.Corpus;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Харнесс «экономика»: каждое изменение казны в журналах корпуса (событие <c>bank</c>, схема v5)
/// объясняется предшествующим событием-причиной и ценой <see cref="Treasury"/>.
/// <para><b>Как находится причина — по seq, а не по времени</b> (ранг 1, все движения корпуса):
/// <c>site_*</c>, <c>heal</c>, <c>revive</c>, <c>city_grow</c> берут seq до оригинала, а казну двигает
/// оригинал изнутри — их строка <c>bank</c> имеет больший seq (до схемы 6.0 — ровно seq + 1, в 6.0 между ними
/// бывают вложенные проверки: <see cref="TreasuryJournal.NestedBank"/>); платная постройка пишет
/// <c>bank</c> сразу перед <c>build</c> (seq на единицу меньше); дневной доход — ближайший <c>bank</c>
/// того же игрока за строкой <c>income</c>; захват столицы — две строки <c>bank</c> сразу за
/// <c>battle_end</c>, добыча руины — одна; дань — две соседние строки без причинного события.</para>
/// <para>Изменения, причина которых из журнала не восстанавливается (читы, наём в столице, жезл,
/// каст — у них нет события в схеме), печатаются отдельным блоком с подсчётом и примером.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class TreasuryHarnessTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void EveryTreasuryMove_IsExplainedByCauseAndModelPrice()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var book = PriceBook.Load(GamePaths.Globals);
        var tally = new TreasuryTally();
        var (unique, duplicates) = JournalSet.Distinct(CorpusFiles.Journals());

        foreach (var path in unique.Where(TreasuryJournal.HasBank))
        {
            tally.Journals++;
            new TreasuryAudit(TreasuryJournal.Load(path), book, tally).Run();
        }
        tally.Print(output, unique.Count, duplicates);

        Assert.True(tally.Moves > 0, "в корпусе нет ни одного движения казны — проверять нечего");
        foreach (var kind in TreasuryAudit.MustCompare)
            Assert.True(tally.Compared(kind) > 0, $"«{kind}»: ноль сравнений — сверка не состоялась");
        Assert.Equal(tally.Moves, tally.ByModel + tally.ByObservation + tally.Unchecked + tally.NoCauseTotal);
        Assert.True(tally.Differed == 0, $"разошлось {tally.Differed} сверок с моделью — см. «РАСХОЖДЕНИЕ» выше");
    }
}

/// <summary>Как объяснено движение казны.</summary>
public enum Resolution { Model, Observation, Unchecked }

/// <summary>Разбор одного журнала.</summary>
public sealed class TreasuryAudit(TreasuryJournal j, PriceBook book, TreasuryTally tally)
{
    public const string HumanIncome = "доход, человек";
    public const string AiMarker = "признак ИИ (строка mode 1 у income) против снимка";
    public const string AiFirst = "доход ИИ, 1-й вызов: золото + ⌊мана/2⌋ (наблюдение)";
    public const string AiSecond = "доход ИИ, 2-й вызов: чит ресурсов по сложности (AiCheatResource)";
    public const string BuyItem = "покупка предмета";
    public const string SellItem = "продажа предмета";
    public const string BuySpell = "покупка заклинания";
    public const string BuyUnit = "наём у наёмника";
    public const string Training = "обучение: опыт по снимку";
    public const string TrainWhole = "обучение: опыта в снимке нет, упор в казну";
    public const string TrainMultiple = "обучение: опыта в снимке нет, только кратность TRAINING_C";
    public const string HealWhole = "лечение: на всю казну";
    public const string HealBySaves = "лечение: полное, нехватка HP по снимкам";
    public const string HealMultiple = "лечение: полное, только кратность HEAL_C";
    public const string Revive = "воскрешение";
    public const string Build = "постройка";
    public const string Grow = "рост города";
    public const string Capture = "захват столицы";
    public const string Ruin = "добыча руины (MidRuin.CASH)";
    public const string Tribute = "дань (опознана парой строк)";

    /// <summary>Виды (по началу имени), у которых на полном корпусе обязано быть хоть одно сравнение.</summary>
    public static readonly string[] MustCompare =
        [HumanIncome, BuyItem, SellItem, BuySpell, BuyUnit, "обучение", "лечение", Revive, Build, Grow, Capture];

    private const string EmptyId = "G000000000";
    private readonly Dictionary<long, Resolution> _claimed = [];
    private readonly Dictionary<string, long> _fortSize = new(StringComparer.OrdinalIgnoreCase);
    private BattleStartEvent? _lastStart;

    private string Name => Path.GetRelativePath(CorpusFiles.Dir, j.Path);

    public void Run()
    {
        for (int i = 0; i < j.Events.Count; i++) Visit(j.Events[i], i);
        Tributes();
        Leftovers();
    }

    private void Visit(CorpusEvent e, int i)
    {
        switch (e)
        {
            case IncomeEvent x: Income(x, i); break;
            case SiteBuyItemEvent x: OnBuyItem(x); break;
            case SiteSellItemEvent x: OnSellItem(x); break;
            case SiteBuySpellEvent x: OnBuySpell(x); break;
            case SiteBuyUnitEvent x: OnBuyUnit(x); break;
            case SiteTrainEvent x: OnTrain(x); break;
            case HealEvent x: OnHeal(x); break;
            case ReviveEvent x: OnRevive(x); break;
            case CityGrowEvent x: OnGrow(x); break;
            case BuildEvent x: OnBuild(x); break;
            case BattleStartEvent x: _lastStart = x; break;
            case BattleEndEvent x: OnBattleEnd(x); break;
            case FortDayEvent x: foreach (var f in x.Forts) _fortSize[f.Id] = f.Size; break;
        }
    }

    // ------------------------------------------------------------ доход

    /// <summary>Человек или ИИ — по снимку своей загрузки карты; нет снимка — по строке <c>bank</c> с
    /// <c>mode = 1</c> рядом с <c>income</c> (у ИИ и нейтралов она есть на каждом доходе, у человека нет; у второго
    /// вызова ИИ своей строки нет — признак берётся у первого, <see cref="FirstCallOf"/>).
    /// Признак сверяется со снимком там, где снимок есть, отдельной строкой счётчиков.</summary>
    private void Income(IncomeEvent e, int i)
    {
        var b = NextBankOf(e.Player, i);
        if (b is null) { tally.Skip(HumanIncome, "за строкой income нет строки bank этого игрока"); return; }
        var income = new Bank(e.Gold, e.Infernal, e.Life, e.Death, e.Runic, e.Grove);
        int first = FirstCallOf(e, i);
        bool aiByMarker = ModeOneNear(e.Player, e.Seq, i) || first >= 0 && ModeOneNear(e.Player, j.Events[first].Seq, first);
        var humanBySave = PlayerIn(j.SaveNear(b.T), e.Player)?.IsHuman;
        if (humanBySave is { } h)
            tally.Count(AiMarker, h != aiByMarker,
                $"{Name} seq {e.Seq} {e.Player}: снимок {(h ? "человек" : "ИИ")}, строка mode 1 {(aiByMarker ? "есть" : "нет")}", true);
        if (humanBySave ?? !aiByMarker) Check(HumanIncome, b, Treasury.Apply(b.Before, income), $"доход {Show(income)}");
        else AiIncome(b, income, second: first >= 0);
    }

    /// <summary>
    /// Первый вызов дневного дохода ИИ, если <paramref name="e"/> — второй: предыдущая строка <c>income</c> того же
    /// игрока и того же хода, а между ними в журнале казны только строки этого первого вызова — <c>bank</c> (mode 0
    /// и 1) и <c>fort_day</c> того же игрока. Возвращает индекс в <see cref="TreasuryJournal.Events"/>, иначе −1.
    /// Прежде пара узнавалась окном <c>seq</c> ≤ 20 (<see cref="JournalSaves.AiIncomeRepeatSeq"/>: обычно 6–8), но
    /// окно считает и строки хуков вне харнесса: 20261014-replay ai-q11, эльфы S143PL0002, ход 6 (seq 1775968 и
    /// 1775994, разрыв 26) — между вызовами 20 строк <c>ai_strength_units</c> нейтрала S143PL0000; второй вызов
    /// считался первым (модель 300/0,100,0,0,74), признак ИИ терялся (строка mode 1 первого вызова, seq 1775972, в 22
    /// от второго). На остальном корпусе пары те же, что по окну, плюс одна потерянная окном: 20260929-hotseat, нейтрал
    /// S073PL0000, ход 10 (seq 2666150 и 2666172, разрыв 22 — <c>stack_move</c>/<c>ai_order</c> бродяг между вызовами;
    /// доход 0, обе формулы дают ту же казну). Счётчики: 1-й вызов 1130 → 1128, 2-й 1109 → 1111.
    /// </summary>
    private int FirstCallOf(IncomeEvent e, int i)
    {
        for (int k = i - 1; k >= 0; k--)
            switch (j.Events[k])
            {
                case IncomeEvent p:
                    return Same(p.Player, e.Player) && p.Turn is { } turn && turn == e.Turn ? k : -1;
                case BankEvent b when Same(b.Player, e.Player): continue;
                case FortDayEvent f when Same(f.Player, e.Player): continue;
                default: return -1;
            }
        return -1;
    }

    /// <summary>ИИ зовёт доход дважды подряд (пара — <see cref="FirstCallOf"/>). Первый вызов — наблюдение по корпусу
    /// (ранг 3, разбора нет). Второй — чит ресурсов <see cref="Treasury.AiResourceCheat"/> (ранг 1 по коду) с процентом по
    /// <c>DIFFGAME</c> снимка своей загрузки карты; снимка нет — сложность неизвестна, сверки нет.</summary>
    private void AiIncome(BankEvent b, Bank income, bool second)
    {
        var before = b.Before;
        if (!second)
        {
            var change = new Bank(income.Gold, income.Infernal / 2, income.Life / 2, income.Death / 2, income.Runic / 2, income.Grove / 2);
            Check(AiFirst, b, Treasury.Apply(before, change), $"доход {Show(income)}", strict: false);
            return;
        }
        if (j.SaveNear(b.T) is not { } save) { Unchecked(AiSecond, b, "нет снимка своей загрузки карты — сложность неизвестна"); return; }
        int percent = book.AiCheats.Resource(save.Difficulty);
        Check(AiSecond, b, Treasury.AiResourceCheat(before, income, percent), $"доход {Show(income)}, D_*_M {percent}");
    }

    private BankEvent? NextBankOf(string player, int i)
    {
        for (int k = i + 1; k < Math.Min(j.Events.Count, i + 9); k++)
            if (j.Events[k] is BankEvent b && b.Mode == 0 && Same(b.Player, player)) return b;
        return null;
    }

    /// <summary>Строка <c>bank mode 1</c> игрока рядом с <c>income</c> — окно по <c>seq</c>, а не по индексу: с
    /// <c>alliance_change</c> в <see cref="TreasuryJournal.Events"/> между ними бывает шесть строк (ai-q25 seq 10894662,
    /// прежнее окно ±4 строки её теряло).</summary>
    private bool ModeOneNear(string player, long seq, int i)
    {
        int reach = (int)JournalSaves.AiIncomeRepeatSeq + 1;
        for (int k = Math.Max(0, i - reach); k < Math.Min(j.Events.Count, i + reach + 1); k++)
            if (j.Events[k] is BankEvent { Mode: 1 } b && Same(b.Player, player)
                && Math.Abs(b.Seq - seq) <= JournalSaves.AiIncomeRepeatSeq) return true;
        return false;
    }

    // ------------------------------------------------------------ объекты карты

    private void OnBuyItem(SiteBuyItemEvent e)
    {
        if (Nested(BuyItem, e.Seq, e.Ret, null) is not { } b) return;
        if (!book.Items.TryGetValue(e.ItemType, out var item)) { Unchecked(BuyItem, b, "предмета нет в GItem"); return; }
        int discount = LowerCost(e.Stack, e.T);
        Check(BuyItem, b, Treasury.Pay(b.Before, SiteTrade.Discounted(Treasury.ItemBuyPrice(item), discount)),
              $"{e.ItemType} VALUE {Show(item.Value)}, скидка {discount}");
    }

    private void OnSellItem(SiteSellItemEvent e)
    {
        if (Nested(SellItem, e.Seq, e.Ret, null) is not { } b) return;
        string? type = null;
        var save = j.SaveBefore(e.T);
        if (save?.ItemTypes.TryGetValue(e.Item, out var found) == true) type = found;
        if (type is null || !book.Items.TryGetValue(type, out var item))
        { Unchecked(SellItem, b, "экземпляра предмета нет в снимке до продажи"); return; }
        // Заряды талисмана — из снимка до продажи (у экземпляра без записи их 0, Treasury.ItemValue).
        int charges = save!.Map.TalismanCharges.TryGetValue(e.Item, out int left) ? left : 0;
        var price = Treasury.ItemSellPrice(item, book.Economy.SellRatio, charges);
        Check(SellItem, b, Treasury.Apply(b.Before, price), $"{e.Item} = {type} VALUE {Show(item.Value)} -> {Show(price)}");
    }

    private void OnBuySpell(SiteBuySpellEvent e)
    {
        if (Nested(BuySpell, e.Seq, e.Ret, e.Player) is not { } b) return;
        if (!book.Spells.TryGetValue(e.Spell, out var spell)) { Unchecked(BuySpell, b, "заклинания нет в Gspells"); return; }
        // Покупатель — отряд последнего посещения этой башни (site_buy_spell отряда не несёт).
        string? buyer = JournalCache.Read(j.Path, "site_visit").OfType<SiteVisitEvent>()
            .LastOrDefault(v => v.Seq < e.Seq && string.Equals(v.Site, e.Site, StringComparison.OrdinalIgnoreCase))?.Stack;
        int discount = LowerCost(buyer, e.T);
        Check(BuySpell, b, Treasury.Pay(b.Before, SiteTrade.Discounted(Treasury.SpellBuyPrice(spell), discount)),
              $"{e.Spell} BUY_C {Show(spell.BuyCost)}, скидка {discount}");
    }

    private static readonly Lazy<D2.Rules.Battle.GameRules> Units = new(() => D2.Data.Battle.RulesLoader.Load(GamePaths.Globals));

    /// <summary>Скидка лидера покупающего отряда (<c>L_LOWER_COST</c>, каждая валюта × (100 − скидка) / 100, <c>0x5868d6</c>; у мага
    /// <c>0x5d7a36</c>, у торговца <c>0x5dfd5f</c>) — по цепочке модификаторов лидера в снимке до покупки. Карта 17 LUTE (19.10): «Молния»
    /// <c>BUY_C</c> 200 с «Лютней очарования» — 180 (seq 5800245). Нет снимка или отряда — 0.</summary>
    private int LowerCost(string? stackId, long t)
    {
        if (stackId is null || j.SaveBefore(t) is not { } save || !save.Map.Stacks.TryGetValue(stackId, out var stack)
            || !save.Map.Units.TryGetValue(stack.LeaderId, out var leader)
            || !Units.Value.Units.TryGetValue(leader.TypeId.ToLowerInvariant(), out var type)) return 0;
        var chain = leader.Modifiers.Select(m => Units.Value.Modifiers?.GetValueOrDefault(m)).OfType<D2.Rules.Units.Modifier>().ToList();
        return D2.Rules.Units.EffectiveStats.Of(type, chain).LowerCostPercent;
    }

    private void OnBuyUnit(SiteBuyUnitEvent e)
    {
        if (Nested(BuyUnit, e.Seq, e.Ret, null) is not { } b) return;
        if (!book.Economy.Hire.TryGetValue(e.UnitType, out var hire)) { Unchecked(BuyUnit, b, "юнита нет в Gunits"); return; }
        Check(BuyUnit, b, Treasury.Pay(b.Before, hire.Cost), $"{e.UnitType} ENROLL_C {Show(hire.Cost)}");
    }

    private void OnTrain(SiteTrainEvent e)
    {
        if (Nested(Training, e.Seq, e.Ret, null) is not { } b) return;
        var near = UnitIn(j.SaveNear(e.T), e.Unit);
        if (near is null || !book.Treasury.Units.TryGetValue(near.TypeId, out var costs)
            || book.XpNext(near.TypeId, near.Level) is not { } xpNext)
        {
            Unchecked(Training, b, j.Saves.Count == 0
                ? "у журнала нет сохранений: строки про него в manifest.json нет"
                : "юнита нет в снимке своей загрузки карты");
            return;
        }
        var rate = costs.Training;
        string what = $"{e.Unit} {near.TypeId} ур. {near.Level}, TRAINING_C {Show(rate)}, XP_NEXT {xpNext}";
        var save = j.SaveBefore(e.T);
        // Тренер по команде ИИ при SUGG_LVL ≥ 10 — цена урезана читом (SiteTrade.TrainAi, 0x61eb4a): карта 16, сложная — × 50 %.
        var cheat = UnitLedger.AiTrainer(j.SaveNear(e.T), e.Unit);
        if (UnitIn(save, e.Unit) is { } was && XpSince(save!, e) is { } gained)
        {
            var trade = cheat is { } c
                ? SiteTrade.TrainAi(was.Xp + gained, xpNext, rate, b.Before, c.High, c.Difficulty)
                : SiteTrade.Train(was.Xp + gained, xpNext, rate, b.Before);
            Check(Training, b, trade.Purse, $"{what}, опыт {was.Xp}+{gained} -> {trade.State} опыта");
            return;
        }
        if (cheat is { } ai)
        {
            // Опыта до обучения нет: очков n не видно, но цена ИИ — AiPrice(TRAINING_C × n) при n ≤ min(⌊казна / ставка⌋, XP_NEXT − 1).
            int most = Math.Min(Treasury.Affordable(b.Before, rate), xpNext - 1);
            Bank Paid(int n) => SiteTrade.AiPrice(Treasury.Training(rate, xpNext, xpNext - 1 - n, b.Before).Cost, ai.High, ai.Difficulty);
            int fit = Enumerable.Range(1, Math.Max(most, 0)).FirstOrDefault(n => Treasury.Pay(b.Before, Paid(n)) == b.After, most);
            Check(TrainMultiple, b, Treasury.Pay(b.Before, Paid(fit)), $"{what}: ИИ, {fit} очков по цене чита");
            return;
        }
        // Как у лечения: упор в казну виден по остатку (на очко опыта уже не хватает), и тогда модель обязана
        // списать TRAINING_C × min(⌊казна / ставка⌋, XP_NEXT − 1).
        if (Treasury.Affordable(b.After, rate) == 0)
        {
            var whole = Treasury.Training(rate, xpNext, 0, b.Before);
            Check(TrainWhole, b, Treasury.Pay(b.Before, whole.Cost), $"{what}: {whole.Points} очков на всю казну");
            return;
        }
        int points = Math.Min(PointsOf(b.Before - b.After, rate), xpNext - 1);
        Check(TrainMultiple, b, Treasury.Pay(b.Before, Treasury.Training(rate, xpNext, xpNext - 1 - points, b.Before).Cost),
            $"{what}: {points} очков");
    }

    /// <summary>Опыт юнита за бои между снимком и обучением; null — был апгрейд или другое обучение.</summary>
    private int? XpSince(SaveAt from, SiteTrainEvent train)
    {
        int gained = 0;
        foreach (var ev in j.Since(from, train.T))
        {
            if (ev is XpEvent x && Same(x.Unit, train.Unit))
            {
                if (x.Ret) return null;
                gained += (int)x.Amount;
            }
            if (ev is SiteTrainEvent t && t.Seq != train.Seq && Same(t.Unit, train.Unit)) return null;
        }
        return gained;
    }

    // ------------------------------------------------------------ храм

    private void OnHeal(HealEvent e)
    {
        if (Nested("лечение", e.Seq, e.Ret, e.Player) is not { } b) return;
        var unit = UnitIn(j.SaveNear(e.T), e.Unit);
        if (unit is null || !book.Treasury.Units.TryGetValue(unit.TypeId, out var costs))
        { Unchecked(HealMultiple, b, "юнита нет в снимке своей загрузки карты"); return; }
        var rate = costs.Heal;
        var whole = Treasury.Heal(rate, int.MaxValue, b.Before);
        string what = $"{e.Unit} {unit.TypeId} HEAL_C {Show(rate)}";
        // Упор в казну виден по остатку, а не по цене: после лечения не хватает ещё на один HP. Тогда модель
        // обязана списать ровно HEAL_C × ⌊казна / HEAL_C⌋, и сверка может разойтись.
        if (Treasury.Affordable(b.After, rate) == 0)
        { Check(HealWhole, b, Treasury.Pay(b.Before, whole.Cost), $"{what}: {whole.Points} HP на всю казну"); return; }
        if (HpBefore(e) is { } was && HpAfter(e) is { } now && now > was)
        {
            Check(HealBySaves, b, Treasury.Pay(b.Before, Treasury.Heal(rate, now - was, b.Before).Cost),
                $"{what}: HP до {was}, после {now}");
            return;
        }
        int points = PointsOf(b.Before - b.After, rate);
        Check(HealMultiple, b, Treasury.Pay(b.Before, Treasury.Heal(rate, points, b.Before).Cost), $"{what}: {points} HP");
    }

    /// <summary>HP юнита перед лечением: 1, если в ту же миллисекунду его воскресили (ИИ воскрешает и
    /// лечит одним действием; зелье между ними выпить некогда); иначе HP из снимка до лечения, если между
    /// ними нет дневного тика игрока, боя с юнитом и другого лечения. Иначе null.</summary>
    private int? HpBefore(HealEvent heal)
    {
        if (j.Events.OfType<ReviveEvent>().Any(r => r.T == heal.T && r.Seq < heal.Seq && Same(r.Unit, heal.Unit)))
            return 1;
        var save = j.SaveBefore(heal.T);
        return save is not null && Clean(j.Since(save, heal.T), heal) ? UnitIn(save, heal.Unit)?.Hp : null;
    }

    /// <summary>HP после полного лечения — из первого снимка после, если между ними чисто.</summary>
    private int? HpAfter(HealEvent heal)
    {
        var save = j.SaveAfter(heal.T);
        return save is not null && Clean(j.Between(heal.T, save.T), heal) ? UnitIn(save, heal.Unit)?.Hp : null;
    }

    private static bool Clean(IEnumerable<CorpusEvent> window, HealEvent heal) => !window.Any(ev => ev switch
    {
        IncomeEvent i => Same(i.Player, heal.Player),
        BattleEndEvent be => be.Units.Any(u => Same(u.Id, heal.Unit)),
        HealEvent h => h.Seq != heal.Seq && Same(h.Unit, heal.Unit),
        ReviveEvent r => Same(r.Unit, heal.Unit) && r.T != heal.T,
        _ => false,
    });

    /// <summary>Сколько целых единиц цены <paramref name="rate"/> в <paramref name="paid"/>.</summary>
    private static int PointsOf(Bank paid, Bank rate)
    {
        int n = Treasury.Affordable(paid, rate);
        return n == int.MaxValue ? 0 : n;
    }

    private void OnRevive(ReviveEvent e)
    {
        if (Nested(Revive, e.Seq, e.Ret, e.Player) is not { } b) return;
        var unit = UnitIn(j.SaveNear(e.T), e.Unit);
        if (unit is null || !book.Treasury.Units.TryGetValue(unit.TypeId, out var costs))
        { Unchecked(Revive, b, "юнита нет в снимке своей загрузки карты"); return; }
        Check(Revive, b, Treasury.Pay(b.Before, Treasury.RevivePrice(costs)), $"{e.Unit} {unit.TypeId} REVIVE_C {Show(costs.Revive)}");
    }

    // ------------------------------------------------------------ столица, города, бои

    private void OnGrow(CityGrowEvent e)
    {
        if (Nested(Grow, e.Seq, e.Ret, e.Player) is not { } b) return;
        var player = PlayerIn(j.SaveNear(e.T), e.Player);
        if (!_fortSize.TryGetValue(e.Fort, out long size) || player is null
            || !book.Treasury.LordCategory.TryGetValue(player.LordId, out int category))
        { Unchecked(Grow, b, "нет размера города (fort_day) или лорда игрока в снимке"); return; }
        var price = Treasury.CityGrowthPrice(book.Economy, player.RaceId, (int)size, category);
        Check(Grow, b, Treasury.Pay(b.Before, price),
            $"{e.Fort} размер {size}, раса {player.RaceId}, лорд {player.LordId} категории {category}, цена {Show(price)}");
        _fortSize[e.Fort] = size + 1;
    }

    private void OnBuild(BuildEvent e)
    {
        if (j.BankBySeq.TryGetValue(e.Seq - 1, out var b) && b.Mode == 0 && b.Change != Bank.Zero && Same(b.Player, e.Player))
        {
            if (!book.Economy.Buildings.TryGetValue(e.Building, out var building)) { Unchecked(Build, b, "постройки нет в Gbuild"); return; }
            Check(Build, b, Treasury.Pay(b.Before, Treasury.BuildingPrice(building)), $"{e.Building} COST {Show(building.Cost)}");
            return;
        }
        if (Same(e.Id2, EmptyId)) tally.Skip(Build, "бесплатно: стартовая постройка на загрузке карты (id2 = G000000000)");
        else if (e.Flag == 1) tally.Skip(Build, "бесплатно: начальная постройка ИИ (flag = 1)");
        else tally.Count(Build, false, $"{Name} seq {e.Seq}: {e.Building} без строки bank и без признака бесплатной", true);
    }

    private void OnBattleEnd(BattleEndEvent e)
    {
        if (e.T is not { } t) return;
        var rows = j.NestedBanks(e.Seq, t, 2);
        if (rows.Count == 0) return;
        var w = rows[0];
        // Опознание — только по месту: две строки bank разных игроков первыми за battle_end в тот же миг, и строка
        // второго двигает казну. Обе стороны затем сверяются с моделью (обнуление погибшего — тоже сверка).
        if (rows.Count == 2 && rows[1] is var l && l.Change != Bank.Zero && !Same(w.Player, l.Player))
        {
            var (winner, loser) = Treasury.CapitalCapture(w.Before, l.Before, victory: false);
            Check(Capture, w, winner, $"захватчик {w.Player} + казна {l.Player} {Show(l.Before)}");
            Check(Capture, l, loser, $"погибший {l.Player}: казна обнулена");
            return;
        }
        if (w.Change != Bank.Zero && _lastStart is { } start && IsRuin(start.DefenderGroup)) RuinLoot(e, start, w);
    }

    /// <summary>Руина после победы отдаёт <c>MidRuin.CASH</c> в казну нападавшего — та же добыча, что у
    /// <see cref="Sites.Visit"/> (<see cref="VisitResult.Gained"/>); берётся из снимка до боя.</summary>
    private void RuinLoot(BattleEndEvent end, BattleStartEvent start, BankEvent b)
    {
        var ruin = end.T is { } t ? j.SaveBefore(t)?.Sites.FirstOrDefault(s => Same(s.Id, start.DefenderGroup)) : null;
        if (ruin is null || !Same(end.Winner, start.AttackerGroup) || !Same(start.AttackerPlayer, b.Player))
        { Unchecked(Ruin, b, "руины нет в снимке до боя или победил не нападавший"); return; }
        Check(Ruin, b, Treasury.Apply(b.Before, ruin.Cash), $"{ruin.Id} CASH {Show(ruin.Cash)}, победил {end.Winner}");
    }

    private static bool IsRuin(string id) => id.Length == 10 && id.AsSpan(4, 2).Equals("RU", StringComparison.OrdinalIgnoreCase);

    private void Tributes()
    {
        var free = j.Events.OfType<BankEvent>()
            .Where(x => x.Mode == 0 && x.Change != Bank.Zero && !_claimed.ContainsKey(x.Seq)).OrderBy(x => x.Seq).ToList();
        for (int i = 0; i + 1 < free.Count; i++)
        {
            var (to, from) = (free[i], free[i + 1]);
            long gold = to.Change.Gold;
            // Опознание — по форме: получатель прибавил только золото, следующей строкой другой игрок золото
            // убавил. Сумма дани берётся из строки получателя, поэтому сверяется плательщик: модель обязана
            // списать ровно столько и ничего больше; строка получателя — «причина есть, цена не сверена».
            if (from.Seq != to.Seq + 1 || gold <= 0 || Same(to.Player, from.Player)
                || to.Change != Economy.Currency(0, gold) || from.Change.Gold >= 0) continue;
            _claimed[to.Seq] = Resolution.Unchecked;
            tally.Skip(Tribute, "получатель: сумма дани взята из его же строки, сверяется только плательщик");
            Check(Tribute, from, Treasury.Tribute(from.Before, to.Before, gold).Payer, $"{from.Player} -> {to.Player}: {gold} золота");
            i++;
        }
    }

    // ------------------------------------------------------------ остаток

    private void Leftovers()
    {
        var ends = j.Events.OfType<BattleEndEvent>().Select(x => x.Seq).ToHashSet();
        var enroll = book.Economy.Hire.Values.Select(h => h.Cost).ToHashSet();
        var casting = book.Spells.Values.Select(s => s.CastingCost).Where(c => c != Bank.Zero).ToHashSet();
        foreach (var b in j.Events.OfType<BankEvent>())
        {
            tally.BankLines++;
            if (b.Mode != 0) { tally.ModeOne++; continue; }
            if (b.Change == Bank.Zero) continue;
            tally.Moves++;
            if (_claimed.TryGetValue(b.Seq, out var how)) { tally.Account(how); continue; }
            tally.NoCause(Label(b, ends, enroll, casting), $"{Name} seq {b.Seq} {b.Player}: {Show(b.Before)} -> {Show(b.After)}");
        }
    }

    private string Label(BankEvent b, HashSet<long> ends, HashSet<Bank> enroll, HashSet<Bank> casting)
    {
        var spent = Bank.Zero - b.Change;
        var (a, c) = (b.After, b.Change);
        if (new[] { a.Gold, a.Infernal, a.Life, a.Death, a.Runic, a.Grove }.All(v => v == Economy.CurrencyMax))
            return "чит moneyfornothing: казна стала 9999 по всем шести";
        if (spent == Treasury.RodPrice(book.Treasury))
            return "жезл: списано ровно ROD_COST (события-причины в схеме нет)";
        if (enroll.Contains(spent))
            return $"наём в столице, {Who(b)}: списано ровно ENROLL_C (события нет)";
        if (c.Gold == 0 && casting.Contains(spent))
            return $"каст заклинания, {Who(b)}: списано ровно CASTING_C (события нет)";
        if (c.Gold == 0 && new[] { c.Infernal, c.Life, c.Death, c.Runic, c.Grove }.All(v => v <= 0))
            return $"трата маны без события, не равная CASTING_C, {Who(b)}";
        if (ends.Contains(b.Seq - 1))
            return "приход сразу за battle_end без захвата и без руины";
        if (a == Bank.Zero && j.BankBySeq.TryGetValue(b.Seq - 1, out var one) && one.Mode == 1 && Same(one.Player, b.Player))
            return $"казна обнулена сразу за строкой mode 1 того же игрока (второй кошелёк?), {Who(b)}";
        if (a == Bank.Zero)
            return $"казна обнулена без захвата, {Who(b)}";
        return "прочее";
    }

    private string Who(BankEvent b) => PlayerIn(j.SaveNear(b.T), b.Player)?.IsHuman switch
    {
        true => "человек", false => "ИИ", null => "игрок не опознан",
    };

    // ------------------------------------------------------------ общее

    private BankEvent? Nested(string kind, long causeSeq, bool ret, string? player)
    {
        if (!ret) { tally.Skip(kind, "ret = false: движок отказал, казна не двигалась"); return null; }
        if (j.NestedBank(causeSeq, player) is { } b) return b;
        tally.Count(kind, false, $"{Name} seq {causeSeq}: нет строки bank (mode 0) до следующей причины при ret = true", true);
        return null;
    }

    private void Check(string kind, BankEvent b, Bank expected, string what, bool strict = true)
    {
        _claimed[b.Seq] = strict ? Resolution.Model : Resolution.Observation;
        tally.Count(kind, b.After == expected,
            $"{Name} seq {b.Seq} {b.Player}: {what}; было {Show(b.Before)}, журнал {Show(b.After)}, модель {Show(expected)}",
            strict);
    }

    private void Unchecked(string kind, BankEvent b, string reason)
    {
        _claimed.TryAdd(b.Seq, Resolution.Unchecked);
        tally.Skip(kind, reason);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static MapPlayer? PlayerIn(SaveAt? s, string id) =>
        s is not null && s.Map.Players.TryGetValue(id, out var p) ? p : null;

    private static MapUnit? UnitIn(SaveAt? s, string id) =>
        s is not null && s.Map.Units.TryGetValue(id, out var u) ? u : null;

    public static string Show(Bank b) => $"{b.Gold}/{b.Infernal},{b.Life},{b.Death},{b.Runic},{b.Grove}";
}

/// <summary>Счётчики харнесса казны: сравнения по видам причин, пропуски с причиной, движения без
/// причины в журнале.</summary>
public sealed class TreasuryTally
{
    private sealed class Row { public int Same, Differ, Skipped; }

    private const int MaxMismatches = 40;
    private readonly SortedDictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _skips = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, (int N, string Sample)> _noCause = new(StringComparer.Ordinal);
    private readonly List<string> _mismatches = [];

    public int Journals, BankLines, ModeOne, Moves, ByModel, ByObservation, Unchecked, Differed;
    public int NoCauseTotal => _noCause.Values.Sum(v => v.N);

    public void Count(string kind, bool ok, string detail, bool strict)
    {
        var row = RowOf(kind);
        if (ok) { row.Same++; return; }
        row.Differ++;
        if (strict) Differed++;
        if (_mismatches.Count < MaxMismatches) _mismatches.Add($"{kind}: {detail}");
    }

    public void Skip(string kind, string reason)
    {
        RowOf(kind).Skipped++;
        _skips[$"{kind}: {reason}"] = _skips.GetValueOrDefault($"{kind}: {reason}") + 1;
    }

    public void NoCause(string label, string sample) =>
        _noCause[label] = _noCause.TryGetValue(label, out var v) ? (v.N + 1, v.Sample) : (1, sample);

    public void Account(Resolution how)
    {
        if (how == Resolution.Model) ByModel++;
        else if (how == Resolution.Observation) ByObservation++;
        else Unchecked++;
    }

    /// <summary>Сравнений у видов, имя которых начинается с <paramref name="prefix"/>.</summary>
    public int Compared(string prefix) =>
        _rows.Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(r => r.Value.Same + r.Value.Differ);

    public void Print(ITestOutputHelper o, int uniqueJournals, int duplicates)
    {
        o.WriteLine($"казна: журналов с событием bank {Journals} (уникальных журналов корпуса {uniqueJournals}, " +
                    $"дублей по sha256 {duplicates}); строк bank {BankLines}, из них mode 1 {ModeOne}; " +
                    $"движений казны (mode 0, after ≠ before) {Moves}");
        o.WriteLine($"  {"причина",-62} {"сравнений",9} {"сошлось",8} {"разошлось",9} {"пропущено",9}");
        foreach (var (kind, r) in _rows)
            o.WriteLine($"  {kind,-62} {r.Same + r.Differ,9} {r.Same,8} {r.Differ,9} {r.Skipped,9}");
        o.WriteLine("пропуски по причинам:");
        foreach (var (reason, n) in _skips) o.WriteLine($"  {reason} — {n}");
        o.WriteLine("движения казны без причины в журнале (моделью не объяснены):");
        foreach (var (label, v) in _noCause) o.WriteLine($"  {label} — {v.N}; пример: {v.Sample}");
        o.WriteLine($"итог: движений {Moves}; объяснено ценой модели {ByModel}; объяснено наблюдаемым правилом ИИ " +
                    $"{ByObservation}; причина есть, цена не сверена {Unchecked}; без причины в журнале {NoCauseTotal}");
        foreach (var m in _mismatches) o.WriteLine($"РАСХОЖДЕНИЕ {m}");
    }

    private Row RowOf(string kind) => _rows.TryGetValue(kind, out var r) ? r : _rows[kind] = new Row();
}
