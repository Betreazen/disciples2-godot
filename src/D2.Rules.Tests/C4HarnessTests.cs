using D2.Data.Corpus;
using D2.Data;
using D2.Data.Dbf;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Map;
using Xunit.Abstractions;
using KSg = Kaitai.Sg;

/// <summary>
/// Счётчики С4: по величине — «проверено / сошлось / разошлось», отдельно пропуски С ПРИЧИНОЙ и
/// первое расхождение по каждой величине. Общей цифры «похоже» здесь нет: пять величин критерия
/// считаются пятью независимыми счётчиками, и нулевой счётчик — тоже результат (тест на нём
/// падает).
/// </summary>
public sealed class C4Stats
{
    private readonly Dictionary<string, (int Ok, int Bad)> _counts = [];
    private readonly Dictionary<(string Key, string Reason), int> _skips = [];
    private readonly Dictionary<string, string> _first = [];

    public int Total => _counts.Values.Sum(v => v.Ok + v.Bad);
    public int Bad => _counts.Values.Sum(v => v.Bad);
    public int CheckedOf(string key) => _counts.TryGetValue(key, out var v) ? v.Ok + v.Bad : 0;
    public int BadOf(string key) => _counts.TryGetValue(key, out var v) ? v.Bad : 0;
    public int Skipped => _skips.Values.Sum();
    public string? FirstDiff(string key) => _first.GetValueOrDefault(key);

    /// <param name="detail">Контрольная точка, объект и ОБА значения — иначе расхождение
    /// нечитаемо и разбирать его придётся заново вручную.</param>
    public void Count(string key, bool ok, string detail)
    {
        var v = _counts.GetValueOrDefault(key);
        _counts[key] = ok ? (v.Ok + 1, v.Bad) : (v.Ok, v.Bad + 1);
        if (!ok && !_first.ContainsKey(key)) _first[key] = detail;
    }

    /// <summary>Пропуск невозможен без причины — она в сигнатуре.</summary>
    public void Skip(string key, string reason) =>
        _skips[(key, reason)] = _skips.GetValueOrDefault((key, reason)) + 1;

