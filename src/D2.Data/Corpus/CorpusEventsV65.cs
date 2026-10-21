using System.Text.Json;
using D2.Rules.Map;

namespace D2.Data.Corpus;

// Схема 6.5: экономический шаг ИИ (probeaieco.cpp) и тактика ИИ на карте (probeaitac.cpp). Каждое событие
// пишется на каждый вызов, без ключа; разбор — disciples-modding/docs/synthetic-maps/analysis/
// O-re65-strategic.md и N-re65-tactics.md. Поля с рангом 2/3 разбора лежат рядом с сырыми байтами (Raw).

/// <summary>Объект оценки или действия стратегии по vftable: <see cref="Class"/> и <see cref="Type"/> — null,
/// если vftable зонду не известна; <see cref="Fields"/> — поля по таблице зонда (<c>id4</c>, <c>d16</c>,
/// <c>cat32</c>, …: имя = вид + смещение), <see cref="Raw"/> — байты объекта после vftable, hex.</summary>
public sealed record AiObject(string Vft, string? Class, long? Type, IReadOnlyDictionary<string, JsonElement> Fields,
    string? Raw);

/// <summary>Клетка карты.</summary>
public sealed record AiPoint(long X, long Y);

/// <summary>Действие тактики — геттеры слотов 1–4: тип, отряд, цель, точка; null — слот не прочитан.</summary>
public sealed record AiTacticAction(string Ptr, string Vft, long? Type, string? Stack, string? Target, AiPoint? Point);

/// <summary>Узел списка кандидатов тактики: действие и счёт.</summary>
public sealed record AiTacticCandidate(AiTacticAction? Action, double? Score);

/// <summary>Узел порядка городов <c>0x44809c</c>.</summary>
public sealed record AiCityRank(string? Id, long? Prio);

/// <summary>Сосед кристалла в <c>ai_rod_cell</c>: проверки по порядку (null — движок до неё не дошёл),
/// расстояние — только у годной клетки.</summary>
public sealed record AiRodCell(long X, long Y, bool? OkPlace, bool? OkAlly, bool? OkBlock, long? Dist, bool Chosen);

