using D2.Data.Corpus;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>
/// R250 движка в момент счётчика журнала (<c>rng_before</c> строк зонда). Броски журнал пишет не все, но траектория генератора
/// восстанавливается: от последней строки <c>rng_seed</c> (таблица и индекс) счётчик вызовов почти равен числу шагов — сдвиг копится
/// редко (отбраковка хвоста, <c>docs/re-rng.md</c>), поэтому он подбирается по ближайшим строкам <c>rng</c> до счётчика (их значения
/// сходятся только при верном сдвиге). Так харнесс получает броски движка, которых журнал не пишет (бои, выбор заклинания), — это
/// состояние, не правило.
/// </summary>
internal sealed class JournalRng
{
    /// <summary>Строк <c>rng</c>, по которым подбирается сдвиг.</summary>
    private const int Anchors = 4;

    /// <summary>Наибольший сдвиг шагов против счётчика (к концу партии human — около 12).</summary>
    private const int MaxShift = 400;

    private readonly List<RngSeedEvent> _seeds;
    private readonly List<RngEvent> _draws;

    /// <param name="extraDraws">Броски, которые журнал пишет не строками <c>rng</c>, а полями своих строк (<c>roll</c> проверок
    /// дипломатии ИИ и ответов, номер сообщения): тоже опоры для подбора сдвига — до первого боя карты строк <c>rng</c> нет.</param>
    public JournalRng(IEnumerable<CorpusEvent> events, IEnumerable<RngEvent>? extraDraws = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.OrderBy(e => e.Seq).ToList();
        _seeds = [.. list.OfType<RngSeedEvent>()];
        _draws = [.. list.OfType<RngEvent>().Where(d => !d.Std).Concat(extraDraws ?? []).OrderBy(d => d.Seq).ThenBy(d => d.N)];
    }

    /// <summary>Генератор перед вызовом движка с номером <paramref name="counter"/> (строка журнала <paramref name="seq"/> — чтобы взять
    /// сев той же партии запуска). Нет сева или строк <c>rng</c> для подбора — null.</summary>
    public EngineRng? At(long seq, long counter)
    {
        if (_seeds.LastOrDefault(s => s.Seq < seq) is not { } seed) return null;
        var near = _draws.Where(d => d.Seq > seed.Seq && d.Seq < seq && d.N < counter).TakeLast(Anchors).ToList();
        return near.Count == 0 ? null : Match(seed, near, counter);
    }

    /// <summary>То же, но опор берётся столько последних, чтобы они несли не меньше <paramref name="minBits"/> бит (Σ log2 Max): короткие
    /// броски (номер сообщения <c>rng(5)</c>, вид <c>rng(3)</c>) при чужом сдвиге совпадают часто. Не набралось — null.</summary>
    public EngineRng? At(long seq, long counter, double minBits)
    {
        if (_seeds.LastOrDefault(s => s.Seq < seq) is not { } seed) return null;
        var near = new List<RngEvent>();
        double bits = 0;
        foreach (var d in _draws.Where(d => d.Seq > seed.Seq && d.Seq < seq && d.N < counter).Reverse())
        {
            near.Insert(0, d);
            bits += Math.Log2(Math.Max(d.Max, 1));
            if (bits >= minBits && near.Count >= Anchors) break;
        }
        return bits < minBits ? null : Match(seed, near, counter);
    }

    /// <summary>То же по броскам <b>после</b> вызова <paramref name="counter"/> (тот же сев): начало партии — до конца первого хода
    /// человека бросков журнал не пишет (карта 23, день 1: сев seq 452, первые <c>ai_rng</c> — уже в ходе ИИ). Сдвиг — тот, при котором
    /// сходятся ближайшие следующие броски, несущие не меньше <paramref name="minBits"/> бит (броски <c>rng(1)</c> найма лидера сходятся
    /// при любом сдвиге); отбраковки между вызовом и ними не предполагается. Нет — null.</summary>
    public EngineRng? AtAhead(long seq, long counter, double minBits = 24)
    {
        if (_seeds.LastOrDefault(s => s.Seq < seq) is not { } seed) return null;
        long nextSeed = _seeds.FirstOrDefault(s => s.Seq > seed.Seq)?.Seq ?? long.MaxValue;
        var next = new List<RngEvent>();
        double bits = 0;
        foreach (var d in _draws.Where(d => d.Seq > seed.Seq && d.Seq < nextSeed && d.N >= counter))
        {
            next.Add(d);
            bits += Math.Log2(Math.Max(d.Max, 1));
            if (bits >= minBits && next.Count >= Anchors) break;
        }
        if (bits < minBits) return null;
        var start = FromSeed(seed, (int)(counter - seed.N));
        for (int shift = 0; shift < MaxShift; shift++, start.Step())
        {
            var g = start.Clone();
            long pos = counter;
            bool ok = true;
            foreach (var d in next)
            {
                g.Advance((int)(d.N - pos));
                if (g.Next((int)d.Max) != d.R) { ok = false; break; }
                pos = d.N + 1;
            }
            if (ok) return start.Clone();
        }
        return null;
    }

