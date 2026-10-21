using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using D2.Data.Corpus;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Сохранение корпуса на оси журнала: файл, <c>seq</c> и <c>t</c> строки <c>save</c>, день (<c>-tNN-</c> имени) и загрузка карты.</summary>
internal sealed record SaveCopy(string Path, long Seq, long T, int Turn, int Epoch);

/// <summary>
/// Журнал зонда оригинала в части проверок действий (<c>*_check</c>, перенос юнитов, ответы ИИ на предложения) — один проход по файлу на
/// процесс тестов (<see cref="Load"/>), с отбором строк по имени события до разбора JSON (<see cref="JournalCache.Read(string, IReadOnlyCollection{string})"/>).
/// <para><b>Загрузка карты</b> (эпоха) — смена игрового потока, как у <c>TreasuryJournal</c> харнессов казны: первая строка — поток
/// интерфейса (<c>start</c>), каждая новая карта идёт в новом игровом потоке; сохранение берётся только из той же эпохи.</para>
/// <para><b>Сохранения</b> — строки <c>save</c> (<c>ok</c>, не временные) и файлы манифеста: поровну — по порядку (с проверкой дня),
/// иначе каждому файлу по возрастанию <c>mtime</c> — последняя свободная строка того же дня с <c>t ≤ mtime + 0,5 с</c> (то же правило
/// T02, что у <c>TreasuryJournal</c>, но день файла — из имени, без разбора сохранения). Файл без пары в ось не входит.</para>
/// </summary>
internal sealed partial class CheckJournal
{
    /// <summary>Строки, которые читает харнесс действий человека.</summary>
    private static readonly string[] Wanted =
    [
        "start", "save", "bank", "swap_check", "dismiss_check", "equip_check", "wand_check", "hire_check", "hire_leader_check",
        "grow_check", "rod_check", "merc_check", "sell_check", "unit_transfer", "ai_answer", "trade_accept", "rng_seed", "rng",
        "city_grow", "hire_unit", "rod_drop", "stack_move", "ai_break_check", "ai_offer_check", "ai_gift_check",
    ];

    private const long SlackMs = 500;

    public required string Path { get; init; }

    /// <summary>Проверки и действия по порядку <c>seq</c> (без <c>save</c>, <c>bank</c>, <c>rng</c>).</summary>
    public required IReadOnlyList<CorpusEvent> Events { get; init; }

    /// <summary>Эпоха каждой строки <see cref="Events"/> (по индексу).</summary>
    public required IReadOnlyList<int> Epochs { get; init; }

    public required IReadOnlyList<SaveCopy> Saves { get; init; }

    /// <summary>Строки <c>bank</c> основной казны (<c>mode 0</c>) по игроку — по возрастанию <c>seq</c>.</summary>
    public required IReadOnlyDictionary<string, List<BankEvent>> Banks { get; init; }

    /// <summary>Строки <c>rng_seed</c> и <c>rng</c> — для генератора движка в момент ответа ИИ (<see cref="JournalRng"/>).</summary>
    public required JournalRng Rng { get; init; }

    private static readonly ConcurrentDictionary<string, Lazy<CheckJournal>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static CheckJournal Load(string path) =>
        Loaded.GetOrAdd(System.IO.Path.GetFullPath(path), p => new Lazy<CheckJournal>(() => LoadCore(p))).Value;

    /// <summary>Последнее сохранение до строки <paramref name="index"/> в её эпохе.</summary>
    public SaveCopy? Before(int index) =>
        Saves.LastOrDefault(s => s.Seq < Events[index].Seq && s.Epoch == Epochs[index]);

    /// <summary>Первое сохранение после строки <paramref name="index"/> в её эпохе.</summary>
    public SaveCopy? After(int index) =>
        Saves.FirstOrDefault(s => s.Seq > Events[index].Seq && s.Epoch == Epochs[index]);

    /// <summary>Первая и последняя строка загрузки карты <paramref name="epoch"/> (по <see cref="Events"/> и сохранениям); загрузки нет —
    /// пустой интервал.</summary>
    public (long From, long To) EpochSeqs(int epoch)
    {
        var seqs = Events.Where((_, i) => Epochs[i] == epoch).Select(e => e.Seq).Concat(Saves.Where(s => s.Epoch == epoch).Select(s => s.Seq)).ToList();
        return seqs.Count == 0 ? (1, 0) : (seqs.Min(), seqs.Max());
    }

