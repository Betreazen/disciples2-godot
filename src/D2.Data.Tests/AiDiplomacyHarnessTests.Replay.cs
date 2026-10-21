using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

// Решения и исполнение дипломатии ИИ портом на R250 движка: генератор в миг каждой оценки восстанавливается по журналу (JournalRng:
// сев карты и броски, записанные до неё), состояние — сохранение дня, докрученное строками журнала (relation, alliance_change, bank,
// spell_research, trade_accept, fort_capture). Так сверяются не только правила по полям строки, но и сами броски, их число, перемешивание кандидатов
// подарка и всё, что исполнение делает с картой.
public partial class AiDiplomacyHarnessTests
{
    private static readonly string[] ReplayKinds =
    [
        "ai_turn", "ai_eval", "ai_break_check", "ai_offer_check", "ai_gift_check", "ai_candidate", "ai_exec", "ai_chat", "ai_answer",
        "trade_accept", "alliance_change", "relation", "bank", "spell_research", "fort_capture", "rng_seed", "rng",
    ];

    /// <summary>Сколько бит должны нести опоры до вызова, чтобы сдвиг генератора подбирался однозначно (две опоры <c>rng(100)</c> — 13,3;
    /// сдвигов перебирается до 400).</summary>
    private const double MinAnchorBits = 16;

    /// <summary>Итог переигровки: оценок (проверка разрыва или предложения и подарок той же цели), сошлось; исполнений типа 8, сошлось;
    /// оценок и исполнений без генератора (сдвиг не подобрался); из сошедшихся — с генератором «до» или «после», подобранным по опорам
    /// после вызова (начало карты: до вызова опор меньше <see cref="MinAnchorBits"/>). У «до» первая опора — собственный бросок
    /// сверяемого, его значение совпадает по построению; доказаны входы, число бросков и решения по прочим.</summary>
    private sealed record ReplayCounts(int Evaluations, int EvaluationsSame, int Executions, int ExecutionsSame, int NoRng, int Ahead);

    /// <summary>
    /// Карта 15 на R250 движка. <b>Оценка</b> (<c>0x4486f5</c>): порт (<see cref="GameSession.DiplomacyDecision"/>) на генераторе журнала
    /// перед первой проверкой цели и состоянии в тот миг даёт те же входы (отношение, союз, крепости, шансы, порог подарка), то же
    /// решение (разрыв, предложение, ненависть, вид подарка и есть ли он), то же предложенное (предмет с крепостью или заклинание — после
    /// перемешивания <c>0x449550</c>/<c>0x4495d8</c>, — золото, цена) и генератор после оценки — тот же, что у движка перед следующей
    /// строкой. <b>Исполнение</b> (<c>ai_exec</c> типа 8, <c>0x4491f7</c>/<c>0x449278</c>/<c>0x449307</c> и действия союза):
    /// сообщения, ответ ИИ-цели (<c>0x474b37</c>, <c>0x474841</c>), ответ человека — по журналу (окно <c>DLG_OFFER</c>/
    /// <c>DLG_DIPLO_BOX</c> движок показывает посреди хода ИИ, ход стоит), затем слова пар отношений, казны сторон, известные
    /// заклинания и генератор после последнего броска исполнения — как у движка. Измерено 18.10: оценок 309, сошлись все (в том числе
    /// 42 выбора подарка — тот же предмет или заклинание после перемешивания); исполнений 14, сошлись все. Чувствительность: граница
    /// перемешивания <c>rng(i + 1)</c> вместо <c>rng(i)</c> роняет 8 из первых 9 подарков дня 1. Генератор по опорам после вызова — у 3
    /// (первая оценка дня 1 и её соседи).
    /// </summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Replay_Synth() => Assert.Equal(new ReplayCounts(309, 309, 14, 14, 0, 3), Replay(SynthRuns));

