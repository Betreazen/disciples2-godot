using System.Text.Json;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Начало хода игрока ИИ по журналам зонда (<c>docs/ai-turn-plan.md</c>, срезы 0–1): порядок ходов в дне,
/// доход ИИ (золото в казну, половина маны во второй кошелёк — <see cref="Treasury.AiIncome"/>) и чит ресурсов
/// (<see cref="Treasury.AiResourceCheat"/>) дают казну на начало хода — поле <c>bank</c> первой строки <c>ai_budget</c>.</summary>
[Trait("Category", "Corpus")]
public class AiTurnStartHarnessTests(ITestOutputHelper output)
{
    private static readonly string[] Sessions = ["20260918-probe65", "20260919-human", "20260920-legions"];

    /// <summary>Порядок ходов: после человека — игроки ИИ в очереди <see cref="GameSession.TurnOrder"/> (ключ расы
    /// <c>0x42e1fe</c>: Империя, Легионы, Кланы, Орды, Эльфы, нейтралы; ранг 1 по листингу <c>0x42b8e8</c>); день у всех тот
    /// же, что у человека (<c>ai_turn begin</c>). Расы игроков — из первого сохранения карты рядом с журналом. Было правило
    /// «по id, нейтралы последними»: оно расходилось во всех 24 днях второй карты probe65 (Power to Corrupt, человек —
    /// Империя S073PL0009: ходят 000b Легионы → 000a Кланы → 000c Орды); с ключом расы — 0 из 179.</summary>
    [SkippableFact]
    public void TurnOrder_HumanThenAiByRaceKeyThenNeutrals()
    {
        var book = D2.Data.Play.GameBook.Load(Path.Combine(AppContext.BaseDirectory, "data", "rules"));
        int days = 0, mismatches = 0;
        foreach (string session in Sessions)
        {
            string path = Path.Combine(GameSaveTests.CorpusDir(), session, "passive-001.jsonl");
            Skip.IfNot(File.Exists(path), $"нет журнала {session}");
            // В одном журнале бывает несколько карт: день — вместе с префиксом id карты (S143, S073…).
            var byDay = new SortedDictionary<(string Map, long Turn), List<string>>();
            foreach (var e in Events(path, "\"ai_turn\""))
            {
                if (e.GetProperty("phase").GetString() != "begin" || e.GetProperty("cause").GetString() != "ai") continue;
                var turn = (e.GetProperty("player").GetString()![..4], e.GetProperty("turn").GetInt64());
                if (!byDay.TryGetValue(turn, out var list)) byDay[turn] = list = [];
                string player = e.GetProperty("player").GetString()!;
                if (!list.Contains(player)) list.Add(player);
            }
            var orders = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var (day, players) in byDay)
            {
                if (!orders.TryGetValue(day.Map, out var order))
                {
                    var map = D2.Data.Map.MapLoader.Load(FirstSaveOf(Path.GetDirectoryName(path)!, day.Map));
                    var humans = map.Players.Values.Where(p => p.IsHuman).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    orders[day.Map] = order = D2.Data.Play.GameSession.TurnOrder(map, humans, book);
                }
                var expected = order.Where(p => players.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                days++;
                if (!expected.SequenceEqual(players, StringComparer.OrdinalIgnoreCase))
                {
                    mismatches++;
                    output.WriteLine($"{session} день {day}: журнал {string.Join(",", players)}, порт {string.Join(",", expected)}");
                }
            }
        }
        output.WriteLine($"дней {days}, порядок разошёлся {mismatches}");
        Assert.True(days > 50);
        Assert.Equal(0, mismatches);
    }

    /// <summary>Первое по имени сохранение карты с префиксом id <paramref name="map"/> (S143, S073…): префикс — у первого
    /// <c>OBJ_ID</c> файла, без разбора сохранения.</summary>
    private static string FirstSaveOf(string dir, string map)
    {
        var mark = System.Text.Encoding.ASCII.GetBytes("OBJ_ID");
        foreach (string sg in Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(sg);
            int at = bytes.AsSpan().IndexOf(mark);
            // OBJ_ID, длина (4 байта), id.
            if (at >= 0 && System.Text.Encoding.ASCII.GetString(bytes, at + 10, 4) == map) return sg;
        }
        throw new FileNotFoundException($"нет сохранения карты {map} в {dir}");
    }

