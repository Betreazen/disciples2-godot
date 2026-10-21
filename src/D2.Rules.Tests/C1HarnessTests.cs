using D2.Data;
using D2.Data.Battle;
using D2.Data.Magic;
using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Rng;
using D2.Rules.Magic;
using D2.Rules.Units;
using Xunit.Abstractions;

/// <summary>С1 (ТЗ §7): каждое событие формулы из корпуса пересчитывается нашей функцией и
/// сравнивается с записанным результатом; порог 100 %. Считается только то, что действительно
/// сверено: файлы-дубликаты (одинаковый sha256) не учитываются дважды, а всё, что сверить нельзя,
/// уходит в пропуски С ПРИЧИНОЙ — молчаливого «сошлось» тут быть не должно.
///
/// Привязка вложенных вызовов: armor/dmg_max/dmg_buffs имеют seq больше внешнего dmg и стоят
/// следом. Окно поиска пары — от dmg до ближайшего turn_*/round/battle_turn/battle_end ИЛИ до
/// следующего dmg, что раньше; в окне обязан быть ровно один кандидат, иначе пропуск. Без границы
/// по следующему dmg первый удар двойной атаки (ATCK_TWICE) или площадной атаки утащил бы пару
/// второго и дал ложное «сошлось».
///
/// Номер атаки: события power/dmg_buffs его не несут, он выводится из порядка событий внутри хода
/// (окно от turn_before/battle_units до следующего) — см. <see cref="AttackIndexOf"/>. Сентинел -1
/// (probehooks.cpp:56, notComputed) означает, что движок значения не вернул — такое событие
/// пропускается, а не сравнивается с -1.</summary>
[Trait("Category", "Corpus")]
public class C1HarnessTests(ITestOutputHelper output)
{
    /// <summary>probehooks.cpp:56 — движок не вернул значение (нулевой указатель).</summary>
    private const int NotComputed = -1;

    /// <summary>Сверки формул — то, что и есть С1; из них складывается «независимых сверок формул».
    /// dmg_buffs и dmg_buffs_random — две ветки одной формулы, потому семь формул на восемь ключей.
    /// immune добавлен задачей 14 Ф6 (событие корпуса v4, <c>isUnitImmuneToAttack</c>).</summary>
    private static readonly string[] Required =
        ["power", "dmg_max", "armor", "dmg_buffs", "dmg_buffs_random", "miss", "dmg", "immune"];

    /// <summary>Те же формулы power/dmg_buffs, но по второй атаке юнита (ATTACK2_ID): считаются в
    /// сумму сверок, отдельные ключи — чтобы было видно, что вторая атака вообще измерена.</summary>
    private static readonly string[] SecondAttack = ["power_attack2", "dmg_buffs_attack2"];

    /// <summary>Наблюдения над журналом, а не проверки наших формул: инвариант ret = dmg + crit_dmg,
    /// «крита не было» и диапазон найденного броска. Считаются отдельной суммой, чтобы не раздувать
    /// заслугу С1. attacks_per_turn — Attacks.AttacksPerTurn против order[].attacks из battle_units; это ОСТАТОК ударов хода: у
    /// юнита с двойным ударом (ATCK_TWICE) после первого удара (turn_after с next = он же) движок пишет 1 — первые такие строки в
    /// корпусе 20261016e-av, карта 8 (двойной удар человека, [15066670]: S143UN0009 seq 15070664).
    /// miss_boundary и dmg_round_alt не входят ни в одну сумму — это пересчёт уже учтённых miss/dmg:
    /// dmg_round_alt — сходится ли при броне &gt; 0 альтернатива round-half-up
    /// (round(dwb·(100−armor)/100)) с журналом; наша формула dwb − dwb·armor/100 (ранг 1 по
    /// дизассемблеру) от неё отличается на 27/40 (17 против 16), корпус пока видел только 27/50.</summary>
    private static readonly string[] Observations =
        ["dmg_ret", "crit_dmg_zero", "rng_max", "attacks_per_turn", "immune_one_answer", "immune_once_spent", "immune_stub_class"];

    [SkippableFact] public void EveryRecordedFormulaCall_MatchesOurs()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        Skip.IfNot(CorpusFiles.Available, "нет корпуса");
        var rules = RulesLoader.Load(GamePaths.Globals);
        var stat = new Stats();
        stat.Ensure("miss_boundary");   // ноль пограничных промахов — тоже результат, он обязан быть напечатан
        stat.Ensure("dmg_round_alt");
        stat.Ensure("retype");          // ноль превращений в бою — тоже результат, он обязан быть напечатан
        stat.Ensure("immune_once_spent");
        // Задачи 15-16 Ф7: крит и «человек на лёгкой» — ноль тоже результат, он обязан быть напечатан.
        foreach (var key in new[] { "crit_dmg", "dmg_buffs_easy", "dmg_buffs_random_easy", "rng_max_easy", "power_ai_difficulty" })
            stat.Ensure(key);
        // 16.10.2026: редкие классы и canPerform второй атаки (третья партия оригинала на port-synth-1).
        foreach (var key in RareKeys) stat.Ensure(key);
        var (files, duplicates) = JournalSet.Distinct(CorpusFiles.Journals());
        var spells = SpellLoader.Load(GamePaths.Globals);
        var mapEffects = new MapEffectTables(spells, D2.Data.Magic.ItemLoader.Load(GamePaths.Globals));   // заклинания и зелья карты
        var modifiers = ModifierLoader.Load(GamePaths.Globals).Modifiers;
        var economy = D2.Data.Map.EconomyLoader.Load(GamePaths.Globals);   // защита города, задача 5 Ф7
        var items = D2.Data.Magic.ItemLoader.Load(GamePaths.Globals);        // зелья, выпитые в бою (ai-q33)
        int battlesTotal = 0, battlesUsed = 0, battlesBuffed = 0, battlesChained = 0, snapshots = 0;

