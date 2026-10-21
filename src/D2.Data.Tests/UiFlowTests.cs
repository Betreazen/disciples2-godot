using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Ui;
using Xunit;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Граф экранов <c>data/ui/flow.json</c>: ссылается только на существующие экраны и элементы, числа
/// переходов закреплены, пересборка из корпуса даёт тот же файл байт в байт, правило пары щелчка и смены экрана
/// проверено на коротких выдуманных журналах.</summary>
[Trait("Category", "Corpus")]
public class UiFlowTests(ITestOutputHelper output)
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", "ui", name);

    private static UiFlow Flow => UiFlows.Load(Data("flow.json"));

    [Fact]
    public void Flow_ReferencesOnlyKnownScreensAndElements()
    {
        var screens = UiScreens.Load(Data("screens.json")).ToDictionary(s => s.Id);
        var flow = Flow;
        var engine = flow.EngineScreens.Select(e => e.Id).ToHashSet();

        Assert.All(engine, id => Assert.False(screens.ContainsKey(id), $"{id} есть в screens.json"));
        bool Known(string id) => screens.ContainsKey(id) || engine.Contains(id);

        var problems = new List<string>();
        foreach (var s in flow.Screens)
        {
            if (!Known(s.Id)) problems.Add($"экран {s.Id}");
            foreach (var e in s.Edges)
            {
                if (e.Target is { } t && !Known(t)) problems.Add($"{s.Id}.{e.Element} -> {t}");
                if (e.Via is { } v && !Known(v)) problems.Add($"{s.Id}.{e.Element} via {v}");
                if (e.Element is { } el && !(screens.TryGetValue(s.Id, out var scr) && scr.Elements.Any(x => x.Id == el)))
                    problems.Add($"элемент {s.Id}.{el}");
                if (e.Action is UiFlowAction.Open or UiFlowAction.Goto or UiFlowAction.Panel && e.Target is null)
                    problems.Add($"{s.Id}.{e.Element} {e.Action} без цели");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>Числа пересчитаны по файлу, собранному из <c>20260919-human</c>, <c>20260918-probe65</c> и
    /// <c>20260920-ui</c>, <c>20260921-ui</c>, <c>20260922-ui</c> (срезы экранов по памяткам <c>docs/PLAY-ui-screens*.md</c>).</summary>
    [Fact]
    public void Flow_EdgeCountsAndKeyTransitions()
    {
        var flow = Flow;
        var edges = flow.Screens.SelectMany(s => s.Edges.Select(e => (s.Id, e))).ToList();
        output.WriteLine(string.Join(", ", edges.GroupBy(x => x.e.Action).Select(g => $"{g.Key}={g.Count()}")));

        Assert.Equal(81, flow.Screens.Count);
        Assert.Equal(8, flow.EngineScreens.Count);
        Assert.Equal(356, edges.Count);
        Assert.Equal(94, edges.Count(x => x.e.Action == UiFlowAction.Open));
        Assert.Equal(103, edges.Count(x => x.e.Action == UiFlowAction.Goto));
        Assert.Equal(40, edges.Count(x => x.e.Action == UiFlowAction.Close));
        Assert.Equal(31, edges.Count(x => x.e.Action == UiFlowAction.Panel));
        Assert.Equal(88, edges.Count(x => x.e.Action == UiFlowAction.None));

        UiFlowEdge One(string screen, string? element, string? target) =>
            Assert.Single(edges, x => x.Id == screen && x.e.Element == element && x.e.Target == target).e;

        Assert.Equal(UiFlowAction.Open, One("DLG_MAIN_MENU", "BTN_OPTIONS", "DLG_OPTIONS").Action);
        var single = One("DLG_MAIN_MENU", "BTN_SINGLE", "DLG_SINGLE_PLAYER");
        Assert.Equal((UiFlowAction.Goto, "DLG_MENU_TRANS"), (single.Action, single.Via));
        var start = One("DLG_CHOOSE_LORD", "BTN_CONTINUE", "DLG_SCENARIO_BRIEFING");
        Assert.Equal((UiFlowAction.Goto, "DLG_WAIT", (UiPhase?)UiPhase.Game), (start.Action, start.Via, start.Phase));
        Assert.Equal(UiPhase.Battle, One("DLG_STRATEGIC", null, "DLG_BATTLE_A").Phase);
        Assert.Equal(UiFlowAction.Goto, One("DLG_STRATEGIC", "BTN_CAPITAL", "DLG_CAPITAL").Action);
        Assert.Equal(UiFlowAction.Panel, One("DLG_BUILD_STRUCTURE", "TOG_MAGE", "DLG_M_MAGE").Action);
        Assert.Contains(edges, x => x.Id == "DLG_BATTLE_A" && x.e is { Element: "BTN_CLOSE", Action: UiFlowAction.Close, Phase: UiPhase.Game });
    }

    /// <summary>Файл — это вывод <c>tools/UiFlowImport</c> по корпусу; пропускается, когда журналов на машине нет
    /// (корпус не в git: <c>D2_CORPUS_DIR</c> указывает на него из рабочего дерева).</summary>
    [SkippableFact]
    public void Flow_Json_IsUpToDate()
    {
        string corpus = CorpusDir();
        var missing = UiFlowImport.DefaultJournals.Where(j => !File.Exists(Path.Combine(corpus, j))).ToList();
        Skip.If(missing.Count > 0, $"нет журналов в {corpus}: {string.Join(", ", missing)}");

        var ids = UiScreens.Load(Data("screens.json")).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var (flow, _) = UiFlowImport.FromCorpus(corpus, UiFlowImport.DefaultJournals, ids);
        Assert.True(UiFlows.ToJson(flow) == File.ReadAllText(Data("flow.json")).ReplaceLineEndings("\n"),
            "data/ui/flow.json устарел. Пересоберите: dotnet run --project tools/UiFlowImport");
    }

    [Fact]
    public void Merge_ReplacesEdgesOfSameElementAndAddsScreens()
    {
        var baseFlow = new UiFlow
        {
            Screens =
            [
                new UiFlowScreen
                {
                    Id = "DLG_MAIN_MENU",
                    Edges =
                    [
                        new UiFlowEdge { Element = "BTN_QUIT", Action = UiFlowAction.Close },
                        new UiFlowEdge { Element = "BTN_TUTORIAL", Action = UiFlowAction.None },
                    ],
                },
            ],
        };
        var mod = new UiFlow
        {
            Screens =
            [
                new UiFlowScreen
                {
                    Id = "dlg_main_menu",
                    Edges = [new UiFlowEdge { Element = "BTN_TUTORIAL", Action = UiFlowAction.Goto, Target = "MY_TUTORIAL" }],
                },
                new UiFlowScreen { Id = "MY_TUTORIAL", Edges = [new UiFlowEdge { Element = "BTN_BACK", Action = UiFlowAction.Close }] },
            ],
        };

        var merged = UiFlows.Merge(baseFlow, mod);
        var main = merged.Screens[0];
        Assert.Equal(["BTN_QUIT", "BTN_TUTORIAL"], main.Edges.Select(e => e.Element!));
        Assert.Equal("MY_TUTORIAL", main.Edges[1].Target);
        Assert.Equal("MY_TUTORIAL", merged.Screens[1].Id);
        Assert.Equal(UiFlows.ToJson(merged), UiFlows.ToJson(UiFlows.FromJson(UiFlows.ToJson(merged))));
    }

    [Fact]
    public void GameContent_LoadsFlowFromData()
    {
        var content = SharedContent.Load(Path.Combine(AppContext.BaseDirectory, "data"));
        Assert.Equal(Flow.Screens.Count, content.Flow.Screens.Count);
    }

    // --- Правило пары на выдуманных журналах ---

    private static readonly IReadOnlySet<string> Ids =
        new HashSet<string> { "DLG_A", "DLG_B", "DLG_MESSAGE_BOX", "DLG_MENU_TRANS", "DLG_P1", "DLG_P2" };

    private static UiFlowEdge OnlyEdge(params CorpusEvent[] events)
    {
        var (flow, _) = UiFlowImport.Build([("t", events)], Ids);
        return Assert.Single(flow.Screens.SelectMany(s => s.Edges));
    }

    private static UiStackEvent Show(long seq, long t, string screen, long depth) =>
        new(seq, 1, t, "show", screen, "C" + screen, "menu", depth, screen, "0x0");

    private static UiStackEvent Hide(long seq, long t, string screen, long depth, string top) =>
        new(seq, 1, t, "hide", screen, "C" + screen, "menu", depth, top, "0x0");

    private static UiClickEvent Click(long seq, long t, string screen, string element, string kind = "button") =>
        new(seq, 1, t, kind, screen, element, null, null, null, null, screen, "0x0");

    [Fact]
    public void Pairing_GotoThroughTransitionSurvivesLongBuild()
    {
        var e = OnlyEdge(Show(1, 0, "DLG_A", 1), Click(2, 100, "DLG_A", "BTN_GO"),
            Hide(3, 100, "DLG_A", 0, "none"), Show(4, 100, "DLG_MENU_TRANS", 1),
            Hide(5, 900, "DLG_MENU_TRANS", 0, "none"), Show(6, 3500, "DLG_B", 1));
        Assert.Equal((UiFlowAction.Goto, "DLG_B", "DLG_MENU_TRANS"), (e.Action, e.Target, e.Via));
    }

    [Fact]
    public void Pairing_OpenCloseAndMessage()
    {
        var (flow, _) = UiFlowImport.Build([("t", new CorpusEvent[]
        {
            Show(1, 0, "DLG_A", 1), Click(2, 100, "DLG_A", "BTN_ASK"),
            new UiMsgBoxEvent(3, 1, 100, "Точно?", "X005TA0001", true, "DLG_A", "0x0"),
            Show(4, 100, "DLG_MESSAGE_BOX", 2), Click(5, 2000, "DLG_MESSAGE_BOX", "BTN_NO"),
            Hide(6, 2000, "DLG_MESSAGE_BOX", 1, "DLG_A"),
        })], Ids);
        var a = Assert.Single(flow.Screens.Single(s => s.Id == "DLG_A").Edges);
        Assert.Equal((UiFlowAction.Open, "DLG_MESSAGE_BOX", "X005TA0001"), (a.Action, a.Target, a.Message));
        Assert.Equal(UiFlowAction.Close, Assert.Single(flow.Screens.Single(s => s.Id == "DLG_MESSAGE_BOX").Edges).Action);
    }

    [Fact]
    public void Pairing_GapEndsBurstAndLateChangeHasNoElement()
    {
        var (flow, report) = UiFlowImport.Build([("t", new CorpusEvent[]
        {
            Show(1, 0, "DLG_A", 1), Click(2, 100, "DLG_A", "BTN_SCROLL"),
            Show(3, 100 + UiFlowImport.GapMs + 1, "DLG_B", 2),
        })], Ids);
        var edges = flow.Screens.Single(s => s.Id == "DLG_A").Edges;
        Assert.Equal([("BTN_SCROLL", UiFlowAction.None), (null, UiFlowAction.Open)],
            edges.Select(e => (e.Element, e.Action)));
        Assert.Equal(1, report.ClicksNoChange);
    }

    [Fact]
    public void Pairing_ToggleDuplicateDroppedAndPanelSwapDetected()
    {
        var (flow, report) = UiFlowImport.Build([("t", new CorpusEvent[]
        {
            Show(1, 0, "DLG_A", 1), new UiOpenEvent(2, 1, 0, "DLG_P1", "0x1", "CPanel", "other", "DLG_A", true, "0x0"),
            Click(3, 100, "DLG_A", "TOG_TWO"), Click(4, 100, "DLG_A", "TOG_TWO", "toggle"),
            new UiCloseEvent(5, 1, 100, "DLG_P1", "0x1"),
            new UiOpenEvent(6, 1, 100, "DLG_P2", "0x2", "CPanel", "other", "DLG_A", true, "0x0"),
        })], Ids);
        var e = Assert.Single(flow.Screens.Single(s => s.Id == "DLG_A").Edges);
        Assert.Equal((UiFlowAction.Panel, "DLG_P2", 1), (e.Action, e.Target, e.Seen));
        Assert.Equal(1, report.ToggleDuplicates);
    }

    private static string CorpusDir()
    {
        if (Environment.GetEnvironmentVariable("D2_CORPUS_DIR") is { Length: > 0 } dir) return dir;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
        return d is null ? "" : Path.Combine(d.FullName, "corpus");
    }
}
