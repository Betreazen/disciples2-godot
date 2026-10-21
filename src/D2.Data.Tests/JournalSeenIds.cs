using D2.Data.Corpus;

namespace D2.Data.Tests;

/// <summary>
/// Нижняя граница счётчиков id движка (<c>CMidgardScenarioMap::freeIdTypeIndices</c>, <c>0x5ee8bd</c>) по id, которые журнал уже
/// показал в этом процессе игры: движок номера не переиспользует (при загрузке — наибольший + 1, дальше только растёт), так что
/// счётчик класса не меньше наибольшего виденного номера + 1. Нужна журналам без <c>id_alloc</c> (до схемы 7.2): живой день
/// начинается с сохранения, где погибших за сессию уже нет, и порт выдал бы их номера снова (human, день 42 — пал лидер
/// <c>S073UN0264</c> отряда <c>KC009f</c>; призыв дня 43 у движка — <c>UN0265</c>, у порта без границы — <c>UN0264</c>).
/// Новая фаза игры (<c>game_phase</c> <c>new</c>/<c>load</c>, в старых журналах <c>enter</c>) счётчики сбрасывает — граница начинается
/// заново. Счётчики — по карте (префикс id).
/// </summary>
internal sealed class JournalSeenIds
{
    /// <summary>Префикс карты → класс → наибольший виденный номер + 1.</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _next = new(StringComparer.OrdinalIgnoreCase);

    private int _cursor;

    /// <summary>Учесть строки <paramref name="events"/> (по порядку seq) до строки <paramref name="seq"/>, ещё не учтённые, и вернуть
    /// границу счётчиков карты <paramref name="mapPrefix"/> (первые 4 знака id, <c>S073</c>). Звать с неубывающим <paramref name="seq"/>:
    /// учтённые строки назад не отматываются.</summary>
    public IReadOnlyDictionary<string, int> Until(IReadOnlyList<CorpusEvent> events, long seq, string mapPrefix)
    {
        ArgumentNullException.ThrowIfNull(events);
        for (; _cursor < events.Count && events[_cursor].Seq < seq; _cursor++) Note(events[_cursor]);
        return _next.TryGetValue(mapPrefix, out var next) ? next : new Dictionary<string, int>();
    }

    private void Note(CorpusEvent e)
    {
        switch (e)
        {
            case GamePhaseEvent { What: not "leave" }:   // new, load (зонд 7+) или enter (старые журналы) — новая фаза игры
                _next.Clear();
                break;
            case BattleStartEvent b:
                Add(b.AttackerGroup);
                Add(b.DefenderGroup);
                foreach (var u in b.Units) Add(u.Id);
                break;
            case BattleEndEvent b:
                foreach (var u in b.Units) Add(u.Id);
                break;
            case ApRestoreEvent a:
                Add(a.Stack);
                Add(a.Leader);
                break;
            case StackMoveEvent m:
                Add(m.Stack);
                break;
            // Схема 7.2+: выданный номер любого класса (0x5ee8bd) — и тех, чей id журнал больше нигде не пишет: жезл (rod_drop без id),
            // погибший в тот же ход. Карта 23, дни 21–25: без строк id_alloc у живого дня номера жезлов на один меньше, чем у движка
            // (S143RD0021 против RD0022 в той же клетке), и кандидаты AttackRod расходились целью.
            case IdAllocEvent { Id: { Length: 10 } id }:
                Add(id);
                break;
        }
    }

    private void Add(string? id)
    {
        if (id is not { Length: 10 }) return;
        if (!_next.TryGetValue(id[..4], out var next)) _next[id[..4]] = next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, n) in D2.Data.Map.MapLoader.IdNextOf([id]))
            if (next.GetValueOrDefault(kind) < n) next[kind] = n;
    }
}
