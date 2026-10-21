using System.Text.Json;
using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Оперативный слой ИИ (<c>docs/ai-turn-plan.md</c>, срез 7): смена приказов <c>0x46defb</c> — защитник получает
/// дальний свой город. Сохранение дня → начало хода и стратегия каждого игрока ИИ порта → строки «приказ» отчёта против
/// <c>ai_order</c> с причиной <c>operational_orders</c> того же игрока и дня (три партии).</summary>
[Trait("Category", "Corpus")]
public class AiOperationalHarnessTests(ITestOutputHelper output)
{
    private static readonly string[] Sessions = ["20260918-probe65", "20260919-human", "20260920-legions"];

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Сколько ходов игроков ИИ сверено и сколько раз порт и движок сменили приказ одинаково (город-цель).
    /// Измерено 23.09: 714 из 716 ходов, обе смены движка (probe65, новые защитники <c>S143KC009d</c>, <c>S143KC009f</c>)
    /// повторены. Два лишних у порта (probe65, <c>S143PL0004</c>, дни 4 и 5) — защитники, которых в эти дни нанял сам
    /// порт (подприказ найма лидера разошёлся), у движка таких наймов нет.</summary>
    [SkippableFact]
    public void OrderChanges_MatchJournal()
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        int turns = 0, same = 0, differ = 0, changes = 0;
        foreach (string session in Sessions)
        {
            string dir = Path.Combine(GameSaveTests.CorpusDir(), session);
            string path = Path.Combine(dir, "passive-001.jsonl");
            Skip.IfNot(File.Exists(path), $"нет журнала {session}");
            var journal = Journal(path);
            foreach (string sg in Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal)
                         .GroupBy(f => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^(.*-t\d+)-").Groups[1].Value)
                         .Select(g => g.Last()))
            {
                var looks = MapLooksLoader.Load(sg);
                var map = looks.Map;
                var human = map.Players.Values.First(p => p.IsHuman);
                var info = new GameInfo("", "", [human.Id], map.Limits?.DifficultyGame ?? 1, map.Limits?.SuggestedLevel ?? 1);
                var game = new GameSession(new GameState(info, looks), content.MapArt, book);
                foreach (string player in GameSession.TurnOrder(map, new HashSet<string>([human.Id], StringComparer.OrdinalIgnoreCase), book))
                {
                    if (!journal.Turns.Contains((player, map.Turn))) continue;
                    var report = new List<string>();
                    game.BeginAiTurn(player, report);
                    var mine = report.Where(r => r.StartsWith("приказ ", StringComparison.Ordinal))
                                     .Select(r => r.Split(' ')[2] + " " + r.Split(' ').ElementAtOrDefault(3)).Order().ToList();
                    var want = journal.Orders.GetValueOrDefault((player, map.Turn), []).Order().ToList();
                    turns++;
                    changes += want.Count;
                    if (mine.SequenceEqual(want)) same++;
                    else
                    {
                        differ++;
                        if (differ <= 15) output.WriteLine($"{Path.GetFileName(sg)} {player}: журнал [{string.Join(", ", want)}], порт [{string.Join(", ", report.Where(r => r.StartsWith("приказ ", StringComparison.Ordinal)))}]");
                    }
                }
            }
        }
        output.WriteLine($"ходов ИИ {turns}: сошлось {same}, разошлось {differ}; смен приказа в журнале {changes}");
        Assert.Equal(716, turns);
        Assert.Equal(714, same);
        Assert.Equal(2, changes);
    }

    /// <summary>Ходы игроков ИИ (игрок, день) и смены приказа оперативного слоя в них — «приказ город».</summary>
    private static (HashSet<(string, int)> Turns, Dictionary<(string, int), List<string>> Orders) Journal(string path)
    {
        var turns = new HashSet<(string, int)>();
        var orders = new Dictionary<(string, int), List<string>>();
        (string Player, int Day)? current = null;
        var rows = new List<JsonElement>();
        foreach (string line in File.ReadLines(path))
        {
            if (!line.Contains("\"ev\":\"ai_turn\"", StringComparison.Ordinal) && !line.Contains("\"operational_orders\"", StringComparison.Ordinal))
                continue;
            using var doc = JsonDocument.Parse(line);
            rows.Add(doc.RootElement.Clone());
        }
        foreach (var e in rows.OrderBy(e => e.GetProperty("seq").GetInt64()))
        {
            if (e.GetProperty("ev").GetString() == "ai_turn")
            {
                if (e.GetProperty("phase").GetString() == "begin" && e.GetProperty("cause").GetString() == "ai")
                {
                    current = (e.GetProperty("player").GetString()!, (int)e.GetProperty("turn").GetInt64());
                    turns.Add(current.Value);
                }
                continue;
            }
            if (current is not { } c) continue;
            if (!orders.TryGetValue(c, out var list)) orders[c] = list = [];
            string order = e.GetProperty("order_name").GetString() == "defend_city" ? "DefendCity" : "Normal";
            string target = e.GetProperty("target").GetString() ?? "";
            list.Add($"{order} {(target.Equals("G000000000", StringComparison.OrdinalIgnoreCase) ? "" : target)}");
        }
        return (turns, orders);
    }
}
