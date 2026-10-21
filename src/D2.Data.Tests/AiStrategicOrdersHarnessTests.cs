using System.Text.Json;
using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Раздача стратегических приказов ИИ (<c>0x44e29e</c>, <c>docs/ai-turn-plan.md</c> §1.1): в день 1 отряды с
/// действующим <c>L_NORMAL</c> получают приказ ИИ — второй по силе боец <c>L_DEFEND_CITY</c> и т. д. Сохранение дня 1
/// (быстрое, перед концом хода человека) → начало хода и стратегия каждого игрока ИИ порта по очереди → строки
/// «раздача» отчёта против <c>ai_order</c> с возвратом <c>0x44e6cc</c> того же хода. Сохранение привязано к строке
/// <c>save</c> журнала по времени записи файла (как <see cref="D2.Data.Battle.SaveSnapshots"/>), ход ИИ — к последнему
/// сохранению той же загрузки перед ним: журнал 20261014-replay переигрывает день 1 много раз на картах одной основы.</summary>
[Trait("Category", "Corpus")]
public class AiStrategicOrdersHarnessTests(ITestOutputHelper output)
{
    private static readonly (string Dir, string File)[] Journals =
    [
        ("20260918-probe65", "passive-001.jsonl"), ("20260919-human", "passive-001.jsonl"),
        ("20260920-legions", "passive-001.jsonl"), ("20260930-probe72", "passive-001.jsonl"),
        ("20261014-replay", "passive-001.jsonl"), ("20261014-replay", "passive-002.jsonl"),
        ("20261016-port-synth", "passive-002.jsonl"), ("20261016b-port-synth", "passive-001.jsonl"),
        ("20261016c-port-synth", "passive-001.jsonl"), ("20261016d-port-synth", "passive-001.jsonl"),
        ("20261019c-cov", "passive-001.jsonl"),
    ];

    private const string StrategicOrdersReturn = "0x44e6cc";

    private const string EmptyId = "G000000000";

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// Все раздачи журналов — только в день 1 (<c>0x44e26e</c>: <c>cmp [CScenarioInfo +0x20], 1</c>), и каждая повторена
    /// портом. Измерено 15.10: в журналах 7 раздач, все в 20261014-replay (S135 ×3 — <c>KC0020</c>; r2a и r2b — по
    /// <c>KC008e</c> Легионов и <c>KC0092</c> Нежити), во всех — <c>L_DEFEND_CITY</c> второму по силе из двух бойцов. Ходов
    /// ИИ дня 1 с привязанным сохранением 66: сошлось 66 (до правки 59 — порт не раздавал), и в тех же ходах оперативный
    /// слой повторяет все 7 смен журнала (до правки 59 из 66): <c>KC008e</c> → <c>L_DEFEND_CITY</c> на деревню
    /// <c>FT0010</c>, прочие без своих городов — обратно <c>L_NORMAL</c>. Остальные ветки <c>0x44e518</c> (3 и больше отряда,
    /// воры) до 16.10 в журналах не встречались — только листинг (<c>AiTacticEvalTests.StrategicOrders_ByListing</c>).
    /// 16.10 — оригинал на <c>port-synth-4-ai</c> (20261016-port-synth passive-002, лёгкая): у каждого из трёх ИИ по 5 отрядов
    /// в раздаче (одному <c>L_DEFEND_CITY</c>, четырём <c>L_ASSIST</c>), всего 15; ходов дня 1 стало 70 — сошлось
    /// 70, оперативный слой — 10 смен журнала, сошлось 70 ходов из 70. 16.10 (вторая партия) — 20261016b-port-synth passive-001: те
    /// же 15 раздач на очень трудной (карта 4) и дни 1 карт 1–3: ходов 86, сошлось 86; раздач 37, все в сверенных ходах; оперативный
    /// слой — 13 смен, 86 из 86. 16.10 (третья партия) — 20261016c-port-synth passive-001 (карты 1 и 4, человек — Кланы): ещё 15 раздач
    /// карты 4 и 8 ходов дня 1; всего ходов 94, сошлось 94; раздач 52, все в сверенных ходах; оперативный слой — 16 смен, 94 из 94.
    /// 16.10 вечер (четвёртая партия) — 20261016d-port-synth passive-001 (карта 4, места в плане): ещё 15 раздач и 4 хода дня 1; всего
    /// ходов 98, сошлось 98; раздач 67, все в сверенных ходах; оперативный слой — 19 смен, 98 из 98.
    /// </summary>
    [SkippableFact]
    public void Orders_MatchJournal() => Check(synth: false, wantOrders: 13, wantTurns: 70, wantOperational: 10);

    /// <summary>То же на партиях синтетических карт (<c>port-synth-4-ai</c>) — отдельной группой (<c>Category=Synth</c>).</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Orders_MatchJournal_Synth() => Check(synth: true, wantOrders: 60, wantTurns: 32, wantOperational: 12);