    /// <summary>Генератор перед первым из бросков, записанных после строки <paramref name="seq"/> до <paramref name="until"/> (бой: все его
    /// броски журнал пишет строками <c>rng</c>). Опоры — от этого броска и дальше, пока не наберут <paramref name="minBits"/> бит (как у
    /// <see cref="AtAhead"/>): броски <c>rng(1)</c> (найм лидера, перемешивание из одного) сходятся при любом сдвиге, и сдвиг отбраковки,
    /// накопленный с сева, по ним не подбирается — карта 23, день 16: после боя <c>S143KC001a</c> с <c>S143KC0015</c> следующие броски —
    /// восемь <c>rng(1)</c> найма лидера, генератор вставал на 3 шага позади движка, и броски остатка дня (вор, дуэль) расходились.
    /// Нет — null.</summary>
    public EngineRng? BeforeDraws(long seq, long until, double minBits = 24)
    {
        if (_seeds.LastOrDefault(s => s.Seq < seq) is not { } seed) return null;
        long nextSeed = _seeds.FirstOrDefault(s => s.Seq > seed.Seq)?.Seq ?? long.MaxValue;
        var first = _draws.FirstOrDefault(d => d.Seq > seq && d.Seq < until);
        if (first is null) return null;
        var next = new List<RngEvent>();
        double bits = 0;
        foreach (var d in _draws.Where(d => d.Seq >= first.Seq && d.Seq < nextSeed && d.N >= first.N))
        {
            if (next.Count > 0 && d.N == next[^1].N) continue;   // бросок боя пишется и строкой rng, и строкой ai_rng
            next.Add(d);
            bits += Math.Log2(Math.Max(d.Max, 1));
            if (bits >= minBits && next.Count >= Anchors) break;
        }
        return next.Count == 0 ? null : Match(seed, next, first.N);   // конец журнала — по тому, что есть
    }

    /// <summary>Шагов между опорными состояниями генератора от сева (<see cref="FromSeed"/>).</summary>
    private const int MarkEvery = 1 << 16;

    /// <summary>Опорные состояния каждого сева (ключ — <c>seq</c> строки <c>rng_seed</c>): генератор через 0, <see cref="MarkEvery"/>,
    /// 2·<see cref="MarkEvery"/> … шагов.</summary>
    private readonly Dictionary<long, List<EngineRng>> _marks = [];

    /// <summary>Генератор сева <paramref name="seed"/>, продвинутый на <paramref name="steps"/> шагов (не больше нуля — сам сев): то же, что
    /// <c>EngineRng.FromSeed</c> и <c>Advance(steps)</c>, но от ближайшего опорного состояния. К концу длинной партии счётчик — миллионы
    /// вызовов, а подбор зовут на каждый выбор и ход ИИ (посуточные сверки, <c>DayCheck</c>): 20.10 перемотка от сева занимала ~50 с
    /// процессора прогона D2.Data.Tests.</summary>
    private EngineRng FromSeed(RngSeedEvent seed, int steps)
    {
        lock (_marks)
        {
            if (!_marks.TryGetValue(seed.Seq, out var marks))
                _marks[seed.Seq] = marks = [EngineRng.FromSeed(seed.Table, (int)seed.IndexAfter)];
            int mark = Math.Max(steps, 0) / MarkEvery;
            while (marks.Count <= mark)
            {
                var next = marks[^1].Clone();
                next.Advance(MarkEvery);
                marks.Add(next);
            }
            var g = marks[mark].Clone();
            g.Advance(steps - (mark * MarkEvery));
            return g;
        }
    }

    /// <summary>Сдвиг, при котором броски <paramref name="near"/> сходятся; генератор — перед вызовом <paramref name="counter"/>.</summary>
    private EngineRng? Match(RngSeedEvent seed, IReadOnlyList<RngEvent> near, long counter)
    {
        var start = FromSeed(seed, (int)(near[0].N - seed.N));
        for (int shift = 0; shift < MaxShift; shift++, start.Step())
        {
            var g = start.Clone();
            long pos = near[0].N;
            bool ok = true;
            foreach (var d in near)
            {
                g.Advance((int)(d.N - pos));
                if (g.Next((int)d.Max) != d.R) { ok = false; break; }
                pos = d.N + 1;
            }
            if (!ok) continue;
            if (counter >= pos)
            {
                g.Advance((int)(counter - pos));
                return g;
            }
            var back = start.Clone();
            back.Advance((int)(counter - near[0].N));
            return back;
        }
        return null;
    }
}
