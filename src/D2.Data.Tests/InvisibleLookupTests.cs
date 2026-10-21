using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>
/// <see cref="GameSession.Invisible"/> по множеству id невидимых — то же, что прежний линейный проход по обликам
/// (<c>Any(Same(s.Id, id) &amp;&amp; s.Invisible)</c>) на сохранении живой партии (<c>20260919-human</c>, день 44): каждый отряд
/// в исходном регистре, в верхнем и нижнем, чужие id; и после <see cref="GameSession.Unhide"/>, который ставит новый список обликов.
/// </summary>
[Trait("Category", "Corpus")]
public class InvisibleLookupTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableFact]
    public void Invisible_SameAsLinearScan_AlsoAfterUnhide()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t44-077.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var looks = MapLooksLoader.Load(sg);
        // Каждый третий отряд прячем: в сохранении невидимых может не быть.
        looks = looks with { Stacks = [.. looks.Stacks.Select((s, i) => i % 3 == 0 ? s with { Invisible = true } : s)] };
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var game = new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks),
                                   SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        var hidden = game.Looks.Stacks.First(s => s.Invisible).Id;

        AssertSame(game);
        game.Unhide(hidden.ToLowerInvariant());
        Assert.False(game.Invisible(hidden));
        AssertSame(game);
    }

    private static void AssertSame(GameSession game)
    {
        var stacks = game.Looks.Stacks;
        Assert.Contains(stacks, s => s.Invisible);
        var ids = stacks.SelectMany(s => new[] { s.Id, s.Id.ToUpperInvariant(), s.Id.ToLowerInvariant() })
                        .Concat(["", "S073KC9999", "нет"]);
        foreach (string id in ids)
            Assert.Equal(stacks.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase) && s.Invisible), game.Invisible(id));
    }
}
