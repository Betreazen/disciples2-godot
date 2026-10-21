using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Шаг отряда ключевыми кадрами (<see cref="StackWalk"/>, задача движения <c>0x4f5b8d</c>/<c>0x4f61e2</c>) и обзор по каждой точке
/// пути (<see cref="GameSession.MoveStack"/>, <c>CEffectMove::apply</c> <c>0x43a0cf</c>; <see cref="GameSession.RevealFrom"/>, <c>0x4f6856</c>).</summary>
[Trait("Category", "Corpus")]
public class StackWalkTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Шаг кадра: 14 / 30 / 52 точки по скорости, пополам при <c>StackSmoothScroll</c> поставляемого ini (с отбрасыванием дроби).
    /// Кадр — тик группы хода: 33 мс при <c>StackSmoothScroll</c>, 66 без него (<c>0x4f5e7c</c>). Журнал <c>20260919-human</c> снят с ini
    /// драйвера (скорость 3, без плавности: <c>s</c> = 52) — шаг в 32 точки 1 кадр (62–78 мс), в 64 точки — 2 кадра (125–141 мс).</summary>
    [Fact]
    public void Keyframes_ByEngineStep()
    {
        Assert.Equal(7, StackWalk.PixelsPerKeyframe(MapSettings.SpeedFactor(1)));
        Assert.Equal(15, StackWalk.PixelsPerKeyframe(MapSettings.SpeedFactor(2)));
        Assert.Equal(26, StackWalk.PixelsPerKeyframe(MapSettings.SpeedFactor(3)));
        // Флажок окна «Плавная прокрутка при движении отряда» выключен — шаг не делится (0x4f5d59).
        Assert.Equal((14, 52), (StackWalk.PixelsPerKeyframe(MapSettings.SpeedFactor(1), smooth: false),
                                StackWalk.PixelsPerKeyframe(MapSettings.SpeedFactor(3), smooth: false)));

        var straight = StackWalk.Keyframes(32, 16, 7);
        Assert.Equal([(0, 0), (7, 4), (14, 7), (21, 11), (28, 14)], straight);
        var down = StackWalk.Keyframes(0, 32, 7);
        Assert.Equal(5, down.Count);
        Assert.All(down, p => Assert.Equal(0, p.X));
        var diagonal = StackWalk.Keyframes(-64, 0, 7);
        Assert.Equal(10, diagonal.Count);
        Assert.Equal((-63, 0), diagonal[^1]);
        Assert.Equal([(0, 0)], StackWalk.Keyframes(0, 0, 7));
        Assert.Single(StackWalk.Keyframes(32, 16, 52));
        Assert.Equal((33, 66), (StackWalk.KeyframeMs(smooth: true), StackWalk.KeyframeMs(smooth: false)));
        // Журнал: s = 52, медленная группа — 1 и 2 кадра по 66 мс (часы журнала — кванты 15,6 мс: 62/78 и 125/141).
        Assert.InRange(StackWalk.Keyframes(32, 16, 52).Count * StackWalk.KeyframeMs(smooth: false), 62, 78);
        Assert.InRange(StackWalk.Keyframes(-64, 0, 52).Count * StackWalk.KeyframeMs(smooth: false), 125, 141);
        // Скорость в точках за секунду от флажка не зависит: 7 точек за 33 мс и 14 за 66.
        Assert.Equal(StackWalk.PixelsPerKeyframe(1f, smooth: true) * StackWalk.KeyframeMs(smooth: false),
                     StackWalk.PixelsPerKeyframe(1f, smooth: false) * StackWalk.KeyframeMs(smooth: true));
        // Шаг вверх: точки отрезка движка строятся от нижнего конца (0x51f522) — у (32, −16) кадр 7 — (7, −3), а не (7, −4).
        Assert.Equal([(0, 0), (7, -3), (14, -7), (21, -10), (28, -14)], StackWalk.Keyframes(32, -16, 7));
    }

    /// <summary>Легионы, день 1 (туман закрыт): отряд из столицы идёт 7 шагов к 14,92. Обзор открывается по каждой клетке пути — клетки,
    /// видные только с середины пути, тоже открыты (прежде — только вокруг конечной клетки). <see cref="GameSession.RevealFrom"/> на первой
    /// клетке шага открывает часть тумана сразу и при повторе ничего не меняет.</summary>
    [SkippableFact]
    public void MoveStack_RevealsEveryPathCell()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260920-legions", "rastlennaya-vlast-heretic-t01-001.sg");
        Skip.IfNot(File.Exists(sg), "нет корпуса");
        var looks = MapLooksLoader.Load(sg);
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        string legions = looks.Map.Stacks["S073KC0002"].Owner;
        GameSession Fresh() => new(new GameState(new GameInfo("PtC", "PtC", [legions], 1, 1), MapLooksLoader.Load(sg)), content.MapArt, book);

        var session = Fresh();
        var stack = session.Map.Stacks["S073KC0002"];
        var path = session.PathTo(stack, 14, 92);
        var today = path.Today;
        Assert.True(today.Count >= 3);
        var fog = session.Map.Fog[session.Map.Players[legions].FogId];
        int hiddenBefore = Count(fog);
        Assert.True(session.RevealFrom(stack.Id, today[0].X, today[0].Y) || Count(fog) == hiddenBefore);
        Assert.False(session.RevealFrom(stack.Id, today[0].X, today[0].Y));   // второй раз — ничего нового

        var end = today[^1];
        var before = today.Count > 1 ? today[^2] : path.Points[0];
        var walked = Fresh();
        walked.MoveStack(stack.Id, end.X, end.Y, PathFinder.Facing(before, end), path.Points[today.Count].Cost, null, today);
        var jumped = Fresh();
        jumped.MoveStack(stack.Id, end.X, end.Y, PathFinder.Facing(before, end), path.Points[today.Count].Cost);
        int byPath = Count(walked.Map.Fog[walked.Map.Players[legions].FogId]);
        int byEnd = Count(jumped.Map.Fog[jumped.Map.Players[legions].FogId]);
        Assert.True(byPath < byEnd, $"скрыто по пути {byPath}, только по концу {byEnd}");
    }

    private static int Count(bool[,] fog)
    {
        int n = 0;
        foreach (bool cell in fog) if (cell) n++;
        return n;
    }
}
