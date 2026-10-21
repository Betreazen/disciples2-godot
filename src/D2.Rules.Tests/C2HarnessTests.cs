using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Rng;
using Xunit.Abstractions;

/// <summary>С2 (ТЗ §7): бой прогоняется на модели по броскам журнала, и последовательность ходов
/// (кто ходит, промах, урон, HP цели, конец боя, победитель) сверяется с журналом. Порог ≥ 99 %
/// боёв при N ≥ 10; С2 &lt; 90 % — точка отмены.
///
/// Два замера. <b>Строгий</b> — BattleSim.Run целиком: очередь, механика и политика ИИ (BattleAi: цель, защита, бегство,
/// предметы, место призыва). Из журнала берутся только решения человека (<see cref="HumanChoices"/>: ход с нажатой кнопкой или
/// щелчком по цели — защита, ожидание, бегство, цель удара, предмет, место призыва), потому что модели человека нет; решения
/// ИИ сверяются и сами (<see cref="CompareChoices"/>), и по следствиям. <b>Журнальный</b> (диагностический) — ходящий из журнала
/// (turn_before / первый turn_after), цель одиночного удара — из dmg, immune после броска или attack_hit, лечения — из роста HP
/// или attack_hit, очередь — на каждое событие round, всё остальное — модель (площадная цель и её порядок — Attacks.Shuffle,
/// вторая атака, DoT, паралич). Разница между ними изолирует политику от механики. Строгий режим бой, первое расхождение
/// которого — решение ИИ (цель, защита, бегство, предмет), пропускает с причиной «политика ИИ».
///
/// Источник бросков — журнал, а не EngineRng.Fit: без принудительного автобоя поток UI (viewer,
/// в corpus/20260906-manual tid 34268) берёт rng(16) из того же глобального генератора между
/// ходами (seq 53013/53016, номера n сплошные с боевыми), и предсказать эти броски модель не
/// может. Поэтому модели скармливаются только броски боевого потока (tid события battle_start)
/// по порядку n, с проверкой диапазона каждого броска; отдельно проверяется, что генератор один и
/// цепочка целиком лежит на одной траектории: Fit по ВСЕМ броскам первого боя файла, дальше —
/// продолжение той же траектории через промежуток между боями (EngineRng.FindAhead; серия
/// «одна игра — много боёв» на 50,7 тыс. вызовов между боями лимит Fit выедала за 2–3 боя) —
/// и что модель израсходовала ровно столько боевых бросков, сколько их в журнале.
///
/// Файлы-дубликаты по sha256 считаются один раз; всё, что сверить нельзя, — пропуск с причиной, и причина обязана быть из
/// списка честных (<see cref="KnownSkips"/>, тест <see cref="C2_SkipsOnlyForKnownReasons"/>): журнал оборван, автобой зонда
/// (строгий), загрузка карты без своих сохранений и цепочка модификаторов, которую снимки не разводят, бегство ИИ стороны без
/// сохранения (строгий), очередь, перестроенная посреди раунда (строгий), превращение в журнале без attack_hit, политика ИИ.
/// Окна хода и что из них берётся — <see cref="Recorded"/>. На каждом turn_after журнального режима сверяются HP всех юнитов,
/// на первом turn_before хода — паралич. Полный список строк обоих режимов пишется в каталог из <c>D2_C2_DUMP</c>, если задан.</summary>
[Trait("Category", "Corpus")]
public partial class C2HarnessTests(ITestOutputHelper output)
{
    /// <summary>Одна запись хода журнала: бросок точности (событие miss) или одно лечение. Target
    /// пуст у промаха первой атаки (событие dmg не пишется, а miss цели не несёт).</summary>
    private sealed record Expected(string Unit, string Target, bool Miss, int Damage, int TargetHpAfter, int Attack = 1);

    /// <summary>Удар с броском точности, лечение, действие без броска (усиление, обереги, дар атаки, призыв, двойник —
    /// видно только событием attack_hit).</summary>
    private enum ActionKind { Attack, Heal, NoRoll, Item }

    /// <summary>Одно действие журнала (один battleTurn с ударом или лечением): событие, на котором
    /// модель его выполняет (первый miss окна либо battle_turn/battle_units окна лечения), и записи.</summary>
    private sealed record RecordedAction(long TriggerSeq, string Unit, ActionKind Kind, List<Expected> Hits, bool Human = false,
                                         string? Target = null, int ItemIndex = -1, int Slot = -1);

    /// <summary>Решение хода по журналу: кто, решал ли человек (<see cref="HumanInput"/>), что выбрано (null — журнал не
    /// показывает) и цель удара по одному (null — не видна или площадной).</summary>
    private sealed record JournalTurn(string Unit, bool Human, TurnChoiceKind? Choice, string? Target, int ItemIndex = -1, int Slot = -1);