    /// <summary>Четыре партии карты 4 (<c>port-synth-4-ai</c>: лорд-маг Легионов, правитель Нежити; лёгкая, очень трудная, третья,
    /// четвёртая) — те же сверки (18.10): оценок 1392, сошлись все; исполнений 36, сошлось 32, у 4 генератора нет — ненависть дня 1, когда
    /// до неё и после неё бросают только номера сообщений (<c>rng(5)</c>): опор на 16 бит не набирается; по опорам после вызова — 92 (дни 1 без отношений выше D_NEUTRAL).</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Replay_Map4_Synth() => Assert.Equal(new ReplayCounts(1392, 1392, 36, 32, 4, 92), Replay(Map4Runs));

    private static readonly Run[] Map4Runs =
    [
        new("20261016-port-synth", "passive-002.jsonl", "port-synth-4-ai", 0, 0),
        new("20261016b-port-synth", "passive-001.jsonl", "port-synth-4-ai", 2300059, 4727542),
        new("20261016c-port-synth", "passive-001.jsonl", "port-synth-4-ai", 588357, 3394036),
        new("20261016d-port-synth", "passive-001.jsonl", "port-synth-4-ai", 0, 0),
    ];

    private ReplayCounts Replay(IEnumerable<Run> runs)
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(System.IO.Path.Combine(Data, "rules"));
        int evals = 0, evalsSame = 0, execs = 0, execsSame = 0, noRng = 0, aheadSame = 0;
        var bad = new List<string>();
        foreach (var run in runs)
        {
            Skip.IfNot(File.Exists(run.Path), $"нет журнала {run}");
            var events = Events(run, ReplayKinds);
            var saves = new Dictionary<int, string>();
            foreach (string f in DaySaves(run))
                if (!saves.TryAdd(DayOf(f), f)) throw new InvalidOperationException($"{run}: два сохранения дня {DayOf(f)} — партия не одна, нужен отрезок");
            var first = MapLooksLoader.Load(saves.OrderBy(p => p.Key).First().Value);
            var raceIds = first.Map.Players.ToDictionary(p => p.Key, p => p.Value.RaceId, StringComparer.OrdinalIgnoreCase);
            // Сев карты (rng_seed «new_game_time») лежит до первой строки её отрезка — генератор берёт весь журнал.
            var anchors = Anchors(events, book, raceIds).ToList();
            var seeded = JournalCache.Read(run.Path, ["rng_seed", "rng"]).ToList();
            var rng = new JournalRng(seeded, anchors);
            var draws = seeded.OfType<RngEvent>().Where(d => !d.Std).Concat(anchors).OrderBy(d => d.Seq).ThenBy(d => d.N).ToList();
            // Генератор перед вызовом counter. Опоры до него (после сева) должны нести не меньше MinAnchorBits: в начале карты отношения
            // ниже D_NEUTRAL и бросают только сообщения (rng(5)), а одна-две такие опоры подходят и при чужом сдвиге. Не набралось — по
            // опорам после, если первая из них и есть этот вызов (JournalRng.BeforeDraws).
            bool ahead = false;
            EngineRng? At(long seq, long counter)
            {
                if (rng.At(seq, counter, MinAnchorBits) is { } g) return g;
                if (draws.FirstOrDefault(d => d.Seq >= seq)?.N != counter) return null;
                ahead = true;
                return rng.BeforeDraws(seq - 1, long.MaxValue);
            }
            var replay = new DayReplay(book, content.MapArt);
            var offers = new Dictionary<(string, string), AiTradeOffer?>();
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e is AiTurnEvent { Phase: "begin", Cause: "ai" } turn)
                {
                    offers.Clear();   // выбранный подарок живёт до исполнения в том же ходе
                    if (replay.Day != turn.Turn && saves.TryGetValue((int)turn.Turn, out var sg)) replay.Load(MapLooksLoader.Load(sg), (int)turn.Turn);
                }
                if (replay.Looks is null) continue;
                replay.Apply(e);
                switch (e)
                {
                    case AiBreakCheckEvent or AiOfferCheckEvent:
                    {
                        evals++;
                        var (player, target) = PlayerAndTarget(e);
                        var gift = NextOf<AiGiftCheckEvent>(events, i, g => g.Player == player && g.Other == target);
                        var candidate = gift?.Action is null ? null : CandidateAfter(events, events.IndexOf(gift), player);
                        var last = (CorpusEvent?)gift ?? e;
                        // Оценка без бросков (отношение не выше D_NEUTRAL, союз без шанса разрыва) генератора не требует: порт не должен шагать.
                        bool silent = RngAfter(last) == RngBefore(e);
                        ahead = false;
                        var before = At(e.Seq, RngBefore(e) + 1) ?? (silent ? new EngineRng() : null);
                        var after = silent ? null : At(last.Seq + 1, RngAfter(last) + 1);
                        if (before is null || (!silent && after is null))
                        {
                            noRng++;
                            output.WriteLine($"{run} seq {e.Seq} {player}→{target}: генератор не подобран");
                            break;
                        }
                        var game = replay.Session(before);
                        var decision = game.DiplomacyDecision(player, target);
                        offers[(player, target)] = decision.Offer;
                        var check = game.DiplomacyChecks![^1];
                        bool sameRng = silent ? game.Rng.Steps == 0 : SameRng(game.Rng, after!);
                        string? why = EvaluationWhy(e, gift, candidate, check, decision) ?? (sameRng ? null : "генератор после оценки");
                        if (why is null) { evalsSame++; if (ahead) aheadSame++; }
                        else bad.Add($"{run} seq {e.Seq} день {replay.Day} {player}→{target}: {why} — порт {check}");
                        break;
                    }
                    case AiExecEvent { Action: { Type: 8 } act } x:
                    {
                        execs++;
                        string player = x.Player!, target = Field(act, "id4")!;
                        var window = ExecWindow(events, i);
                        long end = window.Select(RngAfterOrNull).Max() ?? x.RngAfter;
                        ahead = false;
                        if (At(x.Seq, x.RngBefore + 1) is not { } before || At(window.LastOrDefault()?.Seq + 1 ?? x.Seq + 1, end + 1) is not { } after)
                        {
                            noRng++;
                            break;
                        }
                        var game = replay.Session(before);
                        // У всего, кроме ненависти, итог в журнале есть: слово пары, сделка или дань — без него сверять нечего.
                        bool effects = act.Class == "dipl_hate" || window.Any(w => w is RelationEvent or AllianceChangeEvent or TradeAcceptEvent);
                        string? why = (effects ? null : "в окне исполнения нет строк итога")
                                      ?? Execute(game, player, target, act, offers.GetValueOrDefault((player, target)), window)
                                      ?? ExecutionWhy(game, replay, window)
                                      ?? (SameRng(game.Rng, after) ? null : "генератор после исполнения");
                        if (why is null) { execsSame++; if (ahead) aheadSame++; }
                        else bad.Add($"{run} seq {x.Seq} день {replay.Day} {player} {act.Class} {target}: {why}");
                        break;
                    }
                }
            }
        }
        foreach (string line in bad.Take(30)) output.WriteLine(line);
        foreach (var g in bad.GroupBy(l => System.Text.RegularExpressions.Regex.Match(l, @": ([^—0-9]+)").Groups[1].Value.Trim()))
            output.WriteLine($"  {g.Key}: {g.Count()}");
        var counts = new ReplayCounts(evals, evalsSame, execs, execsSame, noRng, aheadSame);
        output.WriteLine($"оценок {evals}: сошлось {evalsSame}; исполнений {execs}: сошлось {execsSame}; без генератора {noRng}; сошлось с генератором по опорам после {aheadSame}");
        return counts;
    }

    /// <summary>Что разошлось в оценке; <c>null</c> — ничего. Входы — поля строк зонда, решение — <c>ret</c> проверок и <c>action</c>
    /// подарка, предложенное — строка <c>ai_candidate</c> сразу за подарком.</summary>
    private static string? EvaluationWhy(CorpusEvent head, AiGiftCheckEvent? gift, AiCandidateEvent? candidate, AiDiploCheck port,
                                         AiDiploDecision decision)
    {
        switch (head)
        {
            case AiBreakCheckEvent b:
                if (!port.Ally) return "союза нет";
                if (port.OwnForts != b.FortsPlayer || port.OtherForts != b.FortsOther) return "крепости";
                if (port.BreakChance != b.Chance) return $"шанс разрыва {port.BreakChance}, журнал {b.Chance}";
                if ((decision.Action == AiDiploAction.Break) != b.Ret) return $"разрыв {decision.Action}";
                break;
            case AiOfferCheckEvent o:
                if (port.Ally) return "союз";
                if (port.Relation != o.Rel) return $"отношение {port.Relation}, журнал {o.Rel}";
                if (port.OfferChance != o.Chance) return $"шанс предложения {port.OfferChance}, журнал {o.Chance}";
                if ((decision.Action == AiDiploAction.Offer) != o.Ret) return $"предложение {decision.Action}";
                if (!o.Ret && (decision.Action == AiDiploAction.Hate) != (o.Turn == 1)) return $"ненависть {decision.Action}";
                break;
        }
        if (decision.Action is AiDiploAction.Break or AiDiploAction.Offer) return gift is null ? null : "подарок после разрыва или предложения";
        if (gift is null) return port.GiftThreshold is null ? null : "у порта проверка подарка";
        if (port.GiftThreshold != gift.Threshold) return $"порог подарка {port.GiftThreshold}, журнал {gift.Threshold}";
        if (gift.KindRoll is null != decision.Gift is null) return $"вид {decision.Gift}, журнал {gift.KindRoll}";
        if (gift.KindRoll is >= 0 && (int?)decision.Gift != gift.KindRoll) return $"вид {decision.Gift}, журнал {gift.KindRoll}";
        if ((decision.Offer is not null) != (gift.Action is not null)) return $"подарок {decision.Offer}, журнал {gift.Action}";
        if (candidate?.Action is not { } a || decision.Offer is not { } offer) return null;
        var (thing, fort, gold) = Offered(a);
        if (!thing.Equals(offer.Object, StringComparison.OrdinalIgnoreCase) || !fort.Equals(offer.Container, StringComparison.OrdinalIgnoreCase)
            || gold != offer.Gold)
            return $"предложено {offer.Object} {offer.Container} {offer.Gold}, движок {thing} {fort} {gold}";
        return null;
    }

    /// <summary>Предложенное действием подарка: объект, крепость предмета и золото (<c>raw</c>: цель, объект, [крепость], золото).</summary>
    private static (string Thing, string Fort, int Gold) Offered(AiObject a) => a.Class switch
    {
        "dipl_item" => (Field(a, "id12") ?? "", Field(a, "id8") ?? "", Word(a.Raw!, 3)),
        "dipl_spell" => (Field(a, "id8") ?? "", "", Word(a.Raw!, 2)),
        _ => ("", "", Word(a.Raw!, 1)),
    };

    /// <summary>Исполнение действия журнала портом; ответ человека — из журнала. Вернёт, что не так, или <c>null</c>.</summary>
    private static string? Execute(GameSession game, string player, string target, AiObject act, AiTradeOffer? offer,
                                   IReadOnlyList<CorpusEvent> window)
    {
        var report = new List<string>();
        bool human = game.State.Info.Humans.Contains(target, StringComparer.OrdinalIgnoreCase);
        switch (act.Class)
        {
            case "dipl_hate": return game.ExecuteDiplomacy(player, target, AiDiploAction.Hate, report) ? null : "ненависть не исполнилась";
            case "dipl_break": return game.ExecuteDiplomacy(player, target, AiDiploAction.Break, report) ? null : "разрыв не исполнился";
            case "dipl_offer":
                if (!game.ExecuteDiplomacy(player, target, AiDiploAction.Offer, report)) return "предложение не исполнилось";
                if (!human) return null;
                return window.OfType<AllianceChangeEvent>().FirstOrDefault() is { } answer
                    ? game.AnswerAllianceOffer(player, target, answer.Accept, report)
                    : "ответа человека в журнале нет";
            default:
                if (offer is null) return "у порта нет выбранного подарка";
                var (thing, fort, gold) = Offered(act);
                if (!thing.Equals(offer.Object, StringComparison.OrdinalIgnoreCase) || !fort.Equals(offer.Container, StringComparison.OrdinalIgnoreCase)
                    || gold != offer.Gold)
                    return $"исполнено {thing} {fort} {gold}, у порта {offer.Object} {offer.Container} {offer.Gold}";
                game.ExecuteGift(player, target, offer, report);
                if (!human || offer.Kind == AiGiftKind.Gold) return null;
                return window.OfType<TradeAcceptEvent>().FirstOrDefault() is { } trade
                    ? game.AnswerTradeOffer(player, target, trade.Accepted, report)
                    : "ответа человека в журнале нет";
        }
    }

    /// <summary>Итог исполнения против строк окна: слово каждой пары (последняя строка <c>relation</c>/<c>alliance_change</c>), казна
    /// (последняя строка <c>bank</c> игрока), заклинание сделки у покупателя.</summary>
    private static string? ExecutionWhy(GameSession game, DayReplay replay, IReadOnlyList<CorpusEvent> window)
    {
        foreach (var (pair, raw) in replay.PairsOf(window))
        {
            var mine = game.Map.Relations.FirstOrDefault(r => r.Pairs(pair.Item1, pair.Item2));
            if (mine.Raw != raw) return $"пара {pair.Item1}-{pair.Item2}: порт {mine.Raw:x}, журнал {raw:x}";
        }
        foreach (var bank in window.OfType<BankEvent>().GroupBy(b => b.Player, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            if (!game.Map.Players.TryGetValue(bank.Player, out var who)) return $"казна {bank.Player}: игрока нет на карте";
            else if (who.Bank != bank.After) return $"казна {bank.Player}: порт {game.Map.Players[bank.Player].Bank}, журнал {bank.After}";
        foreach (var t in window.OfType<TradeAcceptEvent>().Where(t => t.Object.StartsWith("G000SS", StringComparison.OrdinalIgnoreCase)))
            if (game.Map.Players[t.A].KnownSpells.Contains(t.Object, StringComparer.OrdinalIgnoreCase) != t.Accepted)
                return $"заклинание {t.Object} у {t.A}: сделка {t.Accepted}";
        return null;
    }

    /// <summary>Строки исполнения: от <c>ai_exec</c> до следующей оценки, исполнения или хода.</summary>
    private static List<CorpusEvent> ExecWindow(List<CorpusEvent> events, int at)
    {
        var list = new List<CorpusEvent>();
        for (int k = at + 1; k < events.Count; k++)
        {
            if (events[k] is AiExecEvent or AiEvalEvent or AiTurnEvent or AiBreakCheckEvent or AiOfferCheckEvent or AiGiftCheckEvent) break;
            list.Add(events[k]);
        }
        return list;
    }

    /// <summary>Строка вида <typeparamref name="T"/> за строкой <paramref name="at"/> до следующей проверки другой цели.</summary>
    private static T? NextOf<T>(List<CorpusEvent> events, int at, Func<T, bool> match) where T : CorpusEvent
    {
        for (int k = at + 1; k < events.Count; k++)
        {
            if (events[k] is T t && match(t)) return t;
            if (events[k] is AiBreakCheckEvent or AiOfferCheckEvent or AiGiftCheckEvent or AiExecEvent or AiEvalEvent) return null;
        }
        return null;
    }

    /// <summary>Действие подарка, добавленное оценкой (<c>ai_candidate</c> <c>dipl_item</c>/<c>dipl_spell</c>/<c>dipl_gold</c>).</summary>
    private static AiCandidateEvent? CandidateAfter(List<CorpusEvent> events, int at, string player)
    {
        for (int k = at + 1; k < events.Count; k++)
        {
            if (events[k] is AiCandidateEvent { Action.Class: "dipl_item" or "dipl_spell" or "dipl_gold" } c && c.Player == player) return c;
            if (events[k] is AiBreakCheckEvent or AiOfferCheckEvent or AiGiftCheckEvent or AiExecEvent) return null;
        }
        return null;
    }

    private static long RngBefore(CorpusEvent e) => e switch
    {
        AiBreakCheckEvent b => b.RngBefore, AiOfferCheckEvent o => o.RngBefore, AiGiftCheckEvent g => g.RngBefore,
        _ => throw new ArgumentException(e.ToString()),
    };

    private static long RngAfter(CorpusEvent e) => RngAfterOrNull(e) ?? throw new ArgumentException(e.ToString());

    private static long? RngAfterOrNull(CorpusEvent e) => e switch
    {
        AiBreakCheckEvent b => b.RngAfter, AiOfferCheckEvent o => o.RngAfter, AiGiftCheckEvent g => g.RngAfter,
        AiChatEvent c => c.RngAfter, AiAnswerEvent a => a.RngAfter, _ => null,
    };

    private static bool SameRng(EngineRng a, EngineRng b) => a.Index == b.Index && a.TableSnapshot().AsSpan().SequenceEqual(b.TableSnapshot());

    /// <summary>Броски журнала в полях строк — опоры генератора (<see cref="JournalRng"/>): проверки разрыва, предложения и подарка
    /// (<c>rng(100)</c>, вид — <c>rng(3)</c> следующим вызовом), ответы ИИ (<c>rng(100)</c>), сообщения (<c>rng(N)</c>, N — строк
    /// <c>TAiMsg</c> расы отправителя в категории). <c>−1</c> — бросок вытеснен из кольца зонда, не опора. Номер вызова — как у строк
    /// <c>rng</c> (их <c>n</c> — счётчик после вызова): у проверок <c>rng_before</c> — счётчик до броска, так что вызов броска —
    /// <c>rng_before + 1</c>. На карте 15 это видно по <c>rng_index</c> боя дня 9 (seq 3964302: <c>n</c> 1369610, индекс 56 — сдвиг
    /// 4 от сева против 5 у проверок по <c>rng_before</c>).</summary>
    private static IEnumerable<RngEvent> Anchors(IEnumerable<CorpusEvent> events, GameBook book, IReadOnlyDictionary<string, string> raceIds)
    {
        foreach (var e in events)
            switch (e)
            {
                case AiBreakCheckEvent { Roll: >= 0 } b: yield return new RngEvent(b.Seq, b.Tid, b.RngBefore + 1, 100, b.Roll.Value, false); break;
                case AiOfferCheckEvent { Roll: >= 0 } o: yield return new RngEvent(o.Seq, o.Tid, o.RngBefore + 1, 100, o.Roll.Value, false); break;
                case AiGiftCheckEvent g:
                    if (g.Roll is >= 0) yield return new RngEvent(g.Seq, g.Tid, g.RngBefore + 1, 100, g.Roll.Value, false);
                    if (g.KindRoll is >= 0) yield return new RngEvent(g.Seq, g.Tid, g.RngBefore + 2, 3, g.KindRoll.Value, false);
                    break;
                case AiAnswerEvent { Roll: >= 0 } a: yield return new RngEvent(a.Seq, a.Tid, a.RngBefore + 1, 100, a.Roll.Value, false); break;
                case AiChatEvent { Roll: >= 0, From: { } from } c when raceIds.TryGetValue(from, out var race)
                                                                      && book.AiMessages(race, (AiMessage)c.Category).Count is > 0 and var n:
                    yield return new RngEvent(c.Seq, c.Tid, c.RngBefore + 1, n, c.Roll.Value, false);
                    break;
            }
    }

    /// <summary>Состояние дня для переигровки: сохранение дня, докрученное строками журнала.</summary>
    private sealed class DayReplay(GameBook book, MapArtData art)
    {
        public MapLooks? Looks { get; private set; }

        public int Day { get; private set; }

        private GameInfo _info = new("", "", [], 1, 1);

        public void Load(MapLooks looks, int day)
        {
            Looks = looks;
            Day = day;
            var map = looks.Map;
            _info = new GameInfo("", "", [.. map.Players.Values.Where(p => p.IsHuman).Select(p => p.Id)], map.Limits?.DifficultyGame ?? 1,
                                 map.Limits?.SuggestedLevel ?? 1);
        }

        /// <summary>Партия на текущем состоянии с генератором <paramref name="rng"/>.</summary>
        public GameSession Session(EngineRng rng) =>
            new(new GameState(_info, Looks!) { Random = new RandomStreams(rng.Index, rng.TableSnapshot(), 1) }, art, book) { DiplomacyChecks = [] };

        private int RaceOf(string player) => book.Economy.Races[Looks!.Map.Players[player].RaceId].RaceType;

        /// <summary>Последнее слово каждой пары рас в строках <paramref name="rows"/>.</summary>
        public IEnumerable<((int, int) Pair, uint Raw)> PairsOf(IEnumerable<CorpusEvent> rows)
        {
            var words = new Dictionary<(int, int), uint>();
            foreach (var e in rows)
                switch (e)
                {
                    case RelationEvent r:
                        // Мутатор двигает и третьи стороны: changed — все пары, чьё слово за вызов изменилось.
                        if (r.After is { } word) words[Key((int)r.RaceA, (int)r.RaceB)] = (uint)word.Raw;
                        foreach (var c in r.Changed) words[Key((int)c.Race1, (int)c.Race2)] = (uint)c.After;
                        break;
                    case AllianceChangeEvent { After: { } after } a: words[Key(RaceOf(a.A), RaceOf(a.B))] = (uint)after.Raw; break;
                }
            return words.Select(p => (p.Key, p.Value));
        }

        private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        /// <summary>Строка журнала двигает состояние: отношения, казна, известные заклинания, предмет сделки, хозяин взятой крепости
        /// (карта 22, день 2: Легионы берут деревню до хода Кланов — крепостей у цели проверки разрыва 2, в сохранении дня 1).</summary>
        public void Apply(CorpusEvent e)
        {
            var map = Looks!.Map;
            switch (e)
            {
                case RelationEvent or AllianceChangeEvent:
                {
                    var relations = map.Relations.ToList();
                    foreach (var (pair, raw) in PairsOf([e]))
                    {
                        int at = relations.FindIndex(r => r.Pairs(pair.Item1, pair.Item2));
                        if (at >= 0) relations[at] = relations[at] with { Raw = raw };
                        else relations.Add(new Relation(pair.Item1, pair.Item2, raw));
                    }
                    Set(GameSession.With(map, relations: relations));
                    break;
                }
                case BankEvent b when map.Players.ContainsKey(b.Player):
                    Set(GameSession.With(map, players: With(map, b.Player, p => p with { Bank = b.After })));
                    break;
                case SpellResearchEvent s when map.Players.ContainsKey(s.Player):
                    Set(GameSession.With(map, players: With(map, s.Player, p => p with { KnownSpells = [.. p.KnownSpells, s.Spell.ToUpperInvariant()] })));
                    break;
                case TradeAcceptEvent { Accepted: true } t when t.Object.StartsWith("G000SS", StringComparison.OrdinalIgnoreCase):
                    Set(GameSession.With(map, players: With(map, t.A, p => p with { KnownSpells = [.. p.KnownSpells, t.Object.ToUpperInvariant()] })));
                    break;
                case FortCaptureEvent { Captured: true } f:
                    Set(GameSession.With(map, sites: [.. map.Sites.Select(s => s.Id.Equals(f.Fort, StringComparison.OrdinalIgnoreCase) ? s with { Owner = f.OwnerAfter } : s)]));
                    break;
                case TradeAcceptEvent { Accepted: true } t:
                {
                    string? capital = map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Capital && s.Owner.Equals(t.A, StringComparison.OrdinalIgnoreCase))?.Id;
                    var sites = map.Sites.Select(s =>
                        s.Id.Equals(t.Id2, StringComparison.OrdinalIgnoreCase)
                            ? s with { Items = [.. s.Items.Where(i => !i.Equals(t.Object, StringComparison.OrdinalIgnoreCase))] }
                            : s.Id.Equals(capital, StringComparison.OrdinalIgnoreCase) ? s with { Items = [.. s.Items, t.Object] } : s).ToList();
                    Set(GameSession.With(map, sites: sites));
                    break;
                }
            }
        }

        private static Dictionary<string, MapPlayer> With(MapState map, string player, Func<MapPlayer, MapPlayer> change)
        {
            var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            players[player] = change(players[player]);
            return players;
        }

        private void Set(MapState map) => Looks = GameSession.Retold(Looks!, map, Looks!.Stacks);
    }
}
