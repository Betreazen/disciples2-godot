using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Rng;
using Xunit.Abstractions;

/// <summary>Харнесс очереди ходов уровня С1: правило очереди не угадывается, а измеряется.
///
/// <para>В журнале есть готовый эталон — поле <c>order</c> событий <c>battle_units</c> и
/// <c>turn_before</c>/<c>turn_after</c> (снимок <c>BattleMsgData::turnsOrder</c>, то есть
/// объявленная движком очередь). Событие <c>round</c> зонд пишет ДО оригинального вызова
/// <c>beforeBattleRound</c>, поэтому его собственный <c>order</c> — очередь ПРЕДЫДУЩЕГО раунда
/// (обычно пустая) и эталоном быть не может; берётся первый снимок очереди ПОСЛЕ построения.</para>
///
/// <para>Одно построение очереди = одно событие <c>round</c> плюс идущая сразу за ним серия
/// <c>rng(BAT_INIT)</c> (по броску на живого юнита). Раунд 1 движок строит дважды (см.
/// <see cref="BattleSim.FirstRoundQueueBuilds"/>) — сверяется последнее построение перед снимком,
/// первое движок выбрасывает.</para>
///
/// <para>Сверяется ровно то, что можно сверить: состояние берётся из снимка <c>units</c> самого
/// события <c>round</c> (HP, отступление), броски — из журнала, и результат сравнивается с
/// <c>order</c>. Всё, что сверить нельзя (нет бросков, число бросков не сошлось с числом живых,
/// снимок очереди усечён), уходит в
/// пропуск С ПРИЧИНОЙ.</para>
///
/// <para>Таблица гипотез ничьей — отдельное измерение, ради которого харнесс и заведён: на равных
/// значениях <c>инициатива + бросок</c> порядок решает правило ничьей, и корпус обязан сказать,
/// какое именно. Инициатива и раздача бросков здесь повторены (две строки) намеренно: без своих
/// величин таблица гипотез не строится. Гипотеза «сторона+слот» — та же, что в
/// <see cref="TurnOrder"/>, и её расхождение с ним самим считается отдельным счётчиком: разойдись
/// они — сломан харнесс, а не движок.</para></summary>
[Trait("Category", "Corpus")]
public class TurnOrderHarnessTests(ITestOutputHelper output)
{
    /// <summary><c>BattleStatus::LowerInitiative</c> — бит 13 поля <c>st</c> (battlemsgdata.h).</summary>
    private const int LowerInitiativeBit = 13;

    /// <summary>Превращение: <c>Transform</c> (22), <c>TransformSelf</c> (24),
    /// <c>TransformDoppelganger</c> (25), <c>TransformDrainLevel</c> (26) — battlemsgdata.h.
    /// У превращённого юнита инициатива берётся у ТЕКУЩЕЙ формы, а журнал несёт только исходный тип
    /// (<c>battle_units</c> опознаёт юнита по objectMap до превращения), поэтому такое построение
    /// очереди не сверяется, а честно пропускается с причиной. Ровно эти шесть построений и были
    /// единственным расхождением харнесса до того, как пропуск появился.</summary>
    private const long TransformMask = (1L << 22) | (1L << 24) | (1L << 25) | (1L << 26);

    /// <summary>Юнит очереди: значение сортировки (инициатива + бросок) и индекс в массиве
    /// <c>unitsInfo</c> (он же порядок <see cref="BattleState.Units"/>).</summary>
    private sealed record Row(BattleUnit Unit, int Value, int Index);

    private sealed record Hypothesis(string Name, Func<IEnumerable<Row>, IOrderedEnumerable<Row>> Order);

    /// <summary>Пять правил ничьей. Первое — нынешнее правило <see cref="TurnOrder"/>. OrderBy в LINQ
    /// устойчив, поэтому «порядок unitsInfo» — это сортировка вообще без правила ничьей.</summary>
    private static readonly Hypothesis[] Hypotheses =
    [
        new("сторона+слот", rs => rs.OrderByDescending(r => r.Value)
                                    .ThenByDescending(r => r.Unit.Attacker).ThenBy(r => r.Unit.Slot)),
        new("слот+сторона", rs => rs.OrderByDescending(r => r.Value)
                                    .ThenBy(r => r.Unit.Slot).ThenByDescending(r => r.Unit.Attacker)),
        new("id по возрастанию", rs => rs.OrderByDescending(r => r.Value)
                                         .ThenBy(r => r.Unit.Id, StringComparer.OrdinalIgnoreCase)),
        new("порядок unitsInfo", rs => rs.OrderByDescending(r => r.Value).ThenBy(r => r.Index)),
        new("обратный unitsInfo", rs => rs.OrderByDescending(r => r.Value).ThenByDescending(r => r.Index)),
    ];

