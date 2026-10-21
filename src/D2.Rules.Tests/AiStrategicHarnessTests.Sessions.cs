using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using D2.Data.Corpus;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Сохранение драйвера на оси <c>seq</c>: строка <c>save</c> и копия из манифеста (<c>null</c> — копии нет).</summary>
public sealed record AiSave(long Seq, long Turn, string Name, string? File);

/// <summary>
/// Сессия журнала — одна загрузка карты: строки от первой строки нового логического потока до последней перед
/// следующей загрузкой. <see cref="Name"/> — «прогон#номер карта»: имя карты одно и то же в разных прогонах и в разных
/// загрузках одной партии, ключ сессии — (прогон, номер).
/// </summary>
/// <param name="AiTurns">В сессии закончил ход (<c>ai_turn</c> <c>end</c>, <c>cause</c> <c>ai</c>) хоть один игрок ИИ, не
/// нейтрал: раса игрока — из <c>terrain_day</c> той же сессии (<c>race</c> 4 — нейтрал; раса неизвестна — считается ИИ).
/// Нейтрал ходит через тот же <c>ai_turn</c>, но стратегии ИИ у него нет: карты Ф7 только с нейтралами (20260919-f7
/// m7/m12/m6/m17) сравнений ИИ не дают по самим данным. Начатый и не законченный ход (падение f7-m15-diplo на первом
/// ходе ИИ, seq 674751) ходом не считается.</param>
public sealed record AiSession(string Run, string Journal, int Index, long Tid, long First, long Last, string Map,
                               IReadOnlyList<AiSave> Saves, bool AiTurns = true)
{
    public string Name => $"{Run}#{Index} {Map}";

    /// <summary>Группа сверки — карта прогона (все загрузки одной партии вместе).</summary>
    public string Group => $"{Run} {Map}";

    public bool Contains(long seq) => seq >= First && seq <= Last;

    /// <summary>Последнее сохранение сессии с копией раньше <paramref name="seq"/> (того же дня, если он задан) — снимок
    /// на момент события. В партии человека дня бывает несколько: быстрые сохранения посреди хода и автосохранение в его
    /// конце, прямо перед ходами ИИ; берётся ближайшее.</summary>
    public AiSave? SaveBefore(long seq, long? day = null) =>
        Saves.LastOrDefault(s => s.File is not null && s.Seq < seq && (day is null || s.Turn == day));
}

/// <summary>
/// Сессии прогонов зонда 6.5+ (в строке <c>start</c> среди хуков есть <c>ai_tactic_candidate</c>) — из самих данных, без
/// констант <c>seq</c>. Правило то же, что у <c>docs/synthetic-maps/journal_maps.py</c> (disciples-modding): поток первой
/// строки — поток интерфейса; строка с другим потоком, не равным текущему игровому, открывает новую загрузку карты.
/// Копия сохранения из манифеста привязывается к последней ещё не занятой строке <c>save</c> (<c>ok</c>, не временной) с
/// <c>t ≤ mtime − started_at + 0,5 с</c> — и только если день строки равен <c>tNN</c> имени файла.
/// </summary>
public static class AiCorpus
{
    private const long SlackMs = 500;
    private const string ProbeMarker = "\"ai_tactic_candidate\"";
    private static readonly Regex Tail = new(@"(-(human|heretic|elf|undead|dwarf))+-t\d\d-\d{3}\.sg$", RegexOptions.IgnoreCase);
    private static readonly Regex FileDay = new(@"-t(\d+)-\d+\.sg$", RegexOptions.IgnoreCase);
    private static readonly Regex Player = new("\"player\":\"([^\"]+)\"");
    private static readonly Regex PlayerRace = new("\"player\":\"([^\"]+)\".*?\"race\":(\\d+)");
    private const int NeutralRace = 4;   // Grace.RACE_TYPE нейтралов (terrain_day.race)

    private static readonly Lazy<(IReadOnlyList<AiSession> Sessions, IReadOnlyList<Unbound> Unbound)> All = new(Discover);

