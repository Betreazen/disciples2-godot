using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Map;
using D2.Data;
using D2.Data.Magic;
using D2.Rules.Magic;
using Xunit.Abstractions;

/// <summary>Переходник «строка журнала -> флаги отряда». Один на харнесс и на юнит-тесты, чтобы
/// перепутанный порядок булевых полей нельзя было проверить дважды одной и той же ошибкой.</summary>
public static class MoveEvents
{
    public static MoveOptions OptionsOf(MoveCostEvent e) => new(
        LeaderAlive: e.LeaderAlive,
        PlainsBonus: e.Plains,
        ForestBonus: e.Forest,
        WaterBonus: e.Water,
        WaterOnly: e.WaterOnly,
        ForbidWaterOnLand: e.ForbidWater);

    /// <summary>Флаги строкой — для группировки расхождений: подгонять число под журнал нельзя,
    /// а вот увидеть «расходится только там, где стоит forest» — можно.</summary>
    public static string Flags(MoveCostEvent e) =>
        $"leader={(e.LeaderAlive ? 1 : 0)} plains={(e.Plains ? 1 : 0)} forest={(e.Forest ? 1 : 0)} " +
        $"water={(e.Water ? 1 : 0)} only={(e.WaterOnly ? 1 : 0)} forbid={(e.ForbidWater ? 1 : 0)}";
}

