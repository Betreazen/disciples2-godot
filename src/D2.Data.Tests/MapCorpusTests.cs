using D2.Data.Corpus;

/// <summary>
/// Схема корпуса v3 — события карты (docs/corpus-schema.md, раздел «События карты»).
/// Разбор проверяется на синтетических строках, точно повторяющих формат, который печатает
/// <c>tools/d2probe/mss32/src/probehooks.cpp</c>; на настоящем журнале карты — отдельные
/// <c>SkippableFact</c>, они включатся, как только журнал появится (задача 2 плана Ф5).
/// </summary>
[Trait("Category", "Corpus")]
public class MapCorpusTests
{
    /// <summary>Слепок разбора всех журналов боёв Ф4 плюс образца — снят на читателе **до**
    /// правки v3 (коммит 497c6a0). Доказательство обратной совместимости: те же файлы дают ровно
    /// те же события тех же типов и то же число боёв. Пересчитывать нельзя, только сверять;
    /// расхождение здесь означает, что схема v3 сломала чтение старых журналов.</summary>
    private const string BattleJournalsBaseline =
        "files=66 battles=110 ArmorEvent=3533 AutobattleForcedEvent=12 BattleEndEvent=110 " +
        "BattleStartEvent=496 BattleTurnEvent=1589 BattleUnitsEvent=904 DamageBuffsEvent=2781 " +
        "DamageEvent=1273 DamageMaxEvent=1273 IncomeEvent=110 MissEvent=1481 PowerEvent=1481 " +
        "RngEvent=11980 RoundEvent=643 StartEvent=66 TurnEvent=5014";

    private const string SamplePath = "corpus/sample/one_battle.jsonl";

    /// <summary>Корпус: <c>D2_CORPUS_DIR</c> или <c>corpus/</c> репозитория (<see cref="GameSaveTests.CorpusDir"/>) — в рабочей копии git
    /// там одни описи, журналы лежат вне git.</summary>
    private static string CorpusDir => D2.Data.Tests.GameSaveTests.CorpusDir();

