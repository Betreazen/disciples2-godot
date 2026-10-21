using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Курсоры карты (<see cref="MapCursors"/>, разбор <c>CTaskStrat</c>/<c>CTaskSelectUnit</c>) на сохранении дня 21 живой партии.</summary>
[Trait("Category", "Corpus")]
public class MapCursorsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableFact]
    public void Cursor_ByTaskAndObject()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t21-037.sg");
        Skip.IfNot(File.Exists(sg), "нет корпуса");
        var looks = MapLooksLoader.Load(sg);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt, book);
        const string viewer = "S073PL000a", hero = "S073KC0001", enemy = "S073KC0059";
        bool CanMove(MapStack s, int x, int y) => session.PathTo(s, x, y).Points.Count > 1;
        MapCursors.Context Ctx(string? stack, Func<int, int, bool>? hidden = null) =>
            new(session.Looks, book, viewer, stack, null, hidden ?? ((_, _) => false), CanMove);

        var me = session.Map.Stacks[hero];
        var foe = session.Map.Stacks[enemy];
        Assert.Equal(MapCursors.SelectUnit, MapCursors.At(Ctx(null), me.X, me.Y));
        Assert.Equal(MapCursors.ManageStack, MapCursors.At(Ctx(hero), me.X, me.Y));
        Assert.Equal(MapCursors.Battle, MapCursors.At(Ctx(hero), foe.X, foe.Y));
        Assert.Equal(MapCursors.Default, MapCursors.At(Ctx(null), foe.X, foe.Y));
        Assert.Equal(MapCursors.Default, MapCursors.At(Ctx(hero, (_, _) => true), foe.X, foe.Y));   // туман — как без выбора

        // Пустая проходимая клетка рядом с героем — ход; своя столица вне ворот — выбор города.
        var free = Enumerable.Range(1, 3).SelectMany(d => new[] { (me.X + d, me.Y), (me.X, me.Y + d), (me.X - d, me.Y) })
                             .First(c => session.Map.Contains(c.Item1, c.Item2) && !session.Map.Plan.ContainsKey(c) && CanMove(me, c.Item1, c.Item2));
        Assert.Equal(MapCursors.Move, MapCursors.At(Ctx(hero), free.Item1, free.Item2));
        var capital = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == viewer);
        Assert.Equal(MapCursors.SelectCity, MapCursors.At(Ctx(null), capital.X, capital.Y));
        Assert.Equal(MapCursors.ViewCity,
                     MapCursors.At(new MapCursors.Context(session.Looks, book, viewer, null, capital.Id, (_, _) => false, CanMove), capital.X, capital.Y));
    }
}
