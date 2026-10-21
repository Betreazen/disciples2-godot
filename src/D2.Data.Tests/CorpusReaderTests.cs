using System.Text.Json;
using D2.Data.Corpus;

public class CorpusReaderTests
{
    private const string SamplePath = "corpus/sample/one_battle.jsonl";

    private static string WriteTemp(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    [Fact] public void Battles_FindsExactlyOneCompleteBattle()
    {
        var battles = CorpusReader.Battles(SamplePath);

        Assert.Single(battles);
        Assert.NotNull(battles[0].End);
    }

    [Fact] public void Battle_HasRoundAndTurnAndAtLeastOneDamageEvent()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];

        Assert.Contains(battle.Events, e => e is RoundEvent);
        Assert.Contains(battle.Events, e => e is TurnEvent);
        Assert.True(battle.Events.OfType<DamageEvent>().Count() >= 1);
    }

    [Fact] public void Battle_UnitsComesFromBattleUnitsEvent_TwoUnitsWithTypes()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];

        var units = battle.Units;
        Assert.NotNull(units);
        Assert.Equal(2, units!.Count);
        var types = units.Select(u => u.Type).ToList();
        Assert.Contains("G000UU0019", types);
        Assert.Contains("G000UU5130", types);
    }

    [Fact] public void Order_BattleUnitsAndRound1Turns_HaveBothUnitIds_Round1RoundsAreEmpty()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];
        var expectedIds = new HashSet<string> { "S143UN0007", "S143UN0013" };

        var battleUnits = Assert.Single(battle.Events.OfType<BattleUnitsEvent>());
        Assert.Equal(expectedIds, battleUnits.Order.Select(o => o.Id).ToHashSet());

        var round1Turns = battle.Events.OfType<TurnEvent>().Where(t => t.Round == 1).ToList();
        Assert.NotEmpty(round1Turns);
        Assert.All(round1Turns, t => Assert.Equal(expectedIds, t.Order.Select(o => o.Id).ToHashSet()));

        var round1Rounds = battle.Events.OfType<RoundEvent>().Where(r => r.Round == 1).ToList();
        Assert.NotEmpty(round1Rounds);
        Assert.All(round1Rounds, r => Assert.Empty(r.Order));
    }

    [Fact] public void Battle_HasExactlySixDamageEvents_FromAttackerToTarget()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];
        var dmgs = battle.Events.OfType<DamageEvent>().ToList();

        Assert.Equal(6, dmgs.Count);
        Assert.All(dmgs, d => Assert.Equal("S143UN0007", d.Attacker));
        Assert.All(dmgs, d => Assert.Equal("S143UN0013", d.Target));
    }

    [Fact] public void EveryMiss_RngAfterIsNotBeforeRngBefore()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];
        var misses = battle.Events.OfType<MissEvent>().ToList();

        Assert.NotEmpty(misses);
        Assert.All(misses, m => Assert.True(m.RngAfter >= m.RngBefore));
    }

    [Fact] public void EveryMiss_DoubleRollsRng_RngAfterMinusBeforeIsTwo()
    {
        // Fact of this specific capture, not a general schema rule (see docs/corpus-schema.md):
        // attackShouldMiss rolled the engine rng generator twice per call in this battle.
        var battle = CorpusReader.Battles(SamplePath)[0];
        var misses = battle.Events.OfType<MissEvent>().ToList();

        Assert.Equal(7, misses.Count);
        Assert.All(misses, m => Assert.Equal(2, m.RngAfter - m.RngBefore));
    }

    [Fact] public void MissEvents_ExactlyOneActuallyMissed()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];
        var misses = battle.Events.OfType<MissEvent>().ToList();

        Assert.Equal(7, misses.Count);
        Assert.Single(misses, m => m.Miss);
    }

    [Fact] public void EveryDamageEvent_RngAfterMinusBeforeIsOne()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];
        var dmgs = battle.Events.OfType<DamageEvent>().ToList();

        Assert.Equal(6, dmgs.Count);
        Assert.All(dmgs, d => Assert.Equal(1, d.RngAfter - d.RngBefore));
    }

    [Fact] public void EveryDamageEvent_AttackerAndTargetAreInBattleStartUnits()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];
        var unitIds = battle.Start.Units.Select(u => u.Id).ToHashSet();

        foreach (var dmg in battle.Events.OfType<DamageEvent>())
        {
            Assert.Contains(dmg.Attacker, unitIds);
            Assert.Contains(dmg.Target, unitIds);
        }
    }

    [Fact] public void BattleEvents_AreSortedBySeq()
    {
        var battle = CorpusReader.Battles(SamplePath)[0];
        var seqs = battle.Events.Select(e => e.Seq).ToList();

        Assert.Equal(seqs.OrderBy(s => s).ToList(), seqs);
    }

    [Fact] public void Read_ReturnsFileOrderNotSeqOrder()
    {
        // The probe README/schema guarantee: seq is taken before nested calls, so a nested
        // event's line (bigger seq) lands in the file before its outer event's line.
        var events = CorpusReader.Read(SamplePath).ToList();
        var seqs = events.Select(e => e.Seq).ToList();

        Assert.NotEqual(seqs.OrderBy(s => s).ToList(), seqs);
    }

    [Fact] public void UnknownEv_ParsesAsUnknownEventInsteadOfThrowing()
    {
        var tmp = WriteTemp("{\"seq\":1,\"tid\":1,\"ev\":\"zzz\"}\n");
        try
        {
            var events = CorpusReader.Read(tmp).ToList();

            var unknown = Assert.Single(events);
            var e = Assert.IsType<UnknownEvent>(unknown);
            Assert.Equal("zzz", e.Ev);
            Assert.Contains("\"zzz\"", e.RawJson);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact] public void Battles_WithoutBattleEnd_ReturnsOneTruncatedBattleWithNoLostEvents()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1,\"ev\":\"battle_start\",\"attacker_group\":\"a\",\"defender_group\":\"b\"," +
            "\"attacker_player\":\"ap\",\"defender_player\":\"dp\",\"round\":1,\"units\":[]}\n" +
            "{\"seq\":2,\"tid\":1,\"ev\":\"round\",\"round\":1,\"order\":[],\"units\":[]}\n");
        try
        {
            var battles = CorpusReader.Battles(tmp);

            var battle = Assert.Single(battles);
            Assert.Null(battle.End);
            Assert.Equal(2, battle.Events.Count);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private const string StartAB =
        "\"ev\":\"battle_start\",\"attacker_group\":\"a\",\"defender_group\":\"b\",\"attacker_player\":\"ap\",\"defender_player\":\"dp\"," +
        "\"units\":[{\"id\":\"u1\",\"hp\":150,\"xp\":0,\"st\":0,\"st2\":0,\"flags\":10,\"shattered\":0,\"fort\":0}," +
        "{\"id\":\"u2\",\"hp\":65,\"xp\":0,\"st\":0,\"st2\":0,\"flags\":2,\"shattered\":0,\"fort\":0}]";
    private const string Miss = "\"ev\":\"miss\",\"power\":80,\"miss\":false,\"rng_before\":1,\"rng_after\":3";
    private const string EndA = "\"ev\":\"battle_end\",\"winner\":\"a\",\"round\":2,\"units\":[]";

    /// <summary>Без принудительного автобоя движок делает копию BattleMsgData на каждый раунд, и
    /// зонд пишет battle_start на каждую (corpus/20260906-manual: seq 52975/53047/53083 при
    /// одном battle_end). Тот же набор юнитов без battle_end между ними — продолжение боя.</summary>
    [Fact] public void Battles_BattleStartWithSameUnitsBeforeBattleEnd_IsContinuation()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1," + StartAB + ",\"round\":0}\n" +
            "{\"seq\":2,\"tid\":1," + Miss + "}\n" +
            "{\"seq\":3,\"tid\":1," + StartAB + ",\"round\":1}\n" +
            "{\"seq\":4,\"tid\":1," + Miss + "}\n" +
            "{\"seq\":5,\"tid\":1," + EndA + "}\n" +
            "{\"seq\":6,\"tid\":1," + StartAB + ",\"round\":0}\n" +
            "{\"seq\":7,\"tid\":1," + Miss + "}\n");
        try
        {
            var battles = CorpusReader.Battles(tmp);

            Assert.Equal(2, battles.Count);
            Assert.NotNull(battles[0].End);
            Assert.Equal(1, battles[0].Start.Seq);
            Assert.Equal(2, battles[0].Events.OfType<BattleStartEvent>().Count());
            Assert.Equal(2, battles[0].Events.OfType<MissEvent>().Count());
            Assert.Equal(5, battles[0].Events.Count);
            Assert.Equal(6, battles[1].Start.Seq);   // после battle_end те же юниты — новый бой
            Assert.Null(battles[1].End);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact] public void Battles_BattleStartWithOtherUnitsBeforeBattleEnd_SplitsAsBefore()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1," + StartAB + ",\"round\":0}\n" +
            "{\"seq\":2,\"tid\":1,\"ev\":\"battle_start\",\"attacker_group\":\"a\",\"defender_group\":\"b\"," +
            "\"attacker_player\":\"ap\",\"defender_player\":\"dp\",\"round\":0,\"units\":[]}\n");
        try
        {
            var battles = CorpusReader.Battles(tmp);

            Assert.Equal(2, battles.Count);
            Assert.All(battles, b => Assert.Null(b.End));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Копия посреди боя, в которой появился призванный (другой набор юнитов, те же группы, раунд копии не нулевой), — тот же
    /// бой (corpus/20260909-arena1: бой 121432 резался на шесть кусков — в копиях раундов 1–5 добавлялся призванный).</summary>
    [Fact] public void Battles_MidBattleCopyWithSummonedUnit_IsContinuation()
    {
        const string StartWithSummon =
            "\"ev\":\"battle_start\",\"attacker_group\":\"a\",\"defender_group\":\"b\",\"attacker_player\":\"ap\",\"defender_player\":\"dp\"," +
            "\"units\":[{\"id\":\"u1\",\"hp\":150,\"xp\":0,\"st\":0,\"st2\":0,\"flags\":10,\"shattered\":0,\"fort\":0}," +
            "{\"id\":\"u2\",\"hp\":65,\"xp\":0,\"st\":0,\"st2\":0,\"flags\":2,\"shattered\":0,\"fort\":0}," +
            "{\"id\":\"u3\",\"hp\":80,\"xp\":0,\"st\":134217728,\"st2\":0,\"flags\":0,\"shattered\":0,\"fort\":0}]";
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1," + StartAB + ",\"round\":0}\n" +
            "{\"seq\":2,\"tid\":1," + Miss + "}\n" +
            "{\"seq\":3,\"tid\":1," + StartWithSummon + ",\"round\":2}\n" +
            "{\"seq\":4,\"tid\":1," + Miss + "}\n" +
            "{\"seq\":5,\"tid\":1," + EndA + "}\n");
        try
        {
            var battles = CorpusReader.Battles(tmp);

            Assert.Single(battles);
            Assert.NotNull(battles[0].End);
            Assert.Equal(2, battles[0].Events.OfType<MissEvent>().Count());
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Те же группы и ненулевой раунд, но наборы юнитов не вложены друг в друга, — это другой бой, а не копия.</summary>
    [Fact] public void Battles_MidBattleStartWithDisjointUnits_SplitsAsBefore()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1," + StartAB + ",\"round\":0}\n" +
            "{\"seq\":2,\"tid\":1,\"ev\":\"battle_start\",\"attacker_group\":\"a\",\"defender_group\":\"b\"," +
            "\"attacker_player\":\"ap\",\"defender_player\":\"dp\",\"round\":2,\"units\":[" +
            "{\"id\":\"u9\",\"hp\":80,\"xp\":0,\"st\":0,\"st2\":0,\"flags\":0,\"shattered\":0,\"fort\":0}]}\n");
        try
        {
            Assert.Equal(2, CorpusReader.Battles(tmp).Count);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact] public void Battles_WithoutBattleStart_IsEmpty_ButReadStillReturnsEveryLine()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1,\"ev\":\"round\",\"round\":1,\"order\":[],\"units\":[]}\n" +
            "{\"seq\":2,\"tid\":1,\"ev\":\"turn_before\",\"unit\":\"u1\",\"round\":1,\"order\":[],\"units\":[]}\n");
        try
        {
            Assert.Empty(CorpusReader.Battles(tmp));
            Assert.Equal(2, CorpusReader.Read(tmp).Count());
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact] public void Read_ToleratesCrlfAndBlankLines()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1,\"ev\":\"start\",\"exe_size\":1,\"version\":\"v\",\"probe\":\"p\"," +
            "\"autobattle\":false}\r\n\r\n{\"seq\":2,\"tid\":1,\"ev\":\"battle_turn\",\"round\":1}\r\n");
        try
        {
            Assert.Equal(2, CorpusReader.Read(tmp).Count());
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact] public void Read_MissingRequiredField_ThrowsJsonExceptionNamingTheField()
    {
        var tmp = WriteTemp(
            "{\"seq\":1,\"tid\":1,\"ev\":\"dmg\",\"attacker\":\"a\",\"crit\":false,\"ret\":1,\"dmg\":1," +
            "\"crit_dmg\":-1,\"rng_before\":0,\"rng_after\":0}\n");
        try
        {
            var ex = Assert.Throws<JsonException>(() => CorpusReader.Read(tmp).ToList());
            Assert.Contains("target", ex.Message);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
