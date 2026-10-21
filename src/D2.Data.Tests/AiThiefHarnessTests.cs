using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Решение вора ИИ у цели (<c>docs/ai-tactic-candidates.md</c> §12): на каждом свитке вора игрока ИИ (<c>noble_choose</c>) —
/// список доступного порта (<see cref="GameSession.ThiefActions"/>) против журнала, порог действия
/// (<see cref="GameSession.ThiefChance"/>) против <c>thief_roll.chance</c>, выбор ИИ (<see cref="AiTactics.ThiefChoice"/>,
/// <c>0x45b98a</c>) против действия журнала и украденный у торговца товар против <c>noble_action.id</c>.
/// <para>Выбор идёт на бросках R250, и здесь они настоящие: траектория генератора восстанавливается от строки <c>rng_seed</c> по
/// двум броскам вора (<c>thief_roll</c>: <c>dice1</c>, <c>dice2</c>), а выбор порта стоит на k бросков раньше — k подбирается
/// так, чтобы порт сделал ровно k бросков (перемешивание доступного: n − 1). Состояние — сохранение дня плюс журнал:
/// позиции, бои, опыт, наймы, казна, предметы, приказы, союзы, шпионаж, мешки и передачи предметов.</para>
/// </summary>
public abstract class AiThiefHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Измерено 24.09.2026 (не подгонять): human — свитков вора ИИ 14 (пятнадцатый — вор <c>S073KC0089</c> дня 23, который
    /// в партии переигрывали трижды, в живом дне его нет), список доступного, порог, броски перемешивания сразу перед броском вора и
    /// выбор сошлись у всех 14 (из них дуэль 2 — у лидера цели нет жезла; шпионаж 12 — у жезлоносцев дуэли нет); legions — 19 (без
    /// <c>S073KC009a</c> дня 53: живой день давал нанятому id порта — с 25.09 id движка, и свиток есть, см. ниже), 19, 19, 19, 19, краж у
    /// торговца 8, товар — самый дорогой — 8. До разбора
    /// приказов живого дня выбор человеческого корпуса был 12: вор числился с <c>L_ASSIST</c> (ветка без перемешивания).</summary>
    [SkippableTheory]
    // 25.09: нанятый отряд в живом дне получает id движка (AiLiveDay.Hired: номера погибших движок не переиспользует) — строки журнала
    // о нём больше не теряются. legions — свитков 19 → 20 (<c>S073KC009a</c>), все пункты сошлись; кражи у торговца 9 из 9. human без изменений.
    [CorpusRow("20260919-human", 14, 14, 14, 14, 14, 0, 0)]
    [CorpusRow("20260920-legions", 20, 20, 20, 20, 20, 9, 9)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — вор ИИ: один свиток, всё сошлось, краж нет.
    [CorpusRow("20261019c-cov", 1, 1, 1, 1, 1, 0, 0)]
    // 20.10 (r20/batB): карта 23 — свитков ИИ 82: список 81 (KC0053 → KC0055 день 44: у движка в цели больше одного юнита — Misfit),
    // порог 81 (KC0034 → KC0017 день 25: лидер цели вырос утром чужого хода, 65 против 70 — правило в ThiefAudit, D2.Rules), броски 79,
    // выбор 76; краж у торговца 34 из 34.
    // 20.10 (r20/rest): «подложный приказ» полезен только при малом юните с атакой «все»/«любой» (0x45c1e5 → getSizeSmall 0x5e9830; И156) —
    // FT000b дня 58 у KC001c и KC005f отравление, как у движка: выбор 76 → 78.
    [CorpusRow("20261020-port-synth", 82, 81, 81, 79, 78, 34, 34)]
    public void ThiefChoices_MatchJournal(string corpus, int wantVisits, int wantList, int wantChance, int wantDraws,
                                          int wantChoice, int wantSteals, int wantStealSame)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var saves = Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal)
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Last());
        var events = JournalCache.Read(journal, ["ai_budget_spend", "ai_turn", "stack_move", "battle_end", "hire_leader", "xp", "bank", "item_create",
                                                 "potion_use", "item_consume", "site_buy_item", "noble_choose", "thief_roll",
                                                 "noble_action", "alliance_change", "ai_tactic_result", "ai_tactic_pick", "rng_seed",
                                                 "ai_order"])
            .OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);
        var spiedToday = AiTacticFromMapHarnessTests.SpiedByDay(events);

        AiLiveDay? live = null;
        RngSeedEvent? seed = null;
        (NobleChooseEvent Choose, GameSession Session, string Target)? open = null;
        string? stealPick = null, stealStack = null;
        bool chosenGive = false;
        int visits = 0, listSame = 0, chanceSame = 0, drawsFound = 0, choiceSame = 0, steals = 0, stealSame = 0;
        foreach (var e in events)
        {
            switch (e)
            {
                case RngSeedEvent s:
                    seed = s;
                    break;
                case AiTurnEvent { Phase: "begin" } t when t.Player is not null:
                    if (live is null || live.Day != t.Turn)
                        live = saves.TryGetValue((int)t.Turn, out var sg)
                            ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends)
                            {
                                NextSpied = AiTacticFromMapHarnessTests.SpiedOf(saves, (int)t.Turn + 1),
                                SpiedToday = spiedToday.GetValueOrDefault((int)t.Turn),
                            }
                            : null;
                    live?.LoseSpied(t.Player);
                    chosenGive = false;
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
                case BankEvent { Mode: 0 } bk when live is not null:
                    live.SetBank(bk.Player, bk.After, journalRow: true);
                    break;
                case SiteBuyItemEvent bi when live is not null:
                    live.BuyItem(bi);
                    break;
                case ItemCreateEvent { Ret: true } ic when live is not null && ic.Cause != "site_buy_item"
                                                          && ic.Ids.TryGetValue("stack", out var holder) && ic.Ids.TryGetValue("item_type", out var made):
                    live.GainItem(holder, made);
                    break;
                case PotionUseEvent { Ret: true, Destroy: true } pu when live is not null:
                    live.LoseItem(pu.Item);
                    break;
                case ItemConsumeEvent { Consumed: true } used when live is not null:
                    live.LoseItem(used.ItemBefore);
                    break;
                case AllianceChangeEvent ac when live is not null && ac.Ret:
                    live.Alliance(ac);
                    break;
                case AiOrderEvent ao when live is not null:
                    live.Order(ao);
                    break;
                case AiTacticPickEvent { PhaseBefore: > 3 } p4:
                    chosenGive = p4.Chosen is [{ Action.Vft: "0x6d09c4" }, ..];
                    break;
                case AiTacticResultEvent r when live is not null && r.Stack is not null:
                    if (r.Status is AiTactic.StatusProgress or 2 && r.Target is { Length: > 0 } bagId) live.TakeBag(bagId, r.Stack);
                    if (r.Status == AiTactic.StatusStuck && chosenGive && r.Target is { Length: > 0 } receiver) live.GiveItems(r.Stack, receiver);
                    break;
                case NobleChooseEvent nc when live is not null:
                {
                    var session = live.Session();
                    if (!session.Map.Players.TryGetValue(nc.Player, out var owner) || owner.IsHuman) break;
                    if (!session.Map.Stacks.ContainsKey(nc.Stack))
                    {
                        output.WriteLine($"{nc.Stack} → {nc.Target} день {live.Day}: вора нет в живом дне");
                        break;
                    }
                    visits++;
                    string target = AiTactics.ThiefTargetOf(session, nc.Target);
                    var port = session.ThiefActions(nc.Stack, target).Select(a => (long)a).ToList();
                    if (port.SequenceEqual(nc.Actions)) listSame++;
                    else output.WriteLine($"{nc.Stack} → {nc.Target} день {live.Day}: список журнал [{string.Join(",", nc.Actions)}], порт [{string.Join(",", port)}]");
                    open = (nc, session, target);
                    break;
                }
                case ThiefRollEvent roll when open is { } o && roll.Action is { } act:
                {
                    open = null;
                    int chance = o.Session.ThiefChance(o.Target, (ThiefAction)act);
                    if (chance == roll.Chance) chanceSame++;
                    else output.WriteLine($"{o.Choose.Stack} → {o.Target} день {live?.Day}: порог журнал {roll.Chance}, порт {chance}");
                    var (choice, k) = ChoiceOnJournalRolls(o.Session, seed, roll, o.Choose.Stack, o.Target);
                    if (k >= 0) drawsFound++;
                    (stealPick, stealStack) = (choice?.Pick, o.Choose.Stack);
                    if (Environment.GetEnvironmentVariable("D2_THIEF_SHOW") is not null
                        && o.Session.Map.Sites.FirstOrDefault(x => x.Id.Equals(o.Target, StringComparison.OrdinalIgnoreCase)) is { Kind: SiteKind.Merchant } shop)
                        output.WriteLine($"  склад {shop.Id}: [{string.Join(" ", shop.Stock.Select(r => $"{r.Item}x{r.Count}:{(book.Items.TryGetValue(r.Item, out var it) ? Treasury.ItemBuyPrice(it).Gold : -1)}"))}]");
                    if (choice?.Action == (ThiefAction)act) choiceSame++;
                    else output.WriteLine($"{o.Choose.Stack} → {o.Target} день {live?.Day}: выбор журнал {(ThiefAction)act}, порт {choice?.Action} (бросков {k})");
                    break;
                }
                case NobleActionEvent na:
                    if (na.Action == (int)ThiefAction.StealMerchant && string.Equals(stealStack, na.Stack, StringComparison.OrdinalIgnoreCase))
                    {
                        steals++;
                        if (string.Equals(stealPick, na.Id, StringComparison.OrdinalIgnoreCase)) stealSame++;
                        else output.WriteLine($"{na.Stack} у {na.Target}: товар журнал {na.Id}, порт {stealPick}");
                    }
                    (stealPick, stealStack) = (null, null);
                    live?.Noble(na);
                    break;
            }
        }
        output.WriteLine($"{corpus}: свитков ИИ {visits}, список {listSame}, порог {chanceSame}, броски найдены {drawsFound}, выбор {choiceSame}, "
                         + $"краж у торговца {steals}, товар {stealSame}");
        Assert.Equal(wantVisits, visits);
        Assert.Equal(wantList, listSame);
        Assert.Equal(wantChance, chanceSame);
        Assert.Equal(wantDraws, drawsFound);
        Assert.Equal(wantChoice, choiceSame);
        Assert.Equal(wantSteals, steals);
        Assert.Equal(wantStealSame, stealSame);
    }

    /// <summary>
    /// Выбор порта на бросках движка: траектория R250 от последнего <c>rng_seed</c> до броска вора (два <c>rand(100)</c> —
    /// <c>dice1</c>, <c>dice2</c>; <see cref="EngineRng.FindAhead"/>), затем генератор на k шагов раньше, и k — первое из 0…4, при
    /// котором порт делает ровно k бросков (перемешивание стоит сразу перед броском вора). Не нашлось — (null, −1).
    /// </summary>
    private static ((ThiefAction Action, string? Pick)? Choice, int Draws) ChoiceOnJournalRolls(GameSession session, RngSeedEvent? seed,
                                                                                               ThiefRollEvent roll, string stack, string target)
    {
        if (seed is null || roll.Dice1 is not { } d1 || roll.Dice2 is not { } d2) return (null, -1);
        long known = Math.Max(roll.RngBefore - seed.N, 0);
        var found = EngineRng.FromSeed(seed.Table, (int)seed.IndexAfter)
            .FindAhead([(Hundred, (int)d1), (Hundred, (int)d2)], known, known + SearchSlack);
        if (found is null) return (null, -1);
        for (int k = 0; k <= MaxChoiceDraws; k++)
        {
            var gen = EngineRng.FromSeed(seed.Table, (int)seed.IndexAfter);
            gen.Advance(found.Steps - k);
            var counting = new CountingRng(gen);
            var choice = AiTactics.ThiefChoice(session, counting, stack, target);
            if (counting.Calls == k) return (choice, k);
        }
        return (null, -1);
    }

    private const int Hundred = 100;

    /// <summary>Запас поиска траектории сверх счётчика вызовов: шагов бывает больше вызовов (отбраковка хвоста диапазона).</summary>
    private const int SearchSlack = 50_000;

    private const int MaxChoiceDraws = 4;
}

/// <summary>Партии харнесса — отдельными классами, чтобы шли параллельно (<see cref="CorpusRowAttribute"/>).</summary>
[OnlyCorpus("20260919-human")]
[Trait("Category", "Corpus")]
public sealed class AiThiefHarnessTests_Human(ITestOutputHelper output) : AiThiefHarnessTests(output);

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class AiThiefHarnessTests_Legions(ITestOutputHelper output) : AiThiefHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class AiThiefHarnessTests_Cov(ITestOutputHelper output) : AiThiefHarnessTests(output);

[OnlyCorpus("20261020-port-synth")]
[Trait("Category", "Synth")]
public sealed class AiThiefHarnessTests_PortSynth23(ITestOutputHelper output) : AiThiefHarnessTests(output);
