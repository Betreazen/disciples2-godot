using D2.Rules.Battle;

namespace D2.Data.Sound;

/// <summary>
/// Звуки сообщений боя без удара (<see cref="BattleState.Notes"/>; разбор 27.09, <c>CBattleViewerInterf</c> <c>0x630a7b</c>, таблица
/// шагов <c>0x6310b3</c>, звук <c>0x5c1dec</c> громкостью по умолчанию, ранг 1): шаги 13 и 16 (прежний облик, восстановление сил) —
/// <c>UNTRANSF</c> (текст — по проверке, звук — всегда), 14 (призванный изгнан) — <c>UNSUMMON</c>, 15 (повышение) — <c>UPGRADE</c> + раса
/// владельца группы (<c>HU</c>/<c>HE</c>/<c>DW</c>/<c>UN</c>/<c>EL</c>, иначе <c>NE</c>; <c>0x5b4b7c</c>; у нейтралов движок берёт расу
/// самого юнита — ранг 2, у порта расы типа нет: <c>NE</c>). В архивах есть только <c>UPGRADEEL</c> — прочие молчат, как в игре. Снятие
/// паралича и окаменения — <c>UPARALYZE</c>/<c>UPETRIFY</c>, их в архивах нет.
/// </summary>
public static class BattleNoteSounds
{
    public const string Archive = "Battle";

    /// <summary>Коды рас по типу расы (<c>Lrace</c>): 0 люди, 1 нежить, 2 легионы, 3 кланы, 4 нейтралы, 5 эльфы.</summary>
    private static readonly string[] RaceCodes = ["HU", "UN", "HE", "DW", "NE", "EL"];

    /// <summary>Звук сообщения; <paramref name="raceType"/> — тип расы владельца группы юнита (−1 — нет).</summary>
    public static string Of(BattleNoteKind kind, int raceType) => kind switch
    {
        BattleNoteKind.Untransformed => "UNTRANSF",
        BattleNoteKind.Unsummoned or BattleNoteKind.UnsummonedInBattle => "UNSUMMON",
        BattleNoteKind.LevelUp => "UPGRADE" + (raceType >= 0 && raceType < RaceCodes.Length ? RaceCodes[raceType] : "NE"),
        BattleNoteKind.Unparalyzed => "UPARALYZE",
        BattleNoteKind.Unpetrified => "UPETRIFY",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
