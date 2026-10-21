using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace D2.Data.Corpus;

/// <summary>
/// Журналы зонда, разобранные один раз на процесс: тот же ответ, что у <see cref="CorpusReader"/>, но каждая строка разбирается
/// не больше одного раза, а повторные чтения — любой выборкой событий и <see cref="Battles"/> — идут из памяти.
/// <para><b>Зачем.</b> Харнессы тестов читали одни и те же большие журналы корпуса порознь: замер 16.10 (2108 тестов) — 4099 чтений,
/// 815 с потокового времени; <c>20260919-human/passive-001.jsonl</c> (84 МБ, 505 тыс. строк) в процессе D2.Rules.Tests читался 48 раз
/// (22 класса, 141 с). Дорог разбор строки (<see cref="System.Text.Json.JsonDocument"/>): проход по строкам без разбора — 0,15 с на этот
/// файл, разбор целиком — 1,2 с.</para>
/// <para><b>Как.</b> Первое обращение к файлу — один проход по байтам: где каждая непустая строка и какой у неё вид (<c>"ev"</c>).
/// Разбираются только запрошенные виды, каждый один раз, параллельно (строки дочитываются с диска по смещениям — кэш ОС). Харнессам
/// D2.Data.Tests из журнала нужна примерно четверть строк (16.10: 15–35 % у больших журналов), D2.Rules.Tests — почти все (бои).</para>
/// <para><b>Тот же ответ.</b> Порядок — порядок файла; строки делятся как у <see cref="File.ReadLines(string)"/> (<c>\n</c>, <c>\r\n</c>,
/// <c>\r</c>; метка UTF-8 в начале пропускается), пустые пропускаются; вид строки — первое <c>"ev":"…"</c> (<see cref="CorpusReader.EvOf"/>):
/// на всём корпусе 16.10 он совпал с поиском подстроки <see cref="CorpusReader.Read(string, string)"/> для каждого имени каждого файла.
/// Строка, которая не разбирается, запоминается с исключением: оно бросается там же, где бросил бы потоковый разбор, — при чтении этой
/// строки; выборка без неё его не видит. Отсутствующий файл и файл в UTF-16 не кэшируются: ответ — как у <see cref="CorpusReader"/>.</para>
/// <para><b>Ключ</b> — полный путь, время записи и длина (как у <c>SgFile.Open</c>): переписанный файл разбирается заново; переписанный
/// посреди разбора — исключение. События — неизменяемые записи, общий экземпляр делить безопасно. Кэш растёт до конца процесса (весь
/// корпус 16.10 целиком — ~1,1 ГБ), поэтому он для тестов и инструментов; игра читает журналы потоком (<see cref="CorpusReader"/>).</para>
/// </summary>
public static class JournalCache
{
    private static readonly ConcurrentDictionary<string, Lazy<Journal?>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Все события журнала в порядке файла (<see cref="CorpusReader.Read(string)"/>).</summary>
    public static IEnumerable<CorpusEvent> Read(string path) =>
        Of(path) is { } journal ? journal.All() : CorpusReader.Read(path);

    /// <summary>События одного вида в порядке файла (<see cref="CorpusReader.Read(string, string)"/>).</summary>
    public static IEnumerable<CorpusEvent> Read(string path, string ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (Of(path) is not { } journal) return CorpusReader.Read(path, ev);
        return journal.Ids.TryGetValue(ev, out int id) ? journal.Only(id) : [];
    }

    /// <summary>События из набора видов в порядке файла (<see cref="CorpusReader.Read(string, IReadOnlyCollection{string})"/>).</summary>
    public static IEnumerable<CorpusEvent> Read(string path, IReadOnlyCollection<string> evs)
    {
        ArgumentNullException.ThrowIfNull(evs);
        if (Of(path) is not { } journal) return CorpusReader.Read(path, evs);
        var mask = new bool[journal.Ids.Count];
        foreach (var ev in evs)
            if (journal.Ids.TryGetValue(ev, out int id)) mask[id] = true;
        return journal.Where(mask);
    }

    /// <summary>Бои журнала (<see cref="CorpusReader.Battles(string)"/>), собранные один раз на процесс.</summary>
    public static IReadOnlyList<Battle> Battles(string path) =>
        Of(path) is { } journal ? journal.Battles.Value : CorpusReader.Battles(path);

    /// <summary>Есть ли в журнале строка события <paramref name="ev"/> (без разбора — по указателю видов).</summary>
    public static bool Has(string path, string ev) => Count(path, ev) > 0;

    /// <summary>Сколько в журнале строк события <paramref name="ev"/> (без разбора).</summary>
    public static int Count(string path, string ev)
    {
        if (Of(path) is not { } journal) return CorpusReader.Read(path, ev).Count();
        return journal.Ids.TryGetValue(ev, out int id) ? journal.Postings[id].Length : 0;
    }