    /// <summary>Гипотеза, реализованная в <see cref="TurnOrder"/>: харнесс обязан давать на ней тот же
    /// ответ, что и сама модель, иначе сломан харнесс.</summary>
    private const string ProductionRule = "порядок unitsInfo";

    /// <summary>Одно построение очереди: событие раунда, его броски и первый снимок очереди после него.</summary>
    private sealed record QueueBuild(RoundEvent Round, IReadOnlyList<int> Draws, IReadOnlyList<string> Observed);

    [SkippableFact] public void EveryRecordedQueue_MatchesOurs()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        Skip.IfNot(CorpusFiles.Available, "нет корпуса");
        var rules = RulesLoader.Load(GamePaths.Globals);
        var (files, duplicates) = JournalSet.Distinct(CorpusFiles.Journals());
        var stat = new Stats();
        int battlesTotal = 0, battlesUsed = 0, ties = 0, tiePairs = 0;

        foreach (var file in files)
        {
            // Инициативу правит цепочка модификаторов (Gmodif TYPE 9), а её пишет не журнал, а
            // сохранение рядом с ним — см. SaveSnapshots.
            var saves = SaveSnapshots.Of(file);
            var marks = saves.Count == 0 ? [] : RouteCasts.TurnMarks(file);
            foreach (var battle in JournalCache.Battles(file))
            {
                battlesTotal++;
                int turn = saves.Count == 0 ? 0 : RouteCasts.TurnAt(marks, battle.Start.Seq);
                if (!CorpusBattleLoader.TryLoad(battle, rules, out var state, out string reason,
                                                saves: saves, turn: turn))
                { stat.Skip("бой", reason); continue; }
                bool used = false;
                foreach (var build in Builds(battle, rules.Constants.BatInit))
                    used |= Check($"{Path.GetFileName(file)} [{battle.Start.Seq}] раунд {build.Round.Round}",
                                  build, state, rules, stat, ref ties, ref tiePairs);
                if (used) battlesUsed++;
                else stat.Skip("бой", "ни одного сверяемого построения очереди");
            }
        }

        output.WriteLine($"файлов {files.Count}, дублей по sha256 {duplicates}; боёв {battlesTotal}, пригодных {battlesUsed}");
        output.WriteLine($"очередей проверено {stat.Checked}, совпало {stat.Ok("очередь")}, разошлось {stat.Bad("очередь")}, " +
                         $"пропущено {stat.Skipped}; из проверенных с ничьёй {ties} (ничейных пар {tiePairs})");
        foreach (var line in stat.Lines()) output.WriteLine(line);
        foreach (var m in stat.Mismatches.Take(15)) output.WriteLine("  " + m);
        Console.WriteLine($"[TurnOrder harness] queues checked {stat.Checked}, matched {stat.Ok("очередь")}, " +
                          $"mismatched {stat.Bad("очередь")}, skipped {stat.Skipped}, with ties {ties}");

