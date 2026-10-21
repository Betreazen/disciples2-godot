using D2.Data.Corpus;
using D2.Rules.Battle;

/// <summary>С2: разбор журнала боя на окна ходов — записи (броски точности, лечение), действия без броска, предметы, решения
/// человека и ИИ (<see cref="Recorded"/>) — и мелкие чтения журнала (новые юниты, перестроенная очередь).</summary>
public partial class C2HarnessTests
{
    /// <summary>Позиция юнита, впервые появившегося в снимке после <paramref name="seq"/> (призванного): <c>flags &amp; 7</c>;
    /// −1 — не найден.</summary>
    private static int NewUnitSlot(IReadOnlyList<CorpusEvent> events, long seq, BattleState state)
    {
        var known = new HashSet<string>(state.Units.Select(u => u.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var e in events.Where(x => x.Seq < seq).SelectMany(UnitsOf)) known.Add(e.Id);
        foreach (var e in events.Where(x => x.Seq > seq))
            if (UnitsOf(e).FirstOrDefault(u => !known.Contains(u.Id)) is { } fresh) return (int)(fresh.Flags & 7);
        return -1;
    }

    /// <summary>Очередь раунда, построенная второй раз уже после чьего-то хода (строка round того же номера после удара или turn_after).
    /// Так бывает у двойника в начале боя: он ходит в предраунде (раунд 0) между двумя построениями очереди раунда 1 —
    /// <see cref="BattleSim.BeginPreRound"/> (<c>0x6290bc</c>, 18.10, реестр Б32); строгий режим это моделирует (20261016-port-synth
    /// [545297], b [522314], c [518729] — сходятся). Причина пропуска осталась на случай иной перестройки, если она встретится.</summary>
    private static string? MidRoundRebuild(D2.Data.Corpus.Battle battle)
    {
        long round = -1;
        bool acted = false;
        foreach (var e in battle.Events)
        {
            if (e is TurnEvent { After: true } || IsTurnEffect(e)) acted = true;
            if (e is not RoundEvent r) continue;
            if (r.Round == round && acted)
                return $"очередь раунда {r.Round} построена заново посреди раунда (seq {r.Seq}) — не предраунд двойника";
            (round, acted) = (r.Round, false);
        }
        return null;
    }

    /// <summary>Снимки юнитов события (для новых id боя — призванных).</summary>
    private static IEnumerable<UnitState> UnitsOf(CorpusEvent e) =>
        e switch { TurnEvent t => t.Units, RoundEvent r => r.Units, BattleStartEvent b => b.Units, _ => [] };

    /// <summary>Записанные действия и решения: окно от turn_before (или battle_units) до следующего turn_before/battle_units; в окне —
    /// miss/dmg/immune/attack_hit того же хода и HP из turn_after. Ввод человека (<see cref="HumanInput"/>) в окне или в его пустом
    /// вызове перед ним помечает решение как человеческое.
    /// <para>Окно с miss — удар: записи по каждому miss в порядке журнала; номер атаки — по правилу C1HarnessTests.AttackIndexOf
    /// (вторая атака — после всех целей первой, по живым попаданиям; вторая без броска точности miss не пишет); цель попадания —
    /// из dmg, промаха — из immune сразу после броска, попадания без урона — из attack_hit; HP цели первой атаки — до второй.</para>
    /// <para>Окно без miss: пустой вызов хода человека (только оценки и экран, turn_after next == сам), beforeBattleTurn перед
    /// battle_end, защита (бит 31 при battle_turn или не было до хода), ожидание (бит 4 flags), бегство по решению (бит 29 у
    /// ходящего), паралич и уходящий до хода, смерть от тика DoT, лечение (класс Heal: attack_hit с ростом HP, без него — рост HP
    /// между снимками, ранг 1, corpus/20260906-b), предмет (атака попадания — ATTACK_ID боевого предмета, или potion_use в бою),
    /// действие без броска (attack_hit первой атаки: цель, полученное здоровье второй атаки-лечения, позиция призыва), в журналах до
    /// attack_hit — то же по battle_turn и росту HP. Превращение без attack_hit и любое другое окно без miss — причина, бой в
    /// пропуск.</para></summary>
    private static RecordedTurns Recorded(D2.Data.Corpus.Battle battle, BattleState state)
    {
        var actions = new List<RecordedAction>();
        int defends = 0;
        // Сила DoT, наложенного ВТОРОЙ атакой раньше в этом бою: ключ «класс:цель» → QTY_DAM.
        // ponytail: DoT от первой атаки (яд без dmg — цели нет) не учитывается; понадобится — брать из attack_new.
        var dotQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var events = battle.Events;
        var anchors = Enumerable.Range(0, events.Count).Where(i => events[i] is TurnEvent { After: false } or BattleUnitsEvent).ToList();
        List<CorpusEvent> Window(int k) =>
            events.Skip(anchors[k] + 1).Take((k + 1 < anchors.Count ? anchors[k + 1] : events.Count) - anchors[k] - 1).ToList();
        string UnitOf(int k) => events[anchors[k]] is TurnEvent t ? t.Unit
            : Window(k).OfType<TurnEvent>().FirstOrDefault(t => t.After)?.Unit ?? ((BattleUnitsEvent)events[anchors[k]]).Order.FirstOrDefault()?.Id ?? "";
        UnitState? StateBefore(int k, string id)
        {
            for (int i = anchors[k]; i >= 0; i--)
            {
                var units = events[i] switch { TurnEvent t => t.Units, RoundEvent r => r.Units, BattleStartEvent b => b.Units, _ => null };
                if (units?.FirstOrDefault(u => Same(u.Id, id)) is { } found) return found;
            }
            return null;
        }

        // Тип цели на момент хода — по последнему battle_units: превращённый в бою (Ведьма, понижение уровня) уже не тот, что в начале,
        // а canPerform второй атаки смотрит текущую реализацию (DrainLevel 0x662608 — PREV_ID). 20261018b-port-synth [5062040] seq 5080741:
        // понижение уровня Гномом по Бесу (из Ветерана) — без броска, у Беса нет PREV_ID.
        UnitType? TypeBefore(int k, string id)
        {
            for (int i = anchors[k]; i >= 0; i--)
                if (events[i] is BattleUnitsEvent bu && bu.Units.FirstOrDefault(u => Same(u.Id, id))?.Type is { } t)
                    return state.Rules?.Units.GetValueOrDefault(t.ToLowerInvariant());
            return null;
        }
        bool CanPerformAsTyped(int k, BattleUnit self, AttackType attack, BattleUnit victim, UnitState st)
        {
            var saved = victim.Type;
            if (TypeBefore(k, victim.Id) is { } now) victim.Type = now;
            try { return SnapshotCanPerform.Of(self, attack, victim, st); }
            finally { victim.Type = saved; }
        }

        var turns = new List<JournalTurn>();
        bool carried = false;   // решение человека в пустом вызове его хода — переходит к следующему окну того же юнита
        for (int k = 0; k < anchors.Count; k++)
        {
            var window = Window(k);
            string unit = UnitOf(k);
            // Первое окно боя: щелчок человека стоит до battle_units (якоря) — между battle_start и ним.
            bool human = carried || HumanInput(window) || k == 0 && HumanInput(events.Take(anchors[0]));
            carried = false;
            var misses = window.OfType<MissEvent>().ToList();
            var after = window.OfType<TurnEvent>().FirstOrDefault(t => t.After);
            if (misses.Count == 0)
            {
                // Защита — только решение этого хода: бит 31, перенесённый в окно без battle_turn, защитой не считается.
                // Бита не было в снимке до хода — защита этого хода и без battle_turn: у первого хода боя (окно от
                // battle_units) движок battle_turn не пишет — 20260918-probe65 [9290325] seq 9290360 → 9290362.
                if (after is not null && Same(after.Unit, unit) && Defended(after)
                    && (window.Any(e => e is BattleTurnEvent) || StateBefore(k, unit)?.Defend == false))
                { defends++; turns.Add(new JournalTurn(unit, human, TurnChoiceKind.Defend, null)); continue; }
                // «Ждать» — бит 4 flags у ходящего в его turn_after, которого не было до хода (BattleSim.Wait).
                if (after is not null && Same(after.Unit, unit) && Waited(after) && (StateBefore(k, unit)?.Flags & 0x10) is 0)
                { turns.Add(new JournalTurn(unit, human, TurnChoiceKind.Wait, null)); continue; }
                // Пустой вызов хода человека: turn_before → (оценки ИИ-подсказки, броски и события UI) →
                // battle_units/battle_turn → turn_after next = сам → turn_before того же юнита; окна без удара идут до
                // второго turn_before. Пустой — без следствий хода: ни броска точности, ни урона, ни попадания, ни лечения,
                // ни предмета (attack_new, immune, armor, dmg_buffs, ai_strength, crt_rand и UI — только оценка и экран).
                bool idle = !window.Any(IsTurnEffect);
                // Бегство последним ходом раунда — не пустой вызов: turn_after этого хода движок пишет уже после нового раунда, с
                // next = сам (20261016e-av, карта 8 [15955320]: призванный S143UN0055 «Отступить» в раунде 9, seq 15968039 → 15968053).
                bool fled = after is not null && Same(after.Unit, unit)
                            && after.Units.FirstOrDefault(u => Same(u.Id, unit))?.Retreating == true && StateBefore(k, unit)?.Retreating == false;
                if (idle && !fled && (after is null || Same(after.Next, unit)) && k + 1 < anchors.Count && Same(UnitOf(k + 1), unit))
                { carried = human; continue; }
                if (window.Any(e => e is BattleEndEvent) && !window.Any(e => e is BattleTurnEvent or DamageEvent)) continue;
                var before = StateBefore(k, unit);
                var me = after?.Units.FirstOrDefault(u => Same(u.Id, unit));
                if (before?.Paralyzed == true || before?.Retreating == true) continue;
                if (before?.Hp > 0 && me?.Dead == true) continue;   // умер от тика DoT
                // Решение бежать: бит 29 у ходящего появился в его же ход без удара (BattleSim.Retreat; бит от Fear ставит враг).
                if (after is not null && Same(after.Unit, unit) && me?.Retreating == true && !window.Any(e => e is AttackHitEvent))
                { turns.Add(new JournalTurn(unit, human, TurnChoiceKind.Retreat, null)); continue; }
                // Окно от battle_units (второй вызов хода человека) — якорь сам и есть начало действия.
                var trigger = window.FirstOrDefault(e => e is BattleTurnEvent or BattleUnitsEvent)
                              ?? (events[anchors[k]] is BattleUnitsEvent ? events[anchors[k]] : null);
                if (trigger is not null && after is not null && state.Unit(unit)?.Attack.Class == AttackClass.Heal)
                {
                    // С attack_hit (схема 6.5+) — полученное здоровье по попаданиям и с номером атаки (воскрешение второй атакой
                    // Патриарха: 20261016-port-synth [523929] seq 527150, 0 → 50); без него — рост HP между снимками.
                    var healed = window.OfType<AttackHitEvent>().Any(h => Same(h.Attacker, unit)) ? Gains(window, unit)
                        : after.Units.Where(u => StateBefore(k, u.Id) is { } b && u.Hp > b.Hp)
                                     .OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
                                     .Select(u => new Expected(unit, u.Id, false, 0, (int)u.Hp)).ToList();
                    actions.Add(new RecordedAction(trigger.Seq, unit, ActionKind.Heal, healed, human));
                    turns.Add(new JournalTurn(unit, human, TurnChoiceKind.Attack, healed.Count == 1 ? healed[0].Target : null));
                    continue;
                }
                // Действие без броска точности (усиление, обереги, дар атаки, призыв, двойник): журнал называет цель событием
                // attack_hit первой атаки. Цель — юнит боя; у призыва — клетка (id позиции), её модель выбирает сама.
                var hit = window.OfType<AttackHitEvent>().FirstOrDefault(h => Same(h.Attacker, unit)
                    && window.OfType<AttackNewEvent>().FirstOrDefault(n => n.Seq == h.NewSeq)?.Number is null or 1);
                // Предмет из боевого слота: атака попадания — не своя, а ATTACK_ID предмета (у атаки предмета броска нет,
                // CBatAttackItem 0x6642ab); зелье — potion_use в бою (атаки у зелья нет вовсе).
                var bearer = state.Unit(unit);
                var potion = window.OfType<PotionUseEvent>().FirstOrDefault(x => x.InBattle && x.Ret);
                // Предмет с уроном (талисман, сфера): attack_hit пишется только у классов без урона — атака предмета видна по attack_new
                // (impl — сам предмет), цель — по dmg (20261017-port-synth, карта 10 [3116774]: S143UN000e, S143IM000c G000AA9127 по
                // S143UN0010, seq 3119897 → 3119901).
                var itemNew = hit is null && potion is null
                    ? window.OfType<AttackNewEvent>().FirstOrDefault(n => Same(n.Attacker, unit) && !Same(n.Impl, unit)) : null;
                var itemDmg = itemNew is null ? null : window.OfType<DamageEvent>().FirstOrDefault(d => Same(d.Attacker, unit) && d.Seq > itemNew.Seq);
                int item = bearer is null ? -1
                    : potion is not null ? bearer.BattleItems.FindIndex(i => Same(i.Id, potion.ItemType))
                    : itemDmg is not null ? bearer.BattleItems.FindIndex(i => Same(i.AttackId, itemNew!.Attack))
                    : hit is null || Same(hit.Attack, bearer.Attack.Id) || Same(hit.Attack, bearer.Attack2?.Id) ? -1
                    : bearer.BattleItems.FindIndex(i => Same(i.AttackId, hit.Attack));
                if (item >= 0)
                {
                    string? target = (potion?.Target ?? hit?.Target ?? itemDmg?.Target) is { } t && state.Unit(t) is not null ? t : null;
                    actions.Add(new RecordedAction(potion?.Seq ?? hit?.Seq ?? itemNew!.Seq, unit, ActionKind.Item, [], human, target, item));
                    turns.Add(new JournalTurn(unit, human, TurnChoiceKind.UseItem, target, item));
                    continue;
                }
                if (hit is not null)
                {
                    string? target = state.Unit(hit.Target) is not null ? hit.Target : null;
                    // Полученное здоровье лечения и воскрешения — запись хода, как у модели (вторая атака Лесной Девы, Патриарха;
                    // у двойника HP меняется превращением — это не запись):
                    // attack_hit с found и hp_after > hp_before, номер атаки — из её attack_new.
                    var gains = Gains(window, unit);
                    // Призыв: позиция — у нового юнита в следующем снимке (flags & 7).
                    int slot = hit.DispClass == (long)AttackClass.Summon ? NewUnitSlot(events, hit.Seq, state) : -1;
                    actions.Add(new RecordedAction(hit.Seq, unit, ActionKind.NoRoll, gains, human, target, Slot: slot));
                    turns.Add(new JournalTurn(unit, human, TurnChoiceKind.Attack, target, Slot: slot));
                    continue;
                }
                // Журналы до attack_hit (схемы 5–6.4): действие без броска видно только по battle_turn, призыву (новый юнит) и росту
                // HP от второй атаки-лечения (обереги Лесной Девы). Цель — вылеченный, если он один; иначе её выбирает модель.
                if (trigger is not null && after is not null && state.Unit(unit) is { } doer
                    && !Attacks.RollsAccuracy(doer.Attack.Class, itemAttack: false)
                    && doer.Attack.Class is not (AttackClass.Heal or AttackClass.Doppelganger or AttackClass.TransformSelf)
                    && !window.Any(e => e is DamageEvent or PotionUseEvent))
                {
                    var gains = doer.Attack2?.Class is AttackClass.Heal or AttackClass.Revive
                        ? after.Units.Where(u => StateBefore(k, u.Id) is { } b && u.Hp > b.Hp)
                                     .OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
                                     .Select(u => new Expected(unit, u.Id, false, 0, (int)u.Hp, 2)).ToList()
                        : [];
                    string? target = gains.Count == 1 ? gains[0].Target : null;
                    int slot = doer.Attack.Class == AttackClass.Summon ? NewUnitSlot(events, trigger.Seq, state) : -1;
                    actions.Add(new RecordedAction(trigger.Seq, unit, ActionKind.NoRoll, gains, human, target, Slot: slot));
                    turns.Add(new JournalTurn(unit, human, TurnChoiceKind.Attack, target, Slot: slot));
                    continue;
                }
                // Двойник и превращение себя без attack_hit: в кого превратился, видно лишь позже (battle_units), а дальше юнит бьёт
                // чужой атакой, которой разбор окон по исходному типу не знает (20260908-f6 [944403], [1281528]).
                if (state.Unit(unit)?.Attack.Class is AttackClass.Doppelganger or AttackClass.TransformSelf)
                    return new RecordedTurns(actions, $"превращение {unit} в журнале без attack_hit (схема до 6.5): в кого — не видно", defends, turns);
                var kinds = window.Where(e => e is not (TurnEvent or RoundEvent or BattleEndEvent or RngEvent))
                                  .Select(e => e.GetType().Name.Replace("Event", "")).Distinct().DefaultIfEmpty("пусто");
                return new RecordedTurns(actions, $"ход без miss ({unit}, {state.Unit(unit)?.Attack.Class}, seq {events[anchors[k]].Seq}): " +
                                                  string.Join(",", kinds), defends, turns);
            }

            // Удар: цель попадания — dmg между этим miss и следующим; вторая атака — последние N2 miss
            // окна, где N2 — попадания первой с живой целью в turn_after (правило C1 AttackIndexOf).
            var list = new List<Expected>();
            var targets = new List<string?>();
            foreach (var e in window)
            {
                if (e is MissEvent) { targets.Add(null); }
                else if (e is DamageEvent d && targets.Count > 0) targets[^1] ??= d.Target;
            }
            int HpAfter(string? id) => (int)(after?.Units.FirstOrDefault(u => Same(u.Id, id))?.Hp ?? -1);
            AttackHitEvent? SecondHit(string id) => id.Length == 0 ? null : window.OfType<AttackHitEvent>().FirstOrDefault(h =>
                h.Found && Same(h.Attacker, unit) && Same(h.Target, id) && window.OfType<AttackNewEvent>().FirstOrDefault(n => n.Seq == h.NewSeq)?.Number == 2);
            // Вторая атака идёт только по тем, на кого ложится её класс (Attacks.CanPerform, ранг 1) — её
            // miss в окне тогда нет: по отступавшему ещё ДО хода яд не кладётся (RejectsRetreating), и
            // слабый DoT не ложится на висящий сильный того же вида (Poison canPerform 0x65e1ef: jge 0x65e338).
            // corpus/20260906-e [106426] seq 106796: яд Паука (35, seq 106777) на S143UN0015, Master Thug
            // попадает — второго power/miss нет (яд 20 < 35); тик 88 → 53 = 35.
            var second = state.Unit(unit)?.Attack2;
            bool SecondRefused(string id) =>
                second is not null && (Attacks.RejectsRetreating(second.Class) && StateBefore(k, id)?.Retreating == true
                                       || StrongerDot(second, id)
                                       // Fear canPerform 0x65c5ed: и не по парализованному/окаменевшему (биты 2/3).
                                       || second.Class == AttackClass.Fear && StateBefore(k, id)?.Paralyzed == true
                                       // Shatter 0x65d741, DrainLevel 0x662608, LowerInitiative 0x65de8f — по снимку цели.
                                       || SnapshotCanPerform.StateClasses.Contains(second.Class)
                                          && state.Unit(unit) is { } self && state.Unit(id) is { } victim
                                          && StateBefore(k, id) is { } st && !CanPerformAsTyped(k, self, second, victim, st));
            bool StrongerDot(AttackType a, string id) =>
                DotBit(a.Class) is int bit && ((StateBefore(k, id)?.St ?? 0) >> bit & 1) != 0
                && dotQty.TryGetValue($"{a.Class}:{id}", out int qty) && qty > a.QtyDamage;
            var aliveHits = targets.Where(t => t is not null && HpAfter(t) > 0 && !SecondRefused(t)).ToList();
            // Вторая атака без броска точности (разбить броню, понизить уровень у части юнитов — Formulas.ClassHasPower) miss в
            // окне не пишет: все miss окна — первой атаки (20261016c-port-synth [526879] seq 529951: Shatter по трём целям — три
            // attack_hit без power и miss).
            bool hasSecond = second is not null && Attacks.RollsAccuracy(second.Class, itemAttack: false);
            int n1 = hasSecond ? misses.Count - aliveHits.Count : misses.Count;
            if (n1 < 0) return new RecordedTurns(actions, $"в окне хода {unit} бросков {misses.Count} меньше живых попаданий {aliveHits.Count}", defends, turns);
            for (int i = 0; i < misses.Count; i++)
            {
                bool Between(CorpusEvent x) => x.Seq > misses[i].Seq && (i + 1 >= misses.Count || x.Seq < misses[i + 1].Seq);
                var dmg = i < n1 ? window.OfType<DamageEvent>().FirstOrDefault(Between) : null;
                // Цель промаха: dmg нет, но движок спрашивает иммунитет цели сразу после броска (immune с always_only = false).
                // По корпусу у попаданий такой immune и dmg называют одну цель 3025 раз из 3025 (иначе — 0); в журналах
                // без хука immune цель промаха остаётся пустой — тогда её выбирает модель.
                var immune = dmg is null && i < n1 ? window.OfType<ImmuneEvent>().FirstOrDefault(x => !x.AlwaysOnly && Between(x)) : null;
                // Попадание без урона (паралич, страх, ослабление): цель — attack_hit первой атаки после броска
                // (20260929-hotseat [3645542] seq 3650225: паралич человека по S073UN014a, immune после броска нет).
                var landed = dmg is null && immune is null && i < n1
                    ? window.OfType<AttackHitEvent>().FirstOrDefault(h => Between(h) && Same(h.Attacker, unit) && h.Found) : null;
                string target = i < n1 ? dmg?.Target ?? immune?.Unit ?? landed?.Target ?? "" : aliveHits[i - n1]!;
                // HP цели после первой атаки — до второй: вторая атака сама меняет HP (понижение уровня — пропорционально,
                // 20261016c-port-synth [518729] seq 521345: 169 → 135), и тогда turn_after показывает HP уже после неё.
                int hp = i < n1 && SecondHit(target) is { } second2 ? (int)second2.HpBefore : HpAfter(target);
                list.Add(new Expected(unit, target, misses[i].Miss, (int)(dmg?.Damage ?? 0), hp, i < n1 ? 1 : 2));
            }
            // Дошедший до onHit DoT перезаписывает прежний (0x65e39d) — теперь на цели его сила.
            if (second is not null && DotBit(second.Class) is not null)
                foreach (var h in list.Where(x => x.Attack == 2 && !x.Miss))
                    dotQty[$"{second.Class}:{h.Target}"] = second.QtyDamage;
            actions.Add(new RecordedAction(misses[0].Seq, unit, ActionKind.Attack, list, human));
            turns.Add(new JournalTurn(unit, human, TurnChoiceKind.Attack, list.FirstOrDefault(h => h.Attack == 1 && h.Target.Length > 0)?.Target));
        }
        return new RecordedTurns(actions, null, defends, turns);
    }

    /// <summary>Полученное здоровье лечения и воскрешения по попаданиям окна (attack_hit с found и hp_after &gt; hp_before), номер атаки —
    /// из её attack_new. Порядок — по атаке и id, как у модели (<c>BattleSim.ActOnAllies</c>): площадное лечение движок обходит в
    /// порядке, которого боевой поток не определяет (20260918-probe65 [6018044]: 01a1, 01a0, 01a2, затем 01a0, 01a1, 01a2).</summary>
    private static List<Expected> Gains(List<CorpusEvent> window, string unit) =>
        window.OfType<AttackHitEvent>()
              .Where(h => Same(h.Attacker, unit) && h.Found && h.HpAfter > h.HpBefore
                          && h.DispClass is (long)AttackClass.Heal or (long)AttackClass.Revive)
              .Select(h => new Expected(unit, h.Target, false, 0, (int)h.HpAfter,
                                        (int)(window.OfType<AttackNewEvent>().FirstOrDefault(n => n.Seq == h.NewSeq)?.Number ?? 1)))
              .OrderBy(x => x.Attack).ThenBy(x => x.Target, StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>Решение принял человек: нажата кнопка хода (защита, ожидание, бегство) или щелчок левой кнопкой (выбор цели,
    /// <c>WM_LBUTTONDOWN</c> 513), и автобой в этом окне не включался (<c>TOG_AUTOBATTLE</c> с галкой — дальше решает ИИ).</summary>
    private static bool HumanInput(IEnumerable<CorpusEvent> window)
    {
        bool input = false;
        foreach (var e in window)
        {
            if (e is UiClickEvent { Element: "TOG_AUTOBATTLE", Checked: true }) return false;
            if (e is UiClickEvent { Element: "BTN_DEFEND" or "BTN_WAIT" or "BTN_RETREAT" } or InputEvent { Msg: 513 }) input = true;
        }
        return input;
    }

    /// <summary>Событие, которое есть следствие хода, а не оценка или экран: бросок точности, урон, попадание, лечение,
    /// воскрешение, зелье или предмет, смена HP, конец боя.</summary>
    private static bool IsTurnEffect(CorpusEvent e) =>
        e is MissEvent or DamageEvent or AttackHitEvent or HealEvent or ReviveEvent or PotionUseEvent or ItemEquipEvent
            or HpChangeEvent or BattleEndEvent;

    /// <summary>Бит статуса DoT в unitStatuses (BattleStatus: Poison 15, Frostbite 17, Blister 19).</summary>
    private static int? DotBit(AttackClass cls) =>
        cls switch { AttackClass.Poison => 15, AttackClass.Frostbite => 17, AttackClass.Blister => 19, _ => null };
}
