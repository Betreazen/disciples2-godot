using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>Счётчики после чтения карты против <c>id_alloc</c> движка (<c>docs/new-game.md</c> §6).</summary>
public sealed partial class IdAllocHarnessTests
{
    /// <summary>Журналы схемы 7.2+ (со строками <c>id_alloc</c>) и сохранения при них.</summary>
    private static readonly (string Dir, string File)[] AllocJournals =
    [
        ("20260930-probe72", "passive-001.jsonl"), ("20261014-replay", "passive-001.jsonl"), ("20261014-replay", "passive-002.jsonl"),
        ("20261016-port-synth", "passive-001.jsonl"), ("20261016-port-synth", "passive-002.jsonl"),
        ("20261016b-port-synth", "passive-001.jsonl"), ("20261016c-port-synth", "passive-001.jsonl"),
        ("20261016d-port-synth", "passive-001.jsonl"), ("20261016e-av", "passive-001.jsonl"), ("20261016g-port-synth", "passive-001.jsonl"),
        ("20261017-port-synth", "passive-001.jsonl"), ("20261018-port-synth", "passive-001.jsonl"), ("20261019c-cov", "passive-001.jsonl"),
        ("20261020-port-synth", "passive-001.jsonl"),
    ];

    private static readonly string[] LoadKinds = ["id_alloc", "game_phase", "battle_start", "battle_end", "ap_restore", "stack_move"];

    /// <summary>
    /// Пары «сохранение → следующие выдачи»: по каждому сохранению партии (привязка к строке <c>save</c> — <see cref="SaveSnapshots"/>) и
    /// каждому классу, который движок выдал позже в той же партии (до <c>game_phase leave</c>), — номер первой выдачи после записи
    /// (это счётчик движка в миг записи: он меняется только выдачей) против счётчика порта после чтения этого файла
    /// (<see cref="MapLoader"/>; сохранение и сценарий порта считают так же — <see cref="GameSession.IdNextOf"/>). Порт после загрузки
    /// не выше движка никогда (иначе он пропускал бы номера); ниже — объект с наибольшим номером класса снят до записи: движок без
    /// перезагрузки этот номер больше не выдаст, а после загрузки файла выдаст снова (<c>0x5ee996</c>), и порт так же. Граница по
    /// журналу (<see cref="JournalSeenIds"/>, для журналов без <c>id_alloc</c>) — тоже не выше движка; сколько разрывов она закрывает —
    /// печать. Измерено 18.10.2026.
    /// </summary>
    [SkippableFact]
    public void Saves_CountersAfterLoad_NotAboveEngine()
    {
        int files = 0, pairs = 0, same = 0, lower = 0, higher = 0, seenSame = 0, seenHigher = 0, recount = 0;
        foreach (var (dir, file) in AllocJournals)
        {
            string journal = Path.Combine(GameSaveTests.CorpusDir(), dir, file);
            if (!File.Exists(journal)) continue;
            var events = JournalCache.Read(journal, LoadKinds).OrderBy(e => e.Seq).ToList();
            var leaves = events.OfType<GamePhaseEvent>().Where(p => p.What == "leave").Select(p => p.Seq).ToList();
            var allocs = events.OfType<IdAllocEvent>().Where(a => a.Id is { Length: 10 } && a.Index is not null).ToList();
            var seen = new JournalSeenIds();
            int jPairs = 0, jLower = 0, jSeen = 0;
            foreach (var (path, _, seq) in SaveSnapshots.Of(journal).BoundSaves())
            {
                var map = MapLoader.Load(path);
                if (map.Stacks.Keys.FirstOrDefault() is not { } any) continue;
                files++;
                string prefix = any[..4];
                if (IdNextLoadTests.Of(GameSession.IdNextOf(map)) != IdNextLoadTests.Of(map.IdNext)) recount++;
                var bound = seen.Until(events, seq, prefix);
                long end = leaves.FirstOrDefault(l => l > seq, long.MaxValue);
                foreach (string kind in IdNextLoadTests.Issued)
                {
                    if (allocs.FirstOrDefault(a => a.Seq > seq && a.Seq < end && a.Id!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                                   && a.Id.Substring(4, 2).Equals(kind, StringComparison.OrdinalIgnoreCase))
                        is not { Index: { } index }) continue;
                    int port = map.IdNext.GetValueOrDefault(kind), withSeen = Math.Max(port, bound.GetValueOrDefault(kind));
                    pairs++;
                    jPairs++;
                    if (port == index) same++;
                    else if (port < index) { lower++; jLower++; }
                    else
                    {
                        higher++;
                        output.WriteLine($"  выше движка: {dir} {Path.GetFileName(path)} {kind} порт {port:x} движок {index:x}");
                    }
                    if (withSeen == index) { seenSame++; jSeen++; }
                    else if (withSeen > index) seenHigher++;
                    else output.WriteLine($"  граница журнала ниже: {dir} {Path.GetFileName(path)} {kind} файл {port:x} журнал " +
                                          $"{bound.GetValueOrDefault(kind):x} движок {index:x}");
                }
            }
            output.WriteLine($"{dir}/{file}: пар {jPairs}, порт ниже движка {jLower}, с границей журнала совпало {jSeen}");
        }
        Skip.If(files == 0, "нет корпуса");
        output.WriteLine($"сохранений {files}, пар {pairs}: совпало {same}, порт ниже {lower}, выше {higher}; с границей журнала " +
                         $"совпало {seenSame}, выше {seenHigher}; пересчёт по модели разошёлся {recount}");
        Assert.Equal(0, higher);
        Assert.Equal(0, seenHigher);
        Assert.Equal(0, recount);
    }

