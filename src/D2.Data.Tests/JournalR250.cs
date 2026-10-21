using D2.Data.Corpus;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>
/// Поток R250 движка по журналу схемы 7.5: строки <c>ai_rng</c> (<c>kind</c> <c>r250</c>, все решения игры вне показа и боя) и
/// <c>rng</c> (бой) вместе дают КАЖДЫЙ вызов обёртки <c>0x54851f</c> партии с его границей и ответом — номера <c>n</c> идут подряд
/// (<see cref="Gaps"/>). До первой строки партии генератор тратит показ и создание карты (грунт <c>CIsoEngineGround</c>, 456 192 вызова
/// у карт 16–18) — их границ журнал не пишет, поэтому сдвиг от строки <c>rng_seed</c> подбирается по первым строкам (как
/// <see cref="JournalRng"/>), а дальше генератор порта (<see cref="EngineRng.Next"/>) повторяет каждый вызов движка с его границей:
/// ответ порта против <c>r</c> журнала (<see cref="Mismatches"/>). Так у харнесса есть точное состояние движка перед любым вызовом
/// (<see cref="Before"/>) — решение порта можно пустить с него и сверить состояние после (<see cref="After"/>).
/// <para>Партия — от строки <c>rng_seed</c> до следующей или до входа в загруженную партию (<c>game_phase load</c>: загрузка пишет
/// таблицу из сохранения, строки <c>rng_seed</c> у неё нет) — строки после загрузки в <see cref="AfterLoad"/>, не повторяются.</para>
/// </summary>
internal sealed class JournalR250
{
    /// <summary>Наибольший сдвиг шагов (отбраковка хвоста <c>0x6781dd</c> на невидимых вызовах показа) против числа вызовов.</summary>
    private const int MaxShift = 2000;

    /// <summary>Сколько бит (Σ log2 max) первых строк должно сойтись, чтобы сдвиг считался найденным.</summary>
    private const double FitBits = 64;

    private readonly Dictionary<long, EngineRng> _before = [];
    private readonly Dictionary<long, EngineRng> _after = [];

    /// <summary>Вызов R250 партии: номер, граница, ответ движка, место (<c>battle</c> — строка <c>rng</c> боя) и строка журнала.</summary>
    public sealed record Draw(long Seq, long N, int Max, int R, string Site, string? Origin, string? Player);

    /// <summary>Все вызовы журнала по порядку номеров (без повторов: бросок места в бою пишут обе строки).</summary>
    public IReadOnlyList<Draw> Draws { get; }

    /// <summary>Разрывы нумерации внутри партии: вызовы, которых журнал не написал (место вне списка 7.5).</summary>
    public int Gaps { get; private set; }

    /// <summary>Партий, у которых сдвиг от сева не нашёлся.</summary>
    public int Unfitted { get; private set; }

    /// <summary>Вызовов, повторённых генератором порта.</summary>
    public int Replayed { get; private set; }

    /// <summary>Вызовы, где ответ порта не равен ответу движка.</summary>
    public List<(Draw Draw, int Port)> Mismatches { get; } = [];

    /// <summary>Вызовы загруженных партий (состояние — из сохранения, не повторяются).</summary>
    public int AfterLoad { get; private set; }

    /// <summary>Строки новой партии до создания карты (перед разрывом невидимых вызовов грунта), с которых сдвиг не находится — не
    /// повторяются.</summary>
    public int BeforeMap { get; private set; }

