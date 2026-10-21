using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Дипломатия ИИ (<c>docs/ai-turn-plan.md</c>, срез 4; <c>docs/ai-diplomacy.md</c>) против журналов зонда: модель каждой
/// проверки (<c>ai_break_check</c>, <c>ai_offer_check</c>, <c>ai_gift_check</c>) и сообщений (<c>ai_chat</c>), входы и шансы первого
/// прохода, которые порт собирает из сохранения дня, ненависть дня 1, выбор подарка, исполняемое решение и (часть <c>Replay</c>) решения
/// и исполнение порта на R250 движка. Партии корпуса — <see cref="CorpusRuns"/>, синтетическая карта 15 (<c>port-synth-15-diplo</c>) —
/// <see cref="SynthRuns"/> (<c>Category=Synth</c>). Журналы — <see cref="JournalCache"/>.</summary>
[Trait("Category", "Corpus")]
public partial class AiDiplomacyHarnessTests(ITestOutputHelper output)
{
    /// <summary>Партия: каталог корпуса, журнал, начало имени сохранений карты и отрезок seq (<c>To</c> 0 — до конца). Журнал с
    /// несколькими картами одной S143 — карта отрезком: id игроков у карт общие.</summary>
    private sealed record Run(string Dir, string File, string Stem, long From, long To)
    {
        public string Path => System.IO.Path.Combine(GameSaveTests.CorpusDir(), Dir, File);

        public string Folder => System.IO.Path.Combine(GameSaveTests.CorpusDir(), Dir);

        public bool In(long seq) => seq >= From && (To == 0 || seq <= To);

        public override string ToString() => Stem.Length > 0 ? $"{Dir} {Stem}" : Dir;
    }

    private static readonly Run[] CorpusRuns =
    [
        new("20260918-probe65", "passive-001.jsonl", "", 0, 0), new("20260919-human", "passive-001.jsonl", "", 0, 0),
        new("20260920-legions", "passive-001.jsonl", "", 0, 0),
        // Партия с покрытием (зонд 7.5, Greenskins, человек, дни 1–18).
        new("20261019c-cov", "passive-001.jsonl", "greenskins-human", 0, 0),
    ];

    /// <summary>Карта 15 (<c>port-synth-15-diplo</c>, Эльфы, 10 дней) партии 18.10: в журнале до неё карты 13 и 14.</summary>
    private static readonly Run[] SynthRuns = [new("20261018-port-synth", "passive-001.jsonl", "port-synth-15-diplo", 3122310, 0)];

    private static string Data => System.IO.Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// Каждая проверка журнала против правил: разрыв — <see cref="AiDiplomacy.BreakChance"/> из полей строки, бросок есть
    /// ровно при шансе, <c>ret == бросок &lt; шанс</c>; предложение — шанс считается ровно при <c>rel &gt; D_NEUTRAL</c>, в
    /// день 1 при <c>rel ≥ 90</c> — 100, иначе не меньше 5, бросок ⇔ шанс, сдвиг ГСЧ = бросок; подарок — порог
    /// <c>rel·20/100</c>, вид ⇔ бросок &lt; порога, без действия сдвиг 1 или 2. Сообщение — бросок меньше числа строк
    /// <c>TAiMsg</c> расы отправителя в категории, сдвиг 1. Измерено 23.09: 0 расхождений.
    /// </summary>
    [SkippableFact]
    public void Checks_MatchRules()
    {
        var (breaks, offers, gifts, chats) = CheckRows(CorpusRuns);
        Assert.True(breaks > 400 && offers > 3000 && gifts > 400 && chats > 70);
    }

    /// <summary>То же на карте 15 (18.10): разрыв 135, предложение 174, подарок 187, сообщений 19 — 0 расхождений.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Checks_MatchRules_Synth() => Assert.Equal((135, 174, 187, 19), CheckRows(SynthRuns));

