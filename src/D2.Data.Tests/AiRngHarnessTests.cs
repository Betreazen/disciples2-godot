using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Броски решений игры журнала схемы 7.5 (<c>ai_rng</c>, <c>docs/corpus-schema.md</c> «Схема 7.5») — по местам: сколько строк, и
/// повторяет ли их порт (место, граница, значение).
/// <list type="number">
/// <item>Поток R250 (<see cref="JournalR250"/>): строки <c>ai_rng</c> R250 и <c>rng</c> боя — каждый вызов <c>0x54851f</c> партии подряд
/// (разрывов нет — места 7.5 полны), и генератор порта с сева повторяет ответ каждого вызова с его границей.</item>
/// <item>CRT: сырой <c>rand()</c> строки — тот же, что у потока CRT порта (<c>srand(1)</c> на поток ОС, <see cref="JournalCrt"/>), ответ —
/// <c>rand() % max</c> (<c>0x548576</c>); у <c>unit_pick</c> граница — отобранные фильтром из списка найма <c>ai_unit_pick</c>.</item>
/// <item>Правила порта на бросках журнала (<see cref="JournalRolls"/>): чат (<see cref="AiDiplomacy.Chat"/> — граница = строк
/// <c>TAiMsg</c> расы и категории, номер = <c>ai_chat.roll</c>), «натиск» (<see cref="AiSpellRules.HarassGo"/>/<see cref="AiSpellRules.HarassCount"/>:
/// бросок числа — только у близкого прохода и только при <c>rng(100) ≤ 49</c>), вор (<see cref="Thieves.Roll"/> — кубики и успех
/// <c>thief_roll</c>/<c>noble_action</c>), мятеж (<see cref="Fortifications.RiotEnd"/> — граница <c>RIOT_MAX − RIOT_MIN</c>), шанс события
/// (<see cref="ScenarioEvents.ChanceDie"/>).</item>
/// </list>
/// Решения целиком на бросках журнала сверяют <see cref="AiSpellHarnessTests"/> (оперативный слой: «натиск», <c>op_spell_*</c>) и
/// <see cref="AiSiteVisitHarnessTests"/> (торговец: <c>merchant_item</c>).
/// </summary>
[Trait("Category", "Synth")]
public class AiRngHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Партии журнала: начало имени сохранений карты и отрезок <c>seq</c> (карты одной <c>S143</c> в одном журнале).</summary>
    private static readonly (string Stem, long From, long To)[] Maps18b =
    [
        ("port-synth-16-ai-calm", 437, 4507811), ("port-synth-17-mixed", 4508451, 6591373), ("port-synth-18-ai-rows", 6591743, long.MaxValue),
    ];

    /// <summary>Измерено 19.10.2026 (первая партия с зондом 7.5, карты 16–18; не подгонять): вызовов R250 после первой строки партии
    /// 422 (ai_rng 159, бой 263) — разрывов нумерации 0, сдвиг от сева нашёлся у всех трёх новых партий, ответ порта совпал у 422 из 422;
    /// в загруженной партии (карта 17 со дня 2) вызовов R250 нет. Способ журналов до 7.5 (<see cref="JournalRng"/> — опоры только строки
    /// боя) предсказал 14 строк <c>ai_rng</c> (после боёв), все 14 верно.</summary>
    [SkippableFact]
    public void R250Stream_CompleteAndReplayed()
    {
        string journal = Journal("20261018b-port-synth");
        var stream = Stream(journal);
        output.WriteLine($"вызовов {stream.Draws.Count}, повторено {stream.Replayed}, разрывов {stream.Gaps}, без сдвига {stream.Unfitted}, "
                         + $"после загрузки {stream.AfterLoad}, разошлось {stream.Mismatches.Count}");
        foreach (var (d, port) in stream.Mismatches.Take(20))
            output.WriteLine($"  {d.Site} n {d.N} max {d.Max}: журнал {d.R}, порт {port}");
        foreach (var g in stream.Draws.GroupBy(d => d.Site == "battle" ? "battle" : "ai_rng"))
            output.WriteLine($"  {g.Key}: {g.Count()}");
        Assert.Equal(0, stream.Gaps);
        Assert.Equal(0, stream.Unfitted);
        Assert.Equal(0, stream.AfterLoad);
        Assert.Equal(stream.Draws.Count, stream.Replayed);
        Assert.Empty(stream.Mismatches);
        Assert.Equal(422, stream.Replayed);

        // Проверка способа журналов до 7.5 (JournalRng: генератор от сева, сдвиг — по строкам rng боя до счётчика): им харнессы торговца
        // (И39) и литья перед атакой (И30) бросают там, где строк ai_rng нет. Здесь те же опоры — только строки боя, а ответ сверяется со
        // строкой ai_rng.
        var events = JournalCache.Read(journal, ["rng_seed", "rng", "ai_rng"]).ToList();
        var old = new JournalRng(events.Where(e => e is not AiRngEvent));
        int predicted = 0, right = 0;
        foreach (var r in events.OfType<AiRngEvent>().Where(r => r.Kind == "r250"))
            if (old.At(r.Seq, r.N, minBits: 48) is { } g)
            {
                predicted++;
                if (g.Next((int)r.Max) == r.R) right++;
            }
        output.WriteLine($"JournalRng по строкам боя: предсказано {predicted} строк ai_rng, верно {right}");
        Assert.Equal(14, predicted);
        Assert.Equal(predicted, right);
    }

    /// <summary>Строк и сверок по местам (место → строк → сверено правилом порта → сошлось). Измерено 19.10.2026 (не подгонять):
    /// <c>unit_pick</c> 284 (CRT — 284; граница не больше длины списка <c>ai_unit_pick</c>), <c>harass_chance</c> 84 и <c>harass_count</c> 19 (правило — 84 и 19),
    /// <c>ai_chat_msg</c> 27 (27), <c>merchant_item</c> 18 (значения — поток R250; покупки — <see cref="AiSiteVisitHarnessTests"/>),
    /// <c>event_chance</c> 5 (граница 5), <c>op_spell_start</c> 4 (поток; выбор — <see cref="AiSpellHarnessTests"/>),
    /// <c>new_game_desc</c> 3 (CRT 3), <c>thief_dice1</c>/<c>thief_dice2</c> 1/1 (кубики и успех), <c>riot_crt</c> 1 (CRT и граница).</summary>
    [SkippableFact]
    public void Sites_MatchPortRules()
    {
        string journal = Journal("20261018b-port-synth");
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var events = JournalCache.Read(journal, ["ai_rng", "ai_chat", "ai_unit_pick", "thief_roll", "noble_action", "crt_rand", "ai_turn"])
                                 .OrderBy(e => e.Seq).ToList();
        var rolls = events.OfType<AiRngEvent>().ToList();
        var stream = Stream(journal);
        var crt = new JournalCrt(events);
        var crtRows = events.OfType<CrtRandEvent>().OrderBy(c => c.Seq).ToList();
        var tally = new SortedDictionary<string, (int Rows, int Checked, int Ok)>(StringComparer.Ordinal);
        void Count(string site, bool checkedHere, bool ok)
        {
            var t = tally.GetValueOrDefault(site);
            tally[site] = (t.Rows + 1, t.Checked + (checkedHere ? 1 : 0), t.Ok + (checkedHere && ok ? 1 : 0));
        }
        void Bad(AiRngEvent r, string why) => output.WriteLine($"  {r.Site} seq {r.Seq} n {r.N} max {r.Max} r {r.R}: {why}");

        var replayed = stream.Draws.Where(d => d.Site != "battle").Select(d => d.N).ToHashSet();
        var races = Maps18b.ToDictionary(m => m.Stem, m => RacesOf(journal, m.Stem));
        string StemOf(long seq) => Maps18b.First(m => seq >= m.From && seq <= m.To).Stem;
        var picks = events.OfType<AiUnitPickEvent>().ToList();
        var chats = events.OfType<AiChatEvent>().ToList();
        var thiefRolls = events.OfType<ThiefRollEvent>().ToList();
        var nobles = events.OfType<NobleActionEvent>().ToList();
        for (int i = 0; i < rolls.Count; i++)
        {
            var r = rolls[i];
            bool ok;
            switch (r.Site)
            {
                case "unit_pick" or "new_game_desc" or "leader_name" or "ground_variant" or "riot_crt":
                {
                    ok = CrtOk(r, crt, crtRows, out string why);
                    // unit_pick: граница — отобранные фильтром (ближний малый, большой, любой) из списка найма ai_unit_pick, не list_len.
                    if (ok && r.Site == "unit_pick" && !picks.Any(p => p.Seq < r.Seq && p.SeqEnd > r.Seq && p.ListLen >= r.Max))
                    {
                        ok = false;
                        why = "нет ai_unit_pick с длиной списка не меньше границы";
                    }
                    if (ok && r.Site == "riot_crt")
                    {
                        int turn = 0;
                        int end = Fortifications.RiotEnd(book.Thieves, turn, new JournalRolls([r]));
                        ok = book.Thieves.RiotMax - book.Thieves.RiotMin == r.Max && end == book.Thieves.RiotMin + 1 + r.R;
                        why = $"RIOT_MAX − RIOT_MIN {book.Thieves.RiotMax - book.Thieves.RiotMin}";
                    }
                    if (!ok) Bad(r, why);
                    Count(r.Site, true, ok);
                    break;
                }
                case "ai_chat_msg":
                {
                    var chat = chats.FirstOrDefault(c => c.RngBefore + 1 == r.N && c.Seq > r.Seq - 50 && c.Seq < r.Seq + 50);
                    int n = chat?.From is { } from && races[StemOf(r.Seq)].TryGetValue(from, out var race)
                        ? book.AiMessages(race, (AiMessage)chat.Category).Count : -1;
                    var jr = new JournalRolls([r]);
                    ok = n == r.Max && AiDiplomacy.Chat(jr, n) == chat!.Roll && jr.Exact;
                    if (!ok) Bad(r, $"ai_chat {chat?.Seq} roll {chat?.Roll}, строк TAiMsg у порта {n}");
                    Count(r.Site, true, ok);
                    break;
                }
                case "harass_chance":
                {
                    bool far = r.Origin == "harass_far";
                    var next = i + 1 < rolls.Count ? rolls[i + 1] : null;
                    bool go = AiSpellRules.HarassGo(new JournalRolls([r]));
                    bool countRow = next is { Site: "harass_count" } && next.Player == r.Player;
                    // Далёкий проход число не бросает (HarassCount(far) без броска — пустые броски журнала не просятся).
                    ok = r.Max == 100 && (far ? !countRow && AiSpellRules.HarassCount(true, new JournalRolls([])) == 1 : countRow == go);
                    if (!ok) Bad(r, $"{r.Origin}: порт go {go}, за ним строка числа {countRow}");
                    Count(r.Site, true, ok);
                    break;
                }
                case "harass_count":
                {
                    var prev = i > 0 ? rolls[i - 1] : null;
                    var jr = new JournalRolls([r]);
                    int count = AiSpellRules.HarassCount(false, jr);
                    ok = prev is { Site: "harass_chance", Origin: "harass_near" } && prev.R <= 49 && jr.Exact && count == r.R + 2;
                    if (!ok) Bad(r, $"перед ней {prev?.Site}/{prev?.Origin} r {prev?.R}");
                    Count(r.Site, true, ok);
                    break;
                }
                case "thief_dice1":
                {
                    var ev = thiefRolls.FirstOrDefault(t => t.RngBefore + 1 == r.N);
                    var second = i + 1 < rolls.Count && rolls[i + 1].Site == "thief_dice2" ? rolls[i + 1] : null;
                    var noble = nobles.FirstOrDefault(nb => ev is not null && nb.Seq > ev.Seq);
                    ok = ev is { Chance: { } chance } && second is not null && noble is not null;
                    if (ok)
                    {
                        var jr = new JournalRolls([r, second!]);
                        var roll = Thieves.Roll(jr, (int)ev!.Chance!.Value);
                        ok = jr.Exact && roll.Dice1 == ev.Dice1 && roll.Dice2 == ev.Dice2 && roll.Success == noble!.Success;
                    }
                    if (!ok) Bad(r, $"thief_roll {ev?.Seq} кубики {ev?.Dice1}/{ev?.Dice2} шанс {ev?.Chance}, успех {noble?.Success}");
                    Count(r.Site, true, ok);
                    if (second is not null)
                    {
                        Count(second.Site, true, ok);
                        i++;
                    }
                    break;
                }
                case "event_chance":
                    ok = r.Max == ScenarioEvents.ChanceDie && replayed.Contains(r.N);
                    if (!ok) Bad(r, "граница или поток");
                    Count(r.Site, true, ok);
                    break;
                default:
                    // Значение и граница — потоком R250 (R250Stream_CompleteAndReplayed); решение целиком — своим харнессом.
                    Count(r.Site, r.Kind == "r250", replayed.Contains(r.N) && !stream.Mismatches.Any(m => m.Draw.N == r.N));
                    break;
            }
        }
        foreach (var (site, t) in tally) output.WriteLine($"{site,-20} строк {t.Rows,4}, сверено {t.Checked,4}, сошлось {t.Ok,4}");
        foreach (var (site, t) in tally) Assert.True(t.Checked == t.Ok, $"{site}: сошлось {t.Ok} из {t.Checked}");
        Assert.Equal(447, tally.Values.Sum(t => t.Rows));
        Assert.Equal(447, tally.Values.Sum(t => t.Checked));
        Assert.Equal((284, 284, 284), tally["unit_pick"]);
        Assert.Equal((84, 84, 84), tally["harass_chance"]);
        Assert.Equal((19, 19, 19), tally["harass_count"]);
        Assert.Equal((27, 27, 27), tally["ai_chat_msg"]);
        Assert.Equal((18, 18, 18), tally["merchant_item"]);
    }

    /// <summary>
    /// Шанс события (<c>event_chance</c>, <c>0x441ea0</c>) на ходу человека карты 17: шаг в зону события — два броска R250(100), упреждающая
    /// проверка пути (<see cref="GameSession.PathEvents"/>, <c>0x440284</c>) и проверка после хода (<see cref="GameSession.MoveStack"/>). Порт
    /// идёт тот же шаг с сохранения дня с генератором в состоянии движка перед первым броском (<see cref="JournalR250.Before"/>): после хода
    /// генератор порта — в состоянии движка после второго (те же вызовы с теми же границами), а событие создаёт тот же отряд.
    /// Измерено 19.10.2026 (не подгонять): «[PS-E01g]» (<c>S143KC0006</c> 14,18 → 15,18, броски 912674–912675, Гоблин в 16,18) и
    /// «[PS-PRI]» (<c>S143KC0013</c> 8,64 → 10,64, броски 912676–912677, отряд в 14,64) — оба сошлись.
    /// </summary>
    [SkippableTheory]
    [InlineData("S143KC0006", 14, 18, 15, 18, 912674L, 16, 18)]
    [InlineData("S143KC0013", 8, 64, 10, 64, 912676L, 14, 64)]
    public void EventChance_StepIntoZone_TwoRollsLikeEngine(string stackId, int fromX, int fromY, int toX, int toY, long firstRoll, int madeX, int madeY)
    {
        string journal = Journal("20261018b-port-synth");
        var stream = Stream(journal);
        var before = stream.Before(firstRoll);
        var after = stream.After(firstRoll + 1);
        Skip.If(before is null || after is null, "бросков нет в потоке");
        Assert.All(new[] { firstRoll, firstRoll + 1 }, n => Assert.Equal("event_chance", stream.Draws.Single(d => d.N == n).Site));
        // Сохранение дня 1 перед шагом: отряд в начальной клетке, клетка созданного отряда пуста (последнее такое по порядку файлов).
        string dir = Path.GetDirectoryName(journal)!;
        string? sg = Directory.GetFiles(dir, "port-synth-17-mixed-*-t01-*.sg").Order(StringComparer.Ordinal).LastOrDefault(f =>
        {
            var m = MapLooksLoader.Load(f).Map;
            return m.Stacks.TryGetValue(stackId, out var s) && (s.X, s.Y) == (fromX, fromY) && !m.Stacks.Values.Any(o => (o.X, o.Y) == (madeX, madeY));
        });
        Skip.If(sg is null, "нет сохранения перед шагом");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var looks = MapLooksLoader.Load(sg!);
        var events = EventLoader.Load(sg!);
        var info = new GameInfo("PtC", "PtC", [.. looks.Map.Players.Values.Where(p => p.IsHuman).Select(p => p.Id)], looks.Map.Limits?.DifficultyGame ?? 1,
                                looks.Map.Limits?.SuggestedLevel ?? 1);
        var state = new GameState(info, looks) { Events = events, EventProgress = EventProgress.From(events), Random = new RandomStreams(before!.Index, before.TableSnapshot(), 1) };
        var session = new GameSession(state, content.MapArt, book);
        var stack = session.Map.Stacks[stackId];
        var path = session.PathTo(stack, toX, toY);
        var move = PlayerMove.Plan(session, stackId, path);
        var end = move.Today[^1];
        session.MoveStack(stackId, end.X, end.Y, PathFinder.Facing(path.Points[0], end), path.Points[move.Today.Count].Cost, move.Fired, move.Today);
        bool made = session.Map.Stacks.Values.Any(s => (s.X, s.Y) == (madeX, madeY));
        output.WriteLine($"{Path.GetFileName(sg)}: путь [{string.Join(" ", move.Today.Select(p => $"{p.X},{p.Y}"))}], сработало "
                         + $"[{string.Join(" ", move.Fired.Select(f => f.Event.Id))}], отряд в {madeX},{madeY}: {made}, генератор как у движка: {JournalR250.Same(session.Rng, after!)}");
        var probe = before.Clone();
        int steps = 0;
        while (steps < 1000 && !JournalR250.Same(probe, session.Rng)) { probe.Step(); steps++; }
        output.WriteLine($"  шагов генератора у порта: {(steps < 1000 ? steps : "> 1000")}; у движка {after!.Steps - before.Steps}");
        Assert.Equal((toX, toY), (end.X, end.Y));
        Assert.True(made);
        Assert.True(JournalR250.Same(session.Rng, after!), "генератор порта после хода — не в состоянии движка после двух бросков шанса");
    }

    /// <summary>Сырой <c>rand()</c> строки CRT — значение потока CRT порта в парной строке <c>crt_rand</c> (следующая того же потока ОС),
    /// ответ — <c>rand() % max</c>.</summary>
    private static bool CrtOk(AiRngEvent r, JournalCrt crt, List<CrtRandEvent> crtRows, out string why)
    {
        var pair = crtRows.FirstOrDefault(c => c.Tid == r.Tid && c.Seq > r.Seq);
        if (pair is null || r.Raw is not { } raw)
        {
            why = "нет парной строки crt_rand";
            return r.Max == 0 && r.R == 0;
        }
        int port = new CrtRand(crt.Before(pair.Tid, pair.Seq)).Next();
        why = $"crt_rand {pair.Seq} {pair.R}, поток порта {port}";
        return port == raw && pair.R == raw && r.R == (r.Max == 0 ? 0 : raw % r.Max);
    }

    /// <summary>Раса каждого игрока карты (первое сохранение партии).</summary>
    private static Dictionary<string, string> RacesOf(string journal, string stem)
    {
        string sg = Directory.GetFiles(Path.GetDirectoryName(journal)!, stem + "*.sg").Order(StringComparer.Ordinal).First();
        return MapLooksLoader.Load(sg).Map.Players.ToDictionary(p => p.Key, p => p.Value.RaceId, StringComparer.OrdinalIgnoreCase);
    }

    internal static JournalR250 Stream(string journal) =>
        new(JournalCache.Read(journal, ["rng_seed", "rng", "ai_rng", "game_phase"]));

    private static string Journal(string corpus)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), corpus, "passive-001.jsonl");
        Skip.IfNot(File.Exists(path), "нет корпуса " + corpus);
        return path;
    }
}

