using System.Text.Json;
using D2.Data.Corpus;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Посуточная сверка ходов ИИ с сохранений оригинала для любой партии корпуса с Q в начале каждого дня (карты 21–23,
/// переигровки старых длинных партий — <c>docs/synthetic-maps-port/replay-old-parties.md</c>): поиск партии в корпусе и итог по дням.
/// Сам день — <see cref="SavedAiDays"/>.</summary>
public partial class PortSynthMapsTests
{
    /// <summary>Итог посуточной сверки: дней сверено, выборов тактики и шагов стратегии у движка и совпавших, лишних строк у порта, клеток
    /// утра разошлось, бегств защитника у движка.</summary>
    internal sealed record DailyTotals(int Days, int Picks, int PicksSame, int Steps, int StepsSame, int Extra, int Morning, int Fled)
    {
        /// <summary>Расхождений состояния дня с журналом (бои, R250, земля — <c>DayCheck</c>), только печать.</summary>
        public int Checks { get; init; }

        /// <summary>Клеток отрядов и гарнизонов ИИ, сумок и складов наутро разошлось (расстановка и подготовка в миг хода, И33).</summary>
        public int Cells { get; init; }

        /// <summary>Юнитов групп ИИ наутро с другим типом, уровнем, здоровьем или опытом (И230; сверяется — 0).</summary>
        public int UnitStates { get; init; }
    }

    /// <summary>Журналы корпуса с сохранениями партии <paramref name="stem"/> (начало имени сохранения): каталог, журнал (строка
    /// <c>manifest.json</c>, в <c>saves</c> которой есть её сохранение) и seq последнего входа в партию (<c>game_phase</c> new/load/enter)
    /// перед первым её сохранением в журнале — до него в том же журнале могут быть другие карты с тем же префиксом id. С
    /// <paramref name="run"/> — только каталог с этим именем, а если такого нет — последний по имени каталог, оканчивающийся на
    /// <c>-<paramref name="run"/></c> (дата партии в начале имени не важна).</summary>
    internal static IEnumerable<(string Dir, string Journal, long From)> DailyJournals(string stem, string? run = null)
    {
        string root = GameSaveTests.CorpusDir();
        if (!Directory.Exists(root)) yield break;
        var dirs = Directory.GetDirectories(root).Order(StringComparer.Ordinal).ToList();
        if (run is not null)
        {
            string? exact = dirs.FirstOrDefault(d => Path.GetFileName(d) == run);
            string? suffixed = dirs.LastOrDefault(d => Path.GetFileName(d).EndsWith("-" + run, StringComparison.Ordinal));
            dirs = exact is not null ? [exact] : suffixed is not null ? [suffixed] : [];
        }
        foreach (string dir in dirs)
        {
            string manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest) || Directory.GetFiles(dir, $"{stem}-*.sg").Length == 0) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("file", out var file) || !row.TryGetProperty("saves", out var saves)
                    || !saves.EnumerateArray().Any(n => n.GetString()!.StartsWith(stem, StringComparison.Ordinal))) continue;
                string journal = Path.Combine(dir, file.GetString()!);
                if (!File.Exists(journal)) continue;
                var events = JournalCache.Read(journal, ["save", "game_phase"]).OrderBy(e => e.Seq).ToList();
                var bound = AiStrategicOrdersHarnessTests.BoundSaves(dir, Path.GetFileName(journal), events.OfType<SaveEvent>().ToList())
                                                         .Where(b => Path.GetFileName(b.Path).StartsWith(stem, StringComparison.Ordinal))
                                                         .OrderBy(b => b.Save.Seq).ToList();
                if (bound.Count == 0) continue;
                long first = bound[0].Save.Seq;
                long from = events.OfType<GamePhaseEvent>().Where(g => g.IsEnter && g.Seq < first).Select(g => g.Seq).DefaultIfEmpty(0).Max();
                yield return (dir, journal, from);
            }
        }
    }

    /// <summary>Ходы ИИ каждого дня партии <paramref name="stem"/> во всех её журналах <paramref name="runs"/> — с сохранения оригинала
    /// того же дня (<see cref="SavedAiDays"/>; сохранений в журнале — не меньше <paramref name="minSaves"/>). Печать — в том же виде, что у
    /// <see cref="Long_AiTurnsMatchTheOriginalJournal"/> (у той пока свой цикл: её числа правит ветка <c>r20/rest</c>; после слияния её можно
    /// свести к этому итогу): расхождения выборов и шагов по дням, клетки утра, выборы движка по типам.</summary>
    internal static DailyTotals DailyAiTurns(ITestOutputHelper output, string stem, IEnumerable<(string Dir, string Journal, long From)> runs,
                                             int minSaves)
    {
        var harness = new PortSynthMapsTests(output);
        int same = 0, total = 0, stepsSame = 0, steps = 0, fled = 0, days = 0, extra = 0, morning = 0, checks = 0, cells = 0, unitStates = 0;
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (dir, journal, from) in runs)
        {
            output.WriteLine($"{Path.GetFileName(dir)}/{Path.GetFileName(journal)} с seq {from}");
            // Журнал, где сохранений партии меньше minSaves, пропускается сам (а не вся проверка): неполную партию ловит число дней.
            List<AiDay> saved;
            try { saved = harness.SavedAiDays(stem, dir, journal, from, minSaves); }
            catch (SkipException e)
            {
                output.WriteLine($"  журнал не сверяется: {e.Message}");
                continue;
            }
            foreach (var d in saved)
            {
                if (d.Blind)
                {
                    output.WriteLine($"день {d.Day}: после пропущенного сохранения — память стратегии не та, не сверяется");
                    continue;
                }
                days++;
                var lines = Aligned(d.Engine, d.Port);
                var stepLines = Aligned(d.EngineSteps, d.PortSteps);
                int ok = lines.Count(l => l.StartsWith('=')), okSteps = stepLines.Count(l => l.StartsWith('='));
                (same, total, stepsSame, steps) = (same + ok, total + d.Engine.Count, stepsSame + okSteps, steps + d.EngineSteps.Count);
                foreach (var e in d.Engine) kinds[e.Split(' ')[2]] = kinds.GetValueOrDefault(e.Split(' ')[2]) + 1;
                output.WriteLine($"день {d.Day}: выборов движка {d.Engine.Count}, порта {d.Port.Count}, совпало {ok}; шагов движка " +
                                 $"{d.EngineSteps.Count}, порта {d.PortSteps.Count}, совпало {okSteps}; бежали с поля {d.Fled.Count}");
                // D2_DAILY_ALL=1 — печать и совпавших выборов (разбор дня: что движок выбрал до расхождения).
                foreach (var l in lines.Where(l => AllLines || !l.StartsWith('='))) output.WriteLine("  " + l);
                foreach (var l in stepLines.Where(l => !l.StartsWith('='))) output.WriteLine("  шаг " + l);
                foreach (var m in d.Morning) output.WriteLine("  утро: " + m);
                foreach (var c in d.Checks) output.WriteLine("  бой: " + c);
                foreach (var c in d.Cells) output.WriteLine("  клетки: " + c);
                checks += d.Checks.Count;
                cells += d.Cells.Count;
                foreach (var u in d.UnitStates) output.WriteLine("  юнит: " + u);
                unitStates += d.UnitStates.Count;
                extra += lines.Concat(stepLines).Count(l => l.StartsWith('+'));
                (fled, morning) = (fled + d.Fled.Count, morning + d.Morning.Count);
            }
        }
        output.WriteLine($"сверено дней {days}; итого выборов {total}, совпало {same}; шагов {steps}, совпало {stepsSame}; бегств защитника " +
                         $"{fled}; лишних у порта {extra}, клеток утра разошлось {morning}; расхождений состояния дня {checks}; " +
                         $"клеток, сумок и складов ИИ {cells}; юнитов ИИ с другим опытом или уровнем {unitStates}");
        output.WriteLine("выборы движка: " + string.Join(", ", kinds.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
        return new DailyTotals(days, total, same, steps, stepsSame, extra, morning, fled) { Checks = checks, Cells = cells, UnitStates = unitStates };
    }

    /// <summary>Печатать и совпавшие выборы (<c>D2_DAILY_ALL=1</c>) — для разбора дня.</summary>
    private static bool AllLines => Environment.GetEnvironmentVariable("D2_DAILY_ALL") == "1";
}