    /// <summary>
    /// Начало партии: первая выдача каждого класса после входа в карту (строки между <c>game_phase leave</c> — новая карта выдаёт и до
    /// строки <c>new</c>) — это счётчик сразу после чтения сценария (<c>0x5eea31</c> → <c>0x5ee996</c>). Сценарий порта той же карты
    /// (<c>mods/port-synth</c>, имя — по сохранениям партии) обязан дать тот же счётчик: до 18.10 <see cref="ScenarioFile"/> счётчиков
    /// не читал вовсе (новая партия порта шла по правилу «наибольший из живых + 1»). И загрузка: в <c>20261014-replay</c> могила
    /// <c>S135TB0000</c> выдана до загрузки (seq 2585338) и снова после (2646745) — номер объекта, которого нет в загруженном файле,
    /// движок выдаёт снова.
    /// </summary>
    [SkippableFact]
    public void Start_ScenarioCounters_MatchFirstAllocs()
    {
        // Сценарии порта по имени без знаков и регистра: база (data/scenarios, «'Greenskins'») и слой синтетических карт.
        var scenarios = new[] { Path.Combine(AppContext.BaseDirectory, "data", "scenarios"),
                                Path.Combine(AppContext.BaseDirectory, "mods", "port-synth", "data", "scenarios") }
            .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*" + ScenarioFile.Extension))
            .GroupBy(f => Plain(Path.GetFileNameWithoutExtension(f))).ToDictionary(g => g.Key, g => g.Last());
        int starts = 0, kinds = 0, same = 0, stale = 0;
        bool replayRead = false;
        foreach (var (dir, file) in AllocJournals)
        {
            string journal = Path.Combine(GameSaveTests.CorpusDir(), dir, file);
            if (!File.Exists(journal)) continue;
            var events = JournalCache.Read(journal, LoadKinds).OrderBy(e => e.Seq).ToList();
            var phases = events.OfType<GamePhaseEvent>().ToList();
            var leaves = phases.Where(p => p.What == "leave").Select(p => p.Seq).Append(long.MaxValue).ToList();
            var allocs = events.OfType<IdAllocEvent>().Where(a => a.Id is { Length: 10 } && a.Index is not null).ToList();
            var saves = SaveSnapshots.Of(journal).BoundSaves();
            var started = StartedAt(journal);
            long from = 0;
            foreach (long to in leaves)
            {
                var first = saves.FirstOrDefault(x => x.Seq > from && x.Seq < to);
                long window = from;
                from = to;
                if (first.Path is null) continue;
                // Окно после загрузки (game_phase load) — счётчики из файла сохранения, а не сценария (20261019c-cov: загрузка t03 и t10
                // посреди партии); такие окна сверяет Saves_CountersAfterLoad_NotAboveEngine.
                if (phases.FirstOrDefault(p => p.Seq > window && p.Seq < to && p.What != "leave") is { What: "load" })
                    continue;
                // Имя сохранения драйвера — «<карта>-<раса>-tNN-NNN.sg».
                string stem = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(first.Path), @"-[a-z]+-t\d+-\d+\.sg$", "");
                if (!scenarios.TryGetValue(Plain(stem), out var scenario)) continue;
                if (Regenerated.TryGetValue(Plain(stem), out var redone) && started < redone)
                {
                    stale++;   // партия шла на прежней карте — сценарий порта уже другой
                    continue;
                }
                var map = ScenarioFile.Read(scenario).Looks.Map;
                string prefix = map.Stacks.Keys.First()[..4];
                starts++;
                var own = allocs.Where(a => a.Seq > window && a.Seq < to && a.Id!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var g in own.GroupBy(a => a.Id!.Substring(4, 2).ToUpperInvariant()))
                {
                    long engine = g.First().Index!.Value;
                    int port = map.IdNext.GetValueOrDefault(g.Key);
                    kinds++;
                    if (port == engine) same++;
                    else output.WriteLine($"  {dir} {stem} {g.Key}: сценарий порта {port:x}, движок {engine:x}");
                }
            }
            if (dir == "20261014-replay" && file == "passive-001.jsonl")
            {
                replayRead = true;
                var tombs = allocs.Where(a => a.Id!.Equals("S135TB0000", StringComparison.OrdinalIgnoreCase)).Select(a => a.Seq).ToList();
                long load = events.OfType<GamePhaseEvent>().Single(p => p.What == "load").Seq;
                var earlier = leaves.Where(l => l < load).ToList();
                Assert.True(earlier.Count >= 2, "перед загрузкой — партия между двумя выходами в меню");
                long before = earlier[^2];   // партия перед загрузкой
                Assert.Contains(tombs, s => s > before && s < load);
                Assert.Contains(tombs, s => s > load);
            }
        }
        Skip.If(starts == 0, "нет корпуса");
        output.WriteLine($"начал партий со сценарием порта {starts}, классов {kinds}, совпало {same}; на прежней карте {stale}");
        Assert.InRange(stale, 0, 6);   // 6 — при всём корпусе 18.10
        Assert.Equal(kinds, same);
        Assert.True(replayRead || !Directory.Exists(Path.Combine(GameSaveTests.CorpusDir(), "20261014-replay")));
    }

    /// <summary>Сценарии порта, переделанные после ранних партий корпуса (UTC): объекты карты другие, сверять не с чем.
    /// Карта 1 — 2740b7b6 (16.10 08:24, юниты A3–A5: UN до 0x42 вместо 0x3d), карта 4 — b14e1c40 (16.10 20:53, предмет руины —
    /// тип, а не экземпляр: IM 0x15 вместо 0x18).</summary>
    private static readonly Dictionary<string, DateTimeOffset> Regenerated = new(StringComparer.OrdinalIgnoreCase)
    {
        [Plain("port-synth-1-battle")] = new(2026, 10, 16, 8, 24, 21, TimeSpan.Zero),
        [Plain("port-synth-4-ai")] = new(2026, 10, 16, 20, 53, 15, TimeSpan.Zero),
    };

    /// <summary>Начало прогона журнала (<c>started_at</c> строки манифеста); нет — наименьшее время.</summary>
    private static DateTimeOffset StartedAt(string journal)
    {
        string manifest = Path.Combine(Path.GetDirectoryName(journal)!, "manifest.json");
        if (!File.Exists(manifest)) return DateTimeOffset.MinValue;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.TryGetProperty("file", out var f) && f.GetString() == Path.GetFileName(journal)
                && row.TryGetProperty("started_at", out var at) && at.GetString() is { } text)
                return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return DateTimeOffset.MinValue;
    }

    private static string Plain(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
