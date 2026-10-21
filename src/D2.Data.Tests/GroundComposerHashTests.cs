using System.Security.Cryptography;
using D2.Data.Gfx;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Сборка земли бит в бит (<see cref="GroundComposer"/>): SHA-256 картинок, снятые 14.10.2026 до переписывания горячих
/// путей на прямые циклы (гипотеза H8 — без делегата на точку). Графика — шумовая: узоры, маски с нулевыми, частичными и полными
/// весами, берег с прозрачными местами, фон воды — так задеты все ветки (узор, переход блока по X, Y и обеим осям, смешение,
/// 565, анимированная вода, туман, броски движка). Раскладки — сценарий S142SC0000 (120×120), сохранение дня 48 из корпуса и
/// своя карта с озёрами. Хеш поменялся — поменялась картинка: правка поведения, а не скорости, и её доказывают отдельно.</summary>
[Trait("Category", "Corpus")]
public class GroundComposerHashTests
{
    private sealed class NoisyArt : IGroundArt
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(char Kind, int A, int B), RgbaImage> _cache = new();

        public int PatternVariants(GroundType type) => type == GroundType.Black ? 1 : 4;

        public RgbaImage Pattern(GroundType type, int variant) =>
            Image('p', (int)type, variant, 192, 192, (x, y, h) => (h, h >> 8, h >> 16, 255));

        // Маски 7 и 23 без картинок (пропуск), у чётных — два варианта.
        public int MaskVariants(int mask) => mask is 7 or 23 ? 0 : mask % 2 == 0 ? 2 : 1;

        // Вес: полоса нулей, полоса 255, между ними шум.
        public RgbaImage? Mask(int mask, int variant) => Image('m', mask, variant, 64, 32, (x, y, h) =>
        {
            int w = x < 12 ? 0 : x > 52 ? 255 : h & 0xFF;
            return (w, w, w, 255);
        });

        public int ShoreVariants(int mask) => mask == 5 ? 0 : 1 + (mask % 3);

        public RgbaImage? Shore(int mask, int variant) =>
            Image('s', mask, variant, 64, 32, (x, y, h) => (h, h >> 8, h >> 16, (h >> 24) % 3 == 0 ? 0 : 255));

        public RgbaImage? WaterBackground => Image('w', 0, 0, 128, 128, (x, y, h) => (h, h >> 8, h >> 16, 255));

