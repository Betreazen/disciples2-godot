using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Пути отрядов через конец хода (<see cref="StackRoutes"/>, <c>docs/map-path.md</c> §4 «Путь через конец хода», реестр Ин80) на карте 13:
/// интерфейс помнит путь за каждым отрядом (<c>0x41a799</c>/<c>0x41a96d</c>), ход срезает пройденное (<c>0x41a84d</c>), начало дня путей не
/// стирает (<c>0x41a3f4</c>) — остаток показывается с новым запасом; на остатке отряд, жезл или мешок — путь стирается (<c>0x4cfead</c>).</summary>
public partial class PortSynthMapsTests
{
    private const string RouteA = "S143KC0013", RouteB = "S143KC0016";

    /// <summary>Тот же день с полным запасом у всех отрядов — как после начала хода (<c>ap_restore</c>).</summary>
    private static GameSession NextMorning(GameSession s)
    {
        var stacks = s.Map.Stacks.ToDictionary(p => p.Key, p => p.Value with { Move = s.DayAllowance(p.Value) }, StringComparer.OrdinalIgnoreCase);
        return new GameSession(s.State with { Looks = GameSession.Retold(s.Looks, GameSession.With(s.Map, stacks: stacks), s.Looks.Stacks) },
                               s.Art, s.Book);
    }

    /// <summary>Отряд идёт по сегодняшней части пути (команда, как у сцены карты: <see cref="PlayerMove.Plan"/>, <see cref="GameSession.MoveStack"/>).</summary>
    private static int WalkToday(GameSession session, string id, StackPath path, (int X, int Y) aim)
    {
        var move = PlayerMove.Plan(session, id, path, aim);
        var stop = move.Today[^1];
        session.MoveStack(id, stop.X, stop.Y, 0, move.Charge ?? stop.Cost, move.Fired, move.Today);
        return move.Today.Count;
    }

    /// <summary>A идёт к цели дальше дневного запаса и встаёт без очков: остаток — весь «не сегодня» (белые флажки), «Продолжить» выключена.
    /// B получает свой путь — путь A помнится. Утром оба пути на месте, у A все точки остатка достижимы, «Продолжить» доступна; дошедший до
    /// цели пути больше не помнит.</summary>
    [SkippableFact]
    public void Routes_KeptForEveryStack_ThroughTheNight()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var session = Begin(Path3Map, 1);
        var routes = new StackRoutes();
        var a = session.Map.Stacks[RouteA];
        var aim = (a.X, a.Y + 16);
        var path = session.PathTo(a, aim.Item1, aim.Item2);
        Assert.True(path.Reachable < path.Points.Count - 1, "путь A дойдёт за день — карта изменилась");
        routes.Remember(RouteA, path, aim);
        int walked = WalkToday(session, RouteA, path, aim);
        routes.Walked(RouteA, path, walked, aim);

        a = session.Map.Stacks[RouteA];
        Assert.Equal(0, a.Move);
        var evening = routes.Show(session, a);
        Assert.NotNull(evening);
        Assert.Equal(path.Points.Count - walked, evening.Value.Path.Points.Count);
        Assert.Equal(0, evening.Value.Path.Reachable);   // очков нет — весь остаток следующим днём
        Assert.Equal(aim, evening.Value.Target);
        Assert.False(routes.CanContinue(a));

        var b = session.Map.Stacks[RouteB];
        routes.Remember(RouteB, session.PathTo(b, b.X, b.Y + 16), (b.X, b.Y + 16));
        Assert.Equal(2, routes.Count);

        var morning = NextMorning(session);
        a = morning.Map.Stacks[RouteA];
        var shown = routes.Show(morning, a);
        Assert.NotNull(shown);
        Assert.Equal(evening.Value.Path.Points.Count - 1, shown.Value.Path.Reachable);   // с новым запасом остаток — сегодня
        Assert.Equal(evening.Value.Path.Points.Select(p => (p.X, p.Y)), shown.Value.Path.Points.Select(p => (p.X, p.Y)));   // точки те же
        Assert.True(routes.CanContinue(a));
        Assert.NotNull(routes.Show(morning, morning.Map.Stacks[RouteB]));

        int rest = WalkToday(morning, RouteA, shown.Value.Path, aim);
        routes.Walked(RouteA, shown.Value.Path, rest, aim);
        a = morning.Map.Stacks[RouteA];
        Assert.Equal(aim, (a.X, a.Y));
        Assert.Null(routes.Show(morning, a));   // дошёл — пути нет (0x4cfdff: клетка = цель)
        Assert.False(routes.Has(RouteA));
        Assert.True(routes.Has(RouteB));
    }

    /// <summary>На остатке пути встал другой отряд — выбор отряда путь стирает (<c>0x4cfead</c> → <c>0x41a808</c>); отряд ушёл с клетки, где его
    /// оставил ход, — путь устарел. Ход без цели (к жезлу) и пустой путь — пути нет.</summary>
    [SkippableFact]
    public void Routes_BlockedOrStale_Forgotten()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var session = Begin(Path3Map, 1);
        var routes = new StackRoutes();
        var a = session.Map.Stacks[RouteA];
        var aim = (a.X, a.Y + 16);
        var path = session.PathTo(a, aim.Item1, aim.Item2);
        routes.Remember(RouteA, path, aim);
        var block = path.Points[^3];
        session.MoveStack(RouteB, block.X, block.Y, 0, 0);
        Assert.Null(routes.Show(session, session.Map.Stacks[RouteA]));
        Assert.False(routes.Has(RouteA));

        routes.Remember(RouteA, path, aim);
        var side = path.Points[1];
        session.MoveStack(RouteA, side.X + 1, side.Y, 0, 0);   // не по пути
        Assert.Null(routes.Show(session, session.Map.Stacks[RouteA]));
        Assert.False(routes.Has(RouteA));

        routes.Remember(RouteA, path, aim);
        routes.Walked(RouteA, path, 1, null);
        Assert.False(routes.Has(RouteA));
        routes.Remember(RouteA, new StackPath([], 0), aim);
        Assert.Equal(0, routes.Count);

        routes.Remember(RouteA, path, aim);
        routes.Remember("S143KC0fff", path, aim);   // отряда нет (погиб) — стирается
        routes.Prune(session.Map.Stacks.ContainsKey);
        Assert.True(routes.Has(RouteA));
        Assert.Equal(1, routes.Count);
    }
}
