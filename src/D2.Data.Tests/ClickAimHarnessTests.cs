using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Прицел щелчка выбранным отрядом (<c>docs/map-path.md</c> §4, <c>0x4d04c3</c>; <see cref="MapCursors.ClickCell"/>, <see cref="GameSession.ClickAim"/>)
/// и подтверждение показанного пути (<c>0x4cc8c2</c>, <see cref="GameSession.ClickConfirms"/>) — по всем журналам со щелчками карты
/// (<c>map_click</c>, схема 7.1+): щелчок <c>CTaskSelectUnit</c> по клетке C и путь, который он показал (<c>path</c> — <c>showMovementPath</c>,
/// его <c>end</c> — цель искателя <c>+0x14</c>, а не клетка под мышью). Цель (x+1, y+1) у пустой C — прицел движка. Порт берёт сохранение окна
/// и сверяет только щелчки, вокруг которых с сохранения никто не ходил (отряд, C и (x+1, y+1) — как в сохранении).
/// </summary>
[Trait("Category", "Corpus")]
public class ClickAimHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly string[] Kinds = ["save", "stack_move", "map_click", "path"];

    /// <summary>Прицелов в корпусе 17.10 — 41 (карта 10, VA1–VA3, и 20261016e-av); меньше двадцати — корпус не тот.</summary>
    private const int MinAimed = 20;

    /// <summary>Окно от щелчка до пути, который он показал (seq): путь пишется внутри обработчика щелчка.</summary>
    private const long PathWindow = 5000;

    /// <summary>Каждый сверяемый щелчок: прицел порта = прицел движка; после прицеленного щелчка (путь к отряду под клеткой) отряд ни разу не
    /// пошёл до следующего щелчка — повторный щелчок по той же клетке путь не подтверждает (17.10, VA1: 15 щелчков по (10,20) подряд, хода нет).</summary>
    [SkippableFact]
    public void ClickAboveForeignStack_AimsAtIt_AndNeverConfirms()
    {
        var t = Survey();
        output.WriteLine($"сверено щелчков {t.Checked}, прицел у порта {t.PortAimed}; прицелов движка в корпусе {t.Aimed}, после прицела пошёл {t.MovedAfterAim}");
        foreach (var d in t.AimDiffs) output.WriteLine(d);
        Skip.If(t.Checked == 0, "в корпусе нет щелчков карты");
        Assert.Empty(t.AimDiffs);
        Assert.True(t.Aimed >= MinAimed, $"прицелов {t.Aimed}");
        Assert.True(t.PortAimed >= 1, "ни одного прицела не сверено портом");
        Assert.Equal(0, t.MovedAfterAim);
    }

    /// <summary>Подтверждение (<see cref="GameSession.ClickConfirms"/>): следующий щелчок того же отряда после показанного пути — движок пошёл
    /// (<c>stack_move</c> до следующего щелчка, без нового <c>path</c>) или показал путь заново; порт по показанному им пути к прицелу прежнего
    /// щелчка решает так же. Среди сверенных — щелчки по клетке над врагом (не подтверждают) и по концу пути к врагу (подтверждают).</summary>
    [SkippableFact]
    public void SecondClick_ConfirmsLikeTheEngine()
    {
        var t = Survey();
        output.WriteLine($"следующих щелчков сверено {t.Seconds}: подтвердил движок {t.EngineConfirmed}, из них по другой клетке {t.ConfirmedElsewhere}; " +
                         $"повторов щелчка над врагом без хода {t.AimRepeats}");
        foreach (var d in t.ConfirmDiffs) output.WriteLine(d);
        Skip.If(t.Seconds == 0, "в корпусе нет повторных щелчков");
        Assert.Empty(t.ConfirmDiffs);
        Assert.True(t.AimRepeats >= 1, "нет повтора щелчка над врагом");
        Assert.True(t.EngineConfirmed >= 1, "нет подтверждённых щелчков");
    }

    private sealed class Tally
    {
        public int Checked, Aimed, PortAimed, MovedAfterAim, Seconds, EngineConfirmed, ConfirmedElsewhere, AimRepeats;
        public List<string> AimDiffs { get; } = [];
        public List<string> ConfirmDiffs { get; } = [];
    }

    private static readonly Lazy<Tally> Surveyed = new(Run);

    private static Tally Survey() => Surveyed.Value;

    private static Tally Run()
    {
        var content = SharedContent.Load(Data);
        var t = new Tally();
        foreach (var (dir, journal) in Journals())
        {
            var events = JournalCache.Read(Path.Combine(dir, journal), Kinds).OrderBy(e => e.Seq).ToList();
            if (!events.OfType<MapClickEvent>().Any()) continue;
            List<(string Path, SaveEvent Save)> bound;
            try { bound = [.. AiStrategicOrdersHarnessTests.BoundSaves(dir, journal, events.OfType<SaveEvent>().ToList()).OrderBy(b => b.Save.Seq)]; }
            catch (InvalidOperationException) { continue; }
            for (int w = 0; w < bound.Count; w++)
            {
                long from = bound[w].Save.Seq, to = w + 1 < bound.Count ? bound[w + 1].Save.Seq : long.MaxValue;
                var window = events.Where(e => e.Seq > from && e.Seq < to).ToList();
                if (window.OfType<MapClickEvent>().Any()) Scan(t, Path.GetFileName(dir), Session(bound[w].Path, content), window, to);
            }
        }
        return t;
    }

    private static void Scan(Tally t, string dir, GameSession game, List<CorpusEvent> window, long to)
    {
        var moved = new Dictionary<string, (long X, long Y)>(StringComparer.OrdinalIgnoreCase);
        var clicks = window.OfType<MapClickEvent>().ToList();
        // Прежний сверенный щелчок: отряд, прицел и путь, показанный портом.
        (string Stack, (int X, int Y) Aim, StackPath Path)? shownByPort = null;
        foreach (var e in window)
        {
            if (e is StackMoveEvent m) { moved[m.Stack] = (m.X, m.Y); shownByPort = null; continue; }
            if (e is not MapClickEvent c) continue;
            if (c.Button != 1 || c.Task != "CTaskSelectUnit" || c.Cell is not { } cell || c.Objects is null) { shownByPort = null; continue; }
            long next = clicks.FirstOrDefault(k => k.Seq > c.Seq)?.Seq ?? to;
            var shown = window.OfType<PathEvent>().FirstOrDefault(p => p.Seq > c.Seq && p.Seq < Math.Min(next, c.Seq + PathWindow));
            // Подтверждённый щелчок — ход (stack_move) раньше любого пути: после хода интерфейс показывает путь заново уже с новой клетки.
            var firstMove = window.OfType<StackMoveEvent>().FirstOrDefault(s => s.Seq > c.Seq && s.Seq < next);
            bool engineConfirms = firstMove is not null && (shown is null || firstMove.Seq < shown.Seq);
            if (engineConfirms) shown = null;
            (int X, int Y) at = ((int)cell.X, (int)cell.Y), below = (at.X + 1, at.Y + 1);
            if (shownByPort is { } prev && game.Map.Stacks.TryGetValue(prev.Stack, out var mover) && (shown is null || Same(shown.Stack, prev.Stack))
                && (shown is not null || engineConfirms))
            {
                t.Seconds++;
                if (engineConfirms)
                {
                    t.EngineConfirmed++;
                    if (at != prev.Aim) t.ConfirmedElsewhere++;
                }
                else if (prev.Aim == below) t.AimRepeats++;
                bool port = game.ClickConfirms(mover, prev.Path, prev.Aim, at.X, at.Y);
                if (port != engineConfirms)
                    t.ConfirmDiffs.Add($"{dir} seq {c.Seq}: {prev.Stack} показан путь к {prev.Aim}, щёлкнул {at}: движок " +
                                       $"{(engineConfirms ? "пошёл" : "показал заново")}, порт {(port ? "пошёл" : "показал заново")}");
            }
            shownByPort = null;
            if (shown is null) continue;
            bool engineAims = c.Objects.Count == 0 && (shown.EndX, shown.EndY) == below;
            if (engineAims)
            {
                t.Aimed++;
                if (window.OfType<StackMoveEvent>().Any(s => s.Seq > c.Seq && s.Seq < next && Same(s.Stack, shown.Stack))) t.MovedAfterAim++;
            }
            // Порт — по сохранению окна: только если вокруг с него никто не ходил.
            if (!game.Map.Stacks.TryGetValue(shown.Stack, out var stack) || moved.ContainsKey(stack.Id)) continue;
            if (moved.Values.Any(p => (p.X, p.Y) == at || (p.X, p.Y) == below)) continue;
            if (game.Map.Plan.TryGetValue(below, out var ids) && ids.Any(moved.ContainsKey)) continue;
            if (!game.Map.Plan.ContainsKey(at) != (c.Objects.Count == 0)) continue;
            t.Checked++;
            var aim = game.ClickAim(stack, at.X, at.Y);
            bool portAims = aim == below;
            if (portAims) t.PortAimed++;
            if (portAims != engineAims)
                t.AimDiffs.Add($"{dir} seq {c.Seq}: {shown.Stack} щёлкнул {at}, движок — цель ({shown.EndX},{shown.EndY}), порт {aim}");
            var path = game.PathTo(stack, aim.X, aim.Y);
            if (path.Points.Count > 0) shownByPort = (stack.Id, aim, path);
        }
    }

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
        return new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), content.MapArt);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
