using System.Text.Json;
using D2.Data.Corpus;
using D2.Data.Diagnostics;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Где интерфейс движка стирает путь отряда (реестр Ин82, <c>docs/map-path.md</c> §4 «Путь через конец хода»): «ОК» окна «расторгните альянс»
/// (<c>X005TA0303</c>, <c>0x4d201e</c>), «Нет» сносу жезла (<c>X005TA0272</c>, <c>0x4d1f06</c>) и итог своего вора «Visit Ruin» (<c>0x4d23a2</c>) зовут
/// <c>0x4cc63b(1)</c> → <c>0x41a808</c>; шпионаж вора путь не трогает. Журнал — партия карты 28 (<c>port-synth-28-path-reset</c>, зонд 7.6,
/// <c>corpus/20261020-path-reset</c>).
/// <para>Как видно стирание. Каждый выбор отряда — новая <c>CTaskSelectUnit</c> (конструктор <c>0x4cf8fa</c>): она показывает запомненный путь
/// (<c>0x4cf9cc</c> → <c>0x4cfa0a</c> → <c>0x4cc3b3</c> → <c>ShowMovementPath</c>, строка <c>path</c> зонда) и затем ставит эллипс (<c>0x4cfb32</c>,
/// <c>map_sel</c> <c>show</c> <c>via</c> <c>select</c>). Значит, путь помнится, если между вводом, вызвавшим выбор, и эллипсом есть <c>path</c> того же
/// отряда. Берётся первый выбор той же клетки после окна (у вора — после «ОК» окна итога <c>DLG_ACTION_RESULT</c>: итог приходит до него).
/// Строки <c>map_path</c> 7.6 зонд оригинала до Ин130 не писал (хук <c>0x4ccc2c</c> делил адрес с хуком <c>path</c>) — поэтому <c>path</c>.</para>
/// <para>Порт — выписка партии пользователя в порте (<c>golden/path-reset/*.path-reset.jsonl</c>: строки <c>allyrefused</c>, <c>thief</c>,
/// <c>rodbreak</c>, <c>routedrop</c>) или свежий журнал <c>D2_PORT_PATHRESET_JOURNAL</c>: стёр ли порт путь того же полигона
/// (<c>MapView.DropRoute</c> печатает <c>routedrop</c>).</para>
/// </summary>
[Trait("Category", "Corpus")]
public class PathResetHarnessTests(ITestOutputHelper output)
{
    private const string Run = "20261020-path-reset";
    private const long LeftButton = 1;
    private static readonly string[] Kinds = ["path", "map_sel", "map_click", "ui_msgbox", "ui_click", "noble_action"];

    /// <summary>Отряды полигонов карты 28 (<c>docs/synthetic-maps-port/port-synth-28-path-reset/facts.json</c>).</summary>
    private const string Ally = "S143KC0004", RuinThief = "S143KC0006", SpyThief = "S143KC0007", RodBearer = "S143KC0008";

    /// <summary>Полигон: имя, отряд, стирает ли путь по листингу.</summary>
    private sealed record Polygon(string Name, string Stack, bool Drops);

    private static readonly Polygon AllyOk = new("AL «ОК» союзнику", Ally, true), RuinVisit = new("TH1 «Visit Ruin»", RuinThief, true),
                                    Spy = new("TH2 шпионаж", SpyThief, false), RodNo = new("RD «Нет» сносу", RodBearer, true);

    private static readonly Polygon[] Polygons = [AllyOk, RuinVisit, Spy, RodNo];

    /// <summary>Ответ движка по полигону: окно закрыто на <see cref="Seq"/>, отряд в клетке <see cref="Cell"/>, первый выбор той же клетки —
    /// <see cref="SelSeq"/> (null — выбора не было), показан ли при нём путь отряда (<see cref="Kept"/>).</summary>
    private sealed record Verdict(Polygon Polygon, long Seq, (long X, long Y) Cell, long? SelSeq, bool? Kept);

