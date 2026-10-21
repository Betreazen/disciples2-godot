using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Map;
using D2.Rules.Tests;
using Xunit.Abstractions;

/// <summary>
/// Харнесс правила роста земли: модель <see cref="LandModel"/> против события <c>terrain_day</c>
/// живого прогона. Сверяется самое строгое, что модель может утверждать, — МНОЖЕСТВО клеток
/// на каждый день, а не только их число:
/// <list type="bullet">
/// <item><b>число</b> занятых клеток должно совпасть точно;</item>
/// <item><b>«уверенно»</b> (<see cref="LandModel.Certain"/>) обязано целиком лежать внутри
/// множества журнала — ни одной лишней клетки у нас;</item>
/// <item><b>журнал</b> обязан целиком лежать внутри <see cref="LandModel.Possible"/> — ни одной
/// клетки за пределами кольца в работе;</item>
/// <item><b>порядок колец</b>: ни одна клетка кольца d+1 не занята раньше, чем занято всё
/// кольцо d (квадраты жил не в счёт — они кольцевой порядок и нарушают);</item>
/// <item><b>грунт</b>: каждая занятая клетка проходима.</item>
/// </list>
/// Клетки кольца в работе модель не различает — это ПРОПУСК С ПРИЧИНОЙ, а не тихое «сошлось»:
/// порядок внутри кольца ранга 3 (см. описание <see cref="LandModel"/>). Нулевой счётчик роняет
/// тест.
/// </summary>
[Trait("Category", "Corpus")]
public class LandHarnessTests(ITestOutputHelper output)
{
    /// <summary>Ландмарки с <c>GLmark.MOUNTAIN = T</c> — их клетки рост земли отвергает (ai-q25, 4/0).</summary>
    private static readonly Lazy<IReadOnlySet<string>> Mountains =
        new(() => D2.Data.Map.EconomyLoader.LoadMountainLandmarks(GamePaths.Globals));

    private const string Count = "число клеток";
    private const string Ours = "уверенные клетки";
    private const string Theirs = "клетки журнала";
    private const string Rings = "порядок колец";
    private const string Ground = "грунт";
    private const string NodeDay = "день захвата жилы";

    [SkippableFact] public void Land_MatchesTerrainDayOfTheRun()
    {
        var run = Setup();
        var stat = new C4Stats();
        // Из проверки порядка колец выведены клетки, которые рост не занимал: стартовое пятно
        // столицы (её квадрат 5x5 сам по себе кольцевой порядок и нарушает — углы стоят на
        // кольце 4, а клетка (4,7) кольца 3 ещё ничья) и квадраты жил.
        var notGrown = new HashSet<(int X, int Y)>(run.Land.NodeCells);
        notGrown.UnionWith(run.Land.Certain(1));

        foreach (var (day, cells) in run.Days.OrderBy(kv => kv.Key))
        {
            var certain = run.Land.Certain(day);
            stat.Count(Count, run.Land.OwnedCells(day) == cells.Count,
                       $"день {day}: наше {run.Land.OwnedCells(day)}, игра {cells.Count}");

            var extra = certain.Except(cells).ToList();
            stat.Count(Ours, extra.Count == 0,
                       $"день {day}: у нас лишних {extra.Count}, первая {First(extra)}");

            var outside = cells.Except(run.Land.Possible(day)).ToList();
            stat.Count(Theirs, outside.Count == 0,
                       $"день {day}: игра заняла вне нашего кольца {outside.Count}, первая {First(outside)}");


            var bad = cells.Where(c => !run.Start.At(c.X, c.Y).Passable).ToList();
            stat.Count(Ground, bad.Count == 0,
                       $"день {day}: занято непроходимых {bad.Count}, первая {First(bad)}");

            var (violations, frontier) = RingOrder(run, cells, notGrown);
            stat.Count(Rings, violations == 0,
                       $"день {day}: занято {violations} клеток дальше кольца {frontier}, а оно не закрыто");
        }

        foreach (var line in stat.Lines()) output.WriteLine(line);
        foreach (var key in new[] { Count, Ours, Theirs, Rings, Ground })
            Assert.True(stat.CheckedOf(key) > 0, $"нет ни одной проверки «{key}» — счётчик пуст");
        Assert.Equal(0, stat.Bad);
    }