    /// <summary>Доход ИИ и чит ресурсов: из казны до дохода (первая строка <c>income</c>) и дохода модель даёт казну
    /// после первого начисления (вторая строка) и казну на начало хода (<c>ai_budget.bank</c>).</summary>
    [SkippableFact]
    public void AiIncome_AndResourceCheat_GiveTurnStartBank()
    {
        int split = 0, splitBad = 0, cheat = 0, cheatBad = 0;
        foreach (string session in Sessions)
        {
            string path = Path.Combine(GameSaveTests.CorpusDir(), session, "passive-001.jsonl");
            Skip.IfNot(File.Exists(path), $"нет журнала {session}");
            var events = Events(path, "\"income\"", "\"ai_budget\"", "\"ai_turn\"").ToList();
            for (int i = 0; i + 1 < events.Count; i++)
            {
                var a = events[i];
                var b = events[i + 1];
                if (Ev(a) != "income" || Ev(b) != "income" || Player(a) != Player(b)) continue;
                // Только ход ИИ: за парой строк идёт ai_budget этого игрока (у человека бывает двойная строка
                // income при загрузке — это не доход ИИ).
                var budget = events.Skip(i + 2).TakeWhile(e => Ev(e) != "income")
                                   .FirstOrDefault(e => Ev(e) == "ai_budget" && Player(e) == Player(a));
                if (budget.ValueKind != JsonValueKind.Object) continue;
                var income = Income(a);
                var (purse, _) = Treasury.AiIncome(BankOf(a.GetProperty("bank")), Bank.Zero, income);
                split++;
                if (purse != BankOf(b.GetProperty("bank")))
                {
                    splitBad++;
                    output.WriteLine($"{session} seq {a.GetProperty("seq")}: {Player(a)} доход {income}, до {BankOf(a.GetProperty("bank"))}, " +
                                     $"после {BankOf(b.GetProperty("bank"))}, модель {purse}");
                }
                // Казна на начало хода — первая ai_budget этого игрока после пары (до неё трат нет).
                if (budget.GetProperty("bank").ValueKind != JsonValueKind.Object) continue;
                cheat++;
                // Сложность партий корпуса — средняя: D_AVG_M = 100 (GAI).
                var start = Treasury.AiResourceCheat(purse, Income(b), 100);
                if (start != BankOf(budget.GetProperty("bank"))) cheatBad++;
                i++;
            }
        }
        output.WriteLine($"доход ИИ: {split} пар, расхождений {splitBad}; чит ресурсов → ai_budget.bank: {cheat}, расхождений {cheatBad}");
        Assert.True(split > 500);
        Assert.Equal(0, splitBad);
        Assert.Equal(0, cheatBad);
    }

    private static string Ev(JsonElement e) => e.GetProperty("ev").GetString()!;

    private static string Player(JsonElement e) => e.GetProperty("player").GetString() ?? "";

    private static Bank Income(JsonElement e)
    {
        var m = e.GetProperty("mana");
        return new Bank(e.GetProperty("gold").GetInt64(), m.GetProperty("infernal").GetInt64(), m.GetProperty("life").GetInt64(),
                        m.GetProperty("death").GetInt64(), m.GetProperty("runic").GetInt64(), m.GetProperty("grove").GetInt64());
    }

    private static Bank BankOf(JsonElement b) =>
        new(b.GetProperty("gold").GetInt64(), b.GetProperty("infernal").GetInt64(), b.GetProperty("life").GetInt64(),
            b.GetProperty("death").GetInt64(), b.GetProperty("runic").GetInt64(), b.GetProperty("grove").GetInt64());

    /// <summary>События журнала, чья строка содержит одну из меток (быстрый отбор до разбора JSON), в порядке
    /// <c>seq</c>: строки разных потоков в файле бывают переставлены.</summary>
    private static IEnumerable<JsonElement> Events(string path, params string[] marks)
    {
        var list = new List<JsonElement>();
        foreach (string line in File.ReadLines(path))
        {
            if (!marks.Any(m => line.Contains("\"ev\":" + m, StringComparison.Ordinal))) continue;
            using var doc = JsonDocument.Parse(line);
            list.Add(doc.RootElement.Clone());
        }
        return list.OrderBy(e => e.GetProperty("seq").GetInt64());
    }