        private RgbaImage Image(char kind, int a, int b, int width, int height, Func<int, int, int, (int R, int G, int B, int A)> pixel) =>
            _cache.GetOrAdd((kind, a, b), _ =>
            {
                var img = new RgbaImage(width, height);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        var (r, g, bl, al) = pixel(x, y, Noise(kind, a, b, x, y));
                        int o = img.Offset(x, y);
                        (img.Pixels[o], img.Pixels[o + 1], img.Pixels[o + 2], img.Pixels[o + 3]) = ((byte)r, (byte)g, (byte)bl, (byte)al);
                    }
                return img;
            });

        private static int Noise(char kind, int a, int b, int x, int y)
        {
            uint h = unchecked((uint)((kind * 374761393) + (a * 668265263) + (b * 2246822519) + (x * 3266489917) + (y * 2654435761)));
            h = unchecked((h ^ (h >> 15)) * 2246822519);
            h = unchecked((h ^ (h >> 13)) * 3266489917);
            return (int)(h ^ (h >> 16));
        }
    }

    private static readonly NoisyArt Art = new();

    private static string Hash(GroundComposer composer, params (int Left, int Top, int Width, int Height)[] rects)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (left, top, width, height) in rects) sha.AppendData(composer.Render(left, top, width, height).Pixels);
        return Convert.ToHexString(sha.GetHashAndReset())[..16];
    }

    /// <summary>Вся карта с полем за краем (чёрный узор) кусками 1024 и невыровненный кусок.</summary>
    private static (int, int, int, int)[] Whole(int n)
    {
        const int chunk = 1024;
        var rects = new List<(int, int, int, int)>();
        for (int top = -40; top < (n * 32) + 40; top += chunk)
            for (int left = -(n * 32) - 40; left < (n * 32) + 40; left += chunk) rects.Add((left, top, chunk, chunk));
        rects.Add((-37, 13, 333, 211));
        return [.. rects];
    }

    private static GroundType[,] Cells(D2.Rules.Map.MapState map)
    {
        var cells = new GroundType[map.Size, map.Size];
        for (int x = 0; x < map.Size; x++)
            for (int y = 0; y < map.Size; y++) cells[x, y] = GroundGeometry.TypeOf(map.Tiles[x, y]);
        return cells;
    }

    private static bool Fog(int x, int y) => ((x / 7) + (y / 5)) % 3 == 0;

    /// <summary>Своя карта 24×24: полосы всех типов, озёра и остров — берег со всеми масками, границы до трёх типов на клетку.</summary>
    private static GroundType[,] Lakes()
    {
        const int n = 24;
        var cells = new GroundType[n, n];
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
            {
                int dx = x - 8, dy = y - 9, ex = x - 18, ey = y - 16;
                bool lake = (dx * dx) + (2 * dy * dy) < 40 || (ex * ex) + (ey * ey) < 14 || (x + (2 * y)) % 17 == 0;
                cells[x, y] = lake && !(x == 8 && y == 9) ? GroundType.Water : (GroundType)(2 + (((x / 3) + (y / 4) + (x * y % 5)) % 6));
            }
        return cells;
    }

    [Theory]
    [InlineData(false, false, false, false, "2E4380DCA646EA10")]
    [InlineData(true, false, false, false, "0D25D09AB6717541")]
    [InlineData(false, true, false, false, "45B58D98580B6FFB")]
    [InlineData(true, true, false, false, "ADE2A2395105C326")]
    [InlineData(false, false, true, false, "1B93E15994F48FCE")]
    [InlineData(true, true, true, true, "BE16F8E8AA34B3E0")]
    public void Lakes_SameAsBefore(bool rgb565, bool animated, bool fog, bool draws, string expected)
    {
        var cells = Lakes();
        int n = cells.GetLength(0);
        GroundVariantDraws? d = null;
        if (draws)
        {
            var rng = new EngineRng(59);
            rng.Advance(1 + (22 * n * n) + 5);
            d = GroundVariantDraws.ForSave(rng.TableSnapshot(), rng.Index, n);
        }
        var composer = new GroundComposer(cells, 7746743, Art) { Rgb565 = rgb565, AnimatedWater = animated, Hidden = fog ? Fog : null, Draws = d };
        Assert.Equal(expected, Hash(composer, Whole(n)));
    }

    [Theory]
    [InlineData(false, false, "DEF594358C07DBF1")]
    [InlineData(true, true, "CF32BD472CEC3EA7")]
    public void Scenario142_SameAsBefore(bool rgb565, bool animated, string expected)
    {
        var map = ScenarioFile.Read(ScenarioFile.PathOf(Path.Combine(AppContext.BaseDirectory, "data", "scenarios"), "S142SC0000")).Looks.Map;
        var composer = new GroundComposer(Cells(map), map.MapSeed, Art) { Rgb565 = rgb565, AnimatedWater = animated };
        Assert.Equal(expected, Hash(composer, Whole(map.Size)));
    }

    [SkippableTheory]
    [InlineData(false, false, "9FAFB976DEFFF04D")]
    [InlineData(true, true, "A672DCA38FC012C2")]
    public void SaveDay48_SameAsBefore(bool rgb565, bool fog, string expected)
    {
        Skip.If(Day48.Value is null, "нет корпуса 20260919-human");
        var (map, draws) = Day48.Value!.Value;
        var composer = new GroundComposer(Cells(map), map.MapSeed, Art) { Rgb565 = rgb565, Hidden = fog ? Fog : null, Draws = draws };
        Assert.Equal(expected, Hash(composer, Whole(map.Size)));
    }

    /// <summary>Карта и броски земли сохранения дня 48 — один раз на оба случая: <see cref="GroundVariantDraws.ForSave"/> у этого
    /// сохранения идёт встречным поиском по 2 млн шагов на всех ядрах.</summary>
    private static readonly Lazy<(D2.Rules.Map.MapState Map, GroundVariantDraws Draws)?> Day48 = new(() =>
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        string? sg = Directory.Exists(dir) ? Directory.GetFiles(dir, "*-t48-*.sg").Order(StringComparer.Ordinal).LastOrDefault() : null;
        if (sg is null) return null;
        var map = MapLooksLoader.Load(sg).Map;
        var (table, index) = GroundVariantDraws.ReadSaveState(sg)!.Value;
        return (map, GroundVariantDraws.ForSave(table, index, map.Size));
    });
}
