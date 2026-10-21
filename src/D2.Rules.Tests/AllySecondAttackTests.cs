using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Rng;

/// <summary>
/// Партия пользователя 15.10 на <c>port-synth-1-battle</c> (пометки 2, 3, 5, 9): вторая атака по своим, превращение себя, павшие призванные,
/// цели человека. Юниты и атаки — настоящие, из ванили; где есть журнал оригинала — харнесс по нему
/// (<c>corpus/20260908-f6/map-001 [1281528]</c>: тот же набор редких классов, что у полигонов A2–A3).
/// </summary>
[Trait("Category", "Corpus")]
public class AllySecondAttackTests
{
    private static readonly Lazy<GameRules> Rules = new(() => RulesLoader.Load(GamePaths.Globals));
    private static RulesConstants C => Rules.Value.Constants;

    private const string Abbess = "g000uu0017";      // лечение всех 40 (REACH 1), вторая — Cure всех
    private const string Patriarch = "g000uu0150";   // лечение 120 (REACH 2), вторая — Revive
    private const string ElderVampire = "g000uu0175"; // DrainOverflow, источник — смерть
    private const string Ghoul = "g000uu5033";       // Always к смерти (Gimmu: IMMUNITY 3, IMMUNECAT 3)
    private const string WolfLord = "g000uu0164";    // TransformSelf, запасная g000aa1164 — Damage по всем
    private const string Elementalist = "g000uu0153"; // Summon
    private const string Knight = "g000uu0002";
    private const string Squire = "g000uu0001";

    private static BattleUnit Of(string typeId, bool attacker, int slot, string? id = null)
    {
        var type = Rules.Value.Units[typeId];
        return new BattleUnit
        {
            Id = id ?? typeId, Type = type, Attack = Rules.Value.AttackOf(type)!, Attack2 = Rules.Value.Attack2Of(type),
            Attacker = attacker, Slot = slot, HpMax = type.HitPoints, Hp = type.HitPoints,
        };
    }

    private static BattleState State(params BattleUnit[] units) => new()
    {
        Units = [.. units], AttackerGroup = "A", DefenderGroup = "D", Round = 1, Rules = Rules.Value,
    };

    private static ScriptedRng Zeros() => new(Enumerable.Repeat(0, 2000));

    /// <summary>Сессия с ходом игрока: ходы чужих до него делает ИИ.</summary>
    private static BattleSession Session(BattleState state)
    {
        var session = new BattleSession(state, new BattleSim(state, Zeros(), C), C);
        while (session.Current is { Attacker: false }) session.AiStep();
        return session;
    }

    /// <summary>Пометка 2: Аббатиса лечит отравленного и второй атакой (<c>Cure</c>) снимает яд — значок яда и заливка портрета (по
    /// <see cref="BattleUnit.Dots"/>) уходят.</summary>
    [SkippableFact] public void Abbess_HealsAndCuresPoisonWithSecondAttack()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var abbess = Of(Abbess, true, 1, "abbess");
        var knight = Of(Knight, true, 0, "knight");
        knight.Hp = 50;
        knight.Dots.Add(new DotEffect { Class = AttackClass.Poison, Damage = 20, AppliedRound = 1, Long = true });
        var state = State(abbess, knight, Of(Squire, false, 0, "enemy"));
        var sim = new BattleSim(state, Zeros(), C);

        var log = sim.Act(1, abbess);