/// <summary>Поток R250 партии с покрытием <c>20261019c-cov</c> (Greenskins, человек, 18 дней, зонд 7.5; две загрузки сохранения посреди
/// партии) — тот же разбор, что <see cref="AiRngHarnessTests.R250Stream_CompleteAndReplayed"/>: все вызовы <c>0x54851f</c> подряд и ответ
/// генератора порта с сева. Измерено 19.10.2026 (не подгонять): вызовов 5117 (бой 4114, <c>event_chance</c> 442, <c>shuffle_list</c> 236,
/// <c>merchant_item</c> 147 …); новая партия (дни 1–3) — 967 вызовов подряд, повторены все, ответ порта совпал у всех; шанс события вступления
/// (n = 1) — до создания карты (<see cref="JournalR250.BeforeMap"/>); 4149 вызовов после загрузок (дни 3–18) — состояние из сохранения,
/// не повторяются.</summary>
[Trait("Category", "Corpus")]
public class AiRngHarnessTests_Cov(ITestOutputHelper output)
{
    [SkippableFact]
    public void R250Stream_CompleteAndReplayed()
    {
        string journal = Path.Combine(GameSaveTests.CorpusDir(), "20261019c-cov", "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса 20261019c-cov");
        var stream = AiRngHarnessTests.Stream(journal);
        output.WriteLine($"вызовов {stream.Draws.Count}, повторено {stream.Replayed}, разрывов {stream.Gaps}, без сдвига {stream.Unfitted}, "
                         + $"после загрузки {stream.AfterLoad}, до карты {stream.BeforeMap}, разошлось {stream.Mismatches.Count}");
        foreach (var (d, port) in stream.Mismatches.Take(20))
            output.WriteLine($"  {d.Site} n {d.N} max {d.Max}: журнал {d.R}, порт {port}");
        foreach (var g in stream.Draws.GroupBy(d => d.Site).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {g.Key}: {g.Count()}");
        Assert.Equal(0, stream.Gaps);
        Assert.Equal(0, stream.Unfitted);
        Assert.Empty(stream.Mismatches);
        Assert.Equal(967, stream.Replayed);
        Assert.Equal(1, stream.BeforeMap);
        Assert.Equal(4149, stream.AfterLoad);
    }
}

/// <summary>Поток R250 партии карты 23 (<c>20261020-port-synth</c>, <c>port-synth-23-ai-long</c>: три ИИ на очень трудной, 60 дней, зонд 7.5;
/// человек сохранялся в начале каждого дня и не загружался) — тот же разбор, что <see cref="AiRngHarnessTests_Cov"/>. Измерено 20.10.2026: вызовов
/// 6873 (бой 4955, выбор лидера 644, <c>op_spell_start</c> 356, <c>shuffle_list</c> 262, раскрытие шпиона 145 …) — вся партия подряд с сева,
/// повторены все, ответ порта совпал у всех; загрузок нет.</summary>
[Trait("Category", "Synth")]
public class AiRngHarnessTests_PortSynth23(ITestOutputHelper output)
{
    [SkippableFact]
    public void R250Stream_CompleteAndReplayed()
    {
        string journal = Path.Combine(GameSaveTests.CorpusDir(), "20261020-port-synth", "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса 20261020-port-synth");
        var stream = AiRngHarnessTests.Stream(journal);
        output.WriteLine($"вызовов {stream.Draws.Count}, повторено {stream.Replayed}, разрывов {stream.Gaps}, без сдвига {stream.Unfitted}, "
                         + $"после загрузки {stream.AfterLoad}, до карты {stream.BeforeMap}, разошлось {stream.Mismatches.Count}");
        foreach (var (d, port) in stream.Mismatches.Take(20))
            output.WriteLine($"  {d.Site} n {d.N} max {d.Max}: журнал {d.R}, порт {port}");
        foreach (var g in stream.Draws.GroupBy(d => d.Site).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {g.Key}: {g.Count()}");
        Assert.Equal(0, stream.Gaps);
        Assert.Equal(0, stream.Unfitted);
        Assert.Empty(stream.Mismatches);
        Assert.Equal(6873, stream.Replayed);
        Assert.Equal(0, stream.BeforeMap);
        Assert.Equal(0, stream.AfterLoad);
    }
}