        Assert.True(stat.Checked > 0, "ни одной сверки очереди — измерять нечего");
        Assert.Equal(0, stat.Bad("порядок unitsInfo стабилен"));
        Assert.Equal(0, stat.Bad("харнесс = TurnOrder"));
        Assert.Empty(stat.Mismatches);
    }

    /// <summary>Бит 7 поля <c>flags</c> снимка — <see cref="BattleUnit.RetreatingAfterWait"/>.</summary>
    private const long RetreatingAfterWaitFlag = 0x80;

    /// <summary>Одно построение: применяет снимок раунда к состоянию, строит очередь моделью и
    /// сверяет с журналом; заодно считает таблицу гипотез ничьей. false — сверить нельзя, причина
    /// уже записана.</summary>
    private static bool Check(string name, QueueBuild build, BattleState state, GameRules rules, Stats stat,
                              ref int ties, ref int tiePairs)
    {
        var byId = build.Round.Units.Where(u => !string.Equals(u.Id, "INVALID-ID", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var unit in state.Units)
        {
            if (!byId.TryGetValue(unit.Id, out var snapshot))
            { stat.Skip("очередь", $"юнит {unit.Id} отсутствует в снимке раунда"); return false; }
            // Уровень снижения по биту восстановим: построение очереди 0x62457c уровня не читает — при бите 13
            // вычитает BAT_LOWER_INI % суммы (0x5838c0), то есть это уровень 1. Состояние общее для построений
            // боя, поэтому ставится явно в обе стороны (Build гасит конечный статус, 0x624752).
            unit.LowerInitiativeLevel = (int)(snapshot.St >> LowerInitiativeBit & 1);
            unit.LowerInitiativeLong = (snapshot.St >> (LowerInitiativeBit + 1) & 1) != 0;
            if (unit.LowerInitiativeLevel > 0) stat.Count("LowerInitiative в построении", true);
            if ((snapshot.St & TransformMask) != 0)
            { stat.Skip("очередь", "юнит превращён: инициатива у текущей формы, а журнал несёт исходный тип"); return false; }
            unit.Hp = snapshot.Dead ? 0 : (int)snapshot.Hp;
            unit.Retreated = (snapshot.St >> 28 & 1) != 0;
            // Бит 7 flags — «Бежать» в отложенный ход (BattleUnit.RetreatingAfterWait): такой юнит построение пропускает и броска
            // не тратит. 16.10 (20261016-port-synth passive-001, бои A1 и WR) — два построения, прежде пропуском «бросков меньше».
            unit.RetreatingAfterWait = (snapshot.Flags & RetreatingAfterWaitFlag) != 0;
            if (unit.RetreatingAfterWait) stat.Count("retreatingAfterWait в построении", true);
        }
        // Порядок массива unitsInfo — он же порядок раздачи бросков — обязан быть тем же, что в
        // battle_start, иначе модель раздаёт броски не тем юнитам (движок делает новую копию
        // BattleMsgData на раунд, и порядок в ней теоретически мог бы отличаться).
        // Призванных движок дописывает в КОНЕЦ unitsInfo, префикс остаётся прежним (ранг 1, разбор 17.09:
        // 20260916-synth passive-003 бои seq 12502437, 13656814, 16729389 — атака призыва G000AA8012), поэтому
        // стабильность — это префикс длины исходного состава плюс хвост только из новых id.
        var ids = build.Round.Units.Where(u => byId.ContainsKey(u.Id)).Select(u => u.Id).ToList();
        var known = state.Units.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tail = ids.Skip(state.Units.Count).ToList();
        stat.Count("порядок unitsInfo стабилен",
                   ids.Count >= state.Units.Count
                   && ids.Take(state.Units.Count).SequenceEqual(state.Units.Select(u => u.Id), StringComparer.OrdinalIgnoreCase)
                   && tail.All(id => !known.Contains(id)));
        if (tail.Count > 0)
        { stat.Skip("очередь", "в хвосте unitsInfo призванные в бою — в составе боя модели их нет"); return false; }

        var onField = state.Units.Where(u => u.OnField && !u.RetreatingAfterWait).ToList();
        if (build.Draws.Count != onField.Count)
        { stat.Skip("очередь", $"бросков {build.Draws.Count}, живых на поле {onField.Count}"); return false; }
        if (build.Observed.Count != onField.Count)
        { stat.Skip("очередь", $"снимок очереди на {build.Observed.Count} юнитов, живых на поле {onField.Count}"); return false; }

        var rows = Rows(onField, build.Draws, rules.Constants);   // до Build: тот гасит конечный LowerInitiative
        var model = TurnOrder.Build(state, new ScriptedRng(build.Draws), rules.Constants).Select(u => u.Id).ToList();
        bool ok = model.SequenceEqual(build.Observed, StringComparer.OrdinalIgnoreCase);
        stat.Count("очередь", ok);
        if (rows.Select(r => r.Value).Distinct().Count() < rows.Count)
        { ties++; tiePairs += rows.Count - rows.Select(r => r.Value).Distinct().Count(); }
        foreach (var h in Hypotheses)
        {
            bool hit = h.Order(rows).Select(r => r.Unit.Id).SequenceEqual(build.Observed, StringComparer.OrdinalIgnoreCase);
            stat.Count("гипотеза: " + h.Name, hit);
            if (h.Name == ProductionRule) stat.Count("харнесс = TurnOrder", hit == ok);
        }
        if (!ok)
            stat.Mismatch($"{name}: модель [{string.Join(", ", model)}], журнал [{string.Join(", ", build.Observed)}]; " +
                          string.Join("; ", rows.Select(r =>
                              $"{r.Unit.Id} {r.Unit.Type.Id} ини {r.Value - build.Draws[r.Index]}+{build.Draws[r.Index]} " +
                              $"слот {r.Unit.Slot} {(r.Unit.Attacker ? "атака" : "защита")}")));
        return true;
    }

    /// <summary>Значения сортировки: та же инициатива и та же раздача бросков, что в
    /// <see cref="TurnOrder.Build"/> (броски по порядку массива unitsInfo, по одному на живого).</summary>
    private static List<Row> Rows(List<BattleUnit> onField, IReadOnlyList<int> draws, RulesConstants c)
    {
        var rows = new List<Row>(onField.Count);
        for (int i = 0; i < onField.Count; i++)
        {
            var u = onField[i];
            // Процент снижения — с суммы инициативы и броска (0x62457c, см. TurnOrder.Build).
            int total = D2.Rules.Units.EffectiveStats.OfAttack(u.Attack, u.Chain).Attack.Initiative + draws[i];
            total -= total * c.LowerInitiativePercent(u.LowerInitiativeLevel) / 100;
            rows.Add(new Row(u, total, i));
        }
        return rows;
    }

    /// <summary>Построения очереди боя: событие <c>round</c>, идущая за ним серия <c>rng(BAT_INIT)</c>
    /// того же потока и первый снимок <c>order</c> после неё. Новое событие <c>round</c> отменяет
    /// незакрытое построение — так отсеивается первое из двух построений раунда 1, результат
    /// которого движок выбрасывает.</summary>
    private static IEnumerable<QueueBuild> Builds(D2.Data.Corpus.Battle battle, int batInit)
    {
        RoundEvent? pending = null;
        List<int> draws = [];
        bool collecting = false;
        foreach (var e in battle.Events)
        {
            switch (e)
            {
                case RoundEvent r: pending = r; draws = []; collecting = true; continue;
                case RngEvent { Std: false } g when collecting && pending is not null && g.Tid == pending.Tid:
                    if (g.Max == batInit) { draws.Add((int)g.R); continue; }
                    collecting = false; continue;
                case BattleUnitsEvent { Order.Count: > 0 } b when pending is not null:
                    yield return new QueueBuild(pending, draws, b.Order.Select(o => o.Id).ToList());
                    pending = null; collecting = false; continue;
                case TurnEvent { Order.Count: > 0 } t when pending is not null:
                    yield return new QueueBuild(pending, draws, t.Order.Select(o => o.Id).ToList());
                    pending = null; collecting = false; continue;
                case MissEvent or DamageEvent or BattleTurnEvent or BattleUnitsEvent or TurnEvent or BattleEndEvent:
                    collecting = false; continue;
            }
        }
    }

    /// <summary>Парные счётчики: сошлось/разошлось по ключу, пропуски с причиной, примеры расхождений.</summary>
    private sealed class Stats
    {
        private readonly Dictionary<string, (int ok, int bad)> _counts = [];
        private readonly Dictionary<(string key, string reason), int> _skips = [];
        public List<string> Mismatches { get; } = [];
        public int Checked => _counts.GetValueOrDefault("очередь").ok + _counts.GetValueOrDefault("очередь").bad;
        public int Skipped => _skips.Values.Sum();

        public void Count(string key, bool ok)
        {
            var v = _counts.GetValueOrDefault(key);
            _counts[key] = ok ? (v.ok + 1, v.bad) : (v.ok, v.bad + 1);
        }

        public void Skip(string key, string reason) => _skips[(key, reason)] = _skips.GetValueOrDefault((key, reason)) + 1;
        public void Mismatch(string text) => Mismatches.Add(text);
        public int Ok(string key) => _counts.GetValueOrDefault(key).ok;
        public int Bad(string key) => _counts.GetValueOrDefault(key).bad;

        public IEnumerable<string> Lines()
        {
            foreach (var (k, v) in _counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                yield return $"{k,-28} сошлось={v.ok,-6} разошлось={v.bad}";
            foreach (var (k, n) in _skips.OrderBy(kv => kv.Key.key, StringComparer.Ordinal))
                yield return $"{k.key,-28} пропущено={n,-6} причина: {k.reason}";
        }
    }
}
