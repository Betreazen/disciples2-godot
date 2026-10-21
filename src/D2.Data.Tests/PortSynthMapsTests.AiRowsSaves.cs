using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 18 против журнала и сохранений оригинала (<c>corpus/20261018b-port-synth</c>, сохранения t01-031 … t03-033, зонд 7.5):
/// ходы ИИ дней 1–2 — выбор тактики, его счёт, статус исполнения; итог дня — клетки отрядов и хозяева крепостей.</summary>
public partial class PortSynthMapsTests
{
    private const string AiRowsRun = "20261018b-port-synth";

    private static string AiRowsDir => Path.Combine(GameSaveTests.CorpusDir(), AiRowsRun);

    private static string AiRowsJournal => Path.Combine(AiRowsDir, "passive-001.jsonl");

    private static readonly string[] AiRowsKinds = ["save", "ai_tactic_pick", "ai_tactic_result", "fort_capture", "noble_choose"];

    /// <summary>События карты 18 по порядку и её сохранения, привязанные к строкам <c>save</c>.</summary>
    private static (List<CorpusEvent> Events, List<(string Path, SaveEvent Save)> Saves) AiRowsOriginal()
    {
        var events = JournalCache.Read(AiRowsJournal, AiRowsKinds).OrderBy(e => e.Seq).ToList();
        var saves = AiStrategicOrdersHarnessTests.BoundSaves(AiRowsDir, "passive-001.jsonl", events.OfType<SaveEvent>().ToList())
                                                 .Where(b => Path.GetFileName(b.Path).StartsWith(AiRowsMap, StringComparison.Ordinal))
                                                 .OrderBy(b => b.Save.Seq).ToList();
        if (saves.Count == 0) return ([], saves);
        long first = saves[0].Save.Seq, last = saves[^1].Save.Seq;
        return ([.. events.Where(e => e.Seq >= first && e.Seq <= last)], saves);
    }

