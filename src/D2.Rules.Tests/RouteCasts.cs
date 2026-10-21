using System.Text.Json;
using D2.Data.Corpus;

/// <summary>Заклинания, применённые в прогоне: то, чего зонд не пишет, но маршрут знает.
///
/// <para><b>Зачем.</b> Событий заклинания в корпусе нет вовсе (адресов <c>castSpell</c> в версионной
/// таблице движка нет — задача 4 Ф6), а на журнал они влияют дважды: заклинание смены террейна
/// меняет стоимость шага на своих клетках (<c>C4MoveHarnessTests</c>), а заклинание с модификатором
/// меняет точность и урон в бою, начавшемся тем же ходом (<c>C1HarnessTests</c>). Оба следа
/// объективны, и оба остались бы «унаследованным расхождением», если бы харнесс не знал о кастах.
/// Источник — <c>route.json</c>, который драйвер кладёт рядом с журналом и по которому и играл:
/// он несёт ход, заклинание и клетку-центр.</para>
///
/// <para><b>Ранг 3 — длительность.</b> Каст берётся только для того хода, на котором он сделан:
/// сколько дней живёт наложенный на отряд модификатор, ни журнал, ни маршрут не говорят. Снимает
/// дифф <c>.sg</c> (<c>MidSpellEffects</c>) между сохранениями соседних ходов.</para></summary>
public sealed record RouteCast(int Turn, string SpellId, string On, int X, int Y)
{
    public const string OnSelf = "self";
    public const string OnEnemy = "enemy";
}

public static class RouteCasts
{
    /// <summary>Касты из <c>route.json</c> рядом с журналом. Файла нет — пустой список (журналы Ф4
    /// и Ф5 сняты без книги заклинаний); каст с целью <c>"stack"</c> (клетка отряда берётся из
    /// журнала на месте) пропускается — клетки у него в маршруте нет.</summary>
    public static IReadOnlyList<RouteCast> Of(string journalPath)
    {
        var route = Path.Combine(Path.GetDirectoryName(journalPath) ?? ".", "route.json");
        if (!File.Exists(route)) return [];

        var casts = new List<RouteCast>();
        using var doc = JsonDocument.Parse(File.ReadAllText(route));
        if (!doc.RootElement.TryGetProperty("turns", out var turns)) return [];
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("actions", out var actions)) continue;
            int n = turn.GetProperty("turn").GetInt32();
            foreach (var a in actions.EnumerateArray())
            {
                if (a.GetProperty("do").GetString() != "cast") continue;
                if (!a.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.Array) continue;
                casts.Add(new RouteCast(n, a.GetProperty("spell").GetString() ?? "",
                                        a.TryGetProperty("on", out var on) ? on.GetString() ?? "" : "",
                                        t[0].GetInt32(), t[1].GetInt32()));
            }
        }
        return casts;
    }

    /// <summary>Ход, шедший на момент события: границы дня видны по <c>terrain_day</c> (оно пишется
    /// из хука дохода ровно раз на игрока в день и несёт номер хода). Журнал без <c>terrain_day</c>
    /// (схема 3 и раньше) даёт пустые отметки, и тогда <see cref="TurnAt"/> отвечает 0 — «хода не
    /// знаем», то есть ни один каст не подойдёт.</summary>
    public static IReadOnlyList<(long Seq, int Turn)> TurnMarks(string journalPath) =>
        JournalCache.Read(journalPath, "terrain_day").OfType<TerrainDayEvent>()
            .Select(e => (e.Seq, (int)e.Turn)).OrderBy(m => m.Seq).ToList();

    public static int TurnAt(IReadOnlyList<(long Seq, int Turn)> marks, long seq)
    {
        int turn = 0;
        foreach (var m in marks)
        {
            if (m.Seq > seq) break;
            turn = m.Turn;
        }
        return turn;
    }
}
