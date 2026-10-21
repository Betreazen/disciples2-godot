using D2.Rules.Map;

namespace D2.Data.Corpus;

/// <summary>
/// One "ev" line of a probe journal (<c>tools/d2probe</c>). Field names follow the JSON keys
/// written by <c>probehooks.cpp</c>/<c>probe.cpp</c> — see <c>docs/corpus-schema.md</c> for the
/// full table. Two fields are common to every line: <see cref="Seq"/> (assigned before the
/// original engine call, so file order is not sorted by it — nested calls write their line
/// first) and <see cref="Tid"/> (the calling thread id).
/// </summary>
public abstract record CorpusEvent(long Seq, long Tid);

/// <summary>One occupied slot of <c>BattleMsgData::unitsInfo</c>. Schema v2: <see cref="Type"/>/
/// <see cref="Level"/>/<see cref="HpMax"/> are dead weight here — no event populates them any more
/// (in v1 only <see cref="BattleStartEvent"/> did; <c>objectMap</c> is not reachable from the hook
/// that now writes <c>battle_start</c>). Unit identity lives in <see cref="BattleUnitsEvent"/> /
/// <see cref="UnitIdentity"/> instead. Kept nullable rather than removed so old parsing code needs
/// no change — they will just always deserialize as null. <see cref="St"/>/<see cref="St2"/> are the
/// raw <c>unitStatuses</c>/<c>unitStatuses2</c> bitmasks as <c>long</c> — safe for status bits up
/// to 62; if the engine ever defines status bit 63, this needs <c>ulong</c> instead.</summary>
public sealed record UnitState(
    string Id, long Hp, long Xp, long St, long St2, long Flags, long Shattered, long Fort,
    string? Type, long? Level, long? HpMax)
{
    /// <summary><c>BattleStatus::Defend</c> = bit 31 of <see cref="St"/> (battlemsgdata.h): the unit
    /// used defend and its own next <c>beforeBattleTurn</c> has not cleared it yet.</summary>
    public bool Defend => (St & (1L << 31)) != 0;
    /// <summary><c>BattleStatus::Dead</c> = bit 1.</summary>
    public bool Dead => (St & (1L << 1)) != 0;
    /// <summary><c>BattleStatus::Paralyze</c> (bit 2) or <c>Petrify</c> (bit 3): the unit skips its turn.</summary>
    public bool Paralyzed => (St & (0b11L << 2)) != 0;
    /// <summary><c>BattleStatus::Retreated</c> (bit 28) or <c>Retreat</c> (bit 29) — Fear.</summary>
    public bool Retreating => (St & (0b11L << 28)) != 0;

    /// <summary>Взведённые уровни <c>BattleStatus::BoostDamageLvl1..4</c> — биты 5..8. Список, а не
    /// один уровень, потому что движок складывает проценты ВСЕХ взведённых:
    /// <c>computeDamageWithBuffs</c> (GOG <c>0x5d2e05</c>–<c>0x5d2ec1</c>) — четыре независимых
    /// <c>if (getUnitStatus(…, 5|6|7|8))</c>, и у каждого свой
    /// <c>add esi, Percent::of(base, BATBOOSTDn)</c>. В ванили одновременно взведён один (класс
    /// <c>BoostDamage</c> кладёт свой уровень поверх), но «брать старший» — правило, которого в
    /// движке нет.</summary>
    public IEnumerable<int> BoostDamageLevels => SetLevels(5, 4);

    /// <summary><c>BattleStatus::LowerDamageLvl1..2</c> — биты 10..11, те же независимые <c>if</c>,
    /// только с <c>sub esi, …</c> (<c>0x5d2ec1</c>–<c>0x5d2f1d</c>).</summary>
    public IEnumerable<int> LowerDamageLevels => SetLevels(10, 2);

    private IEnumerable<int> SetLevels(int firstBit, int count)
    {
        for (int i = 0; i < count; i++)
            if ((St & (1L << (firstBit + i))) != 0) yield return i + 1;
    }
}

