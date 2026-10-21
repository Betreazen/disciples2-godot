using D2.Rules.Map;

namespace D2.Data.Corpus;

// Схема 7.2 (probeai72.cpp): данные ИИ для остатка расхождений порта — кэш целей тактики с кошельком, сила группы по
// юнитам, дневное лечение, цель повышения, порядок раскрытия узлов A*, выдача id. Адреса и смысл полей —
// docs/corpus-schema.md, раздел «Схема 7.2». Пустой и недействительный id зонд пишет JSON null.

/// <summary><c>ai_place_cache</c> (7.2) — места в кэше целей тактики и кошелёк тактики (<c>CAiTactic +0x515c</c>).
/// <see cref="At"/> = <c>enter</c> — вход в тактику (<c>0x46586c</c>, после заполнения кошелька): <see cref="Owner"/> —
/// игрок тактики, <see cref="Bank"/> — его казна; поля кэша null. <see cref="At"/> = <c>cache</c> — пересборка кэша перед
/// оценкой отряда (<c>0x45e4c7</c>): <see cref="Stack"/>, <see cref="Pos"/> — отряд и его клетка (в крепости — вход),
/// <see cref="Places"/> — все площадки сбора <c>0x45efa4</c> с причиной отбора, <see cref="Picked"/> — вектор мест кэша
/// (<c>+0x2c</c>), <see cref="Excluded"/> — исключения кэша (<c>+0x6c</c>); <see cref="Purse"/> — кошелёк тактики, если
/// кэш принадлежит последней вошедшей тактике, иначе null.</summary>
public sealed record AiPlaceCacheEvent(long Seq, long Tid, long T, string At, string? Player, string? Owner, Bank? Purse,
    Bank? Bank, string? Stack, AiPoint? Pos, IReadOnlyList<AiCachePlace>? Places, IReadOnlyList<string?>? Picked,
    IReadOnlyList<string?>? Excluded, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary>Площадка в сборе кэша: <see cref="Class"/> — класс движка (<c>CMidSiteMerchant</c>, <c>CMidSiteMage</c>,
/// <c>CMidSiteTrainer</c>, <c>CMidSiteMercs</c>), <see cref="D"/> — ключ multimap (евклид от клетки отряда до входа),
/// <see cref="Pos"/> — вход, <see cref="Why"/> — <c>picked</c> (в векторе кэша), <c>radius</c> (выброшена радиусом у
/// чужих столиц <c>0x45f5f1</c>), <c>excluded</c> (в исключениях), <c>rank</c> (не вошла в три), <c>unknown</c>.</summary>
public sealed record AiCachePlace(string? Id, string? Class, double? D, AiPoint? Pos, string Why);

/// <summary><c>ai_fort_cache</c> (7.2) — крепости кэша целей (<c>0x45eb14</c>, вектор <c>+0x1c</c>), та же пересборка,
/// что у строки <c>ai_place_cache</c> с <c>seq</c> = <see cref="PlaceSeq"/>. <see cref="Forts"/> — каждая крепость карты
/// в порядке обхода с исходом фильтра <c>0x45ec77</c>; null — сбор не звался (отряд не найден).</summary>
public sealed record AiFortCacheEvent(long Seq, long Tid, long T, string? Player, string? Stack, AiPoint? Pos,
    IReadOnlyList<AiCacheFort>? Forts, IReadOnlyList<string?> Picked, IReadOnlyList<string?> Excluded, long PlaceSeq)
    : CorpusEvent(Seq, Tid);

/// <summary>Крепость в сборе кэша: <see cref="Filter"/> — ответ фильтра <c>0x45ec77</c>, <see cref="Guard"/> — ответ
/// охраны <c>0x45ed86</c> (true — охраняется, фильтр её отверг; null — до охраны не дошло), <see cref="Owner"/>
/// (<c>+0x84</c>), <see cref="Visitor"/> (<c>+0x8c</c>), <see cref="D"/>/<see cref="Pos"/> — только у прошедших фильтр.
/// <see cref="Why"/>: <c>filter</c>, <c>guard</c>, <c>radius</c>, <c>excluded</c>, <c>rank</c>, <c>picked</c>, <c>unknown</c>.</summary>
public sealed record AiCacheFort(string? Id, bool Filter, bool? Guard, string? Owner, string? Visitor, double? D,
    AiPoint? Pos, string Why);

/// <summary><c>ai_strength_units</c> (7.2) — сила группы <c>0x5dcec7</c> и вклад каждого живого юнита (<c>0x5dd06c</c>).
/// <see cref="Stack"/> — id, по которому движок ищет крепость стоянки (броня укрепления, <c>0x5dcf60</c>);
/// <see cref="Enemies"/> — число врагов (аргумент 5), <see cref="City"/> — броня укрепления считается (аргумент 7 = 0),
/// <see cref="Full"/> — сила при полном здоровье, <see cref="Armor"/> — прибавка брони, переданная юнитам (null — живых
/// нет). <see cref="Total"/> — ответ функции, <see cref="RetAddr"/> отличает вызовы (свой массив атаки — <c>0x453511</c>).</summary>
public sealed record AiStrengthUnitsEvent(long Seq, long Tid, long T, string? Player, string? Stack, long A3, long Enemies,
    long A6, bool City, bool Full, long? Armor, IReadOnlyList<AiStrengthUnit> Units, double? Total, string RetAddr)
    : CorpusEvent(Seq, Tid);

/// <summary>Юнит в <c>ai_strength_units</c>: <see cref="Hp"/> — текущее здоровье, <see cref="Max"/> — максимум
/// (<c>0x5e987c</c>), <see cref="S"/> — сила юнита, <see cref="Mods"/> — id модификаторов цепочки юнита.</summary>
public sealed record AiStrengthUnit(string? Id, string? Type, long Hp, long Max, double? S, IReadOnlyList<string?> Mods);

/// <summary><c>regen</c> (7.2) — дневное лечение юнита процентом от максимума (<c>0x5d4099</c>), только если юнит был не
/// на полном здоровье. <see cref="Term"/>: <c>fort</c> — слагаемое укрепления (<c>0x5d4181</c>, проход <c>0x5d40d9</c>),
/// <c>unit</c> — слагаемое юнита (<c>0x5d4588</c>: своя регенерация + <see cref="Bonus"/> — Воитель и своя земля),
/// <c>ruin</c> — руина, <c>other</c> — прочее. <see cref="Owner"/> — отряд или крепость группы, <see cref="Inside"/> —
/// крепость стоянки отряда, <see cref="Pct"/> — процент этого вызова.</summary>
public sealed record RegenEvent(long Seq, long Tid, long T, string Term, string? Unit, string? Type, string? Owner,
    string? Inside, long? Pct, long? Bonus, long HpBefore, long HpAfter, long HpMax, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>upgrade_target</c> (7.2) — поиск цели повышения: <see cref="Leader"/> false — <c>0x5d7fe7</c>, true —
/// <c>0x5d89c6</c>. <see cref="Target"/> — тип-цель (null — цели нет), <see cref="Flag"/> — байт ответа <c>0x5d7fe7</c>.
/// Прочее — те же запросы, что делает движок: <see cref="Grow"/> (<c>0x5d8f84</c>), <see cref="Container"/> — отряд или
/// крепость юнита (<c>0x5ea791</c>), <see cref="Owner"/> — её хозяин (<c>0x5eaad1</c>), <see cref="RacePlayer"/> — игрок
/// расы типа (<c>0x5d8883</c>), <see cref="RaceOk"/> — <c>0x5d88be</c>; null — движок до запроса не доходит.</summary>
public sealed record UpgradeTargetEvent(long Seq, long Tid, long T, bool Leader, string? Player, string? Unit, string? Type,
    long? Xp, bool? Dyn, bool? Grow, string? Container, string? Owner, string? RacePlayer, bool? RaceOk, string? Target,
    long? Flag, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>path_expand</c> (7.2) — поиск пути отряда не человека (<c>0x5cc6da</c>) и порядок раскрытия узлов A*
/// (<c>0x680786</c>): A* идёт от цели к отряду, <see cref="Nodes"/> — снятые из открытого списка узлы (не больше 300,
/// <see cref="N"/> — всего, <see cref="Cut"/> — обрезано). <see cref="Path"/> — точки пути с накопленной ценой.</summary>
public sealed record PathExpandEvent(long Seq, long Tid, long T, string? Player, string? Stack, string? Owner,
    AiPoint? Target, AiPoint? Resolved, long ForbidWater, long A5, long Mode, long A8, long A10, long N, bool Cut,
    IReadOnlyList<PathExpandNode> Nodes, IReadOnlyList<PathExpandPoint> Path, AiPoint? End, AiPoint? Reach,
    string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary>Раскрытый узел A*: клетка, <see cref="G"/> — цена от старта поиска, <see cref="F"/> = g + h (float движка).</summary>
public sealed record PathExpandNode(long X, long Y, double? G, double? F);

/// <summary>Точка пути: клетка и накопленная цена (у первой — 0).</summary>
public sealed record PathExpandPoint(long X, long Y, long Cost);

/// <summary><c>id_alloc</c> (7.2) — новый id объекта сценария (<c>CMidgardScenarioMap</c> слот 9, <c>0x5ee8bd</c>):
/// <see cref="Type"/> — тип id движка, <see cref="Index"/> — счётчик типа до выдачи, <see cref="Callers"/> — адреса
/// возврата в коде exe по цепочке кадров (ранг 2: функции без кадра её рвут).</summary>
public sealed record IdAllocEvent(long Seq, long Tid, long T, string? Player, long Type, string? Id, long? Index,
    string RetAddr, IReadOnlyList<string> Callers) : CorpusEvent(Seq, Tid);
