using D2.Data.Ui;

// Граф экранов порта data/ui/flow.json из журналов зонда (схема 7.0).
//
//   dotnet run --project tools/UiFlowImport [-- --corpus <каталог>] [--journal <путь от корня корпуса>]...
//                                           [--screens <screens.json>] [--out <flow.json>]
//
// Правило пары щелчка и смены экрана и его слабые места — в D2.Data.Ui.UiFlowImport; здесь только параметры,
// запись файла и отчёт о покрытии (сводится в docs/ui-flow.md). Перевод повторяем байт в байт — это проверяет
// UiFlowTests.Flow_Json_IsUpToDate.

string root = RepoRoot();
string corpus = Arg("--corpus") ??
    (Environment.GetEnvironmentVariable("D2_CORPUS_DIR") is { Length: > 0 } env ? env : Path.Combine(root, "corpus"));
var journals = Args("--journal") is { Count: > 0 } js ? js : UiFlowImport.DefaultJournals;
string screensPath = Arg("--screens") ?? Path.Combine(root, "data", "ui", "screens.json");
string outPath = Arg("--out") ?? Path.Combine(root, "data", "ui", "flow.json");

var missing = journals.Where(j => !File.Exists(Path.Combine(corpus, j))).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"нет журналов в {corpus}: {string.Join(", ", missing)}");
    return 1;
}

var screens = UiScreens.Load(screensPath);
var ids = screens.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
var (flow, report) = UiFlowImport.FromCorpus(corpus, journals, ids);
UiFlows.Save(outPath, flow);

int edges = flow.Screens.Sum(s => s.Edges.Count);
Console.WriteLine($"журналы: {string.Join(", ", journals)}");
Console.WriteLine($"экранов в графе {flow.Screens.Count} (движка {flow.EngineScreens.Count}), переходов {edges} -> {outPath}");
PrintCoverage(screens, flow, report);
return 0;

static void PrintCoverage(IReadOnlyList<UiScreen> screens, UiFlow flow, UiFlowReport r)
{
    var seen = r.SeenScreens.ToHashSet(StringComparer.Ordinal);
    var never = screens.Where(s => !seen.Contains(s.Id)).Select(s => s.Id).ToList();
    var clickable = screens.SelectMany(s => s.Elements
            .Where(e => e.Kind is UiKind.Button or UiKind.Toggle or UiKind.List or UiKind.TextList)
            .Select(e => $"{s.Id}.{e.Id}")).ToHashSet(StringComparer.Ordinal);
    int clicked = r.ClickedElements.Count(clickable.Contains);
    var byAction = flow.Screens.SelectMany(s => s.Edges).GroupBy(e => e.Action)
        .Select(g => $"{g.Key}={g.Count()}");

    Console.WriteLine($"экранов открыто {screens.Count - never.Count} из {screens.Count}");
    Console.WriteLine($"элементов нажато {clicked} из {clickable.Count} нажимаемых (кнопки, переключатели, списки); " +
                      $"вне screens.json: {string.Join(", ", r.ClickedElements.Where(c => !clickable.Contains(c)))}");
    Console.WriteLine($"щелчков {r.Clicks}: без смены экрана {r.ClicksNoChange}, без имени {r.ClicksUnnamed}, " +
                      $"дублей кнопка+переключатель {r.ToggleDuplicates}");
    Console.WriteLine($"переходы по видам: {string.Join(", ", byAction)}");
    Console.WriteLine($"вспышек без щелчка {r.EngineBursts}, окон сообщений {r.MsgBoxes} (без щелчка {r.MsgBoxesUnpaired}), " +
                      $"несогласий стека {r.StackMismatches}, ui_clear {r.Clears}, show_refused {r.ShowRefused}, " +
                      $"ui_open ok=false {r.OpenFailed}");
    Console.WriteLine($"щелчки без имени: {string.Join("; ", r.UnknownNames)}");
    Console.WriteLine($"не открывались ({never.Count}): {string.Join(", ", never)}");
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

List<string> Args(string name) =>
    args.Select((a, i) => (a, i)).Where(p => p.a == name && p.i + 1 < args.Length).Select(p => args[p.i + 1]).ToList();

// Корень репозитория — ближайшая вверх папка с Disciples.sln: так вызов работает и из рабочего дерева git.
static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Disciples.sln"))) dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("не найден Disciples.sln выше текущей папки");
}
