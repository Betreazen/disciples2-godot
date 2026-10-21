using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Rng;
using D2.Rules.Units;
using Xunit.Abstractions;

/// <summary>Задачи 19–25 Ф7: боевые правила, где русская вики спорила с портом. Решал код движка
/// (GOG 3.01), адреса — в XML-doc правил. Юниты здесь синтетические: проверяется правило, а не
/// данные ванили. Вани́льные двойники — в <c>ImmunityTests</c>, <c>AttackClassWardTests</c>,
/// <c>AttackClassTests</c>.</summary>
public class WikiBattleRulesTests
{
    private static readonly RulesConstants C = new(BatInit: 10, BatDamage: 6);
    private const int Fire = 4;

    private static AttackType Atk(AttackClass cls, int dam = 25, int source = 0, int level = 0, int reach = 2) =>
        new($"a-{cls}-{dam}-{level}", 50, cls, 80, reach, dam, 0, false, level, Source: source);

    private static BattleUnit U(string id, AttackType attack, bool attacker, AttackType? second = null,
                                IReadOnlyList<ImmunityGrant>? bySource = null) =>
        new()
        {
            Id = id, Attack = attack, Attack2 = second, Attacker = attacker, Slot = 0, HpMax = 100, Hp = 100,
            // Id типа — десять символов, как у настоящего (Formulas.DamageMax сверяет с прошитыми лидерами).
            Type = new UnitType("g000uu9999", 1, 100, 0, attack.Id, second?.Id ?? "g000000000", false, true, 10, 100,
                                ImmunitiesBySource: bySource),
        };

    private static BattleState State(params BattleUnit[] units) =>
        new() { Units = [.. units], AttackerGroup = "S073KC0001", DefenderGroup = "S073KC0002", Round = 1 };

    // ---- 19: вторая атака по цели, иммунной к первой ---------------------------------------------

    [Fact] public void SecondAttack_IsNotAppliedToTargetImmuneToFirst()
    {
        var attacker = U("thug", Atk(AttackClass.Damage, source: Fire), true, Atk(AttackClass.Poison, dam: 10));
        var immune = U("dragon", Atk(AttackClass.Damage), false, bySource: [new ImmunityGrant(Fire, ImmunityLevel.Always)]);
        var rng = new ScriptedRng([0, 0]);                 // только точность первой атаки

        new BattleSim(State(attacker, immune), rng, C).Act(1, attacker, immune);

        Assert.Equal(2, rng.Used);
        Assert.Empty(immune.Dots);

        var plain = U("squire", Atk(AttackClass.Damage), false);
        var hit = new ScriptedRng([0, 0, 0, 0, 0]);        // точность + урон, затем точность яда
        new BattleSim(State(attacker, plain), hit, C).Act(1, attacker, plain);
        Assert.Equal(5, hit.Used);
        Assert.Single(plain.Dots);
    }

    // ---- 20: бросок раньше иммунитета, Once тратится и на промахе --------------------------------

    [Fact] public void Immunity_IsCheckedAfterTheMissRoll_AndOnceIsSpentEvenOnMiss()
    {
        var attacker = U("caster", Atk(AttackClass.Damage, source: Fire), true);
        var target = U("ward", Atk(AttackClass.Damage), false, bySource: [new ImmunityGrant(Fire, ImmunityLevel.Once)]);

        var missed = Attacks.Apply(attacker, attacker.Attack, target, new ScriptedRng([99, 99]), C, round: 1);
        Assert.True(missed.Miss);
        Assert.True(missed.Immune);
        Assert.Equal(1 << Fire, target.SourceImmunityUsed);   // заряд ушёл на промахе

        var through = Attacks.Apply(attacker, attacker.Attack, target, new ScriptedRng([0, 0, 0]), C, round: 1);
        Assert.False(through.Immune);
        Assert.Equal(25, through.Damage);
    }

    // ---- 22: «Исцеление» -------------------------------------------------------------------------

    [Fact] public void Cure_KeepsFearAndLowerings_ClearsDisableAtNextTurnStart()
    {
        var healer = U("healer", Atk(AttackClass.Cure, dam: 0), true);
        var ally = U("ally", Atk(AttackClass.Damage), true);
        ally.Paralyzed = ally.Retreating = true;
        ally.LowerDamageLevel = 2;
        ally.Dots.Add(new DotEffect { Class = AttackClass.Frostbite, Damage = 5, AppliedRound = 1, Long = true });
        var state = State(healer, ally, U("enemy", Atk(AttackClass.Damage), false));

        Assert.True(Attacks.Apply(healer, healer.Attack, ally, new ScriptedRng([]), C, round: 1).Applied);
        Assert.Empty(ally.Dots);
        Assert.True(ally.Paralyzed && ally.Cured);

        // Отложенная часть идёт в начале хода ЛЮБОГО юнита (0x628874 обходит всех). Ход берём у лекаря:
        // в свой ход напуганный союзник просто ушёл бы с поля.
        new BattleSim(state, new ScriptedRng([]), C).BeginTurn(healer);
        Assert.False(ally.Paralyzed);
        Assert.False(ally.Cured);
        Assert.True(ally.Retreating);
        Assert.Equal(2, ally.LowerDamageLevel);
        Assert.False(Attacks.CanBeCured(U("healthy", Atk(AttackClass.Damage), true)));
    }

