using System.Text.Json;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Память трат стратегии ИИ (<see cref="AiMemory"/>: последний день траты по типу списка) из журнала. Движок её в
/// <c>.sg</c> не пишет, а от неё зависит бюджет дня (рост деревень, найм, постройки) — без неё стратегия порта с сохранения
/// тратит иначе, чем движок (probe65, <c>S143PL0002</c> дни 12–16: без памяти бюджет роста выше цены, и деревня растёт до 5 на
/// пять дней раньше движка).</summary>
internal sealed class AiSpendMemory
{
    private readonly Dictionary<(string Player, AiStepType Type), List<long>> _days = [];

    /// <summary>Из строк журнала <c>ai_turn</c> и <c>ai_budget_spend</c> (JSON, по порядку <c>seq</c>; прочие строки пропускаются) —
    /// день траты по <c>ai_turn begin</c> её игрока.</summary>
    public static AiSpendMemory From(IEnumerable<JsonElement> lines)
    {
        var memory = new AiSpendMemory();
        var day = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in lines)
        {
            string ev = e.GetProperty("ev").GetString()!;
            if (ev is not ("ai_turn" or "ai_budget_spend")) continue;
            string player = e.GetProperty("player").GetString() ?? "";
            if (ev == "ai_turn")
            {
                if (e.GetProperty("phase").GetString() == "begin" && e.GetProperty("cause").GetString() == "ai")
                    day[player] = e.GetProperty("turn").GetInt64();
                continue;
            }
            if (!day.TryGetValue(player, out long d)) continue;
            memory.Add(player, (AiStepType)e.GetProperty("type").GetInt32(), d);
        }
        return memory;
    }

    /// <summary>То же из уже прочитанных строк (<c>ai_turn</c> и <c>ai_budget_spend</c> по порядку <c>seq</c>).</summary>
    public static AiSpendMemory From(IEnumerable<D2.Data.Corpus.CorpusEvent> events)
    {
        var memory = new AiSpendMemory();
        var day = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
            switch (e)
            {
                case D2.Data.Corpus.AiTurnEvent { Phase: "begin", Cause: "ai", Player: { } p } t:
                    day[p] = t.Turn;
                    break;
                case D2.Data.Corpus.AiBudgetSpendEvent { Player: { } p } s when day.TryGetValue(p, out long d):
                    memory.Add(p, (AiStepType)s.Type, d);
                    break;
            }
        return memory;
    }

    /// <summary>Трата игрока <paramref name="player"/> типа <paramref name="type"/> в день <paramref name="day"/>.</summary>
    public void Add(string player, AiStepType type, long day)
    {
        if (!_days.TryGetValue((player.ToUpperInvariant(), type), out var days)) _days[(player.ToUpperInvariant(), type)] = days = [];
        days.Add(day);
    }

    /// <summary>Память всех игроков карты на начало дня <paramref name="day"/>: последний день траты типа до него.</summary>
    public Dictionary<string, AiMemory> Before(MapState map, int day) =>
        map.Players.Keys.ToDictionary(
            id => id,
            id => AiMemory.From(_days.Where(x => x.Key.Player == id.ToUpperInvariant())
                                     .ToDictionary(x => x.Key.Type, x => (int)x.Value.Where(d => d < day).DefaultIfEmpty(0).Max()),
                                new Dictionary<(string Id, AiStepType Type), int>()),
            StringComparer.OrdinalIgnoreCase);
}