    /// <summary>
    /// День, с которого квадрат жилы стоит на своей земле, — по модели и по журналу. Отдельным
    /// тестом: это единственная величина ранга 3, попадающая в казну, и сутки ошибки здесь стоят
    /// 50 золота на каждой контрольной точке С4 после этого дня.
    /// </summary>
    [SkippableFact] public void Land_NodeCaptureDayMatchesTheJournal()
    {
        var run = Setup();
        var nodes = run.Start.Objects.Values
            .Where(o => o.Kind.Equals("CR", StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
        Skip.If(nodes.Count == 0, "на карте прогона нет ни одной жилы");

        var stat = new C4Stats();
        foreach (var node in nodes)
        {
            int? theirs = run.Days.OrderBy(kv => kv.Key)
                .Where(kv => kv.Value.Contains((node.X, node.Y)))
                .Select(kv => (int?)kv.Key).FirstOrDefault();
            int? ours = run.Land.NodeCaptureDay((node.X, node.Y));
            output.WriteLine($"жила {node.Id} ({node.X},{node.Y}): наше {Show(ours)}, игра {Show(theirs)}");
            if (theirs is null && ours is null)
            { stat.Skip(NodeDay, "жила не досталась игроку ни в модели, ни в прогоне"); continue; }
            stat.Count(NodeDay, ours == theirs, $"жила {node.Id}: наше {Show(ours)}, игра {Show(theirs)}");
        }

        foreach (var line in stat.Lines()) output.WriteLine(line);
        Assert.True(stat.CheckedOf(NodeDay) > 0, "ни одной жилы не проверено — сверять было нечего");
        Assert.Equal(0, stat.Bad);
    }

    /// <summary>
    /// Нарушения порядка колец: берём ближайшее кольцо, в котором есть НЕзанятая достижимая
    /// клетка, и считаем занятые клетки дальше него. <paramref name="notGrown"/> — то, что рост
    /// не занимал (пятно столицы и квадраты жил): и то и другое кольцевой порядок нарушает.
    /// </summary>
    private static (int Violations, int Frontier) RingOrder(
        Run run, IReadOnlySet<(int X, int Y)> cells, IReadOnlySet<(int X, int Y)> notGrown)
    {
        int frontier = int.MaxValue;
        foreach (var c in run.Reachable)
            if (!cells.Contains(c))
                frontier = Math.Min(frontier, LandModel.Distance(run.Land.Centre, c));
        if (frontier == int.MaxValue) return (0, frontier);

        int violations = cells.Count(c => !notGrown.Contains(c) &&
                                          LandModel.Distance(run.Land.Centre, c) > frontier);
        return (violations, frontier);
    }

    private static string First<T>(IReadOnlyList<T> list) => list.Count == 0 ? "-" : $"{list[0]}";

    private static string Show(int? day) => day?.ToString() ?? "никогда";

    // ------------------------------------------------------------------ поиск данных прогона

    private sealed record Run(MapState Start, LandModel Land,
                              IReadOnlyDictionary<int, IReadOnlySet<(int X, int Y)>> Days,
                              IReadOnlySet<(int X, int Y)> Reachable);

    /// <summary>Журнал с событиями <c>terrain_day</c> и лежащая рядом <c>map-start.sg</c>.
    /// Прогон снимает пользователь; без него тест пропускается, а не «зеленеет».</summary>
    private Run Setup()
    {
        var journal = Journals().FirstOrDefault(HasTerrainDay);
        Skip.If(journal is null,
            "нет журнала с событием terrain_day (corpus/**/map-*.jsonl) — прогон снимает пользователь");
        string dir = Path.GetDirectoryName(journal!)!;
        string startPath = Path.Combine(dir, "map-start.sg");
        Skip.IfNot(File.Exists(startPath), $"нет стартовой карты {startPath}");

        var events = JournalCache.Read(journal!).OfType<TerrainDayEvent>().ToList();
        // Игрок прогона — тот, у кого земли на старте меньше: нейтралу принадлежит вся остальная
        // карта, и его земля только убывает.
        string player = events.GroupBy(e => e.Player, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.OrderBy(e => e.Turn).First().Owned).First().Key;
        var days = events.Where(e => e.Player.Equals(player, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => (int)e.Turn)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<(int X, int Y)>)g.Last().Cells);
        int terrain = (int)events.First(e => e.Player.Equals(player, StringComparison.OrdinalIgnoreCase)).Terrain;

        var start = MapLoader.Load(startPath);
        var land = LandModel.Build(start, terrain, player);
        output.WriteLine($"прогон {Path.GetFileName(dir)}: игрок {player}, террейн {terrain}, " +
                         $"дней {days.Count} ({days.Keys.Min()}..{days.Keys.Max()}), центр {land.Centre}, " +
                         $"стартовое пятно {land.StartCells}, достижимо {land.Reachable}");

        return new Run(start, land, days, land.Possible(land.MaxDay));
    }

    // ============================================ задача 7: несколько источников и приобретение

    private const string Owned = "land_owned";
    private const string OwnedOther = "land_owned_other";
    private const string Capture = "capture_budget";
    private const string Placement = "rod_placement";
    private const string AcquireOther = "acquire_other";

    /// <summary>Карты плана (задача 7): только на них счётчики блокируют. Всё остальное
    /// (большие партии и попутные карты прогонов) идёт в <c>*_other</c> отдельными строками —
    /// там чужие источники ходят внутри хода, а автосохранение отстаёт от дневного тика
    /// по-разному (план Ф7 §1.4, §2.0).</summary>
    private static readonly string[] Named = ["f7-m6-rods", "f7-m7-land", "f7-m12-capture"];

    /// <summary>Одно сохранение цепочки: карта, её объекты и клетки своей земли игрока.</summary>
    private sealed record Shot(string File, int Day, int Terrain, MapState Map,
                               IReadOnlyList<MapSite> Sites, IReadOnlySet<(int X, int Y)> Owned);

    /// <summary>Цепочка сохранений как наблюдение: день считается от первого хода цепочки.</summary>
    private static List<Shot> Shots(IReadOnlyList<string> files, MapState first, int terrain, int day0)
    {
        var shots = new List<Shot>();
        foreach (var f in files)
        {
            var map = f == files[0] ? first : SaveMaps.Map(f);
            shots.Add(new Shot(Path.GetFileName(f), map.Turn - day0 + 1, terrain, map,
                               SaveMaps.Sites(f), CellsOf(map, terrain)));
        }
        return shots;
    }

    /// <summary>
    /// Задача 7: число клеток земли игрока на каждый день против цепочек сохранений. Наблюдение
    /// берётся из САМИХ сохранений (террейн каждой клетки), а не из <c>terrain_day</c>: журнал
    /// прогона 1 (44 хода <c>m6-rods</c>, 8 <c>m7-land</c>) затёрт следующим прогоном в тот же
    /// каталог (та же ошибка <c>passive_run.write_manifest</c>, что описана в
    /// <see cref="CorpusFiles"/>), а сохранения целы и несут ту же величину точнее — поимённо по
    /// клеткам, а не числом.
    /// <para>Модель ведётся НАКОПИТЕЛЬНО от первого сохранения цепочки: источники и дни их
    /// появления читаются из всей цепочки, дальше <see cref="LandModel"/> считает сам. Цепочка
    /// рвётся там, где ход не вырос (перезагрузка карты).</para>
    /// </summary>
    [SkippableFact] public void Land_MatchesSourceQuotasOnSaveChains()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Dir.Length > 0 && Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var rules = EconomyLoader.Load(GamePaths.Globals);
        var quotas = EconomyLoader.LoadLand(GamePaths.Globals);
        var c = new Counters();
        c.Note($"GVars: T_CAPITAL={quotas.Capital}, T_CITY={string.Join("/", quotas.City)}, " +
               $"ROD_RANGE={quotas.RodRange}, T_CAPTURE={quotas.CapturePercent}");
        c.Note("land_owned_other / acquire_other НЕ блокируют — три причины, все вне правила роста: "
             + "(1) автосохранение больших партий отстаёт от дневного тика по-разному, и в одно окно "
             + "попадают и установка жезла, и его первый суточный обход (наше 3, игра 5); (2) чужие "
             + "источники перекрашивают землю игрока внутри хода; (3) разовый "
             + "обход отделяется от чужих радиусом, и там, где деревня стоит рядом со столицей, в него "
             + "попадают клетки суточного обхода столицы.");

        foreach (var (name, files) in SaveChains.All()) Chain(name, files, rules, quotas, c);

        c.Print(output, "задача 7 — рост земли по каждому источнику (цепочки сохранений)");
        // Блокирующие счётчики — только на картах плана (Named): корпус с ними потерян 18.09.2026, а в больших
        // партиях те же величины идут строками *_other (чужие источники внутри хода, отставание автосохранения).
        c.RequireOrSkip(
            (Owned, "карты плана f7-m6-rods / f7-m7-land / f7-m12-capture, сыгранные с хода 1: земля человека по дням " +
                    "при одной столице, без чужих источников рядом"),
            (Capture, "карта плана f7-m7-land или f7-m12-capture: человек захватывает деревню (разовый обход T_CAPTURE)"),
            (Placement, "карта плана f7-m6-rods: человек ставит жезлы вдали от прочих источников земли"));
    }

