using D2.Data.Content;
using D2.Data.Gfx;
using D2.Data.Map;
using Xunit;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Картинки карты (<see cref="MapScene"/>) по разбору движка (<c>docs/map-render.md</c>,
/// <c>docs/map-render-stacks.md</c>): формулы якоря и точки, порядок и — на всех 33 картах игры — что каждое имя,
/// которое строят правила, нашлось в графике порта.</summary>
[Trait("Category", "Corpus")]
public class MapSceneTests(ITestOutputHelper output)
{
    private static GameContent Content => _content ??= SharedContent.Load(Path.Combine(AppContext.BaseDirectory, "data"));
    private static GameContent? _content;

    [Theory]
    [InlineData(1, 10, 10, 0)]   // клетка: якорь — она сама
    [InlineData(3, 11, 11, 0)]   // руины: середина следа
    [InlineData(4, 11, 12, 32)]  // город: чётная ширина — якорь на клетку левее, сдвиг 32
    [InlineData(5, 12, 12, 0)]   // столица
    public void Anchor_FollowsFootprint(int size, int ax, int ay, int dx)
    {
        Assert.Equal((ax, ay, dx), MapScene.Anchor(10, 10, size, size));
    }

    /// <summary>Центр холста — в центре следа <c>((x−y)·32 + 32, (x+y)·16 + 16·s)</c> для любого квадратного следа.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Place_CentersCanvasOnFootprint(int s)
    {
        var (ax, ay, dx) = MapScene.Anchor(7, 3, s, s);
        var (x, y) = MapScene.Place(ax, ay, 320, 320, dx);

        Assert.Equal(((7 - 3) * 32) + 32, x + 160);
        Assert.Equal(((7 + 3) * 16) + (16 * s), y + 160);
    }

    [Fact]
    public void Place_StackOn320Canvas_MatchesDocFormula()
    {
        Assert.Equal((((5 - 2) * 32) - 128, ((5 + 2) * 16) - 144), MapScene.Place(5, 2, 320, 320));
    }

    /// <summary>Лодка в пути (<c>0x5c43ae</c>, <c>0x5c45ec</c>): у идущего отряда на воде — <c>{RACE_ID}BTMV{f}</c> и тень <c>SBTM{f}</c>, у стоящего —
    /// <c>BOAT</c>/<c>SBOA</c>; у эльфов теней лодки в архиве нет.</summary>
    [SkippableFact]
    public void Boat_MovingUsesBtmv()
    {
        string save = Path.Combine(GameSaveTests.CorpusDir(), "20260922-ui", "ui-misc-human-t01-002.sg");
        Skip.IfNot(File.Exists(save), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(save);
        var scene = MapScene.Build(looks, Content.MapArt, Content.Gfx);
        var water = Enumerable.Range(0, looks.Map.Size).SelectMany(x => Enumerable.Range(0, looks.Map.Size).Select(y => (x, y)))
            .First(c => looks.Map.Tiles[c.x, c.y].Ground == D2.Rules.Map.Ground.Water);
        var owner = looks.Map.Players.Values.First(p => p.RaceId.Equals("G000RR0000", StringComparison.OrdinalIgnoreCase));
        var stack = new StackLook("S999KC0001", water.x, water.y, owner.Id, "", "G000UU0001", true, true, 2, "", false);

        string[] moving = [.. scene.StackSprites(stack, 2, moving: true).Select(m => m.Name)];
        string[] standing = [.. scene.StackSprites(stack, 2, moving: false).Select(m => m.Name)];
        Assert.Contains("G000RR0000BTMV2", moving);
        Assert.Contains("G000RR0000SBTM2", moving);
        Assert.Contains("G000RR0000BOAT2", standing);
        Assert.Contains("G000RR0000SBOA2", standing);
    }

    /// <summary>Знамя отряда — по <c>BANNER</c> его подрасы, владелец не проверяется (<c>0x5bbf61</c>…<c>0x5bbf90</c>: подраса стека
    /// <c>+0x90</c> → <c>CMidSubRace +0x24</c> → <c>0x5b7b67</c>, отказ только при <c>BANNER &lt; 0</c>). В «A Return To Simpler Times»
    /// нейтральная подраса сценария <c>S001SR000c</c> «Imperial Army» (тип 0, своя подраса редактора) несёт <c>BANNER 0</c> — знамя
    /// Империи, как у подрасы игрока <c>S001SR0001</c>: сине-жёлтые знамёна над нейтральными латниками и разбойниками (пометка 2
    /// партии 14.10) — так задумал автор карты, и движок рисует их так же.</summary>
    [SkippableFact]
    public void NeutralStack_CarriesItsSubraceBanner_EvenEmpires()
    {
        string path = Path.Combine(GamePaths.Exports, "'A Return To Simpler Times'.sg");
        Skip.IfNot(File.Exists(path), "нет копии игры");
        var looks = MapLooksLoader.Load(path);
        var scene = MapScene.Build(looks, Content.MapArt, Content.Gfx, viewer: "S001PL0001");
        string Banner(string stack) => Assert.Single(scene.Sprites, s => s.Source == stack && s.Name.StartsWith("STACK_BANNER_", StringComparison.Ordinal)).Name;

        var neutral = looks.Stacks.First(s => s.Subrace == "S001SR000c" && s.Inside.Length == 0);
        Assert.Equal("S001PL0000", neutral.Owner);
        Assert.Equal(0, looks.Banners["S001SR000c"]);
        Assert.Equal(0, looks.Banners["S001SR0001"]);   // подраса Империи игрока S001PL0001 — то же знамя
        Assert.Equal("STACK_BANNER_0000", Banner(neutral.Id));
    }

    [Fact]
    public void MapArt_IsImported()
    {
        var art = Content.MapArt;
        Assert.Equal(52, art.Layers.Count);
        Assert.Equal(391, art.Layer("STACKS"));
        Assert.Equal(870, art.Layer("BANNERS"));
        Assert.True(art.IsFlyer("G000UU0019"));   // Рыцарь на пегасе
        Assert.False(art.IsFlyer("G000UU0001"));  // Сквайр
    }

    /// <summary>На каждой карте все имена найдены, порядок — по ключу, затем по Y и X, и на карте есть отряды,
    /// крепости и лес.</summary>
    [SkippableFact]
    public void AllScenarios_EveryNameResolves_AndOrderIsEngineOrder()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        Skip.If(Content.Gfx.Archive("IsoUnit") is null, "нет описи графики data/gfx");
        var files = Directory.GetFiles(GamePaths.Exports, "*.sg").Order(StringComparer.Ordinal).ToList();
        var problems = new List<string>();
        int total = 0;
        foreach (var file in files)
        {
            var looks = MapLooksLoader.Load(file);
            var scene = MapScene.Build(looks, Content.MapArt, Content.Gfx);
            total += scene.Sprites.Count;
            if (scene.Missing.Count > 0)
                problems.Add($"{Path.GetFileName(file)}: нет {string.Join(", ", scene.Missing.Distinct().Take(8))}");
            for (int i = 1; i < scene.Sprites.Count; i++)
            {
                var (a, b) = (scene.Sprites[i - 1], scene.Sprites[i]);
                if ((a.Key, a.Y, a.X).CompareTo((b.Key, b.Y, b.X)) > 0) problems.Add($"{Path.GetFileName(file)}: порядок на {i}");
            }
        }
        output.WriteLine($"карт {files.Count}, картинок {total}");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(33, files.Count);
    }

    /// <summary>Номер дерева (биты 26–31 клетки) на всех картах в пределах числа картинок леса своей расы: 0..23 у HU и
    /// EL, 0..22 у DW и HE, 0..24 у UN, 0..18 у NE (<c>map-render.md</c> §2).</summary>
    [SkippableFact]
    public void TreeImage_WithinRaceRange()
    {
        Skip.IfNot(GamePaths.IsAvailable);
        int[] max = [0, 23, 22, 22, 24, 18, 23];  // по Lterrain 1..6: HU, DW, HE, UN, NE, EL
        foreach (var file in Directory.GetFiles(GamePaths.Exports, "*.sg"))
        {
            var looks = MapLooksLoader.Load(file);
            for (int x = 0; x < looks.Map.Size; x++)
                for (int y = 0; y < looks.Map.Size; y++)
                {
                    var tile = looks.Map.Tiles[x, y];
                    if (tile.Ground != D2.Rules.Map.Ground.Forest) continue;
                    Assert.True(looks.TreeGroup[x, y] == 0, $"{file} ({x},{y}): группа {looks.TreeGroup[x, y]}");
                    Assert.InRange(looks.TreeImage[x, y], 0, max[tile.Terrain]);
                }
        }
    }
}