    /// <summary>Бюджет дня ИИ (<c>0x446292</c>, <see cref="AiBudget.Start"/>): по каждой строке <c>ai_budget</c> —
    /// <c>⌊казна·pct/100⌋</c>, <c>pct = min(B_типа + 5·(день − день последней траты типа), 100)</c>; день последней траты —
    /// из <c>ai_budget_spend</c> этой же партии. Счётчики по типам печатаются; порог — измеренные числа. Резерв исследования
    /// (<c>flag</c> типа 6) сверяется с правилом <see cref="AiStrategicRules.ResearchReserve"/>: известные заклинания — из
    /// первого сохранения карты плюс исследованные и купленные по журналу до строки.</summary>
    [SkippableFact]
    public void DayBudget_ByType()
    {
        var ok = new Dictionary<int, int>();
        var bad = new Dictionary<int, int>();
        var samples = new List<string>();
        var book = D2.Data.Play.GameBook.Load(Path.Combine(AppContext.BaseDirectory, "data", "rules"));
        int reserveOk = 0, reserveBad = 0;
        var shares = D2.Data.Map.EconomyLoader.LoadAiLeaderShares(Path.Combine(AppContext.BaseDirectory, "data", "rules"));
        int leaderOk = 0, leaderBad = 0, leaderUnknown = 0;
        foreach (string session in Sessions)
        {
            string path = Path.Combine(GameSaveTests.CorpusDir(), session, "passive-001.jsonl");
            Skip.IfNot(File.Exists(path), $"нет журнала {session}");
            var last = new Dictionary<(string, int), long>();
            var day = new Dictionary<string, long>();
            var attitude = Attitudes(path);
            // Тип 0: строка бюджета ждёт ближайшего вызова роли того же игрока — там счётчики ролей (0x44a405).
            var pendingLeader = new Dictionary<string, (Bank Bank, Bank Want, int Day, int Last)>(StringComparer.OrdinalIgnoreCase);
            // Известные заклинания игрока: из первого сохранения его карты, дальше — исследования и покупки журнала.
            var known = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var lords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> KnownOf(string player)
            {
                if (known.TryGetValue(player, out var set)) return set;
                var map = D2.Data.Map.MapLoader.Load(FirstSaveOf(Path.GetDirectoryName(path)!, player[..4]));
                foreach (var p in map.Players.Values)
                {
                    known[p.Id] = p.KnownSpells.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    lords[p.Id] = p.LordId;
                }
                return known[player];
            }
            foreach (var e in Events(path, "\"ai_budget\"", "\"ai_budget_spend\"", "\"ai_turn\"", "\"ai_leader_role\"",
                                     "\"spell_research\"", "\"site_buy_spell\""))
            {
                string player = Player(e);
                switch (Ev(e))
                {
                    case "spell_research" or "site_buy_spell" when e.GetProperty("ret").GetBoolean():
                        KnownOf(player).Add(e.GetProperty("spell").GetString()!);
                        break;
                    case "ai_leader_role" when e.GetProperty("fn").GetString() == "role" && pendingLeader.Remove(player, out var pl):
                    {
                        var i = e.GetProperty("i").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                        var ok3 = e.GetProperty("b").EnumerateArray().Select(x => x.GetInt32() != 0).ToArray();
                        if (!attitude.TryGetValue(player, out int att) || !shares.TryGetValue(att, out var sh)) { leaderUnknown++; break; }
                        // База 0x44a053: счётчики i = [бойцы, жезлоносцы, воры], допуски b = [боец, жезл, вор].
                        int leaderBase = AiStrategicRules.LeaderBudgetBase(i[0], i[1], i[2], sh, rodOk: ok3[1], thiefOk: ok3[2],
                                                                           highLevel: false, AiBudgetRules.Vanilla.Base[AiStepType.HireLeader]);
                        var rules = AiBudgetRules.Vanilla with
                        {
                            Base = AiBudgetRules.Vanilla.Base.ToDictionary(x => x.Key, x => x.Key != AiStepType.HireLeader ? x.Value : leaderBase),
                            FirstDay = new Dictionary<AiStepType, int>(),
                        };
                        if (AiBudget.StartOf(AiStepType.HireLeader, pl.Day, pl.Last, pl.Bank, Bank.Zero, rules) == pl.Want) leaderOk++;
                        else
                        {
                            leaderBad++;
                            if (leaderBad <= 12)
                                output.WriteLine($"тип 0 разошёлся: {session} {player} день {pl.Day} последняя {pl.Last} отношение {att} счётчики [{string.Join(",", i)}] " +
                                                 $"мин ({sh.FighterMin},{sh.RodMin},{sh.ThiefMin}) казна {pl.Bank.Gold} журнал {pl.Want.Gold} порт {AiBudget.StartOf(AiStepType.HireLeader, pl.Day, pl.Last, pl.Bank, Bank.Zero, rules).Gold}");
                        }
                        break;
                    }
                    case "ai_turn" when e.GetProperty("phase").GetString() == "begin":
                    {
                        day[player] = e.GetProperty("turn").GetInt64();
                        break;
                    }
                    case "ai_budget_spend":
                        last[(player, e.GetProperty("type").GetInt32())] = day.GetValueOrDefault(player);
                        break;
                    case "ai_budget" when e.GetProperty("budget").ValueKind == JsonValueKind.Object:
                    {
                        // ai_turn begin идёт раньше строк ai_budget (seq): день — текущий ход игрока.
                        int type = e.GetProperty("type").GetInt32();
                        long d = day.GetValueOrDefault(player);
                        var bank = BankOf(e.GetProperty("bank"));
                        var spell = e.GetProperty("spell_bank").ValueKind == JsonValueKind.Object ? BankOf(e.GetProperty("spell_bank")) : Bank.Zero;
                        var want = BankOf(e.GetProperty("budget"));
                        var got = AiBudget.StartOf((AiStepType)type, (int)d, (int)last.GetValueOrDefault((player, type)), bank, spell,
                                                   AiBudgetRules.Vanilla, e.GetProperty("flag").GetBoolean());
                        if (type == 0) pendingLeader[player] = (bank, want, (int)d, (int)last.GetValueOrDefault((player, type)));
                        if (type == 6)
                        {
                            bool reserve = AiStrategicRules.ResearchReserve(KnownOf(player).Count, (LordCategory)book.LordCategory(lords[player]));
                            if (reserve == e.GetProperty("flag").GetBoolean()) reserveOk++;
                            else reserveBad++;
                        }
                        if (got == want) ok[type] = ok.GetValueOrDefault(type) + 1;
                        else
                        {
                            bad[type] = bad.GetValueOrDefault(type) + 1;
                            if (type != 0 && samples.Count(x => x.StartsWith($"тип {type}", StringComparison.Ordinal)) < 3)
                                samples.Add($"тип {type} {session} день {d} последняя {last.GetValueOrDefault((player, type))}: " +
                                            $"казна {bank}, магия {spell}, журнал {want}, модель {got}");
                        }
                        break;
                    }
                }
            }
        }
        foreach (int t in ok.Keys.Union(bad.Keys).Order())
            output.WriteLine($"тип {t}: сошлось {ok.GetValueOrDefault(t)}, разошлось {bad.GetValueOrDefault(t)}");
        foreach (string x in samples) output.WriteLine(x);
        output.WriteLine($"резерв исследования: сошлось {reserveOk}, разошлось {reserveBad}");
        foreach (int t in new[] { 1, 2, 3, 5, 6, 8 })
        {
            Assert.True(ok.GetValueOrDefault(t) > 500, $"тип {t}");
            Assert.Equal(0, bad.GetValueOrDefault(t));
        }
        // Резерв исследования по правилу 0x44cd3e (известных меньше половины предела): 549 из 549. Прежде порт держал
        // его включённым всегда — так расходятся 165 строк (флаг сбрасывается навсегда, когда известных становится 6).
        Assert.True(reserveOk > 500);
        Assert.Equal(0, reserveBad);
        // Столица: 2 строки legions день 12 — повтор дня после перезагрузки; память трат в сохранении старше
        // сегодняшней траты, журнал её не восстанавливает. Тип 0 (найм лидера): база B не разобрана — измеренное
        // число (ponytail в AiBudget.StartOf).
        Assert.Equal(2, bad.GetValueOrDefault(4));
        Assert.Equal(397, bad.GetValueOrDefault(0));
        // Тип 0 с базой 0x44a053 (AiStrategicRules.LeaderBudgetBase): бойцов нет — 100, недобор при допуске — 90, бойцов меньше
        // L_MAX — B_LEADERS, иначе 0; счётчики и допуски — из ближайшего вызова роли того же хода.
        output.WriteLine($"тип 0 по недобору ролей: сошлось {leaderOk}, разошлось {leaderBad}, без отношения {leaderUnknown}");
        // Измерено 23.09: 369 из 375 (правило по журналу); 26.09 по листингу 0x44a053 — 371: дни без единого бойца (база 100)
        // сошлись. Остаток 4 — повтор дня после перезагрузки (последняя трата «сегодня»).
        Assert.Equal(4, leaderBad);
    }

    /// <summary>Отношение ИИ игроков (<c>ATTITUDE</c>) из первого сохранения рядом с журналом.</summary>
    private static Dictionary<string, int> Attitudes(string journal)
    {
        var sg = Directory.GetFiles(Path.GetDirectoryName(journal)!, "*.sg").Order(StringComparer.Ordinal).FirstOrDefault();
        return sg is null ? [] : D2.Data.Map.MapLoader.Load(sg).Players.Values.ToDictionary(p => p.Id, p => p.Attitude, StringComparer.OrdinalIgnoreCase);
    }
}