    private void Check(bool synth, int wantOrders, int wantTurns, int wantOperational)
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        int turns = 0, same = 0, orders = 0, covered = 0, laterDays = 0, operational = 0, operationalSame = 0;
        foreach (var (dirName, file) in Journals.Where(x => CorpusSets.IsSynth(x.Dir) == synth))
        {
            string dir = Path.Combine(GameSaveTests.CorpusDir(), dirName);
            string path = Path.Combine(dir, file);
            Skip.IfNot(File.Exists(path), $"нет журнала {dirName}/{file}");
            var events = JournalCache.Read(path, ["save", "ai_turn", "ai_order"]).OrderBy(e => e.Seq).ToList();
            var given = events.OfType<AiOrderEvent>().Where(o => o.RetAddr == StrategicOrdersReturn).ToList();
            orders += given.Count;
            var begins = events.OfType<AiTurnEvent>().Where(t => t is { Phase: "begin", Cause: "ai" }).ToList();
            laterDays += given.Count(o => begins.LastOrDefault(b => b.Tid == o.Tid && b.Seq < o.Seq) is not { Turn: 1 });
            var bound = BoundSaves(dir, file, events.OfType<SaveEvent>().ToList());
            foreach (var (sg, save) in bound)
            {
                if (save.Turn != 1) continue;
                long next = bound.Where(x => x.Save.Tid == save.Tid && x.Save.Seq > save.Seq)
                                 .Select(x => x.Save.Seq).DefaultIfEmpty(long.MaxValue).Min();
                var dayTurns = begins.Where(b => b.Tid == save.Tid && b.Seq > save.Seq && b.Seq < next && b.Turn == 1).ToList();
                if (dayTurns.Count == 0) continue;
                var looks = MapLooksLoader.Load(sg);
                var map = looks.Map;
                if (map.Turn != 1) continue;
                var human = map.Players.Values.First(p => p.IsHuman);
                var info = new GameInfo("", "", [human.Id], map.Limits?.DifficultyGame ?? 1, map.Limits?.SuggestedLevel ?? 1);
                var game = new GameSession(new GameState(info, looks), content.MapArt, book);
                var players = GameSession.TurnOrder(map, new HashSet<string>([human.Id], StringComparer.OrdinalIgnoreCase), book);
                foreach (var begin in dayTurns)
                {
                    if (!players.Contains(begin.Player ?? "", StringComparer.OrdinalIgnoreCase)) continue;
                    var report = new List<string>();
                    game.BeginAiTurn(begin.Player!, report);
                    var mine = report.Where(r => r.StartsWith("раздача ", StringComparison.Ordinal)).Select(r => r[8..]).Order().ToList();
                    long end = begins.Where(b => b.Tid == begin.Tid && b.Seq > begin.Seq).Select(b => b.Seq).DefaultIfEmpty(long.MaxValue).Min();
                    var want = given.Where(o => o.Tid == begin.Tid && o.Seq > begin.Seq && o.Seq < end)
                                    .Select(o => $"{o.Stack} {(StackOrder)o.Order}").Order().ToList();
                    turns++;
                    covered += want.Count;
                    if (mine.SequenceEqual(want, StringComparer.OrdinalIgnoreCase)) same++;
                    else output.WriteLine($"{Path.GetFileName(sg)} {begin.Player}: журнал [{string.Join(", ", want)}], порт [{string.Join(", ", mine)}]");
                    // Следствие в том же ходе — смена приказов оперативного слоя (0x46defb): новый защитник без цели получает
                    // дальний свой город или, без городов, снова L_NORMAL.
                    var mineOps = report.Where(r => r.StartsWith("приказ ", StringComparison.Ordinal)).Select(r => r[7..].TrimEnd()).Order().ToList();
                    var wantOps = events.OfType<AiOrderEvent>()
                        .Where(o => o.Cause == "operational_orders" && o.Tid == begin.Tid && o.Seq > begin.Seq && o.Seq < end)
                        .Select(o => $"{o.Stack} {(StackOrder)o.Order} {(o.Target == EmptyId ? "" : o.Target)}".TrimEnd()).Order().ToList();
                    operational += wantOps.Count;
                    if (mineOps.SequenceEqual(wantOps, StringComparer.OrdinalIgnoreCase)) operationalSame++;
                    else output.WriteLine($"  оперативный слой: журнал [{string.Join(", ", wantOps)}], порт [{string.Join(", ", mineOps)}]");
                }
            }
        }
        output.WriteLine($"ходов ИИ дня 1 {turns}: сошлось {same}; раздач в журналах {orders}, из них в сверенных ходах {covered}, не в день 1 {laterDays}; "
                         + $"оперативный слой: сошлось {operationalSame}, смен в журнале {operational}");
        Assert.Equal(0, laterDays);
        Assert.Equal(wantOrders, orders);
        Assert.Equal(wantOrders, covered);
        Assert.Equal(wantTurns, turns);
        Assert.Equal(wantTurns, same);
        Assert.Equal(wantOperational, operational);
        Assert.Equal(wantTurns, operationalSame);
    }

    /// <summary>Сохранения манифеста, привязанные к строкам <c>save</c> журнала: файл — к последней ещё не занятой строке,
    /// записанной не позже времени файла (с запасом 0,5 с), как <c>SaveSnapshots.BindToSaves</c>.</summary>
    internal static List<(string Path, SaveEvent Save)> BoundSaves(string dir, string journal, List<SaveEvent> saves)
    {
        var bound = new List<(string, SaveEvent)>();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        var row = doc.RootElement.EnumerateArray().FirstOrDefault(r => r.TryGetProperty("file", out var f) && f.GetString() == journal);
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("saves", out var names)
            || !row.TryGetProperty("started_at", out var at)) return bound;
        var started = DateTimeOffset.Parse(at.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var used = new HashSet<long>();
        var lines = saves.Where(s => s.Ok && !s.Temp).ToList();
        foreach (string sg in names.EnumerateArray().Select(n => Path.Combine(dir, n.GetString()!)).Where(File.Exists)
                                   .OrderBy(File.GetLastWriteTimeUtc))
        {
            double mt = (new DateTimeOffset(File.GetLastWriteTimeUtc(sg)) - started).TotalMilliseconds;
            var hit = lines.Where(s => s.T <= mt + BindSlackMs && !used.Contains(s.Seq)).MaxBy(s => s.Seq);
            if (hit is null) continue;
            used.Add(hit.Seq);
            bound.Add((sg, hit));
        }
        return bound;
    }

    private const double BindSlackMs = 500;
}
