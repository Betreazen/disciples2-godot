using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Units;

namespace D2.Data.Battle;

/// <summary>Состояние боя из корпуса: battle_start даёт группы, HP и флаги, battle_units —
/// опознание юнита (тип из Gunits, уровень, HpMax). Без battle_units бой не грузится:
/// выдумывать тип по HP — ложные данные в измерении соответствия.</summary>
public static class CorpusBattleLoader
{
    /// <summary><paramref name="onAttackers"/>/<paramref name="onDefenders"/> — цепочка
    /// модификаторов, наложенных на группу ДО боя заклинанием с карты. Журнал её не пишет —
    /// событий заклинания в схеме нет вовсе, — поэтому источник у неё внешний
    /// (<c>route.json</c> прогона), и передаётся она явно, а не угадывается здесь.
    /// <para><paramref name="saves"/> — сохранения рядом с журналом (<see cref="SaveSnapshots"/>):
    /// оттуда берётся СВОЯ цепочка юнита — выбранные способности лидера и надетые предметы.
    /// Журнал не пишет и её, но источник тут объективный, а не маршрутный, поэтому цепочка не
    /// передаётся списком, а спрашивается по каждому юниту. Снимков нет — цепочка пустая, ровно как
    /// было до задачи 16; снимки есть, но юнит в них неоднозначен — бой не грузится, с причиной.
    /// <paramref name="turn"/> — ход, шедший на момент боя (метки <c>terrain_day</c>).</para>
    /// <para>Порядок слоёв: сперва своя цепочка юнита в порядке хранения <c>MidUnit.Modifiers</c>,
    /// поверх — заклинание, наложенное на группу перед боем. Иначе и быть не может: предмет надет
    /// раньше, чем брошено заклинание того же хода, а <c>EffectiveStats</c> считает каждый слой от
    /// предыдущего.</para>
    /// <para><paramref name="spells"/> — справочники <c>Gspells</c>/<c>GItem</c>: с ними в цепочку юнита входят модификаторы
    /// заклинаний и зелий карты, наложенных до боя по журналу (<c>spell_cast</c>, <c>potion_use</c>) и ещё не снятых (<c>spell_expire</c>) — см.
    /// <see cref="SaveSnapshots.TryChain"/>. Без него — как раньше: только то, что видят сохранения окна.</para>
    /// <para><paramref name="orders"/> — смены приказов (<c>ai_order</c>) этой загрузки карты до боя, по порядку: приказы стороны
    /// поверх сохранения (<see cref="SideOf"/>).</para></summary>
    public static bool TryLoad(D2.Data.Corpus.Battle battle, GameRules rules,
                              out BattleState state, out string reason,
                              IReadOnlyList<Modifier>? onAttackers = null,
                              IReadOnlyList<Modifier>? onDefenders = null,
                              SaveSnapshots? saves = null, int turn = 0,
                              IReadOnlyDictionary<string, D2.Rules.Magic.Item>? items = null,
                              MapEffectTables? spells = null, IReadOnlyList<AiOrderEvent>? orders = null)
    {
        state = null!;
        saves = saves?.At(battle.Start.Seq);             // снимки только своей загрузки карты
        var identities = battle.Units;
        if (identities is null) { reason = "нет события battle_units (журнал схемы v1 или бой оборван)"; return false; }

        var byId = identities.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var groupIndex = GroupOrder(battle);
        var units = new List<BattleUnit>();
        foreach (var snapshot in battle.Start.Units)
        {
            if (!byId.TryGetValue(snapshot.Id, out var identity))
            { reason = $"юнит {snapshot.Id} отсутствует в battle_units"; return false; }
            if (identity.Type is null)
            { reason = $"юнит {snapshot.Id}: тип не опознан зондом (скрытый/дуэль), type=null"; return false; }
            // ImplOf, а не Units[...]: прокачанного юнита движок кладёт в battle_units порождённым
            // типом G000UG****, которого в Gunits нет (CUnitGenerator, см. UnitGenerator).
            if (rules.ImplOf(identity.Type) is not { } impl)
            { reason = $"тип {identity.Type} не найден ни в Gunits, ни как порождённый прокачкой"; return false; }
            var type = impl.Type;
            if (impl.Attack is not { } attack)
            { reason = $"у типа {identity.Type} нет атаки {type.AttackId} в Gattacks"; return false; }
            bool attacker = (snapshot.Flags & 0b1000) != 0;   // UnitFlags: бит 3
            if (!Chain(saves, turn, snapshot.Id, identity.Level, rules, battle.Start.Seq, spells, out var own, out reason))
                return false;
            units.Add(new BattleUnit
            {
                Id = snapshot.Id, Type = type, Attack = attack, Attack2 = impl.Attack2,
                Modifiers = [.. own, .. (attacker ? onAttackers : onDefenders) ?? []],
                Attacker = attacker,
                Slot = (int)(snapshot.Flags & 0b111),        // UnitFlags: биты 0-2
                HpMax = (int)(identity.HpMax ?? type.HitPoints),
                Hp = (int)snapshot.Hp, Xp = (int)snapshot.Xp,
                ShatteredArmor = (int)snapshot.Shattered, FortificationArmor = (int)snapshot.Fort,
                Hidden = (snapshot.St >> Experience.HiddenStatusBit & 1) != 0,
                GroupIndex = groupIndex.GetValueOrDefault(snapshot.Id, -1),
                // Призванный (бит 27): бой, снятый с середины, несёт призванных в первом снимке; ИИ их обходит (AiTargets) —
                // 20260909-arena1 [124967]: без признака у модели нет ничьей rng(2) между S143UN001a и S143UN001e.
                Summoned = (snapshot.St >> SummonedStatusBit & 1) != 0,
            });
        }

        // «Человек на лёгкой» берётся из флага easy событий dmg_buffs: это входной аргумент
        // computeDamageWithBuffs, который движок сам вычислил по игроку и сложности (0x65b790–0x65b88b),
        // а не результат формулы. Событий нет — сторона считается обычной.
        var easy = battle.Events.OfType<DamageBuffsEvent>().Where(e => e.Easy)
                         .Select(e => e.Unit).ToHashSet(StringComparer.OrdinalIgnoreCase);
        state = new BattleState
        {
            Units = units, AttackerGroup = battle.Start.AttackerGroup,
            DefenderGroup = battle.Start.DefenderGroup, Round = (int)battle.Start.Round,
            // Справочники нужны только Summon (кого призвать) и DrainLevel (тип предыдущего
            // уровня); остальному бою они безразличны.
            Rules = rules,
            AttackersHumanOnEasy = units.Any(u => u.Attacker && easy.Contains(u.Id)),
            DefendersHumanOnEasy = units.Any(u => !u.Attacker && easy.Contains(u.Id)),
            AttackersPowerShift = PowerShift(battle, saves, turn, battle.Start.AttackerGroup, units, attackers: true),
            DefendersPowerShift = PowerShift(battle, saves, turn, battle.Start.DefenderGroup, units, attackers: false),
            DefendersCanRetreat = DefendersCanRetreat(battle.Start),
            AttackerSide = WithOrders(SideOf(battle.Start.AttackerGroup, saves, turn,
                                             saves?.AttackerMovement(battle.Start.AttackerGroup, battle.Start.AttackerPlayer,
                                                                     battle.Start.Tid, battle.Start.Seq)),
                                      battle.Start.AttackerGroup, orders, battle.Start.Seq),
            DefenderSide = WithOrders(SideOf(battle.Start.DefenderGroup, saves, turn, movement: null,
                                             fresh: saves?.FreshStackOf(turn, battle.Start.DefenderGroup, battle.Start.DefenderPlayer)),
                                      battle.Start.DefenderGroup, orders, battle.Start.Seq),
        };
        EquipLeaders(state, battle, saves, turn, items);
        reason = "";
        return true;
    }

