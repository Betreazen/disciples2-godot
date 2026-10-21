using D2.Data.Corpus;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>
/// CRT <c>rand()</c> движка в момент строки журнала: поток свой у каждого потока ОС (msvcrt), старт — <c>srand(1)</c>, каждая строка
/// <c>crt_rand</c> потока — один шаг (<c>AiStrategicHarnessTests.Economy</c>: поток сходится по всем строкам). Им ИИ выбирает тип юнита
/// найма в гарнизон (<c>ai_unit_pick</c>, <c>0x44c137</c>) — харнесс ставит порту поток движка перед стратегией, чтобы тип выбирало
/// правило порта (<see cref="D2.Rules.Map.AiStrategicRules.PickUnits"/>), а не поток порта.
/// </summary>
internal sealed class JournalCrt
{
    private readonly Dictionary<long, (List<long> Seq, List<uint> After)> _byTid = [];
    private readonly List<AiUnitPickEvent> _picks;

    public JournalCrt(IEnumerable<CorpusEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.OrderBy(e => e.Seq).ToList();
        foreach (var g in list.OfType<CrtRandEvent>().GroupBy(e => e.Tid))
        {
            var rand = new CrtRand(1);
            var seqs = new List<long>();
            var after = new List<uint>();
            foreach (var c in g)
            {
                rand.Next();
                seqs.Add(c.Seq);
                after.Add(rand.State);
            }
            _byTid[g.Key] = (seqs, after);
        }
        _picks = [.. list.OfType<AiUnitPickEvent>()];
        _begins = [.. list.OfType<AiTurnEvent>().Where(t => t.Phase == "begin").Select(t => t.Seq)];
        _turns = [.. list.OfType<AiTurnEvent>()];
    }

    private readonly List<AiTurnEvent> _turns;

    /// <summary>Поток в миг конца хода человека дня <paramref name="day"/> (последняя строка <c>ai_turn end cause human</c> перед первым
    /// <c>ai_turn begin</c> ИИ этого дня) — для свободного дня с сохранения: дальше броски делает порт (рост земли — картинки деревьев,
    /// И210; наймы). <paramref name="within"/> — строки партии (журнал нескольких карт). Строк нет — null (поток порта).</summary>
    public uint? AtHumanEnd(int day, Func<long, bool>? within = null)
    {
        var begin = _turns.FirstOrDefault(t => t is { Phase: "begin", Cause: "ai" } && t.Turn == day && (within?.Invoke(t.Seq) ?? true));
        if (begin is null) return null;
        var end = _turns.LastOrDefault(t => t is { Phase: "end", Cause: "human" } && t.Seq < begin.Seq && (within?.Invoke(t.Seq) ?? true));
        return end is null ? null : Before(end.Tid, end.Seq);
    }

    private readonly List<long> _begins;

    /// <summary>Поток по всему журналу <paramref name="journal"/>, а не по отрезку партии: у каждой новой партии свой логический поток
    /// ОС (свой <c>srand(1)</c>), и первые его <c>crt_rand</c> журнал пишет до строки <c>game_phase new</c>, с которой харнесс режет
    /// отрезок карты (20261017-port-synth: карта 11 — seq 546 при отрезке с 609). Без них поток отстаёт на шаг, и тип найма в гарнизон
    /// порт выбирает не тот (17.10: найм разошёлся у 16 из 16 ходов карт 10–12). Поток ведётся по <c>tid</c> без сброса: переиспользованный
    /// ОС <c>tid</c> продолжил бы чужой поток; по корпусу (все журналы, 17.10) начало <c>srand(1)</c> — 41, 18467 — у каждого <c>tid</c> одно.</summary>
    public static JournalCrt Of(string journal) =>
        new(JournalCache.Read(journal, ["crt_rand", "ai_unit_pick", "ai_turn"]));

    /// <summary>Состояние потока <paramref name="tid"/> перед строкой <paramref name="seq"/>.</summary>
    public uint Before(long tid, long seq)
    {
        if (!_byTid.TryGetValue(tid, out var s)) return 1;
        int i = s.Seq.BinarySearch(seq);
        if (i < 0) i = ~i;
        return i == 0 ? 1u : s.After[i - 1];
    }

    /// <summary>Поток перед первым выбором юнита найма игрока <paramref name="player"/> в его ходу (строка <c>ai_turn begin</c>
    /// <paramref name="turn"/>, до следующего <c>ai_turn begin</c>); выборов нет — перед началом хода.</summary>
    public uint BeforeHire(AiTurnEvent turn, string player) => Before(turn.Tid, FirstHirePick(turn, player) ?? turn.Seq);

    /// <summary>Строка первого выбора юнита найма игрока <paramref name="player"/> в ходу <paramref name="turn"/>; <c>null</c> — выборов нет.</summary>
    public long? FirstHirePick(AiTurnEvent turn, string player)
    {
        ArgumentNullException.ThrowIfNull(turn);
        long until = _begins.FirstOrDefault(b => b > turn.Seq, long.MaxValue);
        return _picks.FirstOrDefault(p => p.Seq > turn.Seq && p.Seq < until
                                          && string.Equals(p.Player, player, StringComparison.OrdinalIgnoreCase))?.Seq;
    }
}
