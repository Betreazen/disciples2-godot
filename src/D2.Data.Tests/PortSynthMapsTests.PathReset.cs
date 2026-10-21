using D2.Data.Play;
using D2.Data.Ui.Windows;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 28 <c>port-synth-28-path-reset</c> (<c>tools/synth-maps/maps_path_reset.py</c>, реестр Ин82): полигоны складываются в порте так,
/// как задумано памяткой, — дальний путь к союзнику упирается в окно «расторгните альянс», вору доступны «Разведать руины» у руины и
/// «Шпионаж» у деревни Легионов, жезл сносится с вопросом, деревня FT — дальше дневного запаса. Что движок делает с путём — партия
/// 21.10 (<see cref="PathResetHarnessTests"/>: AL, TH1, RD стирают, шпионаж — нет).</summary>
public partial class PortSynthMapsTests
{
    private const string PathResetMap = "port-synth-28-path-reset";

    [SkippableFact]
    public void PathReset_PolygonsAsInMemo()
    {
        Skip.IfNot(MapIds().Contains(PathResetMap));
        var facts = Facts(PathResetMap);
        var session = Begin(PathResetMap, 1);
        var map = session.Map;

        var al = Zone(facts, "AL");
        var walker = map.Stacks[Str(al, "stack")];
        var (fx, fy) = XY(al, "far_at");
        var far = session.PathTo(walker, fx, fy);
        Assert.True(far.Points.Count > 0 && far.Reachable < far.Points.Count - 1, "AL: дальний путь дойдёт за день или пути нет");
        var (ax, ay) = XY(al, "ally_at");
        Assert.True(session.AllyRefuses(walker, session.ClickAim(walker, ax, ay)), "AL: щелчок по союзнику не упирается в союз");

        var th1 = Zone(facts, "TH1");
        string ruin = AiTactics.ThiefTargetOf(session, Str(th1, "ruin"));
        Assert.Contains(ThiefAction.SpyRuin, session.ThiefActions(Str(th1, "stack"), ruin));
        var th2 = Zone(facts, "TH2");
        string village = AiTactics.ThiefTargetOf(session, Str(th2, "village"));
        Assert.Contains(ThiefAction.Spy, session.ThiefActions(Str(th2, "stack"), village));

        var rd = Zone(facts, "RD");
        var (rx, ry) = XY(rd, "rod_at");
        var bearer = map.Stacks[Str(rd, "stack")];
        Assert.NotNull(session.BreakableRodAt(bearer.Id, rx, ry));
        Assert.True(GameSession.AsksRodBreak(session.PathTo(bearer, rx, ry)), "RD: жезл сегодня не достижим — вопроса не будет");

        var ft = Zone(facts, "FT");
        var (vx, vy) = XY(ft, "village_at");
        var toFort = session.PathTo(map.Stacks[Str(ft, "stack")], vx, vy);
        Out.WriteLine($"AL путь {far.Points.Count} дойдёт {far.Reachable}; FT путь {toFort.Points.Count} дойдёт {toFort.Reachable}");
        Assert.True(toFort.Points.Count > 0 && toFort.Reachable < toFort.Points.Count - 1, "FT: деревня дойдётся за день или пути нет");
    }

    /// <summary>Путь вора стирает только итог «Разведать руины» (<c>0x4d23a2</c>: категория <c>0x837E30</c>), успех или провал; шпионаж и
    /// отказ — нет.</summary>
    [Fact]
    public void PathReset_OnlyRuinVisitDropsThiefRoute()
    {
        Assert.True(ThiefActionsLogic.Drops(new ThiefReport(ThiefAction.SpyRuin, "S143RU0000", null, Success: true)));
        Assert.True(ThiefActionsLogic.Drops(new ThiefReport(ThiefAction.SpyRuin, "S143RU0000", null, ThiefDied: true)));
        Assert.False(ThiefActionsLogic.Drops(new ThiefReport(ThiefAction.Spy, "S143FT0001", null, Success: true)));
        Assert.False(ThiefActionsLogic.Drops(new ThiefReport(ThiefAction.SpyRuin, "S143RU0000", "отказ")));
        Assert.False(ThiefActionsLogic.Drops(null));
    }
}
