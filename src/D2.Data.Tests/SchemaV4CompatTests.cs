using D2.Data.Corpus;

/// <summary>Долг задачи 4 Ф6, закрытый задачей 14: обратная совместимость схемы корпуса.
///
/// <para>Задача 4 добавила события версии 4 (<c>immune</c>, <c>attack_new</c>, <c>attack_get</c>,
/// <c>terrain_day</c>) и обязана была доказать, что журнал версии 3 прежний код читает без правок,
/// — но <c>src/</c> ей был запрещён (его занимала задача 1), и проверка осталась только на
/// питоновских чекерах. Здесь она сделана тем же читателем, которым пользуются харнессы.</para>
///
/// <para>Замер парный, иначе «сошлось» ничего не значит: <b>старое</b> — закоммиченный образец v3
/// (<c>corpus/sample/one_battle.jsonl</c>, копируется в вывод сборки), где ни одно событие не
/// должно уехать в <see cref="UnknownEvent"/> и все прежние поля обязаны быть на месте;
/// <b>новое</b> — живой журнал v4 прогона Ф6, где обязаны разбираться и новые события, и все
/// прежние. Журнал v4 лежит вне git, поэтому его половина — <see cref="SkippableFactAttribute"/> с
/// причиной, а половина v3 идёт всегда.</para></summary>
[Trait("Category", "Corpus")]
public class SchemaV4CompatTests
{
    private const string SampleV3 = "corpus/sample/one_battle.jsonl";

    /// <summary>Журнал живого прогона Ф6 (схема 4). Корпус вне git — ищем от каталога сборки вверх
    /// до <c>Disciples.sln</c>; каталог, начинающийся с <c>_</c>, выведен из оборота.</summary>
    private static string? JournalV4
    {
        get
        {
            var corpus = Path.Combine(D2.Data.Tests.GameSaveTests.CorpusDir(), "20260908-f6");   // D2_CORPUS_DIR, иначе corpus/ репозитория
            return Directory.Exists(corpus)
                ? Directory.GetFiles(corpus, "map-*.jsonl").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
                : null;
        }
    }

    [Fact] public void V3Sample_EveryLineParses_NoUnknownEvent()
    {
        var events = CorpusReader.Read(SampleV3).ToList();

        Assert.Equal(108, events.Count);
        Assert.Empty(events.OfType<UnknownEvent>());
    }

    /// <summary>Прежние поля на месте — по одному утверждению на каждый тип события образца, а не
    /// «прочиталось без исключения»: читатель бросает только на ОТСУТСТВУЮЩЕМ поле, а поле,
    /// переехавшее в чужой слот, молча дало бы ноль.</summary>
    [Fact] public void V3Sample_EveryEventTypeKeepsItsFields()
    {
        var events = CorpusReader.Read(SampleV3).ToList();

        var start = Assert.Single(events.OfType<StartEvent>());
        Assert.True(start.Autobattle);   // образец снят под маркером автобоя (есть autobattle_forced)
        Assert.NotEqual(0, start.ExeSize);

        var battleStart = Assert.Single(events.OfType<BattleStartEvent>());
        Assert.NotEmpty(battleStart.AttackerGroup);
        Assert.NotEmpty(battleStart.DefenderPlayer);
        Assert.NotEmpty(battleStart.Units);
        Assert.Null(battleStart.RngIndex);          // v2.1: индекса генератора ещё нет — null, не 0

        var battleUnits = Assert.Single(events.OfType<BattleUnitsEvent>());
        Assert.Equal(2, battleUnits.Units.Count);
        Assert.All(battleUnits.Units, u => Assert.NotNull(u.Type));

        var end = Assert.Single(events.OfType<BattleEndEvent>());
        Assert.NotEmpty(end.Winner);

        Assert.Equal(6, events.OfType<DamageEvent>().Count());
        Assert.All(events.OfType<DamageEvent>(), d => { Assert.NotEmpty(d.Attacker); Assert.NotEmpty(d.Target); });
        Assert.Equal(7, events.OfType<MissEvent>().Count());
        Assert.All(events.OfType<MissEvent>(), m => Assert.Equal(2, m.RngAfter - m.RngBefore));
        Assert.Equal(7, events.OfType<PowerEvent>().Count());
        Assert.Equal(6, events.OfType<DamageMaxEvent>().Count());
        Assert.Equal(14, events.OfType<ArmorEvent>().Count());
        Assert.Equal(13, events.OfType<DamageBuffsEvent>().Count());
        Assert.Equal(25, events.OfType<RngEvent>().Count(e => !e.Std));
        Assert.Single(events.OfType<RngEvent>(), e => e.Std);
        Assert.Equal(6, events.OfType<BattleTurnEvent>().Count());
        Assert.Equal(3, events.OfType<RoundEvent>().Count());
        Assert.Equal(13, events.OfType<TurnEvent>().Count());
        Assert.Equal(2, events.OfType<AutobattleForcedEvent>().Count());

        var income = Assert.Single(events.OfType<IncomeEvent>());
        Assert.Null(income.Turn);                   // v2.1: «turn»/«bank» ещё нет — null, не 0
        Assert.Null(income.Purse);

        var battle = Assert.Single(CorpusReader.Battles(SampleV3));
        Assert.NotNull(battle.End);
    }