/// <summary>
/// С1 карты для движения: каждое событие <c>move_cost</c> и <c>can_move</c> из журнала зонда
/// пересчитывается нашими функциями и сравнивается с записанным результатом движка. Порог — 100 %
/// по тому, что вообще сверяемо; всё несверяемое уходит в пропуски С ПРИЧИНОЙ, молчаливого
/// «сошлось» здесь быть не должно.
///
/// <para><b>Пока журнала карты нет, тесты пропускаются</b> (его снимает пользователь — задача 2
/// плана Ф5). Журнал ищется так же, как в <c>D2.Data.Tests/MapCorpusTests</c>: переменная
/// окружения <c>MAP_JOURNAL</c> либо <c>corpus/**/map-*.jsonl</c>. Карта, на которой снят прогон,
/// — <c>MAP_SCENARIO</c> либо единственный <c>.sg</c> рядом с журналом.</para>
///
/// <para><b>Чего харнесс сверить не может и не делает вид, что может.</b> Движковая
/// <c>computeMovementCost</c> получает две битовые маски пути (<c>a6</c>, <c>a7</c>), которых в
/// журнале нет: ими ищущий путь запрещает клетки и, наоборот, разрешает клетку с мешком или чужим
/// отрядом. Поэтому случай «движок запретил (<c>ret=0</c>) там, где местность разрешает» считается
/// отдельной строкой пропусков, а не расхождением. Обратное — «мы запретили там, где движок дал
/// цену» — маской не объясняется и падает как расхождение.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class C4MoveHarnessTests(ITestOutputHelper output)
{
    /// <summary>Все журналы карты: <c>MAP_JOURNAL</c> (один файл) либо <c>corpus/**/map-*.jsonl</c>.
    /// Каталог, начинающийся с <c>_</c>, — сознательно выведенный из оборота прогон, как в
    /// <see cref="CorpusFiles"/>.</summary>
    private static IReadOnlyList<string> Journals()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAP_JOURNAL");
        if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? [fromEnv] : [];
        var corpus = CorpusFiles.Dir;   // D2_CORPUS_DIR (рабочие копии ../dg-wt-*: корпуса в них нет), иначе corpus/ репозитория
        if (!Directory.Exists(corpus)) return [];
        return Directory.GetFiles(corpus, "map-*.jsonl", SearchOption.AllDirectories)
            .Where(f => !(Path.GetFileName(Path.GetDirectoryName(f)) ?? "").StartsWith('_'))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>Карта прогона: <c>MAP_SCENARIO</c>, иначе <c>map-start.sg</c> рядом с журналом
    /// (раскладка <c>tools/corpus_c4_run.py</c>: стартовая карта плюс сохранения
    /// <c>save-NN.sg</c>), иначе единственный <c>.sg</c> в каталоге. Несколько безымянных <c>.sg</c>
    /// рядом — отказ, а не выбор наугад: не тот файл дал бы ложные расхождения по всей таблице.</summary>
    private static (MapState? Map, string Note) MapOf(string journal)
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAP_SCENARIO");
        string dir = Path.GetDirectoryName(journal)!;
        string start = Path.Combine(dir, "map-start.sg");
        var candidates = !string.IsNullOrEmpty(fromEnv) ? [fromEnv]
            : File.Exists(start) ? [start]
            : Directory.GetFiles(dir, "*.sg").OrderBy(p => p, StringComparer.Ordinal).ToArray();

        if (candidates.Length == 0) return (null, "рядом с журналом нет .sg (путь можно задать MAP_SCENARIO)");
        if (candidates.Length > 1) return (null, $"рядом с журналом {candidates.Length} файлов .sg — какой из них снимали, неизвестно");
        return (MapLoader.Load(candidates[0]), candidates[0]);
    }

    private static string GroundKey(MapState map, int x, int y)
    {
        if (!map.Contains(x, y)) return "вне карты";
        return map.At(x, y).Ground switch
        {
            Ground.Plain => map.IsRoad(x, y) ? "дорога" : "равнина",
            Ground.Forest => "лес",
            Ground.Water => Movement.IsDeepWater(map, x, y) ? "вода глубокая" : "вода",
            Ground.Mountain => "горы",
            var other => $"грунт {(int)other}",
        };
    }

    /// <summary>Заклинание, сменившее землю по ходу прогона: ход каста, накрытые клетки и новая
    /// земля. Пустой список, если рядом нет <c>route.json</c> или недоступны данные игры.</summary>
    private sealed record TerrainCast(int Turn, IReadOnlySet<(int X, int Y)> Cells, Ground Ground)
    {
        public bool Applied { get; set; }
    }

    /// <summary>Смена террейна заклинанием — единственное, из-за чего карта прогона перестаёт быть
    /// стартовым <c>.sg</c>.
    ///
    /// <para><b>Ранг 1, подтверждено журналом стоимости шага.</b> На ходу 44 прогона Ф6 брошено
    /// «Создание Рощи» (<c>g000ss0107</c>, <c>CATEGORY 11 ChangeTerrain</c>, <c>AREA 3</c>,
    /// <c>GROUND_CAT 1</c> — лес) в клетку (18,20). Модель заклинаний задачи 9 предсказывает
    /// квадрат 3×3 с центром там же, то есть <c>x 17..19</c>, <c>y 19..21</c>. Со следующего хода
    /// движок отвечает на <c>move_cost</c> по клеткам (17,19), (17,20), (17,21), (18,19) ценой
    /// <b>4</b> — лесной, — а до каста отвечал <b>2</b>. Все четыре клетки лежат внутри
    /// предсказанного квадрата, ни одна клетка вне него цену не сменила: <c>AREA</c> и
    /// <c>GROUND_CAT</c> прочитаны верно. Прежде это расхождение числилось «унаследованным
    /// дефектом стоимости шага» — им оно не было.</para>
    ///
    /// <para><b>Ранг 3 — форма области</b> (квадрат против ромба): четыре наблюдённые клетки лежат
    /// внутри обеих. Снимает дифф слоя земли <c>.sg</c> до и после каста.</para></summary>
    private static List<TerrainCast> TerrainCasts(string journal, MapState map)
    {
        var casts = RouteCasts.Of(journal);
        if (casts.Count == 0 || !GamePaths.IsAvailable) return [];
        var spells = SpellLoader.Load(GamePaths.Globals);
        var list = new List<TerrainCast>();
        foreach (var cast in casts)
        {
            if (!spells.TryGetValue(cast.SpellId, out var spell)) continue;
            if (spell.Category != SpellCategory.ChangeTerrain) continue;
            list.Add(new TerrainCast(cast.Turn,
                SpellCast.Tiles(spell, cast.X, cast.Y, map.Size, map.Size).ToHashSet(),
                (Ground)spell.Ground));
        }
        return list;
    }

    /// <summary>Применить к карте касты, уже состоявшиеся к началу хода <paramref name="turn"/>.
    /// Строго «до наступившего хода», а не «на нём»: момента заклинания внутри хода журнал не
    /// пишет, и вызовы того же хода до каста считались бы по новой земле.</summary>
    private static void ApplyTerrain(MapState map, List<TerrainCast> casts, int turn)
    {
        foreach (var cast in casts.Where(c => !c.Applied && turn > c.Turn))
        {
            foreach (var (x, y) in cast.Cells)
                if (map.Contains(x, y))
                    map.Tiles[x, y] = new Tile((byte)((map.Tiles[x, y].Raw & ~0b111000) | ((int)cast.Ground << 3)));
            cast.Applied = true;
        }
    }

    [SkippableFact] public void EveryRecordedMoveCost_MatchesOurs()
    {
        var journals = Journals();
        Skip.If(journals.Count == 0, "нет журнала карты (corpus вне git; путь можно задать MAP_JOURNAL)");

        var stat = new Stats();
        int files = 0, calls = 0;

        foreach (var journal in journals)
        {
            var (map, note) = MapOf(journal);
            if (map is null) { stat.Skip("файл", note); continue; }
            files++;
            output.WriteLine($"{Path.GetFileName(journal)} <- {Path.GetFileName(note)} ({map.Size}x{map.Size})");

            var events = JournalCache.Read(journal).ToList();
            foreach (var c in events.OfType<CountsEvent>())
                calls = (int)Math.Max(calls, c.Calls.GetValueOrDefault("move_cost"));

            var terrain = TerrainCasts(journal, map);
            int turn = 0;
            foreach (var ev in events)
            {
                if (ev is TerrainDayEvent day) { turn = (int)day.Turn; ApplyTerrain(map, terrain, turn); continue; }
                if (ev is not MoveCostEvent e) continue;

                int x = (int)e.X, y = (int)e.Y;
                var key = GroundKey(map, x, y);
                if (!map.Contains(x, y)) { stat.Skip(key, "клетка вне карты этого .sg"); continue; }
                if (terrain.Any(t => t.Turn == turn && t.Cells.Contains((x, y))))
                {   // Каст пишется маршрутом с точностью до хода, а не до seq: внутри хода каста
                    // неизвестно, случился вызов до заклинания или после.
                    stat.Skip(key, "ход каста смены террейна: момент заклинания журнал не пишет");
                    continue;
                }

                int ours = Movement.Cost(map, x, y, MoveEvents.OptionsOf(e), MovementRules.Vanilla);
                if (ours == e.Ret) { stat.Count(key, true); continue; }

                // Единственное объяснимое одностороннее расхождение — маска пути a6/a7, которой в
                // журнале нет. Обратная сторона (мы запретили, движок дал цену) — настоящее
                // расхождение и падение теста.
                if (e.Ret == Movement.Forbidden)
                {
                    stat.Skip(key, "движок запретил клетку, местность её разрешает — маска пути a6/a7 в журнале не записана");
                    continue;
                }

                stat.Count(key, false);
                stat.Mismatch(key, MoveEvents.Flags(e), $"({x},{y}) {key}: движок {e.Ret}, у нас {ours}, {MoveEvents.Flags(e)}");
            }
        }

        output.WriteLine($"журналов сверено {files} из {journals.Count}; вызовов move_cost по счётчику зонда {calls}");
        foreach (var line in stat.Lines()) output.WriteLine(line);
        foreach (var line in stat.MismatchLines(20)) output.WriteLine(line);

        Assert.True(stat.Checked > 0, "ни одного события move_cost не сверено — проверять было нечего");
        Assert.True(stat.Bad == 0, $"расхождений {stat.Bad} из {stat.Checked}; разбивка — в выводе теста");
    }

    [SkippableFact] public void EveryRecordedCanMove_MatchesOurs()
    {
        var journals = Journals();
        Skip.If(journals.Count == 0, "нет журнала карты");

        var stat = new Stats();

        foreach (var journal in journals)
        {
            var (map, note) = MapOf(journal);
            if (map is null) { stat.Skip("файл", note); continue; }

            // Позиции живые: старт из .sg, дальше каждый stack_move их двигает. Без этого сверка
            // была бы сверкой с расстановкой первого дня.
            var positions = Movement.Positions(map);

            foreach (var e in JournalCache.Read(journal))
            {
                if (e is StackMoveEvent m) { if (m.Ret) positions[m.Stack] = ((int)m.X, (int)m.Y); continue; }
                if (e is not CanMoveEvent c) continue;

                int x = (int)c.X, y = (int)c.Y;
                var key = GroundKey(map, x, y);
                if (!positions.ContainsKey(c.Stack))
                { stat.Skip(key, "отряда нет в .sg (создан по ходу партии) — своя клетка неизвестна"); continue; }

                bool ours = Movement.CanMoveTo(positions, c.Stack, x, y);
                if (ours == c.Ret) { stat.Count(key, true); continue; }

                // Движок пропускает занятую клетку, если стоящий там отряд невидим
                // (CMidStack::invisible); этого поля в модели карты нет.
                if (c.Ret && !ours)
                { stat.Skip(key, "движок пустил на занятую клетку — возможен невидимый отряд (CMidStack::invisible)"); continue; }

                stat.Count(key, false);
                stat.Mismatch(key, "can_move", $"({x},{y}) {key} отряд {c.Stack}: движок {c.Ret}, у нас {ours}");
            }
        }

        foreach (var line in stat.Lines()) output.WriteLine(line);
        foreach (var line in stat.MismatchLines(20)) output.WriteLine(line);

        Assert.True(stat.Checked > 0, "ни одного события can_move не сверено");
        Assert.True(stat.Bad == 0, $"расхождений {stat.Bad} из {stat.Checked}");
    }

    /// <summary>Счётчики: строка на грунт (проверено / сошлось / разошлось) плюс пропуски с
    /// причиной и разбивка расхождений по паре «грунт + флаги». Пропуск без причины невозможен —
    /// причина в сигнатуре.</summary>
    private sealed class Stats
    {
        private readonly Dictionary<string, (int ok, int bad)> _counts = [];
        private readonly Dictionary<(string key, string reason), int> _skips = [];
        private readonly Dictionary<(string key, string flags), int> _groups = [];
        private readonly List<string> _examples = [];

        public int Checked => _counts.Values.Sum(v => v.ok + v.bad);
        public int Bad => _counts.Values.Sum(v => v.bad);

        public void Count(string key, bool ok)
        {
            var v = _counts.GetValueOrDefault(key);
            _counts[key] = ok ? (v.ok + 1, v.bad) : (v.ok, v.bad + 1);
        }

        public void Skip(string key, string reason) =>
            _skips[(key, reason)] = _skips.GetValueOrDefault((key, reason)) + 1;

        public void Mismatch(string key, string flags, string example)
        {
            _groups[(key, flags)] = _groups.GetValueOrDefault((key, flags)) + 1;
            _examples.Add(example);
        }

        public IEnumerable<string> Lines()
        {
            foreach (var (k, v) in _counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                yield return $"{k,-16} проверено={v.ok + v.bad,-7} сошлось={v.ok,-7} разошлось={v.bad}";
            foreach (var (k, n) in _skips.OrderBy(kv => kv.Key.key, StringComparer.Ordinal))
                yield return $"{k.key,-16} пропущено={n,-7} причина: {k.reason}";
        }

        public IEnumerable<string> MismatchLines(int limit)
        {
            foreach (var ((key, flags), n) in _groups.OrderByDescending(kv => kv.Value))
                yield return $"расхождений {n,-7} {key} при {flags}";
            foreach (var e in _examples.Take(limit))
                yield return "  " + e;
        }
    }
}
