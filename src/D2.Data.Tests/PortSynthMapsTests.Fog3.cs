using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>Карта 14 <c>port-synth-14-fog3</c> (<c>tools/synth-maps/maps_fog3.py</c>): отряды Империи в день 1 уходят из разведанных мест,
/// и в день 2 туману заклинаниями есть что закрыть у Империи; Сокрытие держится после шага. Сколько клеток закрывает движок (квадрат без
/// углов?) — печать: ответ — сохранения оригинала до и после литья.</summary>
public partial class PortSynthMapsTests
{
    private const string Fog3Map = "port-synth-14-fog3";

    [SkippableFact]
    public void Fog3_LeaversGoAway_AndFogSpellsCloseTheirStart()
    {
        Skip.IfNot(MapIds().Contains(Fog3Map));
        var facts = Facts(Fog3Map);
        var session = Begin(Fog3Map, 1);
        string human = session.ActiveHuman, empire = facts.GetProperty("watched").GetString()!;
        var iv = Zone(facts, "IV");
        string hidden = Str(iv, "stack");
        Assert.Null(session.CastSpell(human, Str(iv, "spell"), XY(iv, "at").X, XY(iv, "at").Y, useSpellBank: false));
        Assert.True(session.IsInvisible(hidden));
        var (sx, sy) = XY(iv, "step_to");
        session.MoveStack(hidden, sx, sy, 0, 4);
        Assert.True(session.IsInvisible(hidden), "Сокрытие снято простым шагом");

        session.EndTurn();
        Assert.Equal(2, session.Map.Turn);
        bool[,] Fog() => session.Map.Fog[session.Map.Players[empire].FogId];
        foreach (var l in Zone(facts, "NF").GetProperty("leavers").EnumerateArray())
        {
            var (x0, y0) = XY(l, "start");
            var st = session.Map.Stacks[Str(l, "stack")];
            Out.WriteLine($"{Str(l, "key")}: ушёл из ({x0},{y0}) на ({st.X},{st.Y}); старт у Империи {(Fog()[x0, y0] ? "в тумане" : "разведан")}");
            Assert.True(st.X - x0 >= 6, $"{Str(l, "key")}: отряд Империи ушёл только на {st.X - x0} клеток");
            Assert.False(Fog()[x0, y0], $"{Str(l, "key")}: старт у Империи не разведан");
        }
        foreach (var cast in Zone(facts, "casts").GetProperty("casts").EnumerateArray())
        {
            int before = HiddenCount(Fog());
            var at = cast.GetProperty("at");
            // у заклинания на всю карту клетки нет (at — null): литьё её не смотрит, подаётся угол карты
            var (x, y) = at.ValueKind == System.Text.Json.JsonValueKind.Null ? (0, 0) : (at[0].GetInt32(), at[1].GetInt32());
            Assert.Equal(at.ValueKind == System.Text.Json.JsonValueKind.Null, cast.GetProperty("area").GetInt32() == 999);
            string? refused = session.CastSpell(human, Str(cast, "spell"), x, y, useSpellBank: false);
            int closed = HiddenCount(Fog()) - before;
            Out.WriteLine($"{Str(cast, "key")} {Str(cast, "spell")}: {refused ?? "да"}, закрыто у Империи {closed}");
            Assert.True(refused is null, $"{Str(cast, "key")}: {refused}");
            Assert.True(closed > 0, $"{Str(cast, "key")}: у Империи ничего не закрыто");
        }
        session.EndTurn();
        Assert.Equal(3, session.Map.Turn);
    }
}
