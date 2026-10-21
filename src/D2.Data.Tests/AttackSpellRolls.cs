using D2.Data.Corpus;
using D2.Data.Play;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Броски выбора заклинания перед атакой (<c>0x472142</c>) из журнала схемы 7.5 для харнесса тактики
/// (<see cref="AiTacticFromMapHarnessTests"/>): строки <c>ai_rng</c> <c>attack_spell_kind</c>/<c>attack_spell_pick</c> окна
/// оценки — между <c>seq</c> и <c>seq_end</c> строки <c>ai_tactic_pick</c> фазы 2, следующей за выбором отряда. У журналов до
/// 7.5 значений нет, но генератор движка восстанавливается от сева по строкам <c>rng</c> (<see cref="JournalRng"/>): окно бросает им с
/// вызова <c>rng_before + 1</c> строки оценки, и сверяется число бросков (<c>rng_after − rng_before</c>); не восстановился — харнесс
/// бросает своим R250, как прежде.
/// </summary>
internal sealed class AttackSpellRolls
{
    private readonly bool _v75;
    private readonly List<AiRngEvent> _rolls;
    private readonly List<AiTacticPickEvent> _evals;
    private readonly List<long> _selects;
    private readonly SortedDictionary<string, int> _differ = new(StringComparer.Ordinal);
    private int _shown, _cursor;

    private readonly JournalRng? _engine;
    private int _engineShown;

    public AttackSpellRolls(string journal, IReadOnlyList<CorpusEvent> events)
    {
        _v75 = JournalCache.Read(journal, "start").OfType<StartEvent>().Any(st => st.SchemaAtLeast(7, 5));
        if (!_v75 && Environment.GetEnvironmentVariable("D2_ATTACK_ENGINE") != "off")
            _engine = new JournalRng(JournalCache.Read(journal, ["rng_seed", "rng"]));
        _rolls = [.. events.OfType<AiRngEvent>().Where(r => JournalRolls.AttackSpellSites.Contains(r.Site)).OrderBy(r => r.Seq)];
        _evals = [.. events.OfType<AiTacticPickEvent>().Where(p => p.PhaseBefore == 2).OrderBy(p => p.Seq)];
        _selects = [.. events.OfType<AiTacticStackEvent>().Select(s => s.Seq).Order()];
    }

    /// <summary>Окон с бросками журнала (журнал 7.5, у окна есть строка оценки).</summary>
    public int Windows { get; private set; }

    /// <summary>Окон, где порт потратил ровно броски журнала и с теми же границами.</summary>
    public int ExactWindows { get; private set; }

    /// <summary>Окон до 7.5, где порт бросал генератором движка (<see cref="JournalRng"/>).</summary>
    public int EngineWindows { get; private set; }

    /// <summary>Из них — порт бросил столько же, сколько движок в окне.</summary>
    public int EngineExact { get; private set; }

    /// <summary>Броски для окна оценки после выбора отряда <paramref name="stackSeq"/>: у журнала 7.5 — строки журнала
    /// (<see cref="JournalRolls"/>, сверх них бросает <paramref name="fallback"/>), до 7.5 — генератор движка с первого вызова окна;
    /// null — оценки нет или генератор не восстановился.</summary>
    public IRng? Open(long stackSeq, IRng fallback)
    {
        if (!_v75 && _engine is null) return null;
        // Окна идут по порядку seq — курсор только вперёд.
        while (_cursor < _evals.Count && _evals[_cursor].Seq <= stackSeq) _cursor++;
        if (_cursor >= _evals.Count) return null;
        var eval = _evals[_cursor];
        // Оценка — этого выбора отряда, а не следующего: у выбора без оценки окна нет (курсор не сдвигается).
        int next = _selects.BinarySearch(stackSeq);
        int k = next < 0 ? ~next : next + 1;
        if (k < _selects.Count && eval.Seq > _selects[k]) return null;
        if (!_v75)
            return _engine!.At(eval.Seq, eval.RngBefore + 1, minBits: 48) is { } g
                ? new EngineWindow(g, eval.RngAfter - eval.RngBefore) : null;
        return new JournalRolls(JournalRolls.Between(_rolls, eval.Seq, eval.SeqEnd, JournalRolls.AttackSpellSites), fallback);
    }

    /// <summary>Генератор движка в окне оценки (журнал до 7.5) и сколько вызовов у движка в окне.</summary>
    private sealed class EngineWindow(EngineRng engine, long want) : IRng
    {
        public long Want { get; } = want;

        public int Calls { get; private set; }

        public int Next(int maxValue)
        {
            if (maxValue != 0) Calls++;
            return engine.Next(maxValue);
        }
    }

    /// <summary>Итог окна: точно или в чём разошлось (лишние броски порта, недотраченные журнала, границы).</summary>
    public void Close(IRng window, string where, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(output);
        if (window is EngineWindow engine)
        {
            EngineWindows++;
            if (engine.Calls == engine.Want) EngineExact++;
            else if (_engineShown++ < 20) output.WriteLine($"броски перед атакой {where} (генератор движка): движок {engine.Want}, порт {engine.Calls}");
            return;
        }
        if (window is not JournalRolls replay) return;
        Windows++;
        if (replay.Exact)
        {
            ExactWindows++;
            return;
        }
        string why = replay.Extra > 0 ? "порт бросил больше" : replay.Unused > 0 ? "порт бросил меньше" : "границы бросков";
        _differ[why] = _differ.GetValueOrDefault(why) + 1;
        if (_shown++ < 20)
            output.WriteLine($"броски перед атакой {where}: {why}; журнал {replay.Used + replay.Unused}, порт {replay.Used + replay.Extra}, "
                             + $"границы (журнал, порт) [{string.Join(" ", replay.BoundsDiffer.Select(b => $"{b.JournalMax}/{b.PortMax}"))}]; "
                             + $"по порядку журнал [{string.Join(" ", replay.Rolls.Select(r => r.Max))}], порт [{string.Join(" ", replay.PortBounds)}]");
    }

    public void Report(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!_v75)
        {
            if (_engine is not null) output.WriteLine($"броски оценки на генераторе движка (до 7.5): окон {EngineWindows}, столько же бросков {EngineExact}");
            return;
        }
        output.WriteLine($"броски выбора заклинания перед атакой (7.5): окон {Windows}, точно {ExactWindows}"
                         + string.Concat(_differ.Select(p => $", {p.Key} {p.Value}")));
    }
}
