using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>«Раненые в крепости» (<c>0x4676ed</c>): павший в группе или здоровье ниже <c>GAI.HP_STAYMIN</c> процентов.</summary>
[Trait("Category", "Corpus")]
public class AiInjuredInsideTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableFact]
    public void InjuredInside_ByStayMinPercent_OrFallenUnit()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t45-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var art = SharedContent.Load(Data).MapArt;
        GameSession With(int hp)
        {
            var map = looks.Map;
            var stack = map.Stacks["S073KC0003"];
            var probe = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), art, book);
            var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            foreach (string id in stack.UnitIds) units[id] = units[id] with { Hp = probe.HpMax(units[id]) };   // остальные — здоровы
            units[stack.UnitIds[^1]] = units[stack.UnitIds[^1]] with { Hp = hp };
            var changed = GameSession.Retold(looks, GameSession.With(map, units: units), looks.Stacks);
            return new GameSession(new GameState(GameInfo.FromMap("PtC", changed), changed), art, book);
        }

        var full = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), art, book);
        var stack = full.Map.Stacks["S073KC0003"];
        int max = full.HpMax(full.Map.Units[stack.UnitIds[^1]]);
        Assert.False(AiTactics.InjuredInside(With(max), stack));
        Assert.False(AiTactics.InjuredInside(With((max * 66 + 99) / 100), stack));   // ровно 66 % и выше — не ранен
        Assert.True(AiTactics.InjuredInside(With(max * 66 / 100 - 1), stack));        // ниже 66 %
        Assert.True(AiTactics.InjuredInside(With(0), stack));                          // павший
    }
}