    /// <summary>Одна цепочка: собрать источники, прогнать модель, сверить число клеток по дням,
    /// а в дни приобретения — разовый обход поимённо по расстояниям.</summary>
    private void Chain(string name, IReadOnlyList<string> files, EconomyRules rules, LandQuotas quotas,
                       Counters c)
    {
        bool named = Named.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
        string key = named ? Owned : OwnedOther;
        var first = MapLoader.Load(files[0]);
        var me = first.Players.Values.FirstOrDefault(p => p.IsHuman);
        if (me is null || !rules.Races.TryGetValue(me.RaceId, out var race))
        { c.Skip(key, $"{name}: в сохранении нет человека или его расы в Grace.dbf"); return; }
        int day0 = first.Turn;
        var shots = Shots(files, first, Economy.TerrainOfRace(race.RaceType), day0);
        if (shots[0].Owned.Count == 0) { c.Skip(key, $"{name}: у человека нет своей земли"); return; }

        var sources = SourcesOf(shots, me.Id, quotas);
        // Свои укрепления и жезлы движок отвергает наравне с чужими; клетки самого источника
        // пропускает модель (LandModel, «Приём клетки»).
        var blocked = LandModel.BlockedCells(shots[0].Map, shots[0].Sites, Mountains.Value);

        var land = LandModel.BuildSources(shots[0].Map, shots[0].Terrain, sources, quotas, me.Id, blocked);
        c.Note($"{name}: дней {shots[0].Day}..{shots[^1].Day}, источников {sources.Count} " +
               $"({string.Join(", ", sources.Select(s => $"{s.Id}q{s.Quota}с{s.Since}" +
                                                        (s.Until == int.MaxValue ? "" : $"до{s.Until}") +
                                                        string.Concat((s.Steps ?? []).Select(t => $"/{t.Quota}с{t.From}"))))})");

        // Первый ход партии — тоже наблюдение, а не «старт как есть»: в него уже вошло то, что
        // игрок успел сделать до первого сохранения. Так на m7-land видна награда за захват
        // (25 + 16 + 15 = 56), а на прогоне 2 m6-rods — три установки жезла (25 + 3 × 3 = 34).
        // Считать его можно только там, где известна СЦЕНАРНАЯ стартовая земля: на картах плана это
        // пятно столицы 5×5 (terrain_day хода 1 = 25, run1 §2.1–2.2), а у прочих сценариев она
        // нарисована в файле сценария, которого в корпусе нет.
        if (named && day0 == 1) DayOne(name, shots[0], sources, quotas, blocked, c);
        else c.Skip(key, day0 == 1
            ? $"{name}: сценарная стартовая земля неизвестна — первый день не сверяется"
            : $"{name}: цепочка начата с хода {day0} — что успел игрок до неё, из сохранений не видно");
        for (int k = 1; k < shots.Count; k++)
        {
            int ours = land.OwnedCells(shots[k].Day), theirs = shots[k].Owned.Count;
            c.Count(key, ours == theirs,
                    $"{name} {shots[k].File} день {shots[k].Day}: наше {ours}, игра {theirs}");
        }
        for (int k = 1; k < shots.Count; k++)
            foreach (var s in sources.Where(s => s.Since == shots[k].Day && s.Since > 0))
                Acquisition(name, named, s, shots[k - 1].Owned, shots[k], quotas, blocked, c);
    }

    /// <summary>Первый день цепочки: столица даёт своё пятно, а каждый прочий источник, уже наш к
    /// этому дню, — своё пятно и разовый обход. Сверяется и итог дня, и разбор по источникам.</summary>
    private void DayOne(string name, Shot shot, IReadOnlyList<LandSource> sources,
                        LandQuotas quotas, IReadOnlySet<(int X, int Y)> blocked, Counters c)
    {
        var running = new HashSet<(int X, int Y)>(sources[0].Own);
        foreach (var s in sources.Skip(1).Where(s => s.Since == 0))
        {
            var ours = Acquisition(name, true, s, running, shot, quotas, blocked, c);
            running.UnionWith(ours);
        }
        c.Count(Owned, running.Count == shot.Owned.Count,
                $"{name} {shot.File} день {shot.Day} (старт): наше {running.Count}, игра {shot.Owned.Count}");
    }

    /// <summary>Разовый обход приобретения: сверяются ЧИСЛО клеток и их расстояния от центра
    /// источника (сами клетки — ранг 3, порядок внутри кольца). Наблюдение отделяется от обходов
    /// прочих источников радиусом самой дальней предсказанной клетки.</summary>
    private IReadOnlyList<(int X, int Y)> Acquisition(
        string name, bool named, LandSource s, IReadOnlySet<(int X, int Y)> before, Shot after,
        LandQuotas quotas, IReadOnlySet<(int X, int Y)> blocked, Counters c)
    {
        string key = !named ? AcquireOther : s.Quota == quotas.RodRange ? Placement : Capture;
        var ours = LandModel.AcquisitionCells(after.Map, before, s, quotas, blocked);
        int reach = ours.Count == 0 ? 0 : ours.Max(x => LandModel.Distance(s.Centre, x));
        var theirs = after.Owned.Except(before).Where(x => LandModel.Distance(s.Centre, x) <= reach).ToList();
        c.Count(key, ours.Count == theirs.Count,
                $"{name} {s.Id} день {after.Day}: наше {ours.Count} клеток, игра {theirs.Count}");
        c.Count(key, Histogram(s.Centre, ours) == Histogram(s.Centre, theirs),
                $"{name} {s.Id} день {after.Day}: кольца наше {Histogram(s.Centre, ours)}, " +
                $"игра {Histogram(s.Centre, theirs)}");
        if (named)
            c.Note($"  приобретение {name} {s.Id} день {after.Day} (квота {s.Quota}): клеток " +
                   $"{theirs.Count}, сумма расстояний {theirs.Sum(x => LandModel.Distance(s.Centre, x))}, " +
                   $"кольца {Histogram(s.Centre, theirs)}; модель «T_CAPTURE — очки расстояния» дала бы " +
                   $"{Budget(after.Map, before, s, quotas)}");
        return ours;
    }

    /// <summary>Отвергнутая модель плана §1.6: <c>T_CAPTURE</c> = 100 очков манхэттенского
    /// расстояния, клетки от дешёвых к дорогим. Печатается рядом с наблюдением, чтобы разница
    /// была видна числом, а не на слово.</summary>
    private static string Budget(MapState map, IReadOnlySet<(int X, int Y)> owned, LandSource s,
                                 LandQuotas quotas)
    {
        var free = new List<int>();
        for (int x = 0; x < map.Size; x++)
            for (int y = 0; y < map.Size; y++)
                if (map.At(x, y).Passable && !owned.Contains((x, y)))
                    free.Add(LandModel.Distance(s.Centre, (x, y)));
        free.Sort();
        int spent = 0, cells = 0;
        foreach (int d in free)
        {
            if (spent + d > quotas.CapturePercent) break;
            spent += d;
            cells++;
        }
        return $"{cells} клеток, сумма {spent}";
    }

    private static string Histogram((int X, int Y) centre, IEnumerable<(int X, int Y)> cells) =>
        string.Join(" ", cells.GroupBy(x => LandModel.Distance(centre, x)).OrderBy(g => g.Key)
                             .Select(g => $"d{g.Key}:{g.Count()}"));

    /// <summary>Источники всей цепочки: свои укрепления и свои жезлы с днём появления
    /// (<see cref="LandSource.Since"/>), днём исчезновения (<see cref="LandSource.Until"/>) и ступенями
    /// квоты (<see cref="LandSource.Steps"/>). Столица идёт первой — к ней привязан центр модели.</summary>
    private static List<LandSource> SourcesOf(List<Shot> shots, string playerId, LandQuotas quotas)
    {
        var proto = new Dictionary<string, LandSource>(StringComparer.OrdinalIgnoreCase);
        var firstDay = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastDay = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var steps = new Dictionary<string, List<(int From, int Quota)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var shot in shots)
            foreach (var s in Sources(shot, playerId, quotas))
            {
                if (!proto.TryAdd(s.Id, s)) Step(steps, proto[s.Id], s, shot.Day);
                firstDay.TryAdd(s.Id, shot.Day);
                lastDay[s.Id] = shot.Day;
            }
        int end = shots[^1].Day;
        return [.. proto.Values
            .Select(s => s with
            {
                Since = firstDay[s.Id] == shots[0].Day ? 0 : firstDay[s.Id],
                Until = lastDay[s.Id] >= end ? int.MaxValue : lastDay[s.Id] + 1,
                Steps = steps.TryGetValue(s.Id, out var list) ? list : null,
            })
            // Столица первой: к первому источнику привязано правило жилы. Её и видно по клеткам —
            // 25 против 16 у деревни и 1 у жезла (CMidgardPlan, ранг 1).
            .OrderByDescending(s => s.Own.Count)
            .ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>§3 п. 23: квота уровня, видного в сохранении дня <paramref name="day"/>, действует с
    /// обхода СЛЕДУЮЩЕГО дня — сохранение снято в конце хода, после дневного обхода, а рост города
    /// покупается внутри хода (корпус: квота каждого обхода = <c>T_CITY[SIZE]</c> последнего сохранения
    /// перед ним, 155/0).</summary>
    private static void Step(Dictionary<string, List<(int From, int Quota)>> steps, LandSource first,
                             LandSource now, int day)
    {
        var list = steps.TryGetValue(now.Id, out var had) ? had : null;
        int current = list is { Count: > 0 } ? list[^1].Quota : first.Quota;
        if (current == now.Quota) return;
        if (list is null) steps[now.Id] = list = [];
        list.Add((day + 1, now.Quota));
    }

    private static IEnumerable<LandSource> Sources(Shot shot, string playerId, LandQuotas quotas)
    {
        foreach (var s in shot.Sites)
        {
            if (s.Kind is not (SiteKind.Capital or SiteKind.Village)) continue;
            if (!s.Owner.Equals(playerId, StringComparison.OrdinalIgnoreCase)) continue;
            var centre = LandModel.FortCentre(s.X, s.Y);
            yield return new LandSource(s.Id, centre,
                s.Kind == SiteKind.Capital ? quotas.Capital : quotas.Village(s.Size ?? 1),
                Own(shot.Map, s.Id, centre));
        }
        foreach (var r in shot.Map.Rods.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
            if (r.Owner.Equals(playerId, StringComparison.OrdinalIgnoreCase))
                yield return new LandSource(r.Id, (r.X, r.Y), quotas.RodRange,
                                            Own(shot.Map, r.Id, (r.X, r.Y)));
    }

    private static IReadOnlyList<(int X, int Y)> Own(MapState map, string id, (int X, int Y) fallback) =>
        LandModel.PlanCells(map, id) is { Count: > 0 } cells ? cells : [fallback];

    private static IReadOnlySet<(int X, int Y)> CellsOf(MapState map, int terrain)
    {
        var set = new HashSet<(int X, int Y)>();
        for (int x = 0; x < map.Size; x++)
            for (int y = 0; y < map.Size; y++)
                if (map.Tiles[x, y].Terrain == terrain) set.Add((x, y));
        return set;
    }

    private static bool HasTerrainDay(string path) =>
        File.ReadLines(path).Take(4000)
            .Any(l => l.Contains("\"ev\":\"terrain_day\"", StringComparison.Ordinal));

    private static IEnumerable<string> Journals()
    {
        if (CorpusFiles.Dir.Length == 0 || !Directory.Exists(CorpusFiles.Dir)) yield break;
        foreach (var f in Directory.GetFiles(CorpusFiles.Dir, "map-*.jsonl", SearchOption.AllDirectories)
                     .Where(f => !(Path.GetFileName(Path.GetDirectoryName(f)) ?? "").StartsWith('_'))
                     .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }
}
