using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Состояние хода тактики, которое харнессы восстанавливают по журналу между оценками одного игрока.</summary>
internal static class AiJournalTactic
{
    /// <summary>
    /// После проверки пути (фаза 3 → 4 журнала): исключения кэша целей. Проверенные кандидаты — сверху по счёту
    /// (устойчиво), пока не наберётся четыре оставшихся; выпавший — неудача пути, и в исключения идут объекты клетки его
    /// точки (<see cref="AiTactics.PathExclusions"/>, <c>0x46903e</c> → <c>0x45f902</c>). Выпавший, чья цель осталась
    /// другим кандидатом, пути не терял: производные атаки делят одну проверку (<c>0x45ab0b</c>, кеш <c>+0x51</c>/<c>+0x52</c>)
    /// и выпадают сами — подмога, чей помощник сегодня не дойдёт. Приближение: водный отказ (<c>0x468f81</c>, до поиска, без
    /// исключений) журнал от отказа пути не отличает — такой выпавший здесь тоже даёт исключения. Пустой итог (<c>0x46652e</c>) — у «главного» отряда с
    /// флагом радиуса (<see cref="AiTacticTurn.RadiusFlag"/>, а не радиусом > 0) флаг снят; иначе он снова в силе, а выросший набор разрешает отряду слабые атаки
    /// (<see cref="AiTacticTurn.WeakAttacks"/>) — как в <see cref="AiTactics.Choose"/>.
    /// </summary>
    /// <param name="all">Вставленные кандидаты окна: цель, точка и счёт.</param>
    /// <param name="kept">Цели, оставшиеся после проверки пути (<c>top</c> выбора фазы 3).</param>
    /// <param name="map">Карта окна — план клеток для исключений.</param>
    public static void AfterPathCheck(AiTacticTurn turn, IEnumerable<(string Target, int X, int Y, double Score)> all,
                                      IReadOnlyList<string> kept, MapState map)
    {
        int before = turn.Excluded.Count, n = 0;
        var left = kept.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var (target, x, y, _) in all.OrderByDescending(c => c.Score))
        {
            if (n == AiTactic.TopCount) break;
            if (left.TryGetValue(target, out int k) && k > 0)
            {
                left[target] = k - 1;
                n++;
            }
            else if (!left.ContainsKey(target)) turn.Excluded.UnionWith(AiTactics.PathExclusions(map, x, y));
        }
        if (kept.Count > 0) return;
        if (turn.RadiusFlag)
        {
            turn.RadiusSpent = true;
            return;
        }
        turn.RadiusSpent = false;
        if (turn.Excluded.Count > before) turn.WeakAttacks = true;
    }

    /// <summary>Цели, оставшиеся после проверки пути, из выбора фазы 3 (по одной на оставшегося кандидата).</summary>
    public static IReadOnlyList<string> Kept(D2.Data.Corpus.AiTacticPickEvent pick) =>
        [.. (pick.Top ?? []).Select(t => t.Action?.Target ?? "")];
}