    [SkippableFact]
    public void Engine_DropsRouteAfterAllyRodNoAndRuinVisit()
    {
        var verdicts = EngineVerdicts();
        foreach (var v in verdicts)
            output.WriteLine($"{v.Polygon.Name} {v.Polygon.Stack}: окно закрыто seq {v.Seq}, клетка ({v.Cell.X},{v.Cell.Y}), выбор seq " +
                             $"{v.SelSeq?.ToString() ?? "-"} — путь {(v.Kept is null ? "не проверен" : v.Kept.Value ? "помнится" : "стёрт")}");
        int judged = 0;
        foreach (var v in verdicts.Where(v => v.Kept is not null))
        {
            judged++;
            Assert.True(v.Kept == !v.Polygon.Drops, $"{v.Polygon.Name} seq {v.Seq}: движок путь {(v.Kept == true ? "помнит" : "стёр")}, листинг — наоборот");
        }
        Skip.If(judged == 0, "в партии нет окон полигонов с путём до них и выбором после");
        var missing = Polygons.Where(p => !verdicts.Any(v => v.Polygon == p && v.Kept is not null)).Select(p => p.Name).ToList();
        if (missing.Count > 0) output.WriteLine($"не сыграны или без выбора после окна: {string.Join(", ", missing)}");
    }

    /// <summary>Порт стирает путь там же, где движок: по каждому полигону, сыгранному в обеих версиях, — <c>routedrop</c> того же отряда после
    /// окна у порта тогда и только тогда, когда движок путь стёр.</summary>
    [SkippableFact]
    public void Port_MatchesEngine()
    {
        // Ответ движка по полигону — первое окно с вердиктом (повторные окна сверяет Engine_…).
        var engine = EngineVerdicts().Where(v => v.Kept is not null).GroupBy(v => v.Polygon.Stack, StringComparer.OrdinalIgnoreCase)
                                     .ToDictionary(g => g.Key, g => g.First().Kept == false, StringComparer.OrdinalIgnoreCase);
        var ports = PortJournals();
        Skip.If(ports.Count == 0, "нет выписки или журнала порта карты 28");
        int compared = 0;
        foreach (string journal in ports)
        {
            var port = PortVerdicts(journal);
            foreach (var p in Polygons)
            {
                if (!port.TryGetValue(p.Stack, out bool dropped) || !engine.TryGetValue(p.Stack, out bool engineDropped)) continue;
                compared++;
                output.WriteLine($"{Path.GetFileName(journal)} {p.Name}: движок {(engineDropped ? "стёр" : "помнит")}, порт {(dropped ? "стёр" : "помнит")}");
                Assert.True(dropped == engineDropped, $"{Path.GetFileName(journal)} {p.Name}: порт путь {(dropped ? "стёр" : "помнит")}, движок — наоборот");
            }
        }
        Skip.If(compared == 0, "нет полигонов, сыгранных и в оригинале, и в порте");
    }