/// <summary>One unit's identity, as carried by <see cref="BattleUnitsEvent"/> — <see cref="Type"/>/
/// <see cref="Level"/>/<see cref="HpMax"/> are only present for a unit the probe could resolve
/// against the scene's object map (same rule <see cref="BattleStartEvent"/> used in schema v1).</summary>
public sealed record UnitIdentity(string Id, string? Type, long? Level, long? HpMax);

/// <summary>One entry of a <see cref="RoundEvent"/>'s <c>order</c> list (<c>turnsOrder</c>).</summary>
public sealed record OrderEntry(string Id, long Attacks);

public sealed record StartEvent(long Seq, long Tid, long ExeSize, string Version, string Probe, bool Autobattle)
    : CorpusEvent(Seq, Tid)
{
    /// <summary>Схема журнала (<c>"7.5"</c>); null — у журналов, где поля нет (до 7.3 оно писалось не всегда).</summary>
    public string? Schema { get; init; }

    /// <summary>Схема не ниже <paramref name="major"/>.<paramref name="minor"/> (нет поля — нет).</summary>
    public bool SchemaAtLeast(int major, int minor) =>
        Schema is { } s && s.Split('.') is [var a, var b, ..]
        && int.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out int ma)
        && int.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out int mi)
        && (ma > major || ma == major && mi >= minor);
}

/// <summary>Covers both "rng" (engine generator) and "rng_std" (CRT rand) — same fields, separate
/// counters distinguished by <see cref="Std"/>. <see cref="RngIndex"/> (schema v2.1) is the R250
/// index right after the original call — the state the next engine draw starts from. Only ever
/// present on "rng" (never "rng_std"), and only on a Gog build — see docs/corpus-schema.md and
/// docs/re-rng.md in disciples-modding.</summary>
public sealed record RngEvent(long Seq, long Tid, long N, long Max, long R, bool Std, long? RngIndex = null)
    : CorpusEvent(Seq, Tid);

public sealed record MissEvent(long Seq, long Tid, long Power, bool Miss, long RngBefore, long RngAfter)
    : CorpusEvent(Seq, Tid);

/// <summary><see cref="Crit"/> is <c>computeCriticalHit</c>'s **input argument** to
/// <c>computeDamage</c>, not the result of a crit computation. It is <c>true</c> from
/// <c>Damage::onHit</c> (GOG <c>0x65b577 push 1</c>) and <c>false</c> from <c>Drain</c>/<c>DrainOverflow</c>,
/// which also pass NULL outputs — hence <c>dmg = crit_dmg = −1</c> (11 such events in the corpus,
/// crit.md 11.09.2026). Field kept named "crit" for format compatibility with the schema doc.</summary>
public sealed record DamageEvent(
    long Seq, long Tid, string Attacker, string Target, bool Crit, long Ret, long Dmg, long CritDmg,
    long RngBefore, long RngAfter) : CorpusEvent(Seq, Tid)
{
    /// <summary>Урон без крита. У Drain/DrainOverflow выходы NULL (<c>dmg = −1</c>), и урон — возврат
    /// <c>computeDamage</c> (<c>0x65B9C1</c>: ret = урон + крит, а крит при <c>computeCriticalHit = 0</c> не
    /// считается; вызовы <c>0x65BE16</c>, <c>0x65F8DE</c>). Корпус: у 104 таких ударов ret = dwb − dwb·armor/100
    /// по вложенным <c>dmg_buffs</c>/<c>armor</c>, 104/0.</summary>
    public long Damage => Dmg == -1 ? Ret : Dmg;
}

public sealed record DamageMaxEvent(long Seq, long Tid, string Unit, long Ret) : CorpusEvent(Seq, Tid);

public sealed record DamageBuffsEvent(long Seq, long Tid, string Unit, long DmgMax, bool Random, bool Easy, long Ret)
    : CorpusEvent(Seq, Tid);

public sealed record ArmorEvent(long Seq, long Tid, string Unit, long Ret) : CorpusEvent(Seq, Tid);