    /// <summary>Policy — бои, пропущенные строгим режимом из-за политики ИИ (защита, цель), Unsupported —
    /// из-за класса атаки вне Ф4 (Fear): отдельно от прочих пропусков — это не ошибка очереди и не
    /// порча журнала.</summary>
    private sealed record Measurement(int Files, int Duplicates, int Used, int Ok, int Policy, int Unsupported,
                                      int ChainUnverified,
                                      List<string> Skipped, List<string> ChainWhy, List<string> Report)
    {
        /// <summary>Решений хода человека, взятых строгим режимом из журнала (<see cref="HumanChoices"/>).</summary>
        public int HumanTurns { get; init; }

        public string Summary => $"боёв воспроизведено {Ok}/{Used} (пропущено {Skipped.Count}, из них политика ИИ {Policy}, " +
                                 $"не реализовано в Ф4 {Unsupported}; файлов {Files}, дублей по sha256 {Duplicates}); " +
                                 $"траектория генератора подтверждена у {Used + Skipped.Count - ChainUnverified} боёв из {Used + Skipped.Count}" +
                                 (HumanTurns > 0 ? $"; решений человека из журнала {HumanTurns}" : "");
    }

    /// <summary>Разбор журнала боя: действия (Actions), причина непригодности и число ходов-защит.</summary>
    private sealed record RecordedTurns(List<RecordedAction> Actions, string? Unverifiable, int Defends, List<JournalTurn> Turns)
    {
        public List<Expected> Hits => Actions.SelectMany(a => a.Hits).ToList();

        /// <summary>Действия без броска по порядку: кто и по кому (null — цель не юнит, у призыва — клетка).</summary>
        public List<(string Unit, string? Target)> NoRolls =>
            Actions.Where(a => a.Kind == ActionKind.NoRoll).Select(a => (a.Unit, a.Target)).ToList();
    }

    /// <summary>Броски боевого потока журнала по порядку n. В отличие от ScriptedRng проверяет
    /// диапазон: модель и движок обязаны просить бросок одного max в одном месте потока, иначе
    /// «правильное» r ушло бы на чужой вопрос и расхождение всплыло бы позже или никогда.</summary>
    private sealed class JournalRng(IReadOnlyList<(int Max, int R)> draws) : IRng
    {
        public int Used { get; private set; }

        public int Next(int maxValue)
        {
            if (maxValue == 0) return 0;
            if (Used == draws.Count) throw new InvalidOperationException($"боевые броски кончились после {Used}, модель просит rng({maxValue})");
            var (max, r) = draws[Used++];
            if (max != maxValue) throw new InvalidOperationException($"бросок {Used}: модель просит rng({maxValue}), в журнале rng({max})");
            return r;
        }
    }

    /// <summary>Расхождение, найденное самим прогоном (паралич, HP на turn_after) — не бросками.</summary>
    private sealed class ReplayMismatch(string message) : Exception(message);

    // Fit перебирает 250 × 200 000 шагов на бой — считаем по разу на режим, не по разу на тест,
    // а состояние генератора первого боя файла кешируется между режимами.
    private static readonly Lazy<Measurement> Strict = new(() => Measure(journalActors: false));
    private static readonly Lazy<Measurement> Journal = new(() => Measure(journalActors: true));
    private static readonly Dictionary<string, EngineRng?> FitCache = [];

    [SkippableFact] public void C2_ReportsReproductionRate()
    {
        var m = Strict.Value;
        Print("С2 строгий (BattleSim.Run)", "strict", m);
        Skip.If(m.Used == 0, "нет пригодных боёв — нужен журнал без принудительного автобоя (start.autobattle=false) с battle_end");
        Assert.True(m.Ok == m.Used, $"строгий С2 {m.Ok}/{m.Used}: " + string.Join("; ", m.Report.Take(5)));
    }

    [SkippableFact] public void C2_AtLeast99PercentOfBattlesReproduce()
    {
        var m = Strict.Value;
        Skip.If(m.Used < 10, $"боёв слишком мало для оценки: {m.Used}");
        output.WriteLine($"С2 = {100.0 * m.Ok / m.Used:F1}% ({m.Ok}/{m.Used})");
        // Латиницей: Console идёт через кодовую страницу OEM, кириллица там нечитаема (см. Print).
        Console.WriteLine($"[C2 strict] {100.0 * m.Ok / m.Used:F1}% ({m.Ok}/{m.Used}), threshold 99%");
        Assert.True(m.Ok >= m.Used * 0.99, $"С2 = {100.0 * m.Ok / m.Used:F1}% ({m.Ok}/{m.Used}), порог 99 %");
    }

    /// <summary>Диагностика и регресс-барьер: всё, кроме политики ИИ, обязано сходиться в
    /// каждом пригодном бою, включая журналы под принудительным автобоем (на корпусе 06.09 — 5/5).</summary>
    [SkippableFact] public void C2_WithJournalActors_EveryUsableBattleReproduces()
    {
        var m = Journal.Value;
        Print("С2 по журнальной очереди (ходящий из журнала)", "journal-actors", m);
        Skip.If(m.Used == 0, "нет пригодных боёв");
        Assert.True(m.Ok == m.Used, $"регресс: по журнальной очереди сошлось {m.Ok}/{m.Used}");
    }

