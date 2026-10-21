using D2.Data.Corpus;

namespace D2.Data.Battle;

/// <summary>Один ход боя, как он записан в журнале: кто ходил (<c>turn_after.unit</c>), был ли
/// промах (<c>miss</c>), цель и урон (<c>dmg</c>, при промахе — нет), HP всех юнитов после хода
/// (<c>turn_after.units</c>). Ничего не считается по модели — только чтение журнала.</summary>
public sealed record ReplayTurn(string Unit, bool Miss, string? Target, int Damage,
                                IReadOnlyDictionary<string, int> HpAfter);

/// <summary>Ходы боя для проигрывания (сцена BattleReplay). Ход — отрезок событий до очередного
/// <c>turn_after</c>; отрезок без <c>miss</c> (например, <c>turn_after</c> нового раунда, когда
/// живой остался один) — не атака и пропускается.</summary>
public static class ReplayTurns
{
    /// <exception cref="InvalidDataException">Дубль id юнита в снимке <c>turn_after</c>.</exception>
    public static IReadOnlyList<ReplayTurn> Of(Corpus.Battle battle)
    {
        var turns = new List<ReplayTurn>();
        MissEvent? miss = null;
        DamageEvent? dmg = null;
        // ponytail: порядок seq не проверяется — доверие CorpusReader.Battles (сортирует по seq).
        // Потолок: Battle, собранный вручную не по seq, даст ходы в порядке списка; проверять здесь,
        // если появится второй источник Battle.
        foreach (var e in battle.Events)
        {
            switch (e)
            {
                case MissEvent m: miss = m; break;
                case DamageEvent d: dmg = d; break;
                case TurnEvent { After: true } t:
                    if (miss is not null)
                        turns.Add(new ReplayTurn(t.Unit, miss.Miss, dmg?.Target, (int)(dmg?.Damage ?? 0), HpOf(t)));
                    miss = null;
                    dmg = null;
                    break;
            }
        }
        return turns;
    }

    private static Dictionary<string, int> HpOf(TurnEvent t)
    {
        var hp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in t.Units)
            if (!hp.TryAdd(u.Id, (int)u.Hp))
                throw new InvalidDataException($"дубль id юнита {u.Id} в turn_after seq {t.Seq}");
        return hp;
    }
}