    // ---- 23: повторное наложение ----------------------------------------------------------------

    [Fact] public void Poison_WeakerDoesNotReplaceStronger_EqualOrStrongerDoes()
    {
        var weak = U("hornet", Atk(AttackClass.Damage), true, Atk(AttackClass.Poison, dam: 10));
        var target = U("victim", Atk(AttackClass.Damage), false);
        target.Dots.Add(new DotEffect { Class = AttackClass.Poison, Damage = 20, AppliedRound = 1, Long = true });

        Assert.False(Attacks.CanPerform(weak, weak.Attack2!, target));
        var rng = new ScriptedRng([0, 0, 0]);              // первая атака; второй бросков нет
        new BattleSim(State(weak, target), rng, C).Act(1, weak, target);
        Assert.Equal(3, rng.Used);
        Assert.Equal(20, Assert.Single(target.Dots).Damage);

        Assert.True(Attacks.CanPerform(weak, Atk(AttackClass.Poison, dam: 20), target));
        Assert.True(Attacks.CanPerform(weak, Atk(AttackClass.Frostbite, dam: 1), target));   // другой вид
    }

    [Fact] public void BoostDamage_NotAboveExistingLevelIsRefused_UnlessHealerCanCure()
    {
        var booster = U("druid", Atk(AttackClass.BoostDamage, dam: 0, level: 2), true);
        var ally = U("ally", Atk(AttackClass.Damage), true);
        ally.BoostDamageLevel = 2;
        Assert.False(Attacks.CanPerform(booster, booster.Attack, ally));
        Assert.True(Attacks.CanPerform(booster, Atk(AttackClass.BoostDamage, dam: 0, level: 3), ally));

        var caster = U("mage", Atk(AttackClass.Heal, dam: 0), true);   // без атаки с уроном
        Assert.False(Attacks.CanPerform(booster, booster.Attack, caster));
        Assert.False(Attacks.Apply(booster, booster.Attack, caster, new ScriptedRng([]), C, 1).Applied);

        var matriarch = U("matriarch", booster.Attack, true, Atk(AttackClass.Cure, dam: 0));
        ally.Paralyzed = true;
        Assert.True(Attacks.CanPerform(matriarch, matriarch.Attack, ally));
    }

    // ---- 24: «Ждать» ----------------------------------------------------------------------------

    [Fact] public void Wait_PutsUnitAtEndOfRound_OncePerRound_NotInRoundZero()
    {
        var a = U("a", Atk(AttackClass.Damage), true);
        var d = U("d", Atk(AttackClass.Damage), false);
        var state = State(a, d);
        var sim = new BattleSim(state, new ScriptedRng([0, 0, 0, 0]), C);

        var queue = sim.BeginRound(1);
        var first = queue[0];
        Assert.True(sim.Wait(first));
        Assert.Equal(3, queue.Count);
        Assert.Same(first, queue[^1]);
        Assert.False(sim.Wait(first));                     // второй раз в том же раунде нельзя

        sim.BeginRound(2);
        Assert.False(first.Waited);
        state.Round = 0;
        Assert.False(sim.Wait(first));
    }