    public IEnumerable<string> Lines()
    {
        foreach (var (k, v) in _counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            yield return $"{k,-12} проверено={v.Ok + v.Bad,-6} сошлось={v.Ok,-6} разошлось={v.Bad}";
        foreach (var (k, n) in _skips.OrderBy(kv => kv.Key.Key, StringComparer.Ordinal))
            yield return $"{k.Key,-12} пропущено={n,-6} причина: {k.Reason}";
        foreach (var (k, d) in _first.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            yield return $"  первое расхождение {k}: {d}";
    }
}

/// <summary>Кого модель вообще берётся считать: игрок прогона, его отряды и его юниты. Всё
/// остальное — чужие ходы, найм и покупки — уходит в пропуск «политика ИИ» (Global Constraints
/// плана Ф5), а не в расхождения.</summary>
public sealed record C4Scope(string PlayerId, ISet<string> Stacks, ISet<string> Units)
{
    public static C4Scope Of(MapState start, string playerId)
    {
        var stacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var units = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in start.Stacks.Values)
        {
            if (!s.Owner.Equals(playerId, StringComparison.OrdinalIgnoreCase)) continue;
            stacks.Add(s.Id);
            foreach (var u in s.UnitIds) units.Add(u);
        }
        return new C4Scope(playerId, stacks, units);
    }
}

/// <summary>Снимок пяти величин с сохранения игры — эталонная сторона С4.</summary>
public static class C4Checkpoints
{
    public static MapCheckpoint FromSave(MapState save, string playerId)
    {
        if (!save.Players.TryGetValue(playerId, out var player))
            throw new InvalidDataException(
                $"в сохранении нет игрока {playerId} (есть: {string.Join(", ", save.Players.Keys)})");
        if (!save.Fog.TryGetValue(player.FogId, out var fog))
            throw new InvalidDataException($"в сохранении нет маски тумана {player.FogId} игрока {playerId}");

        return new MapCheckpoint(
            save.Turn,
            player.Bank,
            save.Stacks.Values.ToDictionary(s => s.Id, s => (s.X, s.Y, s.Move), StringComparer.OrdinalIgnoreCase),
            save.Units.Values.ToDictionary(u => u.Id, u => u.Xp, StringComparer.OrdinalIgnoreCase),
            save.Stacks.Values.ToDictionary(s => s.Id, s => s.UnitIds, StringComparer.OrdinalIgnoreCase),
            fog,
            save.Units.Values.ToDictionary(u => u.Id, u => u.TypeId, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>Сверка двух снимков. Вынесена из теста, чтобы её саму можно было проверить на
/// синтетике — без игры, без журнала и без сохранений.</summary>
public static class C4Compare
{
    public const string Bank = "казна";
    public const string Positions = "позиции";
    public const string Xp = "опыт";
    public const string Squads = "состав";
    public const string FogMask = "туман";

    /// <summary>Шестая, справочная величина: в критерий С4 не входит, но отличает «разошлась
    /// стоимость шага» от «разошлась модель хода» (точка отмены 4 плана Ф5).</summary>
    public const string MovePoints = "очки хода";

    public static readonly string[] Five = [Bank, Positions, Xp, Squads, FogMask];

    public static void Checkpoint(MapCheckpoint mine, MapCheckpoint theirs, C4Scope scope,
                                  C4Stats stat, Action<string> log)
    {
        int turn = mine.Turn;   // ход маршрута; день из сохранения печатает вызывающий
        stat.Count(Bank, mine.Bank == theirs.Bank, $"ход {turn}: наше {mine.Bank}, игра {theirs.Bank}");

        foreach (var (id, pos) in theirs.Stacks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!scope.Stacks.Contains(id))
            { stat.Skip(Positions, "чужой отряд (ИИ или нейтралы) — их ходы модель не считает"); continue; }
            if (!mine.Stacks.TryGetValue(id, out var ours))
            { stat.Skip(Positions, "отряда нет в модели — он появился по ходу партии"); continue; }

            stat.Count(Positions, (ours.X, ours.Y) == (pos.X, pos.Y),
                       $"ход {turn}, отряд {id}: наше ({ours.X},{ours.Y}), игра ({pos.X},{pos.Y})");
            stat.Count(MovePoints, ours.Move == pos.Move,
                       $"ход {turn}, отряд {id}: наше {ours.Move}, игра {pos.Move}");
        }
        foreach (var id in mine.Stacks.Keys.Where(id => scope.Stacks.Contains(id) && !theirs.Stacks.ContainsKey(id)))
            stat.Skip(Positions, $"отряда {id} нет в сохранении (погиб или распущен) — модель отряды не удаляет");

        // Опыт и состав — по отряду и месту в нём, а не по id юнита: движок пересоздаёт
        // стартового героя под выбранного лорда с новым id (см. MapCheckpoint.UnitType).
        foreach (var (id, squad) in theirs.Squads.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!scope.Stacks.Contains(id))
            {
                stat.Skip(Squads, "чужой отряд (ИИ или нейтралы) — его найм модель не считает");
                foreach (var _ in squad) stat.Skip(Xp, "юнит чужого игрока — политика ИИ не моделируется");
                continue;
            }
            if (!mine.Squads.TryGetValue(id, out var ours))
            { stat.Skip(Squads, "отряда нет в модели — он появился по ходу партии"); continue; }

            var ourTypes = ours.Select(u => TypeOf(mine, u)).ToList();
            var theirTypes = squad.Select(u => TypeOf(theirs, u)).ToList();
            stat.Count(Squads, ourTypes.SequenceEqual(theirTypes, StringComparer.OrdinalIgnoreCase),
                       $"ход {turn}, отряд {id}: наше [{string.Join(",", ours)}] (типы {string.Join(",", ourTypes)}), " +
                       $"игра [{string.Join(",", squad)}] (типы {string.Join(",", theirTypes)})");

            for (int slot = 0; slot < squad.Count; slot++)
            {
                if (slot >= ours.Count)
                { stat.Skip(Xp, "юнита нет в модели — он нанят по ходу партии"); continue; }
                int theirXp = theirs.UnitXp.GetValueOrDefault(squad[slot]);
                int ourXp = mine.UnitXp.GetValueOrDefault(ours[slot]);
                stat.Count(Xp, ourXp == theirXp,
                           $"ход {turn}, отряд {id}, место {slot} (наш {ours[slot]}, их {squad[slot]}): наше {ourXp}, игра {theirXp}");
            }
        }

        var (total, same, onlyOurs, onlyTheirs) = Fog.Compare(mine.Fog, theirs.Fog);
        log($"ход {turn}: туман {same}/{total} = {100.0 * same / total:F2} %, " +
            $"лишних у нас {onlyOurs}, недостающих {onlyTheirs}");
        stat.Count(FogMask, same == total,
                   $"ход {turn}: совпало {same} из {total}, лишних у нас {onlyOurs}, недостающих {onlyTheirs}");
    }

    private static string TypeOf(MapCheckpoint shot, string unitId) =>
        shot.UnitType is not null && shot.UnitType.TryGetValue(unitId, out var t) ? t : unitId;

    /// <summary>Ноль сверок — тоже результат, и он означает провал, а не «зелено».</summary>
    public static void Verify(C4Stats stat)
    {
        Assert.True(stat.Total > 0, "не проверено ни одной величины — сверять было нечего");
        foreach (var key in Five)
            Assert.True(stat.CheckedOf(key) > 0, $"нет ни одной проверки «{key}» — счётчик пуст");
    }
}

/// <summary>
/// С4 (ТЗ §7): одна и та же последовательность ходов в игре и у нас. Эталон — сохранения самой
/// игры из прогона задачи 2 (<c>corpus/&lt;каталог&gt;/save-NN.sg</c>), модель —
/// <see cref="MapSim"/> поверх стартовой карты и <c>route.json</c>. Сверяются пять величин
/// критерия, каждая отдельным счётчиком.
///
/// <para><b>Пока прогона нет, оба харнесса пропускаются</b> — игру запускает только пользователь.
/// Данные ищутся так же, как в <c>D2.Data.Tests/MapCorpusTests</c> и
/// <c>EconomyHarnessTests</c>: журнал — <c>MAP_JOURNAL</c> либо <c>corpus/**/map-*.jsonl</c>, а
/// рядом с ним <c>map-start.sg</c> (<c>MAP_START_SG</c>), <c>route.json</c> (<c>MAP_ROUTE</c>) и
/// сохранения <c>save-NN.sg</c>, которые кладёт <c>tools/corpus_c4_run.py</c>.</para>
///
/// <para><b>Сама сверка проверена без данных</b> — синтетическими тестами ниже:
/// счётчики считают, подложенное расхождение ловится с обоими значениями, пустой счётчик
/// роняет тест.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class C4HarnessTests(ITestOutputHelper output)
{
    [SkippableFact] public void C4_ReportsPerQuantityCounters()
    {
        var run = Setup();
        var (stat, played) = Play(run);

        foreach (var line in stat.Lines()) output.WriteLine(line);
        output.WriteLine($"контрольных точек сверено {played} из {run.Route.Checkpoints.Count} + финал (ход {run.LastTurn})");

        C4Compare.Verify(stat);
    }

    /// <summary>Порог фазы. Отдельным тестом, чтобы отчётный тест выше всегда печатал числа, даже
    /// когда порог не взят: молчащий упавший тест — это потерянное измерение.</summary>
    [SkippableFact] public void C4_FirstCheckpointMatchesOnEveryQuantity()
    {
        var run = Setup();
        var first = run.Route.Checkpoints.OrderBy(c => c).First();
        var (stat, played) = Play(run, only: first);
        Skip.If(played == 0, $"сохранения первой контрольной точки (ход {first}) в прогоне нет");

        foreach (var line in stat.Lines()) output.WriteLine(line);
        C4Compare.Verify(stat);

        var bad = C4Compare.Five.Where(k => stat.BadOf(k) > 0).ToList();
        Assert.True(bad.Count == 0,
            $"ход {first}: разошлись величины {string.Join(", ", bad)} — первое расхождение по каждой " +
            "напечатано выше; порядок разбора — шаг 6 задачи 9 плана Ф5");
    }

    /// <summary>
    /// Подбор геометрии обзора по первой контрольной точке — ранг 3 плана Ф5 (форма, радиус
    /// отряда, радиус города, момент раскрытия). Перебираются форма × радиус отряда 4..7 × радиус
    /// города 0..12 (от каждой клетки пятна, см. <see cref="Eyes"/>) × раскрытие на каждом шаге
    /// или только в конце хода. Печатаются лучшие пары по сумме «лишних + недостающих»; порог не
    /// проверяется — это измерение, а не критерий. Итог прогона 20260907-c4: единственная точная
    /// пара — <see cref="FogShape.SquareNoCorners"/> 5 / город 6 / на каждом шаге, 2304/2304.
    /// </summary>
    [SkippableFact] public void C4_FogGeometry_RankedByFirstCheckpoint()
    {
        var run = Setup();
        int first = run.Route.Checkpoints.Min();
        string save = SaveOf(run, first);
        Skip.IfNot(File.Exists(save), $"нет сохранения {Path.GetFileName(save)}");
        var theirs = C4Checkpoints.FromSave(MapLoader.Load(save), run.Scope.PlayerId);

        var eyesNow = run.Settings.Eyes ?? [];
        output.WriteLine($"GcityInf.SCOUT расы {run.Start.Players[run.Scope.PlayerId].RaceId}: " +
                         string.Join(",", run.Settings.Economy.Cities
                             .Where(c => c.RaceId.Equals(run.Start.Players[run.Scope.PlayerId].RaceId,
                                                         StringComparison.OrdinalIgnoreCase))
                             .OrderBy(c => c.Size).Select(c => $"размер {c.Size}={c.Scout}")));
        output.WriteLine($"клеток пятна городов {eyesNow.Count}; сейчас: {run.Settings.Shape}, " +
                         $"радиус города {eyesNow.Select(e => e.Radius).DefaultIfEmpty(0).Max()}");

        var rows = new List<(string Shape, int Stack, int City, string When, int Same, int OnlyOurs, int OnlyTheirs, MapCheckpoint Mine)>();
        foreach (var shape in new[] { FogShape.Circle, FogShape.Square, FogShape.Diamond, FogShape.SquareNoCorners })
            for (int rs = 4; rs <= 7; rs++)
                for (int rc = 0; rc <= 12; rc++)
                    foreach (var eachStep in new[] { true, false })
                    {
                        var eyes = eyesNow.Select(e => (e.X, e.Y, rc)).ToList();
                        var scout = (run.Settings.ScoutByType ?? new Dictionary<string, int>())
                            .ToDictionary(kv => kv.Key, _ => rs, StringComparer.OrdinalIgnoreCase);
                        var sim = new MapSim(run.Start, run.Settings with
                            { Shape = shape, ScoutByType = scout, Eyes = eyes, RevealEachStep = eachStep });
                        var mine = sim.Run(run.Route, [first])[0];
                        var (_, same, onlyOurs, onlyTheirs) = Fog.Compare(mine.Fog, theirs.Fog);
                        rows.Add((shape.ToString(), rs, rc, eachStep ? "шаг" : "конец хода",
                                  same, onlyOurs, onlyTheirs, mine));
                    }

        output.WriteLine("форма            отряд  город  когда        совпало  лишних  недостающих");
        foreach (var r in rows.OrderBy(r => r.OnlyOurs + r.OnlyTheirs).ThenBy(r => r.City).Take(12))
            output.WriteLine($"{r.Shape,-16} {r.Stack,5}  {r.City,5}  {r.When,-12} {r.Same,7}  {r.OnlyOurs,6}  {r.OnlyTheirs,11}");
        var best = rows.OrderBy(r => r.OnlyOurs + r.OnlyTheirs).First();
        output.WriteLine($"лучшая: {best.Shape}, радиус отряда {best.Stack}, радиус города {best.City} ({best.When}): " +
                         $"лишних {best.OnlyOurs}, недостающих {best.OnlyTheirs} из {theirs.Fog.Length}");
        // Односторонний признак: туман только накапливается, поэтому пара без единой недостающей
        // клетки — верхняя граница обзора; печатается самая тесная из них с картой лишних клеток.
        var tight = rows.Where(r => r.OnlyTheirs == 0).OrderBy(r => r.OnlyOurs).FirstOrDefault();
        if (tight.Mine is not null && tight.OnlyOurs > 0)
        {
            output.WriteLine($"без недостающих, меньше всего лишних: {tight.Shape}, отряд {tight.Stack}, " +
                             $"город {tight.City} ({tight.When}): лишних {tight.OnlyOurs}");
            PrintFogDiff(tight.Mine, theirs);
        }
        Assert.NotEmpty(rows);
    }

    /// <summary>Клетки пятна каждого города из <paramref name="eyes"/> (по <see cref="MapState.Plan"/>);
    /// города нет в плане — остаётся его угол.</summary>
    private static List<(int X, int Y)> Footprint(MapState map, IReadOnlyList<(int X, int Y, int Radius)> eyes)
    {
        var cells = new List<(int X, int Y)>();
        foreach (var (x, y, _) in eyes)
        {
            var id = map.Objects.Values.FirstOrDefault(o => o.Kind.Equals("FT", StringComparison.OrdinalIgnoreCase)
                                                             && o.X == x && o.Y == y)?.Id;
            var found = id is null ? [] : map.Plan
                .Where(kv => kv.Value.Contains(id, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            cells.AddRange(found.Count == 0 ? [(x, y)] : found);
        }
        return cells;
    }

    // ------------------------------------------------------------------ синтетика (без данных)

    [Fact] public void Sim_SpendsPoints_AccumulatesFog_AndTakesIncomeOncePerDay()
    {
        var map = Plain5x5(bank: new Bank(100, 0, 0, 0, 0, 0), move: 20);
        var route = new MapRoute("synthetic", "S001KC0001",
            Path: [(0, 0), (1, 0), (2, 0), (3, 0)],
            Turns: [(1, 2, 0), (2, 3, 0)],
            Checkpoints: [1, 2]);

        var sim = new MapSim(map, Settings(income: 40));
        var shots = sim.Run(route, route.Checkpoints);

        Assert.Equal(2, shots.Count);
        // Снимок «после хода N» — это начало дня N+1 (так сохраняет corpus_c4_run): доход дня 1
        // начислен на старте, доход дня 2 — на смене дня, очки хода уже полные
        // (MOVE лидера нет в таблице -> fallback = Move отряда). Ход 1: два шага по равнине.
        Assert.Equal((2, 0, 20), shots[0].Stacks["S001KC0001"]);
        Assert.Equal(new Bank(180, 0, 0, 0, 0, 0), shots[0].Bank);
        Assert.Equal((3, 0, 20), shots[1].Stacks["S001KC0001"]);
        Assert.Equal(new Bank(220, 0, 0, 0, 0, 0), shots[1].Bank);

        // Туман накоплен по всему пройденному пути, а не только вокруг последней клетки:
        // круг радиуса 1 (SCOUT неизвестен -> fallback) вокруг (0,0),(1,0),(2,0),(3,0) — это
        // вся строка y=0 (5 клеток) плюс (0..3, 1) (4 клетки).
        int open = 0;
        for (int y = 0; y < map.Size; y++)
            for (int x = 0; x < map.Size; x++)
                if (!shots[1].Fog[x, y]) open++;
        Assert.Equal(9, open);
        Assert.False(shots[1].Fog[0, 0]);            // старт остался открытым — накопление, а не пересчёт
    }

    [Fact] public void Sim_StopsWhenPointsRunOut_AndSaysSoWithReason()
    {
        var map = Plain5x5(bank: Bank.Zero, move: 3);          // хватает ровно на один шаг (2)
        var route = new MapRoute("synthetic", "S001KC0001",
            Path: [(0, 0), (1, 0), (2, 0)], Turns: [(1, 2, 0)], Checkpoints: [1]);

        var sim = new MapSim(map, Settings(income: 0));
        var shots = sim.Run(route, route.Checkpoints);

        Assert.Equal((1, 0, 3), shots[0].Stacks["S001KC0001"]);   // очки уже полные: смена дня
        Assert.Contains(sim.Notes, n => n.Quantity == "позиции" && n.Reason.Contains("очков не хватило"));
    }

    [Fact] public void Compare_CountsEveryQuantity_AndVerifyPasses()
    {
        var mine = Shot(10, gold: 500, x: 3, y: 4, move: 12, xp: 7, squad: ["U1", "U2"], open: [(3, 4)]);
        var stat = new C4Stats();

        C4Compare.Checkpoint(mine, mine, Scope(), stat, output.WriteLine);

        foreach (var line in stat.Lines()) output.WriteLine(line);
        // опыт считается по месту в отряде — два юнита, две проверки
        foreach (var key in C4Compare.Five) Assert.Equal(key == C4Compare.Xp ? 2 : 1, stat.CheckedOf(key));
        Assert.Equal(0, stat.Bad);
        C4Compare.Verify(stat);                       // пять непустых счётчиков — тест проходит
    }

    [Fact] public void Compare_CatchesPlantedMismatch_OnEveryQuantity()
    {
        var mine = Shot(10, gold: 500, x: 3, y: 4, move: 12, xp: 7, squad: ["U1", "U2"], open: [(3, 4)]);
        var theirs = Shot(10, gold: 480, x: 3, y: 5, move: 10, xp: 9, squad: ["U1"], open: [(0, 0)]);
        var stat = new C4Stats();

        C4Compare.Checkpoint(mine, theirs, Scope(), stat, output.WriteLine);
        foreach (var line in stat.Lines()) output.WriteLine(line);

        foreach (var key in C4Compare.Five)
        {
            Assert.Equal(1, stat.BadOf(key));
            Assert.NotNull(stat.FirstDiff(key));
            Assert.Contains("ход 10", stat.FirstDiff(key));
        }
        // Оба значения — в строке расхождения, иначе его пришлось бы разбирать заново руками.
        Assert.Contains("наше (3,4), игра (3,5)", stat.FirstDiff(C4Compare.Positions));
        Assert.Contains("наше 7, игра 9", stat.FirstDiff(C4Compare.Xp));
        Assert.Contains("[U1,U2]", stat.FirstDiff(C4Compare.Squads));
        Assert.Equal(1, stat.BadOf(C4Compare.MovePoints));   // справочная величина считается отдельно
    }

    [Fact] public void Compare_SkipsForeignStacksWithReason_InsteadOfCallingThemMismatches()
    {
        var mine = Shot(10, gold: 500, x: 3, y: 4, move: 12, xp: 7, squad: ["U1", "U2"], open: [(3, 4)]);
        var theirs = Shot(10, gold: 500, x: 3, y: 4, move: 12, xp: 7, squad: ["U1", "U2"], open: [(3, 4)],
                          extraStack: ("S001KC0009", 9, 9));
        var stat = new C4Stats();

        C4Compare.Checkpoint(mine, theirs, Scope(), stat, output.WriteLine);
        foreach (var line in stat.Lines()) output.WriteLine(line);

        Assert.Equal(0, stat.Bad);
        Assert.True(stat.Skipped > 0, "чужой отряд обязан попасть в пропуск с причиной");
        Assert.Contains(stat.Lines(), l => l.Contains("пропущено") && l.Contains("ИИ"));
    }

    [Fact] public void Verify_FailsWhenNothingWasChecked()
    {
        var ex = Assert.Throws<Xunit.Sdk.TrueException>(() => C4Compare.Verify(new C4Stats()));
        Assert.Contains("сверять было нечего", ex.Message);
    }

    // ------------------------------------------------------------------ синтетические данные

    private static C4Scope Scope() =>
        new("S001PL0001",
            new HashSet<string>(["S001KC0001"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["U1", "U2"], StringComparer.OrdinalIgnoreCase));

    private static MapCheckpoint Shot(int turn, int gold, int x, int y, int move, int xp,
                                      string[] squad, (int X, int Y)[] open,
                                      (string Id, int X, int Y)? extraStack = null)
    {
        var stacks = new Dictionary<string, (int X, int Y, int Move)>(StringComparer.OrdinalIgnoreCase)
            { ["S001KC0001"] = (x, y, move) };
        var squads = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            { ["S001KC0001"] = squad };
        if (extraStack is { } e)
        {
            stacks[e.Id] = (e.X, e.Y, 0);
            squads[e.Id] = ["U9"];
        }

        var fog = Fog.Empty(10);
        foreach (var (fx, fy) in open) fog[fx, fy] = false;

        return new MapCheckpoint(turn, new Bank(gold, 0, 0, 0, 0, 0), stacks,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["U1"] = xp },
            squads, fog,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["U1"] = "T1", ["U2"] = "T2", ["U9"] = "T9" });
    }

    /// <summary>Карта 5×5 из равнины, один игрок, один отряд в (0,0) — руками, без файлов.</summary>
    private static MapState Plain5x5(Bank bank, int move)
    {
        const int size = 5;
        var tiles = new Tile[size, size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tiles[x, y] = new Tile((byte)(((int)Ground.Plain << 3) | 1));

        var stack = new MapStack
        {
            Id = "S001KC0001", Owner = "S001PL0001", Subrace = "S001SR0001", LeaderId = "U1",
            LeaderAlive = true, X = 0, Y = 0, Move = move, Inside = "",
            UnitIds = ["U1"], Slots = [-1, -1, 0, -1, -1, -1],
        };
        var player = new MapPlayer
        {
            Id = "S001PL0001", RaceId = "G000RR0000", LordId = "G000LR0000", IsHuman = true,
            Bank = bank, FogId = "S001FG0001", BuildingsId = "S001BR0001", Buildings = [],
        };

        return new MapState
        {
            Size = size,
            Tiles = tiles,
            Stacks = new Dictionary<string, MapStack>(StringComparer.OrdinalIgnoreCase) { [stack.Id] = stack },
            Units = new Dictionary<string, MapUnit>(StringComparer.OrdinalIgnoreCase)
                { ["U1"] = new MapUnit { Id = "U1", TypeId = "G000UU0019", Level = 1, Hp = 100, Xp = 0 } },
            Players = new Dictionary<string, MapPlayer>(StringComparer.OrdinalIgnoreCase) { [player.Id] = player },
            Objects = new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase),
            Plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>(),
            Fog = new Dictionary<string, bool[,]>(StringComparer.OrdinalIgnoreCase),
            Turn = 1,
        };
    }

    /// <summary>Справочники из руки: доход расы — единственное слагаемое (жил на карте нет).</summary>
    private static MapSimSettings Settings(int income) => new(
        PlayerId: "S001PL0001",
        Economy: new EconomyRules(
            Races: new Dictionary<string, RaceInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["G000RR0000"] = new("G000RR0000", new Bank(income, 0, 0, 0, 0, 0), 5, 0, "", 0, true),
            },
            Cities: [], Buildings: new Dictionary<string, BuildingType>(),
            Hire: new Dictionary<string, UnitHire>(),
            IncomeBonusPercent: [0, 0, 0, 0], CrystalProfit: 50, SellRatio: 2, ConstructionUrgency: 1),
        Costs: MovementRules.Vanilla);

    // ------------------------------------------------------------------ поиск данных прогона

    private sealed record Run(string Dir, MapRoute Route, MapState Start, MapSimSettings Settings,
                              C4Scope Scope)
    {
        public int LastTurn => Route.Turns.Max(t => t.Turn);
    }

    /// <summary>Раскладка <c>tools/corpus_c4_run.py</c>: контрольная точка хода N —
    /// <c>save-NN.sg</c>, последний ход маршрута — <c>save-final.sg</c>; оба снимаются ПОСЛЕ
    /// конца хода, то есть в сохранении стоит день N+1.</summary>
    private static string SaveOf(Run run, int turn) =>
        Path.Combine(run.Dir, turn == run.LastTurn ? "save-final.sg" : $"save-{turn:D2}.sg");

    private (C4Stats Stat, int Played) Play(Run run, int? only = null)
    {
        var sim = new MapSim(run.Start, run.Settings);
        IReadOnlyList<int> wanted = only is null ? run.Route.Checkpoints.Append(run.LastTurn).Distinct().ToList()
                                                  : new[] { only.Value };
        var ours = sim.Run(run.Route, wanted);

        var stat = new C4Stats();
        int played = 0;
        foreach (var mine in ours)
        {
            string save = SaveOf(run, mine.Turn);
            if (!File.Exists(save))
            { stat.Skip("контрольная точка", $"нет сохранения {Path.GetFileName(save)} — ход не сыгран"); continue; }

            var theirs = C4Checkpoints.FromSave(MapLoader.Load(save), run.Scope.PlayerId);
            output.WriteLine($"ход {mine.Turn}: {Path.GetFileName(save)}, день в сохранении {theirs.Turn}" +
                             (theirs.Turn == mine.Turn + 1 ? "" : " — ВНИМАНИЕ: ожидался день ход+1"));
            int fogBadBefore = stat.BadOf(C4Compare.FogMask);
            C4Compare.Checkpoint(mine, theirs, run.Scope, stat, output.WriteLine);
            if (fogBadBefore == 0 && stat.BadOf(C4Compare.FogMask) == 1) PrintFogDiff(mine, theirs);
            played++;
        }

        foreach (var n in sim.Notes)
            stat.Skip(n.Quantity, n.Reason + (n.Turn > 0 ? $" (ход {n.Turn})" : ""));
        return (stat, played);
    }

    /// <summary>Карта расхождений тумана первой разошедшейся точки: где именно лишние и
    /// недостающие клетки — без неё числа «лишних 14, недостающих 166» не разобрать.</summary>
    private void PrintFogDiff(MapCheckpoint mine, MapCheckpoint theirs)
    {
        int size = mine.Fog.GetLength(0);
        output.WriteLine($"ход {mine.Turn}: карта тумана ('.' открыто у обоих, '#' скрыто у обоих, " +
                         "'+' лишняя у нас, '-' недостающая у нас)");
        for (int y = 0; y < size; y++)
        {
            var row = new char[size];
            for (int x = 0; x < size; x++)
            {
                bool ours = !mine.Fog[x, y], game = !theirs.Fog[x, y];
                row[x] = ours && game ? '.' : !ours && !game ? '#' : ours ? '+' : '-';
            }
            output.WriteLine($"  {y,2} {new string(row)}");
        }
    }

    private Run Setup()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");

        var journal = Journal();
        Skip.If(journal is null,
            "нет журнала карты (corpus/**/map-*.jsonl или MAP_JOURNAL) — прогон снимает пользователь, " +
            "задача 2 плана Ф5: python tools/corpus_c4_run.py --route route.json --calibrate");
        string dir = Path.GetDirectoryName(journal!)!;

        string routePath = Environment.GetEnvironmentVariable("MAP_ROUTE") is { Length: > 0 } r
            ? r : Path.Combine(dir, "route.json");
        Skip.IfNot(File.Exists(routePath), $"нет маршрута {routePath} (MAP_ROUTE задаёт путь)");

        string startPath = Environment.GetEnvironmentVariable("MAP_START_SG") is { Length: > 0 } s
            ? s : Path.Combine(dir, "map-start.sg");
        Skip.IfNot(File.Exists(startPath), $"нет стартовой карты {startPath} (MAP_START_SG задаёт путь)");

        var route = RouteLoader.Load(routePath);
        var start = MapLoader.Load(startPath);
        Skip.If(route.Checkpoints.Count == 0, "в маршруте нет контрольных точек");
        var planned = route.Path;
        route = WithJournalPath(route, journal!, start.Stacks[route.StackId].Owner);
        int firstDiff = Enumerable.Range(0, Math.Min(planned.Count, route.Path.Count))
            .FirstOrDefault(i => planned[i] != route.Path[i], -1);
        output.WriteLine($"путь: из журнала {route.Path.Count} клеток; route.json {planned.Count} клеток" +
                         (firstDiff < 0 && planned.Count == route.Path.Count ? ", совпадают"
                          : $", расходятся с клетки {firstDiff} (журнал {route.Path.ElementAtOrDefault(Math.Max(firstDiff, 0))}, " +
                            $"route.json {planned.ElementAtOrDefault(Math.Max(firstDiff, 0))})"));

        if (!start.Stacks.TryGetValue(route.StackId, out var stack))
            throw new InvalidDataException($"отряда маршрута {route.StackId} нет в {startPath}");
        var player = start.Players[stack.Owner];

        var rules = EconomyLoader.Load(GamePaths.Globals);
        var settings = new MapSimSettings(
            PlayerId: player.Id,
            Economy: rules,
            Costs: MovementRules.Vanilla,
            Difficulty: DifficultyOf(startPath),
            MoveByType: Column("MOVE"),
            ScoutByType: Column("SCOUT"),
            Eyes: Eyes(startPath, start, player, rules));

        output.WriteLine($"прогон {dir}");
        output.WriteLine($"карта {Path.GetFileName(startPath)} ({start.Size}x{start.Size}), " +
                         $"маршрут {Path.GetFileName(routePath)}: ходов {route.Turns.Count}, " +
                         $"контрольные точки {string.Join(",", route.Checkpoints)}");
        output.WriteLine($"игрок {player.Id} (раса {player.RaceId}), отряд {route.StackId}, " +
                         $"сложность {settings.Difficulty}");

        return new Run(dir, route, start, settings, C4Scope.Of(start, player.Id));
    }

    /// <summary>
    /// Путь отряда — из журнала, а не из <c>route.json</c>: путь ищет сам движок, и среди равных
    /// по цене он выбирает не так, как Дейкстра <c>scenario_c4.py</c> (день 2 прогона 20260907-c4:
    /// игра шла (11,10),(12,10),(13,10),(14,10) — <c>stack_move seq 111692..111698</c>, а
    /// <c>route.json</c> — (11,11),(12,12),(13,11),(14,10)). Концы ходов совпадают, но туман
    /// накапливается вдоль пути, и сверять его с чужой прогулкой нельзя.
    /// <para>Раскладка <c>stack_move</c> внутри дня (ранг 2, по журналу): клетки пути по порядку
    /// с полным <c>move_left</c> (предпросмотр), затем возврат в клетку начала дня, затем конечная
    /// клетка с настоящим остатком; граница дня — событие <c>income</c> игрока. Путь дня — клетки
    /// до первого возврата в клетку начала дня.</para>
    /// </summary>
    private static MapRoute WithJournalPath(MapRoute route, string journal, string playerId)
    {
        var path = new List<(int X, int Y)> { route.Path[0] };
        var day = new List<(int X, int Y)>();
        var dayStart = route.Path[0];
        bool closed = false;
        foreach (var e in JournalCache.Read(journal))
        {
            if (e is IncomeEvent income && income.Player.Equals(playerId, StringComparison.OrdinalIgnoreCase))
            {
                path.AddRange(day);
                day.Clear();
                dayStart = path[^1];
                closed = false;
                continue;
            }
            if (closed || e is not StackMoveEvent m || !m.Ret ||
                !m.Stack.Equals(route.StackId, StringComparison.OrdinalIgnoreCase)) continue;
            var cell = ((int)m.X, (int)m.Y);
            if (cell == dayStart) { closed = true; continue; }
            day.Add(cell);
        }
        path.AddRange(day);
        return route with { Path = path };
    }

    private static string? Journal()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAP_JOURNAL");
        if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
        var corpus = CorpusFiles.Dir;   // D2_CORPUS_DIR (рабочие копии ../dg-wt-*: корпуса в них нет), иначе corpus/ репозитория
        if (!Directory.Exists(corpus)) return null;
        return Directory.GetFiles(corpus, "map-*.jsonl", SearchOption.AllDirectories)
            .Where(f => !(Path.GetFileName(Path.GetDirectoryName(f)) ?? "").StartsWith('_'))
            .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Колонка <c>Globals/Gunits.dbf</c> по типу юнита (<c>MOVE</c>, <c>SCOUT</c>).</summary>
    private static IReadOnlyDictionary<string, int> Column(string name)
    {
        var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (name.Length == 0 || !GamePaths.IsAvailable) return byType;
        string path = Directory.EnumerateFiles(GamePaths.Globals)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "Gunits.dbf",
                                               StringComparison.OrdinalIgnoreCase)) ?? "";
        if (path.Length == 0) return byType;
        foreach (var row in DbfTable.Read(path).Rows) byType[row.GetString("UNIT_ID")] = row.GetInt(name);
        return byType;
    }