    /// <summary>Честные причины пропуска (подстрока причины): бой нельзя сверить по данным журнала, а не по слабости модели.</summary>
    private static readonly string[] KnownSkips =
    [
        "нет battle_end",                                  // журнал оборван посреди боя (перезагрузка, выход)
        "принудительный автобой зонда",                    // строгий: очередь искажена зондом
        "загрузка карты без своих сохранений",             // цепочек модификаторов взять неоткуда
        "цепочка модификаторов юнита",                     // снимки и журнал не разводят цепочку на момент боя
        "сторона без сохранения рядом с журналом",         // строгий: бегство ИИ без приказа, расы и остатка хода
        "построена заново посреди раунда",                 // строгий: правило движка не установлено
        "без attack_hit",                                  // превращение в журнале старой схемы
        "политика ИИ:",                                    // строгий: решение ИИ разошлось (считается отдельно)
    ];

    /// <summary>Регресс-барьер пропусков: в оба режима бой уходит только по честной причине (<see cref="KnownSkips"/>) — новая
    /// причина (окно, которого разбор не понимает, отказ загрузки) роняет тест, а не тихо уменьшает число сверенных боёв.</summary>
    [SkippableFact] public void C2_SkipsOnlyForKnownReasons()
    {
        var odd = Strict.Value.Skipped.Concat(Journal.Value.Skipped)
                        .Where(s => !KnownSkips.Any(k => s.Contains(k, StringComparison.Ordinal))).ToList();
        Assert.True(odd.Count == 0, "пропуски без честной причины: " + string.Join("; ", odd.Take(5)));
    }

