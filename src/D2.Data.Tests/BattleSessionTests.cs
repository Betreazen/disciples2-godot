using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Rng;
using Xunit;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Бой по шагам (<see cref="BattleSession"/>) — тот же бой, что у модели целиком: ход за ходом
/// экран ведёт <see cref="BattleSim"/> в порядке <see cref="BattleSim.Run"/>.</summary>
public class BattleSessionTests(ITestOutputHelper output)
{
    private static string Rules => ImportedData.PathOf("rules");

    private static string Sample
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
            return d is null ? "" : Path.Combine(d.FullName, "corpus", "sample", "one_battle.jsonl");
        }
    }

    private static BattleState Load(GameRules rules)
    {
        var battle = JournalCache.Battles(Sample).First(b => b.End is not null);
        Assert.True(CorpusBattleLoader.TryLoad(battle, rules, out var state, out string reason), reason);
        return state;
    }

    /// <summary>Ход без удара — для показа надписью и вспышкой (<see cref="BattleSession.TurnShows"/>, шаги просмотра 9 и 10): «Защищаться» и
    /// «Ждать» человека записываются ходящему.</summary>
    [SkippableFact]
    public void TurnShows_DefendAndWait()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        while (!session.Finished && !session.WaitsPlayer) session.AiStep();
        Skip.If(session.Finished || session.Current is null, "в образце нет хода человека");
        var defender = session.Current!;
        Assert.True(session.Defend());
        Assert.Equal(new BattleTurnNote(defender.Id, BattleTurnShow.Defend), session.TurnShows[^1]);
        while (!session.Finished && !(session.WaitsPlayer && session.Allowed.Contains(BattleCommand.Wait))) session.AiStep();
        Skip.If(session.Finished, "бой кончился до «Ждать»");
        var waiter = session.Current!;
        Assert.True(session.Wait());
        Assert.Equal(new BattleTurnNote(waiter.Id, BattleTurnShow.Wait), session.TurnShows[^1]);
    }

    [SkippableFact]
    public void Session_PlaysTheSameBattleAsRun()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);

        var whole = new BattleSim(Load(rules), new EngineRng(), rules.Constants);
        var expected = whole.Run().ToList();

        var state = Load(rules);
        var sim = new BattleSim(state, new EngineRng(), rules.Constants);
        var session = new BattleSession(state, sim, rules.Constants);
        var got = session.Resolve(200).ToList();

        output.WriteLine($"ходов целиком {expected.Count}, по шагам {got.Count}, победитель {session.Winner ?? "-"}");
        Assert.Equal(whole.Winner, session.Winner);
        Assert.Equal(expected.Count, got.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], got[i]);
    }

    /// <summary>На ходу игрока экран ждёт команду, и доступны те действия, что и в игре: удар, защита,
    /// пропуск, отступление, ожидание (<c>docs/battle-screen.md</c> §1).</summary>
    [SkippableFact]
    public void PlayerTurn_OffersEngineActions()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);

        while (!session.Finished && !session.WaitsPlayer) session.AiStep();
        Skip.If(session.Finished, "в образце нет хода стороны игрока");

        var allowed = session.Allowed;
        output.WriteLine($"ходит {session.Current!.Id}, доступно {string.Join(", ", allowed)}, целей {session.Targets().Count}");
        Assert.Contains(BattleCommand.Defend, allowed);
        Assert.Contains(BattleCommand.Skip, allowed);
        Assert.Contains(BattleCommand.Retreat, allowed);
        Assert.Contains(BattleCommand.Attack, allowed);
        Assert.NotEmpty(session.Targets());
        Assert.All(session.Targets(), t => Assert.True(session.CanTarget(t)));
    }

    /// <summary>Щелчок по негодной цели (<c>0x63cce6</c>): воскрешение по уже воскрешённому — <c>X008TA0031</c>, двойник по стражу —
    /// <c>X008TA0032</c>, прочее — ничего.</summary>
    [SkippableFact]
    public void RefusalText_ReviveOnceAndGuardianCopy()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        while (!session.Finished && !session.WaitsPlayer) session.AiStep();
        Skip.If(session.Finished, "в образце нет хода стороны игрока");
        var me = session.Current!;
        var enemy = state.Units.First(u => u.Attacker != me.Attacker);
        // В образце своих может не быть: свой юнит — на свободной клетке (сторона цели — то, что проверяет движок).
        var other = state.Units.FirstOrDefault(u => !ReferenceEquals(u, me) && u.Attacker == me.Attacker);
        if (other is null)
        {
            other = new BattleUnit
            {
                Id = "ALLY", Type = me.Type, Attack = me.Attack, Attack2 = me.Attack2, Attacker = me.Attacker,
                Slot = Enumerable.Range(0, 6).First(s => !state.Units.Any(u => u.Attacker == me.Attacker && u.Slot == s)),
                HpMax = me.HpMax, Hp = me.HpMax,
            };
            state.Units.Add(other);
        }
        Assert.Null(session.RefusalText(other));

        // Движок признак «воскрешён» не читает: павший своей стороны, негодный для воскрешения, — сообщение; чужой павший — ничего
        // (0x63e370 = +0xfa1, 0x5e98ad).
        me.Attack = rules.Attacks.Values.First(a => a.Class == AttackClass.Revive);
        Assert.Null(session.RefusalText(other));
        other.Hp = 0;
        other.Revived = true;
        Assert.Equal(BattleSession.RevivedOnce, session.RefusalText(other));
        enemy.Hp = 0;
        enemy.Revived = true;
        Assert.Null(session.RefusalText(enemy));

        me.Attack = rules.Attacks.Values.First(a => a.Class == AttackClass.Doppelganger);
        other.Hp = other.HpMax;
        other.Type = rules.Units.Values.First(u => u.Category == UnitCategory.Guardian);
        Assert.Equal(BattleSession.NoGuardianCopy, session.RefusalText(other));
    }

    /// <summary>Бой двух людей «горячего стула»: автобой у каждой стороны свой (<c>data2+0x38</c>/<c>+0x39</c>) — включённый у защитника,
    /// он не ведёт нападающего: их юнитам команду ждёт экран (партия 28.09 <c>corpus/20260929-hotseat</c>).</summary>
    [SkippableFact]
    public void Auto_BothHuman_EachSideOwnFlag()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants) { BothHuman = true };
        session.SetAuto(attacker: false, on: true);

        int attackers = 0;
        for (int i = 0; i < 500 && !session.Finished; i++)
        {
            var unit = session.Current!;
            Assert.Equal(unit.Attacker, session.WaitsPlayer);   // защитники идут сами, нападающие ждут команду
            if (!session.WaitsPlayer)
            {
                session.AiStep();
                continue;
            }
            attackers++;
            session.Defend();
        }
        output.WriteLine($"ходов нападающих вручную {attackers}, автобой: нап={session.AutoAttackers} защ={session.AutoDefenders}");
        Assert.True(attackers > 0);
        Assert.False(session.Auto);
        session.Auto = true;
        Assert.True(session.AutoOf(true) && session.AutoOf(false));
    }

    /// <summary>Свой бой: боец (ранен, если <paramref name="woundedFighter"/>), лекарь (лечит любого своего) против одного врага с 10 ОЗ.</summary>
    private static (BattleState State, BattleUnit Fighter, BattleUnit Healer, BattleUnit Enemy) HealerBattle(bool woundedFighter)
    {
        BattleUnit Unit(string id, bool attacker, int slot, int hp, int hpMax, AttackType attack) => new()
        {
            Id = id, Attacker = attacker, Slot = slot, Hp = hp, HpMax = hpMax, Attack = attack,
            Type = new UnitType("g000uu0019", 1, hpMax, 0, attack.Id, "g000000000", false, true, 10, 100),
        };
        var strike = new AttackType("g000aa0019", 90, AttackClass.Damage, 100, 2, 100, 0, false, 1);
        var heal = new AttackType("g000aa0011", 10, AttackClass.Heal, 100, 2, 0, 20, false, 1);
        var weak = new AttackType("g000aa0020", 50, AttackClass.Damage, 100, 2, 1, 0, false, 1);
        var fighter = Unit("F", true, 0, woundedFighter ? 50 : 100, 100, strike);
        var healer = Unit("H", true, 1, 60, 60, heal);
        var enemy = Unit("E", false, 0, 10, 10, weak);
        return (new BattleState { Units = [fighter, healer, enemy], AttackerGroup = "A", DefenderGroup = "D" }, fighter, healer, enemy);
    }

    /// <summary>Замечание 18 прогона 11.10 (переход раунда 0x62689c, 0x626d49): боец добил последнего врага, у победителя есть раненый —
    /// лекарь-человек ходит ещё раз (новая очередь только из лекарей), целей — раненые свои, после лечения бой окончен.</summary>
    [SkippableFact]
    public void Healer_TurnAfterVictory_WhenWoundedAlly()
    {
        var rules = RulesLoader.Load(Rules);
        var (state, fighter, healer, enemy) = HealerBattle(woundedFighter: true);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        for (int guard = 0; guard < 100 && session.Winner is null; guard++)
        {
            if (!session.WaitsPlayer) session.AiStep();
            else if (ReferenceEquals(session.Current, fighter)) session.Attack(enemy);
            else session.Defend();
        }
        Assert.False(enemy.Alive);

        Assert.True(session.AfterBattle);
        Assert.False(session.Finished);
        Assert.Same(healer, session.Current);
        Assert.True(session.WaitsPlayer);
        Assert.Equal([fighter], session.Targets());
        // Только лечение (ответ пользователя 11.10): ни «Защищаться», ни «Пропустить», ни «Ждать», ни «Отступить», ни предметов.
        Assert.Equal([BattleCommand.Attack, BattleCommand.Auto, BattleCommand.Resolve], session.Allowed.Order());
        int hp = fighter.Hp;
        session.Attack(fighter);

        Assert.True(fighter.Hp > hp);
        Assert.True(session.Finished);
        Assert.False(session.AfterBattle);
    }

    /// <summary>«Ждать» в обычном раунде (<c>CBatAttackWait::onHit</c> <c>0x659e3e</c> → <c>0x6224dd</c>: первое свободное место
    /// <c>turnsOrder</c>): ждущий ходит ещё раз в конце того же раунда, после всех, кто стоял за ним (находка D2 11.10: сессия ходила по
    /// копии очереди, а модель дописывала ждущего в свою — ход терялся).</summary>
    [SkippableFact]
    public void Wait_UnitMovesAtEndOfSameRound()
    {
        var rules = RulesLoader.Load(Rules);
        var (state, _, _, enemy) = HealerBattle(woundedFighter: false);
        enemy.HpMax = enemy.Hp = 10_000;   // бой не кончается за раунд
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        while (!session.WaitsPlayer) session.AiStep();
        var waiter = session.Current!;
        int round = session.Round;
        Assert.Contains(BattleCommand.Wait, session.Allowed);

        Assert.True(session.Wait());
        var after = new List<BattleUnit>();
        while (session.Round == round && session.Current is { } next)
        {
            after.Add(next);
            if (ReferenceEquals(next, waiter)) Assert.DoesNotContain(BattleCommand.Wait, session.Allowed);   // ждать — раз за раунд
            if (session.WaitsPlayer) session.Defend();
            else session.AiStep();
        }

        output.WriteLine($"ждал {waiter.Id}, дальше в раунде {round}: {string.Join(",", after.Select(u => u.Id))}");
        Assert.Same(waiter, after[^1]);                                   // ход ждущего — последним в раунде
        Assert.Equal(1, after.Count(u => ReferenceEquals(u, waiter)));
    }

    /// <summary>Раненых у победителя нет — ходов лекарей нет, бой окончен сразу после удара (корпус: 3 таких боя без хода лекаря).</summary>
    [SkippableFact]
    public void Healer_NoTurnAfterVictory_WhenNobodyWounded()
    {
        var rules = RulesLoader.Load(Rules);
        var (state, fighter, _, enemy) = HealerBattle(woundedFighter: false);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants) { Auto = true };
        session.PlayOut(100);

        Assert.False(enemy.Alive);
        Assert.True(session.Finished);
        Assert.Equal(fighter.HpMax, fighter.Hp);
        Assert.DoesNotContain(session.Log, r => r.Unit == "H" && r.Target == "F");
    }

    /// <summary>Защита кончает ход и ставит признак защиты; следующий ходящий — другой юнит.</summary>
    [SkippableFact]
    public void Defend_EndsTurn()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        while (!session.Finished && !session.WaitsPlayer) session.AiStep();
        Skip.If(session.Finished, "в образце нет хода стороны игрока");

        var unit = session.Current!;
        session.Defend();

        Assert.True(unit.Defending);
        Assert.NotSame(unit, session.Current);
    }

    /// <summary>Цели боевого предмета (0x62425b): зелье лечения — живые раненые своей группы, воскрешения — павшие свои,
    /// сфера — как атака лидера этим классом; брошенное мимо цели не применяется; применённый предмет второй раз целей не даёт.</summary>
    [SkippableFact]
    public void ItemTargets_PotionsAndOrb()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var items = D2.Data.Play.GameBook.Load(Rules).Items.Values;
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        while (!session.Finished && !(session.WaitsPlayer && session.Round >= 1)) session.AiStep();
        Skip.If(session.Finished, "в образце нет хода игрока после первого раунда");
        var leader = session.Current!;
        leader.Type = leader.Type with { IsLeader = true };
        var heal = items.First(i => i.Category == D2.Rules.Magic.ItemCategory.PotionHeal);
        var revive = items.First(i => i.Category == D2.Rules.Magic.ItemCategory.PotionRevive);
        leader.BattleItems.Add(heal);
        leader.BattleItems.Add(revive);

        var own = state.Units.Where(u => u.Attacker == leader.Attacker && !u.Retreated && !u.Hidden).ToList();
        Assert.Equal(own.Where(u => u.Alive && u.Hp < u.HpMax), session.ItemTargets(0));
        Assert.Equal(own.Where(u => !u.Alive), session.ItemTargets(1));
        Assert.Equal(session.ItemTargets(0).Count + session.ItemTargets(1).Count > 0, session.Allowed.Contains(BattleCommand.UseItem));

        var enemy = state.Units.First(u => u.Attacker != leader.Attacker && u.OnField);
        Assert.False(session.UseItemOn(0, enemy));   // мимо цели — ничего
        Assert.Same(leader, session.Current);

        leader.BattleItems.Clear();
        var orb = items.First(i => i.Category == D2.Rules.Magic.ItemCategory.Orb && rules.Attacks.TryGetValue(i.AttackId, out var a)
                                   && a.Class != AttackClass.Summon);
        leader.BattleItems.Add(orb);
        var expected = BattleTargets.Possible(state, leader, rules.Attacks[orb.AttackId]);
        output.WriteLine($"лидер {leader.Id}: сфера {orb.Id} — целей {expected.Count}");
        Assert.Equal(expected, session.ItemTargets(0));
        leader.AppliedItems.Add(0);
        Assert.Empty(session.ItemTargets(0));
    }

    /// <summary>Предмет-призыв: цели — свободные позиции своей группы (EMPTYSELSMALL), призванный встаёт на выбранную.</summary>
    [SkippableFact]
    public void ItemSummon_IntoChosenSlot()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var items = D2.Data.Play.GameBook.Load(Rules).Items.Values;
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        while (!session.Finished && !(session.WaitsPlayer && session.Round >= 1)) session.AiStep();
        Skip.If(session.Finished, "в образце нет хода игрока после первого раунда");
        var leader = session.Current!;
        leader.Type = leader.Type with { IsLeader = true };
        var orb = items.First(i => rules.Attacks.TryGetValue(i.AttackId, out var a) && a.Class == AttackClass.Summon
                                   && rules.SummonCandidates(a.Id, smallSlot: true).Count == 1);
        leader.BattleItems.Add(orb);
        var slots = session.ItemSummonSlots(0);
        output.WriteLine($"сфера {orb.Id}: свободные позиции {string.Join(",", slots)}");
        Skip.If(slots.Count == 0, "группа полна");
        Assert.Empty(session.ItemTargets(0));
        Assert.Contains(BattleCommand.UseItem, session.Allowed);
        int chosen = slots[^1];
        int before = state.Units.Count;
        Assert.True(session.UseItemAt(0, chosen));
        var summoned = state.Units[before];
        Assert.Equal(chosen, summoned.Slot);
        Assert.Equal(leader.Attacker, summoned.Attacker);
        Assert.Null(state.SummonSlot);
    }

    /// <summary>Применение предмета ложится в <see cref="BattleSession.ItemUses"/> для показа (шаг 12, <c>0x6365f2</c>): Сфера грома — атака предмета
    /// (урон, «−N» на цели, а не строка класса носителя), юнит предмета <c>G000UU0008</c> (его <c>TUCH</c> — молния на цели); зелье лечения — атака
    /// своего юнита (<c>GItem.UNIT_ID</c>), «+N». Записей хода (<see cref="BattleSession.Log"/>) применение не даёт.</summary>
    [SkippableFact]
    public void ItemUse_RecordedForShow_OrbAndPotion()
    {
        Skip.IfNot(File.Exists(Sample), "нет образца боя в корпусе");
        var rules = RulesLoader.Load(Rules);
        var items = D2.Data.Play.GameBook.Load(Rules).Items;
        var state = Load(rules);
        var session = new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants);
        while (!session.Finished && !(session.WaitsPlayer && session.Round >= 1)) session.AiStep();
        Skip.If(session.Finished, "в образце нет хода игрока после первого раунда");
        var leader = session.Current!;
        leader.Type = leader.Type with { IsLeader = true };
        var orb = items.Values.First(i => i.Id.Equals("G000IG9027", StringComparison.OrdinalIgnoreCase));
        leader.BattleItems.Add(orb);
        var target = session.ItemTargets(0).First();
        int log = session.Log.Count, hp = target.Hp;
        Assert.True(session.UseItemOn(0, target));

        var use = Assert.Single(session.ItemUses);
        Assert.Equal(log, session.Log.Count);
        Assert.Equal((leader.Id, target.Id, hp, target.Hp), (use.Bearer, use.Target, use.TargetHpBefore, use.TargetHpAfter));
        Assert.Equal(AttackClass.Damage, use.Shown?.Class);
        Assert.Equal("G000UU0008", use.Item.UnitId, ignoreCase: true);
        Assert.Equal(hp - target.Hp, use.Record.Damage);
        var note = Assert.Single(use.Notes.After([use.Record], state, id => id == "X008TA0004" ? "%DAMAGE%" : id));
        Assert.Equal(use.Immune ? "X008TA0002" : $"-{hp - target.Hp}", note.Markup);

        var heal = items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.PotionHeal);
        var shown = BattleItemUse.ShownAttack(heal, rules);
        Assert.Equal(AttackClass.Heal, shown?.Class);
        // «Сила» зелья силы (20261016e-av), хотя атака его юнита G000UU0161 — «дар атаки».
        Assert.Equal(AttackClass.BoostDamage, BattleItemUse.ShownAttack(items["G000IG0003"], rules)?.Class);
        var boost = Assert.Single(StrikeNotes.Before(state, leader, BattleItemUse.ShownAttack(items["G000IG0003"], rules))
            .After([new TurnRecord(state.Round, leader.Id, leader.Id, false, 0, leader.Hp)], state, id => id));
        Assert.Equal("X008TA0003", boost.Markup);
    }
}