    private static List<Verdict> EngineVerdicts()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), Run);
        var journals = Directory.Exists(dir) ? Directory.GetFiles(dir, "passive-*.jsonl").OrderBy(p => p, StringComparer.Ordinal).ToList() : [];
        Skip.If(journals.Count == 0, $"нет партии карты 28 ({Run})");
        var verdicts = new List<Verdict>();
        foreach (string journal in journals)
        {
            var events = JournalCache.Read(journal, Kinds).OrderBy(e => e.Seq).ToList();
            var shows = events.OfType<AvProbeEvent>().Select(e => e.Av).Where(a => a.Ev == "map_sel" && a.S("action") == "show").ToList();
            foreach (var (polygon, seq) in Triggers(events))
                verdicts.Add(Judge(events, shows, polygon, seq));
        }
        return verdicts;
    }

    /// <summary>Окна полигонов: «ОК» после <c>X005TA0303</c> (AL), «Нет» после <c>X005TA0272</c> (RD), «ОК» окна итога после действия вора TH1
    /// у руины (<c>action</c> 12) и TH2 у деревни (<c>action</c> 1). Действия воров ИИ не берутся.</summary>
    private static IEnumerable<(Polygon Polygon, long Seq)> Triggers(IReadOnlyList<CorpusEvent> events)
    {
        string? asked = null;
        Polygon? thief = null;
        foreach (var e in events)
        {
            switch (e)
            {
                case UiMsgBoxEvent box:
                    asked = box.LastTextId?.ToUpperInvariant();
                    break;
                case UiClickEvent { Element: "BTN_OK" } c when asked == "X005TA0303":
                    asked = null;
                    yield return (AllyOk, c.Seq);
                    break;
                case UiClickEvent { Element: "BTN_NO" } c when asked == "X005TA0272":
                    asked = null;
                    yield return (RodNo, c.Seq);
                    break;
                case UiClickEvent { Screen: "DLG_ACTION_RESULT" } c when thief is not null:
                    yield return (thief, c.Seq);
                    thief = null;
                    asked = null;
                    break;
                case UiClickEvent:
                    asked = null;
                    break;
                case NobleActionEvent { Action: 12 } n when n.Stack.Equals(RuinThief, StringComparison.OrdinalIgnoreCase):
                    thief = RuinVisit;
                    break;
                case NobleActionEvent { Action: 1 } n when n.Stack.Equals(SpyThief, StringComparison.OrdinalIgnoreCase):
                    thief = Spy;
                    break;
            }
        }
    }

    /// <summary>До окна у отряда полигона был показан путь (последний <c>path</c> до окна — его: он выбран, путь есть), клетка отряда — последний
    /// эллипс до окна; первый выбор той же клетки после окна — новая задача выбора: путь помнится, если от ввода, вызвавшего выбор, до эллипса
    /// есть <c>path</c> этого отряда. Иначе вердикта нет (<see cref="Verdict.Kept"/> = null).</summary>
    private static Verdict Judge(IReadOnlyList<CorpusEvent> events, IReadOnlyList<AvEvent> shows, Polygon polygon, long seq)
    {
        bool Own(PathEvent p) => p.Stack.Equals(polygon.Stack, StringComparison.OrdinalIgnoreCase);
        var cellAt = shows.LastOrDefault(a => a.Seq < seq);
        var cell = (cellAt?.L("x") ?? -1, cellAt?.L("y") ?? -1);
        var sel = shows.FirstOrDefault(a => a.Seq > seq && a.S("via") == "select" && (a.L("x") ?? -1, a.L("y") ?? -1) == cell);
        var shownBefore = events.OfType<PathEvent>().LastOrDefault(p => p.Seq < seq);
        if (cellAt is null || sel is null || shownBefore is null || !Own(shownBefore)) return new Verdict(polygon, seq, cell, null, null);
        long input = events.Where(e => e.Seq < sel.Seq && e is UiClickEvent or MapClickEvent { Button: LeftButton })
                           .Select(e => e.Seq).DefaultIfEmpty(seq).Max();
        bool kept = events.OfType<PathEvent>().Any(p => p.Seq > input && p.Seq < sel.Seq && Own(p));
        return new Verdict(polygon, seq, cell, sel.Seq, kept);
    }

    /// <summary>Журналы порта: <c>D2_PORT_PATHRESET_JOURNAL</c> (путь или пути через «;») или выписки <c>golden/path-reset</c>.</summary>
    private static List<string> PortJournals()
    {
        if (Environment.GetEnvironmentVariable("D2_PORT_PATHRESET_JOURNAL") is { Length: > 0 } env)
            return [.. env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(File.Exists)];
        string dir = Path.Combine(AppContext.BaseDirectory, "golden", "path-reset");
        return Directory.Exists(dir) ? [.. Directory.GetFiles(dir, "*.jsonl").OrderBy(p => p, StringComparer.Ordinal)] : [];
    }

    /// <summary>Полигоны партии порта → стёр ли порт путь после первого окна отряда: окно союзника (<c>allyrefused</c>), окно вора у руины
    /// или деревни (<c>thief</c> <c>отряд -> цель</c>), вопрос сноса (<c>rodbreak ask</c>); стирание — <c>routedrop отряд</c>, отнесённый к последнему
    /// окну перед ним (того же отряда — иначе стирание не от окна).</summary>
    private static Dictionary<string, bool> PortVerdicts(string journal)
    {
        var windows = new List<(string Stack, bool Dropped)>();
        foreach (string line in File.ReadLines(journal))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ev", out var ev) || ev.GetString() != "print" || !root.TryGetProperty("text", out var t)
                || t.GetString() is not { } text) continue;
            string[] w = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? stack = w switch
            {
                ["allyrefused", var s, ..] => s,
                ["thief", var s, "->", ..] => s,
                ["rodbreak", "ask", var s, ..] => s,
                _ => null,
            };
            if (stack is not null) windows.Add((stack, false));
            else if (w is ["routedrop", var dropped] && windows.Count > 0 && windows[^1].Stack.Equals(dropped, StringComparison.OrdinalIgnoreCase))
                windows[^1] = (dropped, true);
        }
        return windows.GroupBy(x => x.Stack, StringComparer.OrdinalIgnoreCase)
                      .ToDictionary(g => g.Key, g => g.First().Dropped, StringComparer.OrdinalIgnoreCase);
    }
}
