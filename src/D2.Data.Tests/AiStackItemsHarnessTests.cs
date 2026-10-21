using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Подготовка отряда ИИ перед оценкой (<c>docs/ai-items.md</c>): зелья и снаряжение. «Живой день»
/// (<see cref="AiLiveDay"/>): сохранение дня, поверх — ходы, бои, наймы и приказы из журнала; на каждом выборе отряда
/// (<c>ai_tactic_stack</c>) порт делает подготовку (<see cref="AiStackItems.Prepare"/>) и сравнивает с тем, что движок сделал
/// с этим отрядом с прошлого выбора: зелья вне боя (<c>potion_use</c>: предмет и юнит, по порядку) и снаряжение — семь
/// слотов после подготовки против слотов «до», к которым применены <c>item_equip</c>/<c>item_unequip</c> журнала (надевание
/// после выпитого зелья идёт без <c>equip_check</c>, поэтому по командам не сверить). Перед первым выбором в ходе игрока —
/// ещё проход по очереди (<see cref="GameSession.PrepareTactic"/>), и тогда сравнивается всё, что движок сделал с начала хода.
/// <para>Юниты (<c>docs/ai-items.md</c> §6, <see cref="AiStackUnits"/>): переносы гарнизон ↔ отряд (<c>unit_transfer</c> с
/// <c>ai_reorder</c>: группы, клетки, юниты — по порядку) и лечение с воскрешением тактики (<c>heal</c>/<c>revive</c> вне
/// <c>ai_exec</c> стратегии). Наймы в гарнизоны здесь берутся из журнала (<c>hire_unit</c>), а не стратегией порта: иначе
/// сверяется не перестановка, а найм. Остаток (24.09): клетки гарнизона и id новых отрядов «живого дня» расходятся с
/// движком (с 25.09 нанятые лидеры получают id движка — <c>AiLiveDay.Hired</c>; юниты найма в гарнизон — нет), у лечения — казна и здоровье. Зелья и снаряжение human 45 → 46 из 54: юнит из гарнизона теперь в отряде, как у
/// движка. С расстановкой гарнизонов в начале стратегии (<c>0x44edee</c>) клетки гарнизона совпадают чаще: юниты 246 → 252
/// и 62 → 64.</para>
/// </summary>
public abstract class AiStackItemsHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableTheory]
    // 25.09: нанятый отряд в живом дне получает id движка (AiLiveDay.Hired: номера погибших движок не переиспользует) — строки журнала
    // о нём больше не теряются. Переносы human 252 → 259; legions — окна 43 → 44, снаряжение 42 → 43, переносы 64 → 68.
    // 25.09 (вечер): живой день с памятью трат стратегии ИИ из журнала (AiSpendMemory: ai_budget_spend — в .sg её нет, а от неё бюджет
    // дня): legions окна 44 → 45, снаряжение 43 → 44. Без памяти (переключателем) — прежние числа.
    // 26.09: живой день берёт мешки, взятые действием (ai_tactic_result 5/2 с целью-мешком), и покупки у торговца (site_buy_item) — предметы,
    // которые движок потом надевает: human сошлось 46 → 47, снаряжение 46 → 47; legions окон 59 → 60, сошлось 45 → 47, снаряжение 44 → 46.
    // Без них — прежние числа.
    // 27.09: из отряда с павшим лидером воины не уходят (правило сервера CStackSwapUnitMsg, найдено сессией Б в окне обмена) —
    // human переносы 259 → 261; со старым правилом — прежние числа.
    // 15.10: обмен с большим юнитом сводит позиции к передним клеткам колонок (0x6116ba), а не отказывает «не с переднего ряда» —
    // legions переносы 68 → 69; с прежним отказом — прежнее число.
    // 20.10 (r20/batB, И26/И33): живой день повторяет передачу предметов (CAiGiveItemsAction, статус 1), казну движка (bank mode 0; плата
    // за сверяемое лечение — казной «до») и захваты крепостей, а покупку не кладёт в сумку дважды (item_create покупки пропускается):
    // по шагам — передача: legions окон 60 → 58, подготовка 47 → 48, снаряжение 46 → 47 (human без сдвига); казна и захваты: human юниты
    // 261 → 262, legions 69 → 72 (окон 77 → 76; подготовка 48 → 47, снаряжение 47 → 46, окон 57); покупка без двойника: human подготовка 47 → 50, снаряжение
    // 47 → 50, legions подготовка 47 → 50, снаряжение 46 → 49.
    [CorpusRow("20260919-human", 54, 50, 48, 50, 267, 262, 335)]
    // 20.10 (r20/aiA, слияние с r20/batB): уход стратегии по юниту (И110), отступление бежавшего защитника (И93) — legions подготовка
    // 50 → 52, снаряжение 49 → 51, юниты 72 → 73.
    [CorpusRow("20260920-legions", 57, 52, 52, 51, 76, 73, 138)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — подготовка 19 из 23 окон, юниты 20 из 23
    // (дни 2 и 14: у движка в подготовке лечение юнитов, у порта нет; у KC0001–KC0003 движок снял предметы со слотов, порт — нет) — остаток класса И33, как у human 47 из 54.
    // 20.10 (r20/batB): «сняты со слотов» — передача предметов (окон 23 → 20), лечение — казна движка: юниты 23 из 23, подготовка 19 из 20.
    [CorpusRow("20261019c-cov", 20, 19, 16, 16, 23, 23, 40)]
    // 19.10 (r19b/m1920): карта 20 — раненый P (S143KC000a) пьёт оба лечебных до выбора, как движок (potion_use ×2), подготовка 3 из 3,
    // переносы 5 из 5. Карта 19 — подготовка 3 из 4 (S143KC0002 день 2: движок снимает IM0006/IM0007 из боевых слотов, порт нет), переносы
    // 1 из 10 — id нанятых в гарнизон юнитов живого дня на 1 меньше движка (UN009C против UN009D), состав переносов тот же.
    // 20.10 (r20/batB): у S143KC0002 дня 2 «снятые» IM0006/IM0007 — передача предметов: окон 4 → 3, сошлись 3 из 3.
    [CorpusRow("20261019b-port-synth", 3, 3, 0, 3, 10, 1, 22, "port-synth-19-ai-spells", 557L, 657903L)]
    [CorpusRow("20261019b-port-synth", 3, 3, 2, 3, 5, 5, 13, "port-synth-20-ai-battle", 1164985L, 1947173L)]
    // 20.10 (r20/batB): карта 23 (20261020-port-synth, 60 дней) — подготовка 52 из 57 окон (зелий журнала 32), снаряжение 47 из 50, юниты
    // 359 из 361 (строк 1044). Остаток — живой день: предмет IM0011 у KC001a дня 2 (откуда — журнал не говорит), порядок сумки KC000a
    // дня 58 (И82), зелье KC0028 дня 54 (здоровье живого дня), id отрядов гарнизона дней 32 и 40.
    [CorpusRow("20261020-port-synth", 57, 52, 32, 47, 361, 359, 1044, "port-synth-23-ai-long")]
    public void Preparation_MatchesJournal(string corpus, int wantWindows, int wantSame, int wantPotions, int wantEquips,
                                           int wantUnitWindows, int wantUnitSame, int wantUnitLines,
                                           string stem = "", long fromSeq = 0, long toSeq = 0)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        // Журнал нескольких карт (20261019b-port-synth: карты 19–22) — сохранения карты stem и отрезок seq её партии.
        var saves = Directory.GetFiles(dir, $"{stem}*.sg").Order(StringComparer.Ordinal)
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Last());
        var events = JournalCache.Read(journal, ["ai_budget_spend", "ai_turn", "ai_tactic_stack", "ai_order", "hire_leader", "stack_move", "battle_end",
                                                 "potion_use", "item_equip", "item_unequip", "item_create",
                                                 "unit_transfer", "heal", "revive", "ai_exec", "hire_unit", "ai_tactic_result", "site_buy_item", "ai_tactic_pick", "bank", "fort_capture"])
            .Where(e => e.Seq >= fromSeq && (toSeq == 0 || e.Seq <= toSeq))
            .OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);
        AiLiveDay? live = null;
        var seen = new List<string>();
        var slotEvents = new List<ItemEquipEvent>();
        var seenUnits = new List<string>();
        long execEnd = -1;
        int unitWindows = 0, unitSame = 0, unitLines = 0, unitShown = 0;
        bool started = false;
        bool chosenGive = false;   // фаза 4 исполнила передачу предметов (CAiGiveItemsAction, vtable 0x6d09c4)
        bool careBank = false;     // следующая строка bank — плата за лечение или воскрешение движка
        bool careRun = false;      // в окне уже стоит казна до первой платы за лечение — следующие платы её не трогают
        int windows = 0, same = 0, potions = 0, shown = 0, equipSame = 0, equipWindows = 0;
        var hiresOfTurn = AiSpellHarnessTests.HiresOfTurns(events);
        foreach (var e in events)
            switch (e)
            {
                case AiTurnEvent { Phase: "begin" } t:
                    seen.Clear();
                    slotEvents.Clear();
                    seenUnits.Clear();
                    started = false;
                    chosenGive = careBank = careRun = false;
                    if (live is null || live.Day != t.Turn)
                        live = saves.TryGetValue((int)t.Turn, out var sg) ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends) : null;
                    if (t.Player is not null) live?.Begin(t.Player, hireUnits: false, hires: hiresOfTurn.GetValueOrDefault(t.Seq));
                    break;
                case PotionUseEvent { InBattle: false } p:
                    seen.Add($"зелье {p.Group} {p.Item} → {p.Target}".ToUpperInvariant());
                    break;
                case ItemEquipEvent q:
                    slotEvents.Add(q);
                    break;
                // Юниты гарнизона в отряд (StackReorder) и лечение тактики; лечение стратегии лежит внутри ai_exec.
                case AiExecEvent x:
                    execEnd = Math.Max(execEnd, x.SeqEnd);
                    break;
                case UnitTransferEvent { Cause: "ai_reorder" } ut:
                    seenUnits.Add(Transfer(ut.From, ut.FromPos, ut.Unit, ut.To, ut.ToPos, ut.Other));
                    break;
                case HealEvent { Ret: true } h when h.Seq > execEnd:
                    seenUnits.Add($"лечение {h.Unit}".ToUpperInvariant());
                    careBank = true;
                    break;
                case ReviveEvent { Ret: true } rv when rv.Seq > execEnd:
                    seenUnits.Add($"воскрешение {rv.Unit}".ToUpperInvariant());
                    careBank = true;
                    break;
                // Казна движка (bank mode 0) и захваты крепостей — состояние живого дня, как в AiAttackStrengthHarnessTests: доход начала хода,
                // траты стратегии и чужих ходов сохранение дня не несёт, а лечение в крепости упирается в казну ХОЗЯИНА крепости. Плата за
                // лечение тактики, которое сверяется (строка сразу за heal/revive), — казной «до»: порт лечит в окне сам. Карта 23
                // (20261020-port-synth): KC0028 день 36, KC001a день 40, KC0007 дни 54–55 — без этого «казны не хватает ни на один HP».
                case BankEvent { Mode: 0 } bk when live is not null:
                    if (!(careBank && careRun)) live.SetBank(bk.Player, careBank ? bk.Before : bk.After, journalRow: true);
                    careRun |= careBank;
                    careBank = false;
                    break;
                case FortCaptureEvent fc when live is not null:
                    live.Capture(fc);
                    break;
                case AiTacticStackEvent s when live is not null && s.Stack is not null:
                {
                    careRun = careBank = false;
                    var report = new List<string>();
                    bool first = !started;
                    var before = live.Session().Map.Stacks.Values
                        .Where(x => first ? Same(x.Owner, s.Player) : Same(x.Id, s.Stack))
                        .ToDictionary(x => x.Id, x => D2.Rules.Magic.Equipment.Slots(x).ToArray(), StringComparer.OrdinalIgnoreCase);
                    if (first && s.Player is not null) live.PrepareTactic(s.Player, report);
                    started = true;
                    live.Prepare(s.Stack, report);
                    // Зелья — по порядку.
                    var mine = report.Where(r => r.StartsWith("зелье ", StringComparison.Ordinal) && !r.Contains("отказ", StringComparison.Ordinal))
                                     .Select(r => r.ToUpperInvariant()).ToList();
                    var want = seen.Where(x => first || x.Contains(" " + s.Stack.ToUpperInvariant() + " ", StringComparison.Ordinal)).ToList();
                    seen.Clear();
                    // Снаряжение — итоговые слоты.
                    var after = live.Session().Map.Stacks;
                    var journalSlots = before.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                    foreach (var q in slotEvents)
                        if (journalSlots.TryGetValue(q.Stack, out var js) && q.Slot is >= 0 and < 7)
                            js[q.Slot] = q.Equip ? q.Item ?? "" : "";
                    slotEvents.Clear();
                    bool slotsSame = true, slotsChanged = false;
                    var slotNotes = new List<string>();
                    foreach (var (id, js) in journalSlots)
                    {
                        var ps = after.TryGetValue(id, out var st) ? D2.Rules.Magic.Equipment.Slots(st).ToArray() : before[id];
                        bool changed = !js.SequenceEqual(before[id], StringComparer.OrdinalIgnoreCase) || !ps.SequenceEqual(before[id], StringComparer.OrdinalIgnoreCase);
                        if (!changed) continue;
                        slotsChanged = true;
                        if (js.SequenceEqual(ps, StringComparer.OrdinalIgnoreCase)) continue;
                        slotsSame = false;
                        slotNotes.Add($"{id}: было [{string.Join(",", before[id])}] журнал [{string.Join(",", js)}] порт [{string.Join(",", ps)}]");
                    }
                    // Юниты: переносы и лечение — по порядку.
                    var mineUnits = report.Where(r => (r.StartsWith("перенос ", StringComparison.Ordinal) || r.StartsWith("лечение ", StringComparison.Ordinal)
                                                       || r.StartsWith("воскрешение ", StringComparison.Ordinal)) && !r.Contains("отказ", StringComparison.Ordinal))
                                          .Select(r => r.ToUpperInvariant()).ToList();
                    if (mineUnits.Count > 0 || seenUnits.Count > 0)
                    {
                        unitWindows++;
                        unitLines += seenUnits.Count;
                        if (mineUnits.SequenceEqual(seenUnits)) unitSame++;
                        else if (unitShown++ < 40)
                            output.WriteLine($"день {live.Day} {s.Stack}: юниты журнал [{string.Join("; ", seenUnits)}] порт [{string.Join("; ", mineUnits)}]");
                    }
                    seenUnits.Clear();
                    if (mine.Count == 0 && want.Count == 0 && !slotsChanged) break;
                    windows++;
                    potions += want.Count;
                    if (slotsChanged) { equipWindows++; if (slotsSame) equipSame++; }
                    if (mine.SequenceEqual(want) && slotsSame) same++;
                    else if (shown++ < 30)
                        output.WriteLine($"день {live.Day} {s.Stack}: зелья журнал [{string.Join("; ", want)}] порт [{string.Join("; ", mine)}]; {string.Join(" | ", slotNotes)}");
                    break;
                }
                // Новый предмет: добыча руины, покупка у торговца (у неё есть и site_buy_item — не считать дважды), подарок.
                // Покупку ведёт site_buy_item (live.BuyItem) — её item_create пропускается, иначе предмет в сумке дважды и id сдвигаются
                // (карта 23, 20261020-port-synth: KC001a день 1 — у порта шесть зелий вместо трёх и лишнее во втором боевом слоте).
                case ItemCreateEvent { Ret: true } ic when live is not null && ic.Cause != "site_buy_item" && ic.Ids.TryGetValue("stack", out var holder)
                                                           && ic.Ids.TryGetValue("item_type", out var made):
                    live.GainItem(holder, made);
                    break;
                case StackMoveEvent m when live is not null:
                    live.Move(m.Stack, (int)m.X, (int)m.Y, (int)m.MoveLeft);
                    break;
                case AiOrderEvent o when live is not null:
                    live.Order(o);
                    break;
                case HireUnitEvent hu when live is not null:
                    live.HireUnit(hu);
                    break;
                case HireLeaderEvent hl when live is not null:
                    live.HireLeader(hl);
                    break;
                case BattleEndEvent b when live is not null:
                    live.Battle(b);
                    break;
                // Передача предметов вплотную (статус 1 у выбранной передачи): строки обмена журнал не пишет — вся сумка уходит цели, как в
                // AiTacticFromMapHarnessTests. Карта 23 (20261020-port-synth): KC0025 → KC0017 день 9 (IM0039), KC0027 → KC0028 день 12 (IM003b) —
                // без неё у дарителя предметы оставались в слотах, а у получателя их не было.
                case AiTacticPickEvent { PhaseBefore: > 3 } p4:
                    chosenGive = p4.Chosen is [{ Action.Vft: "0x6d09c4" }, ..];
                    break;
                case AiTacticResultEvent { Status: D2.Rules.Map.AiTactic.StatusStuck, Target: { Length: > 0 } receiver } gr when live is not null && chosenGive:
                    live.GiveItems(gr.Stack!, receiver);
                    chosenGive = false;
                    break;
                // Мешок, взятый действием (с соседней клетки или шагом), и покупки у торговца — предметы, которые потом надевают.
                case AiTacticResultEvent { Status: D2.Rules.Map.AiTactic.StatusProgress or 2, Target: { Length: > 0 } bagId } r when live is not null:
                    live.TakeBag(bagId, r.Stack);
                    break;
                case SiteBuyItemEvent bi when live is not null:
                    live.BuyItem(bi);
                    break;
            }
        output.WriteLine($"{corpus}: окон с юнитами {unitWindows}, сошлось {unitSame}; строк журнала {unitLines}");
        output.WriteLine($"{corpus}: окон с подготовкой {windows}, сошлось {same}; зелий журнала {potions}; окон со сменой снаряжения {equipWindows}, слоты сошлись {equipSame}");
        Assert.Equal(wantWindows, windows);
        Assert.Equal(wantSame, same);
        Assert.Equal(wantPotions, potions);
        Assert.Equal(wantEquips, equipSame);
        Assert.Equal(wantUnitWindows, unitWindows);
        Assert.Equal(wantUnitSame, unitSame);
        Assert.Equal(wantUnitLines, unitLines);
    }

    private static string Transfer(string from, long fromPos, string? unit, string? to, long toPos, string? other) =>
        $"перенос {from}[{fromPos}] {unit} → {to}[{toPos}] {(string.IsNullOrEmpty(other) ? "-" : other)}".ToUpperInvariant();

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Партии харнесса — отдельными классами, чтобы шли параллельно (<see cref="CorpusRowAttribute"/>).</summary>
[OnlyCorpus("20260919-human")]
[Trait("Category", "Corpus")]
public sealed class AiStackItemsHarnessTests_Human(ITestOutputHelper output) : AiStackItemsHarnessTests(output);

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class AiStackItemsHarnessTests_Legions(ITestOutputHelper output) : AiStackItemsHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class AiStackItemsHarnessTests_Cov(ITestOutputHelper output) : AiStackItemsHarnessTests(output);

[OnlyCorpus("20261019b-port-synth", Stem = "port-synth-19-ai-spells")]
[Trait("Category", "Synth")]
public sealed class AiStackItemsHarnessTests_PortSynth19(ITestOutputHelper output) : AiStackItemsHarnessTests(output);

[OnlyCorpus("20261019b-port-synth", Stem = "port-synth-20-ai-battle")]
[Trait("Category", "Synth")]
public sealed class AiStackItemsHarnessTests_PortSynth20(ITestOutputHelper output) : AiStackItemsHarnessTests(output);

[OnlyCorpus("20261020-port-synth", Stem = "port-synth-23-ai-long")]
[Trait("Category", "Synth")]
public sealed class AiStackItemsHarnessTests_PortSynth23(ITestOutputHelper output) : AiStackItemsHarnessTests(output);
