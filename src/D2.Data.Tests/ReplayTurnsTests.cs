using D2.Data.Battle;
using D2.Data.Corpus;

/// <summary>Образец corpus/sample/one_battle.jsonl: 7 ходов беса S143UN0007 по герою S143UN0013 —
/// 6 попаданий и 1 промах, герой убит (см. task-5-report.md).</summary>
public class ReplayTurnsTests
{
    private const string SamplePath = "corpus/sample/one_battle.jsonl";

    private static IReadOnlyList<ReplayTurn> SampleTurns() => ReplayTurns.Of(JournalCache.Battles(SamplePath)[0]);

    [Fact] public void Of_SampleHasSevenTurnsOfTheImpWithOneMiss()
    {
        var turns = SampleTurns();

        Assert.Equal(7, turns.Count);
        Assert.All(turns, t => Assert.Equal("S143UN0007", t.Unit));
        Assert.Single(turns, t => t.Miss);
        Assert.All(turns.Where(t => t.Miss), t => { Assert.Null(t.Target); Assert.Equal(0, t.Damage); });
        Assert.All(turns.Where(t => !t.Miss), t => { Assert.Equal("S143UN0013", t.Target); Assert.True(t.Damage > 0); });
    }

    [Fact] public void Of_HpAfterFollowsRecordedDamageDownToZero()
    {
        var turns = SampleTurns();

        int hp = 150;
        foreach (var t in turns)
        {
            hp = Math.Max(0, hp - t.Damage);
            Assert.Equal(hp, t.HpAfter["S143UN0013"]);
            Assert.Equal(65, t.HpAfter["S143UN0007"]);
        }
        Assert.Equal(0, hp);
    }

    [Fact] public void Of_BattleWithoutTurnsIsEmpty()
    {
        var start = new BattleStartEvent(1, 0, "A", "D", "P1", "P0", 0, []);
        var battle = new Battle(start, null, [start, new MissEvent(2, 0, 50, true, 0, 1)]);

        Assert.Empty(ReplayTurns.Of(battle)); // miss без turn_after — ход не закрыт
    }

    [Fact] public void Of_DuplicateUnitIdInTurnAfterIsInvalidData()
    {
        var start = new BattleStartEvent(1, 0, "A", "D", "P1", "P0", 0, []);
        UnitState u(long hp) => new("S143UN0013", hp, 0, 0, 0, 10, 0, 0, null, null, null);
        var turnAfter = new TurnEvent(3, 0, true, "S143UN0007", null, 1, [], [u(124), u(97)]);
        var battle = new Battle(start, null, [start, new MissEvent(2, 0, 50, false, 0, 1), turnAfter]);

        var e = Assert.Throws<InvalidDataException>(() => ReplayTurns.Of(battle));
        Assert.Contains("S143UN0013", e.Message);
        Assert.Contains("seq 3", e.Message);
    }
}