/// <summary><c>ai_eval</c> (6.5) — оценка стратегии добавлена в вектор (<c>0x44dcb8</c>); <see cref="List"/> —
/// построитель по адресу возврата (<c>hire_unit</c>, <c>capital</c>, …), <see cref="Pass"/> — проход хода.</summary>
public sealed record AiEvalEvent(long Seq, long Tid, long T, string? Player, long Pass, string? List, AiObject? Eval,
    string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_candidate</c> (6.5) — действие добавлено в список прохода (<c>0x44dd87</c>);
/// <see cref="Idx"/> — длина списка до вставки, исполняется <see cref="Chosen"/> (idx 0); счёт на выбор не
/// влияет.</summary>
public sealed record AiCandidateEvent(long Seq, long Tid, long T, string? Player, long Pass, long? Idx, bool? Chosen,
    AiObject? Action, double? Score, long? CountAfter, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_exec</c> (6.5) — исполнение действия стратегии (<see cref="Fn"/> — адрес execute).</summary>
public sealed record AiExecEvent(long Seq, long Tid, long T, string? Player, long Pass, string Fn, AiObject? Action,
    long RngBefore, long RngAfter, string RetAddr, bool Ret, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_budget_spend</c> (6.5) — списание бюджета типа после исполнения (<c>0x4464ef</c>):
/// бюджет и казна начала до и после; null — не прочитано.</summary>
public sealed record AiBudgetSpendEvent(long Seq, long Tid, long T, string? Player, long Pass, long Type,
    long? Reserve, Bank? BudgetBefore, Bank? BudgetAfter, Bank? StartBefore, Bank? StartAfter,
    Bank? SpellStartBefore, Bank? SpellStartAfter, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_city_rank</c> (6.5) — порядок городов (<c>0x44809c</c>), слабейший первым.</summary>
public sealed record AiCityRankEvent(long Seq, long Tid, long T, string? Player, long Pass, long? Count,
    IReadOnlyList<AiCityRank> Cities, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_leader_role</c> (6.5) — роль лидера (<see cref="Fn"/> <c>role</c> <c>0x44b5c0</c>) и
/// подприказ (<c>fighter</c> <c>0x44b6f3</c>, <c>thief</c> <c>0x44b7d0</c>): двенадцать аргументов
/// (ранг 2: bool×3, int×3, float×3, int×3) и ответ.</summary>
public sealed record AiLeaderRoleEvent(long Seq, long Tid, long T, string? Player, long Pass, string Fn,
    IReadOnlyList<long> B, IReadOnlyList<long> I, IReadOnlyList<double?> F, IReadOnlyList<long> J, string RetAddr,
    long Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_leader_pick</c> (6.5) — тип нанимаемого лидера (<c>0x44ad0f</c>); <see cref="Roll"/> —
/// первый R250 внутри вызова.</summary>
public sealed record AiLeaderPickEvent(long Seq, long Tid, long T, string? Player, long Pass, string? Id, Bank? Bank,
    long Role, bool Flag, string? Result, long? Roll, long RngBefore, long RngAfter, string RetAddr, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ai_unit_pick</c> (6.5) — случайный юнит найма (<c>0x44c06a</c>, CRT <c>rand</c>);
/// <see cref="Filter"/> <c>melee_small</c>/<c>big</c>/<c>any</c>.</summary>
public sealed record AiUnitPickEvent(long Seq, long Tid, long T, string? Player, long Pass, string? Id,
    string? Filter, string FilterVft, long? ListLen, string? Result, long RngStdBefore, long RngStdAfter,
    string RetAddr, long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_build_pick</c> (6.5) — здание столицы: <see cref="Strategy"/> <c>s1</c>/<c>s2</c> —
/// <see cref="Result"/> id здания, <c>temple</c> (<c>0x5dcc28</c>) — <see cref="Status"/>.</summary>
public sealed record AiBuildPickEvent(long Seq, long Tid, long T, string? Player, long Pass, string Strategy,
    string? Id, string? Result, long? Status, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_rng</c> (6.5) — бросок выбора ИИ (<see cref="Site"/> <c>leader_pick</c> R250,
/// <c>unit_pick</c>/<c>leader_name</c> CRT); <see cref="Raw"/> — сырой <c>rand()</c> из <c>crt_rand</c>,
/// <see cref="N"/> — номер в счётчике rng или rng_std. Схема 7.5: места — все броски решений игры вне показа и боя
/// (<c>attack_spell_kind</c>, <c>merchant_item</c>, <c>shuffle_list</c> …, <c>docs/corpus-schema.md</c>), у
/// перемешиваний и общих выборов — <see cref="Outer"/> (адреса возврата из обёрток выше по стеку) и
/// <see cref="Origin"/> (имя по самому глубокому известному из них), <see cref="Player"/> — игрок хода ИИ; у журналов
/// до 7.5 все три — null.</summary>
public sealed record AiRngEvent(long Seq, long Tid, long T, string Site, string Kind, long N, long Max, long R,
    long? Raw, bool Battle, string RetAddr) : CorpusEvent(Seq, Tid)
{
    /// <summary>Игрок, чей ход ИИ начался последним (схема 7.5).</summary>
    public string? Player { get; init; }

    /// <summary>Цепочка адресов возврата из обёрток (схема 7.5), ближняя первой.</summary>
    public IReadOnlyList<string>? Outer { get; init; }

    /// <summary>Откуда пришёл бросок общей обёртки: <c>mage</c>, <c>gift_spell</c>, <c>harass_far</c> … (схема 7.5).</summary>
    public string? Origin { get; init; }
}

/// <summary><c>crt_rand</c> (6.5) — каждый вызов <c>msvcrt!rand</c>: сырое значение для восстановления
/// состояния LCG потока (<see cref="CorpusEvent.Tid"/>).</summary>
public sealed record CrtRandEvent(long Seq, long Tid, long T, long N, long R, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_tactic_candidate</c> (6.5) — вставка действия тактики (<c>0x4686cf</c>):
/// <see cref="ScoreIn"/> — счёт на входе, <see cref="Score"/> — сохранённый (×1,25 за вчерашний захват),
/// null — не вставлено.</summary>
public sealed record AiTacticCandidateEvent(long Seq, long Tid, long T, string? Player, AiTacticAction? Action,
    double? ScoreIn, long? Idx, bool? Inserted, double? Score, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_tactic_adjust</c> (6.5) — поправка счёта: <see cref="Kind"/> <c>priority</c>
/// (<c>0x466d5c</c>, приоритет редактора цели) или <c>set</c> (<c>0x466cd8</c>, +100).
/// <see cref="Stack"/> — <c>CMidStack::id</c> первого аргумента (6.5.1; до неё поле давало мнимый id).</summary>
public sealed record AiTacticAdjustEvent(long Seq, long Tid, long T, string? Player, string Kind, string? Stack,
    AiTacticAction? Action, long? Priority, double? Before, double? After, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_tactic_pick</c> (6.5) — шаг тактики (<c>0x466388</c>): фаза до и после; <see cref="Top"/> —
/// список после фазы 3 (не больше четырёх), <see cref="Chosen"/> — первый кандидат перед фазой 4.</summary>
public sealed record AiTacticPickEvent(long Seq, long Tid, long T, string? Player, long PhaseBefore, long PhaseAfter,
    string? Stack, string? StackAfter, long? Count, IReadOnlyList<AiTacticCandidate>? Top,
    IReadOnlyList<AiTacticCandidate>? Chosen, long RngBefore, long RngAfter, string RetAddr, bool Ret, long SeqEnd)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ai_tactic_stack</c> (6.5) — выбран следующий отряд (фаза 0 → 1). 7.1: <see cref="Inside"/> — крепость,
/// в которой стоит отряд (<c>CMidStack+0x94</c>; <c>StayInside</c> строится только при непустом), <see cref="Pos"/> —
/// его клетка; в журналах до 7.1 — null. Пустой id (<c>G000000000</c>) читается как null.</summary>
public sealed record AiTacticStackEvent(long Seq, long Tid, long T, string? Player, string? Stack, long PickSeq,
    string? Inside = null, AiPoint? Pos = null) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_tactic_result</c> (6.5) — память результата действия (<c>0x461fb2</c>).</summary>
public sealed record AiTacticResultEvent(long Seq, long Tid, long T, string? Player, string? Stack, string? Target,
    AiPoint? Point, long? Turn, string TurnPtr, long Status, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_rod_cell</c> (6.5) — клетка установки жезла (<c>0x46164d</c>) и пере-проверка восьми
/// соседей кристалла.</summary>
public sealed record AiRodCellEvent(long Seq, long Tid, long T, string? Player, string? Stack, string? Crystal,
    AiPoint? CrystalPos, AiPoint? Result, IReadOnlyList<AiRodCell> Cells, string RetAddr, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ai_strength</c> (6.5) — отношение сил (<c>0x5dda25</c>); <see cref="P1"/>, <see cref="P2"/>,
/// <see cref="P4"/> — сырые указатели (ранг 3): <c>p1</c> — карта, <c>p2</c> и <c>p4</c> — <c>IdVector</c>
/// идентификаторов юнитов, их длины — <see cref="P2N"/> и <see cref="P4N"/> (6.5.1; до неё в журнале стояли
/// мнимые <c>p2_id</c>/<c>p4_id</c>). 7.1: <see cref="P2Units"/>/<see cref="P4Units"/> — сами id юнитов векторов (не
/// больше 16), в журналах до 7.1 — null.</summary>
public sealed record AiStrengthEvent(long Seq, long Tid, long T, string? Player, string P1, string P2, string P4,
    long? P2N, long? P4N, double? A, double? B, double? C, string RetAddr, double? Ret,
    IReadOnlyList<string?>? P2Units = null, IReadOnlyList<string?>? P4Units = null) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_hire_score</c> (7.1) — счёт найма в отряд у крепости (<c>0x45a34c</c>) и запросы самой функции, повторённые
/// зондом: <see cref="Free"/> — свободные места группы отряда, <see cref="Removable"/> — снимаемые из гарнизона
/// (<c>0x45a50e</c>), <see cref="CanHire"/> — <c>0x45a498</c> (false — счёт NoScore). Null — не прочитано.</summary>
public sealed record AiHireScoreEvent(long Seq, long Tid, long T, string? Player, string? Stack, string? Fort,
    double? Score, long? Free, long? Removable, bool? CanHire, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_grid</c> (7.1) — построение сетки влияния тактики по крепостям игрока <see cref="Owner"/>
/// (<c>0x461c85</c>); её метки — строки <see cref="AiGridFortEvent"/> между <see cref="CorpusEvent.Seq"/> и
/// <see cref="SeqEnd"/>.</summary>
public sealed record AiGridEvent(long Seq, long Tid, long T, string? Player, string? Owner, string Grid, string RetAddr,
    long SeqEnd) : CorpusEvent(Seq, Tid);

/// <summary><c>ai_grid_fort</c> (7.1) — метка одной крепости в сетке (<c>0x461dfc</c>): центр и радиус (13 у столицы,
/// 13 + уровень у деревни).</summary>
public sealed record AiGridFortEvent(long Seq, long Tid, long T, string? Player, string Grid, AiPoint? Center, long Radius)
    : CorpusEvent(Seq, Tid);