    /// <summary>Сессии, по которым идут сверки: те, у которых есть сохранение, то есть известна карта. Сессия без
    /// сохранений — это срез экранов интерфейса (<c>corpus/2026092*-ui</c>, памятки <c>docs/PLAY-ui-screens*.md</c>):
    /// карту по ней не восстановить, сверять нечего. Такие печатаются отдельно (<see cref="Skipped"/>), молча не
    /// пропадают.</summary>
    public static IReadOnlyList<AiSession> Sessions => [.. All.Value.Sessions.Where(s => s.Saves.Count > 0)];

    /// <summary>Сессии без сохранений — срез интерфейса; в сверки правил не идут.</summary>
    public static IReadOnlyList<AiSession> Skipped => [.. All.Value.Sessions.Where(s => s.Saves.Count == 0)];

    /// <summary>Копия манифеста, которую не удалось привязать к строке <c>save</c>, и почему.</summary>
    public sealed record Unbound(string Journal, string File, string Why);

    /// <summary>Непривязанные копии — не молча отброшенные, а с причиной.</summary>
    public static IReadOnlyList<Unbound> UnboundCopies => All.Value.Unbound;

    /// <summary>События из <paramref name="wanted"/> по сессиям, каждая по <c>seq</c>; строки вне сессий отброшены.</summary>
    public static List<(AiSession Session, List<CorpusEvent> Events)> Split(IReadOnlyCollection<string> wanted)
    {
        var result = new List<(AiSession, List<CorpusEvent>)>();
        foreach (var journal in Sessions.GroupBy(s => s.Journal))
        {
            var events = JournalCache.Read(journal.Key, wanted).OrderBy(e => e.Seq).ToList();
            foreach (var s in journal)
                result.Add((s, events.Where(e => s.Contains(e.Seq)).ToList()));
        }
        return result;
    }

    /// <summary>
    /// Проверки, которые даёт любой ход ИИ со стратегией: <c>StepChoice</c> считает их на каждом проходе и каждом ходе,
    /// где есть хоть одна строка <c>ai_eval</c>/<c>ai_candidate</c> (пустой проход тоже: <c>idx</c> [] подряд, ранги []
    /// по порядку, проигрыш хода с пустым последним проходом). По корпусу 20260918-probe65 … 20260920-gaps каждый
    /// законченный ход ИИ-не-нейтрала с <c>ai_eval</c> — 408 из 417; остальные 9 — S073PL000c 20260919-human дни 62–70
    /// (игрок без стратегии, в ходе только тактика), поэтому требование — на карту прогона, а не на каждый ход.
    /// Прочее (найм лидера, исследование, жезлы, постройки…) зависит от карты: на малых синтетических картах
    /// (20260919-f7b, 20260920-gaps) ИИ ходит, а лидеров не нанимает, не исследует и жезлов не ставит.
    /// </summary>
    public static readonly IReadOnlySet<string> Core = new HashSet<string>(StringComparer.Ordinal)
        { "step.idx_contiguous", "step.list_order", "step.replay" };