    /// <summary><c>BattleStatus::Summon</c> — бит 27 (<c>LifeAttacks.Summon</c>).</summary>
    private const int SummonedStatusBit = 27;

    /// <summary>Порядок юнитов в группе (<c>CMidUnitGroup</c>) — первый <c>ai_strength_units</c> боя по каждой группе (схема 7.2:
    /// движок обходит группу по индексу, <c>0x5ddacd</c>/<c>0x5ddae5</c>); у него свой порядок, не порядок <c>battle_start</c>. Нужен
    /// раздаче излишка <c>DrainOverflow</c> (<c>0x65fbb8</c>, неделимый остаток — первым по группе): 20261016-port-synth [527152]
    /// seq 527538 — группа <c>[000f, 0010, 0011, 0012, 0013, 0042]</c>, излишек 35 на двоих — 18 у <c>000f</c>, 17 у <c>0013</c>.
    /// Событий нет (старые схемы) — пусто, порядок состояния боя.</summary>
    private static Dictionary<string, int> GroupOrder(D2.Data.Corpus.Battle battle)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in battle.Events.OfType<AiStrengthUnitsEvent>())
        {
            if (e.Stack is not { } stack || !seen.Add(stack)) continue;
            for (int i = 0; i < e.Units.Count; i++)
                if (e.Units[i].Id is { } id) order.TryAdd(id, i);
        }
        return order;
    }

    /// <summary>Боевые предметы лидеров (<see cref="BattleUnit.BattleItems"/>) из сохранения окна хода — только когда
    /// передан справочник <paramref name="items"/>: иначе поведение прежнее, слоты пусты. Неизвестный тип предмета
    /// пропускается. Содержимое слотов берётся из <c>item_consume</c> самого боя (<c>0x629824</c>, оба боевых слота
    /// перед <c>battle_end</c>; в бою слоты не меняются), а сохранение — только когда таких событий нет: предмет,
    /// надетый или выпитый после записи, оно не видит (passive-003 [16848240]: сохранение хода 7 — 16798695, зелье
    /// лечения S143IM0039 выпито на карте в 16799186, в слот 3 надето зелье воскрешения S143IM0044 в 16799207).</summary>
    private static void EquipLeaders(BattleState state, D2.Data.Corpus.Battle battle, SaveSnapshots? saves, int turn,
                                     IReadOnlyDictionary<string, D2.Rules.Magic.Item>? items)
    {
        if (items is null || saves is null || saves.Count == 0) return;
        foreach (var group in new[] { state.AttackerGroup, state.DefenderGroup })
        {
            if (saves.StackOf(turn, group) is not { } st || state.Unit(st.LeaderId) is not { } leader) continue;
            var slots = battle.Events.OfType<ItemConsumeEvent>()
                              .Where(e => string.Equals(e.Group, group, StringComparison.OrdinalIgnoreCase) && e.Slot is 2 or 3)
                              .OrderBy(e => e.Slot).Select(e => e.ItemType is null or "null" ? "" : e.ItemType).ToList();
            foreach (var type in slots.Count == 2 ? slots : st.BattleItems)
                if (items.TryGetValue(type, out var item)) leader.BattleItems.Add(item);
        }
    }

    /// <summary>Сдвиг точности стороны по сложности (<see cref="Formulas.AiPowerShift"/>). Два честных источника по
    /// порядку:
    /// <list type="number">
    /// <item>аргумент <c>easy</c> движковых вызовов <c>computeDamageWithBuffs</c> с <c>random = true</c> — движок сам
    /// вычислил «человек и лёгкая» (<c>0x65b790</c>–<c>0x65b88b</c>, ранг 1). Есть в бою <c>easy = true</c> — партия на
    /// лёгкой; сторона, у юнита которой такой вызов был с <c>easy = false</c>, — ИИ (−15);</item>
    /// <item>иначе — сохранения окна хода (<see cref="SaveSnapshots.SideOf"/>) по id группы и юнитам стороны: все
    /// опознанные обязаны дать одну сторону;</item>
    /// <item>ни группы, ни юнитов в окне нет (отряд нанят после последнего снимка и после боя карту перезагрузили) — по
    /// игроку стороны из <c>battle_start</c> (<c>attacker_player</c>/<c>defender_player</c> — владелец группы, его пишет
    /// движок) и его записи в снимках окна (<see cref="SaveSnapshots.PlayerSide"/>). 20261014-replay ai-q57, бои seq
    /// 2407700 (очень трудная) и 2507110 (трудная): лидер S135UN0146 нанят ИИ S135PL0002 на ходу 2 и сразу напал на
    /// нейтралов, отряда S135KC0030 нет ни в одном снимке своей загрузки — точность считалась без сдвига (журнал 90 и
    /// 85, наше 80).</item>
    /// </list>
    /// Ничего не опознано или снимки расходятся (в манифесте бывают копии другой карты с тем же префиксом id) — 0, как
    /// до переноса сложности. <c>ponytail:</c> бой без сохранений и без урона ИИ на лёгкой сдвига не получает.</summary>
    private static int PowerShift(D2.Data.Corpus.Battle battle, SaveSnapshots? saves, int turn, string groupId,
                                  IReadOnlyList<BattleUnit> units, bool attackers)
    {
        var side = units.Where(u => u.Attacker == attackers).Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var random = battle.Events.OfType<DamageBuffsEvent>().Where(e => e.Random).ToList();
        if (random.Any(e => e.Easy) && random.Any(e => !e.Easy && side.Contains(e.Unit)))
            return Formulas.AiPowerShift(D2.Rules.Map.Difficulty.Easy);
        if (saves is null || saves.Count == 0) return 0;
        var known = side.Prepend(groupId)
                        .Select(id => saves.SideOf(turn, id)).OfType<SavedSide>().Distinct().ToList();
        if (known.Count == 0 && saves.PlayerSide(turn, attackers ? battle.Start.AttackerPlayer : battle.Start.DefenderPlayer) is { } owner)
            known.Add(owner);
        return known.Count == 1 && !known[0].Human
            ? Formulas.AiPowerShift((D2.Rules.Map.Difficulty)known[0].Difficulty)
            : 0;
    }

    /// <summary>Может ли защищающаяся сторона бежать (<see cref="BattleState.DefendersCanRetreat"/>).
    /// Правило движка — ранг 1 (<c>0x5f51bf</c>): крепость и руины нельзя, отряд в городе нельзя.
    /// <para>Сборка из журнала: тип группы — символы 4–5 id (<c>FT</c> крепость, <c>RU</c> руины,
    /// <c>KC</c> отряд; ранг 1 по формату <c>CMidgardID</c>). <c>insideId</c> отряда журнал не пишет,
    /// и «отряд в городе» опознаётся по ненулевой <c>fortificationArmor</c> хоть у одного защитника —
    /// <b>ранг 3</b>: броня укрепления у города любого уровня больше нуля (<c>PROT_1..5</c> 10…30,
    /// столица 50), а вне города её нет. Потолок: город с нулевой защитой (мод) дал бы «можно».</para></summary>
    public static bool DefendersCanRetreat(BattleStartEvent start)
    {
        string type = start.DefenderGroup.Length >= 6 ? start.DefenderGroup.Substring(4, 2) : "";
        if (type is "FT" or "RU") return false;
        return !start.Units.Any(u => (u.Flags & 0b1000) == 0 && u.Fort > 0);
    }

    /// <summary>Сведения стороны для бегства ИИ (<see cref="BattleSide"/>). Группа — отряд, если тип id <c>KC</c>.
    /// Приказы, игрок и раса — из сохранения окна хода (<see cref="SaveSnapshots.StackOf"/>); остаток хода
    /// <paramref name="movement"/> — у нападающего после списания за атаку (<see cref="SaveSnapshots.AttackerMovement"/>),
    /// иначе из сохранения. Остаток защитника политика ИИ не читает. Нет отряда в сохранениях — сторона неизвестна.
    /// <para>Корпус 17.09: остаток последнего <c>stack_move</c> (без списания) давал бегство 17 из 34 — все 17 промахов
    /// были прикрытием защитника при остатке нападающего 1…33, и у 15 из них после списания половины запаса дня
    /// остаётся 0; ложных прикрытий при остатке 14…50 списание не создаёт (после него 1…30). Оставшиеся два промаха —
    /// сторона без сохранений (<c>Known = false</c>).</para>
    /// <para>Защитник, нанятый после снимка (<paramref name="fresh"/>, <see cref="SaveSnapshots.FreshStackOf"/>), берётся
    /// с приказом, который движок ставит новому отряду: <c>passive-001 [393173]</c> — отряд <c>S073KC0077</c> нанят в
    /// ход 4 и в тот же ход погиб, ни в одном снимке его нет, а его лидер-лекарь, оставшись без бойцов, бежит целиком.
    /// Нападающему это не даётся: его приказ ИИ (Assist запрещает бегство) планировщик ставит сам.</para>
    /// <para>ponytail: нейтральная раса опознаётся по id <c>G000RR0004</c> (единственная <c>RACE_TYPE 4</c> в
    /// <c>Grace.dbf</c> ванили); мод с другой нейтральной расой потребует чтения <c>Grace</c>.</para></summary>
    private static BattleSide SideOf(string groupId, SaveSnapshots? saves, int turn, int? movement, SavedStack? fresh = null)
    {
        bool isStack = groupId.Length >= 6 && groupId.Substring(4, 2).Equals("KC", StringComparison.OrdinalIgnoreCase);
        if (saves is null || saves.Count == 0 || (saves.StackOf(turn, groupId) ?? (isStack ? fresh : null)) is not { } st)
            return new BattleSide { IsStack = isStack };
        return new BattleSide
        {
            Known = true, IsStack = isStack, IsHuman = st.Human,
            PlayableRace = !st.RaceId.EndsWith("RR0004", StringComparison.OrdinalIgnoreCase),
            Order = (StackOrder)st.Order, OrderTarget = st.OrderTarget, AiOrder = (StackOrder)st.AiOrder,
            Movement = movement ?? st.Move,
        };
    }

    /// <summary>Приказы стороны в миг боя: сохранение окна хода, поверх — смены приказов журнала до боя (<c>ai_order</c>, визитёр
    /// <c>0x5e811a</c>: флаг 0 — основной приказ <c>+0xa8</c> и его цель <c>+0xac</c>, флаг 1 — приказ ИИ <c>+0xb8</c>; после вызова
    /// журнал пишет оба). Событие сценария меняет приказ посреди дня, а сохранение — с утра: 20260919-human [15601485] — отряд
    /// <c>S073KC0098</c> получил «берсерк» (seq 15533326, <c>0x4353e7</c>), и его лидер <c>S073UN0254</c> при силе 0,22 не бежит
    /// (<c>0x5d2779</c>: приказ не Normal/Roam/AttackStack — бежать нельзя), а бьёт (реестр И37).</summary>
    private static BattleSide WithOrders(BattleSide side, string groupId, IReadOnlyList<AiOrderEvent>? orders, long before)
    {
        if (!side.Known || orders is null) return side;
        foreach (var o in orders)
        {
            // −1 — зонд не прочитал отряд (схема): такая строка приказа не меняет.
            if (o.Seq >= before || !o.Ret || !o.Apply || o.OrderAfter < 0 || o.AiOrderAfter < 0
                || !string.Equals(o.Stack, groupId, StringComparison.OrdinalIgnoreCase)) continue;
            side = side with
            {
                Order = (StackOrder)o.OrderAfter, AiOrder = (StackOrder)o.AiOrderAfter,
                OrderTarget = o.Flag ? side.OrderTarget : o.Target.Equals("G000000000", StringComparison.OrdinalIgnoreCase) ? "" : o.Target,
            };
        }
        return side;
    }

    /// <summary>Своя цепочка юнита из сохранения. Незнакомый <c>MODIF_ID</c> — отказ с причиной, а
    /// не молчаливый пропуск строки: цепочка, из которой выпало звено, даёт заниженный урон и
    /// выглядит как ошибка формулы.</summary>
    private static bool Chain(SaveSnapshots? saves, int turn, string unitId, long? level, GameRules rules,
                              long battleSeq, MapEffectTables? spells,
                              out IReadOnlyList<Modifier> chain, out string reason)
    {
        chain = [];
        if (saves is { Orphan: true })
        { reason = "загрузка карты без своих сохранений — цепочку модификаторов взять неоткуда"; return false; }
        if (saves is null || saves.Count == 0) { reason = ""; return true; }
        if (!saves.TryChain(turn, unitId, level, rules.LeaderUpgradeModifiers, out var ids, out reason, battleSeq, spells))
            return false;
        if (ids.Count == 0) { reason = ""; return true; }
        if (rules.Modifiers is null)
        { reason = $"цепочка юнита {unitId} есть в сохранении, а GameRules.Modifiers не заполнен"; return false; }

        var made = new List<Modifier>(ids.Count);
        foreach (var id in ids)
        {
            if (!rules.Modifiers.TryGetValue(id, out var m))
            { reason = $"модификатор {id} юнита {unitId} из сохранения не найден в Gmodif.dbf"; return false; }
            made.Add(m);
        }
        chain = made;
        reason = "";
        return true;
    }
}