    /// <summary>Журнал карты: <c>corpus/*/map-*.jsonl</c> (куда его кладёт задача 2) либо путь из
    /// переменной окружения <c>MAP_JOURNAL</c> — сырой журнал из <c>game-dev/probe</c> можно
    /// проверить, не копируя его в корпус.</summary>
    private static string? MapJournal
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("MAP_JOURNAL");
            if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
            if (!Directory.Exists(CorpusDir)) return null;
            return Directory.GetFiles(CorpusDir, "map-*.jsonl", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        }
    }

    private static string WriteTemp(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    private static string Summarize(IEnumerable<string> files)
    {
        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var count = 0;
        var battles = 0;
        foreach (var file in files)
        {
            count++;
            foreach (var e in JournalCache.Read(file))
            {
                var name = e.GetType().Name;
                histogram[name] = histogram.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            battles += JournalCache.Battles(file).Count;
        }
        return $"files={count} battles={battles} " +
               string.Join(" ", histogram.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static List<string> BattleJournals()
    {
        if (!Directory.Exists(CorpusDir)) return [];
        return Directory.GetDirectories(CorpusDir, "20260906-*")
            .SelectMany(d => Directory.GetFiles(d, "*.jsonl"))
            .Concat(Directory.GetFiles(Path.Combine(CorpusDir, "sample"), "*.jsonl"))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    [SkippableFact] public void F4BattleJournals_ParseExactlyAsBeforeSchemaV3()
    {
        var files = BattleJournals();
        Skip.If(files.Count == 0, "нет корпуса Ф4 (corpus/ вне git)");

        Assert.Equal(BattleJournalsBaseline, Summarize(files));
    }

    [Fact] public void SampleBattleJournal_IncomeHasNoTurnAndNoPurse()
    {
        var events = CorpusReader.Read(SamplePath).ToList();

        Assert.Empty(events.OfType<UnknownEvent>()); // схема v3 ничего не сломала
        Assert.NotEmpty(events.OfType<BattleStartEvent>());
        var income = events.OfType<IncomeEvent>().First();
        Assert.Null(income.Turn);   // v2.1: полей turn/bank ещё нет
        Assert.Null(income.Purse);
    }

    // Строки один в один того вида, что печатает probehooks.cpp (см. docs/corpus-schema.md).
    private const string MapLines =
        """
        {"seq":1,"tid":7,"ev":"move_cost","x":12,"y":34,"stack":"S143ST0001","leader_alive":true,"plains":false,"forest":true,"water":false,"water_only":false,"forbid_water":true,"ret":3}
        {"seq":2,"tid":7,"ev":"can_move","x":12,"y":34,"stack":"S143ST0001","ret":true}
        {"seq":3,"tid":7,"ev":"path","stack":"S143ST0001","len":5,"last_x":11,"last_y":33,"end_x":12,"end_y":34}
        {"seq":4,"tid":7,"ev":"stack_move","stack":"S143ST0001","x":12,"y":34,"check_area":true,"move_left":17,"ret":true}
        {"seq":5,"tid":7,"ev":"xp","player":"S143PL0001","unit":"S143UN0007","amount":25,"ret":false}
        {"seq":6,"tid":7,"ev":"build_status","player":"S143PL0001","building":"G000BB0001","ignore_cost":false,"ret":3}
        {"seq":7,"tid":7,"ev":"hire_list","player":"S143PL0001","a3":"S143FT0002","count":6,"ret":true}
        {"seq":8,"tid":7,"ev":"site","site":"S143SI0003","category":2}
        {"seq":9,"tid":7,"ev":"income","player":"S143PL0001","turn":4,"gold":100,"mana":{"infernal":0,"life":25,"death":0,"runic":0,"grove":0},"bank":{"gold":350,"infernal":1,"life":2,"death":3,"runic":4,"grove":5}}
        {"seq":10,"tid":7,"ev":"counts","calls":{"move_cost":41273,"can_move":882,"path":57}}
        """;

    [Fact] public void EveryMapEvent_ParsesWithItsFields()
    {
        var tmp = WriteTemp(MapLines);
        try
        {
            var events = CorpusReader.Read(tmp).ToList();

            Assert.Empty(events.OfType<UnknownEvent>());
            Assert.Equal(10, events.Count);

            var move = Assert.Single(events.OfType<MoveCostEvent>());
            Assert.Equal((12, 34, "S143ST0001", 3L), (move.X, move.Y, move.Stack, move.Ret));
            Assert.Equal((true, false, true, false, false, true),
                (move.LeaderAlive, move.Plains, move.Forest, move.Water, move.WaterOnly, move.ForbidWater));

            var can = Assert.Single(events.OfType<CanMoveEvent>());
            Assert.Equal((12L, 34L, true), (can.X, can.Y, can.Ret));

            var path = Assert.Single(events.OfType<PathEvent>());
            Assert.Equal((5L, 11L, 33L, 12L, 34L), (path.Len, path.LastX, path.LastY, path.EndX, path.EndY));

            var step = Assert.Single(events.OfType<StackMoveEvent>());
            Assert.Equal((12L, 34L, true, 17L, true), (step.X, step.Y, step.CheckArea, step.MoveLeft, step.Ret));

            var xp = Assert.Single(events.OfType<XpEvent>());
            Assert.Equal(("S143PL0001", "S143UN0007", 25L, false), (xp.Player, xp.Unit, xp.Amount, xp.Ret));

            var build = Assert.Single(events.OfType<BuildStatusEvent>());
            Assert.Equal(("G000BB0001", false, 3L), (build.Building, build.IgnoreCost, build.Ret));

            var hire = Assert.Single(events.OfType<HireListEvent>());
            Assert.Equal(("S143PL0001", "S143FT0002", 6L, true), (hire.Player, hire.A3, hire.Count, hire.Ret));

            var site = Assert.Single(events.OfType<SiteEvent>());
            Assert.Equal(("S143SI0003", 2L), (site.Site, site.Category));

            var counts = Assert.Single(events.OfType<CountsEvent>());
            Assert.Equal(41273, counts.Calls["move_cost"]);
            Assert.Equal(3, counts.Calls.Count);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact] public void IncomeV3_KeepsIncomeAndAddsTurnAndPurse()
    {
        var tmp = WriteTemp(MapLines);
        try
        {
            var income = Assert.Single(CorpusReader.Read(tmp).OfType<IncomeEvent>());

            Assert.Equal(100, income.Gold);           // gold/mana — по-прежнему доход за день
            Assert.Equal(25, income.Life);
            Assert.Equal(4, income.Turn);
            Assert.Equal(new D2.Rules.Map.Bank(350, 1, 2, 3, 4, 5), income.Purse);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Правило репозитория «молчаливый пропуск запрещён»: неизвестное событие (журнал
    /// схемы v4, снятый более новым зондом) не роняет разбор, но и не исчезает — оно приходит
    /// как <see cref="UnknownEvent"/> с исходной строкой, и счётчик неизвестных виден вызывающему.</summary>
    [Fact] public void UnknownEventBetweenMapEvents_IsCountedNotSwallowed()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":7,\"ev\":\"site\",\"site\":\"S143SI0003\",\"category\":2}\n" +
            "{\"seq\":2,\"tid\":7,\"ev\":\"trade\",\"whatever\":1}\n" +
            "{\"seq\":3,\"tid\":7,\"ev\":\"xp\",\"player\":\"P\",\"unit\":\"U\",\"amount\":1,\"ret\":false}\n");
        try
        {
            var events = CorpusReader.Read(tmp).ToList();

            Assert.Equal(3, events.Count);
            var unknown = Assert.Single(events.OfType<UnknownEvent>());
            Assert.Equal("trade", unknown.Ev);
            Assert.Contains("\"whatever\":1", unknown.RawJson);
            Assert.Single(events.OfType<SiteEvent>());
            Assert.Single(events.OfType<XpEvent>());
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [SkippableFact] public void MapJournal_ParsesEveryNewEventKind()
    {
        var journal = MapJournal;
        Skip.If(journal is null, "нет журнала карты (corpus вне git; путь можно задать MAP_JOURNAL)");
        var events = JournalCache.Read(journal!).ToList();

        Assert.Empty(events.OfType<UnknownEvent>());
        Assert.NotEmpty(events.OfType<MoveCostEvent>());
        Assert.NotEmpty(events.OfType<CanMoveEvent>());
        Assert.NotEmpty(events.OfType<PathEvent>());
        Assert.NotEmpty(events.OfType<StackMoveEvent>());
        Assert.NotEmpty(events.OfType<IncomeEvent>());
        Assert.NotEmpty(events.OfType<CountsEvent>());
        // xp/build_status/hire_list/site движок пишет, только когда есть найм, постройка, посещение
        // или бой: в контрольном прогоне 20260907-c4 (48 ходов по маршруту, без входа в столицу) их
        // законно ноль. Их разбор проверяют синтетические тесты выше; здесь — что нули честные:
        // имя обязано быть в counts (хук стоял), а строк — ровно столько, сколько говорит счётчик.
        var last = events.OfType<CountsEvent>().Last();
        foreach (var (name, present) in new[] {
                     ("xp", events.OfType<XpEvent>().Any()),
                     ("build_status", events.OfType<BuildStatusEvent>().Any()),
                     ("hire_list", events.OfType<HireListEvent>().Any()),
                     ("site", events.OfType<SiteEvent>().Any()) })
        {
            Assert.True(last.Calls.ContainsKey(name), $"в counts нет имени {name} — хук не стоял");
            if (!present) Assert.Equal(0, last.Calls[name]);
        }

        // У события «по ключу» число строк — это число уникальных сочетаний; настоящее число
        // вызовов лежит только в counts, и оно не может быть меньше числа строк.
        var counts = events.OfType<CountsEvent>().Last();
        var lines = events.OfType<MoveCostEvent>().Count();
        Assert.True(counts.Calls["move_cost"] >= lines,
                    $"счётчик вызовов {counts.Calls["move_cost"]} меньше числа строк {lines}");

        var income = events.OfType<IncomeEvent>().Last();
        Assert.NotNull(income.Turn);
        Assert.NotNull(income.Purse);
    }

    [SkippableFact] public void MapJournal_MoveCostCarriesFlagsAndResult()
    {
        var journal = MapJournal;
        Skip.If(journal is null, "нет журнала карты");
        var m = JournalCache.Read(journal!).OfType<MoveCostEvent>().First();

        Assert.True(m.Ret >= 0);
        Assert.NotEqual("", m.Stack);
        Assert.True(m.X >= 0 && m.Y >= 0);
    }
}