    /// <summary>
    /// Города игрока как неподвижные источники обзора. Владельца города в <see cref="MapObject"/>
    /// нет, поэтому он берётся из сырой записи <c>City</c> — как в <c>FogTests.CitiesOf</c>.
    /// <para><b>Ранг 2 (прогон 20260907-c4):</b> обзор излучает КАЖДАЯ клетка пятна города, радиус —
    /// <c>GcityInf.SCOUT</c> по размеру города (сторона пятна из <see cref="MapState.Plan"/>; у
    /// столицы 5×5 Империи это 6). Радиус 5 оставлял 14 недостающих клеток, 7 — 16 лишних, обзор из
    /// одного угла пятна требовал радиуса 10 (<c>C4_FogGeometry_RankedByFirstCheckpoint</c>).
    /// Размер в таблице не найден — наименьший <c>SCOUT</c> расы, чтобы лишняя клетка оставалась
    /// сигналом.</para>
    /// </summary>
    private static IReadOnlyList<(int X, int Y, int Radius)> Eyes(string sgPath, MapState start,
                                                                  MapPlayer player, EconomyRules rules)
    {
        var scoutBySize = rules.Cities
            .Where(c => c.RaceId.Equals(player.RaceId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(c => c.Size, c => c.Scout);
        if (scoutBySize.Count == 0) return [];

        var eyes = new List<(int X, int Y, int Radius)>();
        foreach (var o in SgFile.Open(sgPath).Raw.Objects)
        {
            var city = o.Data switch
            {
                KSg.Capital c => c.City,
                KSg.MidVillage v => v.City,
                _ => null,
            };
            if (city is null || !city.Owner.String.Equals(player.Id, StringComparison.OrdinalIgnoreCase)) continue;

            var cells = Footprint(start, [((int)city.Position.X.Value, (int)city.Position.Y.Value, 0)]);
            int size = (int)Math.Round(Math.Sqrt(cells.Count));
            int radius = scoutBySize.TryGetValue(size, out var r) ? r : scoutBySize.Values.Min();
            eyes.AddRange(cells.Select(c => (c.X, c.Y, radius)));
        }
        return eyes;
    }

    /// <summary>Сложность партии — <c>ScenarioInfo.DIFFGAME</c>; в <see cref="MapState"/> её нет.</summary>
    private static Difficulty DifficultyOf(string sgPath) =>
        (Difficulty)(int)SgFile.Open(sgPath).Raw.Objects.Select(o => o.Data)
            .OfType<KSg.ScenarioInfo>().Single().Diffgame.Value;
}