    private (int Breaks, int Offers, int Gifts, int Chats) CheckRows(IEnumerable<Run> runs)
    {
        var book = GameBook.Load(System.IO.Path.Combine(Data, "rules"));
        var rules = book.Diplomacy;
        int breaks = 0, offers = 0, gifts = 0, chats = 0;
        var bad = new List<string>();
        foreach (var run in runs)
        {
            Skip.IfNot(File.Exists(run.Path), $"нет журнала {run}");
            var races = PlayerRaces(run);
            foreach (var e in Events(run, "ai_break_check", "ai_offer_check", "ai_gift_check", "ai_chat"))
                switch (e)
                {
                    case AiBreakCheckEvent b:
                    {
                        breaks++;
                        if (b.AllyTurn is not { } allyTurn) { bad.Add($"{b.Seq}: пары нет"); break; }
                        var pair = new Relation(0, 1, 1u | ((uint)(allyTurn & 0x7FFF) << 15) | (b.NoBreak == true ? 1u << 31 : 0));
                        int? chance = AiDiplomacy.BreakChance(pair, (int)b.Turn, (int)b.FortsPlayer, (int)b.FortsOther,
                                                              book.AllianceBreak.Turn, book.AllianceBreak.Chance);
                        if (chance != b.Chance) bad.Add($"{b.Seq}: разрыв — шанс {chance}, в журнале {b.Chance}");
                        Rolled(b.Seq, chance, b.Roll, b.Ret, bad);
                        break;
                    }
                    case AiOfferCheckEvent o:
                    {
                        offers++;
                        int rel = (int)o.Rel;
                        bool shouldCount = rel > rules.Neutral;
                        if (shouldCount != o.Chance.HasValue) bad.Add($"{o.Seq}: предложение — шанс при rel {rel}: {o.Chance}");
                        else if (o.Chance is { } c && (rel >= 90 && o.Turn == 1 ? c != 100 : c < 5 || c > Math.Max(5, rel - 10)))
                            bad.Add($"{o.Seq}: предложение — шанс {c} при rel {rel}, день {o.Turn}");
                        Rolled(o.Seq, (int?)o.Chance, o.Roll, o.Ret, bad);
                        if (o.RngAfter - o.RngBefore != (o.Chance.HasValue ? 1 : 0)) bad.Add($"{o.Seq}: предложение — сдвиг {o.RngAfter - o.RngBefore}");
                        break;
                    }
                    case AiGiftCheckEvent g:
                    {
                        gifts++;
                        long drng = g.RngAfter - g.RngBefore;
                        if (g.Threshold != g.Rel * 20 / 100) bad.Add($"{g.Seq}: подарок — порог {g.Threshold} при rel {g.Rel}");
                        if (g.Roll is >= 0 && g.KindRoll.HasValue != g.Roll < g.Threshold) bad.Add($"{g.Seq}: подарок — вид {g.KindRoll} при броске {g.Roll}");
                        if (g.Action is null && drng != (g.KindRoll.HasValue ? 2 : 1)) bad.Add($"{g.Seq}: подарок без действия — сдвиг {drng}");
                        break;
                    }
                    case AiChatEvent m:
                    {
                        chats++;
                        string from = m.From ?? "";
                        int count = races.TryGetValue(from, out var race) ? book.AiMessages(race, (AiMessage)m.Category).Count : -1;
                        if (m.Roll is not { } r || r < 0 || r >= count || m.RngAfter - m.RngBefore != 1)
                            bad.Add($"{m.Seq}: сообщение {from} {(AiMessage)m.Category} — бросок {m.Roll} из {count}, сдвиг {m.RngAfter - m.RngBefore}");
                        break;
                    }
                }
        }
        foreach (string line in bad.Take(20)) output.WriteLine(line);
        output.WriteLine($"разрыв {breaks}, предложение {offers}, подарок {gifts}, сообщений {chats}; расхождений {bad.Count}");
        Assert.Empty(bad);
        return (breaks, offers, gifts, chats);
    }

    /// <summary>
    /// Входы решения из карты порта: сохранение дня снято перед концом хода человека, порт проводит начало хода и стратегию
    /// каждого игрока ИИ (<see cref="GameSession.BeginAiTurn"/>), а первый проход его дипломатии сверяется с журналом того же
    /// дня: какие цели, союз ли, отношение, шанс предложения, шанс разрыва с крепостями сторон, порог подарка (где ни порт,
    /// ни движок не исполнили разрыв или предложение). Измерено 23.09: из 1566 пар сошлось 1532. Остаток — то, что в тот же
    /// день до хода игрока сделали другие, а порт без тактики не повторяет: бои меняют отношение (28; по третьей стороне
    /// тоже), берут крепости (3), броски разрыва (2), доля равнины цели после роста земли (1). Полный день
    /// (<see cref="GameSession.EndTurn"/>) даёт 1535, но в восемь раз дольше.
    /// 25.09 (сессия «день целиком»): рост земли отвергает клетки ландмарков с GLmark.MOUNTAIN = T (HumanDayHarnessTests: земля
    /// человека 7 → 69 из 69 дней): 1532 → 1533; без ландмарков — ровно прежние. 28.09: здание юнитов стратегия ИИ строит только при
    /// казне и раз в день (AiEconomy.Capital, 0x446e37 → 0x5dc9e8): legions, день 49 — PL0009 после навыка и двух лидеров (казна 7)
    /// больше не строит G000BB0010 и G000BB0011 даром, шаги дня теперь как у движка, но на пустом проходе порта выпадает разрыв союза
    /// с PL000a (бросок R250 харнесса, не движка), и у PL000a союз расходится: 1533 → 1532 («союз» 2 → 3).
    /// </summary>
    [SkippableFact]
    public void FirstPass_FromSaves() => Assert.Equal((1719, 1677), FirstPass(CorpusRuns));

