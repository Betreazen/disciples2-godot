using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Battle;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Выбор действия тактики ИИ против зонда 6.5+ — все сессии <see cref="AiCorpus"/> (<c>20260918-probe65</c>: ai-q09 и
/// «Растленная власть»; <c>20260919-human</c>: партия человека на 71 день, шесть загрузок), счётчики — на сессию. Сверяются правила порта: вставка кандидата (<see cref="AiTactic.Insert{T}"/>),
/// поправки (<see cref="AiTacticScore.EditorPriority"/>, <see cref="AiTacticScore.TargetSetBonus"/>), сортировка,
/// четвёрка и выбор (<see cref="AiTactic.Top{T}"/>), очередь отрядов (<see cref="AiTacticOrders.TurnQueue"/>),
/// клетка жезла (<see cref="AiTacticScore.RodCell"/>), сила (<see cref="AiTacticScore.Strength"/>) и фильтр
/// приказа (<see cref="AiTactic.Admits"/>).
/// <para><b>Что выведено, а не прочитано.</b> Проверка пути фазы 3 (слот 6 действия) зондом не пишется: её ответ
/// берётся из <c>top</c> (есть — счёт оттуда, нет — <see cref="AiTactic.NoScore"/>), так что сверяются порядок
/// сортировки, остановка на четырёх и выбор. Признак «своя группа не может ранить» (<c>0x5dcd53</c>) выводится из
/// ответа 0 силы; принадлежность цели множеству +100 — из разницы <c>after − before</c>.</para>
/// <para><b>Состояние карты</b> — сохранение дня (строка <c>save</c> с его <c>turn</c>), поверх него
/// <c>ai_order</c> и <c>stack_move</c> до сверяемой строки.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class AiTacticProbeHarnessTests(ITestOutputHelper output)
{
    private static readonly string[] Wanted =
    [
        "ai_tactic_candidate", "ai_tactic_adjust", "ai_tactic_pick", "ai_tactic_stack", "ai_tactic_result",
        "ai_rod_cell", "ai_strength", "ai_turn", "ai_order", "stack_move", "save",
    ];

    private static readonly Lazy<List<(AiSession Session, List<CorpusEvent> Events)>> Events = new(() => AiCorpus.Split(Wanted));

    private static List<(AiSession Session, List<CorpusEvent> Events)> Sessions()
    {
        Skip.If(AiCorpus.Sessions.Count == 0, $"нет журналов зонда 6.5+ в {CorpusFiles.Dir}");
        return Events.Value;
    }

    private static IEnumerable<CorpusEvent> All() => Sessions().SelectMany(x => x.Events);

    /// <summary>События всех сессий по порядку, каждое со своей сессией.</summary>
    private static IEnumerable<(AiSession S, CorpusEvent E)> Tagged() =>
        Sessions().SelectMany(x => x.Events.Select(e => (x.Session, e)));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int Cheb(long ax, long ay, long bx, long by) => (int)Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    // ------------------------------------------------------------------ 1. вставка 0x4686cf

    [SkippableFact] public void Insertion_MatchesProbe()
    {
        var k = new Counters();
        var memo = AiTacticMemo.Empty;
        var results = new List<AiTacticResultEvent>();
        int turn = 0;
        AiSession? session = null;
        bool loaded = false;
        foreach (var (map, e) in Tagged())
        {
            if (!ReferenceEquals(map, session))
            {
                // Другой журнал или другая карта — другая партия, память пуста. Новая загрузка той же партии (откат человека) память
                // сохраняет: без неё первый день после загрузки терял «вчера 5 → ×1,25» (20260919-human, сессии 2–5,
                // 15 строк). Годится только то, что было до сохранения, — записи дней раньше первого хода ИИ в сессии.
                if (session?.Journal != map.Journal || session.Map != map.Map) results.Clear();
                (session, turn, loaded) = (map, 0, true);
            }
            switch (e)
            {
                case AiTurnEvent { Phase: "begin" } t:
                    turn = (int)t.Turn;
                    if (loaded)
                    {
                        results.RemoveAll(r => r.Turn >= turn);
                        memo = results.Aggregate(AiTacticMemo.Empty, (m, r) => m.With(r.Stack!, r.Target!, (int)r.Point!.X, (int)r.Point.Y, (int)r.Turn!.Value, (int)r.Status));
                        loaded = false;
                    }
                    break;
                case AiTacticResultEvent { Stack: { } rs, Target: { } rt, Point: { } rp, Turn: { } rd } r:
                    results.Add(r);
                    memo = memo.With(rs, rt, (int)rp.X, (int)rp.Y, (int)rd, (int)r.Status);
                    break;
                case AiTacticCandidateEvent c:
                    CheckInsert(k, map.Name, c, memo, turn);
                    break;
            }
        }
        Report(k, "вставка кандидата 0x4686cf", ["insert"]);
    }

    private static void CheckInsert(Counters k, string map, AiTacticCandidateEvent c, AiTacticMemo memo, int turn)
    {
        if (c.Action is not { Stack: { } s, Target: { } tg, Point: { } p } || c.ScoreIn is not { } scoreIn
            || c.Inserted is not { } inserted)
        { k.Skip($"{map} insert", "действие или счёт не прочитаны"); return; }
        int today = memo.Status(s, tg, (int)p.X, (int)p.Y, turn);
        int yesterday = memo.Status(s, tg, (int)p.X, (int)p.Y, turn - 1);
        var list = AiTactic.Insert<string>([], s, scoreIn, today, yesterday);
        double? port = list.Count == 1 ? list[0].Score : null;
        bool ok = (list.Count == 1) == inserted && port == c.Score;
        string detail = $"seq {c.Seq} день {turn} статус {today}/{yesterday} вход {scoreIn} → порт {port}, зонд {c.Score}";
        k.Count($"{map} insert", ok, detail);
        if (scoreIn == AiTactic.NoScore) k.Count($"{map} insert: NoScore выброшен", ok, detail);
        if (scoreIn == 0) k.Count($"{map} insert: счёт 0 вставлен", ok, detail);
        if (yesterday == AiTactic.StatusProgress) k.Count($"{map} insert: вчера 5 → ×1,25", ok, detail);
        if (today != 0) k.Count($"{map} insert: сегодня уже записан (вчерашнего нет)", ok, detail);
    }

    // ------------------------------------------------------------------ 2. поправки 0x466d5c / 0x466cd8

    [SkippableFact] public void Adjust_MatchesPortFormulas()
    {
        var k = new Counters();
        var state = new DayState();
        foreach (var (map, e) in Tagged())
        {
            state.Apply(e, map);
            if (e is not AiTacticAdjustEvent a) continue;
            if (a.Before is not { } before || a.After is not { } after || a.Action is not { } act)
            { k.Skip($"{map.Name} adjust", "счёт или действие не прочитаны"); continue; }
            if (a.Kind == "priority")
            {
                bool orderTarget = state.Stack(act.Stack) is { } st && Same(st.OrderTarget, act.Target);
                double port = AiTacticScore.EditorPriority(before, (int?)a.Priority, orderTarget);
                k.Count($"{map.Name} adjust priority", port == after,
                        $"seq {a.Seq} приоритет {a.Priority} {before} → порт {port}, зонд {after}");
                if (a.Priority is { } pr and not 3) k.Count($"{map.Name} adjust priority ≠ 3 ({pr})", port == after, $"seq {a.Seq}");
            }
            else
            {
                bool inSet = after != before;
                double port = AiTacticScore.TargetSetBonus(before, inSet);
                k.Count($"{map.Name} adjust set", port == after, $"seq {a.Seq} {before} → порт {port}, зонд {after}");
                if (inSet) k.Count($"{map.Name} adjust set: +100 (выведено)", port == after, $"seq {a.Seq}");
            }
            if (!state.Knows(act.Stack)) k.Skip($"{map.Name} adjust", "отряда нет в состоянии дня (цель приказа не известна)");
        }
        k.Note("поле stack строки ai_tactic_adjust (после 6.5.1 мусора быть не должно): " + ProbeAdjustStacks());
        Report(k, "поправки 0x466d5c / 0x466cd8", ["adjust priority", "adjust set"]);
    }

    private static string ProbeAdjustStacks()
    {
        var rows = All().OfType<AiTacticAdjustEvent>().Where(a => a.Kind == "priority").ToList();
        int garbage = rows.Count(a => a.Stack is { } s && s.StartsWith("G0000000", StringComparison.OrdinalIgnoreCase));
        int equal = rows.Count(a => Same(a.Stack, a.Action?.Stack));
        return $"{garbage} из {rows.Count} вида G0000000xx (мусор до 6.5.1), совпало с action.stack {equal}";
    }

    // ------------------------------------------------------------------ 3. сортировка, четвёрка, выбор

    private sealed record Node(string Ptr, double Score);

    [SkippableFact] public void Pick_SortTopAndChosenMatchProbe()
    {
        var k = new Counters();
        int rescoreChanged = 0, removed = 0;
        foreach (var (map, ev) in Sessions())
        {
            var inner = ev.Where(e => e is AiTacticCandidateEvent or AiTacticAdjustEvent or AiTacticResultEvent).ToList();
            var seqs = inner.Select(e => e.Seq).ToList();
            IEnumerable<CorpusEvent> Window(AiTacticPickEvent p)
            {
                int i = LowerBound(seqs, p.Seq + 1);
                for (; i < inner.Count && inner[i].Seq < p.SeqEnd; i++) yield return inner[i];
            }

            List<Node> list = [];
            IReadOnlyList<AiScored<string>>? portTop = null;
            foreach (var p in ev.OfType<AiTacticPickEvent>())
            {
                string m = map.Name;
                switch (p.PhaseBefore)
                {
                    case 2:
                        list = Phase2(k, m, p, Window(p));
                        k.Count($"{m} pick: длина после фазы 2", list.Count == p.Count, $"seq {p.Seq} {list.Count} против {p.Count}");
                        break;
                    case 3:
                    {
                        var top = (p.Top ?? []).Select(t => new Node(t.Action?.Ptr ?? "", t.Score ?? double.NaN)).ToList();
                        var answers = top.ToDictionary(t => t.Ptr, t => t.Score);
                        int asked = 0;
                        portTop = AiTactic.Top(list.Select(n => new AiScored<string>(n.Ptr, n.Score)).ToList(),
                                               (ptr, s) => { asked++; return answers.TryGetValue(ptr, out var r) ? r : AiTactic.NoScore; });
                        bool ok = portTop.Select(t => new Node(t.Action, t.Score)).SequenceEqual(top);
                        k.Count($"{m} pick top", ok, $"seq {p.Seq} порт [{Show(portTop)}], зонд [{string.Join(", ", top.Select(t => $"{t.Ptr}:{t.Score}"))}]");
                        k.Count($"{m} pick: длина после фазы 3", top.Count == p.Count, $"seq {p.Seq}");
                        rescoreChanged += top.Count(t => list.First(n => n.Ptr == t.Ptr).Score != t.Score);
                        removed += asked - top.Count;
                        break;
                    }
                    case 4:
                        CheckChosen(k, m, p, portTop, Window(p).OfType<AiTacticResultEvent>().ToList());
                        portTop = null;
                        break;
                }
            }
        }
        k.Note($"проверка пути сменила счёт у {rescoreChanged} оставшихся; убрала (NoScore) {removed} кандидатов");
        Report(k, "фазы 2–4 CAiTactic::step 0x466388", ["pick top", "pick chosen", "pick result"]);
    }

    /// <summary>Список после фазы 2: вставленные узлы в порядке вставки, счёт — после поправок.</summary>
    private static List<Node> Phase2(Counters k, string m, AiTacticPickEvent p, IEnumerable<CorpusEvent> window)
    {
        var list = new List<Node>();
        foreach (var e in window)
        {
            if (e is AiTacticCandidateEvent { Inserted: true, Action: { } a, Score: { } s }) list.Add(new Node(a.Ptr, s));
            if (e is not AiTacticAdjustEvent { Action: { } aa } adj) continue;
            int i = list.FindIndex(n => n.Ptr == aa.Ptr);
            if (i < 0) { k.Count($"{m} pick: поправка к вставленному", false, $"seq {adj.Seq} {aa.Ptr}"); continue; }
            k.Count($"{m} pick: поправка к вставленному", list[i].Score == adj.Before, $"seq {adj.Seq}");
            list[i] = list[i] with { Score = adj.After ?? double.NaN };
        }
        return list;
    }

    private static void CheckChosen(Counters k, string m, AiTacticPickEvent p, IReadOnlyList<AiScored<string>>? portTop,
                                    IReadOnlyList<AiTacticResultEvent> results)
    {
        if (portTop is null) { k.Skip($"{m} pick chosen", "фаза 4 без фазы 3"); return; }
        var chosen = p.Chosen ?? [];
        var port = portTop.Count > 0 ? portTop[0] : (AiScored<string>?)null;
        bool ok = port is null
            ? chosen.Count == 0
            : chosen.Count == 1 && chosen[0].Action?.Ptr == port.Value.Action && chosen[0].Score == port.Value.Score;
        k.Count($"{m} pick chosen", ok, $"seq {p.Seq} порт {port?.Action}:{port?.Score}");
        var act = chosen.Count == 1 ? chosen[0].Action : null;
        bool resultOk = act is null
            ? results.Count == 0
            : results.Count == 1 && Same(results[0].Stack, act.Stack) && Same(results[0].Target, act.Target)
              && results[0].Point == act.Point;
        k.Count($"{m} pick result", resultOk, $"seq {p.Seq} строк результата {results.Count}");
    }

    private static string Show(IEnumerable<AiScored<string>> l) => string.Join(", ", l.Select(t => $"{t.Action}:{t.Score}"));

    private static int LowerBound(List<long> a, long v)
    {
        int i = a.BinarySearch(v);
        return i >= 0 ? i : ~i;
    }

    // ------------------------------------------------------------------ 4. очередь отрядов 0x467b07 / 0x467c0e

    [SkippableFact] public void StackOrder_MatchesTurnQueue()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var book = ProgressBook.Load(GamePaths.Globals);
        var k = new Counters();
        var state = new DayState();
        (string Player, long Turn, AiSession Map, List<string> Expected, List<string> Seen)? cur = null;
        void Close()
        {
            if (cur is not { } c || c.Seen.Count == 0) return;
            CheckQueue(k, c.Map.Name, $"{c.Player} день {c.Turn}", c.Expected, c.Seen);
        }
        foreach (var (map, e) in Tagged())
        {
            state.Apply(e, map);
            switch (e)
            {
                case AiTurnEvent { Phase: "begin", Player: { } pl } t:
                    Close();
                    cur = (pl, t.Turn, map, [], []);
                    break;
                case AiTacticPickEvent { PhaseBefore: 0 } when cur is { Expected.Count: 0, Seen.Count: 0 } c:
                    c.Expected.AddRange(Queue(state, c.Player, book, k, map.Name));
                    break;
                case AiTacticStackEvent { Stack: { } s } when cur is { } c:
                    if (c.Seen.Count == 0 || !Same(c.Seen[^1], s)) c.Seen.Add(s);
                    break;
            }
        }
        Close();
        Report(k, "очередь отрядов 0x467b07 / 0x467c0e", ["queue"]);
    }

    private static IEnumerable<string> Queue(DayState state, string player, ProgressBook book, Counters k, string m)
    {
        var queued = new List<AiQueued>();
        foreach (var s in state.Stacks.Values.Where(s => Same(s.Owner, player)))
        {
            var order = AiTacticLists.EffectiveOrder((StackOrder)s.Order, (StackOrder)s.AiOrder);
            if (order == StackOrder.Stand) continue;
            string target = s.Order == (int)StackOrder.Normal ? s.AiOrderTarget : s.OrderTarget;
            queued.Add(new AiQueued(s.Id, Convert.ToUInt32(s.Id[^4..], 16), Group(state, s, order, book, k, m), target));
        }
        return AiTacticOrders.TurnQueue(queued, null);
    }

    private static AiStackGroup Group(DayState state, StackNow s, StackOrder order, ProgressBook book, Counters k, string m)
    {
        if (order == StackOrder.Assist) return AiStackGroup.Assist;
        if (order == StackOrder.DefendCity) return AiStackGroup.DefendCity;
        var map = state.Map!;
        if (!map.Units.TryGetValue(s.LeaderId, out var leader) || book.CategoryOf(leader.TypeId) is not { } cat)
        { k.Skip($"{m} queue", "лидер не опознан — группа «прочие»"); return AiStackGroup.Other; }
        if (cat == SummonCategory) return AiStackGroup.Summon;
        if (cat == NobleCategory) return AiStackGroup.Noble;
        bool rod = map.Stacks.TryGetValue(s.Id, out var saved)
                   && book.Abilities(map, saved, -1, out _)?.All.Contains(Rods.LeaderAbilityRod) == true;
        return rod ? AiStackGroup.Rod : AiStackGroup.Other;
    }

    /// <summary><c>LunitC</c>: 1 <c>L_NOBLE</c> (<c>0x838198</c>), 4 <c>L_SUMMON</c> (<c>0x838168</c>).</summary>
    private const int NobleCategory = 1, SummonCategory = 4;

    /// <summary>Отряды, взятые тактикой, — подпоследовательность очереди порта (невалидных движок снимает молча,
    /// <c>0x466b86</c>). Отряды, которых в очереди нет, пропускаются: нанятые и призванные в ход. Призванный
    /// (<c>0x4671b1</c>: добавить и пересортировать) встаёт в группу призыва перед текущим отрядом — у всех трёх
    /// случаев корпуса текущая обёртка перед этим сброшена (<c>stack = null</c> у следующего шага фазы 0), значит
    /// пересортировка шла без «первого»; после призванного текущий отряд продолжает ход, повтор схлопывается.</summary>
    private static void CheckQueue(Counters k, string m, string turn, List<string> expected, List<string> seen)
    {
        var known = new List<string>();
        foreach (var s in seen)
        {
            if (!expected.Any(x => Same(x, s))) { k.Skip($"{m} queue", "отряда нет в очереди начала хода (нанят или призван в ход)"); continue; }
            if (known.Count == 0 || !Same(known[^1], s)) known.Add(s);
        }
        int j = 0;
        foreach (var x in expected)
            if (j < known.Count && Same(known[j], x)) j++;
        k.Count($"{m} queue", j == known.Count,
                $"{turn}: порт [{string.Join(" ", expected.Select(x => x[^4..]))}], зонд [{string.Join(" ", seen.Select(x => x[^4..]))}]");
    }

    // ------------------------------------------------------------------ 5. клетка жезла 0x46164d

    [SkippableFact] public void RodCell_MatchesEveryProbeCall()
    {
        var k = new Counters();
        var state = new DayState();
        foreach (var (map, e) in Tagged())
        {
            state.Apply(e, map);
            if (e is not AiRodCellEvent r) continue;
            if (r.CrystalPos is not { } cp || r.Stack is null)
            { k.Skip($"{map.Name} rod", "кристалл или отряд не прочитаны"); continue; }
            var pos = StackPosition(k, map.Name, r, state);
            if (pos is null) { k.Skip($"{map.Name} rod", "позиция отряда не восстановлена"); continue; }
            AiRodCell Cell(int x, int y) => r.Cells.Single(c => c.X == x && c.Y == y);
            bool Fail(bool? ok) => ok is { } v ? !v : throw new InvalidOperationException("проверка, до которой движок не дошёл");
            (int X, int Y)? port;
            try
            {
                port = AiTacticScore.RodCell((int)cp.X, (int)cp.Y, pos.Value.X, pos.Value.Y,
                                             (x, y) => Fail(Cell(x, y).OkPlace), (x, y) => Fail(Cell(x, y).OkAlly),
                                             (x, y) => Fail(Cell(x, y).OkBlock));
            }
            catch (InvalidOperationException ex) { k.Count($"{map.Name} rod", false, $"seq {r.Seq}: {ex.Message}"); continue; }
            (int, int)? probe = r.Ret && r.Result is { } res ? ((int)res.X, (int)res.Y) : null;
            var marked = r.Cells.Where(c => c.Chosen).Select(c => ((int)c.X, (int)c.Y)).ToList();
            bool ok = port == probe && (probe is null ? marked.Count == 0 : marked.SequenceEqual([probe.Value]));
            k.Count($"{map.Name} rod", ok, $"seq {r.Seq} порт {port}, зонд {probe}");
            bool order = r.Cells.Select(c => ((int)(c.X - cp.X), (int)(c.Y - cp.Y))).SequenceEqual(AiTacticScore.RodNeighbours);
            k.Count($"{map.Name} rod: порядок соседей", order, $"seq {r.Seq}");
        }
        Report(k, "клетка жезла 0x46164d", ["rod", "rod: dist = Чебышёв"]);
    }

    /// <summary>Позиция отряда: из состояния дня, если с ней сходятся все <c>dist</c> годных клеток; иначе — точка,
    /// согласная со всеми <c>dist</c> (счётчик «dist = Чебышёв»: такая точка есть — слот 13 и есть Чебышёв).</summary>
    private static (int X, int Y)? StackPosition(Counters k, string m, AiRodCellEvent r, DayState state)
    {
        var valid = r.Cells.Where(c => c.Dist is not null).ToList();
        bool Fits(long x, long y) => valid.All(c => Cheb(x, y, c.X, c.Y) == c.Dist);
        var known = state.Stack(r.Stack);
        if (valid.Count == 0) return known?.Pos;
        var c0 = valid[0];
        int d = (int)c0.Dist!.Value;
        var fit = (from dx in Enumerable.Range(-d, 2 * d + 1)
                   from dy in Enumerable.Range(-d, 2 * d + 1)
                   let x = c0.X + dx
                   let y = c0.Y + dy
                   where Fits(x, y)
                   select ((int)x, (int)y)).ToList();
        k.Count($"{m} rod: dist = Чебышёв", fit.Count > 0, $"seq {r.Seq}: ни одна точка не даёт все dist");
        if (known is not null && Fits(known.Pos.X, known.Pos.Y)) return known.Pos;
        k.Skip($"{m} rod", "позиция отряда по сохранению и stack_move не сходится с dist — взята из dist");
        return fit.Count > 0 ? fit[0] : null;
    }

    // ------------------------------------------------------------------ 6. сила 0x5dda25

    [SkippableFact] public void Strength_MatchesFormulaExactly()
    {
        var k = new Counters();
        foreach (var (map, s) in Tagged().Where(x => x.E is AiStrengthEvent).Select(x => (x.S, (AiStrengthEvent)x.E)))
        {
            if (s.A is not { } a || s.B is not { } b || s.C is not { } c || s.Ret is not { } ret)
            { k.Skip($"{map.Name} strength", "аргумент не прочитан"); continue; }
            double formula = AiTacticScore.Strength(a, b, c, canDamage: true);
            bool cannot = ret == 0 && formula != 0;
            double port = AiTacticScore.Strength(a, b, c, canDamage: !cannot);
            k.Count($"{map.Name} strength", port == ret, $"seq {s.Seq} a {a} b {b} c {c}: порт {port}, зонд {ret}");
            if (cannot) k.Skip($"{map.Name} strength", $"ответ 0 при формуле ≠ 0 — «не может ранить» (выведено), {s.RetAddr}");
        }
        Report(k, "сила 0x5dda25", ["strength"]);
    }

    // ------------------------------------------------------------------ 7. фильтр приказа

    [SkippableFact] public void OrderFilter_AdmitsEveryCandidateType()
    {
        var k = new Counters();
        var state = new DayState();
        StackNow? cycle = null;
        // Точка фильтра — позиция отряда при НОВОМ выборе (обёртка 0x465af9 строит фильтр 0x465c90 один раз, 0x466a9f); продолжение
        // того же отряда после исполнения (0x466aba, ai_tactic_pick 0 → 1 с тем же stack) её не меняет. 20261016e-av greenskins:
        // S092KC0028 (L_DEFEND_CITY) выбран на (63, 12), AttackStack на (67, 17) движок пропускает и с (61, 7) и из крепости (57, 3).
        bool selected = true;
        (int X, int Y) filterPos = default;
        string? filterFor = null;
        foreach (var (map, e) in Tagged())
        {
            state.Apply(e, map);
            if (e is AiTacticPickEvent { PhaseBefore: 0 } pick && !Same(pick.Stack, pick.StackAfter)) selected = true;
            if (e is AiTacticStackEvent st)
            {
                cycle = state.Stack(st.Stack);
                if (cycle is not null && (selected || !Same(filterFor, cycle.Id))) (filterPos, filterFor, selected) = (cycle.Pos, cycle.Id, false);
            }
            if (e is not AiTacticCandidateEvent { Action: { Type: { } type, Stack: { } sid } act } c) continue;
            if (cycle is null) { k.Skip($"{map.Name} filter", "отряда шага нет в состоянии дня"); continue; }
            if (!Same(sid, cycle.Id))
                k.Skip($"{map.Name} filter", $"action.stack ≠ отряд шага (тип 0x{type:x}) — взят отряд шага");
            var order = AiTacticLists.EffectiveOrder((StackOrder)cycle.Order, (StackOrder)cycle.AiOrder);
            string target = cycle.Order == (int)StackOrder.Normal ? cycle.AiOrderTarget : cycle.OrderTarget;
            if (order == StackOrder.DefendLocation)
            { k.Skip($"{map.Name} filter", "L_DEFEND_LOCATION — радиус GVars не перенесён"); continue; }
            int distance = act.Point is { } ap ? Cheb(filterPos.X, filterPos.Y, ap.X, ap.Y) : 0;
            // Фильтр судит оценщик построителя, а не готовое действие: у построителей сценарных приказов действие может быть
            // помехой на пути (мешок — тип 0, чужой отряд), а оценщик — их тип с целью приказа (20261014-replay, seq 246620, 257782).
            var builder = OrderBuilderType(c.RetAddr);
            var kind = builder ?? (AiActionType)type;
            // Цель оценщика — цель приказа, а действие — помеха на пути к ней: мешок (тип 0) или отряд (KC), — или объект в конце пути:
            // крепость (FT, 0x460fa0; 20261018b, карта 18: MoveToLocation S143KC0010 → S143FT0006, цель приказа S143LO0000). Иначе цель
            // действия и есть цель оценщика и сверяется как есть.
            bool obstacle = builder is not null
                            && ((AiActionType)type != builder || act.Target is { Length: 10 } t && t.Substring(4, 2) is "BG" or "KC" or "FT");
            bool ok = AiTactic.Admits(order, kind, obstacle || Same(act.Target, target), distance);
            string detail = $"seq {c.Seq} {cycle.Id} {cycle.Pos} приказ {cycle.Order}/{cycle.AiOrder} цель {target}: " +
                            $"{kind} на {act.Target} {act.Point} (d {distance}), вставлен {c.Inserted}";
            k.Count($"{map.Name} filter", ok, detail);
            k.Count($"{map.Name} filter {order}", ok, detail);
        }
        Report(k, "фильтр приказа 0x465c90", ["filter"]);
    }

    /// <summary>Тип построителя сценарного приказа по <c>ret_addr</c> вставки: <c>0x45b2c1</c> — <c>MoveToLocation</c>
    /// (<c>0x45afe8</c>), <c>0x455b8f</c> — <c>DefendParty</c> (<c>0x45541d</c>), <c>0x45520b</c> — <c>DefendLocation</c>
    /// (<c>0x454c78</c>). Их счёт — константа построителя (150), а тип и цель действия — того, что выбрал оценщик у цели.</summary>
    internal static AiActionType? OrderBuilderType(string? retAddr) => retAddr switch
    {
        "0x45b2c1" => AiActionType.MoveToLocation,
        "0x455b8f" => AiActionType.DefendParty,
        "0x45520b" => AiActionType.DefendLocation,
        _ => null,
    };

    // ------------------------------------------------------------------ 8. поля зонда

    [SkippableFact] public void ProbeFields_Anomalies()
    {
        var ev = All().ToList();
        var k = new Counters();
        k.Note("ai_tactic_adjust.stack: " + ProbeAdjustStacks());
        var ptrs = Sessions().Select(x => $"{x.Session.Name}: {string.Join(",", x.Events.OfType<AiTacticResultEvent>().Select(r => r.TurnPtr).Distinct())}");
        k.Note("ai_tactic_result.turn_ptr (один указатель на загрузку карты — CScenarioInfo+0x20): " + string.Join("; ", ptrs));
        var lens = ev.OfType<AiStrengthEvent>().SelectMany(s => new[] { s.P2N, s.P4N }).Distinct().Order().ToList();
        k.Note($"ai_strength.p2_n/p4_n (длины векторов юнитов, 6.5.1) — значения: {string.Join(", ", lens)}");
        var emptyChosen = ev.OfType<AiTacticPickEvent>().Count(p => p.PhaseBefore == 4 && p.Chosen is { Count: 0 });
        k.Note($"ai_tactic_pick фазы 4 с пустым chosen ([] а не null): {emptyChosen}");
        foreach (var (m, r) in Tagged().Where(x => x.E is AiTacticResultEvent).Select(x => (x.S, (AiTacticResultEvent)x.E)))
            k.Count($"{m.Name} result: день = день хода", r.Turn is not null, $"seq {r.Seq}");
        Report(k, "поля зонда 6.5", []);
    }

    private void Report(Counters k, string title, string[] required)
    {
        k.Print(output, title);
        foreach (var name in required)
            AiCorpus.Require(s => k.Of($"{s.Name} {name}"), name);
    }

    // ------------------------------------------------------------------ состояние дня

    /// <summary>Отряд на текущий момент. <see cref="Pos"/> — позиция глазами движка (<c>0x5ed9b2</c>): в крепости
    /// (<c>+0x94</c>) это её угол <c>0x5e9563(pos, size)</c> = <see cref="AiTacticScore.AnchorCell"/>, иначе своя
    /// клетка.</summary>
    private sealed record StackNow(string Id, string Owner, string LeaderId, int X, int Y, int Order, string OrderTarget,
                                   int AiOrder, string AiOrderTarget, (int X, int Y)? FortAnchor)
    {
        public (int X, int Y) Pos => FortAnchor ?? (X, Y);
    }

    /// <summary>Сохранение дня и поверх него приказы и шаги отрядов до текущей строки журнала.</summary>
    private sealed class DayState
    {
        private readonly Dictionary<string, MapState?> _files = new(StringComparer.Ordinal);
        public MapState? Map { get; private set; }
        public Dictionary<string, StackNow> Stacks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Knows(string? id) => id is not null && Stacks.ContainsKey(id);

        public StackNow? Stack(string? id) => id is not null && Stacks.TryGetValue(id, out var s) ? s : null;

        /// <summary>Позиция объекта: отряд — текущая, прочее — из сохранения.</summary>
        public (long X, long Y)? Position(string id)
        {
            if (id.Length == 0) return null;
            if (Stack(id) is { } s) return s.Pos;
            return Map is not null && Map.Objects.TryGetValue(id, out var o) ? (o.X, o.Y) : null;
        }

        private AiSession? _session;

        public void Apply(CorpusEvent e, AiSession session)
        {
            if (!ReferenceEquals(session, _session))
            {
                // Новая загрузка карты: состояние прошлой сессии (другая партия или откат) не переносится.
                _session = session;
                Map = null;
                Stacks.Clear();
            }
            switch (e)
            {
                case SaveEvent sv:
                    Load(session, sv.Seq);
                    break;
                case AiOrderEvent { Ret: true } o when Stack(o.Stack) is { } s:
                    Stacks[s.Id] = s with
                    {
                        Order = o.OrderAfter >= 0 ? (int)o.OrderAfter : s.Order,
                        OrderTarget = o.OrderAfter == o.Order ? Ref(o.Target) : s.OrderTarget,
                        AiOrder = o.AiOrderAfter >= 0 ? (int)o.AiOrderAfter : s.AiOrder,
                        AiOrderTarget = o.OrderAfter != o.Order && o.AiOrderAfter == o.Order ? Ref(o.Target) : s.AiOrderTarget,
                    };
                    break;
                case StackMoveEvent { Ret: true } mv when Stack(mv.Stack) is { } s:
                    Stacks[s.Id] = s with { X = (int)mv.X, Y = (int)mv.Y, FortAnchor = AnchorAt((int)mv.X, (int)mv.Y) };
                    break;
            }
        }

        /// <summary>Шаг в клетку крепости (её позицию) — вход внутрь. Размер: столица 5, деревня 4.</summary>
        private (int X, int Y)? AnchorAt(int x, int y) =>
            Map?.Sites.FirstOrDefault(f => f.Kind is SiteKind.Capital or SiteKind.Village && f.X == x && f.Y == y) is { } fort
                ? AiTacticScore.AnchorCell(fort.X, fort.Y, fort.Kind == SiteKind.Capital ? 5 : 4) : null;

        private static string Ref(string id) => id.Equals("G000000000", StringComparison.OrdinalIgnoreCase) ? "" : id;

        /// <summary>Снимок строки <c>save</c> — её копия из манифеста (<see cref="AiSession.Saves"/>); строки без копии
        /// состояние не трогают.</summary>
        private void Load(AiSession session, long saveSeq)
        {
            var file = session.Saves.FirstOrDefault(x => x.Seq == saveSeq)?.File;
            if (file is null) return;
            if (!_files.TryGetValue(file, out var map)) _files[file] = map = SaveMaps.Map(file);
            if (map is null || ReferenceEquals(map, Map)) return;
            Map = map;
            Stacks.Clear();
            foreach (var s in map.Stacks.Values)
                Stacks[s.Id] = new StackNow(s.Id, s.Owner, s.LeaderId, s.X, s.Y, s.Order, s.OrderTarget, s.AiOrder,
                                            s.AiOrderTarget, s.Inside.Length > 0 ? AnchorAt(s.X, s.Y) : null);
        }
    }
}
