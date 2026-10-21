using D2.Rules.Rng;

namespace D2.Data.Corpus;

/// <summary>
/// Броски журнала (строки <c>ai_rng</c> схемы 7.5) как источник R250 для решения порта: порт бросает в том же порядке,
/// что движок, и получает те же значения — выбор сверяется точно, а не «в пределах модели». Граница каждого броска
/// порта сверяется с <c>max</c> строки журнала: расхождение значит, что порт выбирал из другого списка (другие годные,
/// другой склад), — это и есть находка (<see cref="BoundsDiffer"/>). Кончились строки — бросок берётся у
/// <c>fallback</c> и считается в <see cref="Extra"/> (нет <c>fallback</c> — исключение, не тихий ноль).
/// <para>Места бросков — <c>docs/corpus-schema.md</c>, «Схема 7.5»; строки окна выбираются <see cref="Between"/>.</para>
/// </summary>
public sealed class JournalRolls(IReadOnlyList<AiRngEvent> rolls, IRng? fallback = null) : IRng
{
    private readonly List<(long JournalMax, int PortMax)> _bounds = [];
    private readonly List<int> _portBounds = [];

    /// <summary>Границы всех бросков порта по порядку (для печати окна рядом с <c>max</c> строк журнала).</summary>
    public IReadOnlyList<int> PortBounds => _portBounds;

    /// <summary>Строки журнала окна.</summary>
    public IReadOnlyList<AiRngEvent> Rolls => rolls;

    /// <summary>Сколько строк журнала отдано порту.</summary>
    public int Used { get; private set; }

    /// <summary>Броски порта сверх строк журнала (взяты у <c>fallback</c>).</summary>
    public int Extra { get; private set; }

    /// <summary>Строки журнала, которые порт не потратил.</summary>
    public int Unused => rolls.Count - Used;

    /// <summary>Броски, у которых граница порта не равна <c>max</c> журнала: (журнал, порт).</summary>
    public IReadOnlyList<(long JournalMax, int PortMax)> BoundsDiffer => _bounds;

    /// <summary>Значение журнала оказалось за границей порта (бросок взят у <c>fallback</c>).</summary>
    public int OutOfBound { get; private set; }

    /// <summary>Порт потратил ровно строки журнала и с теми же границами.</summary>
    public bool Exact => Extra == 0 && Unused == 0 && OutOfBound == 0 && _bounds.Count == 0;

    public int Next(int maxValue)
    {
        if (maxValue == 0) return 0;   // обёртка движка 0x54851f при 0 не бросает
        _portBounds.Add(maxValue);
        if (Used >= rolls.Count)
        {
            Extra++;
            return fallback?.Next(maxValue)
                   ?? throw new InvalidOperationException($"бросков журнала {rolls.Count}, порт просит ещё (граница {maxValue})");
        }
        var roll = rolls[Used++];
        if (roll.Max != maxValue) _bounds.Add((roll.Max, maxValue));
        if (roll.R >= 0 && roll.R < maxValue) return (int)roll.R;
        // Значение журнала вне границы порта — не подставлять (выбор был бы за концом списка порта).
        OutOfBound++;
        return fallback?.Next(maxValue) ?? (int)(Math.Abs(roll.R) % maxValue);
    }

    /// <summary>Строки <c>ai_rng</c> из (<paramref name="afterSeq"/>, <paramref name="beforeSeq"/>) с местом из
    /// <paramref name="sites"/> (и, если задано, с <see cref="AiRngEvent.Origin"/>), по порядку <c>seq</c>.</summary>
    public static IReadOnlyList<AiRngEvent> Between(IEnumerable<AiRngEvent> all, long afterSeq, long beforeSeq,
                                                    IReadOnlyCollection<string> sites, string? origin = null) =>
        [.. all.Where(r => r.Seq > afterSeq && r.Seq < beforeSeq && sites.Contains(r.Site)
                           && (origin is null || string.Equals(r.Origin, origin, StringComparison.Ordinal)))
               .OrderBy(r => r.Seq)];

    /// <summary>Места бросков выбора заклинания перед атакой (<c>0x472142</c>): категория (<c>0x47234b</c>) и одно из
    /// старших (<c>0x472410</c>).</summary>
    public static readonly IReadOnlyCollection<string> AttackSpellSites = ["attack_spell_kind", "attack_spell_pick"];

    /// <summary>Бросок экземпляра склада торговца (<c>0x46b6f8</c>, <c>0x46b805</c>).</summary>
    public static readonly IReadOnlyCollection<string> MerchantSites = ["merchant_item"];

    /// <summary>Перемешивание <c>0x4495d8</c> — у мага (<see cref="AiRngEvent.Origin"/> <c>mage</c>).</summary>
    public static readonly IReadOnlyCollection<string> ShuffleSites = ["shuffle_list"];
}