    [SkippableFact] public void V4Journal_ParsesNewEventsAndKeepsV3Ones()
    {
        var path = JournalV4;
        Skip.If(path is null, "нет журнала v4 (corpus/20260908-f6/map-*.jsonl вне git)");

        var events = CorpusReader.Read(path!).ToList();

        Assert.Empty(events.OfType<UnknownEvent>());
        // Новое схемы 4.
        Assert.NotEmpty(events.OfType<ImmuneEvent>());
        Assert.NotEmpty(events.OfType<AttackNewEvent>());
        Assert.NotEmpty(events.OfType<AttackGetEvent>());
        Assert.NotEmpty(events.OfType<TerrainDayEvent>());
        Assert.All(events.OfType<TerrainDayEvent>(), t => Assert.Equal(t.Owned, t.Cells.Count));
        // Прежнее схемы 3 — тем же читателем, в том же файле.
        Assert.NotEmpty(events.OfType<MoveCostEvent>());
        Assert.NotEmpty(events.OfType<DamageEvent>());
        Assert.NotEmpty(events.OfType<MissEvent>());
        Assert.NotEmpty(events.OfType<XpEvent>());
        var counts = events.OfType<CountsEvent>().Last();
        foreach (var name in new[] { "immune", "attack_new", "attack_get", "terrain_day", "move_cost" })
            Assert.True(counts.Calls.ContainsKey(name), $"в counts.calls нет счётчика {name}");
    }

    /// <summary>Число боёв = число <c>battle_end</c>, а не <c>battle_start</c>.
    /// <para>Поправка 08.09.2026 (docs/corpus-schema.md): без принудительного автобоя движок делает
    /// новую копию <c>BattleMsgData</c> на каждый раунд, дедуп зонда по указателю слетает, и
    /// <c>battle_start</c> пишется <b>перед каждым раундом</b> — на три боя прогона Ф6 их 13.
    /// Резать журнал по ним значило бы посчитать 13 боёв вместо трёх и подать каждый раунд как
    /// отдельный бой без <c>battle_end</c>.</para></summary>
    [SkippableFact] public void V4Journal_BattleCountEqualsBattleEndCount_NotBattleStart()
    {
        var path = JournalV4;
        Skip.If(path is null, "нет журнала v4 (corpus/20260908-f6/map-*.jsonl вне git)");

        var events = CorpusReader.Read(path!).ToList();
        int starts = events.OfType<BattleStartEvent>().Count();
        int ends = events.OfType<BattleEndEvent>().Count();
        var battles = CorpusReader.Battles(path!);

        Assert.True(starts > ends, $"в этом журнале battle_start ({starts}) обязан быть чаще battle_end ({ends})");
        Assert.Equal(ends, battles.Count);
        Assert.All(battles, b => Assert.NotNull(b.End));
        // Лишние battle_start не потеряны: они остались событиями своего боя.
        Assert.Equal(starts, battles.Sum(b => b.Events.OfType<BattleStartEvent>().Count()));
    }
}