    /// <summary>Строки <see cref="Events"/> с <c>seq</c> в интервале (<paramref name="from"/>, <paramref name="to"/>) — действия между
    /// сохранением и проверкой.</summary>
    public IEnumerable<CorpusEvent> Between(long from, long to)
    {
        int lo = 0, hi = Events.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Events[mid].Seq <= from) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < Events.Count && Events[i].Seq < to; i++) yield return Events[i];
    }

    /// <summary>Казна игрока перед строкой <paramref name="seq"/> (последняя <c>bank</c> основной казны); нет — null.</summary>
    public Bank? BankAt(string player, long seq)
    {
        if (!Banks.TryGetValue(player, out var rows)) return null;
        int lo = 0, hi = rows.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (rows[mid].Seq < seq) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? null : rows[found].After;
    }

    private static CheckJournal LoadCore(string path)
    {
        var events = new List<CorpusEvent>();
        var epochs = new List<int>();
        var saves = new List<(SaveEvent Line, int Epoch)>();
        var banks = new Dictionary<string, List<BankEvent>>(StringComparer.OrdinalIgnoreCase);
        var rng = new List<CorpusEvent>();
        var anchors = new List<RngEvent>();
        var loads = new JournalLoads();
        int epoch = -1;
        foreach (var e in JournalCache.Read(path, Wanted))
        {
            if (loads.Opens(e)) epoch++;
            switch (e)
            {
                case StartEvent: break;
                case SaveEvent { Ok: true, Temp: false } s: saves.Add((s, epoch)); break;
                case SaveEvent: break;
                case BankEvent { Mode: 0 } b:
                    if (!banks.TryGetValue(b.Player, out var list)) banks[b.Player] = list = [];
                    list.Add(b);
                    break;
                case BankEvent: break;
                case RngSeedEvent or RngEvent: rng.Add(e); break;
                case AiBreakCheckEvent or AiOfferCheckEvent or AiGiftCheckEvent: anchors.AddRange(RollAnchors(e)); break;
                default:
                    anchors.AddRange(RollAnchors(e));
                    events.Add(e);
                    epochs.Add(epoch);
                    break;
            }
        }
        return new CheckJournal
        {
            Path = path, Events = events, Epochs = epochs, Banks = banks, Rng = new JournalRng(rng, anchors),
            Saves = Bind(Copies(path), saves),
        };
    }

    /// <summary>Броски дипломатии ИИ в полях строк — опоры <see cref="JournalRng"/> (до первого боя карты строк <c>rng</c> нет): проверки
    /// разрыва, предложения и подарка (<c>rng(100)</c>, вид подарка — <c>rng(3)</c> следующим вызовом) и ответы (<c>rng(100)</c>); номер
    /// вызова — <c>rng_before + 1</c>, как у опор <c>AiDiplomacyHarnessTests</c>. <c>−1</c> — бросок вытеснен из кольца зонда.</summary>
    private static IEnumerable<RngEvent> RollAnchors(CorpusEvent e)
    {
        switch (e)
        {
            case AiBreakCheckEvent { Roll: >= 0 } b: yield return new RngEvent(b.Seq, b.Tid, b.RngBefore + 1, 100, b.Roll.Value, false); break;
            case AiOfferCheckEvent { Roll: >= 0 } o: yield return new RngEvent(o.Seq, o.Tid, o.RngBefore + 1, 100, o.Roll.Value, false); break;
            case AiGiftCheckEvent g:
                if (g.Roll is >= 0) yield return new RngEvent(g.Seq, g.Tid, g.RngBefore + 1, 100, g.Roll.Value, false);
                if (g.KindRoll is >= 0) yield return new RngEvent(g.Seq, g.Tid, g.RngBefore + 2, 3, g.KindRoll.Value, false);
                break;
            case AiAnswerEvent { Roll: >= 0 } a: yield return new RngEvent(a.Seq, a.Tid, a.RngBefore + 1, 100, a.Roll.Value, false); break;
        }
    }

    [GeneratedRegex(@"-t(?<turn>\d+)-(?<n>\d+)\.sg$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyName();

    /// <summary>Файлы манифеста про этот журнал: путь, <c>mtime</c> от <c>started_at</c> (мс), день и номер из имени.</summary>
    private static List<(string Path, long Mtime, int Turn, int N)> Copies(string journal)
    {
        string dir = System.IO.Path.GetDirectoryName(journal) ?? ".";
        string manifest = System.IO.Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        string name = System.IO.Path.GetFileName(journal);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("file", out var f) || !name.Equals(f.GetString(), StringComparison.OrdinalIgnoreCase)
                || !row.TryGetProperty("started_at", out var st) || !row.TryGetProperty("saves", out var list)) continue;
            var start = DateTimeOffset.Parse(st.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var copies = new List<(string, long, int, int)>();
            foreach (var s in list.EnumerateArray())
            {
                string file = System.IO.Path.Combine(dir, s.ValueKind == JsonValueKind.String ? s.GetString()! : s.GetProperty("file").GetString()!);
                var m = CopyName().Match(file);
                if (!File.Exists(file) || !m.Success) continue;
                long mtime = (long)(new DateTimeOffset(File.GetLastWriteTimeUtc(file)) - start).TotalMilliseconds;
                copies.Add((file, mtime, int.Parse(m.Groups["turn"].Value, System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
            return copies;
        }
        return [];
    }

    private static List<SaveCopy> Bind(List<(string Path, long Mtime, int Turn, int N)> copies, List<(SaveEvent Line, int Epoch)> saves)
    {
        var byN = copies.OrderBy(c => c.N).ToList();
        if (byN.Count == saves.Count && byN.Select((c, i) => c.Turn == saves[i].Line.Turn).All(x => x))
            return [.. byN.Select((c, i) => new SaveCopy(c.Path, saves[i].Line.Seq, saves[i].Line.T, c.Turn, saves[i].Epoch))];
        var used = new HashSet<long>();
        var bound = new List<SaveCopy>();
        foreach (var c in copies.OrderBy(c => c.Mtime))
        {
            var hit = saves.Where(s => s.Line.T <= c.Mtime + SlackMs && s.Line.Turn == c.Turn && !used.Contains(s.Line.Seq))
                           .Select(s => ((SaveEvent, int)?)s).LastOrDefault();
            if (hit is not { } h) continue;
            used.Add(h.Item1.Seq);
            bound.Add(new SaveCopy(c.Path, h.Item1.Seq, h.Item1.T, c.Turn, h.Item2));
        }
        return [.. bound.OrderBy(s => s.Seq)];
    }
}