    private static GameSession AiRowsSession(string sg, string human)
    {
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        var state = new GameState(new GameInfo(AiRowsMap, AiRowsMap, [human], 1, 1), looks) { Events = events, EventProgress = EventLoader.LoadProgress(sg, events) };
        return new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book) { EventBattlesOnScreen = false };
    }

    private static readonly Dictionary<long, string> AiRowsNames = Enum.GetValues<AiActionType>().ToDictionary(t => (long)t, t => t.ToString());

    /// <summary>
    /// Ходы ИИ дней 1–2 с сохранения оригинала t01-031: каждый выбор тактики (фаза 4 → 0: отряд, тип, цель, точка, счёт) и статус его
    /// исполнения (<c>ai_tactic_result</c>) по порядку — у порта те же, с той же точкой и счётом; захваты крепостей (<c>fort_capture</c>) —
    /// те же; утром дней 2 и 3 клетки всех отрядов и хозяева крепостей — как в сохранениях t02-032 и t03-033. До 19.10 порт расходился:
    /// IL — отряд иллюзии <c>S143KC000a</c> ходов не делал (стратегия порта брала его в списки сил и назначала помощником, у движка
    /// <c>0x476374</c> → <c>0x431d8c</c> иллюзию из списков выкидывает; у него <c>SummonAttack</c> −168 и −212, статус 5); ML —
    /// <c>MoveToLocation</c> <c>S143KC0010</c> вставал у ворот пустой деревни (у движка захват тем же ходом, точка — угол (20,116)); DP — статус
    /// <c>DefendParty</c> 2 вместо 5, и назавтра счёт 150 вместо 187,5.
    /// </summary>
    [SkippableFact]
    public void AiRows_AiTurnsMatchTheOriginalJournal()
    {
        Skip.IfNot(MapIds().Contains(AiRowsMap));
        Skip.IfNot(File.Exists(AiRowsJournal), $"нет {AiRowsRun}");
        var (events, saves) = AiRowsOriginal();
        Skip.If(saves.Count < 3, $"сохранений карты 18: {saves.Count}");
        string human = Str(Facts(AiRowsMap).GetProperty("human"), "player");

        var engine = new List<string>();
        var captures = new List<string>();
        string? pending = null;
        foreach (var e in events)
        {
            if (e is AiTacticPickEvent { PhaseBefore: 4, Chosen: [{ Action: { } a } c, ..] } p)
                pending = $"{p.Player} {a.Stack} {AiRowsNames.GetValueOrDefault(a.Type ?? -1, $"{a.Type}")} {a.Target} ({a.Point?.X},{a.Point?.Y}) {c.Score:0.##}";
            else if (e is AiTacticResultEvent r && pending is not null)
            {
                engine.Add($"{pending} → {r.Status}");
                pending = null;
            }
            else if (e is FortCaptureEvent { Captured: true } f) captures.Add($"{f.Fort} {f.OwnerBefore} → {f.OwnerAfter}");
        }
        Assert.Equal(18, engine.Count);   // выборов ИИ дней 1–2 у движка (до проверки — чтобы смена корпуса падала понятно)

        var session = AiRowsSession(saves[0].Path, human);
        // NB: список действий вора у деревни Нежити, знающей только призыв (noble_choose: 1 3 4 6 9 13 — без кражи заклинания, 0x5cdf6f).
        var chooses = events.OfType<NobleChooseEvent>().ToList();
        Assert.NotEmpty(chooses);
        foreach (var n in chooses)
        {
            var mine = session.ThiefActions(n.Stack, n.Target).Select(t => (long)t).Order().ToList();
            Out.WriteLine($"NB {n.Stack} → {n.Target}: движок [{string.Join(" ", n.Actions)}], порт [{string.Join(" ", mine)}]");
            Assert.Equal(n.Actions.Order(), mine);
        }
        var trace = new TacticTrace();
        session.AiTrace = trace;
        var port = new List<string>();
        var portCaptures = new List<string>();
        var diffs = new List<string>();
        for (int day = 1; day <= 2; day++)
        {
            var forts = AiTactics.Forts(session.Map).ToDictionary(f => f.Id, f => f.Owner, StringComparer.OrdinalIgnoreCase);
            int from = trace.Picks.Count;
            var report = session.EndTurn();
            var statuses = report.Events.Where(l => l.StartsWith("тактика ", StringComparison.Ordinal))
                                        .Select(l => l[(l.LastIndexOf('→') + 2)..]).ToList();
            var picks = trace.Picks.Skip(from).ToList();
            Assert.Equal(picks.Count, statuses.Count);   // строки «тактика … → статус» и выборы трассы — один к одному
            for (int i = 0; i < picks.Count; i++)
            {
                var p = picks[i];
                port.Add($"{p.Player} {p.Stack} {p.Action} {p.Target} ({p.X},{p.Y}) {p.Score:0.##} → {(i < statuses.Count ? statuses[i] : "?")}");
            }
            foreach (var f in AiTactics.Forts(session.Map))
                if (forts.TryGetValue(f.Id, out var before) && !before.Equals(f.Owner, StringComparison.OrdinalIgnoreCase))
                    portCaptures.Add(f.Id + $" {before} → {f.Owner}");
            var morning = AiRowsSession(saves[day].Path, human).Map;
            foreach (var s in morning.Stacks.Values)
            {
                if (!session.Map.Stacks.TryGetValue(s.Id, out var mine)) diffs.Add($"утро дня {day + 1}: у порта нет {s.Id}");
                else if ((mine.X, mine.Y, mine.Inside) != (s.X, s.Y, s.Inside))
                    diffs.Add($"утро дня {day + 1}: {s.Id} движок ({s.X},{s.Y}) {s.Inside}, порт ({mine.X},{mine.Y}) {mine.Inside}");
            }
            foreach (var s in session.Map.Stacks.Values.Where(s => !morning.Stacks.ContainsKey(s.Id)))
                diffs.Add($"утро дня {day + 1}: у движка нет {s.Id}");
            foreach (var f in AiTactics.Forts(morning))
                if (AiTactics.Forts(session.Map).FirstOrDefault(x => x.Id == f.Id) is { } mine && !mine.Owner.Equals(f.Owner, StringComparison.OrdinalIgnoreCase))
                    diffs.Add($"утро дня {day + 1}: {f.Id} хозяин движок {f.Owner}, порт {mine.Owner}");
        }
        foreach (var l in engine) Out.WriteLine("движок " + l);
        foreach (var l in port) Out.WriteLine("порт   " + l);
        for (int i = 0; i < Math.Max(engine.Count, port.Count); i++)
        {
            string a = i < engine.Count ? engine[i] : "—", b = i < port.Count ? port[i] : "—";
            if (a != b) diffs.Add($"выбор {i + 1}: движок {a}; порт {b}");
        }
        Out.WriteLine($"захваты: движок [{string.Join("; ", captures)}], порт [{string.Join("; ", portCaptures)}]");
        if (!captures.SequenceEqual(portCaptures)) diffs.Add($"захваты: движок [{string.Join("; ", captures)}], порт [{string.Join("; ", portCaptures)}]");
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }
}