    /// <summary>Приёмка: ни одного расхождения ни в одной сессии и хоть одно сравнение во всём корпусе; для проверок
    /// <see cref="Core"/> — ещё и хоть одно сравнение в каждой группе (карта прогона), где ИИ сделал ход
    /// (<see cref="AiSession.AiTurns"/>): ход ИИ без единого стратегического сравнения — провал. Сессия без событий
    /// проверки (короткая загрузка) требования не нарушает.</summary>
    public static void Require(Func<AiSession, (int Ok, int Bad)> of, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var s in Sessions)
                Assert.True(of(s).Bad == 0, $"{s.Name} {name}: разошлось {of(s).Bad} — см. РАСХОЖДЕНИЕ выше");
            Assert.True(Sessions.Sum(s => of(s).Ok) > 0, $"{name}: ноль сравнений во всём корпусе — сверка не состоялась");
            if (!Core.Contains(name)) continue;
            foreach (var g in Sessions.GroupBy(s => s.Group).Where(g => g.Any(s => s.AiTurns)))
                Assert.True(g.Sum(s => of(s).Ok + of(s).Bad) > 0, $"{g.Key} {name}: ноль сравнений — ИИ ходил, а сверка не состоялась");
        }
    }

    /// <summary>То же для счётчиков, заведённых на сессию.</summary>
    public static void RequireEach(IReadOnlyDictionary<AiSession, Counters> bySession, params string[] names)
    {
        foreach (var name in names)
            Require(s => bySession.TryGetValue(s, out var c) ? c.Of(name) : default, name);
    }

    /// <summary>Таблица сессий — для вывода харнесса.</summary>
    public static void Print(ITestOutputHelper o)
    {
        foreach (var s in Sessions)
            o.WriteLine($"{s.Name}: tid {s.Tid}, seq {s.First}–{s.Last}, сохранений {s.Saves.Count}, с копией {s.Saves.Count(x => x.File is not null)}" +
                        (s.Saves.Count > 0 ? $", дни {s.Saves.Min(x => x.Turn)}–{s.Saves.Max(x => x.Turn)}" : "") +
                        (s.AiTurns ? "" : ", ходов ИИ нет — сравнения не обязательны"));
        foreach (var s in Skipped)
            o.WriteLine($"{s.Name}: tid {s.Tid}, seq {s.First}–{s.Last}, сохранений нет — срез интерфейса, не сверяется");
        foreach (var u in UnboundCopies)
            o.WriteLine($"не привязана {Path.GetFileName(u.Journal)} {u.File}: {u.Why}");
    }

    private static (IReadOnlyList<AiSession>, IReadOnlyList<Unbound>) Discover()
    {
        var sessions = new List<AiSession>();
        var unbound = new List<Unbound>();
        if (!Directory.Exists(CorpusFiles.Dir)) return (sessions, unbound);
        var jobs = new List<(string Run, string Journal, List<string> Listed, DateTimeOffset Start)>();
        foreach (var dir in Directory.EnumerateDirectories(CorpusFiles.Dir).Order(StringComparer.Ordinal))
        {
            string run = Path.GetFileName(dir);
            string manifest = Path.Combine(dir, "manifest.json");
            if (CorpusFiles.Excluded(run) || !File.Exists(manifest)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("file", out var f) || !row.TryGetProperty("started_at", out var st)) continue;
                string journal = Path.Combine(dir, f.GetString()!);
                if (!File.Exists(journal) || !(File.ReadLines(journal).FirstOrDefault()?.Contains(ProbeMarker, StringComparison.Ordinal) ?? false))
                    continue;
                var start = DateTimeOffset.Parse(st.GetString()!, CultureInfo.InvariantCulture);
                var listed = row.TryGetProperty("saves", out var sv)
                    ? sv.EnumerateArray().Select(x => Path.Combine(dir, x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetProperty("file").GetString()!)).ToList()
                    : [];
                jobs.Add((run, journal, listed, start));
            }
        }
        // Журналы делятся на сессии параллельно (каждый — проход по всем строкам; 20.10 — 2 ГБ корпуса, все харнессы ИИ ждали этот
        // список), а сессии и непривязанные копии складываются в прежнем порядке.
        var split = JournalSet.Parallel(jobs, j =>
        {
            var own = new List<Unbound>();
            var found = SplitJournal(j.Run, j.Journal, j.Listed.Where(File.Exists).ToList(), j.Start, own).ToList();
            return (Found: found, Unbound: own);
        });
        for (int i = 0; i < jobs.Count; i++)
        {
            var (run, journal, listed, _) = jobs[i];
            unbound.AddRange(listed.Where(p => !File.Exists(p)).Select(p => new Unbound(journal, Path.GetFileName(p), "файла копии нет")));
            unbound.AddRange(split[i].Unbound);
            // Номер сессии сквозной по прогону: у 20260919-f7 два журнала, и «#1» было бы у двух карт.
            int before = sessions.Count(s => s.Run == run);
            sessions.AddRange(split[i].Found.Select(s => s with { Index = s.Index + before }));
        }
        return (sessions, unbound);
    }

    private static IEnumerable<AiSession> SplitJournal(string run, string journal, List<string> copies, DateTimeOffset start,
                                                       List<Unbound> unbound)
    {
        var bounds = new List<(long Tid, long First, long Last)>();
        var races = new List<Dictionary<string, int>>();      // по сессиям: игрок -> раса из terrain_day
        var aiEnded = new List<HashSet<string>>();            // по сессиям: игроки, закончившие ход ИИ
        var saves = new List<SaveEvent>();
        var loads = new JournalLoads();
        foreach (var line in File.ReadLines(journal))
        {
            if (line.Length == 0) continue;
            var (seq, tid) = Head(line);
            if (loads.Opens(tid, line))
            {
                bounds.Add((tid, seq, seq));
                races.Add(new(StringComparer.Ordinal));
                aiEnded.Add(new(StringComparer.Ordinal));
            }
            if (bounds.Count == 0) continue;
            bounds[^1] = bounds[^1] with { Last = Math.Max(bounds[^1].Last, seq) };
            if (line.Contains("\"ev\":\"terrain_day\"", StringComparison.Ordinal) && PlayerRace.Match(line) is { Success: true } pr)
                races[^1][pr.Groups[1].Value] = int.Parse(pr.Groups[2].Value, CultureInfo.InvariantCulture);
            if (line.Contains("\"ev\":\"ai_turn\",\"phase\":\"end\"", StringComparison.Ordinal)
                && line.Contains("\"cause\":\"ai\"", StringComparison.Ordinal) && Player.Match(line) is { Success: true } pl)
                aiEnded[^1].Add(pl.Groups[1].Value);
            if (line.Contains("\"ev\":\"save\"", StringComparison.Ordinal) && Save(line) is { Ok: true, Temp: false } s)
                saves.Add(s);
        }
        var files = Bind(journal, copies, saves, start, unbound);
        for (int i = 0; i < bounds.Count; i++)
        {
            var (tid, first, last) = bounds[i];
            var own = saves.Where(s => s.Seq >= first && s.Seq <= last).OrderBy(s => s.Seq)
                .Select(s => new AiSave(s.Seq, s.Turn, s.Name, files.GetValueOrDefault(s.Seq))).ToList();
            var maps = own.Where(s => s.File is not null).Select(s => Tail.Replace(Path.GetFileName(s.File!), "")).Distinct().ToList();
            string map = maps.Count == 1 ? maps[0] : maps.Count == 0 ? "?" : string.Join("+", maps);
            bool aiTurns = aiEnded[i].Any(p => races[i].GetValueOrDefault(p, -1) != NeutralRace);
            yield return new AiSession(run, journal, i + 1, tid, first, last, map, own, aiTurns);
        }
    }

    /// <summary>Копия → <c>seq</c> строки <c>save</c>: по возрастанию <c>mtime</c> последняя незанятая строка ТОГО ЖЕ
    /// дня, что <c>tNN</c> имени, с <c>t ≤ mtime + 0,5 с</c>. День — в условии поиска, а не проверкой после: сторож
    /// опрашивает раз в 2 с, и копия дня бывает записана уже после следующего сохранения (20260919-f7, f7-m6-rods
    /// t07-031: mtime на 0,4 с позже быстрого сохранения дня 8, seq 578995) — последняя строка вообще была бы чужого
    /// дня, и копия пропадала молча. Непривязанная копия уходит в <paramref name="unbound"/> с причиной.</summary>
    private static Dictionary<long, string> Bind(string journal, List<string> copies, List<SaveEvent> saves, DateTimeOffset start,
                                                 List<Unbound> unbound)
    {
        var bound = new Dictionary<long, string>();
        foreach (var (mt, path) in copies.Select(p => ((long)(new DateTimeOffset(File.GetLastWriteTimeUtc(p)) - start).TotalMilliseconds, p))
                                         .OrderBy(x => x.Item1))
        {
            var day = FileDay.Match(path);
            if (!day.Success) { unbound.Add(new(journal, Path.GetFileName(path), "в имени нет дня tNN")); continue; }
            long turn = long.Parse(day.Groups[1].Value, CultureInfo.InvariantCulture);
            var hit = saves.Where(s => s.Turn == turn && s.T <= mt + SlackMs && !bound.ContainsKey(s.Seq)).MaxBy(s => s.Seq);
            if (hit is null)
            {
                unbound.Add(new(journal, Path.GetFileName(path),
                    $"нет свободной строки save дня {turn} с t ≤ mtime {mt} мс + {SlackMs} (строк дня {saves.Count(s => s.Turn == turn)})"));
                continue;
            }
            bound[hit.Seq] = path;
        }
        // Порядок сохраняется: внутри дня k-я по mtime копия — k-я по seq из выбранных строк (20261016e-av, карта 7: t01-050 и t01-051
        // по «последней незанятой» менялись строками).
        var mtimes = copies.ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p), StringComparer.OrdinalIgnoreCase);
        var turnOf = saves.ToDictionary(s => s.Seq, s => s.Turn);
        return bound.GroupBy(b => turnOf[b.Key])
            .SelectMany(g => g.Select(b => b.Key).Order().Zip(g.Select(b => b.Value).OrderBy(p => mtimes[p])))
            .ToDictionary(x => x.First, x => x.Second);
    }

    private static SaveEvent Save(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var e = doc.RootElement;
        return new SaveEvent(e.GetProperty("seq").GetInt64(), e.GetProperty("tid").GetInt64(), e.GetProperty("t").GetInt64(),
            e.GetProperty("name").GetString() ?? "", e.GetProperty("temp").GetBoolean(), e.GetProperty("turn").GetInt64(),
            e.GetProperty("ok").GetBoolean(), e.TryGetProperty("seq_end", out var se) ? se.GetInt64() : 0);
    }

    /// <summary><c>seq</c> и <c>tid</c> из начала строки <c>{"seq":N,"tid":T,…</c> — без разбора JSON.</summary>
    private static (long Seq, long Tid) Head(string line)
    {
        static long After(string l, string key)
        {
            int i = l.IndexOf(key, StringComparison.Ordinal) + key.Length;
            int j = i;
            while (j < l.Length && char.IsAsciiDigit(l[j])) j++;
            return long.Parse(l.AsSpan(i, j - i), CultureInfo.InvariantCulture);
        }
        return (After(line, "\"seq\":"), After(line, "\"tid\":"));
    }
}

