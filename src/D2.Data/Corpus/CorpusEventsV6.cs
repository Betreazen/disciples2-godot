using System.Text;
using D2.Rules.Map;

namespace D2.Data.Corpus;

// События схемы 6.0, партия 1 (docs/corpus-schema.md, «Схема 6.0»): сохранение, укрепления, земля,
// ход, проверки интерфейса, воры и союзы. Код зонда — probemap.cpp, probecheck.cpp, probenoble.cpp.
// Все несут T — миллисекунды от старта прогона. «Проверки» (garrison_max, scout, rod_check,
// hire_check, hire_leader_check, merc_check, sell_check, grow_check) пишутся ПО КЛЮЧУ: число строк —
// это число сочетаний, а вызовы — только в CountsEvent.

/// <summary><c>save</c> — <c>CMidServerLogicCore::saveGame</c> (<c>0x429f3f</c>), единственный писатель
/// файла сохранения. Всё с <c>seq</c> меньше <see cref="CorpusEvent.Seq"/> в файле уже есть, всё с
/// <c>seq</c> больше <see cref="SeqEnd"/> — ещё нет. <see cref="Name"/> — как в журнале: зонд с
/// исправления 15.09.2026 пишет UTF-8, <c>856bf05</c> писал байты cp1251 символами U+0080…U+00FF;
/// человеческое имя в обоих случаях — <see cref="NameText"/>.</summary>
public sealed record SaveEvent(long Seq, long Tid, long T, string Name, bool Temp, long Turn, bool Ok, long SeqEnd)
    : CorpusEvent(Seq, Tid)
{
    /// <summary>Имя по-человечески. Символ выше U+00FF — уже UTF-8, отдаётся как есть; иначе байты
    /// собираются обратно и декодируются как cp1251. Неоднозначности нет: у cp1251 байты
    /// 0x80…0xFF, которые совпадают с U+0080…U+00FF (<c>« » ° ©</c>…), декодируются в себя же.</summary>
    public string NameText
    {
        get
        {
            if (Name.All(c => c < 0x80) || Name.Any(c => c > 0xFF))
                return Name;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(Encoding.Latin1.GetBytes(Name));
        }
    }
}

/// <summary><c>fort_capture</c> — <c>CEffectCapture::apply</c> (<c>0x433e2e</c>), попытка входа в чужую
/// деревню. Захват — <see cref="Captured"/>; иначе причина — визитёр, живой гарнизон или
/// призванный/вор. Разовый обход земли за захват лежит между <c>Seq</c> и <see cref="SeqEnd"/>.</summary>
public sealed record FortCaptureEvent(
    long Seq, long Tid, long T, string Stack, string Fort, string Owner, string Subrace, bool SkipLeaderCheck,
    string OwnerBefore, string SubraceBefore, string VisitorBefore, string OwnerAfter, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid)
{
    public bool Captured => OwnerAfter != OwnerBefore;
}

/// <summary><c>land_grow</c> — обход роста земли (<c>0x5e0551</c>): центр, квота, террейн источника и
/// взятые клетки <b>в порядке взятия</b>. <see cref="Count"/> — длина списка, сверяется с
/// <see cref="Cells"/>.</summary>
public sealed record LandGrowEvent(
    long Seq, long Tid, long T, long X, long Y, long Quota, long Terrain, long Count,
    IReadOnlyList<(int X, int Y)> Cells, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid)
{
    /// <summary>Разбирает <c>"x:y;x:y"</c>. Испорченная пара — <see cref="FormatException"/>: пропавшая
    /// клетка выглядела бы как правило.</summary>
    public static IReadOnlyList<(int X, int Y)> ParseCells(string text)
    {
        var cells = new List<(int X, int Y)>();
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = pair.IndexOf(':');
            if (colon <= 0 || !int.TryParse(pair.AsSpan(0, colon), out int x) ||
                !int.TryParse(pair.AsSpan(colon + 1), out int y))
                throw new FormatException($"клетка \"{pair}\" не вида x:y (все клетки: \"{text}\")");
            cells.Add((x, y));
        }
        return cells;
    }
}