    /// <summary>Печать сводки — ДВАЖДЫ, и это не описка.
    /// <para>Подробности (по бою на строку, пропуски с причиной) идут через
    /// <see cref="ITestOutputHelper"/>: у <b>прошедшего</b> теста консольный логгер VSTest их
    /// показывает только при <c>verbosity=detailed</c>. Из-за этого 08.09.2026 зелёный С2 был
    /// принят за пропуск («отчитался за 891 мс без сверок»): вывода не было видно, а «Duration»
    /// сводной строки — время последнего теста, а не тяжёлой работы из статического
    /// <see cref="Lazy{T}"/>. Поэтому одна строка — счётчик сверенных боёв — печатается ещё и в
    /// <see cref="Console"/>: её VSTest показывает всегда, и спутать зелёный С2 с пропуском больше
    /// нельзя. Страховка <c>Skip.If(m.Used == 0)</c> при этом остаётся: она честно превращает
    /// отсутствие данных в пропуск, а пропуск виден в сводке отдельной цифрой.</para>
    /// <para>Консольная строка — <b>только латиницей</b> (<paramref name="tag"/>): её печатает не
    /// xunit, а <see cref="Console"/>, и на русской Windows она уходит через кодовую страницу OEM.
    /// Первая редакция печатала туда русскую сводку и выглядела в выводе как каша — счётчик, который
    /// нельзя прочесть, ничем не лучше счётчика, которого нет.</para></summary>
    private void Print(string title, string tag, Measurement m)
    {
        output.WriteLine(title);
        // Строки с расхождением — все и первыми: 253/256 журнального С2 прятали свои три боя за Take(200).
        bool Diverged(string line) => line.Contains("расхождение", StringComparison.Ordinal);
        foreach (var line in m.Report.Where(Diverged).Concat(m.Report.Where(l => !Diverged(l)).Take(200)))
            output.WriteLine("  " + line);
        foreach (var line in m.Skipped.Take(200)) output.WriteLine("  пропуск: " + line);
        output.WriteLine(m.Summary);
        if (Environment.GetEnvironmentVariable("D2_C2_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(Path.Combine(dump, $"c2-{tag}.txt"), m.Report.Concat(m.Skipped.Select(l => "пропуск: " + l)).Append(m.Summary));
        Console.WriteLine($"[C2 {tag}] battles reproduced {m.Ok}/{m.Used}, skipped {m.Skipped.Count} " +
                          $"(AI policy {m.Policy}), files {m.Files}, duplicates {m.Duplicates}");
    }

    private static Measurement Measure(bool journalActors)
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        Skip.IfNot(CorpusFiles.Available, "нет корпуса");
        var rules = RulesLoader.Load(GamePaths.Globals);
        var spells = MapEffectTables.Load(GamePaths.Globals);   // заклинания и зелья карты до боя (spell_cast, potion_use)
        var (files, duplicates) = JournalSet.Distinct(CorpusFiles.Journals());
        int used = 0, ok = 0, policy = 0, unsupported = 0, chainUnverified = 0, humanTurns = 0;
        var chainWhy = new List<string>();
        var skipped = new List<string>();
        var report = new List<string>();

        foreach (var file in files)
        {
            bool forced = JournalCache.Read(file).OfType<StartEvent>().FirstOrDefault()?.Autobattle ?? false;
            // Сохранения рядом с журналом дают цепочку модификаторов юнита (способности лидера,
            // надетые предметы). Без неё С2 гоняет прокачанного героя новобранцем и расходится по
            // урону там, где формулы верны, — см. SaveSnapshots.
            var saves = SaveSnapshots.Of(file);
            var marks = saves.Count == 0 ? [] : RouteCasts.TurnMarks(file);
            EngineRng? chain = null;   // траектория генератора после последнего броска предыдущего боя файла
            long chainN = 0;
            long chainSeq = 0;         // seq последнего броска предыдущего боя: пересев позже него — новая точка
            // Схема 6.2: пересевы и загрузки состояния генератора. Строк мало — берутся фильтром по строке.
            var seeds = JournalCache.Read(file, "rng_seed").OfType<RngSeedEvent>().OrderBy(e => e.Seq).ToList();
            foreach (var battle in JournalCache.Battles(file))
            {
                string name = $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)} [{battle.Start.Seq}]";
                var all = battle.Events.OfType<RngEvent>().Where(e => !e.Std).OrderBy(e => e.N).ToList();
                var chainBefore = chain;
                chain = null;
                // Траектория генератора — ОТДЕЛЬНОЕ наблюдение, а не условие сверки боя: модель берёт
                // броски из журнала (JournalRng), поэтому неподтверждённая траектория не мешает
                // проверять правила. Рвётся она на перезагрузке карты (серия «одна игра — много
                // боёв»): reseed(timeGetTime) ставит новый индекс, а таблица к этому моменту уже
                // мутировала предыдущим боем, и Fit — перебор индексов по ИСХОДНОЙ таблице — такую
                // точку не находит по построению. Первый бой процесса подтверждается всегда.
                var seed = all.Count > 0 ? SeedBefore(seeds, chainSeq, all[0].Seq) : null;
                if (all.Count > 0 && Continue(file, battle, all, chainBefore, chainN, seed, out chain, out string? why) is false)
                { chainUnverified++; chainWhy.Add($"{name}: {why}"); }
                if (all.Count > 0) (chainN, chainSeq) = (all[^1].N, all[^1].Seq);

                if (!journalActors && forced)
                { skipped.Add($"{name}: принудительный автобой зонда (start.autobattle=true), очередь искажена — герой не ходит (task-5-report.md)"); continue; }
                if (battle.End is null) { skipped.Add($"{name}: нет battle_end"); continue; }
                int turn = saves.Count == 0 ? 0 : RouteCasts.TurnAt(marks, battle.Start.Seq);
                if (!CorpusBattleLoader.TryLoad(battle, rules, out var state, out var reason,
                                                saves: saves, turn: turn, items: spells.Items, spells: spells))
                { skipped.Add($"{name}: {reason}"); continue; }
                var rec = Recorded(battle, state);
                var expected = rec.Hits;
                if (rec.Unverifiable is not null) { skipped.Add($"{name}: {rec.Unverifiable}"); continue; }
                if (expected.Count == 0) { skipped.Add($"{name}: в журнале нет ударов"); continue; }

                var own = all.Where(e => e.Tid == battle.Start.Tid).Select(e => ((int)e.Max, (int)e.R)).ToList();
                if (own.Count == 0) { skipped.Add($"{name}: нет бросков rng боевого потока"); continue; }

                var rng = new JournalRng(own);
                // Id призванных — как у движка (счётчик карты): новые id журнала в порядке появления.
                var fresh = new Queue<string>(battle.Events.SelectMany(UnitsOf).Select(u => u.Id).Where(id => state.Unit(id) is null)
                                                  .Distinct(StringComparer.OrdinalIgnoreCase));
                state.NewUnitId = () => fresh.Count > 0 ? fresh.Dequeue() : $"нет-в-журнале-{state.Units.Count + state.Gone.Count}";
                var sim = new BattleSim(state, rng, rules.Constants);
                humanTurns += journalActors ? 0 : rec.Turns.Count(t => t.Human && t.Choice is not null);
                int matched;
                string? diff;
                bool isPolicy = false;
                try
                {
                    (matched, diff, isPolicy) = journalActors ? ReplayAndCompare(battle, state, sim, rec)
                        : Compare(expected, sim.Run(choose: HumanChoices(rec, state)), state);
                    if (diff is null && !journalActors) (diff, isPolicy) = CompareNoRolls(rec, state);
                }
                catch (NotSupportedException ex)
                {
                    unsupported++;
                    skipped.Add($"{name}: {ex.Message}; сошлось {Compare(expected, sim.Log, state).matched} ходов до него (из {expected.Count})");
                    continue;
                }
                catch (ReplayMismatch ex) { (matched, diff) = (Compare(expected, sim.Log, state).matched, ex.Message); }
                catch (ArgumentException ex) { (matched, diff) = (Compare(expected, sim.Log, state).matched, ex.Message); }   // негодный ход журнала
                catch (InvalidOperationException ex)   // JournalRng: поток бросков разошёлся; Log — что успело сойтись
                {
                    var cmp = Compare(expected, sim.Log, state);
                    (matched, diff) = (cmp.matched, cmp.diff is null ? ex.Message : $"{cmp.diff} (бросок: {ex.Message})");
                }

                // Решения ходов (защита, бегство, ожидание, удар, предмет) против журнала: расхождение решения раньше расхождения
                // записей (по раунду) — его и показываем; бегство ИИ без сведений о стороне — пропуск с причиной.
                if (!journalActors && CompareChoices(rec, sim, state) is { } choice
                    && (diff is null || sim.Log.Count <= matched || choice.Round <= sim.Log[matched].Round))
                {
                    if (choice.Unknowable is not null) { skipped.Add($"{name}: {choice.Unknowable}; строго сошлось {matched} ходов (из {expected.Count})"); continue; }
                    (diff, isPolicy) = (choice.Diff, choice.Policy);
                }
                if (!journalActors && diff is not null && MidRoundRebuild(battle) is { } rebuilt)
                { skipped.Add($"{name}: {rebuilt}; строго сошлось {matched} ходов (из {expected.Count})"); continue; }
                if (!journalActors && isPolicy)
                {
                    policy++;
                    skipped.Add($"{name}: политика ИИ: {diff}; строго сошлось {matched} ходов (из {expected.Count})");
                    continue;
                }
                used++;
                diff ??= rng.Used == own.Count ? null : $"бросков боевого потока: модель {rng.Used}, в журнале {own.Count}";
                if (diff is null && !string.Equals(sim.Winner, battle.End.Winner, StringComparison.OrdinalIgnoreCase))
                    diff = $"победитель {sim.Winner ?? "нет"}, в журнале {battle.End.Winner}";
                if (diff is null) ok++;
                report.Add($"{name}: ходов сошлось {matched}/{expected.Count}" + (rec.Defends > 0 ? $", защит {rec.Defends}" : "") +
                           $", бросков {rng.Used}/{own.Count}" + (diff is null ? "" : $", первое расхождение: {diff}"));
            }
        }
        return new Measurement(files.Count, duplicates, used, ok, policy, unsupported, chainUnverified, skipped, chainWhy, report)
               { HumanTurns = humanTurns };
    }

    /// <summary>Генератор один и вся цепочка бросков боя лежит на одной траектории: если после предыдущего боя
    /// журнал записал пересев (<paramref name="seed"/>, схема 6.2), состояние берётся из него и продвигается
    /// FindAhead на разницу n; иначе первый бой файла — EngineRng.Fit (кеш между режимами), следующие —
    /// продолжение траектории предыдущего боя через промежуток между боями (разница n минус один — известные
    /// вызовы; сверх того до 2 млн шагов). На выходе <paramref name="after"/> — состояние после последнего
    /// броска боя.</summary>
    private static bool Continue(string file, D2.Data.Corpus.Battle battle, List<RngEvent> all, EngineRng? chain, long chainN,
                                 RngSeedEvent? seed, out EngineRng? after, out string? why)
    {
        var draws = all.Select(e => ((int)e.Max, (int)e.R)).ToList();
        after = null; why = null;
        EngineRng? before;
        if (seed is not null)
        {
            before = StartFromSeed(seed, all);
            if (before is null) { why = $"от пересева seq={seed.Seq} (n={seed.N}) броски боя не найдены за {all[0].N - seed.N - 1} вызовов + 2 млн шагов"; return false; }
        }
        else if (chain is null)
        {
            string key = $"{file}#{battle.Start.Seq}";
            lock (FitCache)
            {
                if (!FitCache.TryGetValue(key, out before)) FitCache[key] = before = FitDiskCache.Fit(file, battle.Start.Seq, draws);
                before = before?.Clone();
            }
            if (before is null) { why = $"состояние генератора не подобрано по всем {draws.Count} броскам"; return false; }
        }
        else
        {
            long known = all[0].N - chainN - 1;
            before = chain.FindAhead(draws, known, known + 2_000_000);
            if (before is null) { why = $"траектория генератора не продолжена через промежуток между боями ({known} вызовов, до 2 млн шагов)"; return false; }
        }
        if (!before.Replay(draws)) { why = "траектория генератора не воспроизводит броски боя"; return false; }
        after = before;
        return true;
    }

    /// <summary>Последний пересев строго между <paramref name="afterSeq"/> (последний бросок предыдущего боя, 0 — боя
    /// не было) и <paramref name="beforeSeq"/> (первый бросок боя); null — пересева в промежутке нет.</summary>
    internal static RngSeedEvent? SeedBefore(IReadOnlyList<RngSeedEvent> seeds, long afterSeq, long beforeSeq) =>
        seeds.LastOrDefault(s => s.Seq > afterSeq && s.Seq < beforeSeq);

    /// <summary>Состояние ПЕРЕД первым броском боя от пересева: таблица и индекс строки <c>rng_seed</c>, затем
    /// FindAhead — известных вызовов <c>n₀ − n − 1</c> (первый вызов после пересева имеет номер n + 1), сверх того
    /// до 2 млн шагов. null — броски не найдены.</summary>
    internal static EngineRng? StartFromSeed(RngSeedEvent seed, IReadOnlyList<RngEvent> all)
    {
        var draws = all.Select(e => ((int)e.Max, (int)e.R)).ToList();
        long known = all[0].N - seed.N - 1;
        return EngineRng.FromSeed(seed.Table, (int)seed.IndexAfter).FindAhead(draws, known, known + 2_000_000);
    }

    /// <summary>Строгий режим: решения человека берутся из журнала (<see cref="JournalTurn.Human"/>) — модель политики
    /// человека нет и быть не может. Каждому юниту — его решения журнала по порядку, по одному на действие модели; ход ИИ
    /// (и решение человека, которого журнал не показывает) — null, решает BattleAi. Разойдётся очередь — разойдётся и
    /// счёт решений, и это всплывёт расхождением дальше.</summary>
    private static Func<BattleUnit, TurnChoice?>? HumanChoices(RecordedTurns rec, BattleState state)
    {
        if (!rec.Turns.Any(t => t.Human)) return null;
        var queues = rec.Turns.GroupBy(t => t.Unit, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => new Queue<JournalTurn>(g), StringComparer.OrdinalIgnoreCase);
        return unit =>
        {
            if (!queues.TryGetValue(unit.Id, out var q) || q.Count == 0) return null;
            var t = q.Dequeue();
            if (!t.Human || t.Choice is not { } kind) return null;
            var target = t.Target is null ? null : state.Unit(t.Target);
            if (kind == TurnChoiceKind.UseItem) return new TurnChoice(kind, target, t.ItemIndex);
            if (t.Slot >= 0) return new TurnChoice(kind, unit, SummonSlot: t.Slot);
            // Удар по одному без видимой цели — решает ИИ (его броски ничьих тогда лишние, расхождение покажет поток бросков).
            if (kind == TurnChoiceKind.Attack && target is null && unit.Attack.Reach != AttackType.ReachAll) return null;
            return new TurnChoice(kind, target);
        };
    }

    /// <summary>Первое расхождение решений хода (<see cref="BattleSim.Choices"/> против <see cref="RecordedTurns.Turns"/>): раунд модели,
    /// текст, решение ли это ИИ (тот же ходящий, другое решение) и причина непроверяемости — бегство ИИ стороны, о которой без
    /// сохранения ничего не известно (<see cref="BattleSide.Known"/>: приказ, раса, остаток хода — AiRetreat; модель такой стороне
    /// бежать не даёт, 20260906-b, 20260906-c — журналы без сохранений).</summary>
    private sealed record ChoiceDiff(int Round, string Diff, bool Policy, string? Unknowable);

    private static ChoiceDiff? CompareChoices(RecordedTurns rec, BattleSim sim, BattleState state)
    {
        var model = sim.Choices;
        var journal = rec.Turns;
        for (int i = 0; i < Math.Min(model.Count, journal.Count); i++)
        {
            var (m, j) = (model[i], journal[i]);
            if (!Same(m.Unit, j.Unit))
                return new ChoiceDiff(m.Round, $"решение {i}: ходит {m.Unit} ({m.Kind}), в журнале {j.Unit} ({j.Choice})", false, null);
            if (m.Kind == j.Choice) continue;
            if (j.Choice == TurnChoiceKind.Retreat && state.Unit(j.Unit) is { } u && !state.SideOf(u).Known)
                return new ChoiceDiff(m.Round, "", false,
                    $"бегство ИИ {j.Unit} в раунде {m.Round}: сторона без сохранения рядом с журналом — приказа, расы и остатка хода модель не знает");
            return new ChoiceDiff(m.Round, $"решение {i} ({m.Unit}, раунд {m.Round}): модель {m.Kind}, в журнале {j.Choice}", true, null);
        }
        return null;
    }

    /// <summary>Действия без броска (<see cref="ActionKind.NoRoll"/>) против модели: кто и по кому — первые атаки классов
    /// без броска точности (<see cref="BattleState.Actions"/>, кроме лечения — оно в записях хода). Цель призыва — клетка, у
    /// модели — сам призыватель, поэтому у призыва сверяется только ходящий. Другая цель — решение ИИ (policy).</summary>
    private static (string? diff, bool policy) CompareNoRolls(RecordedTurns rec, BattleState state)
    {
        var expected = rec.NoRolls;
        if (expected.Count == 0) return (null, false);
        var actual = state.Actions.Where(a => a.Attack == 1 && !Attacks.RollsAccuracy(a.Class, false) && a.Class != AttackClass.Heal).ToList();
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            var (e, a) = (expected[i], actual[i]);
            if (!Same(e.Unit, a.Unit)) return ($"действие без броска {i}: ходит {a.Unit} ({a.Class}), в журнале {e.Unit}", false);
            if (e.Target is not null && a.Class != AttackClass.Summon && !Same(e.Target, a.Target))
                return ($"действие без броска {i} ({e.Unit}, {a.Class}): цель {a.Target}, в журнале {e.Target}", true);
        }
        return expected.Count == actual.Count ? (null, false)
            : ($"действий без броска: модель {actual.Count}, в журнале {expected.Count}", false);
    }

    /// <summary>Журнальный режим: ходящий из журнала (<see cref="ReplayWithJournalActors"/>), конец боя
    /// сверяется явно — после какой записи модель объявила победителя против числа записей журнала.</summary>
    private static (int matched, string? diff, bool policy) ReplayAndCompare(
        D2.Data.Corpus.Battle battle, BattleState state, BattleSim sim, RecordedTurns rec)
    {
        var expected = rec.Hits;
        int? endedAfter = ReplayWithJournalActors(battle, state, sim, rec);
        var (matched, diff, _) = Compare(expected, sim.Log, state);
        diff ??= endedAfter == expected.Count ? null : $"конец боя: победитель у модели после записи {endedAfter?.ToString() ?? "—"}, у движка после {expected.Count}";
        return (matched, diff, false);
    }

    /// <summary>Ходящий — из журнала, действие — на событии-триггере записанного действия (первый miss
    /// окна удара; battle_turn/battle_units окна лечения), цель одиночного удара/лечения — из журнала,
    /// площадной — модель (Attacks.Shuffle); очередь (и её броски) — на каждое событие round;
    /// turn_before — BeginTurn (снимает Defend, тикает DoT, снимает паралич) с проверкой, что модель
    /// считает юнита парализованным тогда же, когда журнал (бит 2/3); turn_after — защита по биту 31 и
    /// сверка HP всех юнитов. Возвращает число записей журнала, после которого модель объявила
    /// победителя (null — не объявила).</summary>
    private static int? ReplayWithJournalActors(D2.Data.Corpus.Battle battle, BattleState state, BattleSim sim, RecordedTurns rec)
    {
        var actions = rec.Actions.ToDictionary(a => a.TriggerSeq);
        int? endedAfter = null;
        int consumed = 0;
        BattleUnit Unit(string id) => state.Unit(id) ?? throw new NotSupportedException($"ходящий {id} не в состоянии боя");
        BattleUnit? actor = null;   // ходящий: последний turn_before
        foreach (var e in battle.Events)
        {
            switch (e)
            {
                case RoundEvent r:
                    // Последний ходящий раунда собрался бежать: переход раунда идёт внутри afterBattleTurn, и строка round (с
                    // броском очереди) приходит раньше его turn_after — решение видно уже в её снимке. У ждавшего от него зависит
                    // бит 7, а с ним — броски очереди (20261016-port-synth [581388] seq 584036: S143UN003a ждал, затем «Отступить»;
                    // в очереди раунда 2 три броска из четырёх).
                    if (actor is not null && r.Units.FirstOrDefault(x => Same(x.Id, actor.Id))?.Retreating == true
                        && actor is { Retreating: false, Retreated: false }) sim.Retreat(actor);
                    sim.BeginRound((int)r.Round);
                    break;
                case TurnEvent { After: false } t:
                {
                    var u = Unit(t.Unit);
                    actor = u;
                    // JournalUnit.Paralyzed — это биты 2|3, то есть паралич ИЛИ окаменение; у модели
                    // это два разных статуса (задача 5 Ф6), и сверять надо их объединение.
                    bool journalParalyzed = t.Units.FirstOrDefault(x => Same(x.Id, t.Unit))?.Paralyzed == true;
                    bool modelParalyzed = u.Paralyzed || u.Petrified;
                    if (u.TurnBegunRound != state.Round && journalParalyzed != modelParalyzed)
                        throw new ReplayMismatch($"паралич {t.Unit} в раунде {t.Round}: модель {modelParalyzed}, журнал {journalParalyzed}");
                    sim.BeginTurn(u);
                    break;
                }
                case TurnEvent { After: true } t:
                    if (Defended(t)) sim.Defend(Unit(t.Unit));
                    // «Ждать» — бит 4 flags у самого ходящего в его turn_after (BattleSim.Wait): от него зависит бит 7 при бегстве
                    // в отложенный ход (BattleUnit.RetreatingAfterWait) — 16.10, бои человека 20261016-port-synth.
                    if (Waited(t) && state.Unit(t.Unit) is { Waited: false } waiter) sim.Wait(waiter);
                    // Решение ИИ бежать — бит 29 у самого ходящего в его turn_after (BattleSim.Retreat);
                    // бит от Fear модель ставит сама, и тогда здесь ничего не делается.
                    if (Retreats(t) && state.Unit(t.Unit) is { Retreating: false, Retreated: false } fleeing) sim.Retreat(fleeing);
                    // afterBattleTurn снимает конечные BoostDamage/LowerDamage ровно при
                    // unitId != nextUnitId (A hooks.cpp:1786-1791) — журнал несёт обе личности.
                    // На корпусе 06.09 это no-op: ни одного буста и снижения в нём нет.
                    if (!Same(t.Unit, t.Next) && state.Unit(t.Unit) is { } acted) sim.EndTurn(acted);
                    foreach (var js in t.Units)
                        if (state.Unit(js.Id) is { } mu && mu.Hp != js.Hp)
                            throw new ReplayMismatch($"HP {js.Id} после хода {t.Unit} (раунд {t.Round}): модель {mu.Hp}, журнал {js.Hp}");
                    break;
            }
            if (endedAfter is null && sim.Finished) endedAfter = consumed;
            if (!actions.TryGetValue(e.Seq, out var action)) continue;
            var doer = Unit(action.Unit);
            if (action.Kind == ActionKind.Item)
            {
                sim.UseItem(doer, action.ItemIndex, action.Target is { } it ? Unit(it) : doer);
                if (endedAfter is null && sim.Finished) endedAfter = consumed;
                continue;
            }
            var single = action.Kind == ActionKind.NoRoll ? action.Target : action.Hits.FirstOrDefault(h => h.Target.Length > 0)?.Target;
            bool all = doer.Attack.Reach == AttackType.ReachAll;
            // Решение ИИ берётся всегда, кроме хода человека с известной целью: ничьи ИИ тратят rng(2) до ударов, а в ход
            // человека движок решения ИИ не принимает. Цель — из журнала, а где журнал её не показывает (промах без хука
            // immune, призыв в клетку) — выбор политики (BattleAi).
            if (action.Slot >= 0) state.SummonSlot = action.Slot;   // позиция призыва — журнала (у ИИ та же, что выбрал бы он)
            var decided = action.Human && (single is not null || all || action.Slot >= 0) ? null : sim.Decide(doer).Target;
            var target = single is not null ? Unit(single) : all ? null : decided;
            if (action.Kind != ActionKind.Heal || all || single is not null)
                sim.Act(state.Round, doer, target);
            consumed += action.Hits.Count;
            if (endedAfter is null && sim.Finished) endedAfter = consumed;
        }
        // Журнал кончился, а сторона уже была мертва: ходов после победы движок больше не дал —
        // значит бой закончился на последней записи (фаза лекарей, если и была, уже в consumed).
        if (endedAfter is null && sim.Winner is not null) endedAfter = consumed;
        return endedAfter;
    }

    /// <summary>turn_after ходящего с битом 31 у него самого — этот ход был защитой: движок снимает
    /// Defend в beforeBattleTurn самого юнита, удар его не ставит, ставит только CBatAttackDefend
    /// (corpus/20260906-a: seq 53113/53229/53324/53383 — стоит; 53452 → удар 53460 — нет).</summary>
    private static bool Defended(TurnEvent after) =>
        after.Units.FirstOrDefault(u => Same(u.Id, after.Unit))?.Defend == true;

    /// <summary>turn_after ходящего с битом 4 flags (<c>waited</c>) у него самого — этот ход был «Ждать».</summary>
    private static bool Waited(TurnEvent after) =>
        (after.Units.FirstOrDefault(u => Same(u.Id, after.Unit))?.Flags & 0x10) is > 0;

    /// <summary>turn_after ходящего с битом 29 у него самого — ходящий собрался бежать
    /// (<see cref="BattleSim.Retreat"/>): решение ИИ, как и защита.</summary>
    private static bool Retreats(TurnEvent after) =>
        after.Units.FirstOrDefault(u => Same(u.Id, after.Unit))?.Retreating == true;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>policy — первое расхождение есть решение ИИ, не механика: другая цель удара/лечения,
    /// либо не тот ходящий, когда одна из сторон — лечение (лекарь ИИ лечит здорового или не лечит).</summary>
    private static (int matched, string? diff, bool policy) Compare(List<Expected> expected, IReadOnlyList<TurnRecord> all, BattleState state)
    {
        // Запись второй атаки без броска и без полученного здоровья журнал не несёт (нет miss) — её и не сверяем.
        bool Journaled(TurnRecord r) => r.Attack != 2 || state.Unit(r.Unit)?.Attack2 is not { } a2
                                        || Attacks.RollsAccuracy(a2.Class, itemAttack: false) || a2.Class is AttackClass.Heal or AttackClass.Revive;
        var actual = all.Where(Journaled).ToList();
        bool IsHeal(string id) => state.Unit(id)?.Attack.Class == AttackClass.Heal;
        int n = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < n; i++)
        {
            var (e, a) = (expected[i], actual[i]);
            if (!Same(e.Unit, a.Unit)) return (i, $"ход {i}: ходит {a.Unit}, в журнале {e.Unit}", IsHeal(e.Unit) || IsHeal(a.Unit));
            if (e.Attack != a.Attack) return (i, $"ход {i} ({e.Unit}): атака {a.Attack}, в журнале {e.Attack}", false);
            if (e.Target.Length > 0 && !Same(e.Target, a.Target)) return (i, $"ход {i} ({e.Unit}): цель {a.Target}, в журнале {e.Target}", true);
            if (e.Miss != a.Miss) return (i, $"ход {i} ({e.Unit}): промах {a.Miss}, в журнале {e.Miss}", false);
            if (!e.Miss && e.Damage != a.Damage) return (i, $"ход {i} ({e.Unit}): урон {a.Damage}, в журнале {e.Damage}", false);
            if (e.TargetHpAfter >= 0 && e.TargetHpAfter != a.TargetHpAfter) return (i, $"ход {i} ({e.Unit}): HP цели {a.TargetHpAfter}, в журнале {e.TargetHpAfter}", false);
        }
        if (expected.Count == actual.Count) return (n, null, false);
        string extra = actual.Count > n ? actual[n].Unit : expected[n].Unit;
        return (n, $"длина: ходов {actual.Count}, в журнале {expected.Count}", IsHeal(extra));
    }
}