public partial class AiStrategicHarnessTests
{
    /// <summary>Сессии нарезаны по данным: у каждой есть события и карта, каждая копия манифеста либо привязана к строке
    /// <c>save</c> своего дня, либо названа непривязанной с причиной (молча не пропадает ни одна), сессии одного журнала
    /// не пересекаются.</summary>
    [SkippableFact]
    public void Sessions_SplitFromJournalData()
    {
        var sessions = AiCorpus.Sessions;
        Skip.If(sessions.Count == 0, $"нет журналов зонда 6.5+ в {CorpusFiles.Dir}");
        AiCorpus.Print(output);
        foreach (var s in sessions)
        {
            Assert.True(s.Last > s.First, $"{s.Name}: пустая сессия");
            Assert.DoesNotContain("?", s.Map);
            Assert.DoesNotContain("+", s.Map);
        }
        foreach (var j in sessions.GroupBy(s => s.Journal))
        {
            var list = j.ToList();
            for (int i = 1; i < list.Count; i++) Assert.True(list[i - 1].Last < list[i].First, $"{list[i].Name}: сессии пересекаются");
            var manifest = Path.Combine(Path.GetDirectoryName(j.Key)!, "manifest.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            int copies = doc.RootElement.EnumerateArray()
                .Where(r => r.GetProperty("file").GetString() == Path.GetFileName(j.Key))
                .Sum(r => r.GetProperty("saves").GetArrayLength());
            int bound = list.Sum(s => s.Saves.Count(x => x.File is not null));
            int unbound = AiCorpus.UnboundCopies.Count(u => u.Journal == j.Key);
            Assert.True(copies == bound + unbound, $"{j.Key}: в манифесте {copies}, привязано {bound}, с причиной {unbound}");
        }
    }
}
