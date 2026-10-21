using D2.Data.Corpus;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Раскрытие узлов A* узел в узел (<c>path_expand</c>, схема 7.2: искатель <c>0x5cc6da</c>, раскрытие <c>0x680786</c>): на каждом поиске
/// отряда ИИ порт (<see cref="PathFinder.Find"/> с масками <see cref="PathFinder.AiMasks"/> по режиму, <c>a8</c>, <c>a10</c> и
/// <c>forbidWater</c> строки) снимает узлы из открытого списка в том же порядке, с теми же <c>g</c> и <c>f</c> во float (первые 300 —
/// потолок зонда; без обрезки — и их число), и приходит к тому же пути (список <c>+0x30</c>). Состояние карты — живой день
/// (<see cref="LiveDayReplay"/>). Расхождение узлов классифицируется по первому разошедшемуся: «та же f» — порядок равных,
/// «другая f» — иная цена клетки, то есть состояние карты живого дня.
/// </summary>
[Trait("Category", "Corpus")]
public sealed class PathExpandHarnessTests(ITestOutputHelper output)
{
    /// <summary>Потолок узлов в строке зонда.</summary>
    private const int NodeCap = 300;

    /// <summary>
    /// Измерено 30.09.2026 (ai-q09, зонд 7.2, 3410 поисков). До правки g′ во float (двойное округление «float + цена»): узлы совпали 637,
    /// пути 3341 (узлы расходились на 1 ulp у g — 10,070999 против 10,071 — и дальше порядок равных f). После: узлы 3352, пути 3346.
    /// Остаток узлов 58 — состояние живого дня: у первого разошедшегося узла другая f (43) или другое число узлов (2); «та же f» (13) — только
    /// в полных обходах недостижимой цели KC0096/KC0097, где у движка и порта разное число раскрытых (1150 и 1194) — открытый список уже
    /// другой. Остаток путей при тех же узлах 63 — не A*, а разбор списка после него (30.09, вечер; docs/map-path.md §1 «Список точек»):
    /// 59 — цель-мешок в маске «можно» (a8): движок цель оставляет (флаг проходимости цели 0x5cc958 — ещё и бит a7, pop_back 0x680b05 не
    /// делается), порт снимал; 4 — обрыв «идти» = 0 (0x5cd984 → 0x5cd4f5: хвост после последней достижимой стёрт, цель +0x10 := клетка
    /// обрыва), порт сравнивается по Points[0..Reachable] при StackPath.CutAt; 1 — враждебный отряд на цели в a7 при a8 и не режиме 2
    /// (0x5ccc7d), а не «в режиме 1» (seq 228446: режим 1, a8 = 0 — цель снята). Пути 3346 → 3408; прежние правила — 3346.
    /// <para>18.10 (реестр П12): остаток 2 был состоянием живого дня. Мешок, взятый с соседней клетки (seq 322118: BG0027 — KC0096 в seq
    /// 322073, предмет IM0086 в equip_check 322088; строк подбора журнал не пишет), снимается по equip_check его предмета у отряда рядом
    /// (<see cref="AiLiveDay.BagItemSeen"/>); вор, погибший на деле (noble_action success = false, seq 3038526 KC0095), — правилом живого дня
    /// (<see cref="AiLiveDay.Noble"/>): путь KC0099 через его клетку (seq 3042984). Узлы 3352 → 3362, пути 3408 → 3410 из 3410.</para>
    /// <para>13.10, партии 20261014-replay (r2a и r2b — отрезки seq одного журнала): враждебность в масках ИИ — по <c>0x5deb24</c>
    /// (подраса и раса хозяев), а не «хозяин другой» (<see cref="AiTactics.PathHostile"/>): нейтралы разных подрас друг другу враги,
    /// и зона контроля чужого нейтрала закрывает проход (NM KC009b мимо KC0074, seq 246393 — путь пуст). Узлы r2a 1688 → 1728, пути
    /// 1722 → 1735; r2b — 677 → 713 и 713 → 727; probe72 без сдвига (нейтралы там друг к другу не ходят). Остаток r2a: «другая f» 8 при
    /// том же пути и KC00ab → (44,25) seq 986619 — мешок, взятый с соседней клетки (как 322118 в probe72); с 18.10 (мешок по equip_check)
    /// пути r2a 1735 → 1736 из 1736, r2b 727 → 730 из 730.</para>
    /// <para>19.10 вечер (r19b/m22): живой день берёт и <c>alliance_change</c> (<see cref="AiLiveDay.Alliance"/>) — в probe72 союзы ИИ
    /// меняются посреди дней, и зона контроля бывших союзников в масках пути была у харнесса прежней: узлы 3362 → 3410 из 3410.</para>
    /// </summary>
    [SkippableTheory]
    [InlineData("20260930-probe72", 3410, 3410, 3410)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — узлы 996 из 1072, пути 1006: расходятся поиски
    // отрядов, созданных событием (S092KC0039, KC003a, KC003e, KC0041): в живом дне у порта под этим id другой отряд или его нет (Зс30).
    [InlineData("20261019c-cov", 1072, 996, 1006, "passive-001.jsonl", "greenskins-human")]
    [InlineData("20261014-replay", 1736, 1728, 1736, "passive-002.jsonl", "ai-r2a-ai-orders-human", 431L, 1803463L)]
    [InlineData("20261014-replay", 730, 713, 730, "passive-002.jsonl", "ai-r2b-ai-orders-lvl10-human", 1803464L, 2332457L)]
    public void Expansion_MatchesJournal(string corpus, int wantSearches, int wantNodes, int wantPath, string file = "passive-001.jsonl",
                                         string stem = "", long fromSeq = 0, long toSeq = 0)
    {
        var replay = new LiveDayReplay(corpus, file, ["path_expand"], stem, fromSeq, toSeq);
        Skip.IfNot(replay.Exists, "нет корпуса");
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int shown = 0;
        foreach (var (e, live) in replay.Run())
        {
            if (e is not PathExpandEvent p || live is null) continue;
            var (nodes, path, detail) = Compare(live.Session(), p);
            string verdict = $"узлы {nodes}, путь {path}";
            Count(tally, verdict);
            if (verdict != "узлы да, путь да" && (Environment.GetEnvironmentVariable("D2_EXPAND_SHOW") is not null || shown++ < 12))
                output.WriteLine($"{verdict}: {detail}");
        }
        foreach (var (k, n) in tally) output.WriteLine($"{k}: {n}");
        Assert.Equal(wantSearches, tally.Values.Sum());
        Assert.Equal(wantNodes, tally.Where(t => t.Key.StartsWith("узлы да", StringComparison.Ordinal)).Sum(t => t.Value));
        Assert.Equal(wantPath, tally.Where(t => t.Key.EndsWith("путь да", StringComparison.Ordinal)).Sum(t => t.Value));
    }

    /// <summary>Партии синтетической карты port-synth-4-ai (20261016, b, c — зонд 7.3, лёгкая, очень трудная и третья), измерено 18.10
    /// (реестр П11): узлы и пути 948/948, 1007/1007, 1101/1101 — в том числе поиски TakeBag S143KC0034 (seq 3951790, 4130791), чей путь
    /// исполнения расходился в <c>ExecutionPaths_MatchJournal</c>: A* и его порядок равных верны, расходилось состояние живого дня того
    /// харнесса.</summary>
    [SkippableTheory, Trait("Category", "Synth")]
    [InlineData("20261016-port-synth", 948, 948, 948, "passive-002.jsonl", "port-synth-4-ai")]
    [InlineData("20261016b-port-synth", 1007, 1007, 1007, "passive-001.jsonl", "port-synth-4-ai", 2300059L, 4727542L)]
    [InlineData("20261016c-port-synth", 1101, 1101, 1101, "passive-001.jsonl", "port-synth-4-ai", 588357L, 3394036L)]
    // 19.10 вечер, карта 22 (П14: DefendParty DP1/DP2 и прочие ходы 11 дней): поисков 317, узлы 316, пути 317. Живой день теперь берёт и
    // разрыв союза (alliance_change; без него — узлы 311, пути 314: вор Нежити S143KC001b и KC0010 дня 10 у отрядов Кланов). Узлы одного
    // (S143KC0012, день 1, seq 4846425) — клетка (129,117) снесённого в тот же ход жезла S143RD0000: живой день снос жезла не повторяет.
    [InlineData("20261019b-port-synth", 317, 316, 317, "passive-001.jsonl", "port-synth-22-ai-diplo2", 4237300L, 0L)]
    // 20.10 (r20/batB): карта 23 — поисков 4967, узлы 4836, пути 4888: расходятся поиски у клеток, занятых в живом дне иначе (отряды и
    // жезлы чужих ходов того же дня).
    [InlineData("20261020-port-synth", 4967, 4836, 4888, "passive-001.jsonl", "port-synth-23-ai-long")]
    public void Expansion_MatchesJournal_Synth(string corpus, int wantSearches, int wantNodes, int wantPath, string file, string stem,
                                               long fromSeq = 0, long toSeq = 0) =>
        Expansion_MatchesJournal(corpus, wantSearches, wantNodes, wantPath, file, stem, fromSeq, toSeq);

    /// <summary>Поиск порта против строки журнала: узлы — «да» или класс первого расхождения; путь — «да», «мешок-цель», «обрезка», «нет».</summary>
    private static (string Nodes, string Path, string Detail) Compare(GameSession session, PathExpandEvent p)
    {
        if (p.Stack is null || !session.Map.Stacks.TryGetValue(p.Stack, out var stack) || p.Target is not { } target)
            return ("нет (нет отряда)", "нет", p.Stack ?? "");
        var map = session.Map;
        var opt = session.MoveOptionsOf(stack) with { ForbidWaterOnLand = p.ForbidWater != 0 };
        var masks = PathFinder.AiMasks(map, stack, (int)target.X, (int)target.Y, (int)p.Mode, opt, MovementRules.Vanilla,
                                       (a, b) => AiTactics.Allied(session, a, b), bags: p.A8 != 0, allStacks: p.A10 != 0,
                                       hostile: AiTactics.PathHostile(session, stack));
        var trace = new List<ExpandedNode>();
        var found = PathFinder.Find(map, stack, (int)target.X, (int)target.Y, opt, MovementRules.Vanilla, session.DayAllowance(stack),
                                    masks: masks, trace: trace);
        // Список движка обрезан после последней достижимой, если сработало «идти» = 0 (0x5cd984 → 0x5cd4f5); порт отдаёт точки целиком
        // и клетку обрыва (StackPath.CutAt).
        var port = found.Points.Take(found.CutAt is null ? found.Points.Count : found.Reachable + 1).Select(c => (c.X, c.Y)).ToList();
        var engine = p.Path.Select(c => ((int)c.X, (int)c.Y)).ToList();
        string path = port.SequenceEqual(engine) ? "да"
            : engine.Count == port.Count + 1 && engine.Take(port.Count).SequenceEqual(port) && masks.Allow.Contains(engine[^1]) ? "мешок-цель"
            : port.Count > engine.Count && port.Take(engine.Count).SequenceEqual(engine) ? "обрезка"
            : "нет";

        int first = -1;
        for (int i = 0; i < Math.Min(Math.Min(p.N, NodeCap), p.Nodes.Count); i++)
        {
            var j = p.Nodes[i];
            if (i >= trace.Count || trace[i].X != j.X || trace[i].Y != j.Y || trace[i].G != (float)(j.G ?? -1) || trace[i].F != (float)(j.F ?? -1))
            {
                first = i;
                break;
            }
        }
        if (first < 0 && !p.Cut && trace.Count != p.N) first = Math.Min(trace.Count, (int)p.N);
        string nodes = first < 0 ? "да"
            : first >= p.Nodes.Count || first >= trace.Count ? "нет (число)"
            : (float)(p.Nodes[first].F ?? -1) == trace[first].F ? "нет (та же f)" : "нет (другая f)";
        string at = first < 0 ? "" : $" | узел {first}: движок {Node(p.Nodes, first)} порт {(first < trace.Count ? $"{trace[first].X},{trace[first].Y} g {trace[first].G} f {trace[first].F}" : "—")}";
        string detail = $"{p.Stack} → ({target.X},{target.Y}) режим {p.Mode} a8 {p.A8} n {p.N}/{trace.Count} seq {p.Seq}{at}" +
                        $" | движок [{string.Join(" ", engine.Select(c => $"{c.Item1},{c.Item2}"))}] порт [{string.Join(" ", port.Select(c => $"{c.X},{c.Y}"))}]";
        return (nodes, path, detail);
    }

    private static string Node(IReadOnlyList<PathExpandNode> nodes, int i) =>
        i < nodes.Count ? $"{nodes[i].X},{nodes[i].Y} g {nodes[i].G} f {nodes[i].F}" : "—";

    private static void Count(IDictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
}
