using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Путь через конец хода (<c>docs/map-path.md</c> §4 «Путь через конец хода», реестр Ин80; <see cref="StackRoutes"/>) — по всем журналам со
/// щелчками карты: щелчок <c>CTaskSelectUnit</c> по клетке C показал путь отряда S (<c>path</c> с <c>end</c> = C), человек кончил ход, и после окна
/// начала хода (<c>DLG_BEGIN_TURN</c>) движок снова показывает путь S к C (<c>path</c> того же отряда и той же цели), не дожидаясь щелчка по C —
/// отряд выбран через конец хода или выбран снова (Tab), а путь он помнит. Порт берёт автосохранение конца хода (<c>save</c> сразу за
/// <c>BTN_END_TURN</c>): путь к C с очками из сохранения — как <c>path</c> до конца хода (длина и последняя достижимая), остаток того же пути с
/// запасом нового дня (<c>ap_restore</c> <c>turn_start</c> отряда, <see cref="GameSession.KeptPath"/>) — как <c>path</c> после.
/// </summary>
[Trait("Category", "Corpus")]
public class TurnStartPathHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<GameBook> Book = new(() => GameBook.Load(Path.Combine(Data, "rules")));

    private static readonly string[] Kinds = ["save", "stack_move", "map_click", "path", "ui_click", "ui_close", "ap_restore"];

    /// <summary>Случаев в корпусе 20.10 — 19; меньше пятнадцати — корпус не тот.</summary>
    private const int MinCases = 15;

    /// <summary>Движок: в каждом случае путь в начале хода — той же длины, что до конца хода (точки те же, их не ищут заново), а последняя
    /// достижимая у части случаев сдвинулась (запас нового дня). Порт — на случаях, где его путь по сохранению конца хода совпадает с
    /// показанным до конца хода (отряд не ходил после щелчка, цель щелчка на месте), а вокруг пути ночью никто не ходил: остаток с запасом нового
    /// дня (<see cref="GameSession.KeptPath"/>) — та же длина и та же последняя достижимая.</summary>
    [SkippableFact]
    public void KeptPath_ReshownAtTurnStart_LikeTheEngine()
    {
        var content = SharedContent.Load(Data);
        int cases = 0, lastMoved = 0, compared = 0, otherPath = 0, nightMoves = 0;
        var engine = new List<string>();
        var diffs = new List<string>();
        foreach (var (dir, journal) in Journals())
        {
            var events = JournalCache.Read(Path.Combine(dir, journal), Kinds).OrderBy(e => e.Seq).ToList();
            if (!events.OfType<MapClickEvent>().Any()) continue;
            List<(string Path, SaveEvent Save)> bound;
            try { bound = [.. AiStrategicOrdersHarnessTests.BoundSaves(dir, journal, events.OfType<SaveEvent>().ToList())]; }
            catch (InvalidOperationException) { continue; }
            var ends = events.OfType<UiClickEvent>().Where(c => c.Element == "BTN_END_TURN").ToList();
            for (int k = 0; k < ends.Count; k++)
            {
                long end = ends[k].Seq, next = k + 1 < ends.Count ? ends[k + 1].Seq : long.MaxValue;
                if (Case(events, end, next) is not { } c) continue;
                cases++;
                string at = $"{Path.GetFileName(dir)} {c.Pre.Stack} к ({c.Pre.EndX},{c.Pre.EndY})";
                if (c.Post.Len != c.Pre.Len) engine.Add($"{at}: длина до конца хода {c.Pre.Len}, в начале хода {c.Post.Len}");
                if ((c.Pre.LastX, c.Pre.LastY) != (c.Post.LastX, c.Post.LastY)) lastMoved++;
                var save = bound.Where(b => b.Save.Seq > end && b.Save.Seq < c.Post.Seq).OrderBy(b => b.Save.Seq).FirstOrDefault();
                if (save.Path is null) continue;
                var game = Session(save.Path, content);
                if (!game.Map.Stacks.TryGetValue(c.Pre.Stack, out var stack)) continue;
                var path = game.PathTo(stack, (int)c.Pre.EndX, (int)c.Pre.EndY);
                if (c.WalkedSinceClick || Line(path) != Line(c.Pre)) { otherPath++; continue; }
                if (c.NightMoves.Any(m => path.Points.Any(p => Math.Abs(p.X - m.X) <= 2 && Math.Abs(p.Y - m.Y) <= 2))) { nightMoves++; continue; }
                compared++;
                var morning = game.KeptPath(stack with { Move = (int)c.Move }, path, 0);
                output.WriteLine($"{at}: до конца хода {Line(c.Pre)}, в начале хода (запас {c.Move}) движок {Line(c.Post)}, порт {Line(morning)}");
                if (Line(morning) != Line(c.Post))
                    diffs.Add($"{at}: в начале хода (seq {c.Post.Seq}, запас {c.Move}) движок {Line(c.Post)}, порт {Line(morning)}");
            }
        }
        output.WriteLine($"случаев {cases}: последняя достижимая сдвинулась {lastMoved}; порт сверен {compared}, путь порта другой {otherPath} " +
                         $"(отряд шёл после щелчка или цель — отряд, убитый в бою: движок помнит точки), ночью ходили у пути {nightMoves}");
        foreach (var d in engine.Concat(diffs)) output.WriteLine(d);
        Skip.If(cases == 0, "в корпусе нет щелчков карты");
        Assert.Empty(engine);
        Assert.Empty(diffs);
        Assert.True(cases >= MinCases, $"случаев {cases}");
        Assert.True(lastMoved >= 1, "ни у одного пути запас нового дня не сдвинул последнюю достижимую");
        Assert.True(compared >= 1, "ни одного случая не сверено портом");
    }

    /// <summary>Случай: путь до конца хода и после окна начала хода, запас нового дня, шёл ли отряд после щелчка, кто и где ходил ночью.</summary>
    private sealed record Kept(PathEvent Pre, PathEvent Post, long Move, bool WalkedSinceClick, IReadOnlyList<(long X, long Y)> NightMoves);

    /// <summary>Случай конца хода <paramref name="end"/>: путь, показанный последним щелчком до него, и тот же путь после окна начала хода —
    /// раньше любого щелчка по его цели и любого хода этого отряда; запас — <c>ap_restore</c> <c>turn_start</c> отряда.</summary>
    private static Kept? Case(List<CorpusEvent> events, long end, long next)
    {
        var click = events.OfType<MapClickEvent>().LastOrDefault(c => c.Seq < end && c.Button == 1 && c.Task == "CTaskSelectUnit" && c.Cell is not null);
        if (click?.Cell is not { } cell) return null;
        var pre = events.OfType<PathEvent>().LastOrDefault(p => p.Seq > click.Seq && p.Seq < end && (p.EndX, p.EndY) == (cell.X, cell.Y));
        if (pre is null || events.OfType<StackMoveEvent>().Any(m => m.Seq > pre.Seq && m.Seq < end && Same(m.Stack, pre.Stack))) return null;
        var begun = events.OfType<UiCloseEvent>().FirstOrDefault(u => u.Seq > end && u.Seq < next && u.Name == "DLG_BEGIN_TURN");
        if (begun is null) return null;
        var post = events.OfType<PathEvent>().FirstOrDefault(p => p.Seq > begun.Seq && p.Seq < next && Same(p.Stack, pre.Stack)
                                                                  && (p.EndX, p.EndY) == (pre.EndX, pre.EndY));
        if (post is null) return null;
        if (events.OfType<MapClickEvent>().Any(c => c.Seq > begun.Seq && c.Seq < post.Seq && c.Cell is { } at && (at.X, at.Y) == (cell.X, cell.Y)))
            return null;   // щёлкнул по цели снова — путь показан щелчком, а не памятью
        if (events.OfType<StackMoveEvent>().Any(m => m.Seq > end && m.Seq < post.Seq && Same(m.Stack, pre.Stack))) return null;
        var restore = events.OfType<ApRestoreEvent>().FirstOrDefault(r => r.Seq > end && r.Seq < post.Seq && Same(r.Stack, pre.Stack)
                                                                         && r.Cause == "turn_start");
        if (restore is null) return null;
        bool walked = events.OfType<StackMoveEvent>().Any(m => m.Seq > click.Seq && m.Seq < pre.Seq && Same(m.Stack, pre.Stack));
        var night = events.OfType<StackMoveEvent>().Where(m => m.Seq > end && m.Seq < post.Seq).Select(m => (m.X, m.Y)).ToList();
        return new Kept(pre, post, restore.MoveAfter, walked, night);
    }

    private static string Line(PathEvent p) => $"len {p.Len} last ({p.LastX},{p.LastY})";

    private static string Line(StackPath p) =>
        p.Points.Count == 0 ? "len 0" : $"len {p.Points.Count} last ({p.Points[p.Reachable].X},{p.Points[p.Reachable].Y})";

    private static IEnumerable<(string Dir, string Journal)> Journals()
    {
        foreach (string dir in Directory.GetDirectories(GameSaveTests.CorpusDir()).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
            foreach (string journal in Directory.GetFiles(dir, "*.jsonl").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
                yield return (dir, journal);
        }
    }

    private static GameSession Session(string sg, Content.GameContent content)
    {
        var looks = MapLooksLoader.Load(sg);
        return new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), content.MapArt, Book.Value);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
