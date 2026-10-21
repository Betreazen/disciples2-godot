using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Решения ИИ внутри посещения площадки (<c>docs/ai-tactic-candidates.md</c> §10). Тренер: на каждом <c>site_visit</c>
/// отряда ИИ к тренеру — юниты, которым порт даёт команду обучения (<see cref="AiTactics.TrainAtSite"/>), против строк
/// <c>site_train</c> журнала за этим входом (по порядку), и казна после — против последней строки <c>bank</c>.
/// <para>Состояние — сохранение дня плюс журнал: позиции (<c>stack_move</c>), бои, опыт (<c>xp</c>), наймы лидеров, казна
/// хозяина перед входом — из <c>bank</c>; прежние обучения дня — из <c>site_train</c> общим действием порта.</para>
/// </summary>
public abstract class AiSiteVisitHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Измерено 24.09.2026 (не подгонять): human — посещений тренера ИИ 8, юниты и их порядок сошлись во всех 8,
    /// казна после — тоже; legions — 1, 1, 1. Команда уходит и юнитам, которым казна не оплатила ни очка (день 24 human:
    /// первый забрал 144 из 145 золота, двум другим — по строке без траты). 19.10, карта 16 (<c>20261018b-port-synth</c>) — 4, 4, 4 (было 4, 4, 3:
    /// дневной чит опыта между сохранением дня и началом хода не доходил до живого дня — теперь строки <c>xp</c> с последней <c>ai_turn</c>
    /// повторяются в новом).</summary>
    [SkippableTheory]
    [CorpusRow("20260919-human", 8, 8, 8)]
    [CorpusRow("20260920-legions", 1, 1, 1)]
    [CorpusRow("20261018b-port-synth", 4, 4, 4, "port-synth-16-ai-calm", 437L, 4507811L)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — тренер ИИ 3 из 3.
    [CorpusRow("20261019c-cov", 3, 3, 3, "greenskins-human")]
    // 20.10 (r20/batB): карта 23 — тренер ИИ 80 посещений, казна 80, юниты 78: KC0004 день 14 (у порта UN000f вместо UN00ba — состав
    // отряда живого дня) и KC0007 день 21 (у движка ещё UN007f).
    [CorpusRow("20261020-port-synth", 80, 78, 80, "port-synth-23-ai-long")]
    public void TrainerVisits_MatchJournal(string corpus, int wantVisits, int wantUnits, int wantBank, string stem = "", long from = 0, long to = 0)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var saves = DaySaves(dir, stem);
        var events = JournalCache.Read(journal, ["ai_budget_spend", "ai_turn", "stack_move", "battle_end", "hire_leader", "xp", "bank",
                                                 "site_visit", "site_train", "unit_transfer", "hire_unit"]).Where(e => InRange(e.Seq, from, to)).OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);

        AiLiveDay? live = null;
        var banks = new Dictionary<string, Bank>(StringComparer.OrdinalIgnoreCase);
        // Строки xp после последней строки ai_turn: дневной чит опыта ИИ (0x431d07) идёт после сохранения дня и до ai_turn begin
        // игрока — новый живой день с сохранения их не видел (20261018b, карта 16 день 8: без них порт доплачивал 30 очков — 60 золота).
        var xpSinceTurn = new List<XpEvent>();
        int visits = 0, unitsSame = 0, bankSame = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is AiTurnEvent && events[i] is not AiTurnEvent { Phase: "begin" }) xpSinceTurn.Clear();
            switch (events[i])
            {
                case AiTurnEvent { Phase: "begin" } t when t.Player is not null:
                    if (live is null || live.Day != t.Turn)
                    {
                        live = saves.TryGetValue((int)t.Turn, out var sg) ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends) : null;
                        foreach (var x in xpSinceTurn) live?.Xp(x);
                    }
                    xpSinceTurn.Clear();
                    break;
                case BankEvent { Mode: 0 } b:
                    banks[b.Player] = b.After;
                    break;
                case StackMoveEvent m when live is not null:
                    live.Move(m.Stack, (int)m.X, (int)m.Y, (int)m.MoveLeft);
                    break;
                case BattleEndEvent b when live is not null:
                    live.Battle(b);
                    break;
                case XpEvent x:
                    xpSinceTurn.Add(x);
                    live?.Xp(x);
                    break;
                case HireLeaderEvent h when live is not null:
                    live.HireLeader(h);
                    break;
                // Наймы в гарнизоны и переносы (подготовка отряда тактики: юниты из гарнизона в отряд) — из журнала: от числа юнитов отряда
                // зависят и покупки торговца (MerchantWants), и обучение.
                case HireUnitEvent hu when live is not null:
                    live.HireUnit(hu);
                    break;
                case UnitTransferEvent ut when live is not null:
                    live.Transfer(ut);
                    break;
                case SiteTrainEvent st when live is not null && st.Ret:
                    live.Train(st.Site, st.Unit);
                    break;
                case SiteVisitEvent v when live is not null && v.Ret:
                {
                    var session = live.Session();
                    if (session.Map.Sites.FirstOrDefault(s => s.Id.Equals(v.Site, StringComparison.OrdinalIgnoreCase)) is not { Kind: SiteKind.Trainer }
                        || !session.Map.Players.TryGetValue(v.Owner, out var owner) || owner.IsHuman
                        || !session.Map.Stacks.ContainsKey(v.Stack)) break;
                    // Строки обучения за этим входом (с их строками казны) — до первой другой строки.
                    var trained = new List<string>();
                    Bank? after = null;
                    for (int k = i + 1; k < events.Count && events[k] is SiteTrainEvent or BankEvent; k++)
                        if (events[k] is SiteTrainEvent { Ret: true } row) trained.Add(row.Unit);
                        else if (events[k] is BankEvent { Mode: 0 } bank && bank.Player.Equals(v.Owner, StringComparison.OrdinalIgnoreCase)) after = bank.After;
                    if (banks.TryGetValue(v.Owner, out var before)) live.SetBank(v.Owner, before, journalRow: true);
                    session = live.Session();
                    var sent = AiTactics.TrainAtSite(session, null, v.Stack, v.Site);
                    var bankPort = session.Map.Players[v.Owner].Bank;
                    visits++;
                    bool unitsOk = sent.SequenceEqual(trained, StringComparer.OrdinalIgnoreCase);
                    bool bankOk = after is null ? sent.Count == 0 : bankPort == after;
                    if (unitsOk) unitsSame++;
                    if (bankOk) bankSame++;
                    if (!unitsOk || !bankOk)
                        output.WriteLine($"{v.Stack} у {v.Site} день {live.Day}: журнал [{string.Join(" ", trained)}] казна {after?.Gold}, "
                                         + $"порт [{string.Join(" ", sent)}] казна {bankPort.Gold}");
                    break;
                }
            }
        }
        output.WriteLine($"{corpus}: посещений тренера {visits}, юниты сошлись {unitsSame}, казна {bankSame}");
        Assert.Equal(wantVisits, visits);
        Assert.Equal(wantUnits, unitsSame);
        Assert.Equal(wantBank, bankSame);
    }

    /// <summary>
    /// Посещения торговца и мага вплотную (выбор журнала — действие посещения, vtable <c>0x6d247c</c>/<c>0x6d239c</c>): покупки
    /// журнала за входом против решения порта (<see cref="AiTactics.BuyAtMerchant"/>, <see cref="AiTactics.BuyAtMage"/>) на
    /// том же состоянии и кошельке тактики. Значений бросков R250 журнал не пишет, поэтому сверяется то, что от них не
    /// зависит: сколько куплено и что каждая покупка журнала по модели была возможна (тип есть на складе или в башне, цена в
    /// пределах кошелька на тот момент); число бросков журнала (между концом фазы 4 и следующим выбором) — против модели:
    /// у торговца не меньше покупок и не больше экземпляров склада, у мага — ровно n − 1 перемешивания.
    /// Кошелёк — модель §10: 70 % казны журнала на первой оценке тактики игрока минус обучение, покупки и воскрешения.
    /// <para>Журнал схемы 7.5 пишет и сами броски (<c>ai_rng</c> <c>merchant_item</c>, <c>shuffle_list</c> с <c>origin</c>
    /// <c>mage</c>): тогда порт покупает на них (<see cref="JournalRolls"/>), и посещение сверяется точно — те же покупки по
    /// порядку и ровно броски журнала с теми же границами.</para>
    /// </summary>
    /// <remarks>Измерено 24.09.2026 (не подгонять): human — посещений 7, число покупок сошлось 6, покупки журнала возможны
    /// по модели во всех 7, броски в пределах модели — 7; legions — 9, 9, 9, 9 (у мага и перемешивание n − 1 бросков).
    /// Расхождение human — день 26, <c>S073KC0088</c> у <c>S073SI0005</c>: движок за 9 бросков взял три зелья по 150 при
    /// кошельке 553 (сперва дорогое не по карману, потом кошелёк ниже 400), а порт на своих бросках первым вытянул
    /// <c>G000IG0001</c> за 400 — выбор зависит от значений R250, которых харнесс не знает. 19.10: генератор движка восстановлен от сева по
    /// строкам <c>rng</c> (<see cref="JournalRng"/>, перед первым броском окна) — порт покупает на нём: human 7 из 7 (день 26 — те же три зелья
    /// за 9 бросков), legions 9 из 9, у всех тот же порядок покупок и то же число бросков. 19.10, первый журнал 7.5
    /// (<c>20261018b-port-synth</c>, карта 16: торговцы с одним товаром, маги с одним заклинанием) — 8, 8, 8, 8; на бросках журнала
    /// (<c>merchant_item</c> 18 строк) 8 из 8 точно. Было 7: живой день не повторял переносы <c>unit_transfer</c> подготовки отряда (KC0001 дня 1
    /// взял три юнита из гарнизона — движок купил 4 зелья, порт по одному юниту — 1); теперь переносы и наймы — из журнала.</remarks>
    [SkippableTheory]
    [CorpusRow("20260919-human", 7, 7, 7, 7)]
    [CorpusRow("20260920-legions", 9, 9, 9, 9)]
    [CorpusRow("20261018b-port-synth", 8, 8, 8, 8, 8, "port-synth-16-ai-calm", 437L, 4507811L)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — торговец ИИ 2 из 2, на бросках журнала точно (И39).
    [CorpusRow("20261019c-cov", 2, 2, 2, 2, 2, "greenskins-human")]
    // 20.10 (r20/batB): карта 23 — торговец ИИ 10 посещений, всё сошлось, на бросках журнала (7.5) точно 10 из 10 — живой день повторяет
    // мешки, склад крепости и кражи у торговца (было 8: KC001a день 1 и KC0017 день 5 — у порта больше бросков при тех же покупках).
    [CorpusRow("20261020-port-synth", 10, 10, 10, 10, 10, "port-synth-23-ai-long")]
    public void ShopVisits_MatchJournal(string corpus, int wantVisits, int wantCount, int wantPossible, int wantDraws, int wantRolls = 0,
                                        string stem = "", long from = 0, long to = 0)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var saves = DaySaves(dir, stem);
        var events = JournalCache.Read(journal, ["ai_budget_spend", "ai_turn", "stack_move", "battle_end", "hire_leader", "xp", "bank", "site_visit",
                                                 "site_train", "site_buy_item", "site_buy_spell", "item_create", "potion_use",
                                                 "item_consume", "ai_tactic_pick", "ai_tactic_stack", "revive", "ai_rng", "unit_transfer", "hire_unit", "noble_action", "ai_tactic_result"])
            .Where(e => InRange(e.Seq, from, to)).OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);
        // Схема 7.5: броски покупки — строки ai_rng merchant_item (0x46b805) и shuffle_list с origin mage (0x4495fa из 0x46a8e2)
        // в окне исполнения (после ai_tactic_pick фазы 4 — до следующего выбора отряда, фазы 0); порт покупает на них.
        bool v75 = JournalCache.Read(journal, "start").OfType<StartEvent>().Any(st => st.SchemaAtLeast(7, 5));
        var shopRolls = events.OfType<AiRngEvent>().Where(r => r.Site == "merchant_item" || r.Site == "shuffle_list" && r.Origin == "mage").ToList();
        // До 7.5 значений бросков нет, но генератор движка восстанавливается от сева по строкам rng (JournalRng): покупка — на нём.
        var engine = v75 ? null : new JournalRng(JournalCache.Read(journal, ["rng_seed", "rng"]).Where(e => InRange(e.Seq, from, to)));
        int engineVisits = 0, engineExact = 0;
        var nextSelect = events.OfType<AiTacticPickEvent>().Where(p => p.PhaseBefore == 0).Select(p => p.Seq).ToList();
        long phase4SeqEnd = -1;
        int withRolls = 0, exact = 0;

        AiLiveDay? live = null;
        var banks = new Dictionary<string, Bank>(StringComparer.OrdinalIgnoreCase);
        var purses = new Dictionary<string, Bank>(StringComparer.OrdinalIgnoreCase);
        string? current = null, chosenVft = null;
        long phase4End = -1;
        bool spending = false;   // обучение, покупка или воскрешение ждут своей строки bank
        (string Stack, string Site, AiActionType Type, List<string> Port, int PortDraws, int Pool, List<string> Journal, bool Possible,
         JournalRolls? Rolls, bool Engine)? visit = null;
        int visits = 0, countSame = 0, possible = 0, drawsOk = 0;

        void Close(long rngBefore)
        {
            if (visit is not { } v) return;
            visit = null;
            visits++;
            int draws = phase4End < 0 ? -1 : (int)(rngBefore - phase4End);
            bool countOk = v.Port.Count == v.Journal.Count;
            if (countOk) countSame++;
            if (v.Possible) possible++;
            bool drawOk = v.Type == AiActionType.VisitMage ? draws == v.PortDraws : draws >= v.Journal.Count && draws <= v.Pool;
            if (drawOk) drawsOk++;
            // На бросках журнала (7.5) — точно: те же покупки по порядку и ровно броски журнала с теми же границами.
            bool exactOk = v.Rolls is not { } vr || vr.Exact && v.Port.SequenceEqual(v.Journal, StringComparer.OrdinalIgnoreCase);
            // На генераторе движка (до 7.5) — те же покупки по порядку и столько же бросков, сколько у движка в окне.
            if (v.Engine && v.Port.SequenceEqual(v.Journal, StringComparer.OrdinalIgnoreCase) && draws == v.PortDraws) engineExact++;
            else if (v.Engine) output.WriteLine($"  генератор движка {v.Type} {v.Stack}: бросков движка {draws}, порта {v.PortDraws}");
            if (v.Rolls is { } r0)
            {
                withRolls++;
                if (exactOk) exact++;
                else output.WriteLine($"  броски 7.5 {v.Type} {v.Stack}: журнал {r0.Used + r0.Unused}, порт {r0.Used + r0.Extra}, границы (журнал, порт) "
                                      + $"[{string.Join(" ", r0.BoundsDiffer.Select(b => $"{b.JournalMax}/{b.PortMax}"))}]");
            }
            if (!countOk || !v.Possible || !drawOk || !exactOk)
                output.WriteLine($"{v.Type} {v.Stack} у {v.Site} день {live?.Day}: журнал [{string.Join(" ", v.Journal)}] бросков {draws}, "
                                 + $"порт [{string.Join(" ", v.Port)}] бросков {v.PortDraws}; возможно {v.Possible}");
        }

        foreach (var e in events)
        {
            switch (e)
            {
                case AiTurnEvent { Phase: "begin" } t when t.Player is not null:
                    if (live is null || live.Day != t.Turn)
                        live = saves.TryGetValue((int)t.Turn, out var sg) ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends) : null;
                    // Доход игрока (строки bank до ai_turn begin) — уже в казне журнала, а новый живой день взят из сохранения.
                    if (live is not null && banks.TryGetValue(t.Player, out var income)) live.SetBank(t.Player, income, journalRow: true);
                    current = t.Player;
                    purses.Remove(t.Player);
                    break;
                case BankEvent { Mode: 0 } b:
                    if (b.Player.Equals(current, StringComparison.OrdinalIgnoreCase) && purses.TryGetValue(b.Player, out var pu)
                        && spending)
                        purses[b.Player] = Treasury.Pay(pu, Spent(b.Before, b.After));
                    spending = false;
                    banks[b.Player] = b.After;
                    live?.SetBank(b.Player, b.After, journalRow: true);
                    break;
                // Склад крепости — в сумку отряда на выборе (как AiAttackStrengthHarnessTests), мешки — ниже: от сумки зависит MerchantWants.
                // Кража у торговца снимает экземпляр со склада. Карта 23 (20261020-port-synth): KC001a день 1 (мешок BG0001 — IM0010) у порта хотел
                // на один больше и бросал до конца склада (19 бросков против 5); KC0017 день 5 у S143SI0004 — склад на G000IG1001 больше (вор
                // KC0025 seq 1239796), границы бросков сдвинуты на 1.
                case AiTacticStackEvent fs when live is not null && fs.Stack is not null && (fs.Player is null || purses.ContainsKey(fs.Player)):
                    live.FortItems(fs.Stack);
                    break;
                case NobleActionEvent na when live is not null:
                    live.StealFromMerchant(na);
                    break;
                // Мешок, взятый действием (как AiStackItemsHarnessTests): KC001a день 1 берёт S143BG0001 (IM0010) до торговца.
                case AiTacticResultEvent { Status: D2.Rules.Map.AiTactic.StatusProgress or 2, Target: { Length: > 0 } bagId } br when live is not null:
                    live.TakeBag(bagId, br.Stack);
                    break;
                case AiTacticStackEvent s when live is not null && s.Player is not null && !purses.ContainsKey(s.Player):
                    if (s.Stack is not null) live.FortItems(s.Stack);
                    if (live.Session().Map.Players.TryGetValue(s.Player, out var pl)) purses[s.Player] = AiTactics.TacticPurse(live.Session(), pl);
                    break;
                case AiTacticPickEvent { PhaseBefore: 3 } pick:
                    chosenVft = pick.Top is [{ Action: { } a }, ..] ? a.Vft : null;
                    break;
                case AiTacticPickEvent { PhaseBefore: 4 } pick:
                    phase4End = pick.RngAfter;
                    phase4SeqEnd = pick.SeqEnd;
                    break;
                case AiTacticPickEvent { PhaseBefore: 0 } pick:
                    Close(pick.RngBefore);
                    break;
                case StackMoveEvent m when live is not null:
                    live.Move(m.Stack, (int)m.X, (int)m.Y, (int)m.MoveLeft);
                    break;
                case BattleEndEvent b when live is not null:
                    live.Battle(b);
                    break;
                case XpEvent x when live is not null:
                    live.Xp(x);
                    break;
                case HireLeaderEvent h when live is not null:
                    live.HireLeader(h);
                    break;
                // Наймы в гарнизоны и переносы (подготовка отряда тактики: юниты из гарнизона в отряд) — из журнала: от числа юнитов отряда
                // зависят и покупки торговца (MerchantWants), и обучение.
                case HireUnitEvent hu when live is not null:
                    live.HireUnit(hu);
                    break;
                case UnitTransferEvent ut when live is not null:
                    live.Transfer(ut);
                    break;
                case SiteTrainEvent st when live is not null && st.Ret:
                    live.Train(st.Site, st.Unit);
                    break;
                case ItemCreateEvent { Ret: true } ic when live is not null && ic.Cause != "site_buy_item"
                                                          && ic.Ids.TryGetValue("stack", out var holder) && ic.Ids.TryGetValue("item_type", out var made):
                    live.GainItem(holder, made);
                    break;
                case PotionUseEvent { Ret: true, Destroy: true } pot when live is not null:
                    live.LoseItem(pot.Item);
                    break;
                case ItemConsumeEvent { Consumed: true } used when live is not null:
                    live.LoseItem(used.ItemBefore);
                    break;
                case SiteVisitEvent v when live is not null && v.Ret && chosenVft is "0x6d247c" or "0x6d239c":
                {
                    var session = live.Session();
                    if (!session.Map.Players.TryGetValue(v.Owner, out var owner) || owner.IsHuman || !session.Map.Stacks.ContainsKey(v.Stack)
                        || !purses.TryGetValue(v.Owner, out var purse)) break;
                    var type = chosenVft == "0x6d247c" ? AiActionType.VisitMerchant : AiActionType.VisitMage;
                    // Решение порта — на копии состояния, с кошельком модели; живой день дальше идёт по покупкам журнала.
                    var turn = new AiTacticTurn(v.Owner, book.AiWeights.Values.First(), [], live.Day, AiTacticMemo.Empty) { Purse = purse };
                    JournalRolls? rolls = null;
                    if (v75 && phase4SeqEnd >= 0)
                    {
                        // Окно исполнения: от конца выбора фазы 4 до следующего выбора отряда; броски — хозяина посещения.
                        int at = nextSelect.BinarySearch(v.Seq);
                        int k = at < 0 ? ~at : at + 1;
                        long until = k < nextSelect.Count ? nextSelect[k] : long.MaxValue;
                        var own = shopRolls.Where(r => Same(r.Player, v.Owner));
                        rolls = new JournalRolls(JournalRolls.Between(own, phase4SeqEnd, until, ["merchant_item", "shuffle_list"]),
                                                 new D2.Rules.Rng.EngineRng());
                    }
                    var atEngine = engine is not null && phase4End >= 0 ? engine.At(v.Seq, phase4End + 1, minBits: 48) : null;
                    if (atEngine is not null) engineVisits++;
                    var rng = new CountingRng(rolls ?? (D2.Rules.Rng.IRng?)atEngine ?? new D2.Rules.Rng.EngineRng());
                    int pool = session.Map.Sites.First(x => Same(x.Id, v.Site)).Stock.Sum(r => Math.Max(r.Count, 0));
                    var bought = type == AiActionType.VisitMerchant
                        ? AiTactics.BuyAtMerchant(session, turn, rng, v.Stack, v.Site)
                        : AiTactics.BuyAtMage(session, turn, rng, v.Stack, v.Site);
                    visit = (v.Stack, v.Site, type, [.. bought], rng.Calls, pool, [], true, rolls, atEngine is not null);
                    if (Environment.GetEnvironmentVariable("D2_SHOP_SHOW") is not null)
                    {
                        var site0 = live.Session().Map.Sites.First(x => Same(x.Id, v.Site));
                        output.WriteLine($"  вход {v.Stack} у {v.Site} день {live.Day}: кошелёк {purse.Gold} казна {live.Session().Map.Players[v.Owner].Bank.Gold} "
                                         + $"склад [{string.Join(" ", site0.Stock.Select(r => $"{r.Item}x{r.Count}:{(book.Items.TryGetValue(r.Item, out var it) ? it.Value.Gold : -1)}"))}] "
                                         + $"башня [{string.Join(" ", site0.Spells)}] сумка {live.Session().Map.Stacks[v.Stack].Items.Count} юнитов {live.Session().Map.Stacks[v.Stack].UnitIds.Count}");
                    }
                    break;
                }
                case SiteBuyItemEvent bi when live is not null:
                    if (visit is { } vi && Same(vi.Stack, bi.Stack)) visit = vi with { Possible = vi.Possible && Affordable(live, bi.Site, bi.ItemType, true, purses, bi.Stack) };
                    visit?.Journal.Add(bi.ItemType.ToUpperInvariant());
                    live.BuyItem(bi);
                    break;
                case SiteBuySpellEvent bs when live is not null:
                    if (visit is { } vs) visit = vs with { Possible = vs.Possible && Affordable(live, bs.Site, bs.Spell, false, purses, vs.Stack) };
                    visit?.Journal.Add(bs.Spell.ToUpperInvariant());
                    live.BuySpell(bs);
                    break;
            }
            if (e is SiteTrainEvent or SiteBuyItemEvent or SiteBuySpellEvent or ReviveEvent) spending = true;
        }
        output.WriteLine($"{corpus}: посещений {visits}, число покупок сошлось {countSame}, покупки возможны по модели {possible}, "
                         + $"броски сходятся с моделью {drawsOk}" + (v75 ? $"; на бросках журнала (7.5) {withRolls}, точно {exact}" : $"; на генераторе движка {engineVisits}, точно {engineExact}"));
        Assert.Equal(wantVisits, visits);
        Assert.Equal(wantCount, countSame);
        Assert.Equal(wantPossible, possible);
        Assert.Equal(wantDraws, drawsOk);
        Assert.Equal(wantRolls, withRolls);
        Assert.Equal(withRolls, exact);
        Assert.Equal(engineVisits, engineExact);
    }

    /// <summary>Последнее сохранение каждого дня (<c>-tNN-</c>) с началом имени <paramref name="stem"/> (журнал нескольких карт).</summary>
    private static Dictionary<int, string> DaySaves(string dir, string stem) =>
        Directory.GetFiles(dir, stem + "*.sg").Order(StringComparer.Ordinal)
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Last());

    private static bool InRange(long seq, long from, long to) => seq >= from && (to == 0 || seq <= to);

    /// <summary>Покупка журнала возможна по модели: предмет на складе торговца (заклинание в башне), цена в пределах
    /// кошелька тактики модели.</summary>
    private static bool Affordable(AiLiveDay live, string siteId, string id, bool item, Dictionary<string, Bank> purses, string stackId)
    {
        var session = live.Session();
        var map = session.Map;
        if (!map.Stacks.TryGetValue(stackId, out var stack) || !purses.TryGetValue(stack.Owner, out var purse)
            || map.Sites.FirstOrDefault(s => Same(s.Id, siteId)) is not { } site) return false;
        var book = session.Book!;
        if (item)
            return site.Stock.Any(r => Same(r.Item, id) && r.Count > 0) && book.Items.TryGetValue(id, out var it)
                   && !Economy.NotEnough(purse, Treasury.ItemBuyPrice(it));
        return site.Spells.Any(s => Same(s, id)) && book.Spells.TryGetValue(id, out var sp) && !Economy.NotEnough(purse, Treasury.SpellBuyPrice(sp));
    }

    private static Bank Spent(Bank before, Bank after) => new(
        Math.Max(before.Gold - after.Gold, 0), Math.Max(before.Infernal - after.Infernal, 0), Math.Max(before.Life - after.Life, 0),
        Math.Max(before.Death - after.Death, 0), Math.Max(before.Runic - after.Runic, 0), Math.Max(before.Grove - after.Grove, 0));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Партии харнесса — отдельными классами, чтобы шли параллельно (<see cref="CorpusRowAttribute"/>).</summary>
[OnlyCorpus("20260919-human")]
[Trait("Category", "Corpus")]
public sealed class AiSiteVisitHarnessTests_Human(ITestOutputHelper output) : AiSiteVisitHarnessTests(output);

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class AiSiteVisitHarnessTests_Legions(ITestOutputHelper output) : AiSiteVisitHarnessTests(output);

/// <summary>Первый журнал зонда 7.5 (карта 16: у каждого ИИ по торговцу с одним товаром) — покупки на бросках журнала.</summary>
[OnlyCorpus("20261018b-port-synth")]
[Trait("Category", "Synth")]
public sealed class AiSiteVisitHarnessTests_PortSynth16(ITestOutputHelper output) : AiSiteVisitHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class AiSiteVisitHarnessTests_Cov(ITestOutputHelper output) : AiSiteVisitHarnessTests(output);

[OnlyCorpus("20261020-port-synth", Stem = "port-synth-23-ai-long")]
[Trait("Category", "Synth")]
public sealed class AiSiteVisitHarnessTests_PortSynth23(ITestOutputHelper output) : AiSiteVisitHarnessTests(output);