        Assert.Equal(AttackClass.Cure, abbess.Attack2!.Class);
        Assert.Empty(knight.Dots);
        Assert.True(knight.Hp > 50);
        Assert.Contains(new ActionNote(1, "abbess", "knight", 2, AttackClass.Cure), state.Actions);
        Assert.Contains(new AllyNote(1, "abbess", "knight", AttackClass.Cure, Attack: 2), sim.AllyLog);
        Assert.Single(log);   // запись хода — только лечение
    }

    /// <summary>Пометка 5: Патриарх по павшему — лечение павшего не трогает, вторая атака поднимает на половину потолка.</summary>
    [SkippableFact] public void Patriarch_RevivesFallenWithSecondAttack_HealDoesNotTouchDead()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var patriarch = Of(Patriarch, true, 1, "patriarch");
        var squire = Of(Squire, true, 0, "squire");
        squire.Hp = 0;
        var state = State(patriarch, squire, Of(Squire, false, 0, "enemy"));
        var session = Session(state);
        Assert.Same(patriarch, session.Current);
        Assert.Contains(squire, session.Targets());
        int round = session.Round;

        var log = session.Attack(squire);

        Assert.Equal(squire.HpMax / 2, squire.Hp);
        Assert.True(squire.Revived);
        Assert.Equal(new TurnRecord(round, "patriarch", "squire", false, 0, squire.HpMax / 2, Attack: 2), Assert.Single(log));
    }

    /// <summary>Харнесс по журналу оригинала: ход Иерофанта <c>S143UN0004</c> (g000uu0150) в раунде 1 боя <c>[1281528]</c> — по павшему
    /// <c>S143UN0007</c> (Двойник, потолок 120): <c>attack_new</c> номер 1 (лечение) и номер 2 (воскрешение), в <c>turn_after</c> seq 1286030 —
    /// HP 60. Состояние — снимок <c>turn_before</c> seq 1286006.</summary>
    [SkippableFact] public void Corpus_HierophantRevivesFallen_AsInOriginal()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var path = Path.Combine(CorpusFiles.Dir, "20260908-f6", "map-001.jsonl");
        Skip.IfNot(File.Exists(path), "corpus/20260908-f6 вне git");
        var battle = JournalCache.Battles(path).Single(b => b.Start.Seq == 1281528);
        Assert.True(CorpusBattleLoader.TryLoad(battle, Rules.Value, out var state, out var reason), reason);
        var before = battle.Events.OfType<TurnEvent>().Single(e => e.Seq == 1286006);
        var after = battle.Events.OfType<TurnEvent>().Single(e => e.Seq == 1286030);
        foreach (var snap in before.Units)
            if (state.Unit(snap.Id) is { } u) u.Hp = (int)snap.Hp;
        state.Round = 1;
        var hierophant = state.Unit("S143UN0004")!;
        var fallen = state.Unit("S143UN0007")!;
        Assert.Equal(0, fallen.Hp);

        new BattleSim(state, Zeros(), C).Act(1, hierophant, fallen);

        Assert.Equal(after.Units.Single(u => u.Id == "S143UN0007").Hp, fallen.Hp);   // 60
        Assert.True(fallen.Revived);   // флаги 8 → 72 (бит 6 revived)
        foreach (var snap in after.Units)
            if (state.Unit(snap.Id) is { } u) Assert.Equal(snap.Hp, u.Hp);
    }

    /// <summary>Харнесс по журналу оригинала: Волчий лорд <c>S143UN0008</c> в раунде 1 боя <c>[1281528]</c> бьёт врага — удар уходит запасной
    /// атаке <c>g000aa1164</c> (REACH «все»): <c>dmg</c> по <c>S143UN000a</c> и <c>S143UN000b</c> (seq 1285976, 1285987), сам не превращается.</summary>
    [SkippableFact] public void Corpus_WolfLordStrikesAllEnemiesWithAltAttack()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var path = Path.Combine(CorpusFiles.Dir, "20260908-f6", "map-001.jsonl");
        Skip.IfNot(File.Exists(path), "corpus/20260908-f6 вне git");
        var battle = JournalCache.Battles(path).Single(b => b.Start.Seq == 1281528);
        Assert.True(CorpusBattleLoader.TryLoad(battle, Rules.Value, out var state, out var reason), reason);
        var before = battle.Events.OfType<TurnEvent>().Single(e => e.Seq == 1285942);
        foreach (var snap in before.Units)
            if (state.Unit(snap.Id) is { } u) u.Hp = (int)snap.Hp;
        var wolf = state.Unit("S143UN0008")!;
        var struck = battle.Events.OfType<DamageEvent>().Where(e => e.Seq is > 1285942 and < 1286000 && e.Attacker == "S143UN0008")
                           .Select(e => e.Target).Order().ToList();
        Assert.Equal(["S143UN000a", "S143UN000b"], struck);

        var log = new BattleSim(state, Zeros(), C).Act(1, wolf, state.Unit("S143UN000a"));

        Assert.Equal(struck, log.Select(r => r.Target).Order().ToList());
        Assert.False(wolf.TransformedSelf);
        Assert.All(state.Actions, a => Assert.Equal(AttackClass.Damage, a.Class));
    }

    /// <summary>Пометка 9: человеку у превращения себя цели — враги (запасная атака) и сам (<c>0x660e11</c>); щелчок по себе превращает.</summary>
    [SkippableFact] public void Human_TransformSelf_TargetsSelfAndEnemies_SelfTransforms()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var wolf = Of(WolfLord, true, 0, "wolf");
        var e0 = Of(Squire, false, 0, "e0");
        var e2 = Of(Squire, false, 2, "e2");
        var e4 = Of(Squire, false, 4, "e4");
        var state = State(wolf, e0, e2, e4);
        var session = Session(state);
        Assert.Same(wolf, session.Current);

        Assert.Equal(["e0", "e2", "e4", "wolf"], session.Targets().Select(u => u.Id).Order().ToList());
        Assert.DoesNotContain(wolf, BattleTargets.Possible(state, wolf, wolf.Attack));   // ИИ решает сам (AiRareTargets)

        session.Attack(wolf);

        Assert.True(wolf.TransformedSelf);
    }

    /// <summary>Пометка 3: у человека вампир (смерть) бьёт упырей с <c>Always</c> к смерти — цели есть (фильтр <c>0x65a1a5</c> только у ИИ и
    /// автобоя); у ИИ — нет.</summary>
    [SkippableFact] public void Human_ElderVampire_CanTargetDeathImmuneGhouls()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var vampire = Of(ElderVampire, true, 0, "vampire");
        var ghoul = Of(Ghoul, false, 0, "ghoul");
        ghoul.Paralyzed = true;   // ход упыря пропущен — первым ходит вампир
        var state = State(vampire, ghoul);
        var session = Session(state);
        Assert.Same(vampire, session.Current);

        Assert.Equal([ghoul], session.Targets());
        Assert.Empty(BattleTargets.Possible(state, vampire, vampire.Attack));
        Assert.Contains(BattleCommand.Attack, session.Allowed);
    }

    /// <summary>Пометки 3–4: призыв человека — на выбранную позицию, в том числе на клетку павшего; павший призванный в следующем ходу уходит
    /// с поля (костей и места в панели нет), его клетка снова годна для призыва.</summary>
    [SkippableFact] public void Human_SummonsIntoChosenCell_FallenSummonLeavesBattle()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var summoner = Of(Elementalist, true, 1, "summoner");
        var dead = Of(Knight, true, 2, "dead");
        dead.Hp = 0;
        var enemy = Of(Squire, false, 0, "enemy");
        var state = State(summoner, dead, enemy);
        var session = Session(state);
        Assert.Same(summoner, session.Current);

        Assert.Empty(session.Targets());
        Assert.Equal([0, 2, 3, 4, 5], session.SummonSlots());
        session.SummonAt(2);

        var summoned = Assert.Single(state.Units, u => u.Summoned);
        Assert.Equal(2, summoned.Slot);
        Assert.Contains(state.Actions, a => a is { Unit: "summoner", Target: "summoner", Attack: 1, Class: AttackClass.Summon });

        summoned.Hp = 0;   // убит
        var sim = session.Sim;
        sim.ClearFallenSummons();
        Assert.DoesNotContain(summoned, state.Units);
        Assert.Same(summoned, state.Unit(summoned.Id));   // для опыта и показа смерти
    }

    /// <summary>Партия 17.10: обе клетки живого большого (тролль на 2 и 3) — не позиции призыва человека; щелчок по второй клетке не
    /// призывает (<see cref="BattleState.SummonCells"/>, <c>0x662f80</c>).</summary>
    [SkippableFact] public void Human_SummonSlots_SkipBothCellsOfBigUnit()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var summoner = Of(Elementalist, true, 1, "summoner");
        var troll = Of("g000uu5016", true, 2, "troll");
        var state = State(summoner, troll, Of(Squire, false, 0, "enemy"));
        var session = Session(state);
        Assert.Same(summoner, session.Current);

        Assert.Equal([0, 4, 5], session.SummonSlots());
        Assert.Empty(session.SummonAt(3));
        Assert.DoesNotContain(state.Units, u => u.Summoned);
        session.SummonAt(5);
        Assert.Equal(5, Assert.Single(state.Units, u => u.Summoned).Slot);
    }
}