    /// <summary>«Бежать» в отложенный ход: бит 7 (<c>0x65a01b</c>), следующий раунд юнит пропускает и броска не тратит
    /// (<c>0x624637</c> → <c>0x62464e</c>), остаётся на поле под ударами и уходит в свой ход раунда после — как
    /// <c>S143UN003a</c> журнала 20261016-port-synth passive-001 (WR: ждал и бежал в раунде 1, нет в очереди 2, ушёл в 3).
    /// Бегство без ожидания — обычное: уходит в свой ход следующего раунда.</summary>
    [Fact] public void RetreatAfterWait_SkipsNextRound_LeavesTheRoundAfter()
    {
        var waiter = U("waiter", Atk(AttackClass.Damage), true);
        var plain = U("plain", Atk(AttackClass.Damage), true);
        var d = U("d", Atk(AttackClass.Damage), false);
        var state = State(waiter, plain, d);
        var rolls = new ScriptedRng([0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var sim = new BattleSim(state, rolls, C);

        sim.BeginRound(1);
        Assert.True(sim.Wait(waiter));
        sim.Retreat(plain);
        sim.Retreat(waiter);
        Assert.True(waiter.RetreatingAfterWait);
        Assert.False(plain.RetreatingAfterWait);

        int before = rolls.Used;
        var round2 = sim.BeginRound(2);
        Assert.DoesNotContain(waiter, round2);
        Assert.Contains(plain, round2);
        Assert.Equal(2, rolls.Used - before);               // бросок только у двоих в очереди
        Assert.False(waiter.RetreatingAfterWait);           // бит снят построением
        Assert.True(waiter.Retreating && waiter.OnField);   // на поле, бит 29 держится
        Assert.False(sim.BeginTurn(plain));
        Assert.True(plain.Retreated);

        var round3 = sim.BeginRound(3);
        Assert.Contains(waiter, round3);
        Assert.False(sim.BeginTurn(waiter));
        Assert.True(waiter.Retreated);
    }

    // ---- 25: страх, когда бежать некуда ---------------------------------------------------------

    [Fact] public void Fear_OnDefenderThatCannotRetreat_Paralyzes()
    {
        var shaman = U("shaman", Atk(AttackClass.Fear, dam: 0), true);
        var guard = U("guard", Atk(AttackClass.Damage), false);
        var garrison = new BattleState
        {
            Units = [shaman, guard], AttackerGroup = "S073KC0001", DefenderGroup = "S073FT0001", Round = 1,
            DefendersCanRetreat = false,
        };
        Attacks.Apply(shaman, shaman.Attack, guard, new ScriptedRng([0, 0]), C, 1, garrison);
        Assert.True(guard.Paralyzed);
        Assert.False(guard.Retreating);

        var field = U("field", Atk(AttackClass.Damage), false);
        Attacks.Apply(shaman, shaman.Attack, field, new ScriptedRng([0, 0]), C, 1, State(shaman, field));
        Assert.True(field.Retreating);
    }

    [Fact] public void DefendersCanRetreat_NotFromFortRuinOrStackInCity()
    {
        static BattleStartEvent Start(string defender, long fort) =>
            new(1, 1, "S073KC0001", defender, "p1", "p2", 0,
                [new UnitState("u", 100, 0, 0, 0, 0, 0, fort, null, null, null)]);
        Assert.False(CorpusBattleLoader.DefendersCanRetreat(Start("S073FT0001", 20)));
        Assert.False(CorpusBattleLoader.DefendersCanRetreat(Start("S073RU0001", 0)));
        Assert.False(CorpusBattleLoader.DefendersCanRetreat(Start("S073KC0002", 10)));
        Assert.True(CorpusBattleLoader.DefendersCanRetreat(Start("S073KC0002", 0)));
    }

    // ---- быстрое бегство: FastRetreat (LmodifE 16) лидера — CBatAttackRetreat::onHit 0x659fee ------------

    private static readonly Modifier Escape = new("g000um7546", [new ModifierElement(ModifierElementType.FastRetreat)]);

    private static BattleUnit WithEscape(string id, bool leader) =>
        new()
        {
            Id = id, Attack = Atk(AttackClass.Damage), Attacker = false, Slot = 1, HpMax = 100, Hp = 100, Modifiers = [Escape],
            Type = new UnitType("g000uu9998", 1, 100, 0, "a", "g000000000", false, true, 10, 100) { IsLeader = leader },
        };

    [Fact] public void FastRetreat_LeaderLeavesFieldAtOnce_OthersNextTurn()
    {
        var enemy = U("enemy", Atk(AttackClass.Damage), true);
        var leader = WithEscape("leader", leader: true);
        var plain = U("plain", Atk(AttackClass.Damage), false);
        var state = State(enemy, leader, plain);
        var sim = new BattleSim(state, new ScriptedRng([]), C);

        sim.Retreat(leader);                     // 0x65a06a: бит 28 сразу, бита 29 нет
        Assert.True(leader.Retreated);
        Assert.False(leader.Retreating);
        Assert.False(leader.OnField);
        Assert.Null(sim.Winner);

        sim.Retreat(plain);                      // без флага — бит 29 (0x65a089), уходит в свой следующий ход
        Assert.True(plain.Retreating);
        Assert.False(plain.Retreated);
        state.Round = 2;
        Assert.False(sim.BeginTurn(plain));
        Assert.True(plain.Retreated);
        Assert.Equal("S073KC0001", sim.Winner);  // защитники все ушли — бой за нападающим
    }

    [Fact] public void FastRetreat_IgnoredOnNonLeader()
    {
        // Приведение к IUsStackLeader (0x40a41f) у не-лидера даёт null — флаг не читается (0x65a04c).
        var soldier = WithEscape("soldier", leader: false);
        var state = State(U("enemy", Atk(AttackClass.Damage), true), soldier);

        new BattleSim(state, new ScriptedRng([]), C).Retreat(soldier);

        Assert.True(soldier.Retreating);
        Assert.False(soldier.Retreated);
    }

    // ---- 31: паралич раньше бегства ------------------------------------------------------------------

    [Fact] public void ParalyzedRetreatingUnit_SkipsTurnAndStaysOnFieldUntilNextTurn()
    {
        var enemy = U("enemy", Atk(AttackClass.Damage), true);
        var coward = U("coward", Atk(AttackClass.Damage), false);
        coward.Paralyzed = true;
        coward.Retreating = true;
        var state = State(enemy, coward);
        var sim = new BattleSim(state, new ScriptedRng([]), C);

        Assert.False(sim.BeginTurn(coward));
        Assert.True(coward.Retreating);
        Assert.False(coward.Retreated);

        state.Round = 2;
        Assert.False(sim.BeginTurn(coward));
        Assert.True(coward.Retreated);
        Assert.False(coward.Retreating);
    }

    // ---- длинный паралич и отказ Fear/Paralyze/Petrify по обездвиженному (20260920-gaps [393470]) ----

    [Fact] public void LongParalysis_RollsEachOwnTurn_AndTurnIsLostEvenWhenItBreaks()
    {
        var specter = U("specter", Atk(AttackClass.Paralyze, dam: 0) with { Infinite = true }, false);
        var leader = U("leader", Atk(AttackClass.Damage), true);
        var state = State(specter, leader);
        Attacks.Apply(specter, specter.Attack, leader, new ScriptedRng([0, 0]), C, 1, state);
        Assert.True(leader.Paralyzed && leader.DisableLong);

        // Раунд 2: rng 21 → 22 < 90 − 10·1 — держит. Раунд 3: rng 80 → 81 ≥ 90 − 10·2 — снят, но ход потерян.
        state.Round = 2;
        var hold = new ScriptedRng([21]);
        Assert.False(new BattleSim(state, hold, C).BeginTurn(leader));
        Assert.Equal(1, hold.Used);
        Assert.True(leader.Paralyzed);
        Assert.Empty(state.Notes);
        state.Round = 3;
        Assert.False(new BattleSim(state, new ScriptedRng([80]), C).BeginTurn(leader));
        Assert.False(leader.Paralyzed || leader.DisableLong);
        // Сообщение боя для показа и звука (docs/ai-screen.md §11): снят паралич — в раунде 3, у лидера.
        Assert.Equal([new BattleNote(3, leader.Id, BattleNoteKind.Unparalyzed)], state.Notes);
    }

    [Fact] public void FearParalyzeAndPetrify_AreRefusedOnDisabledTarget()
    {
        var caster = U("caster", Atk(AttackClass.Damage), false);
        var stunned = U("stunned", Atk(AttackClass.Damage), true);
        stunned.Paralyzed = true;
        foreach (var cls in new[] { AttackClass.Fear, AttackClass.Paralyze, AttackClass.Petrify })
            Assert.False(Attacks.CanPerform(caster, Atk(cls, dam: 0), stunned));
        stunned.Paralyzed = false;
        stunned.Petrified = true;
        Assert.False(Attacks.CanPerform(caster, Atk(AttackClass.Fear, dam: 0), stunned));
        Assert.True(Attacks.CanPerform(caster, Atk(AttackClass.Damage), stunned));

        // Одиночная цель тоже проходит canPerform: ни бросков, ни записи.
        var petrifier = U("medusa", Atk(AttackClass.Petrify, dam: 0), false);
        var rng = new ScriptedRng([]);
        Assert.Empty(new BattleSim(State(petrifier, stunned), rng, C).Act(1, petrifier, stunned));
        Assert.Equal(0, rng.Used);
    }

    // ---- 32: атака предмета без броска точности ------------------------------------------------------

    [Fact] public void ItemAttack_DoesNotRollAccuracy_ButImmunityStillApplies()
    {
        var bearer = U("bearer", Atk(AttackClass.Poison, dam: 10, source: Fire), true);
        var target = U("target", Atk(AttackClass.Damage), false);
        var noDice = new ScriptedRng([]);

        Attacks.Apply(bearer, bearer.Attack, target, noDice, C, 1, State(bearer, target), itemAttack: true);
        Assert.Equal(0, noDice.Used);
        Assert.Single(target.Dots);

        var immune = U("salamander", Atk(AttackClass.Damage), false,
                       bySource: [new ImmunityGrant(Fire, ImmunityLevel.Always)]);
        Attacks.Apply(bearer, bearer.Attack, immune, new ScriptedRng([]), C, 1, State(bearer, immune), itemAttack: true);
        Assert.Empty(immune.Dots);

        var rolled = new ScriptedRng([0, 0]);
        Attacks.Apply(bearer, bearer.Attack, U("plain", Atk(AttackClass.Damage), false), rolled, C, 1);
        Assert.Equal(2, rolled.Used);
        Assert.False(Attacks.RollsAccuracy(AttackClass.Poison, itemAttack: true));
        Assert.True(Attacks.RollsAccuracy(AttackClass.Poison, itemAttack: false));
    }
}

/// <summary>Корпусные счётчики задач 19–25 Ф7: правило модели против журнала. Падает, если есть
/// расхождение или если по счётчику, для которого в корпусе есть случаи, не нашлось ни одной сверки.</summary>
[Trait("Category", "Corpus")]
public class WikiBattleCorpusTests(ITestOutputHelper output)
{
    private const long Paralyze = 1L << 2, Petrify = 1L << 3, DisableLong = 1L << 4, Dead = 1L << 1;
    private const long Cured = 1L << 21, Retreat = 1L << 29, Retreated = 1L << 28, Summoned = 1L << 27;

    /// <summary>Биты, которые модель держит и которые затрагивает «Исцеление».</summary>
    private static readonly long CureMask = Paralyze | Petrify | (1L << 10) | (1L << 11) | (1L << 13) |
                                            (1L << 15) | (1L << 17) | (1L << 19) | Cured | Retreat;

    [SkippableFact] public void WikiRules_MatchJournal()
    {
        Skip.IfNot(CorpusFiles.Available, "нет корпуса");
        var s = new Counter();
        var (files, _) = JournalSet.Distinct(CorpusFiles.Journals());
        long waited = 0, snapshots = 0;
        foreach (var file in files)
            foreach (var battle in JournalCache.Battles(file))
            {
                var events = battle.Events.OrderBy(e => e.Seq).ToList();
                ImmuneAfterRoll(events, s);
                Fear(battle.Start, events, s);
                Cure(events, s);
                ShortParalysisEnd(events, s);
                RetreatOrder(events, s);
                ItemAttackNoRoll(events, s);
                WaitRules(events, s);
                foreach (var u in events.OfType<TurnEvent>().SelectMany(t => t.Units))
                { snapshots++; if ((u.Flags & 0x10) != 0) waited++; }
            }

        foreach (var line in s.Lines()) output.WriteLine(line);
        output.WriteLine($"waited (бит 4 flags): {waited} из {snapshots} снимков юнитов в turn_*");
        Assert.True(s.Mismatches.Count == 0, string.Join("\n", s.Mismatches.Take(10)));
        foreach (var key in new[] { "immune_after_roll", "fear", "paralysis_short_end", "retreat_free", "retreat_summoned" })
            Assert.True(s.Ok(key) > 0, $"нет ни одной сверки {key}");
        // Ожидание в корпусе появилось 16.10 (20261016-port-synth passive-001, бои человека карты 1): каждое — в конец очереди,
        // бегство ждавшего — бит 7 и пропуск раунда (WaitRules); С2 по журнальной очереди зовёт BattleSim.Wait.
        if (waited > 0)
            foreach (var key in new[] { "wait_to_end", "retreat_after_wait" })
                Assert.True(s.Ok(key) > 0, $"ожидание в журналах есть ({waited} снимков), а сверок {key} нет");
        // Ситуации, которых в корпусе после потери 18.09 нет вовсе (ни одной атаки класса 16, ни одной применённой
        // атаки предмета): расхождения по ним уже проверены выше, а ноль сверок — явный пропуск с тем, что сыграть.
        var missing = new (string Key, string Situation)[]
        {
            ("cure_now", "в бою «Исцеление» (класс 16: Матриарх или Друидесса) по союзнику под параличом или окаменением"),
            ("cure_next_turn", "то же «Исцеление», и вылеченный союзник доживает до своего следующего хода"),
            ("retreat_paralyzed", "юнит под «Страхом» (бегство, бит 29) к началу своего хода ещё и парализован или окаменён"),
            ("item_attack_no_roll", "в бою применить боевую сферу или талисман из боевого слота героя по врагу"),
        }.Where(m => s.Ok(m.Key) == 0).Select(m => $"{m.Key}: нет случая — {m.Situation}").ToList();
        Skip.If(missing.Count > 0, string.Join("; ", missing));
    }

    /// <summary>Задача 20: у класса с точностью событие immune (always_only = false) идёт сразу за
    /// своим miss — бросок раньше иммунитета. Атака предмета (<c>impl</c> типа <c>IM</c>) точности
    /// не бросает вовсе — такие события в сверку не идут, а считаются отдельным пропуском.</summary>
    private static void ImmuneAfterRoll(List<CorpusEvent> events, Counter s)
    {
        CorpusEvent? last = null;
        string impl = "";
        foreach (var e in events)
        {
            if (e is AttackNewEvent an) impl = an.Impl;
            if (e is ImmuneEvent { AlwaysOnly: false } im && Formulas.ClassHasPower((AttackClass)im.Class))
            {
                if (impl.Length >= 6 && impl.Substring(4, 2) == "IM") s.Skip("immune_after_roll", "атака предмета, броска точности нет");
                else s.Count("immune_after_roll", last is MissEvent, $"immune seq {im.Seq}: перед ним {last?.GetType().Name ?? "ничего"}");
            }
            if (e is MissEvent or ImmuneEvent { AlwaysOnly: false } or DamageEvent or AttackNewEvent or TurnEvent or RoundEvent)
                last = e;
        }
    }

    /// <summary>Задача 25: попадание Fear даёт бит 29 цели, которая может бежать, и бит 2 — той,
    /// что не может (<see cref="CorpusBattleLoader.DefendersCanRetreat"/>).</summary>
    private static void Fear(BattleStartEvent start, List<CorpusEvent> events, Counter s)
    {
        bool defendersCan = CorpusBattleLoader.DefendersCanRetreat(start);
        foreach (var (before, after, classes) in ActionWindows(events))
        {
            if (!classes.Contains(7) || classes.Contains(3) || classes.Contains(9)) continue;
            foreach (var u in after.Units)
            {
                long was = before.GetValueOrDefault(u.Id), gained = u.St & ~was;
                if ((gained & (Retreat | Paralyze)) == 0) continue;
                bool can = (u.Flags & 0b1000) != 0 || defendersCan;
                s.Count("fear", (gained & Retreat) != 0 == can, $"Fear seq {after.Seq} {u.Id}: +{gained:x}, можно бежать {can}");
            }
        }
    }

    /// <summary>Задача 22: у цели «Исцеления» (бит 21 появился) после хода лекаря — ровно то, что
    /// оставляет модель; к следующему turn_after паралич, окаменение и Cured сняты.</summary>
    private static void Cure(List<CorpusEvent> events, Counter s)
    {
        var windows = ActionWindows(events).ToList();
        for (int i = 0; i < windows.Count; i++)
        {
            var (before, after, classes) = windows[i];
            if (!classes.Contains(16)) continue;
            foreach (var u in after.Units.Where(u => (u.St & Cured) != 0 && (before.GetValueOrDefault(u.Id) & Cured) == 0))
            {
                var model = FromBits(u.Id, before.GetValueOrDefault(u.Id));
                Attacks.Apply(model, new AttackType("cure", 0, AttackClass.Cure, 100, 2, 0, 0, false, 0), model,
                              new ScriptedRng([]), new RulesConstants(10, 6), 1);
                s.Count("cure_now", (ToBits(model) & CureMask) == (u.St & CureMask),
                        $"Cure seq {after.Seq} {u.Id}: модель {ToBits(model) & CureMask:x}, журнал {u.St & CureMask:x}");
                if (i + 1 >= windows.Count || windows[i + 1].After.Units.FirstOrDefault(x => x.Id == u.Id) is not { } next) continue;
                Attacks.ResolveCured(new BattleState { Units = [model], AttackerGroup = "a", DefenderGroup = "d" });
                const long deferred = Paralyze | Petrify | Cured;
                s.Count("cure_next_turn", (ToBits(model) & deferred) == (next.St & deferred),
                        $"Cure seq {after.Seq} {u.Id}: следующий ход модель {ToBits(model) & deferred:x}, журнал {next.St & deferred:x}");
            }
        }
    }

    /// <summary>Задача 21: короткий паралич (бит 2 без бита 4) снимается в turn_after самой цели.
    /// Последний turn_after боя не считается: там движок снимает паралич у всех (<c>0x6255ff</c>).</summary>
    private static void ShortParalysisEnd(List<CorpusEvent> events, Counter s)
    {
        var turns = events.OfType<TurnEvent>().Where(t => t.After).ToList();
        var state = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < turns.Count; i++)
        {
            foreach (var u in turns[i].Units)
            {
                long was = state.GetValueOrDefault(u.Id);
                state[u.Id] = u.St;
                bool shortEnded = (was & Paralyze) != 0 && (was & DisableLong) == 0 && (u.St & Paralyze) == 0 && (u.St & Dead) == 0;
                if (!shortEnded || (was & Cured) != 0) continue;
                if (i == turns.Count - 1) { s.Skip("paralysis_short_end", "последний ход боя"); continue; }
                s.Count("paralysis_short_end", string.Equals(turns[i].Unit, u.Id, StringComparison.OrdinalIgnoreCase),
                        $"паралич {u.Id} снят в ход {turns[i].Unit}, seq {turns[i].Seq}");
            }
        }
    }

    /// <summary>Задача 31: юнит с битом 29 в своём turn_before уходит с поля (бит 28 в его turn_after) тогда и
    /// только тогда, когда он не парализован и не окаменел — модель (<see cref="BattleSim.BeginTurn"/>) против
    /// журнала. <c>retreat_paralyzed</c> — случаи с битом 2 или 3, <c>retreat_free</c> — без них. Повторный
    /// turn_before того же хода не считается.
    /// <para><c>retreat_summoned</c> — призванный беглец (бит 27): не уходит, а изгоняется — в его turn_after его нет в бою вовсе
    /// (<c>0x628f96</c> → <c>0x628fb4</c> → <c>0x628c00</c>). Первые случаи в корпусе — <c>20261016e-av</c>, карта 8 (снятие призыва):
    /// призванные Призраки, которым человек дал «Отступить», seq 15960701 и ещё 6.</para></summary>
    private static void RetreatOrder(List<CorpusEvent> events, Counter s)
    {
        var seen = new HashSet<(string, long)>();
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is not TurnEvent { After: false } before) continue;
            long st = StOf(before, before.Unit);
            if ((st & Retreat) == 0 || (st & Dead) != 0 || !seen.Add((before.Unit.ToUpperInvariant(), before.Round))) continue;
            bool summoned = (st & Summoned) != 0;
            string key = (st & (Paralyze | Petrify)) != 0 ? "retreat_paralyzed" : summoned ? "retreat_summoned" : "retreat_free";
            var after = events.Skip(i + 1).OfType<TurnEvent>()
                              .FirstOrDefault(t => t.After && string.Equals(t.Unit, before.Unit, StringComparison.OrdinalIgnoreCase));
            if (after is null) { s.Skip(key, "у хода нет turn_after"); continue; }
            bool present = after.Units.Any(u => string.Equals(u.Id, after.Unit, StringComparison.OrdinalIgnoreCase));
            long stAfter = StOf(after, after.Unit);
            if ((stAfter & Dead) != 0) { s.Skip(key, "погиб в начале хода"); continue; }
            bool left = (stAfter & Retreated) != 0;
            var (modelLeft, modelGone) = LeavesOnTurn(st);
            s.Count(key, modelLeft == left && modelGone == !present,
                    $"turn_before seq {before.Seq} {before.Unit}: st {st:x}, в журнале ушёл {left}, изгнан {!present}; модель {modelLeft}, {modelGone}");
        }
    }

    private const long WaitedFlag = 0x10, RetreatingAfterWaitFlag = 0x80;

    /// <summary>«Ждать» и бегство после него против журнала (16.10).
    /// <list type="bullet">
    /// <item><c>wait_to_end</c>: ход, после которого у самого ходящего взвёлся <c>waited</c> (бит 4 flags), ставит его в конец
    /// очереди раунда (<see cref="BattleSim.Wait"/>, <c>0x6224dd</c>) — последний в <c>order</c> его <c>turn_after</c>.</item>
    /// <item><c>retreat_after_wait</c>: ход, после которого ходящий «уходит» (бит 29) — модель (<see cref="BattleSim.Retreat"/> с
    /// <see cref="BattleUnit.Waited"/> из журнала) предсказывает бит 7 (<see cref="BattleUnit.RetreatingAfterWait"/>); в
    /// журнале у следующего <c>round</c> бит 7 у него взведён, и в первую очередь нового раунда он не попал — оба факта
    /// обязаны совпасть с предсказанием (и у ждавших, и у не ждавших беглецов).</item>
    /// </list></summary>
    private static void WaitRules(List<CorpusEvent> events, Counter s)
    {
        var before = new Dictionary<string, TurnEvent>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is not TurnEvent t) continue;
            if (!t.After) { before[t.Unit] = t; continue; }
            if (!before.TryGetValue(t.Unit, out var b) || b.Round != t.Round) continue;
            var was = b.Units.FirstOrDefault(u => string.Equals(u.Id, t.Unit, StringComparison.OrdinalIgnoreCase));
            var now = t.Units.FirstOrDefault(u => string.Equals(u.Id, t.Unit, StringComparison.OrdinalIgnoreCase));
            if (was is null || now is null) continue;
            if ((was.Flags & WaitedFlag) == 0 && (now.Flags & WaitedFlag) != 0)
                s.Count("wait_to_end", t.Order.Count > 0 && string.Equals(t.Order[^1].Id, t.Unit, StringComparison.OrdinalIgnoreCase),
                        $"turn_after seq {t.Seq} {t.Unit}: ждал, очередь [{string.Join(" ", t.Order.Select(o => o.Id))}]");
            if ((was.St & (Retreat | Retreated)) != 0 || (now.St & Retreat) == 0 || (now.St & Dead) != 0) continue;
            var unit = FromBits("u", 0);
            unit.Waited = (was.Flags & WaitedFlag) != 0;
            new BattleSim(new BattleState { Units = [unit], AttackerGroup = "a", DefenderGroup = "d", Round = (int)t.Round },
                          new ScriptedRng([]), new RulesConstants(10, 6)).Retreat(unit);
            int r = events.FindIndex(i + 1, e => e is RoundEvent or BattleEndEvent);
            if (r < 0 || events[r] is not RoundEvent round)
            { s.Skip("retreat_after_wait", "бой кончился до следующего раунда"); continue; }
            // Очередь раунда — у первого turn_before после round: в нём ходящий ещё в голове (turn_after сразу за round — это
            // конец хода прошлого раунда, а у последующих turn_after сходившие уже сняты).
            var queue = events.Skip(r + 1).OfType<TurnEvent>().FirstOrDefault(x => !x.After)?.Order;
            if (queue is null) { s.Skip("retreat_after_wait", "после раунда нет хода"); continue; }
            if (round.Units.FirstOrDefault(u => string.Equals(u.Id, t.Unit, StringComparison.OrdinalIgnoreCase)) is not { } inRound
                || (inRound.St & (Dead | Retreated)) != 0)
            { s.Skip("retreat_after_wait", "к новому раунду беглец пал или уже ушёл"); continue; }
            bool flag = round.Units.Any(u => string.Equals(u.Id, t.Unit, StringComparison.OrdinalIgnoreCase)
                                             && (u.Flags & RetreatingAfterWaitFlag) != 0);
            bool skipped = !queue.Any(o => string.Equals(o.Id, t.Unit, StringComparison.OrdinalIgnoreCase));
            s.Count("retreat_after_wait", unit.RetreatingAfterWait == flag && flag == skipped,
                    $"turn_after seq {t.Seq} {t.Unit}: ждал {unit.Waited}; модель бит 7 {unit.RetreatingAfterWait}, журнал бит 7 {flag}, " +
                    $"нет в очереди раунда {round.Round} {skipped}");
        }
    }

    private static long StOf(TurnEvent t, string id) =>
        t.Units.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase))?.St ?? 0;

    /// <summary>Модель: юнит только с битами 2, 3, 27, 29 из журнала начинает ход против живого врага; призванный — рядом со своим
    /// живым призывателем. Ответ — ушёл ли с поля и изгнан ли из боя.</summary>
    private static (bool Left, bool Gone) LeavesOnTurn(long st)
    {
        var unit = FromBits("u", st & (Paralyze | Petrify | Retreat));
        var enemy = new BattleUnit { Id = "enemy", Attack = unit.Attack, Attacker = false, Slot = 0, HpMax = 100, Hp = 100, Type = unit.Type };
        var state = new BattleState { Units = [unit, enemy], AttackerGroup = "a", DefenderGroup = "d", Round = 1 };
        if ((st & Summoned) != 0)
        {
            var summoner = new BattleUnit { Id = "summoner", Attack = unit.Attack, Attacker = true, Slot = 1, HpMax = 100, Hp = 100, Type = unit.Type };
            unit.Summoned = true;
            unit.SummonerId = summoner.Id;
            state.Units.Add(summoner);
        }
        new BattleSim(state, new ScriptedRng([]), new RulesConstants(10, 6)).BeginTurn(unit);
        return (unit.Retreated, !state.Units.Contains(unit));
    }

    /// <summary>Задача 32: применённая атака предмета (<c>attack_new</c> с <c>impl</c> типа <c>IM</c>, за которым до
    /// следующего действия идёт <c>immune</c> с <c>always_only = false</c>) не пишет <c>miss</c> — модель
    /// <see cref="Attacks.RollsAccuracy"/> против журнала. Построенная, но не применённая атака — пропуск.</summary>
    private static void ItemAttackNoRoll(List<CorpusEvent> events, Counter s)
    {
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is not AttackNewEvent an || an.Impl.Length < 6 || an.Impl.Substring(4, 2) != "IM") continue;
            var window = events.Skip(i + 1).TakeWhile(e => e is not (AttackNewEvent or TurnEvent or RoundEvent)).ToList();
            if (!window.Any(e => e is ImmuneEvent { AlwaysOnly: false }))
            { s.Skip("item_attack_no_roll", "атака предмета построена, но не применена"); continue; }
            bool rolled = window.Any(e => e is MissEvent);
            s.Count("item_attack_no_roll", rolled == Attacks.RollsAccuracy((AttackClass)an.Class, itemAttack: true),
                    $"атака предмета {an.Impl} ({an.Attack}) seq {an.Seq}: miss в журнале {rolled}");
        }
    }

    /// <summary>Действие юнита: статусы перед ним (последний снимок turn_* или battle_start), его
    /// turn_after и классы атак, созданных в окне (<c>attack_new</c>). Снимок события round в окно не
    /// берётся: зонд пишет round следующего раунда посреди хода, уже после удара (см. corpus-schema).</summary>
    private static IEnumerable<(Dictionary<string, long> Before, TurnEvent After, HashSet<long> Classes)> ActionWindows(List<CorpusEvent> events)
    {
        var last = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var classes = new HashSet<long>();
        foreach (var e in events)
        {
            if (e is AttackNewEvent an) classes.Add(an.Class);
            // battle_start пишется перед КАЖДЫМ раундом, в том числе посреди хода (копия BattleMsgData,
            // CorpusReader.Battles их не делит) — статусы берутся только из первой, до всех ходов.
            var units = e switch { TurnEvent t => t.Units, BattleStartEvent b when last.Count == 0 => b.Units, _ => null };
            if (e is TurnEvent { After: true } after)
            {
                yield return (new Dictionary<string, long>(last, StringComparer.OrdinalIgnoreCase), after, classes);
                classes = [];
            }
            if (units is not null) foreach (var u in units) last[u.Id] = u.St;
        }
    }

    private static BattleUnit FromBits(string id, long st)
    {
        var attack = new AttackType("x", 50, AttackClass.Damage, 80, 2, 10, 0, false, 0);
        var u = new BattleUnit
        {
            Id = id, Attack = attack, Attacker = true, Slot = 0, HpMax = 100, Hp = 100,
            Type = new UnitType(id, 1, 100, 0, "x", "g000000000", false, true, 0, 0),
            Paralyzed = (st & Paralyze) != 0, Petrified = (st & Petrify) != 0, Retreating = (st & Retreat) != 0,
            LowerDamageLevel = (st & (1L << 10)) != 0 ? 1 : (st & (1L << 11)) != 0 ? 2 : 0,
            LowerInitiativeLevel = (st & (1L << 13)) != 0 ? 1 : 0, Cured = (st & Cured) != 0,
        };
        foreach (var (bit, cls) in new[] { (15, AttackClass.Poison), (17, AttackClass.Frostbite), (19, AttackClass.Blister) })
            if ((st & (1L << bit)) != 0) u.Dots.Add(new DotEffect { Class = cls, Damage = 1, AppliedRound = 0, Long = false });
        return u;
    }

    private static long ToBits(BattleUnit u)
    {
        long st = 0;
        if (u.Paralyzed) st |= Paralyze;
        if (u.Petrified) st |= Petrify;
        if (u.Retreating) st |= Retreat;
        if (u.Cured) st |= Cured;
        if (u.LowerDamageLevel == 1) st |= 1L << 10;
        if (u.LowerDamageLevel == 2) st |= 1L << 11;
        if (u.LowerInitiativeLevel == 1) st |= 1L << 13;
        foreach (var d in u.Dots)
            st |= 1L << (d.Class switch { AttackClass.Poison => 15, AttackClass.Frostbite => 17, _ => 19 });
        return st;
    }

    private sealed class Counter
    {
        private readonly SortedDictionary<string, (int Ok, int Bad)> _counts = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _skips = new(StringComparer.Ordinal);
        public List<string> Mismatches { get; } = [];

        public void Count(string key, bool ok, string why)
        {
            var (o, b) = _counts.GetValueOrDefault(key);
            _counts[key] = ok ? (o + 1, b) : (o, b + 1);
            if (!ok) Mismatches.Add($"{key}: {why}");
        }

        public void Skip(string key, string why) => _skips[$"{key}: {why}"] = _skips.GetValueOrDefault($"{key}: {why}") + 1;
        public int Ok(string key) => _counts.GetValueOrDefault(key).Ok;

        public IEnumerable<string> Lines() =>
            _counts.Select(kv => $" {kv.Key,-20} сошлось={kv.Value.Ok,-6} разошлось={kv.Value.Bad}")
                   .Concat(_skips.Select(kv => $" пропуск {kv.Key} — {kv.Value}"));
    }
}