    /// <summary>Указатель журнала; файла нет или он не в UTF-8 — null (ответ и исключение тогда — как у потокового чтения).</summary>
    private static Journal? Of(string path)
    {
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || info.Length > Array.MaxLength) return null;   // больше массива — потоком, как прежде
        string key = $"{full}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        var lazy = Loaded.GetOrAdd(key, _ => new Lazy<Journal?>(() => Journal.Index(full, info.LastWriteTimeUtc, info.Length)));
        try { return lazy.Value; }
        catch
        {
            Loaded.TryRemove(new KeyValuePair<string, Lazy<Journal?>>(key, lazy));   // сбой чтения не запоминается: следующий вызов — заново
            throw;
        }
    }

    private sealed class Journal
    {
        private const int Chunk = 1024;

        private readonly string _path;
        private readonly DateTime _written;
        private readonly long _length;

        /// <summary>Смещение и длина в байтах каждой непустой строки.</summary>
        private readonly long[] _starts;
        private readonly int[] _lengths;

        /// <summary>Вид каждой строки — номер в <see cref="Ids"/>; строка без поля <c>ev</c> — последний номер (после всех видов).</summary>
        private readonly int[] _kinds;

        /// <summary>Место строки в <see cref="Postings"/> её вида.</summary>
        private readonly int[] _ranks;

        /// <summary>Разобранные строки вида (по <see cref="Postings"/>); null — вид ещё не разбирался.</summary>
        private readonly Parsed?[] _parsed;
        private readonly object _gate = new();

        public IReadOnlyDictionary<string, int> Ids { get; }

        /// <summary>Номера строк каждого вида по возрастанию; последний список — строки без <c>ev</c>.</summary>
        public int[][] Postings { get; }

        public Lazy<IReadOnlyList<Battle>> Battles { get; }

        private sealed record Parsed(CorpusEvent?[] Events, ExceptionDispatchInfo?[]? Errors);

        private Journal(string path, DateTime written, long length, List<long> starts, List<int> lengths, List<int> kinds,
                        Dictionary<string, int> ids)
        {
            _path = path;
            _written = written;
            _length = length;
            _starts = [.. starts];
            _lengths = [.. lengths];
            Ids = ids;
            int unnamed = ids.Count;
            _kinds = [.. kinds.Select(k => k < 0 ? unnamed : k)];
            var counts = new int[unnamed + 1];
            foreach (int k in _kinds) counts[k]++;
            Postings = [.. counts.Select(c => new int[c])];
            _ranks = new int[_kinds.Length];
            Array.Clear(counts);
            for (int i = 0; i < _kinds.Length; i++)
            {
                int k = _kinds[i];
                _ranks[i] = counts[k];
                Postings[k][counts[k]++] = i;
            }
            _parsed = new Parsed?[unnamed + 1];
            Battles = new Lazy<IReadOnlyList<Battle>>(() => CorpusReader.BattlesOf(All()));
        }

        /// <summary>Один проход по байтам файла: границы строк и их виды. UTF-16 (метка FF FE / FE FF) — null.</summary>
        public static Journal? Index(string path, DateTime written, long length)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && (bytes[0], bytes[1]) is (0xFF, 0xFE) or (0xFE, 0xFF)) return null;
            int at = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            var starts = new List<long>();
            var lengths = new List<int>();
            var kinds = new List<int>();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = ids.GetAlternateLookup<ReadOnlySpan<char>>();
            Span<char> name = stackalloc char[128];
            while (at < bytes.Length)
            {
                var rest = bytes.AsSpan(at);
                int end = rest.IndexOfAny((byte)'\n', (byte)'\r');
                int len = end < 0 ? rest.Length : end;
                if (len > 0)
                {
                    starts.Add(at);
                    lengths.Add(len);
                    kinds.Add(KindOf(rest[..len], ids, names, name));
                }
                at += len;
                if (end >= 0) at += rest[end] == '\r' && end + 1 < rest.Length && rest[end + 1] == '\n' ? 2 : 1;
            }
            return new Journal(path, written, length, starts, lengths, kinds, ids);
        }

        /// <summary>Вид строки — как <see cref="CorpusReader.EvOf"/> по символам (метка и кавычка — ASCII, в UTF-8 их байты однозначны).
        /// Нет поля — −1.</summary>
        private static int KindOf(ReadOnlySpan<byte> line, Dictionary<string, int> ids,
                                  Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> names, Span<char> buffer)
        {
            int start = line.IndexOf("\"ev\":\""u8);
            if (start < 0) return -1;
            var tail = line[(start + 6)..];
            int end = tail.IndexOf((byte)'"');
            if (end < 0) return -1;
            var raw = tail[..end];
            ReadOnlySpan<char> chars = raw.Length <= buffer.Length
                ? buffer[..Encoding.UTF8.GetChars(raw, buffer)]
                : Encoding.UTF8.GetString(raw);
            if (names.TryGetValue(chars, out int id)) return id;
            id = ids.Count;
            names[chars] = id;
            return id;
        }

        /// <summary>Разбирает ещё не разобранные виды из <paramref name="kinds"/> — разом, параллельно по строкам.</summary>
        private void Ensure(IReadOnlyCollection<int> kinds)
        {
            if (kinds.All(k => Volatile.Read(ref _parsed[k]) is not null)) return;
            lock (_gate)
            {
                var missing = kinds.Distinct().Where(k => _parsed[k] is null).ToArray();
                if (missing.Length == 0) return;
                var info = new FileInfo(_path);
                if (info.LastWriteTimeUtc != _written || info.Length != _length)
                    throw new InvalidOperationException($"{_path}: журнал переписан, пока был в кэше");
                var lines = missing.SelectMany(k => Postings[k]).Order().ToArray();
                var events = new CorpusEvent?[_parsed.Length][];
                foreach (int k in missing) events[k] = new CorpusEvent?[Postings[k].Length];
                var errors = new ExceptionDispatchInfo?[_parsed.Length][];
                using (var handle = File.OpenHandle(_path))
                {
                    try { Parallel.For(0, (lines.Length + Chunk - 1) / Chunk, c => ParseChunk(handle, lines, c, events, errors)); }
                    catch (AggregateException e) when (e.InnerExceptions.Count == 1)
                    {
                        ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();   // сбой чтения — то же исключение, что у потока
                    }
                }
                foreach (int k in missing)
                    Volatile.Write(ref _parsed[k], new Parsed(events[k], errors[k]));
            }
        }

        /// <summary>Разбор строк <paramref name="lines"/> одного куска. Строки куска идут подряд по файлу; если их общий отрезок
        /// невелик (<see cref="SpanLimit"/>), он читается одним вызовом, иначе — каждая строка своим.</summary>
        private void ParseChunk(SafeFileHandle handle, int[] lines, int chunk, CorpusEvent?[][] events, ExceptionDispatchInfo?[][] errors)
        {
            int first = chunk * Chunk, stop = Math.Min(lines.Length, first + Chunk);
            long from = _starts[lines[first]];
            long span = _starts[lines[stop - 1]] + _lengths[lines[stop - 1]] - from;
            bool whole = span <= SpanLimit;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(whole ? (int)span : 4096);
            try
            {
                if (whole) ReadExactly(handle, buffer.AsSpan(0, (int)span), from);
                for (int n = first; n < stop; n++)
                {
                    int i = lines[n], len = _lengths[i], kind = _kinds[i];
                    int offset = 0;
                    if (whole) offset = (int)(_starts[i] - from);
                    else
                    {
                        if (buffer.Length < len)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = ArrayPool<byte>.Shared.Rent(len);
                        }
                        ReadExactly(handle, buffer.AsSpan(0, len), _starts[i]);
                    }
                    try { events[kind][_ranks[i]] = CorpusReader.ParseLine(Encoding.UTF8.GetString(buffer, offset, len)); }
                    catch (Exception e)
                    {
                        lock (errors)
                        {
                            errors[kind] ??= new ExceptionDispatchInfo?[Postings[kind].Length];
                            errors[kind][_ranks[i]] = ExceptionDispatchInfo.Capture(e);
                        }
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private const int SpanLimit = 1 << 20;

        private void ReadExactly(SafeFileHandle handle, Span<byte> into, long offset)
        {
            for (int got = 0; got < into.Length;)
            {
                int read = RandomAccess.Read(handle, into[got..], offset + got);
                if (read == 0) throw new EndOfStreamException($"{_path}: журнал короче указателя");
                got += read;
            }
        }

        private CorpusEvent At(int i)
        {
            var parsed = _parsed[_kinds[i]]!;
            parsed.Errors?[_ranks[i]]?.Throw();
            return parsed.Events[_ranks[i]]!;
        }

        public IEnumerable<CorpusEvent> All()
        {
            Ensure([.. Enumerable.Range(0, _parsed.Length)]);
            for (int i = 0; i < _kinds.Length; i++) yield return At(i);
        }

        public IEnumerable<CorpusEvent> Only(int id)
        {
            Ensure([id]);
            foreach (int i in Postings[id]) yield return At(i);
        }

        /// <summary>Строки видов, отмеченных в <paramref name="mask"/> (по номеру в <see cref="Ids"/>), в порядке файла.</summary>
        public IEnumerable<CorpusEvent> Where(bool[] mask)
        {
            Ensure([.. Enumerable.Range(0, mask.Length).Where(k => mask[k])]);
            for (int i = 0; i < _kinds.Length; i++)
                if (_kinds[i] < mask.Length && mask[_kinds[i]]) yield return At(i);
        }
    }
}