/// <summary><c>land_reject</c> — отказ приёма клетки обходом (<c>0x5e07b2</c>); строка только на отказ,
/// причина — из сохранения. <see cref="Cx"/>/<see cref="Cy"/> — центр обхода.</summary>
public sealed record LandRejectEvent(long Seq, long Tid, long T, long X, long Y, long Cx, long Cy, long Quota)
    : CorpusEvent(Seq, Tid);

/// <summary><c>site_visit</c> — вход в сайт (<c>0x5d7664</c>). <see cref="MoveLeft"/> — очки хода до вызова.</summary>
public sealed record SiteVisitEvent(long Seq, long Tid, long T, string Site, string Stack, string Owner, long MoveLeft,
    bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>spell_expire</c> — <c>CEffectRemoveSpell::apply</c> (<c>0x43b9ef</c>): снятие записей
/// <see cref="Player"/> с <c>TURN_STOP ≤ Turn</c>.</summary>
public sealed record SpellExpireEvent(long Seq, long Tid, long T, string Player, long Turn, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>item_create</c> — создание предмета (<c>0x5e732f</c>). Роли аргументов не читаны, поэтому
/// <see cref="Ids"/> — ид-поля, названные по типу id (<c>item_type</c>, <c>stack</c>, <c>idN</c>…).
/// <see cref="Cause"/> — по адресу возврата.</summary>
public sealed record ItemCreateEvent(
    long Seq, long Tid, long T, IReadOnlyDictionary<string, string> Ids, bool Apply, string RetAddr, string Cause,
    bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>ap_effect</c> — конструктор <c>CEffectChangeAP</c> (<c>0x43588c</c>). <see cref="Effect"/> —
/// адрес объекта, пара с <see cref="ApApplyEvent"/>; <see cref="Delta"/> null — объект не прочитался.</summary>
public sealed record ApEffectEvent(long Seq, long Tid, long T, string Effect, string Stack, long? Delta, string RetAddr,
    string Cause) : CorpusEvent(Seq, Tid);

/// <summary><c>ap_apply</c> — <c>CEffectChangeAP::apply</c> (<c>0x4358d4</c>): очки хода отряда до и после.</summary>
public sealed record ApApplyEvent(long Seq, long Tid, long T, string Effect, string Stack, long? Delta, long MoveBefore,
    long MoveAfter, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>hp_change</c> — <c>CEffectChangeHP::apply</c> (<c>0x436299</c>): <see cref="Amount"/> > 0 —
/// урон; HP юнита до и после.</summary>
public sealed record HpChangeEvent(long Seq, long Tid, long T, string Unit, long? Amount, long Flag, long HpBefore,
    long HpAfter, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>garrison_max</c> — предел группы укрепления (<c>0x6144ce</c>), по ключу. <see cref="Allowed"/>
/// −1 — законное «предел — уровень», null — поле не прочиталось.</summary>
public sealed record GarrisonMaxEvent(long Seq, long Tid, long T, string Group, string Fort, long? Allowed, string RetAddr,
    long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>scout</c> — <c>CUmStack::getScout</c> (<c>0x5a38e8</c>), по ключу. База = <c>Ret − Bonus</c>,
/// если не <see cref="Clamped"/>.</summary>
public sealed record ScoutEvent(long Seq, long Tid, long T, long Bonus, bool Clamped, string RetAddr, long Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>rod_check</c> — можно ли поставить жезл (<c>0x5d9e59</c>), по ключу; <see cref="Ret"/> 0 — можно.</summary>
public sealed record RodCheckEvent(long Seq, long Tid, long T, string Stack, long X, long Y, bool Flag, string RetAddr,
    long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>hire_check</c> — можно ли нанять юнита (<c>0x5deea7</c>), по ключу; код отказа в <see cref="Ret"/>.</summary>
public sealed record HireCheckEvent(long Seq, long Tid, long T, string Player, string UnitType, string Group,
    long Position, string RetAddr, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>hire_leader_check</c> — можно ли нанять лидера (<c>0x5de921</c>), по ключу.</summary>
public sealed record HireLeaderCheckEvent(long Seq, long Tid, long T, string Player, string UnitType, string RetAddr,
    long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>merc_check</c> — можно ли нанять у наёмника (<c>0x5e00c6</c>), по ключу.</summary>
public sealed record MercCheckEvent(long Seq, long Tid, long T, string Site, string Stack, string UnitType,
    string RetAddr, bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>sell_check</c> — можно ли продать предмет торговцу (<c>0x5dfe1a</c>), по ключу.</summary>
public sealed record SellCheckEvent(long Seq, long Tid, long T, string Site, string Stack, string Item, string? ItemType,
    string RetAddr, bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>grow_check</c> — можно ли растить деревню (<c>0x5dc62a</c>), по ключу с днём.</summary>
public sealed record GrowCheckEvent(long Seq, long Tid, long T, string Player, string Fort, long Turn, string RetAddr,
    long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>noble_action</c> — <c>CEffectNobleAction::apply</c> (<c>0x43a335</c>): действие вора с тем,
/// кто действовал (<see cref="Stack"/>, <see cref="Player"/>). <see cref="Action"/> — те же числа, что
/// <see cref="ThiefRollEvent.Action"/>; <see cref="Value"/> — сумма кражи или процент отравления.</summary>
public sealed record NobleActionEvent(long Seq, long Tid, long T, string Player, string Stack, string Target, long Action,
    bool Success, long Value, string Id, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>thief_cancel</c> — отмена действия вора сервером (<c>0x443506</c>).</summary>
public sealed record ThiefCancelEvent(long Seq, long Tid, long T, string Player, string Stack, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>noble_choose</c> — <c>CEffectNobleChoose::apply</c> (<c>0x43a5a7</c>): действия, доступные
/// вору на подходе. <see cref="Count"/> — длина множества у движка, сверяется с <see cref="Actions"/>.</summary>
public sealed record NobleChooseEvent(long Seq, long Tid, long T, string Player, string Stack, string Target, long Count,
    IReadOnlyList<long> Actions, bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>alliance_propose</c> — <c>CEffectProposeAlliance::apply</c> (<c>0x43b5c8</c>): <see cref="A"/>
/// предлагает, <see cref="B"/> — адресат. <c>Human*</c> null — игрока не прочитать.</summary>
public sealed record AllianceProposeEvent(long Seq, long Tid, long T, string A, string B, bool? HumanA, bool? HumanB,
    long RaceA, long RaceB, RelationWord? Relation, bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>alliance_change</c> — <c>CEffectChangeAlliance::apply</c> (<c>0x43574e</c>): союз, разрыв или
/// отказ; пара до и после.</summary>
public sealed record AllianceChangeEvent(long Seq, long Tid, long T, string A, string B, bool Accept, bool Silent,
    bool FlagE, bool? HumanA, bool? HumanB, RelationWord? Before, RelationWord? After, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

// Схема 6.0, партия 2: лидеры, предметы, бой (probeleader.cpp; equip_check и wand_check — probecheck.cpp,
// по ключу).

/// <summary><c>skill_offer</c> — список навыков лидера (<c>0x5d8b84</c>), по ключу <c>leader:level:ids:ret</c>.
/// <see cref="Ids"/> — <c>MODIF_ID</c> в порядке массива лорда; вес <c>PRIORITY</c> — <c>GleaUpg.dbf</c> по
/// паре (<see cref="Lord"/>, id).</summary>
public sealed record SkillOfferEvent(long Seq, long Tid, long T, string Leader, string Player, string Lord, long Level,
    IReadOnlyList<string> Ids, bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>skill_pick</c> — <c>visitorLeaderUpgrade</c> (<c>0x5e84c2</c>) с <c>apply = 1</c>: выбранный
/// навык. <see cref="RetAddr"/> <c>0x5d6e6e</c> — выбор (человек и ИИ), <c>0x42f4da</c> — бесплатный.</summary>
public sealed record SkillPickEvent(long Seq, long Tid, long T, string Stack, string Modif, bool Free, bool Apply,
    string Leader, long UpgBefore, long UpgAfter, long RngBefore, long RngAfter, string RetAddr, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>upg_count</c> — <c>visitorLeaderUpgradeCount</c> (<c>0x5e8513</c>): сколько выборов навыка
/// доступно отряду, <c>CMidStack::upgCount</c> до и после.</summary>
public sealed record UpgCountEvent(long Seq, long Tid, long T, string Stack, long Count, long Before, long After,
    bool Apply, string RetAddr, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>equip_check</c> — можно ли надеть (<c>0x5df6b7</c>), по ключу; <see cref="Ret"/> — код отказа
/// (0 можно, 2…7 нет навыка категории, 8, 9, 0xB не даст эффекта).</summary>
public sealed record EquipCheckEvent(long Seq, long Tid, long T, string Item, string? ItemType, string Stack, long Slot,
    bool CheckEffect, string RetAddr, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>wand_check</c> — можно ли применить посох или свиток (<c>0x5dbdd2</c>), по ключу; <see cref="Ret"/>
/// — код (0xE нет навыка, 9 нет маны, 0x11/0x12 исчерпано на день). <see cref="DailyUses"/> — лимит лорда
/// владельца цели: Маг 2, прочие 1, −1 — не прочиталось.</summary>
public sealed record WandCheckEvent(long Seq, long Tid, long T, string Item, string Target, long X, long Y, bool Flag,
    long DailyUses, string RetAddr, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>potion_use</c> — <c>applyPotion</c> (<c>0x5d70f9</c>): зелье в бою (<see cref="Destroy"/> false)
/// или на карте; HP цели до и после.</summary>
public sealed record PotionUseEvent(long Seq, long Tid, long T, string Player, string Target, string Group, string Item,
    string? ItemType, bool Destroy, bool InBattle, long HpBefore, long HpAfter, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>item_consume</c> — расход боевого слота в конце боя (<c>0x629824</c>), до <c>battle_end</c>.
/// Предмет израсходован, если <see cref="ItemAfter"/> ≠ <see cref="ItemBefore"/>. <see cref="ItemType"/> null —
/// слот пуст (<c>G000000000</c>) или группа не отряд.</summary>
public sealed record ItemConsumeEvent(long Seq, long Tid, long T, string Group, long Slot, string ItemBefore,
    string? ItemType, string ItemAfter, long BattleRound, long SeqEnd) : CorpusEvent(Seq, Tid)
{
    public bool Consumed => ItemBefore != ItemAfter;
}

/// <summary><c>talisman_charge</c> — списание заряда талисмана (<c>0x5e74bb</c>), только факт.</summary>
public sealed record TalismanChargeEvent(long Seq, long Tid, long T, string Item, bool Apply, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary>Получатель опыта группы: <see cref="Acc"/> — накопленное за бой, <see cref="Applied"/> — применённое.</summary>
public sealed record XpGroupUnit(string Id, long Acc, long Applied);

/// <summary><c>xp_group</c> — <c>CBatAttackGroupUpgrade::upgradeGroup</c> (<c>0x665217</c>): опыт группы в
/// конце боя. <see cref="WeaponMaster"/> — Инструктор у лидера (<c>0x665851</c>), null — хук не стоял.</summary>
public sealed record XpGroupEvent(long Seq, long Tid, long T, string Group, string Player, bool Attacker, bool Ai,
    bool? WeaponMaster, IReadOnlyList<XpGroupUnit> Units, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>trade_accept</c> — <c>CEffectAcceptTrade::apply</c> (<c>0x433805</c>): <see cref="A"/> платит
/// <see cref="Gold"/>, <see cref="B"/> получает; <see cref="Object"/> — предмет или заклинание.</summary>
public sealed record TradeAcceptEvent(long Seq, long Tid, long T, string A, string B, string Object, string Id2,
    long Gold, bool Accepted, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>attack_hit</c> (схема 6.1) — <c>IBatAttack::onHit</c> класса без урона (<c>probehit.cpp</c>): цель
/// действия и её снимок в <c>BattleMsgData</c> до и после. <see cref="DispClass"/> — класс, чей <c>onHit</c>
/// сработал (24 — обереги); <see cref="NewSeq"/> — строка <c>attack_new</c>, создавшая действие (−1 — не нашлась,
/// тогда <see cref="Attacker"/> и <see cref="Attack"/> — <c>"null"</c>). <see cref="ModsBefore"/>/<see cref="ModsAfter"/>
/// — <c>modifierIds</c> цели; null — цели нет в слотах (<see cref="Found"/> false). Вложенные строки — между
/// <c>Seq</c> и <see cref="SeqEnd"/>.</summary>
public sealed record AttackHitEvent(long Seq, long Tid, long T, long DispClass, long NewSeq, string Attacker,
    string Attack, string Target, bool Found, long HpBefore, long HpAfter, long StBefore, long StAfter,
    IReadOnlyList<string>? ModsBefore, IReadOnlyList<string>? ModsAfter, long SeqEnd) : CorpusEvent(Seq, Tid)
{
    /// <summary>Модификаторы, легшие на цель этим действием (обереги у класса 24).</summary>
    public IReadOnlyList<string> ModsAdded =>
        ModsAfter is null ? [] : ModsAfter.Except(ModsBefore ?? [], StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary><c>rng_seed</c> (схема 6.2) — запись состояния генератора боя <c>setState</c> (<c>0x548473</c>,
/// <c>probestate.cpp</c>): пересев <c>reseed</c> (<c>0x5484bd</c>, <see cref="Via"/> <c>"reseed"</c>) или
/// загрузка сохранённого состояния (<c>"set_state"</c>). <see cref="N"/> — счётчик вызовов генератора на
/// момент записи (следующая строка <c>rng</c> будет с <c>n = N + 1</c>); <see cref="Arg"/> — аргумент-индекс
/// до <c>% 250</c> (у пересева — зерно, <c>timeGetTime</c>); <see cref="Table"/> — 250 слов таблицы
/// <c>0x7b4978</c> после записи, младшие 16 бит; <see cref="Wide"/> — сколько слов не влезло в 16 бит
/// (ожидается 0).</summary>
public sealed record RngSeedEvent(long Seq, long Tid, long T, long N, long Arg, long IndexBefore, long IndexAfter,
    string Via, string Cause, string RetAddr, long Wide, IReadOnlyList<ushort> Table) : CorpusEvent(Seq, Tid)
{
    public const int TableSize = 250;

    /// <summary>Строка из 1000 шестнадцатеричных знаков — 250 слов по 4 знака; иначе null.</summary>
    public static IReadOnlyList<ushort>? ParseTable(string hex)
    {
        if (hex.Length != TableSize * 4) return null;
        var words = new ushort[TableSize];
        for (int i = 0; i < TableSize; i++)
        {
            if (!ushort.TryParse(hex.AsSpan(i * 4, 4), System.Globalization.NumberStyles.AllowHexSpecifier,
                    null, out words[i]))
                return null;
        }
        return words;
    }
}

/// <summary><c>ap_restore</c> (схема 6.2) — каждое изменение запаса хода через <c>changeStackMove</c>
/// (<c>0x5e780d</c>, <c>probestate.cpp</c>): <c>movement −= amount</c> в <c>[0, MOVE лидера]</c>.
/// <see cref="Cause"/> <c>"turn_start"</c> — восстановление в начале хода владельца
/// (<c>CEffectRegenUnits</c> → <c>0x5d41d8</c>, <c>amount = move − max</c>); прочие причины — по адресу
/// возврата. <see cref="Max"/> — <c>0x5ed035</c> после вызова (MOVE лидера, 0 — лидера нет);
/// <see cref="Zero"/> — почему <see cref="MoveAfter"/> = 0: <c>max0_no_leader</c>, <c>max0</c>, <c>spent</c>,
/// <c>unknown</c>; null, если не ноль. −1 и <c>"null"</c> — отряд не прочитан.</summary>
public sealed record ApRestoreEvent(long Seq, long Tid, long T, string Stack, string Player, long Amount,
    long MoveBefore, long MoveAfter, long Max, string Leader, bool LeaderFound, bool? LeaderAlive, string? Zero,
    string Cause, string RetAddr, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

// Схема 6.3: перенос юнитов и отказы интерфейса (probeunit.cpp; wand_precheck — probecheck.cpp).

/// <summary><c>unit_transfer</c> (схема 6.3) — серверный обмен клеток <c>0x5d5642</c> (сообщение
/// <c>CStackSwapUnitMsg</c>, ИИ <c>StackReorder</c>) или увольнение <c>0x5d5773</c>
/// (<c>CStackDismissUnitMsg</c>). <see cref="Kind"/>: <c>swap</c> — обе клетки заняты, <c>move</c> — одна
/// пуста, <c>empty</c> — обе пусты, <c>dismiss</c> — увольнение (<see cref="To"/> null, <see cref="ToPos"/>
/// −1). Группы — id отряда, укрепления или руины; юниты — до (<see cref="Unit"/>, <see cref="Other"/>) и после
/// вызова (<see cref="FromAfter"/>, <see cref="ToAfter"/>), null — клетка пуста. <see cref="Cause"/>:
/// <c>swap_msg</c>, <c>ai_reorder</c>, <c>dismiss_msg</c>, <c>unknown</c>.</summary>
public sealed record UnitTransferEvent(long Seq, long Tid, long T, string Kind, bool SameGroup, string From,
    long FromPos, string? To, long ToPos, string? Unit, string? Other, string? FromAfter, string? ToAfter,
    string Cause, string RetAddr, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>swap_check</c> (схема 6.3) — проверка интерфейса перед переносом (<c>0x5df4fb</c>), по ключу;
/// <see cref="Ret"/>: 0 можно, 1 нет группы или пусто в исходной клетке, 2 страж в другую группу, 3 юнит
/// типа <c>0x78d0c0</c> в другую группу, 0xB — визитёр обмена отказал.</summary>
public sealed record SwapCheckEvent(long Seq, long Tid, long T, string From, long FromPos, string To, long ToPos,
    string? Unit, string? Other, string RetAddr, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>dismiss_check</c> (схема 6.3) — можно ли уволить юнита из клетки (<c>0x5df266</c>), по ключу;
/// <see cref="Ret"/>: 0 можно, 4 пусто, 5 лидер при других юнитах, 6 лидер вне города, 2 страж,
/// 1 удаление отказало.</summary>
public sealed record DismissCheckEvent(long Seq, long Tid, long T, string Group, long Position, string? Unit,
    string RetAddr, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>equip_drop</c> (схема 6.3) — статус сброса предмета в слот героя (<c>CDDEquipment</c>,
/// <c>0x504782</c>), по ключу; <see cref="Ret"/>: 0 принят, 1 отказ с текстом (следом <c>equip_check</c>
/// с кодом), 2 молча не принят (категория не подходит слоту — так отсекается свиток).</summary>
public sealed record EquipDropEvent(long Seq, long Tid, long T, string? Item, long Slot, string RetAddr, long Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ui_refusal</c> (схема 6.3) — интерфейс взял текст отказа (<c>getInterfaceText</c>
/// <c>0x5c7f06</c>, только тексты из перечня зонда), по ключу <c>text:ret_addr</c>. <see cref="What"/> —
/// короткое имя отказа (<c>no_space</c>, <c>leadership_big</c>, <c>wand_used_1</c>, <c>equip_9</c>, …),
/// <see cref="RetAddr"/> — место в обработчике.</summary>
public sealed record UiRefusalEvent(long Seq, long Tid, long T, string Text, string What, string RetAddr)
    : CorpusEvent(Seq, Tid);

/// <summary><c>wand_precheck</c> (схема 6.3) — калитки применяющего посох или свиток (<c>0x5dbbe8</c>), по
/// ключу; интерфейс зовёт её до наведения. <see cref="Ret"/>: 0, 1, 0xE лидер мёртв или нет навыка, 9 мана,
/// 0x11/0x12 исчерпано на день.</summary>
public sealed record WandPrecheckEvent(long Seq, long Tid, long T, string Item, string? ItemType, string Stack,
    long DailyUses, string RetAddr, long Ret) : CorpusEvent(Seq, Tid);

// Схема 6.4: ход ИИ на карте (probeai.cpp). Броски (roll) — значения generateRandomNumber из кольца зонда:
// null — бросков не было, −1 — бросок был, но уже вытеснен из кольца.

/// <summary><c>ai_break_check</c> (схема 6.4) — решение ИИ разорвать союз (<c>0x448897</c>), каждый вызов.
/// «Сильнее» (<see cref="Strong"/>) — у <see cref="Player"/> больше крепостей (<c>0x4489f6</c>);
/// <see cref="TEff"/> = D_BKA_TURN (<see cref="TBase"/>), у сильного вдвое меньше; <see cref="D"/> = день −
/// ход союза (биты 15–29 слова отношений); при <see cref="D"/> &gt; <see cref="TEff"/> шанс
/// <c>min(D − TEff, Cap)</c>, у сильного ×2. <see cref="Chance"/> зонд считает сам (null — броска по модели
/// нет), <see cref="Ret"/> — ответ движка: сверка <c>Ret == (Roll &lt; Chance)</c> проверяет модель.</summary>
public sealed record AiBreakCheckEvent(long Seq, long Tid, long T, string Player, string Other, long FortsPlayer,
    long FortsOther, bool Strong, long Turn, long? AllyTurn, bool? NoBreak, long? D, long TBase, long TEff, long Cap,
    long? Chance, long? Roll, long RngBefore, long RngAfter, string RetAddr, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ai_offer_check</c> (схема 6.4) — предложит ли ИИ союз (<c>0x448aad</c>), каждый вызов.
/// <see cref="Chance"/> — ответ <c>0x448ae2</c> (null — отношение не выше D_NEUTRAL, шанс не считался);
/// <see cref="TurnPlayer"/> — игрок, чей ход ИИ начался последним (аргументов «кто» у функции нет).</summary>
public sealed record AiOfferCheckEvent(long Seq, long Tid, long T, string? TurnPlayer, string? Other, long Rel,
    long Turn, long? Chance, long? Roll, long RngBefore, long RngAfter, string RetAddr, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ai_gift_check</c> (схема 6.4) — подарок ИИ (<c>0x448bda</c>), каждый вызов:
/// <see cref="Roll"/> &lt; <see cref="Threshold"/> (<c>rel·20/100</c>) — подарок, <see cref="KindRoll"/>
/// (<c>rng(3)</c>) — вид; <see cref="Action"/> — класс созданного действия (<c>item</c>, <c>spell</c>,
/// <c>gold</c>, <c>other</c>), null — действия нет.</summary>
public sealed record AiGiftCheckEvent(long Seq, long Tid, long T, string? Player, string? Other, long Rel,
    long Threshold, long? Roll, long? KindRoll, string? Action, long RngBefore, long RngAfter, string RetAddr,
    bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_answer</c> (схема 6.4) — ответ ИИ на предложение союза (<see cref="Kind"/> <c>alliance</c>:
/// <c>0x474b37</c>, шанс <c>0x474c21</c>) или сделки (<c>trade</c>: <c>0x474841</c>, шанс <c>0x4749c8</c>).
/// <see cref="Ret"/> — шанс (ответ функции шанса; null — не считался: сделка отклонена без броска).
/// <see cref="Roll"/> — бросок ответа, <see cref="Accept"/> = <c>Roll &lt; Ret</c>.</summary>
public sealed record AiAnswerEvent(long Seq, long Tid, long T, string Kind, string? Player, string? Proposer,
    long? Rel, long? Gold, long? Price, string? Object, long? Roll, bool? Accept, long RngBefore, long RngAfter,
    string RetAddr, long? Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_strategic_step</c> (схема 6.4) — стратегическое действие ИИ исполнено (запись памяти
/// <c>0x44c825</c>): цель <see cref="Id"/>, тип <see cref="Type"/> (0 HireLeader, 1 UpgradeLeader, 2 HireUnit,
/// 8 Diplomacy, …), день; <see cref="Pass"/> — номер прохода в ходе, <see cref="Step"/> — номер действия.</summary>
public sealed record AiStrategicStepEvent(long Seq, long Tid, long T, string? Player, string? Id, long Type,
    long Turn, long Pass, long Step, long RngBefore, long RngAfter, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_turn</c> (схема 6.4) — границы хода: <c>begin</c> (<c>0x4719f8</c>, аргументы
/// <see cref="Arg1"/>..<see cref="Arg3"/>) и <c>end</c> (<c>EndTurn 0x41e229</c>, <see cref="Cause"/>
/// <c>ai</c>/<c>human</c>; счётчики проходов стратегии с начала хода этого игрока).</summary>
public sealed record AiTurnEvent(long Seq, long Tid, long T, string Phase, string? Player, long Turn, string Cause,
    long? Arg1, long? Arg2, long? Arg3, long? PassBegins, long? Passes, long? Steps, long RngBefore, long RngAfter,
    string RetAddr, bool? Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_order</c> (схема 6.4) — смена приказа отряда (<c>0x5e811a</c>, визитёр), каждый вызов;
/// <see cref="Via"/> <c>cmd</c> — через команду <c>0x41f21d</c> (тогда <see cref="Cause"/> и
/// <see cref="RetAddr"/> — её вызывающего), <c>direct</c> — прямо.</summary>
public sealed record AiOrderEvent(long Seq, long Tid, long T, string Stack, long Order, string? OrderName,
    string Target, bool Flag, bool Apply, long OrderBefore, long OrderAfter, long AiOrderBefore, long AiOrderAfter,
    string Via, string Cause, long RngBefore, long RngAfter, string RetAddr, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ai_budget</c> (схема 6.4) — бюджет стратегического списка ИИ на день (<c>0x44661c</c>):
/// <see cref="Type"/> — тип списка, <see cref="Budget"/> — доля казны <see cref="Purse"/>,
/// <see cref="Flag"/> — вычесть резерв <see cref="SpellBank"/>; null — не прочитано.</summary>
public sealed record AiBudgetEvent(long Seq, long Tid, long T, string? Player, long Type, bool Flag, Bank? Budget,
    Bank? Purse, Bank? SpellBank, long RngBefore, long RngAfter, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_chat</c> (схема 6.4) — сообщение ИИ (<c>0x4733a4</c>): категория LAiMsg,
/// <see cref="Roll"/> — номер сообщения <c>rng(N)</c>.</summary>
public sealed record AiChatEvent(long Seq, long Tid, long T, string? From, string? To, long Category,
    string? CategoryName, long? Roll, long RngBefore, long RngAfter, string Cause, string RetAddr, bool Ret)
    : CorpusEvent(Seq, Tid);
