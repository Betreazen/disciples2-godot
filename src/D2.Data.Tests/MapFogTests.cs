using D2.Data.Content;
using D2.Data.Gfx;
using D2.Data.Map;
using Xunit;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Туман войны на карте (<see cref="MapFog"/>, <c>docs/map-render-stacks.md</c> §4).</summary>
[Trait("Category", "Corpus")]
public class MapFogTests(ITestOutputHelper output)
{
    [Fact]
    public void Name_FromNeighbourMask()
    {
        Assert.Equal("FOG0100", MapFog.Name(0, 3));      // одиночная скрытая клетка
        Assert.Equal("FOG0003", MapFog.Name(255, 3));    // всё вокруг скрыто — сплошной, с вариантом
        Assert.Equal("FOG5400", MapFog.Name(1, 0));      // скрыт только (−1,0)
    }

    [Fact]
    public void Mask_FirstNeighbourIsHighBit_AndOffMapCountsHidden()
    {
        Assert.Equal(0b1000_0000, MapFog.Mask((x, y) => (x, y) == (4, 4), 5, 5, 10));
        Assert.Equal(0b1110_0011, MapFog.Mask((_, _) => false, 0, 0, 10));  // (−1,−1) (0,−1) (+1,−1) (−1,+1) (−1,0) за картой
    }

    [Theory]
    [InlineData(0, 0, "FOG8200")]
    [InlineData(5, 0, "FOG8300")]
    [InlineData(9, 0, "FOG8400")]
    [InlineData(0, 5, "FOG8900")]
    [InlineData(9, 9, "FOG8600")]
    public void Border_ByEdge(int x, int y, string name)
    {
        Assert.Equal(name, MapFog.Border(x, y, 10));
        Assert.Null(MapFog.Border(5, 5, 10));
    }

    /// <summary>Отряд 1×1 с кадром 320: прямоугольник <c>x−6..x+1</c> × <c>y−6..y+1</c>; одна открытая клетка в нём —
    /// отряд виден.</summary>
    [Fact]
    public void Hides_OnlyWhenWholeRectangleHidden()
    {
        Assert.True(MapFog.Hides((_, _) => true, 20, 20, 1, 1, 320, 48));
        Assert.False(MapFog.Hides((x, y) => (x, y) != (14, 14), 20, 20, 1, 1, 320, 48));
        Assert.True(MapFog.Hides((x, y) => (x, y) != (13, 14), 20, 20, 1, 1, 320, 48));
        Assert.True(MapFog.Hides((x, y) => (x, y) != (21, 22), 20, 20, 1, 1, 320, 48));
    }

    /// <summary>Сохранение из живой партии: глазами игрока-человека туман есть и все имена картинок тумана найдены;
    /// без игрока тумана нет.</summary>
    [SkippableFact]
    public void Save_FromLiveGame_HasFogForHuman()
    {
        string save = Path.Combine(CorpusDir(), "20260922-ui", "ui-misc-human-t01-002.sg");
        Skip.IfNot(File.Exists(save), "нет сохранения в корпусе");
        var content = SharedContent.Load(Path.Combine(AppContext.BaseDirectory, "data"));
        var looks = MapLooksLoader.Load(save);
        var human = looks.Map.Players.Values.First(p => p.IsHuman).Id;

        var seen = MapScene.Build(looks, content.MapArt, content.Gfx, viewer: human);
        var editor = MapScene.Build(looks, content.MapArt, content.Gfx);

        int fog = seen.Sprites.Count(s => s.Name.StartsWith("FOG", StringComparison.Ordinal));
        output.WriteLine($"туман {fog}, картинок глазами игрока {seen.Sprites.Count}, в редакторе {editor.Sprites.Count}");
        Assert.True(fog > 0);
        Assert.True(seen.Missing.Count == 0, string.Join(", ", seen.Missing.Distinct().Take(20)));
        Assert.DoesNotContain(editor.Sprites, s => s.Name.StartsWith("FOG", StringComparison.Ordinal));
        Assert.True(seen.Sprites.Count - fog < editor.Sprites.Count, "под туманом что-то должно спрятаться");
    }

    private static string CorpusDir()
    {
        if (Environment.GetEnvironmentVariable("D2_CORPUS_DIR") is { Length: > 0 } dir) return dir;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
        return d is null ? "" : Path.Combine(d.FullName, "corpus");
    }
}