public sealed record PowerEvent(long Seq, long Tid, string Unit, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>computePlayerDailyIncome</c>. <see cref="Gold"/> и пять ман — **доход** за день;
/// <see cref="Purse"/> (схема v3) — казна игрока на момент вызова, <see cref="Turn"/> — день
/// (<c>CScenarioInfo::currentTurn</c>, <c>-1</c>, если объект недоступен). Оба поля необязательны:
/// в журналах v2.1 их нет вовсе, и там они читаются как <c>null</c>. <see cref="T"/> (схема v5) —
/// миллисекунды от старта прогона; в журналах до v5 его нет.</summary>
public sealed record IncomeEvent(
    long Seq, long Tid, string Player, long Gold, long Infernal, long Life, long Death, long Runic, long Grove,
    long? Turn = null, Bank? Purse = null, long? T = null)
    : CorpusEvent(Seq, Tid);

/// <summary><see cref="RngIndex"/> (schema v2.1, Gog only) is the R250 index after the original
/// <c>beforeBattleRound</c> call — see <see cref="RngEvent.RngIndex"/>.</summary>
public sealed record RoundEvent(
    long Seq, long Tid, long Round, IReadOnlyList<OrderEntry> Order, IReadOnlyList<UnitState> Units,
    long? RngIndex = null) : CorpusEvent(Seq, Tid);

/// <summary>Covers both "turn_before" and "turn_after"; <see cref="Next"/> is only present on
/// the "after" line. <see cref="Order"/> is the same <c>turnsOrder</c> snapshot as
/// <see cref="RoundEvent.Order"/> — empty where the engine has not built the queue yet (see
/// docs/corpus-schema.md).</summary>
public sealed record TurnEvent(
    long Seq, long Tid, bool After, string Unit, string? Next, long Round, IReadOnlyList<OrderEntry> Order,
    IReadOnlyList<UnitState> Units) : CorpusEvent(Seq, Tid);

/// <summary>Schema v2: written in <c>beforeBattleRound</c>, before <c>objectMap</c> is reachable —
/// <see cref="Units"/> never carries identity (<see cref="UnitState.Type"/>/<see cref="UnitState.Level"/>/
/// <see cref="UnitState.HpMax"/> are always null here). See <see cref="BattleUnitsEvent"/> for identity.
/// <see cref="RngIndex"/> (schema v2.1, Gog only) is the R250 index at the moment this line is
/// written, before the original call — see <see cref="RngEvent.RngIndex"/>.</summary>
public sealed record BattleStartEvent(
    long Seq, long Tid, string AttackerGroup, string DefenderGroup, string AttackerPlayer, string DefenderPlayer,
    long Round, IReadOnlyList<UnitState> Units, long? RngIndex = null) : CorpusEvent(Seq, Tid);

/// <summary>Schema v2 only: written on the first <c>battleTurn</c> call for a battle (after
/// <see cref="BattleStartEvent"/>, before the first <c>dmg</c>/<c>miss</c>) — the one point where the
/// probe can resolve each unit against the scene's <c>objectMap</c>. <see cref="Order"/> is the same
/// <c>turnsOrder</c> snapshot as <see cref="RoundEvent.Order"/> (see docs/corpus-schema.md).</summary>
public sealed record BattleUnitsEvent(long Seq, long Tid, IReadOnlyList<OrderEntry> Order, IReadOnlyList<UnitIdentity> Units)
    : CorpusEvent(Seq, Tid);

public sealed record BattleTurnEvent(long Seq, long Tid, long Round) : CorpusEvent(Seq, Tid);

/// <summary><see cref="RngIndex"/> (schema v2.1, Gog only) is the R250 index at the same point
/// this line itself is taken (after the original call decided the battle is over) — see
/// <see cref="RngEvent.RngIndex"/>. <see cref="T"/> — миллисекунды от старта прогона (схема v5).</summary>
public sealed record BattleEndEvent(
    long Seq, long Tid, string Winner, long Round, IReadOnlyList<UnitState> Units, long? RngIndex = null,
    long? T = null)
    : CorpusEvent(Seq, Tid);

public sealed record AutobattleForcedEvent(long Seq, long Tid) : CorpusEvent(Seq, Tid);

// Adventure map, schema v3 (docs/corpus-schema.md, «События карты»). Five of the nine are written
// BY KEY — one line per new feature combination, with the real number of calls only in
// CountsEvent. Counting calls by counting lines of a keyed event is wrong: see CountsEvent.

/// <summary><c>computeMovementCost</c> — стоимость шага в клетку. Пишется **по ключу**
/// <c>x:y:флаги:ret</c>. Грунт клетки функции не передают (движок читает его из <c>CMidgardMap</c>) —
/// он присоединяется с нашей стороны из <c>.sg</c>. <see cref="Ret"/> — стоимость в очках хода.</summary>
public sealed record MoveCostEvent(
    long Seq, long Tid, long X, long Y, string Stack, bool LeaderAlive, bool Plains, bool Forest,
    bool Water, bool WaterOnly, bool ForbidWater, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>stackCanMoveToPosition</c> — проходимость клетки для отряда. По ключу <c>x:y:ret</c>.</summary>
public sealed record CanMoveEvent(long Seq, long Tid, long X, long Y, string Stack, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>showMovementPath</c>, каждый вызов. <see cref="EndX"/>/<see cref="EndY"/> — цель искателя пути (<c>+0x14</c> показанного,
/// <c>0x4ccbee</c>): клетка под курсором, вход объекта в ней или — у пустой клетки над чужим отрядом — клетка этого отряда (прицел
/// <c>0x4d04c3</c>); не конец списка. <see cref="LastX"/>/<see cref="LastY"/> — последняя достижимая за этот ход,
/// <see cref="Len"/> — длина списка пути (<c>-1</c>, если списка нет).</summary>
public sealed record PathEvent(
    long Seq, long Tid, string Stack, long Len, long LastX, long LastY, long EndX, long EndY)
    : CorpusEvent(Seq, Tid);

/// <summary><c>CMidStackApi::get().setPosition</c>, каждый вызов. <see cref="MoveLeft"/> — очки хода
/// **после** перемещения: стоимость шага = предыдущий <see cref="MoveLeft"/> минус текущий.
/// <para><b>Исключение — ПЕРВЫЙ за день вызов у отряда</b> (задача 11 Ф7): в нём стоит полный запас
/// дня, то есть очки ДО списания шага. Измерено: 99 первых ходов дня из 99 совпали с
/// <c>Movement.StartMovement</c> (счётчик <c>leader_move_max</c>), а гипотеза «плюс цена шага»
/// разошлась на всех 90. Независимо — на журналах без казны первые ходы дня дают ровно базовый
/// <c>MOVE</c> (48 из 48 в <c>20260907-c4</c>, 54 из 60 в <c>20260908-f6</c>, остальные 6 у героя,
/// выросшего в уровне), и ни одного «база минус цена шага» среди них нет.</para></summary>
public sealed record StackMoveEvent(
    long Seq, long Tid, string Stack, long X, long Y, bool CheckArea, long MoveLeft, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>changeUnitXpCheckUpgrade</c>, каждый вызов. <see cref="Amount"/> — начисляемый опыт,
/// <see cref="Ret"/> — сработал ли апгрейд. <see cref="T"/> — миллисекунды от старта прогона (схема v5).
/// <see cref="RetAddr"/> (схема 6.0) — адрес возврата: <c>0x431d07</c> — чит сложности ИИ
/// <c>AiCheatUnitXP</c>, <c>0x665788</c> — бой; в журналах до 6.0 null.
/// Схема 6.0, партия 2 (в более ранних журналах null): <see cref="XpBefore"/>/<see cref="XpAfter"/> —
/// <c>CMidUnit::currentXp</c> до и после (после повышения — опыт нового типа); <see cref="Hp"/>,
/// <see cref="Level"/>, <see cref="XpNext"/>, <see cref="Impl"/>, <see cref="Leader"/> — юнит <b>до</b>
/// вызова; <see cref="MaxLeader"/> — <c>MAX_LEADER</c> сценария. −1 — не прочиталось.</summary>
public sealed record XpEvent(long Seq, long Tid, string Player, string Unit, long Amount, bool Ret, long? T = null,
    string? RetAddr = null, long? XpBefore = null, long? XpAfter = null, long? Hp = null, long? Level = null,
    long? XpNext = null, string? Impl = null, bool? Leader = null, long? MaxLeader = null)
    : CorpusEvent(Seq, Tid);

/// <summary><c>getBuildingStatus</c>, по ключу <c>player:building:ignore_cost:ret</c>.
/// <see cref="Ret"/> — <c>BuildingStatus</c> (0 можно строить, 3 не хватает казны, 8 уже построено;
/// полная таблица — docs/corpus-schema.md).</summary>
public sealed record BuildStatusEvent(
    long Seq, long Tid, string Player, string Building, bool IgnoreCost, long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>addPlayerUnitsToHireList</c>, по ключу <c>player:a3:count:ret</c>. <see cref="A3"/> —
/// объект, для которого спрашивают список (имя из тулсета, назначение не выяснено);
/// <see cref="Count"/> — длина списка найма **после** вызова.</summary>
public sealed record HireListEvent(long Seq, long Tid, string Player, string A3, long Count, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>updateEncLayoutSite</c>, по ключу = id сайта. <see cref="Category"/> —
/// <c>LSiteCategory::id</c> (лавка, башня мага, лагерь наёмников, наставник).</summary>
public sealed record SiteEvent(long Seq, long Tid, string Site, long Category) : CorpusEvent(Seq, Tid);

/// <summary>
/// <c>terrain_day</c> — своя земля игрока на смену дня: сколько клеток и какие именно.
/// <see cref="Cells"/> уже разобран из строки RLE <c>"5:5-9;6:5-9,12"</c>, где ЧИСЛО ДО ДВОЕТОЧИЯ —
/// строка карты (это <c>y</c>), а список после него — колонки (<c>x</c>); в паре координат здесь
/// порядок обычный, <c>(X, Y)</c>. <see cref="Owned"/> — счётчик самого зонда, он сверяется с
/// длиной <see cref="Cells"/> и расходиться не должен.
/// </summary>
public sealed record TerrainDayEvent(
    long Seq, long Tid, string Player, long Turn, long Race, long Terrain, long Size, long Owned,
    IReadOnlySet<(int X, int Y)> Cells) : CorpusEvent(Seq, Tid)
{
    /// <summary>Разбирает RLE строки <c>cells</c>: точки с запятой разделяют строки карты,
    /// запятые — отрезки колонок, дефис — отрезок включительно с обоих концов.</summary>
    /// <exception cref="FormatException">Строка не той формы или в ней нечисловая величина —
    /// молча пропускать испорченный отрезок нельзя: пропавшая клетка выглядела бы как правило.</exception>
    public static IReadOnlySet<(int X, int Y)> ParseCells(string rle)
    {
        var cells = new HashSet<(int X, int Y)>();
        foreach (var row in rle.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = row.IndexOf(':');
            if (colon <= 0)
                throw new FormatException($"нет ':' в строке клеток \"{row}\" (все клетки: \"{rle}\")");
            if (!int.TryParse(row.AsSpan(0, colon), out int y))
                throw new FormatException($"нечисловая строка карты в \"{row}\"");
            foreach (var span in row[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int dash = span.IndexOf('-');
                if (dash < 0)
                {
                    if (!int.TryParse(span, out int x))
                        throw new FormatException($"нечисловая колонка \"{span}\" в строке \"{row}\"");
                    cells.Add((x, y));
                    continue;
                }
                if (!int.TryParse(span.AsSpan(0, dash), out int from) ||
                    !int.TryParse(span.AsSpan(dash + 1), out int to))
                    throw new FormatException($"нечисловой отрезок \"{span}\" в строке \"{row}\"");
                if (to < from)
                    throw new FormatException($"отрезок \"{span}\" идёт назад (строка \"{row}\")");
                for (int x = from; x <= to; x++) cells.Add((x, y));
            }
        }
        return cells;
    }
}

/// <summary>Счётчики зонда **без бюджета и без потолка**: сколько раз функция была вызвана, а не
/// сколько строк попало в журнал. Пишется на смену дня и при выгрузке DLL. Для событий «по ключу»
/// число вызовов есть только здесь — считать их по строкам нельзя.
/// <para><see cref="KeysDropped"/> — новых ключей отброшено потолком за весь прогон (&gt; 0 — выборка сочетаний неполна).
/// Схема 7.4: <see cref="KeysDroppedBy"/> — то же по видам строк (только виды с отказами), <see cref="KeysResets"/> — сколько раз
/// ключи сброшены при входе в партию (строка <c>game_phase</c>); у журналов до 7.4 — пусто и 0.</para></summary>
public sealed record CountsEvent(long Seq, long Tid, IReadOnlyDictionary<string, long> Calls,
    long KeysDropped = 0, IReadOnlyDictionary<string, long>? KeysDroppedBy = null, long KeysResets = 0)
    : CorpusEvent(Seq, Tid);

/// <summary><c>immune</c> (схема 4) — <c>isUnitImmuneToAttack</c> (GOG <c>0x65a528</c>), по ключу
/// <c>unit:attack:class:source:always_only:ret</c>. <see cref="Class"/> — <c>LattC</c> самой атаки,
/// <see cref="Source"/> — <c>LattS</c>; <see cref="AlwaysOnly"/> — входной <c>checkAlwaysImmuneOnly</c>
/// (спрашивают только про <c>Always</c>), <see cref="Ret"/> — иммунен ли юнит.</summary>
public sealed record ImmuneEvent(
    long Seq, long Tid, string Unit, string Attack, long Class, long Source, bool AlwaysOnly, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>attack_new</c> (схема 4) — <c>createBatAttack</c> (GOG <c>0x644b3e</c>), каждый вызов:
/// одно боевое действие — одна строка, включая действия без урона, у которых своей строки
/// <c>dmg</c> нет вовсе. <see cref="Impl"/> — юнит или предмет, которому принадлежит атака;
/// <see cref="DispClass"/> — класс, переданный аргументом (у альтернативной атаки отличается от
/// <see cref="Class"/>).</summary>
public sealed record AttackNewEvent(
    long Seq, long Tid, string Attacker, string Impl, long Number, string Attack, long Class, long Source,
    long DispClass, string TargetGroup) : CorpusEvent(Seq, Tid);

/// <summary><c>attack_get</c> (схема 4) — <c>getAttackById</c> (GOG <c>0x644626</c>), по ключу
/// <c>impl:number:alt:attack:class:source</c>: во что разрешается атака номер <see cref="Number"/>
/// у импла <see cref="Impl"/>. <see cref="Alt"/> — входной <c>checkAltAttack</c>.</summary>
public sealed record AttackGetEvent(
    long Seq, long Tid, string Impl, long Number, bool Alt, string Attack, long Class, long Source)
    : CorpusEvent(Seq, Tid);

// Казна, объекты и города, схема v5 (docs/corpus-schema.md, «События казны, объектов и города»).
// Все пишутся на каждый вызов. T — миллисекунды от старта прогона: по нему событие привязывается к
// сохранению драйвера (mtime файла от manifest.started_at). Причинные события берут seq ДО
// оригинала, а казну двигает оригинал изнутри, поэтому их строка bank стоит в файле РАНЬШЕ них с
// seq на единицу больше (у build — сразу перед ним с seq на единицу меньше).

/// <summary><c>CVisitorChangePlayerBank::apply</c> (Gog <c>0x60be8d</c>) — единственная точка, где
/// движок меняет казну игрока. Визитор ПРИСВАИВАЕТ: величина операции — только
/// <see cref="Change"/> (<c>after − before</c>); поле <c>delta</c> журнала при <see cref="Mode"/> = 0
/// равно <see cref="After"/>, а при <c>mode = 1</c> пишет второй кошелёк игрока и основную казну не
/// двигает (0 из 41 строки прогонов 3–4), поэтому здесь не читается.</summary>
public sealed record BankEvent(long Seq, long Tid, long T, string Player, long Mode, Bank Before, Bank After, bool Ret)
    : CorpusEvent(Seq, Tid)
{
    public Bank Change => After - Before;
}

/// <summary><c>CMidServerLogic::onSiteBuyItem</c> (<c>0x5d76e2</c>) — покупка у торговца. У всех пяти
/// <c>site_*</c> <see cref="Site"/> — посещаемый объект (<c>SI</c>): склад, ростер и заклинания — у него
/// в сохранении (<see cref="D2.Rules.Map.MapSite"/>).</summary>
public sealed record SiteBuyItemEvent(long Seq, long Tid, long T, string Site, string Stack, string ItemType, long Slot,
    bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>onSiteSellItem</c> (<c>0x5d77f5</c>) — продажа; <see cref="Item"/> — экземпляр
/// (<c>IM</c>), его тип лежит в сохранении.</summary>
public sealed record SiteSellItemEvent(long Seq, long Tid, long T, string Site, string Stack, string Item, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>onSiteBuySpell</c> (<c>0x5d7952</c>) — покупка заклинания в башне мага.</summary>
public sealed record SiteBuySpellEvent(long Seq, long Tid, long T, string Site, string Player, string Spell, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>onSiteBuyUnit</c> (<c>0x5d7aa1</c>) — наём у наёмника.</summary>
public sealed record SiteBuyUnitEvent(long Seq, long Tid, long T, string Site, string Stack, string UnitType, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>onSiteTrainUnit</c> (<c>0x5d7c6b</c>) — обучение. Сколько опыта выдано, журнал не
/// пишет (хук <c>xp</c> обучение не видит, план Ф7 §12.4).</summary>
public sealed record SiteTrainEvent(long Seq, long Tid, long T, string Site, string Unit, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>healUnit</c> (<c>0x5d724e</c>). Сколько HP вылечено, журнал не пишет.</summary>
public sealed record HealEvent(long Seq, long Tid, long T, string Unit, string Player, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>reviveUnit</c> (<c>0x5d733f</c>). HP и опыт юнита до и после — схема 6.0, партия 2;
/// в более ранних журналах null, −1 — юнит не прочитался.</summary>
public sealed record ReviveEvent(long Seq, long Tid, long T, string Unit, string Player, bool Ret,
    long? HpBefore = null, long? HpAfter = null, long? XpBefore = null, long? XpAfter = null)
    : CorpusEvent(Seq, Tid);

/// <summary><c>growCity</c> (<c>0x5da01e</c>). Размер до роста — последний <see cref="FortDayEvent"/>.</summary>
public sealed record CityGrowEvent(long Seq, long Tid, long T, string Player, string Fort, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>CVisitorPlayerAddCapitalBuilding::apply</c> (<c>0x60c496</c>) — факт постройки.
/// <see cref="Id2"/> — номер хода постройки в виде id (<c>G000000000</c> — на загрузке карты),
/// <see cref="Flag"/> = 1 у начальных построек ИИ.</summary>
public sealed record BuildEvent(long Seq, long Tid, long T, string Player, string Building, string Id2, long Flag,
    bool Ret) : CorpusEvent(Seq, Tid);

/// <summary>Укрепление игрока из <see cref="FortDayEvent"/>; <see cref="Size"/> — <c>SIZE</c> деревни,
/// у столицы −1. <see cref="Stack"/> — отряд-визитёр внутри, <see cref="Tier"/> — <c>getTier</c>.
/// <see cref="Capacity"/> — слот <c>+0x20</c>, в тулсете <c>getMaxUnitsInGarrison</c>, но это
/// <b>радиус обзора <c>GcityInf.SCOUT</c></b>, а не вместимость гарнизона (0x5fe1d8, у столицы 6;
/// имя поля оставлено ради совместимости). <see cref="GrowthT"/>, <see cref="RiotT"/>,
/// <see cref="Regen"/> — поля <c>CMidVillage</c>, у столицы −1. <see cref="ProtectB"/> (схема 6.0) —
/// <c>CMidVillage +0xa0</c>, <c>PROTECT_B</c>; у столицы и в журналах до 6.0 null
/// (corpus-schema.md «Укрепления»).</summary>
public sealed record FortState(string Id, string Owner, long Size, string Subrace, string Stack, long Tier,
    long Capacity, long GrowthT, long RiotT, long Regen, string? ProtectB = null);

/// <summary><c>fort_day</c> — укрепления игрока на смену дня (из хука <c>income</c>).</summary>
public sealed record FortDayEvent(long Seq, long Tid, long T, string Player, long Turn, IReadOnlyList<FortState> Forts)
    : CorpusEvent(Seq, Tid);

/// <summary>Any "ev" the reader does not recognise. Carries the raw line so nothing is lost
/// silently — the corpus is read before every field the probe can ever write is modeled here.</summary>
public sealed record UnknownEvent(long Seq, long Tid, string Ev, string RawJson) : CorpusEvent(Seq, Tid);