        foreach (var file in files)
        {
            var casts = RouteCasts.Of(file);
            var saves = SaveSnapshots.Of(file);
            snapshots += saves.Count;
            var marks = casts.Count == 0 && saves.Count == 0 ? [] : RouteCasts.TurnMarks(file);
            foreach (var battle in JournalCache.Battles(file))
            {
                battlesTotal++;
                stat.Where = $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)} [{battle.Start.Seq}] ";
                var (onAttackers, onDefenders) =
                    PreBattleModifiers(casts, marks, battle.Start.Seq, spells, modifiers, stat);
                if (onAttackers.Count + onDefenders.Count > 0) battlesBuffed++;
                int turn = saves.Count == 0 ? 0 : RouteCasts.TurnAt(marks, battle.Start.Seq);
                if (!CorpusBattleLoader.TryLoad(battle, rules, out var state, out var reason,
                                                onAttackers, onDefenders, saves, turn, spells: mapEffects))
                { stat.Skip("бой", reason); continue; }
                var here = saves.At(battle.Start.Seq);          // снимки своей загрузки карты
                if (here.Count > 0 && CheckChain(battle, state, here, turn, stat)) battlesChained++;
                if (Measure(battle, state, rules.Constants, stat, FortArmorOf(here, turn, economy, modifiers), items))
                    battlesUsed++;
            }
        }

        output.WriteLine($"файлов {files.Count}, дублей по sha256 {duplicates}; боёв {battlesTotal}, пригодных {battlesUsed}, " +
                         $"с наложенным до боя заклинанием {battlesBuffed}; " +
                         $"сохранений рядом с журналами {snapshots}, боёв с непустой цепочкой из них {battlesChained}");
        if (stat.Sum(["ward_hit"]) == 0) stat.Skip("ward_hit", "нет журналов 6.1 (attack_hit класса 24)");
        foreach (var line in stat.Lines()) output.WriteLine(line);
        output.WriteLine($"независимых сверок формул {stat.Sum(Required) + stat.Sum(SecondAttack)} (семь формул, из них по второй атаке {stat.Sum(SecondAttack)}), " +
                         $"наблюдений {stat.Sum(Observations)}, расхождений {stat.Mismatches.Count}");
        foreach (var m in stat.Mismatches.Take(100)) output.WriteLine("  " + m);

        Assert.True(battlesUsed > 0, "ни один бой корпуса не загрузился — измерять нечего");
        foreach (var key in Required)
            Assert.True(stat.Ok(key) > 0, $"нет ни одной проверки {key}");
        // Журнал, где эти классы сработали у движка (port-synth-1, A3–A5), — закреплены сверками, а не молчанием.
        if (files.Any(f => f.Contains(RareClassesJournal, StringComparison.OrdinalIgnoreCase)))
            foreach (var key in RareKeys)
                Assert.True(stat.Ok(key) > 0, $"в корпусе {RareClassesJournal} нет ни одной проверки {key}");
        Assert.Empty(stat.Mismatches);
    }

    /// <summary>Обереги, наложенные в бою (<c>attack_hit</c> 6.1), на синтетическом бое из живых
    /// справочников: S2 накладывает <c>g000aa8133</c> (все четыре оберега) на оруженосца S1, у
    /// которого своих иммунитетов нет. Огненный удар журнал отбивает (<c>ret = true</c>); боя без
    /// единой строки <c>attack_hit</c> (зонд до 6.1) харнесс не сверяет — такие вопросы уходят в пропуск. После хода S2
    /// журнал спрашивает про Air (источник 7): зарегистрированный оберег снят — «нет»; оберег Air,
    /// не попавший в <c>mods_after</c> (пары кончились, <c>0x62184a</c>), стоит — «да».</summary>
    [SkippableTheory]
    [InlineData(true, true, 0)]     // все обереги зарегистрированы — Air снят ходом S2
    [InlineData(false, true, 0)]    // attack_hit нет вовсе — зонд < 6.1, вопросы не Always в пропуск
    [InlineData(true, false, 0)]    // Air не в mods_after — стоит и после хода S2
    public void WardsBestowedInBattle_FollowAttackHit(bool withHit, bool airRegistered, int mismatches)
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        const string wardsAttack = "g000aa8133", airWard = "g000um0001";
        var rules = RulesLoader.Load(GamePaths.Globals);
        BattleUnit Unit(string id, string type) => new()
        {
            Id = id, Type = rules.Units[type], Attack = rules.AttackOf(rules.Units[type])!, Attacker = false, Slot = 0, HpMax = 100, Hp = 100,
        };
        var state = new BattleState
        {
            Units = [Unit("S1", "g000uu0001"), Unit("S2", "g000uu0001")],
            AttackerGroup = "A", DefenderGroup = "D", Round = 1, Rules = rules,
        };
        var after = rules.Attacks[wardsAttack].Wards!
            .Where(w => airRegistered || !w.Equals(airWard, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(airRegistered ? 4 : 3, after.Count);
        var hit = new AttackHitEvent(3, 1, 0, 24, 2, "S2", wardsAttack, "S1", true, 100, 100, 0, 0,
                                     ["G000UM0099"], ["G000UM0099", .. after], 3);
        var start = new BattleStartEvent(1, 1, "A", "D", "p1", "p2", 1, []);
        List<CorpusEvent> events =
        [
            start,
            new AttackNewEvent(2, 1, "S2", "S2", 1, wardsAttack, 24, 2, 24, "D"),
            .. withHit ? new CorpusEvent[] { hit } : [],
            new ImmuneEvent(4, 1, "S1", "g000aa0028", 1, 4, false, true),                        // Fire
            new TurnEvent(5, 1, false, "S2", null, 1, [], []),
            new ImmuneEvent(6, 1, "S1", "g000aa0028", 1, 7, false, withHit && !airRegistered),   // Air
        ];
        var stat = new Stats();

        Measure(new D2.Data.Corpus.Battle(start, null, events), state, rules.Constants, stat, _ => null,
                new Dictionary<string, Item>());

        foreach (var line in stat.Lines().Concat(stat.Mismatches)) output.WriteLine(line);
        Assert.Equal(mismatches, stat.Mismatches.Count);
        Assert.Equal(withHit ? 1 : 0, stat.Ok("ward_hit"));
        Assert.Equal(withHit && !airRegistered ? 1 : 0, stat.Ok("ward_unregistered"));
    }

    /// <summary>Модификаторы, наложенные заклинанием с карты на тот же ход, что и бой: слева — на
    /// атакующую группу (это всегда наш отряд, бой начинаем мы), справа — на защищающуюся.
    ///
    /// <para><b>Ранг 1 — цепочка доезжает до боевых формул, и это измерено, а не заявлено.</b>
    /// В прогоне Ф6 перед боем хода 53 брошены «Призыв к оружию» (<c>g000ss0017</c> →
    /// <c>g000um0017</c>, <c>TYPE 4 QtyDamage +33 %</c>) на свой отряд и «Слабость»
    /// (<c>g000ss0064</c> → <c>g000um0064</c>, <c>TYPE 3 Power −10 %</c>) на чужой. Журнал того боя
    /// показывает у врагов точность <b>72</b> при базовых 80 и у своих урон <b>39</b> при 30 и
    /// <b>99</b> при 75 — ровно то, что даёт <c>EffectiveStats.OfAttack</c> с этими двумя
    /// модификаторами. Без этой подстановки те же события выглядели бы расхождением формулы.</para>
    ///
    /// <para>Заклинание без модификатора (урон, лечение, смена террейна) сюда не попадает —
    /// <c>SpellCast.ModifierOf</c> отдаёт null. Незнакомый id заклинания — пропуск с причиной, а не
    /// молчание: маршрут и таблицы обязаны сходиться.</para></summary>
    private static (IReadOnlyList<Modifier> OnAttackers, IReadOnlyList<Modifier> OnDefenders) PreBattleModifiers(
        IReadOnlyList<RouteCast> casts, IReadOnlyList<(long Seq, int Turn)> marks, long battleSeq,
        IReadOnlyDictionary<string, Spell> spells, IReadOnlyDictionary<string, Modifier> modifiers, Stats s)
    {
        if (casts.Count == 0) return ([], []);
        int turn = RouteCasts.TurnAt(marks, battleSeq);
        List<Modifier> onAttackers = [], onDefenders = [];
        foreach (var cast in casts.Where(c => c.Turn == turn))
        {
            if (cast.On is not (RouteCast.OnSelf or RouteCast.OnEnemy)) continue;   // каст в клетку, не в отряд
            if (!spells.TryGetValue(cast.SpellId, out var spell))
            { s.Skip("каст", $"заклинание {cast.SpellId} из маршрута не найдено в Gspells"); continue; }
            if (SpellCast.ModifierOf(spell, modifiers) is not { } modifier) continue;
            (cast.On == RouteCast.OnSelf ? onAttackers : onDefenders).Add(modifier);
        }
        return (onAttackers, onDefenders);
    }

    /// <summary>Проверка самого правила сопоставления боя со снимком <c>.sg</c> — на величинах,
    /// которые журнал пишет САМ и которые из снимка не берутся.
    ///
    /// <para><c>chain_level</c>: уровень юнита в снимке против уровня в <c>battle_units</c>.
    /// «Разошлось» здесь — не ошибка: цепочка принимается и тогда, когда она одна на всё окно
    /// <c>T−1…T+1</c>, а уровень внутри окна успел смениться (прокачка без выбора способности).
    /// Счётчик стоит, чтобы было видно, как часто снимок описывает ровно то же состояние, что и
    /// бой, а не только совместимое с ним.</para>
    ///
    /// <para><c>chain_hp</c>: <c>hp_max</c> журнала против того, что даёт цепочка поверх
    /// выращенного типа. Это уже настоящая сверка: если цепочка взята не та, а в ней есть элемент
    /// <c>Hp</c>, числа разойдутся. Расхождение идёт в общий список — молчаливого «сошлось» тут
    /// быть не должно.</para>
    ///
    /// <para>Возвращает true, если хоть у одного юнита боя цепочка непуста, — так считается
    /// «боёв с непустой цепочкой» в шапке.</para></summary>
    private static bool CheckChain(D2.Data.Corpus.Battle battle, BattleState state,
                                   SaveSnapshots saves, int turn, Stats s)
    {
        if (battle.Units is not { } identities) return false;
        var byId = identities.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        bool any = false;
        foreach (var u in state.Units)
        {
            if (u.Modifiers.Count > 0) any = true;
            if (!byId.TryGetValue(u.Id, out var identity)) continue;
            if (saves.LevelIn(turn, u.Id) is { } level)
            {
                if (identity.Level is { } journalLevel) s.Count("chain_level", level == journalLevel);
            }
            else s.Skip("chain", $"юнита нет ни в одном сохранении окна — цепочка принята пустой, " +
                                 "как у журналов без сохранений");
            if (identity.HpMax is not { } hpMax) continue;
            int ours = EffectiveStats.Of(u.Type, u.Modifiers).HitPoints;
            s.Count("chain_hp", ours == hpMax);
            if (ours != hpMax) s.Mismatch($"chain_hp {u.Id}: журнал {hpMax}, наше {ours}");
        }
        return any;
    }

    /// <summary>
    /// Наша защита укрепления по уровню деревни и <c>PROTECT_B</c> (или <c>Grace.PROTECTION</c>
    /// столицы) — задача 5 Ф7: в броню подставляется ПРАВИЛО, а не журнальное поле <c>fort</c>.
    /// <para>Укрепление юнита берётся из сохранений рядом с журналом
    /// (<see cref="SaveSnapshots.FortOf"/>); журналов без сохранений это не касается — там ответ
    /// <c>null</c>, и броня считается как раньше. Незнакомая раса или отсутствующая строка
    /// <c>GcityInf</c> — тоже <c>null</c>: С1 меряет формулы, и падать на справочнике он не должен
    /// (сам факт сверяет <c>fort_armor_source</c> в <see cref="FortHarnessTests"/>).</para>
    /// </summary>
    private static Func<string, int?> FortArmorOf(SaveSnapshots saves, int turn,
                                                  D2.Rules.Map.EconomyRules economy,
                                                  IReadOnlyDictionary<string, Modifier> modifiers)
    {
        if (saves.Count == 0) return _ => null;
        return id =>
        {
            if (saves.FortOf(turn, id) is not { } f || f.RaceId.Length == 0) return null;
            try
            {
                return D2.Rules.Map.Fortifications.Protection(economy, modifiers, f.Capital, f.Size,
                                                              f.ProtectBonus, f.RaceId);
            }
            catch (ArgumentException) { return null; }
        };
    }

    /// <summary>false — бой измерить не удалось, причина уже записана в пропуски.</summary>
    private static bool Measure(D2.Data.Corpus.Battle battle, BattleState state, RulesConstants c, Stats s,
                                Func<string, int?> fortArmor, IReadOnlyDictionary<string, Item> items)
    {
        var events = battle.Events;   // отсортированы по seq
        var rolls = new Dictionary<(long tid, long n), RngEvent>();
        foreach (var e in events.OfType<RngEvent>().Where(e => !e.Std))
            if (!rolls.TryAdd((e.Tid, e.N), e)) { s.Skip("бой", $"повтор ключа броска (tid {e.Tid}, n {e.N})"); return false; }
        var snapshots = events.Where(e => e is TurnEvent or RoundEvent or BattleStartEvent).ToList();
        var attackIndex = AttackIndexOf(events, state, s);
        CountAmbiguousImmune(events, state, s);
        // Потраченные за бой одноразовые варды (ImmunityLevel.Once) — см. CheckImmune.
        var spentWards = new HashSet<(string, long, long)>();
        // Зонд до 6.1 не пишет attack_hit: обереги, наложенные в бою (класс 24), не видны (20260915-passive
        // S143UN008a — G000AA8133 на группу перед вопросами seq 7798789, 8264982). Такие вопросы — в пропуск.
        bool hitsLogged = events.OfType<AttackHitEvent>().Any();
        var unseenWards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Ударов, уже сделанных юнитом в текущем ходу (второй удар — тот же юнит снова ходит: turn_after с next = он же).
        var struck = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool acted = false;

        foreach (var e in events)
        {
            FollowStrikes(e, struck, ref acted);
            switch (e)
            {
                case PowerEvent p: CheckPower(p, state, attackIndex, s); break;
                case DamageMaxEvent dm: CheckDamageMax(dm, state, c, s); break;
                case ArmorEvent a: CheckArmor(a, state, snapshots, c, s, fortArmor); break;
                case DamageBuffsEvent db when !db.Random: CheckDamageBuffs(db, state, snapshots, attackIndex, c, s); break;
                case MissEvent m: CheckMiss(m, rolls, s); break;
                case DamageEvent d: CheckDamage(d, state, events, snapshots, rolls, c, s); break;
                case BattleUnitsEvent bu: FollowRetype(bu, state, s); CheckAttacksPerTurn(bu, state, struck, s); break;
                case AttackNewEvent { DispClass: 24 } an when !hitsLogged: unseenWards.Add(an.TargetGroup); break;
                case ImmuneEvent im when !im.AlwaysOnly && state.Unit(im.Unit) is { } iu
                                         && unseenWards.Contains(iu.Attacker ? battle.Start.AttackerGroup : battle.Start.DefenderGroup):
                    s.Skip("immune", "зонд < 6.1: в бою наложены обереги (класс 24), а attack_hit не пишется"); break;
                case ImmuneEvent im: CheckImmune(im, state, spentWards, s); break;
                case AttackNewEvent an: CheckSecondCanPerform(an, events, state, snapshots, s); break;
                case AttackHitEvent h:
                    FollowWards(h, state, spentWards, s); FollowTransform(h, state, s);
                    FollowRareHit(h, state, snapshots, c, s); break;
                case RoundEvent r: state.Round = (int)r.Round; break;
                case PotionUseEvent { InBattle: true, Ret: true } pu: FollowPotion(pu, state, items, s); break;
                case TurnEvent { After: false } t when state.Unit(t.Unit) is { } mover:
                    Immunity.ClearWards(mover); FollowTransformBreak(t, mover, events, state, c, s); break;
                case UnknownEvent u: s.Skip("ev_unknown", u.Ev); break;
            }
        }
        return true;
    }

    /// <summary>Сделанные в ходу удары: удар — бросок точности, урон или <c>attack_hit</c> между <c>turn_before</c> и <c>turn_after</c>;
    /// <c>turn_after</c> с <c>next</c> = тот же юнит после удара — первый удар двойного, иначе ход кончен. Новый раунд обнуляет всё
    /// (у <c>turn_after</c> в начале раунда <c>next</c> = он же, но удара перед ним нет).</summary>
    private static void FollowStrikes(CorpusEvent e, Dictionary<string, int> struck, ref bool acted)
    {
        switch (e)
        {
            case RoundEvent: struck.Clear(); acted = false; break;
            case TurnEvent { After: false }: acted = false; break;
            case MissEvent or DamageEvent or AttackHitEvent: acted = true; break;
            case TurnEvent { After: true } t when string.Equals(t.Next, t.Unit, StringComparison.OrdinalIgnoreCase) && acted:
                struck[t.Unit] = struck.GetValueOrDefault(t.Unit) + 1;
                acted = false;
                break;
            case TurnEvent { After: true } t when !string.Equals(t.Next, t.Unit, StringComparison.OrdinalIgnoreCase):
                struck.Remove(t.Unit);
                break;
        }
    }

    /// <summary>Юнита превратили прямо в бою — идти за журналом, а не за загруженным составом.
    ///
    /// <para><b>Ранг 1 — измерено, а не выведено.</b> Событие <c>battle_units</c> пишется заново на
    /// каждый ход и несёт <c>type</c>/<c>hp_max</c> КАЖДОГО юнита, то есть журнал сам называет новую
    /// реализацию. Арена <c>corpus/20260909-arena2</c>: ведьма <c>S143UN000e</c> бьёт
    /// <c>g000aa0067</c> (<c>CLASS 22 TransformOther</c>) на seq 120016, и со следующего снимка
    /// (seq 120046) наш <c>S143UN0009</c> перестаёт быть <c>G000UU0044</c> (King's Guard, урон 60,
    /// потолок 400, HP 225) и становится <c>G000UU5034</c> (Thug, урон 20, потолок 300, HP 50).
    /// Без этой подстановки харнесс давал ровно три расхождения — <c>dmg_buffs</c> 20 против 60,
    /// <c>dmg_buffs(random)</c> 25 против 65 и <c>dmg_max</c> 300 против 400, — и все три были не
    /// ошибкой формул, а устаревшим составом.</para>
    ///
    /// <para><b>Почему это не подгонка.</b> Тип берётся из журнала, а не угадывается: цель
    /// <c>TransformOther</c> в событии удара не названа вовсе (та же слепота, что у
    /// <c>IsBlindAttack</c> в С2), и вычислить её модель не может. Здесь она и не вычисляется —
    /// читается ответ движка, ровно как HP читаются из <c>battle_start</c>. HP при этом НЕ
    /// пересчитывается <see cref="LifeAttacks.Retype"/>: пропорцию движка проверяет не С1, а
    /// снимки, и своё HP юнит получает из них (<c>RestoreState</c>).</para>
    ///
    /// <para>Счётчик <c>retype</c> парный к нулю расхождений: если он покажет ноль, а расхождения
    /// вернутся, значит журнал перестал писать <c>type</c>, а не модель ошиблась. Незнакомый тип
    /// — пропуск с причиной, а не молчаливое сохранение старого. Порождённый прокачкой тип
    /// (<c>G000UG****</c>) с задачи 15 разбирается <c>GameRules.ImplOf</c> и пропуском больше не
    /// является.</para></summary>
    private static void FollowRetype(BattleUnitsEvent bu, BattleState state, Stats s)
    {
        if (state.Rules is not { } rules) { s.Skip("retype", "у состояния боя нет справочников"); return; }
        foreach (var identity in bu.Units)
        {
            if (identity.Type is null) continue;
            var u = state.Unit(identity.Id);
            if (u is null || string.Equals(u.Type.Id, identity.Type, StringComparison.OrdinalIgnoreCase)) continue;
            if (rules.ImplOf(identity.Type) is not { } impl)
            { s.Skip("retype", $"тип {identity.Type} не найден ни в Gunits, ни как порождённый прокачкой"); continue; }
            if (impl.Attack is not { } attack)
            { s.Skip("retype", $"у типа {identity.Type} нет атаки {impl.Type.AttackId} в Gattacks"); continue; }
            u.Type = impl.Type;
            u.Attack = attack;
            u.Attack2 = impl.Attack2;
            u.HpMax = (int)(identity.HpMax ?? impl.Type.HitPoints);
            s.Count("retype", true);
        }
    }

    private static void CheckAttacksPerTurn(BattleUnitsEvent bu, BattleState state, Dictionary<string, int> struck, Stats s)
    {
        foreach (var o in bu.Order)
        {
            var u = state.Unit(o.Id);
            if (u is null) { s.Skip("attacks_per_turn", "юнита нет в состоянии боя"); continue; }
            // Не ниже 1: тот же юнит снова ходит и после дара атаки (класс 19) — у одиночного удара в его новом ходу снова 1
            // (20261016-port-synth [502965]: S143UN000b после Give Attack S143UN000a, seq 509416).
            int ours = Math.Max(1, Attacks.AttacksPerTurn(u) - struck.GetValueOrDefault(o.Id));
            s.Count("attacks_per_turn", ours == o.Attacks);
            if (ours != o.Attacks) s.Mismatch($"attacks_per_turn {o.Id}: журнал {o.Attacks}, наше {ours}");
        }
    }

    /// <summary>Иммунитет: каждый вызов <c>isUnitImmuneToAttack</c> (GOG <c>0x65a528</c>) из журнала
    /// подаётся в <see cref="Immunity"/> и сравнивается с <c>ret</c> движка.
    ///
    /// <para><b>Ранг 1 — правило чтения ответа.</b> <c>always_only</c> — входной
    /// <c>checkAlwaysImmuneOnly</c>: при нём движок отвечает «да» только на
    /// <see cref="ImmunityLevel.Always"/>, иначе — на любой ненулевой уровень. Класс и источник
    /// берутся из самого события, а не из атаки юнита в состоянии боя: у одного носителя их две, и
    /// подстановка «первой» дала бы ложное сошлось.</para>
    ///
    /// <para><b>Расход <c>Once</c> смоделирован задачей 16 — ровно тогда, когда корпус его
    /// показал.</b> Прежняя редакция заряд не тратила и говорила: «если одна и та же четвёрка даст
    /// в журнале оба ответа, сверку надо переписывать на состояние, а не подгонять». Партия на
    /// большой карте это и дала — <c>S073UN024a</c> против <c>G000AA5023</c> (класс Damage,
    /// источник 4 Fire), три вызова подряд в одном бою:
    /// <c>seq 4467111 always_only=true → false</c>, <c>4467122 always_only=false → <b>true</b></c>,
    /// <c>4467168 always_only=false → <b>false</b></c>. Между вторым и третьим вард сгорел.</para>
    /// <para>Поэтому здесь ведётся маска потраченных за бой зарядов: ответ «иммунен» с уровнем
    /// <see cref="ImmunityLevel.Once"/> тратит заряд, и следующий вопрос про ту же тройку (юнит,
    /// класс, источник) получает «не иммунен». Маска считается по НАШИМ ответам, а не по журнальным,
    /// — иначе это была бы подгонка, а не модель. Все три вызова сходятся.
    /// <b>Долг:</b> <c>BattleSim</c> расхода заряда по-прежнему не знает — здесь он живёт только в
    /// харнессе С1.</para>
    ///
    /// <para><b>Наблюдение <c>immune_stub_class</c> (ранг 1, найдено этим харнессом).</b>
    /// <see cref="Immunity.Checked"/> описывает слот 8 vftable <c>CBatAttack</c>: у семи классов там
    /// заглушка «иммунитета не бывает». В журнале эти классы (<c>BoostDamage</c>, <c>GiveAttack</c>,
    /// <c>Doppelganger</c>) всё же спрашивают <c>isUnitImmuneToAttack</c> — но <b>только</b> с
    /// <c>always_only = true</c>, то есть из другого места движка (проверка выбора цели), а не из
    /// <c>onHit</c>. Модель этим не опровергнута; счётчик стоит, чтобы утверждение не пропало.</para></summary>
    private static void CheckImmune(ImmuneEvent e, BattleState state, HashSet<(string, long, long)> spent, Stats s)
    {
        var u = state.Unit(e.Unit);
        if (u is null) { s.Skip("immune", "юнита нет в состоянии боя"); return; }
        if (!Enum.IsDefined(typeof(AttackClass), (int)e.Class))
        { s.Skip("immune", $"класс атаки {e.Class} вне LattC"); return; }

        var cls = (AttackClass)e.Class;
        var attack = new AttackType(e.Attack, 0, cls, 0, 0, 0, 0, false, 0, Source: (int)e.Source);
        var level = Immunity.LevelOf(u, attack);
        var key = (e.Unit, e.Class, e.Source);
        if (level == ImmunityLevel.Once && spent.Contains(key)) level = ImmunityLevel.NotImmune;
        bool ours = e.AlwaysOnly ? level == ImmunityLevel.Always : level != ImmunityLevel.NotImmune;
        if (ours && level == ImmunityLevel.Once) spent.Add(key);   // заряд потрачен этим же ответом

        s.Count("immune", ours == e.Ret);
        if (e.AlwaysOnly && !Immunity.Checked(cls)) s.Count("immune_stub_class", true);
        if (ours != e.Ret)
            s.Mismatch($"immune {e.Unit} <- {e.Attack} (класс {cls}, источник {e.Source}, " +
                       $"always_only={e.AlwaysOnly}): журнал {e.Ret}, наше {ours} (уровень {level})");
    }

    /// <summary>Обереги, наложенные в бою, — по строке <c>attack_hit</c> схемы 6.1 класса 24
    /// (<c>CBatAttackBestowWards::onHit</c> <c>0x65d528</c>). Цель — <c>target</c>, наложивший —
    /// <c>attacker</c> (строка <c>attack_new</c> по <c>new_seq</c>). Снимает их
    /// <c>turn_before</c> наложившего (<see cref="Immunity.ClearWards"/>, <c>beforeBattleTurn</c>
    /// <c>0x62673f</c>) — см. <see cref="Measure"/>.
    /// <para><b>Ранг 1 — что легло, говорит журнал, не модель:</b> <c>mods_after \ mods_before</c>.
    /// Эти обереги кладутся через <see cref="Immunity.Bestow"/> с учётом у наложившего, и
    /// <c>ward_hit</c> сверяет, что наши потолки (повтор, восемь пар) согласны с движком.</para>
    /// <para><b>Пары кончились (<c>0x62184a</c>).</b> <c>CMidUnit::addModifier</c> (<c>0x5e9902</c>)
    /// движок зовёт до потолка, поэтому оберег из <c>Gattacks.WARD1…4</c>, которого нет ни в
    /// <c>mods_before</c>, ни в добавленных, на юните стоит, но не снимется никогда: он кладётся без
    /// наложившего и считается в <c>ward_unregistered</c>.</para>
    /// <para>Лёгший оберег возвращает заряд <c>Once</c> своего источника (<c>0x62196c</c>) — здесь
    /// это снятие потраченного из маски харнесса.</para></summary>
    private static void FollowWards(AttackHitEvent e, BattleState state, HashSet<(string, long, long)> spent, Stats s)
    {
        if (e.DispClass != (long)AttackClass.BestowWards) return;
        if (!e.Found || e.NewSeq < 0) { s.Skip("ward_hit", "цели нет в слотах или attack_new не найдена"); return; }
        if (state.Unit(e.Target) is not { } target || state.Unit(e.Attacker) is not { } caster)
        { s.Skip("ward_hit", "цели или наложившего нет в состоянии боя"); return; }
        if (state.Rules is not { Modifiers: { } mods } rules || !rules.Attacks.TryGetValue(e.Attack, out var attack))
        { s.Skip("ward_hit", "нет справочников или атаки в Gattacks"); return; }
        if (e.ModsAdded.FirstOrDefault(id => !mods.ContainsKey(id)) is { } unknown)
        { s.Skip("ward_hit", $"модификатор {unknown} не найден в Gmodif"); return; }

        var added = e.ModsAdded.Select(id => mods[id]).ToList();
        int placed = Immunity.Bestow(target, added, caster);
        s.Count("ward_hit", placed == added.Count);
        if (placed != added.Count)
            s.Mismatch($"ward_hit {caster.Id} -> {e.Target} ({e.Attack}): движок положил {added.Count}, наше {placed}");
        foreach (var ward in rules.WardsOf(attack))
        {
            bool known = Contains(e.ModsBefore, ward.Id) || Contains(e.ModsAdded, ward.Id);
            if (known || target.Wards.Any(w => string.Equals(w.Id, ward.Id, StringComparison.OrdinalIgnoreCase))) continue;
            target.Wards.Add(ward);   // без наложившего: движок его не снимет
            added.Add(ward);
            s.Count("ward_unregistered", true);
        }
        foreach (var ward in added) RefreshSpent(spent, target.Id, ward);
    }

    /// <summary>Зелье, выпитое в бою: его <c>MOD_POTION</c> действует на цель сразу и до конца боя (ai-q33: броня
    /// Squire 0 → 15 после <c>potion_use</c> seq 1154687, отчёт B). Своей атаки у зелья нет, поэтому без этого шага
    /// С1 считал броню без модификатора.</summary>
    private static void FollowPotion(PotionUseEvent e, BattleState state, IReadOnlyDictionary<string, Item> items, Stats s)
    {
        if (state.Unit(e.Target) is not { } target) { s.Skip("potion_boost", "цели нет в состоянии боя"); return; }
        if (e.ItemType is not { } type || !items.TryGetValue(type, out var item))
        { s.Skip("potion_boost", $"тип зелья {e.ItemType} не найден в GItem"); return; }
        if (item.PotionModifierId.Length == 0) return;                     // лечебное — броню не трогает
        if (state.Rules?.Modifiers is not { } mods || !mods.TryGetValue(item.PotionModifierId, out var boost))
        { s.Skip("potion_boost", $"MOD_POTION {item.PotionModifierId} не найден в Gmodif"); return; }
        target.Potions.Add(boost);
        s.Count("potion_boost", true);
    }

    /// <summary>Превращение чужим (<c>attack_hit</c> класса 22) — тип цели подменяет модель
    /// (<see cref="TransformAttacks.TransformOther"/>), а не журнал: <c>battle_units</c> зонд 7 пишет
    /// раз на бой, и превращённого С1 иначе мерил бы прежней атакой (20260919-human [4934456]:
    /// S073UN019c точность 70 и урон 30 формы Ведьмы против 80/80 своих). Легло ли — бит 22 в
    /// <c>st_after</c> (иммунитет и пустой <c>Gtransf</c> его не ставят).</summary>
    private static void FollowTransform(AttackHitEvent e, BattleState state, Stats s)
    {
        if (e.DispClass != (long)AttackClass.TransformOther || !e.Found || (e.StAfter >> 22 & 1) == 0) return;
        if (state.Unit(e.Target) is not { } target) { s.Skip("transform", "цели нет в состоянии боя"); return; }
        if (state.Rules is not { } rules || !rules.Attacks.TryGetValue(e.Attack, out var attack))
        { s.Skip("transform", $"атака {e.Attack} не найдена в Gattacks"); return; }
        TransformAttacks.TransformOther(attack, target, state);
        s.Count("transform", target.Transformed);
    }

    /// <summary>Срок длинного превращения (<see cref="BattleSim.BeginTurn"/>, <c>0x6287d6</c>): в первом
    /// <c>turn_before</c> хода превращённого бросок <c>rng(100)</c> идёт после бросков длинных DoT
    /// (биты 16, 18, 20 снимка), и <see cref="Attacks.LongEffectBreaks"/> по нему сверяется с битом 22
    /// в <c>turn_after</c> того же юнита — <c>transform_break</c>. Снялось — откат модели, дальше юнит
    /// бьёт своей атакой. Умерший от тика бросает тоже (смерть снимает биты DoT, но не 22/23; <c>20261019-cov [1531355]</c>):
    /// его бросок — последний боевой до следующего снимка (<c>round</c> или <c>turn_*</c>), броски длинных DoT до гибели — раньше.</summary>
    private static void FollowTransformBreak(TurnEvent t, BattleUnit mover, IReadOnlyList<CorpusEvent> events,
                                             BattleState state, RulesConstants c, Stats s)
    {
        if (!mover.TransformedLong || mover.TurnBegunRound == t.Round) return;
        mover.TurnBegunRound = (int)t.Round;
        var self = t.Units.FirstOrDefault(u => string.Equals(u.Id, mover.Id, StringComparison.OrdinalIgnoreCase));
        var after = events.OfType<TurnEvent>().FirstOrDefault(x => x.After && x.Seq > t.Seq &&
                        string.Equals(x.Unit, mover.Id, StringComparison.OrdinalIgnoreCase));
        var afterSelf = after?.Units.FirstOrDefault(u => string.Equals(u.Id, mover.Id, StringComparison.OrdinalIgnoreCase));
        if (self is null || afterSelf is null) { s.Skip("transform_break", "нет снимка ходящего до или после хода"); return; }
        var rolls = events.OfType<RngEvent>().Where(x => !x.Std && x.Tid == t.Tid && x.Seq > t.Seq);
        RngEvent? roll;
        if (afterSelf.Dead)
        {
            long next = events.Where(x => x.Seq > t.Seq && x.Tid == t.Tid && x is RoundEvent or TurnEvent).Select(x => x.Seq).DefaultIfEmpty(long.MaxValue).Min();
            roll = rolls.LastOrDefault(x => x.Seq < next);
        }
        else roll = rolls.Skip(new[] { 16, 18, 20 }.Count(bit => (self.St >> bit & 1) != 0)).FirstOrDefault();
        if (roll is null || roll.Max != 100) { s.Skip("transform_break", "нет броска rng(100) после бросков DoT"); return; }
        bool ours = Attacks.LongEffectBreaks(new ScriptedRng([(int)roll.R]), c, (int)t.Round - mover.TransformAppliedRound);
        bool journal = (afterSelf.St >> 22 & 1) == 0;
        s.Count("transform_break", ours == journal);
        if (ours != journal)
            s.Mismatch($"transform_break {mover.Id} раунд {t.Round} r={roll.R}: журнал {journal}, наше {ours}");
        if (ours) TransformAttacks.Untransform(mover, state);
    }

    /// <summary>Классы, у которых <c>canPerform</c> смотрит на состояние цели и чьё «нельзя» видно в журнале
    /// второй атаки: разбить броню, понизить уровень, замедлить, ослабить (<see cref="Attacks.CanPerform"/>).</summary>
    private static readonly string[] RareKeys = ["drain_level", "shatter", "lower_initiative", "second_can_perform", "second_refused"];

    /// <summary>Каталог третьей партии оригинала на синтетических картах: A3–A5 карты 1 сыграны (DrainLevel, Shatter,
    /// LowerInitiative сработали у движка, docs/synthetic-maps-port/analysis/20261016-original.md).</summary>
    private const string RareClassesJournal = "20261016c-port-synth";

    private static readonly AttackClass[] RefusingSecond = SnapshotCanPerform.StateClasses;

    /// <summary>Классы, чьё действие <see cref="FollowRareHit"/> повторяет по строке <c>attack_hit</c>.</summary>
    private static readonly AttackClass[] RareHitClasses = [AttackClass.Shatter, AttackClass.DrainLevel, AttackClass.LowerInitiative];

    /// <summary><c>canPerform</c> второй атаки по журналу (16.10.2026, третья партия оригинала). Вторая атака
    /// идёт только по тем, в кого попала первая и кто остался жив; по каждой такой цели движок сначала зовёт
    /// <c>canPerform</c> и при «нельзя» не бросает ничего. Число целей, по которым бросок был, журнал называет:
    /// у Shatter промаха нет (заглушка) — цель дошла до <c>onHit</c> (строка <c>attack_hit</c> этого действия) или
    /// оказалась иммунной (<c>immune</c> «да»; сам вопрос зонд пишет не каждый раз — 20261016c seq 531322 без него);
    /// у DrainLevel и LowerInitiative — по строке <c>miss</c> на цель. Модели даётся состояние целей из снимка до
    /// хода (статусы 2, 3, 13, 29, снятая броня, укрепление; тип — из <c>battle_units</c>), и число её «можно»
    /// сравнивается с числом бросков движка — <c>second_can_perform</c>. <c>second_refused</c> — сколько раз движок
    /// отказал хотя бы одной цели (без этих случаев сверка ничего не различает).</summary>
    private static void CheckSecondCanPerform(AttackNewEvent an, IReadOnlyList<CorpusEvent> events, BattleState state,
                                              IReadOnlyList<CorpusEvent> snapshots, Stats s)
    {
        const string key = "second_can_perform";
        if (an.Number != 2 || !RefusingSecond.Any(cls => (long)cls == an.Class)) return;
        if (state.Unit(an.Attacker) is not { Attack2: { } second } attacker || (long)second.Class != an.Class)
        { s.Skip(key, "атакующего или его второй атаки нет в состоянии боя"); return; }
        // Состояние целей — снимок до хода: первая атака того же класса изменила бы его в этом же ходе.
        if (attacker.Attack.Class == second.Class) { s.Skip(key, "первая атака того же класса, что вторая"); return; }
        int at = IndexOfSeq(events, an.Seq);
        if (at < 0) { s.Skip(key, "attack_new не найдена среди событий боя"); return; }
        int start = at - 1;
        while (start >= 0 && events[start] is not TurnEvent &&
               !(events[start] is AttackNewEvent { Number: 1 } first && Same(first.Attacker, an.Attacker))) start--;
        if (start < 0 || events[start] is not AttackNewEvent) { s.Skip(key, "нет attack_new первой атаки в том же ходе"); return; }

        var dealt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (int i = start + 1; i < at; i++)
            if (events[i] is DamageEvent d && Same(d.Attacker, an.Attacker)) dealt[d.Target] = dealt.GetValueOrDefault(d.Target) + d.Ret;
        var candidates = new List<(BattleUnit Unit, UnitState Before)>();
        foreach (var (id, dmg) in dealt)
        {
            if (state.Unit(id) is not { } t || SnapshotOf(t, snapshots, an.Seq) is not { } before)
            { s.Skip(key, "цели первой атаки нет в состоянии боя или в снимках"); return; }
            if (before.Hp - dmg > 0) candidates.Add((t, before));
        }
        if (candidates.Count == 0) return;   // первая атака (по строкам dmg) никого не ранила, не убив, — сверять нечего

        int rolled = 0;
        for (int i = at + 1; i < events.Count && events[i] is not (AttackNewEvent or TurnEvent or BattleEndEvent); i++)
            rolled += second.Class == AttackClass.Shatter
                ? events[i] is AttackHitEvent { Found: true } hit && hit.NewSeq == an.Seq
                  || events[i] is ImmuneEvent { Ret: true } im && Same(im.Attack, an.Attack) ? 1 : 0
                : events[i] is MissEvent ? 1 : 0;
        int ours = candidates.Count(c => SnapshotCanPerform.Of(attacker, second, c.Unit, c.Before));
        s.Count(key, ours == rolled);
        if (rolled < candidates.Count) s.Count("second_refused", ours == rolled);
        if (ours != rolled)
            s.Mismatch($"{key} {an.Attacker} {second.Class} seq {an.Seq}: целей {candidates.Count}, бросков движка {rolled}, наших «можно» {ours}");
    }

    /// <summary>Действие редких классов по строке <c>attack_hit</c> — модель повторяет удар и сверяется с журналом:
    /// <list type="bullet">
    /// <item><c>drain_level</c> — понижение уровня (<c>0x662880</c>): HP после пересчёта <c>hp · новый потолок /
    /// старый</c> против <c>hp_after</c> и бит 26 в <c>st_after</c>; тип модели дальше сверит со следующим
    /// <c>battle_units</c> <see cref="FollowRetype"/> (совпал — счётчик <c>retype</c> не растёт);</item>
    /// <item><c>shatter</c> — снятая броня (<c>0x65d8dc</c>): <c>min(снятое + QTY_DAM, 100)</c> против поля
    /// <c>shattered</c> первого снимка после удара;</item>
    /// <item><c>lower_initiative</c> — бит 13 (и 14 у долгого) в <c>st_after</c> против уровня модели.</item>
    /// </list></summary>
    private static void FollowRareHit(AttackHitEvent e, BattleState state, IReadOnlyList<CorpusEvent> snapshots,
                                      RulesConstants c, Stats s)
    {
        if (!RareHitClasses.Any(cls => (long)cls == e.DispClass) || !e.Found) return;
        var cls = (AttackClass)e.DispClass;
        string key = cls switch { AttackClass.Shatter => "shatter", AttackClass.DrainLevel => "drain_level", _ => "lower_initiative" };
        if (state.Unit(e.Target) is not { } target || state.Unit(e.Attacker) is not { } attacker)
        { s.Skip(key, "цели или атакующего нет в состоянии боя"); return; }
        if (state.Rules is not { } rules || !rules.Attacks.TryGetValue(e.Attack, out var attack))
        { s.Skip(key, $"нет справочников или атаки {e.Attack} в Gattacks"); return; }
        switch (cls)
        {
            case AttackClass.DrainLevel:
            {
                target.Hp = (int)e.HpBefore;
                var outcome = Attacks.Apply(attacker, attack, target, new ScriptedRng([0, 0]), c, state.Round, state);
                bool ok = outcome.Applied && target.Hp == e.HpAfter && Bit(e.StAfter, 26);
                s.Count(key, ok);
                if (!ok) s.Mismatch($"{key} {e.Attacker} -> {e.Target}: журнал HP {e.HpBefore} → {e.HpAfter}, наше {target.Hp} ({target.Type.Id})");
                break;
            }
            case AttackClass.Shatter:
            {
                if (SnapshotOf(target, snapshots, e.Seq) is not { } before || NextSnapshotOf(target, snapshots, e.Seq) is not { } after)
                { s.Skip(key, "нет снимка цели до или после удара"); return; }
                target.ShatteredArmor = (int)before.Shattered;
                Attacks.Apply(attacker, attack, target, new ScriptedRng([]), c, state.Round, state);
                s.Count(key, target.ShatteredArmor == after.Shattered);
                if (target.ShatteredArmor != after.Shattered)
                    s.Mismatch($"{key} {e.Attacker} -> {e.Target}: журнал снято {before.Shattered} → {after.Shattered}, наше {target.ShatteredArmor}");
                break;
            }
            default:
            {
                Attacks.Apply(attacker, attack, target, new ScriptedRng([0, 0]), c, state.Round, state);
                bool ok = (target.LowerInitiativeLevel > 0) == Bit(e.StAfter, 13) && target.LowerInitiativeLong == Bit(e.StAfter, 14);
                s.Count(key, ok);
                if (!ok) s.Mismatch($"{key} {e.Attacker} -> {e.Target}: st_after {e.StAfter:x}, уровень модели {target.LowerInitiativeLevel}");
                break;
            }
        }
    }

    private static bool Bit(long st, int bit) => SnapshotCanPerform.Bit(st, bit);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Индекс события с данным seq (события боя отсортированы по seq); −1 — такого нет.</summary>
    private static int IndexOfSeq(IReadOnlyList<CorpusEvent> events, long seq)
    {
        int lo = 0, hi = events.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (events[mid].Seq < seq) lo = mid + 1; else hi = mid;
        }
        return events.Count > 0 && events[lo].Seq == seq ? lo : -1;
    }

    /// <summary>Первый снимок юнита с seq больше заданного; null — снимков после нет.</summary>
    private static UnitState? NextSnapshotOf(BattleUnit unit, IReadOnlyList<CorpusEvent> snapshots, long seq)
    {
        foreach (var snap in snapshots)
        {
            if (snap.Seq <= seq) continue;
            var units = snap switch { TurnEvent t => t.Units, RoundEvent r => r.Units, BattleStartEvent b => b.Units, _ => null };
            if (units?.FirstOrDefault(u => Same(u.Id, unit.Id)) is { } found) return found;
        }
        return null;
    }

    private static bool Contains(IReadOnlyList<string>? ids, string id) =>
        ids is not null && ids.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Возврат заряда <c>Once</c> источника оберега в маске харнесса (<see cref="CheckImmune"/>).</summary>
    private static void RefreshSpent(HashSet<(string, long, long)> spent, string unit, Modifier ward)
    {
        foreach (var el in ward.Elements)
            if (el.Type == ModifierElementType.Immunity && el.Immunity is { } source)
                spent.RemoveWhere(k => string.Equals(k.Item1, unit, StringComparison.OrdinalIgnoreCase) && k.Item3 == source);
    }

    /// <summary>Четвёрки (юнит, класс, источник, always_only), на которые журнал дал ОБА ответа.
    /// Счёт отдельным проходом по бою, а не внутри <see cref="CheckImmune"/>: там видно одно событие,
    /// а не пара.
    /// <para><b>Два ответа — не всегда расхождение (задача 8).</b> Оберег <see cref="ImmunityLevel.Once"/>
    /// гаснет на первом «иммунен», и дальше движок честно отвечает «нет»: такая четвёрка — это
    /// наблюдение расхода заряда, и она идёт в <c>immune_once_spent</c>, если уровень у юнита по нашей
    /// модели Once и ответы идут «да…да, нет…нет» без возврата к «да». Всё прочее с двумя ответами —
    /// <c>immune_one_answer разошлось</c>, как и раньше. Все четыре четвёрки корпуса с двумя ответами —
    /// ровно такой расход: Fire (источник 4) по <c>S073UN024a</c> (passive-004 [4465238]),
    /// <c>S073UN0213</c>, <c>S073UN0212</c> ([1705714]) и <c>S073UN01eb</c> ([2275284],
    /// 20260911-passive/passive-001), и во всех четырёх <c>immune</c> сходится по маске зарядов.</para></summary>
    private static void CountAmbiguousImmune(IReadOnlyList<CorpusEvent> events, BattleState state, Stats s)
    {
        var answers = new Dictionary<(string, long, long, bool), List<ImmuneEvent>>();
        foreach (var e in events.OfType<ImmuneEvent>())
        {
            var key = (e.Unit, e.Class, e.Source, e.AlwaysOnly);
            if (!answers.TryGetValue(key, out var list)) answers[key] = list = [];
            list.Add(e);
        }
        foreach (var (_, list) in answers)
        {
            if (list.Select(e => e.Ret).Distinct().Count() == 1) { s.Count("immune_one_answer", true); continue; }
            if (OnceSpent(list, state)) { s.Count("immune_once_spent", true); continue; }
            s.Count("immune_one_answer", false);
        }
    }

    /// <summary>Ответы одной четвёрки объясняются расходом оберега: уровень Once и «да» только в начале.</summary>
    private static bool OnceSpent(IReadOnlyList<ImmuneEvent> list, BattleState state)
    {
        var e = list[0];
        if (state.Unit(e.Unit) is not { } u || !Enum.IsDefined(typeof(AttackClass), (int)e.Class)) return false;
        var attack = new AttackType(e.Attack, 0, (AttackClass)e.Class, 0, 0, 0, 0, false, 0, Source: (int)e.Source);
        return Immunity.LevelOf(u, attack) == ImmunityLevel.Once && list.SkipWhile(x => x.Ret).All(x => !x.Ret);
    }

    /// <summary>Номер атаки (1/2) для каждого события power и dmg_buffs random=false по порядку
    /// событий внутри хода. Окно хода — от turn_before (или battle_units для первого хода) до
    /// следующего такого якоря; события до первого якоря — своё окно.
    /// power: пар power+miss в окне M, вторая атака идёт после всех целей первой и только по тем, по
    /// которым первая попала и которые живы (ранг 1, corpus/20260906-c/e — Attacks/BattleSim.Act),
    /// поэтому N2 = число dmg окна с живой целью в turn_after окна, первые M − N2 — атака 1, остальные —
    /// атака 2. Правило объективно (не по значению) и проверяется значением power: 30 у Fear
    /// Shamaness против 80 первой атаки, 90 у яда Spider против 80. Потолок: вторая атака класса с
    /// событием dmg (Damage/Drain — не Poison, яд dmg не пишет) сломала бы счёт N2 — такой юнит
    /// уходит в пропуск с причиной.
    /// dmg_buffs random=false: оценка хода ИИ зовёт computeDamageWithBuffs по каждой атаке юнита с
    /// уроном (Attacks.HasDamage) по порядку — B35 B20 B35 B20 у Master Thug (35 — атака 1, 20 — яд);
    /// событие i юнита в окне — атака list[i mod count], где list = атаки с уроном.</summary>
    private static Dictionary<long, int> AttackIndexOf(IReadOnlyList<CorpusEvent> events, BattleState state, Stats s)
    {
        var map = new Dictionary<long, int>();
        var window = new List<CorpusEvent>();
        foreach (var e in events)
        {
            if (e is TurnEvent { After: false } or BattleUnitsEvent) { Flush(); }
            window.Add(e);
        }
        Flush();
        return map;

        void Flush()
        {
            if (window.Count == 0) return;
            var after = window.OfType<TurnEvent>().LastOrDefault(t => t.After);
            var before = window.OfType<TurnEvent>().FirstOrDefault(t => !t.After);
            var powers = window.OfType<PowerEvent>().ToList();
            int misses = window.Count(x => x is MissEvent);
            var actor = powers.Count > 0 ? state.Unit(powers[0].Unit) : null;
            // Вторая атака-яд по отступавшему до хода не кладётся (Attacks.RejectsRetreating) — такое
            // попадание в N2 не идёт; то же правило стоит в BattleSim.Act и в C2HarnessTests.Recorded.
            bool refused = actor?.Attack2 is { } second2 && Attacks.RejectsRetreating(second2.Class);
            int aliveHits = window.OfType<DamageEvent>().Count(d =>
                after?.Units.FirstOrDefault(u => string.Equals(u.Id, d.Target, StringComparison.OrdinalIgnoreCase))?.Hp > 0 &&
                !(refused && before?.Units.FirstOrDefault(u => string.Equals(u.Id, d.Target, StringComparison.OrdinalIgnoreCase))?.Retreating == true));
            bool secondWritesDmg = actor?.Attack2 is { } a2 && a2.Class is AttackClass.Damage or AttackClass.Drain or AttackClass.DrainOverflow;
            for (int i = 0; i < powers.Count; i++)
            {
                // Журнал сам называет атаку — событие attack_new несёт её id на каждое действие
                // (схема v4). Это сильнее порядкового правила и снимает его долг.
                if (actor is not null && NamedAttack(window, actor, powers[i].Seq) is { } fromJournal)
                { map[powers[i].Seq] = fromJournal; continue; }
                if (powers.Count != misses || after is null) { s.Skip("power", $"в окне хода power {powers.Count}, miss {misses}, turn_after {(after is null ? "нет" : "есть")}"); continue; }
                if (secondWritesDmg) { s.Skip("power", "вторая атака с событием dmg — привязка по N2 не определена"); continue; }
                map[powers[i].Seq] = actor?.Attack2 is null || i < misses - aliveHits ? 1 : 2;
            }
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var db in window.OfType<DamageBuffsEvent>().Where(x => !x.Random))
            {
                var u = state.Unit(db.Unit);
                if (u is null) continue;
                var withDamage = new[] { u.Attack, u.Attack2 }.Where(a => a is not null && Attacks.HasDamage(a.Class)).ToList();
                int i = counts.GetValueOrDefault(db.Unit);
                counts[db.Unit] = i + 1;
                if (withDamage.Count > 0) map[db.Seq] = ReferenceEquals(withDamage[i % withDamage.Count], u.Attack) ? 1 : 2;
            }
            window.Clear();
        }
    }

    /// <summary>Номер атаки, названный самим журналом: поле <c>number</c> последнего события
    /// <c>attack_new</c> того же юнита ПЕРЕД этим <c>power</c>. Движок пишет <c>attack_new</c> на
    /// каждое начатое действие и сам нумерует атаку (схема v4). null — событий нет (журнал схемы v3
    /// и раньше) либо номер не 1 и не 2: тогда работает порядковое правило.
    ///
    /// <para><b>Зачем.</b> Порядковое правило считает вторые атаки по числу выживших целей и на
    /// партии ошиблось дважды: <c>S073UN01e8</c> и <c>S073UN0234</c> получали номер 2 при
    /// журнальной точности 88 и 85 — ровно точности их ПЕРВОЙ атаки. Оба окна журнал размечает
    /// сам.</para>
    ///
    /// <para><b>Почему по номеру, а не по id атаки.</b> У прокачанного юнита движок порождает и
    /// атаки: в окне выше <c>S073UN01e8</c> бьёт <c>G000DA4ea6</c> (номер 1) и <c>G000DC4ea6</c>
    /// (номер 2, класс 12 Poison), тогда как в <c>Gattacks</c> у его типа лежат <c>g000aa0154</c> и
    /// <c>g000aa1154</c>. Сравнение id тут не сходится по построению (см. <c>UnitGenerator.Grow</c>:
    /// порождённые id атак мы намеренно не заводим), а номер — сходится.</para></summary>
    private static int? NamedAttack(IReadOnlyList<CorpusEvent> window, BattleUnit actor, long powerSeq)
    {
        long? number = null;
        foreach (var a in window.OfType<AttackNewEvent>())
        {
            if (a.Seq > powerSeq) break;
            if (string.Equals(a.Attacker, actor.Id, StringComparison.OrdinalIgnoreCase)) number = a.Number;
        }
        return number is 1 or 2 ? (int)number : null;
    }

    private static AttackType? AttackOf(BattleUnit u, IReadOnlyDictionary<long, int> attackIndex, long seq, string key, Stats s)
    {
        int idx = attackIndex.GetValueOrDefault(seq, 1);
        if (idx == 1) return u.Attack;
        if (u.Attack2 is null) { s.Skip(key, "по порядку событий это вторая атака, а у юнита её нет"); return null; }
        return u.Attack2;
    }

    /// <summary>Атака с учётом цепочки модификаторов юнита (заклинание с карты, предмет, вард).
    /// Пустая цепочка возвращает ту же самую ссылку — Ф4/Ф5 не платят ни аллокации.</summary>
    private static AttackType Effective(BattleUnit u, AttackType attack) =>
        EffectiveStats.OfAttack(attack, u.Chain).Attack;

    /// <summary>Атака, у которой есть запасная (<c>Gattacks.ALT_ATTACK</c> — в ванили таких две:
    /// <c>g000aa0164</c> Волчьего лорда и <c>g000aa0171</c> Доппельгангера). По врагу удар целиком
    /// отдаётся запасной атаке (см. <see cref="AttackType.HasAltAttack"/>, ранг 1), и тогда движок
    /// пишет ЕЁ точность и ЕЁ урон, а <c>power</c>/<c>dmg_buffs</c> личности атаки не несут —
    /// какая из двух дала число, из события не видно. Сверять нечем: пропуск с причиной.
    /// Закрывает событие <c>attack_new</c> корпуса v4 — оно несёт id атаки на каждое действие
    /// (долг в Ф7, см. docs/fidelity-f6.md).</summary>
    private static bool AltAttackHides(AttackType attack) => attack.HasAltAttack;

    private static void CheckPower(PowerEvent p, BattleState state, IReadOnlyDictionary<long, int> attackIndex, Stats s)
    {
        if (p.Ret == NotComputed) { s.Skip("power", "notComputed"); return; }
        var u = state.Unit(p.Unit);
        if (u is null) { s.Skip("power", "юнита нет в состоянии боя"); return; }
        if (!attackIndex.ContainsKey(p.Seq)) return;   // причина пропуска уже записана в AttackIndexOf
        if (AttackOf(u, attackIndex, p.Seq, "power", s) is not { } baseAttack) return;
        if (AltAttackHides(baseAttack))
        { s.Skip("power", "у атаки есть запасная (ALT_ATTACK) — чью точность вернул движок, событие не показывает"); return; }

        var attack = Effective(u, baseAttack);
        int shift = state.PowerShift(u);
        int ours = Formulas.ShiftPower(Formulas.AttackPower(attack), shift);
        bool first = ReferenceEquals(baseAttack, u.Attack);
        if (ours != p.Ret && AoeStep(attack, u, state, ours, (int)p.Ret) is not null)
        {   // площадная атака: точность падает на 10 за каждую следующую цель — см. AoeStep
            s.Count("power_aoe_step", true);
            return;
        }
        s.Count(first ? "power" : "power_attack2", ours == p.Ret);
        if (shift != 0) s.Count("power_ai_difficulty", ours == p.Ret);
        if (ours != p.Ret) s.Mismatch($"power {p.Unit} (атака {attackIndex[p.Seq]}): журнал {p.Ret}, наше {ours}");
    }

    /// <summary>Номер цели площадной атаки, если журнальная точность лежит на лестнице
    /// <c>база − 10·k</c>, иначе null.
    ///
    /// <para><b>Ранг 1 по лестнице, ранг 3 по номеру цели.</b> Движок вычитает из точности
    /// <c>attackPowerReduction</c> — по 10 за каждую следующую цель площадной атаки (survey §4:
    /// <c>A/src/hooks.cpp:1737,1816</c>, <c>B:3831</c>, тест 80,70,60,50,40,30). До Ф6 корпус этого
    /// ни разу не показывал («power ret == POWER во всех 35 событиях», <c>Formulas.AttackPower</c>);
    /// прогон Ф6 показал: герой <c>g000uu0021</c> (REACH 1, точность 80) даёт 80/70/60/50 при
    /// четырёх врагах, вампир <c>g000uu0097</c> — 80/60/50/40/30 при шести. Обе последовательности
    /// целиком лежат на лестнице и не выходят за число целей.</para>
    ///
    /// <para>Сам номер <c>k</c> событие <c>power</c> не несёт, поэтому проверяется <b>лестница</b>, а
    /// не конкретное число, и такие случаи идут отдельным счётчиком <c>power_aoe_step</c>, а не в
    /// <c>power</c>: это слабее полной сверки, и выдавать одно за другое нельзя. Полную сверку даст
    /// реализация <c>attackPowerReduction</c> в <c>Formulas.AttackPower</c> (Ф7) — тогда счётчик
    /// уходит.</para></summary>
    private static int? AoeStep(AttackType attack, BattleUnit u, BattleState state, int ours, int recorded)
    {
        if (attack.Reach != AttackType.ReachAll) return null;
        int drop = ours - recorded;
        if (drop <= 0 || drop % 10 != 0) return null;
        int targets = state.Enemies(u).Count();
        return drop / 10 <= targets - 1 ? drop / 10 : null;
    }

    private static void CheckDamageMax(DamageMaxEvent dm, BattleState state, RulesConstants c, Stats s)
    {
        var u = state.Unit(dm.Unit);
        if (u is null) { s.Skip("dmg_max", "юнита нет в состоянии боя"); return; }
        // GlobalId, а не Id: у прокачанного юнита в бою стоит порождённый тип G000UG****, а прошитый
        // список десяти лидеров движок сверяет с базовым UU (getGlobalUnitImplId) — UnitType.GlobalId.
        int ours = Formulas.DamageMax(c, u.Type.GlobalId);
        s.Count("dmg_max", ours == dm.Ret);
        if (ours != dm.Ret) s.Mismatch($"dmg_max {dm.Unit}: журнал {dm.Ret}, наше {ours}");
    }

    /// <summary>Броня. <b>Задача 5 Ф7:</b> защиту укрепления даёт наше правило
    /// (<see cref="FortArmorOf"/>), а не поле <c>fort</c> снимка.
    /// <para>Подстановка идёт там, где движок сам сообщил НЕНУЛЕВУЮ защиту. Причина — ранг 3 на
    /// момент внутри хода: сохранение снимается раз в ход, отряд входит в город и выходит внутри
    /// хода, и «юнит в городе по снимку» не значит «юнит был в городе в бою». Обратное направление
    /// (журнал 0, снимок — город) поэтому и не подставляется: это выдумывало бы броню там, где её
    /// не было. Равенство наших величин журнальным меряет отдельно <c>fort_armor_source</c>.</para></summary>
    private static void CheckArmor(ArmorEvent a, BattleState state, IReadOnlyList<CorpusEvent> snapshots,
                                   RulesConstants c, Stats s, Func<string, int?> fortArmor)
    {
        if (a.Ret == NotComputed) { s.Skip("armor", "notComputed"); return; }
        var u = state.Unit(a.Unit);
        if (u is null) { s.Skip("armor", "юнита нет в состоянии боя"); return; }
        ApplySnapshot(u, snapshots, a.Seq);
        if (u.FortificationArmor > 0 && fortArmor(a.Unit) is { } ourFort) u.FortificationArmor = ourFort;
        int ours = Formulas.Armor(u, c);
        s.Count("armor", ours == a.Ret);
        if (ours != a.Ret) s.Mismatch($"armor {a.Unit}: журнал {a.Ret}, наше {ours}");
    }

    /// <summary><c>computeDamageWithBuffs</c> (GOG <c>0x5d2db1</c>) читает не только атаку: между
    /// базой и потолком он прибавляет проценты статусов <c>BoostDamageLvl1..4</c> и вычитает
    /// <c>LowerDamageLvl1..2</c> того же юнита. Статусы в журнале есть — это сырая маска
    /// <c>unitStatuses</c> в поле <c>st</c> каждого снимка, — поэтому их не угадывают, а читают
    /// (<see cref="DamageStatuses"/>).
    /// <para><b>Зачем это здесь.</b> Без них харнесс давал 40 расхождений на журнале
    /// <c>20260910-passive/passive-004.jsonl</c>: <c>S073UN01f0</c> (Tenderfoot, атака
    /// <c>g000aa0034</c> класса 8 <c>LEVEL 2</c>) бафал союзников в девяти боях из десяти, и ровно
    /// там журнальный урон был в полтора раза (<c>BATBOOSTD2</c> = 50 %) больше расчёта. В
    /// единственном бою того же стека без баффа расчёт сходился точь-в-точь.</para></summary>
    private static void CheckDamageBuffs(DamageBuffsEvent db, BattleState state, IReadOnlyList<CorpusEvent> snapshots,
                                         IReadOnlyDictionary<long, int> attackIndex, RulesConstants c, Stats s)
    {
        var u = state.Unit(db.Unit);
        if (u is null) { s.Skip("dmg_buffs", "юнита нет в состоянии боя"); return; }
        if (AttackOf(u, attackIndex, db.Seq, "dmg_buffs", s) is not { } baseAttack) return;
        if (AltAttackHides(baseAttack))
        { s.Skip("dmg_buffs", "у атаки есть запасная (ALT_ATTACK) — чей урон вернул движок, событие не показывает"); return; }

        var attack = Effective(u, baseAttack);
        var (boost, lower) = DamageStatuses(u, snapshots, db.Seq, c);
        int ours = Formulas.DamageWithBuffs(attack, (int)db.DmgMax, boost, lower, false, new ScriptedRng([]), c, db.Easy);
        // Человек на лёгкой (задача 16 Ф7) — отдельным ключом: разрез, по которому видно, мерилось ли это вообще.
        s.Count(db.Easy ? "dmg_buffs_easy" : ReferenceEquals(baseAttack, u.Attack) ? "dmg_buffs" : "dmg_buffs_attack2", ours == db.Ret);
        if (ours != db.Ret) s.Mismatch($"dmg_buffs {db.Unit} (атака {attackIndex.GetValueOrDefault(db.Seq, 1)}): журнал {db.Ret}, наше {ours}");
    }

    private static void CheckMiss(MissEvent m, Dictionary<(long tid, long n), RngEvent> rolls, Stats s)
    {
        if (m.Power == NotComputed) { s.Skip("miss", "notComputed"); return; }
        if (m.RngAfter - m.RngBefore != 2) { s.Skip("miss", $"бросков {m.RngAfter - m.RngBefore}, ожидали 2"); return; }
        if (!Roll(rolls, m.Tid, m.RngBefore + 1, 100, s, out int r1) ||
            !Roll(rolls, m.Tid, m.RngBefore + 2, 100, s, out int r2)) { s.Skip("miss", "броска нет в журнале"); return; }

        bool ours = Formulas.ShouldMiss((int)m.Power, r1, r2);
        s.Count("miss", ours == m.Miss);
        if ((r1 + r2) / 2 == m.Power) s.Count("miss_boundary", ours == m.Miss);   // строгость сравнения
        if (ours != m.Miss) s.Mismatch($"miss power={m.Power} r={r1},{r2}: журнал {m.Miss}, наше {ours}");
    }

    private static void CheckDamage(DamageEvent d, BattleState state, IReadOnlyList<CorpusEvent> events,
                                    IReadOnlyList<CorpusEvent> snapshots,
                                    Dictionary<(long tid, long n), RngEvent> rolls, RulesConstants c, Stats s)
    {
        // Drain/DrainOverflow: выходы NULL, урон — ret (DamageEvent.Damage), крита нет.
        bool drain = !d.Crit && d.Dmg == NotComputed && d.CritDmg == NotComputed;
        if (!drain && (d.Dmg == NotComputed || d.CritDmg == NotComputed)) { s.Skip("dmg", "notComputed"); return; }
        if (!drain)
        {
            s.Count("dmg_ret", d.Ret == d.Dmg + d.CritDmg);
            if (d.Ret != d.Dmg + d.CritDmg) s.Mismatch($"dmg ret {d.Ret} != dmg {d.Dmg} + crit_dmg {d.CritDmg}");
            s.Count("crit_dmg_zero", d.CritDmg == 0);   // наблюдение: крит в корпусе ни разу не сработал
        }

        long until = events.FirstOrDefault(x => x.Seq > d.Seq && x.Tid == d.Tid &&
                         x is TurnEvent or RoundEvent or BattleTurnEvent or BattleEndEvent or DamageEvent)?.Seq
                     ?? long.MaxValue;
        var dwbs = events.OfType<DamageBuffsEvent>()
                       .Where(x => x.Random && x.Tid == d.Tid && x.Seq > d.Seq && x.Seq < until).ToList();
        var arms = events.OfType<ArmorEvent>().Where(x => x.Tid == d.Tid && x.Seq > d.Seq && x.Seq < until &&
                       string.Equals(x.Unit, d.Target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (dwbs.Count != 1 || arms.Count != 1)
        { s.Skip("dmg", $"вложенная пара неоднозначна: dmg_buffs={dwbs.Count}, armor={arms.Count}"); return; }
        var (dwb, arm) = (dwbs[0], arms[0]);
        if (arm.Ret == NotComputed) { s.Skip("dmg", "armor notComputed"); return; }

        var attacker = state.Unit(d.Attacker);
        if (attacker is null) { s.Skip("dmg", "атакующего нет в состоянии боя"); return; }
        long draws = d.RngAfter - d.RngBefore;
        if (draws == 3) { s.Skip("dmg", "крит-бросок, не поддержан"); return; }
        if (draws != 1) { s.Skip("dmg", $"бросков {draws}, ожидали 1"); return; }
        // Человек на лёгкой (задача 16 Ф7): диапазон добавки 2·BAT_DAMAGE — и разрез отдельными ключами.
        string easy = dwb.Easy ? "_easy" : "";
        if (!Roll(rolls, d.Tid, d.RngAfter, dwb.Easy ? 2 * c.BatDamage : c.BatDamage, s, out int roll, "rng_max" + easy))
        { s.Skip("dmg", "броска нет в журнале"); return; }

        // Удар предметом (сфера, талисман): attack_new называет реализацией экземпляр предмета, а не юнита —
        // атака чужая, и мерить её атакой героя нельзя (ai-q37, seq 313434: сфера G000AA9127, урон 105 против 35).
        bool byItem = events.OfType<AttackNewEvent>()
            .LastOrDefault(x => x.Tid == d.Tid && x.Seq < d.Seq &&
                                string.Equals(x.Attacker, d.Attacker, StringComparison.OrdinalIgnoreCase))
            ?.Impl is { Length: 10 } impl && impl.Substring(4, 2).Equals("IM", StringComparison.OrdinalIgnoreCase);
        if (byItem)
            s.Skip("dmg_buffs_random", "удар предметом (attack_new от экземпляра IM) — атака не юнита");
        else if (AltAttackHides(attacker.Attack))
            s.Skip("dmg_buffs_random", "у атаки есть запасная (ALT_ATTACK) — чей урон вернул движок, событие не показывает");
        else
        {
            var (boost, lower) = DamageStatuses(attacker, snapshots, dwb.Seq, c);
            int oursDwb = Formulas.DamageWithBuffs(Effective(attacker, attacker.Attack), (int)dwb.DmgMax,
                                                   boost, lower, true, new ScriptedRng([roll]), c, dwb.Easy);
            s.Count("dmg_buffs_random" + easy, oursDwb == dwb.Ret);
            if (oursDwb != dwb.Ret) s.Mismatch($"dmg_buffs(random) {dwb.Unit}: журнал {dwb.Ret}, наше {oursDwb}");
        }

        // ComputeDamage считает по ЖУРНАЛЬНЫМ dwb и armor, поэтому запасная атака ей не мешает.
        int ours = Formulas.ComputeDamage((int)dwb.Ret, (int)arm.Ret);
        s.Count(drain ? "dmg_drain" : "dmg", ours == d.Damage);
        if (ours != d.Damage) s.Mismatch($"dmg {d.Attacker}->{d.Target}: журнал {d.Damage}, наше {ours} (dwb={dwb.Ret}, armor={arm.Ret})");
        if (drain) return;

        // Крит (задача 15 Ф7): 5 % от журнального dwb, если класс Damage и CRIT_HIT или способность 17.
        // Ненулевой крит модели там, где журнал пишет 0, — расхождение, а не наблюдение.
        if (byItem) { s.Skip("crit_dmg", "удар предметом — атака не юнита"); return; }
        int oursCrit = Formulas.HasCriticalHit(Effective(attacker, attacker.Attack), attacker)
            ? Formulas.CriticalDamage((int)dwb.Ret) : 0;
        s.Count("crit_dmg", oursCrit == d.CritDmg);
        if (oursCrit != d.CritDmg) s.Mismatch($"crit_dmg {d.Attacker}->{d.Target}: журнал {d.CritDmg}, наше {oursCrit} (dwb={dwb.Ret})");
        if (arm.Ret > 0)   // альтернатива round-half-up: при броне 0 неотличима, считается только там, где может разойтись
            s.Count("dmg_round_alt", (int)Math.Round(dwb.Ret * (100 - arm.Ret) / 100.0, MidpointRounding.AwayFromZero) == d.Dmg);
    }

    /// <summary>Бросок по (поток, номер). Заодно сверяет диапазон: у пары промаха max == 100,
    /// у броска добавки к урону max == BAT_DAMAGE — иначе привязка нашлась не та.</summary>
    private static bool Roll(Dictionary<(long tid, long n), RngEvent> rolls, long tid, long n, int expectedMax,
                             Stats s, out int r, string key = "rng_max")
    {
        r = 0;
        if (!rolls.TryGetValue((tid, n), out var e)) return false;
        s.Count(key, e.Max == expectedMax);
        if (e.Max != expectedMax) s.Mismatch($"rng n={n}: max {e.Max}, ожидали {expectedMax}");
        r = (int)e.R;
        return true;
    }

    /// <summary>Проценты статусов урона на момент события — то, что <c>computeDamageWithBuffs</c>
    /// читает из <c>BattleMsgData</c> помимо самой атаки. Берутся из сырой маски <c>st</c>
    /// последнего снимка перед событием, а не выводятся из хода боя: кого именно бафнул класс 8,
    /// событие <c>attack_new</c> не называет (та же слепота, что у <c>TransformOther</c>), а
    /// маска называет прямо.
    /// <para>Складываются проценты ВСЕХ взведённых уровней — движок так и делает
    /// (<c>0x5d2e05</c>–<c>0x5d2f1d</c>: по независимому <c>if</c> на уровень).</para>
    /// <para>Снимка нет (событие до первого <c>battle_start</c> по seq) — статусов нет: до начала
    /// боя маска пуста, и это не догадка, а её значение в журнале.</para></summary>
    private static (int Boost, int Lower) DamageStatuses(BattleUnit unit, IReadOnlyList<CorpusEvent> snapshots,
                                                         long seq, RulesConstants c)
    {
        if (SnapshotOf(unit, snapshots, seq) is not { } st) return (0, 0);
        return (st.BoostDamageLevels.Sum(c.BoostDamagePercent), st.LowerDamageLevels.Sum(c.LowerDamagePercent));
    }

    /// <summary>Подставить в юнит его состояние (HP, shattered, fort, статус Defend) из последнего
    /// снимка боя перед событием: броня зависит от них на момент вызова.</summary>
    private static void ApplySnapshot(BattleUnit unit, IReadOnlyList<CorpusEvent> snapshots, long seq)
    {
        if (SnapshotOf(unit, snapshots, seq) is not { } last) return;
        unit.Hp = (int)last.Hp; unit.Xp = (int)last.Xp;
        unit.ShatteredArmor = (int)last.Shattered; unit.FortificationArmor = (int)last.Fort;
        unit.Defending = last.Defend;
    }

    /// <summary>Последний снимок этого юнита с seq не больше заданного; null — снимков ещё не было.</summary>
    private static UnitState? SnapshotOf(BattleUnit unit, IReadOnlyList<CorpusEvent> snapshots, long seq)
    {
        UnitState? last = null;
        foreach (var s in snapshots)
        {
            if (s.Seq > seq) break;
            var units = s switch { TurnEvent t => t.Units, RoundEvent r => r.Units, BattleStartEvent b => b.Units, _ => null };
            var found = units?.FirstOrDefault(u => string.Equals(u.Id, unit.Id, StringComparison.OrdinalIgnoreCase));
            if (found is not null) last = found;
        }
        return last;
    }

    /// <summary>Счётчики С1: сошлось/разошлось по функции плюс пропуски с разбивкой по причине.
    /// Пропуск без причины — дыра в измерении, поэтому причина обязательна в сигнатуре.</summary>
    private sealed class Stats
    {
        private readonly Dictionary<string, (int ok, int bad)> _counts = [];
        private readonly Dictionary<(string key, string reason), int> _skips = [];
        public List<string> Mismatches { get; } = [];

        public void Count(string key, bool ok)
        {
            var v = _counts.GetValueOrDefault(key);
            _counts[key] = ok ? (v.ok + 1, v.bad) : (v.ok, v.bad + 1);
        }

        public void Ensure(string key) => _counts.TryAdd(key, (0, 0));
        public void Skip(string key, string reason) => _skips[(key, reason)] = _skips.GetValueOrDefault((key, reason)) + 1;
        /// <summary>Бой, которому принадлежат следующие расхождения, — префикс строки.</summary>
        public string Where { get; set; } = "";
        public void Mismatch(string text) => Mismatches.Add(Where + text);
        public int Ok(string key) => _counts.GetValueOrDefault(key).ok;
        public int Sum(IEnumerable<string> keys) => keys.Sum(k => { var v = _counts.GetValueOrDefault(k); return v.ok + v.bad; });

        public IEnumerable<string> Lines()
        {
            foreach (var (k, v) in _counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                yield return $"{k,-18} сошлось={v.ok,-6} разошлось={v.bad}";
            foreach (var (k, n) in _skips.OrderBy(kv => kv.Key.key, StringComparer.Ordinal))
                yield return $"{k.key,-18} пропущено={n,-6} причина: {k.reason}";
        }
    }
}