    /// <summary>Карта 15 (18.10): 90 пар (3 ИИ × 3 цели × 10 дней), сошлось 88. Остаток — то же «до хода игрока в тот же день»: день 1
    /// Нежить → Кланы — в журнале Нежить уже отвергла союз Кланов (бросок 96 при шансе 85, отношение 95 → 50, seq 3633550), а порт на
    /// своём R250 харнесса союз принял; день 9 Нежить → Кланы — отношение 38: Кланы до хода Нежити пустили на неё вора и напали (noble −5,
    /// attack −7, seq 3964290/3964296), тактику харнесс не повторяет. Решения на R250 движка с этими входами — <see cref="Replay_Synth"/>.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void FirstPass_FromSaves_Synth() => Assert.Equal((90, 88), FirstPass(SynthRuns));

    private (int Pairs, int Same) FirstPass(IEnumerable<Run> runs)
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(System.IO.Path.Combine(Data, "rules"));
        int pairs = 0, same = 0, missing = 0, extra = 0;
        var differ = new Dictionary<string, int>();
        foreach (var run in runs)
        {
            Skip.IfNot(File.Exists(run.Path), $"нет журнала {run}");
            var journal = FirstPasses(run);
            foreach (string sg in DaySaves(run))
            {
                var looks = MapLooksLoader.Load(sg);
                var map = looks.Map;
                var human = map.Players.Values.First(p => p.IsHuman);
                var info = new GameInfo("", "", [human.Id], map.Limits?.DifficultyGame ?? 1, map.Limits?.SuggestedLevel ?? 1);
                var session = new GameSession(new GameState(info, looks), content.MapArt, book) { DiplomacyChecks = [] };
                foreach (string player in GameSession.TurnOrder(map, new HashSet<string>([human.Id], StringComparer.OrdinalIgnoreCase), book))
                    session.BeginAiTurn(player);
                var port = session.DiplomacyChecks!.GroupBy(c => (c.Player, c.Target)).ToDictionary(g => g.Key, g => g.First());
                foreach (string player in port.Keys.Select(k => k.Player).Distinct())
                {
                    if (!journal.TryGetValue((player, map.Turn), out var logged)) continue;
                    foreach (var (target, check) in logged)
                    {
                        pairs++;
                        if (!port.TryGetValue((player, target), out var mine)) { missing++; continue; }
                        string? why = Compare(check, mine);
                        if (why is null) { same++; continue; }
                        differ[why] = differ.GetValueOrDefault(why) + 1;
                        if (differ[why] <= 3) output.WriteLine($"{System.IO.Path.GetFileName(sg)} {player}→{target}: {why} — журнал {check}, порт {mine}");
                    }
                    foreach (var k in port.Keys.Where(k => k.Player == player && !logged.ContainsKey(k.Target)))
                    {
                        extra++;
                        output.WriteLine($"{System.IO.Path.GetFileName(sg)} {player}→{k.Target}: лишняя цель у порта; журнал [{string.Join(",", logged.Keys)}]");
                    }
                }
            }
        }
        output.WriteLine($"пар {pairs}: сошлось {same}, нет у порта {missing}, лишних у порта {extra}; " +
                         string.Join(", ", differ.Select(p => $"{p.Key} {p.Value}")));
        Assert.Equal(0, missing);
        Assert.Equal(0, extra);
        return (pairs, same);
    }

    /// <summary>Ненависть дня 1 (<c>CAiDiploHatePlayer</c>): бросков в решении нет, поэтому исполненное порта за день 1
    /// должно совпасть с журналом — цель за целью (строки <c>ai_exec</c> типа 8 с действием <c>dipl_hate</c>).</summary>
    [SkippableFact]
    public void DayOne_Hate() => Assert.True(Hates(CorpusRuns) >= 6);

    /// <summary>Карта 15, день 1. Ненависть дня 1 зависит от бросков предложений: союз дня 1 у отношения 95 предлагается наверняка
    /// (шанс 100), а ответ цели-ИИ — бросок <c>max(5, rel − 10 − s)</c> = 85; отказ (Нежить → Кланы, бросок 96) опускает отношение до 50,
    /// и в тот же день у отказавшего — ненависть. Порт ходит своим R250, поэтому сверяется только первый ИИ дня (Легионы): его
    /// предложения — до всех ответов.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void DayOne_Hate_Synth() => Assert.Equal(1, Hates(SynthRuns, firstOnly: true));

    private int Hates(IEnumerable<Run> runs, bool firstOnly = false)
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(System.IO.Path.Combine(Data, "rules"));
        int players = 0;
        foreach (var run in runs)
        {
            Skip.IfNot(File.Exists(run.Path), $"нет журнала {run}");
            var hates = Events(run, "ai_exec").OfType<AiExecEvent>().Where(e => e.Action?.Class == "dipl_hate")
                .GroupBy(e => e.Player!)
                .ToDictionary(g => g.Key, g => g.Select(e => Field(e.Action, "id4")!).ToList());
            // Игроки, бросавшие в день 1 шанс предложения (ai_offer_check с шансом): дальше по дню ненависть зависит от бросков
            // (отказ цели опускает отношение — 20261019c-cov: PL0003 → PL0004 предложение, отказ 99 ≥ 58, отношение 80 → 50, у PL0004
            // ненависть к PL0003), а порт ходит своим R250 — после такого игрока сверка дня кончается.
            var rolled = Events(run, "ai_offer_check").OfType<AiOfferCheckEvent>().Where(e => e is { Turn: 1, Chance: not null })
                .Select(e => e.TurnPlayer!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string sg in DaySaves(run).Where(f => System.IO.Path.GetFileName(f).Contains("-t01-", StringComparison.Ordinal)))
            {
                var looks = MapLooksLoader.Load(sg);
                var human = looks.Map.Players.Values.First(p => p.IsHuman);
                var info = new GameInfo("", "", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
                var game = new GameSession(new GameState(info, looks), content.MapArt, book);
                foreach (string player in GameSession.TurnOrder(looks.Map, new HashSet<string>([human.Id], StringComparer.OrdinalIgnoreCase), book))
                {
                    var report = new List<string>();
                    game.BeginAiTurn(player, report);
                    var mine = report.Where(r => r.StartsWith($"{player} Diplomacy Hate ", StringComparison.Ordinal))
                                     .Select(r => r[(r.LastIndexOf(' ') + 1)..]).ToList();
                    var want = hates.GetValueOrDefault(player, []);
                    if (mine.Count == 0 && want.Count == 0)
                    {
                        if (rolled.Contains(player)) break;
                        continue;
                    }
                    players++;
                    output.WriteLine($"{System.IO.Path.GetFileName(sg)} {player}: журнал [{string.Join(",", want)}], порт [{string.Join(",", mine)}]");
                    Assert.Equal(want, mine);
                    if (firstOnly || rolled.Contains(player)) break;
                }
            }
        }
        return players;
    }

    /// <summary>
    /// Выбор «подарка» (<see cref="AiGift"/>) против журнала: у каждой проверки <c>ai_gift_check</c> с выпавшим видом порт из
    /// сохранения того же дня собирает кандидатов — сдвиг ГСЧ сверх двух бросков вида равен шагам перемешивания
    /// (<see cref="AiGift.Draws"/>), действие есть ровно тогда, когда есть подходящий кандидат, а предложенное движком
    /// (<c>ai_candidate</c> <c>dipl_item</c>/<c>dipl_spell</c>) — среди кандидатов порта по той же цене. Сохранение снято до
    /// хода ИИ: покупки и сделки того же дня до проверки порт не видит. 26.09: 95 проверок с видом, сошлось 94. Остаток — legions
    /// день 50 seq 10948761: ИИ изучил G000SS0012 в том же ходу до проверки (spell_research seq 10948735), у движка 6 кандидатов,
    /// в сохранении дня — 5.
    /// </summary>
    [SkippableFact]
    public void Gift_FromSaves() => Assert.Equal((107, 106), Gifts(CorpusRuns));

    /// <summary>Карта 15: 42 проверки с видом (у каждого ИИ есть всё для любого вида: зелья в складе столицы, заклинания, которых
    /// человек не знает, золото), сошлись все 42 — число кандидатов (шаги перемешивания), есть ли действие, предложенное среди
    /// кандидатов по той же цене. Какой именно кандидат выпал перемешиванием — <see cref="Replay_Synth"/>.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Gift_FromSaves_Synth() => Assert.Equal((42, 42), Gifts(SynthRuns));

    private (int Checks, int Same) Gifts(IEnumerable<Run> runs)
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(System.IO.Path.Combine(Data, "rules"));
        int checks = 0, same = 0;
        var differ = new Dictionary<string, int>();
        foreach (var run in runs)
        {
            Skip.IfNot(File.Exists(run.Path), $"нет журнала {run}");
            // Сохранение дня по (игрок, день): в каталоге бывает несколько карт, дни у них пересекаются.
            var saves = new Dictionary<(string, int), string>();
            foreach (string f in DaySaves(run))
                foreach (string id in MapLooksLoader.Load(f).Map.Players.Keys) saves.TryAdd((id, DayOf(f)), f);
            var sessions = new Dictionary<string, GameSession>();
            var day = new Dictionary<string, int>();
            var events = Events(run, "ai_turn", "ai_gift_check", "ai_candidate");
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is AiTurnEvent { Phase: "begin", Cause: "ai", Player: { } who } turn) { day[who] = (int)turn.Turn; continue; }
                if (events[i] is not AiGiftCheckEvent { KindRoll: { } kind } e) continue;
                var next = i + 1 < events.Count ? events[i + 1] as AiCandidateEvent : null;
                // kind_roll −1 — хук не поймал бросок вида (8 строк); вид тогда — по классу предложенного действия.
                if (kind < 0)
                {
                    kind = next?.Action?.Class switch { "dipl_item" => 0, "dipl_spell" => 1, _ => -1 };
                    if (kind < 0) continue;
                }
                string player = e.Player!, target = e.Other!;
                if (!day.TryGetValue(player, out int d) || !saves.TryGetValue((player, d), out var sg)) continue;
                if (!sessions.TryGetValue(sg, out var game))
                {
                    var looks = MapLooksLoader.Load(sg);
                    var human = looks.Map.Players.Values.First(p => p.IsHuman);
                    sessions[sg] = game = new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks), content.MapArt, book);
                }
                checks++;
                int rel = (int)e.Rel;
                int draws = (int)(e.RngAfter - e.RngBefore) - 2;
                bool acted = e.Action is not null;
                // Предложенное движком: строка ai_candidate сразу за проверкой (raw: цель, объект, [крепость/предмет], золото).
                string? offered = null, fort = null;
                int gold = 0;
                if (acted && next?.Action is { } a)
                {
                    if (kind == 0) { fort = Field(a, "id8"); offered = Field(a, "id12"); gold = Word(a.Raw!, 3); }
                    else if (kind == 1) { offered = Field(a, "id8"); gold = Word(a.Raw!, 2); }
                }
                string? why = kind switch
                {
                    0 => GiftWhy(game.GiftItems(player).Select(c => (c.Item, c.Value, Ok: c.Value < AiGift.ItemValueLimit)).ToList(),
                                 draws, acted, offered, gold - rel),
                    1 => GiftWhy(game.GiftSpells(player, target).Select(c => (Item: c.Spell, Value: c.BuyGold, Ok: c.Level < AiGift.SpellLevelLimit)).ToList(),
                                 draws, acted, offered, gold - rel),
                    _ => draws != 0 ? "сдвиг у золота" : acted != (AiGift.Gold(
                            Diplomacy.AreAllies(game.Map.Relations, RaceType(game, book, player), RaceType(game, book, target)), rel,
                            (int)game.Map.Players[player].Bank.Gold) is not null) ? "золото" : null,
                };
                if (why is null) { same++; continue; }
                differ[why] = differ.GetValueOrDefault(why) + 1;
                output.WriteLine($"{run} день {d} seq {e.Seq} {player}→{target} вид {kind}: {why} (сдвиг {draws}, действие {acted}, {fort} {offered} {gold})");
            }
        }
        output.WriteLine($"проверок с видом {checks}: сошлось {same}; " + string.Join(", ", differ.Select(p => $"{p.Key} {p.Value}")));
        return (checks, same);
    }

    private static string? GiftWhy(List<(string Item, int Value, bool Ok)> candidates, int draws, bool acted, string? offered, int value)
    {
        if (draws != AiGift.Draws(candidates.Count)) return $"кандидатов {candidates.Count}";
        if (acted != candidates.Any(c => c.Ok)) return "действие";
        if (offered is not null && !candidates.Any(c => c.Item.Equals(offered, StringComparison.OrdinalIgnoreCase) && c.Value == value))
            return "предложенного нет";
        return null;
    }

    private static int RaceType(GameSession game, GameBook book, string player) =>
        book.Economy.Races[game.Map.Players[player].RaceId].RaceType;

    /// <summary>Сверка одной пары первого прохода; <c>null</c> — сошлось, иначе что разошлось.</summary>
    private static string? Compare(JournalCheck j, AiDiploCheck p)
    {
        if (j.Ally != p.Ally) return "союз";
        if (j.Ally)
        {
            if (j.OwnForts != p.OwnForts || j.OtherForts != p.OtherForts) return "крепости";
            if (j.BreakChance != p.BreakChance) return "шанс разрыва";
        }
        else
        {
            if (j.Relation != p.Relation) return "отношение";
            if (j.OfferChance != p.OfferChance) return "шанс предложения";
        }
        // Подарок сравним, только если ни движок, ни порт не исполнили разрыв или предложение (иначе проверки нет).
        bool bothReachGift = !j.Succeeded && p.Decision.Action is not (AiDiploAction.Break or AiDiploAction.Offer);
        if (bothReachGift && j.GiftThreshold != p.GiftThreshold) return "подарок";
        return null;
    }

    /// <summary>Проверка первого прохода в журнале: союз (строка разрыва) или нет (строка предложения).</summary>
    private sealed record JournalCheck(bool Ally, int Relation, int OwnForts, int OtherForts, int? OfferChance, int? BreakChance,
                                       bool Succeeded, int? GiftThreshold);

    /// <summary>(игрок, день) → цель → проверка первого прохода. Проход — по последней строке <c>ai_eval</c> дипломатии игрока.</summary>
    private static Dictionary<(string Player, int Day), Dictionary<string, JournalCheck>> FirstPasses(Run run)
    {
        var result = new Dictionary<(string, int), Dictionary<string, JournalCheck>>();
        var day = new Dictionary<string, int>();
        var pass = new Dictionary<string, long>();
        foreach (var e in Events(run, "ai_turn", "ai_eval", "ai_offer_check", "ai_break_check", "ai_gift_check"))
        {
            switch (e)
            {
                case AiTurnEvent t:
                    if (t is { Phase: "begin", Cause: "ai", Player: { } p })
                    {
                        day[p] = (int)t.Turn;
                        pass[p] = 0;
                    }
                    continue;
                case AiEvalEvent v:
                    if (v.List == "diplomacy") pass[v.Player!] = v.Pass;
                    continue;
            }
            var (player, target) = PlayerAndTarget(e);
            if (!day.TryGetValue(player, out int d) || pass.GetValueOrDefault(player) != 1) continue;
            if (!result.TryGetValue((player, d), out var checks)) result[(player, d)] = checks = [];
            checks[target] = e switch
            {
                AiBreakCheckEvent b => new JournalCheck(true, -1, (int)b.FortsPlayer, (int)b.FortsOther, null, (int?)b.Chance, b.Ret, null),
                AiOfferCheckEvent o => new JournalCheck(false, (int)o.Rel, 0, 0, (int?)o.Chance, null, o.Ret, null),
                AiGiftCheckEvent g => checks.TryGetValue(target, out var c) ? c with { GiftThreshold = (int)g.Threshold } : null!,
                _ => throw new InvalidOperationException(e.ToString()),
            };
        }
        return result;
    }

    /// <summary>Чья проверка и о ком: у <c>ai_offer_check</c> игрок — <c>turn_player</c> (аргумента «кто» у функции нет).</summary>
    private static (string Player, string Target) PlayerAndTarget(CorpusEvent e) => e switch
    {
        AiBreakCheckEvent b => (b.Player, b.Other),
        AiOfferCheckEvent o => (o.TurnPlayer ?? "", o.Other ?? ""),
        AiGiftCheckEvent g => (g.Player ?? "", g.Other ?? ""),
        _ => ("", ""),
    };

    private static void Rolled(long seq, int? chance, long? roll, bool ret, List<string> bad)
    {
        if (chance.HasValue != roll.HasValue) bad.Add($"{seq}: бросок {roll} при шансе {chance}");
        else if (roll is >= 0 && ret != roll < chance) bad.Add($"{seq}: ответ {ret} при броске {roll} и шансе {chance}");
    }

    /// <summary>Журналы корпуса, где ИИ исполнял дипломатию (<c>ai_exec</c> типа 8): 168 исполнений (с <c>20261019c-cov</c> — 10).</summary>
    private static readonly Run[] ExecRuns =
    [
        .. CorpusRuns, new("20260919-f7b", "passive-001.jsonl", "", 0, 0), new("20260920-gaps", "passive-001.jsonl", "", 0, 0),
        new("20260920-ui", "passive-001.jsonl", "", 0, 0), new("20260921-ui", "passive-004.jsonl", "", 0, 0),
        new("20260922-ui", "passive-001.jsonl", "", 0, 0), new("20260928-shots", "passive-002.jsonl", "", 0, 0),
        new("20260928-shots", "passive-003.jsonl", "", 0, 0), new("20260930-probe72", "passive-001.jsonl", "", 0, 0),
        new("20261014-replay", "passive-001.jsonl", "", 0, 0), new("20261014-replay", "passive-002.jsonl", "", 0, 0),
    ];

    /// <summary>
    /// Какое решение дипломатии исполняется (<c>CAiStrategic::step 0x44e7e4</c>): движок кладёт действия в список
    /// (<c>0x44dd87</c> → <c>0x44df02</c> — вставка в конец) со счётом-константой 1000 (<c>0x6cf580</c>, у всех пяти действий
    /// <c>0x4486f5</c>) и исполняет первое (<c>begin</c> <c>0x43f962</c> → <c>+0x7c</c>, <c>0x44ea89</c>) — счёт выбор не решает.
    /// По каждому проходу журнала решения целей восстанавливаются из проверок (разрыв и предложение — <c>ret</c>, ненависть —
    /// день 1 без предложения, подарок — <c>action</c>), цели — из <c>ai_eval</c> списка 8; выбор порта
    /// (<see cref="AiStrategic.DiplomacyEvaluations"/>, первое действие) сверяется с исполненным (<c>ai_exec</c> типа 8).
    /// </summary>
    [SkippableFact]
    public void Execution_FirstAddedAction() => Assert.Equal(168, Executions(ExecRuns));

    /// <summary>Карта 15: 14 исполнений (ненависть 3, предложение 5, заклинание 4, предмет 1, золото 1), выбор порта сошёлся у всех 14,
    /// цели в порядке id; проходов с несколькими действиями 4, из них ненависть и подарок одной цели 1.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Execution_FirstAddedAction_Synth() => Assert.Equal(14, Executions(SynthRuns));

    private int Executions(IEnumerable<Run> runs)
    {
        int execs = 0, same = 0, hateGift = 0, orderSame = 0, orderPasses = 0, several = 0;
        var bad = new List<string>();
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            Skip.IfNot(File.Exists(run.Path), $"нет журнала {run}");
            // Текущий проход игрока: цели оценок списка 8 по порядку и решения по цели (действия — в порядке добавления).
            var pass = new Dictionary<string, long>();
            var targets = new Dictionary<string, List<string>>();
            var actions = new Dictionary<string, Dictionary<string, List<AiDiploAction>>>();
            List<AiDiploAction> Of(string player, string target)
            {
                var byTarget = actions.TryGetValue(player, out var d) ? d : actions[player] = new(StringComparer.OrdinalIgnoreCase);
                return byTarget.TryGetValue(target, out var l) ? l : byTarget[target] = [];
            }
            foreach (var e in Events(run, "ai_eval", "ai_exec", "ai_break_check", "ai_offer_check", "ai_gift_check"))
            {
                string player = e switch { AiEvalEvent v => v.Player ?? "", AiExecEvent x => x.Player ?? "", _ => PlayerAndTarget(e).Player };
                long? p = e switch { AiEvalEvent v => v.Pass, AiExecEvent x => x.Pass, _ => null };
                if (p is { } at && pass.GetValueOrDefault(player) != at)
                {
                    pass[player] = at;
                    targets[player] = [];
                    actions.Remove(player);
                }
                switch (e)
                {
                    case AiEvalEvent { List: "diplomacy" } v:
                    {
                        // Номер прохода у зонда повторяется (стратегия дня перезапускается с прохода 1): цель уже в списке —
                        // список 8 построен заново, прежние оценки не в счёт.
                        string id = Field(v.Eval, "id4")!;
                        if (targets[player].Contains(id, StringComparer.OrdinalIgnoreCase))
                        {
                            targets[player] = [];
                            actions.Remove(player);
                        }
                        targets[player].Add(id);
                        break;
                    }
                    case AiBreakCheckEvent { Ret: true } b:
                        Of(player, b.Other).Add(AiDiploAction.Break);
                        break;
                    case AiOfferCheckEvent o:
                        if (o.Ret) Of(player, o.Other!).Add(AiDiploAction.Offer);
                        else if (o.Turn == 1) Of(player, o.Other!).Add(AiDiploAction.Hate);
                        break;
                    case AiGiftCheckEvent { Action: not null } g:
                        Of(player, g.Other!).Add(AiDiploAction.Gift);
                        break;
                    case AiExecEvent { Action: { Type: 8 } act } x:
                    {
                        execs++;
                        kinds[act.Class ?? "?"] = kinds.GetValueOrDefault(act.Class ?? "?") + 1;
                        var listed = targets.GetValueOrDefault(player, []);
                        orderPasses++;
                        if (listed.SequenceEqual(listed.Order(StringComparer.Ordinal))) orderSame++;
                        else bad.Add($"{run} seq {x.Seq} {player}: цели не по id [{string.Join(" ", listed)}]");
                        var byTarget = actions.GetValueOrDefault(player);
                        if (byTarget?.Values.Any(l => l is [AiDiploAction.Hate, AiDiploAction.Gift]) == true) hateGift++;
                        if (byTarget?.Values.Sum(l => l.Count) > 1) several++;
                        var first = AiStrategic.DiplomacyEvaluations(listed, t => new AiDiploDecision(
                                byTarget?.GetValueOrDefault(t) is [var head, ..] ? head : AiDiploAction.None, null))
                            .Select(y => y.Evaluate()).FirstOrDefault(y => y.Count > 0)?[0];
                        string target = Field(act, "id4")!;
                        var kind = act.Class switch
                        {
                            "dipl_break" => AiDiploAction.Break, "dipl_offer" => AiDiploAction.Offer, "dipl_hate" => AiDiploAction.Hate,
                            _ => AiDiploAction.Gift,
                        };
                        if (first is { } f && f.Id.Equals(target, StringComparison.OrdinalIgnoreCase) && f.Target == kind.ToString()) same++;
                        else bad.Add($"{run} seq {x.Seq} {player}: движок {kind} {target}, порт {first?.Target} {first?.Id}");
                        break;
                    }
                }
            }
        }
        foreach (string line in bad.Take(20)) output.WriteLine(line);
        output.WriteLine($"исполнений дипломатии {execs} ({string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}"))}): выбор порта сошёлся {same}; "
                         + $"цели прохода в порядке id {orderSame} из {orderPasses}; действий в проходе больше одного {several}, "
                         + $"из них ненависть и подарок одной цели {hateGift} (исполнена ненависть)");
        Assert.Equal(execs, same);
        Assert.Equal(orderPasses, orderSame);
        return execs;
    }

    /// <summary>Сохранения дня карты партии: последнее за день (имя до <c>-tNN-</c> и день).</summary>
    private static IEnumerable<string> DaySaves(Run run) =>
        Directory.GetFiles(run.Folder, "*.sg").Order(StringComparer.Ordinal)
                 .Where(f => System.IO.Path.GetFileName(f).StartsWith(run.Stem, StringComparison.OrdinalIgnoreCase))
                 .GroupBy(f => System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileName(f), @"^(.*-t\d+)-").Groups[1].Value)
                 .Select(g => g.Last());

    private static int DayOf(string save) =>
        int.Parse(System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileName(save), @"-t(\d+)-").Groups[1].Value,
                  System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Раса каждого игрока по сохранениям карты партии (первое сохранение каждой карты).</summary>
    private static Dictionary<string, string> PlayerRaces(Run run)
    {
        var races = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sg in Directory.GetFiles(run.Folder, $"{run.Stem}*-t01-*.sg"))
            foreach (var p in MapLooksLoader.Load(sg).Map.Players.Values)
                races.TryAdd(p.Id, p.RaceId);
        return races;
    }

    private static string? Field(AiObject? o, string name) =>
        o is not null && o.Fields.TryGetValue(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    /// <summary>Слово <paramref name="k"/> (int32, little-endian) байтов объекта после vftable.</summary>
    private static int Word(string raw, int k) => BitConverter.ToInt32(Convert.FromHexString(raw.Substring(k * 8, 8)));

    /// <summary>События нужных видов партии (её отрезок seq) по порядку <c>seq</c>.</summary>
    private static List<CorpusEvent> Events(Run run, params string[] kinds) =>
        [.. JournalCache.Read(run.Path, kinds).Where(e => run.In(e.Seq)).OrderBy(e => e.Seq)];
}
