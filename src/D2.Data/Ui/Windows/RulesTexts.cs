using D2.Data.Dbf;

namespace D2.Data.Ui.Windows;

/// <summary>
/// Тексты окон из таблиц правил — id строк по id записи: описание и раса воина (<c>Gunits.DESC_TXT</c>, <c>RACE_ID</c>), имя расы
/// (<c>Grace.NAME_TXT</c>), имя постройки (<c>Gbuild.NAME_TXT</c>), имя атаки (<c>Gattacks.NAME_TXT</c>), здание найма воина
/// (<c>Gunits.ENROLL_B</c>, только непустые), пол воина (<c>Gunits.SEX_M</c>: T/F). Площадки, карточка воина, окно построек, имя героя.
/// </summary>
public sealed record RulesTexts(IReadOnlyDictionary<string, string> UnitDescs, IReadOnlyDictionary<string, string> RaceNames,
                                IReadOnlyDictionary<string, string> UnitRaces, IReadOnlyDictionary<string, string> BuildingNames,
                                IReadOnlyDictionary<string, string> AttackNames, IReadOnlyDictionary<string, string> EnrollBuildings,
                                IReadOnlyDictionary<string, string> UnitSexes)
{
    /// <summary>Тип героя строкой (<c>Gunits.ABIL_TXT</c>: «Герой», «Вор»…) — последняя строка героя в окне найма (<c>getAbilityName</c>).</summary>
    public IReadOnlyDictionary<string, string> UnitAbilities { get; init; } = new Dictionary<string, string>();

    /// <summary>Сколько ходов живёт призванный (<c>Gunits.LIFE_TIME</c>) — длительность в карточке заклинания призыва.</summary>
    public IReadOnlyDictionary<string, int> UnitLifeTimes { get; init; } = new Dictionary<string, int>();

    public static RulesTexts Load(string rulesDir)
    {
        var units = GameTables.Read(rulesDir, "Gunits.dbf").Rows;
        return new(
            Map(units, "UNIT_ID", "DESC_TXT"),
            Map(GameTables.Read(rulesDir, "Grace.dbf").Rows, "RACE_ID", "NAME_TXT"),
            Map(units, "UNIT_ID", "RACE_ID"),
            Map(GameTables.Read(rulesDir, "Gbuild.dbf").Rows, "BUILD_ID", "NAME_TXT"),
            Map(GameTables.Read(rulesDir, "Gattacks.dbf").Rows, "ATT_ID", "NAME_TXT"),
            Map(units.Where(r => r.GetString("ENROLL_B") is { Length: > 0 } b && !b.Equals("G000000000", StringComparison.OrdinalIgnoreCase)),
                "UNIT_ID", "ENROLL_B"),
            units.ToDictionary(r => r.GetString("UNIT_ID"), r => r.GetBool("SEX_M") ? "T" : "F", StringComparer.OrdinalIgnoreCase))
        {
            UnitAbilities = Map(units, "UNIT_ID", "ABIL_TXT"),
            UnitLifeTimes = units.GroupBy(r => r.GetString("UNIT_ID"), StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.First().GetInt("LIFE_TIME"), StringComparer.OrdinalIgnoreCase),
        };
    }

    private static Dictionary<string, string> Map(IEnumerable<DbfRow> rows, string key, string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map.TryAdd(r.GetString(key), r.GetString(value));
        return map;
    }
}