    public JournalR250(IEnumerable<CorpusEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.OrderBy(e => e.Seq).ToList();
        var seeds = list.OfType<RngSeedEvent>().ToList();
        var loads = list.OfType<GamePhaseEvent>().Where(p => p.What == "load").Select(p => p.Seq).ToList();
        var draws = new SortedDictionary<long, Draw>();
        foreach (var e in list)
            switch (e)
            {
                case AiRngEvent { Kind: "r250" } a:
                    draws[a.N] = new Draw(a.Seq, a.N, (int)a.Max, (int)a.R, a.Site, a.Origin, a.Player);
                    break;
                case RngEvent { Std: false } r when !draws.ContainsKey(r.N):
                    draws[r.N] = new Draw(r.Seq, r.N, (int)r.Max, (int)r.R, "battle", null, null);
                    break;
            }
        Draws = [.. draws.Values.OrderBy(d => d.Seq)];
        for (int s = 0; s < seeds.Count; s++)
        {
            long from = seeds[s].Seq, to = s + 1 < seeds.Count ? seeds[s + 1].Seq : long.MaxValue;
            long load = loads.FirstOrDefault(l => l > from && l < to, to);
            AfterLoad += Draws.Count(d => d.Seq > load && d.Seq < to);
            Replay(seeds[s], [.. Draws.Where(d => d.Seq > from && d.Seq < load)]);
        }
    }

    /// <summary>Состояние движка перед вызовом <paramref name="n"/> (номер строки журнала); вызова нет в повторе — null.</summary>
    public EngineRng? Before(long n) => _before.TryGetValue(n, out var g) ? g.Clone() : null;

    /// <summary>Состояние движка после вызова <paramref name="n"/>; null — вызова нет в повторе.</summary>
    public EngineRng? After(long n) => _after.TryGetValue(n, out var g) ? g.Clone() : null;

    /// <summary>Совпадают ли состояния генератора (индекс и таблица).</summary>
    public static bool Same(EngineRng a, EngineRng b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Index == b.Index && a.TableSnapshot().AsSpan().SequenceEqual(b.TableSnapshot());
    }

    private void Replay(RngSeedEvent seed, IReadOnlyList<Draw> draws)
    {
        if (draws.Count == 0) return;
        var g = Fit(seed, draws);
        // Новая партия: шанс события вступления (n = 1) — до создания карты, затем грунт карты тратит сотни тысяч невидимых вызовов
        // (20261019c-cov: n 1 → 202754). Если с первой строки сдвиг не находится, повтор идёт с первого отрезка после разрыва (только после первого: дальше — «без сдвига»).
        if (g is null && Enumerable.Range(1, draws.Count - 1).FirstOrDefault(i => draws[i].N != draws[i - 1].N + 1) is var k and > 0)
        {
            g = Fit(seed, [.. draws.Skip(k)]);
            if (g is not null)
            {
                BeforeMap += k;
                draws = [.. draws.Skip(k)];
            }
        }
        for (int i = 1; i < draws.Count; i++)
            if (draws[i].N != draws[i - 1].N + 1) Gaps++;
        if (g is null)
        {
            Unfitted++;
            return;
        }
        long expected = draws[0].N;
        foreach (var d in draws)
        {
            if (d.N != expected)
            {
                // Невидимые вызовы (разрыв) — их границ нет: дальше не повторять, иначе состояние неверно.
                return;
            }
            _before[d.N] = g.Clone();
            int port = g.Next(d.Max);
            _after[d.N] = g.Clone();
            Replayed++;
            if (port != d.R) Mismatches.Add((d, port));
            expected = d.N + 1;
        }
    }

    /// <summary>Генератор перед первым вызовом <paramref name="draws"/>: от сева — число невидимых вызовов плюс сдвиг, при котором
    /// первые строки (не меньше <see cref="FitBits"/> бит) повторяются подряд.</summary>
    private static EngineRng? Fit(RngSeedEvent seed, IReadOnlyList<Draw> draws)
    {
        var start = EngineRng.FromSeed(seed.Table, (int)seed.IndexAfter);
        long hidden = draws[0].N - seed.N - 1;
        start.Advance((int)Math.Max(hidden - 2, 0));
        var anchors = new List<Draw>();
        double bits = 0;
        foreach (var d in draws)
        {
            if (anchors.Count > 0 && d.N != anchors[^1].N + 1) break;
            anchors.Add(d);
            bits += Math.Log2(Math.Max(d.Max, 1));
            if (bits >= FitBits) break;
        }
        if (bits < FitBits / 2) return null;
        for (int shift = 0; shift < MaxShift; shift++, start.Step())
        {
            var g = start.Clone();
            if (anchors.All(a => g.Next(a.Max) == a.R)) return start.Clone();
        }
        return null;
    }
}
