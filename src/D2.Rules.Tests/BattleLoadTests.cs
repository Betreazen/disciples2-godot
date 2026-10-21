using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;

[Trait("Category", "Corpus")]
public class BattleLoadTests
{
    [SkippableFact] public void RulesLoader_ReadsUnitsAttacksAndConstants()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        var rules = RulesLoader.Load(GamePaths.Globals);

        Assert.Equal(10, rules.Constants.BatInit);      // GVars.BAT_INIT
        Assert.Equal(6, rules.Constants.BatDamage);     // GVars.BAT_DAMAGE
        var hero = rules.Units["G000UU0019"];           // регистр не важен
        Assert.Equal(150, hero.HitPoints);
        Assert.Equal(0, hero.Armor);
        var imp = rules.Units["g000uu5130"];
        Assert.Equal(65, imp.HitPoints);
        var impAttack = rules.AttackOf(imp)!;
        Assert.Equal(AttackClass.Damage, impAttack.Class);
        Assert.Equal(80, impAttack.Power);              // power в корпусе = 80
        Assert.Equal(25, impAttack.QtyDamage);          // dmg_buffs random=false = 25
    }

    [SkippableFact] public void CorpusBattleLoader_BuildsStateFromSampleBattle()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        var rules = RulesLoader.Load(GamePaths.Globals);
        var battle = JournalCache.Battles(Path.Combine(CorpusFiles.Dir, "sample", "one_battle.jsonl"))[0];

        Assert.True(CorpusBattleLoader.TryLoad(battle, rules, out var state, out var reason), reason);

        Assert.Equal(2, state.Units.Count);
        var hero = state.Unit("S143UN0013")!;
        var imp = state.Unit("S143UN0007")!;
        Assert.True(hero.Attacker);      // flags=10 -> бит 3 = attacker
        Assert.False(imp.Attacker);      // flags=2
        Assert.Equal(2, hero.Slot);      // flags & 7 = indexInGroup
        Assert.Equal(2, imp.Slot);
        Assert.Equal(150, hero.Hp);
        Assert.Equal(150, hero.HpMax);
        Assert.Equal(65, imp.Hp);
        Assert.Equal("G000UU5130", imp.Type.Id, ignoreCase: true);
        Assert.Equal(battle.Start.AttackerGroup, state.AttackerGroup);
        Assert.Equal(battle.Start.DefenderGroup, state.DefenderGroup);
    }

    /// <summary>Бой без принудительного автобоя (герой ходит сам): три battle_start на три копии
    /// BattleMsgData (seq 52975/53047/53083), один battle_end, 4 удара; начальное состояние — из
    /// первого battle_start (HP 150/65), не из копии раунда 1 (124/12).</summary>
    [SkippableFact] public void ManualJournal_IsOneBattle_WithEnd_FourHits_InitialHpFromFirstStart()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        var path = Path.Combine(CorpusFiles.Dir, "20260906-manual", "battle-001.jsonl");
        Skip.IfNot(File.Exists(path), "corpus/20260906-manual вне git");
        var battles = JournalCache.Battles(path);

        var battle = Assert.Single(battles);
        Assert.NotNull(battle.End);
        Assert.Equal(52975, battle.Start.Seq);
        Assert.Equal(3, battle.Events.OfType<BattleStartEvent>().Count());
        Assert.Equal(4, battle.Events.OfType<MissEvent>().Count());
        Assert.True(CorpusBattleLoader.TryLoad(battle, RulesLoader.Load(GamePaths.Globals), out var state, out var reason), reason);
        Assert.Equal(150, state.Unit("S143UN0013")!.Hp);
        Assert.Equal(65, state.Unit("S143UN0007")!.Hp);
    }

    [SkippableFact] public void CorpusBattleLoader_RefusesBattleWithoutIdentity()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        var rules = RulesLoader.Load(GamePaths.Globals);
        var battle = JournalCache.Battles(Path.Combine(CorpusFiles.Dir, "sample", "one_battle.jsonl"))[0];
        var withoutUnits = battle with { Events = battle.Events.Where(e => e is not BattleUnitsEvent).ToList() };

        Assert.False(CorpusBattleLoader.TryLoad(withoutUnits, rules, out _, out var reason));
        Assert.Contains("battle_units", reason);
    }

    [SkippableFact] public void CorpusBattleLoader_RefusesUnitMissingFromBattleUnits()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        var rules = RulesLoader.Load(GamePaths.Globals);
        var battle = JournalCache.Battles(Path.Combine(CorpusFiles.Dir, "sample", "one_battle.jsonl"))[0];
        var withoutImp = WithBattleUnits(battle, units => units.Where(u => u.Id != "S143UN0007").ToList());

        Assert.False(CorpusBattleLoader.TryLoad(withoutImp, rules, out _, out var reason));
        Assert.Contains("отсутствует", reason);
    }

    [SkippableFact] public void CorpusBattleLoader_RefusesUnrecognizedUnitType()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        var rules = RulesLoader.Load(GamePaths.Globals);
        var battle = JournalCache.Battles(Path.Combine(CorpusFiles.Dir, "sample", "one_battle.jsonl"))[0];
        var withUnrecognizedImp = WithBattleUnits(battle,
            units => units.Select(u => u.Id == "S143UN0007" ? u with { Type = null } : u).ToList());

        Assert.False(CorpusBattleLoader.TryLoad(withUnrecognizedImp, rules, out _, out var reason));
        Assert.Contains("не опознан", reason);
    }

    /// <summary>Подменяет юнитов события battle_units внутри боя (обе записи — record, `with`
    /// делает новую копию); остальные события не трогаются.</summary>
    private static D2.Data.Corpus.Battle WithBattleUnits(D2.Data.Corpus.Battle battle,
        Func<IReadOnlyList<UnitIdentity>, IReadOnlyList<UnitIdentity>> transform)
    {
        var ev = battle.Events.OfType<BattleUnitsEvent>().First();
        var modified = ev with { Units = transform(ev.Units) };
        return battle with { Events = battle.Events.Select(e => ReferenceEquals(e, ev) ? modified : e).ToList() };
    }
}
