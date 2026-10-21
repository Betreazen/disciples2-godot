using D2.Data.Gfx;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Варианты масок и берега бросками движка (<see cref="GroundVariantDraws"/>): перемотка R250 назад к исходной таблице,
/// состояние из заголовка сохранения, 22 броска на клетку.</summary>
[Trait("Category", "Corpus")]
public class GroundVariantDrawsTests
{
    [Fact]
    public void Rewind_FindsStartIndex_OrGivesUp()
    {
        var rng = new EngineRng(59);
        rng.Advance(50_690);
        Assert.Equal((50_690, 59), EngineRng.RewindToTable(rng.TableSnapshot(), rng.Index));
        Assert.Equal((0, 47), EngineRng.RewindToTable(new EngineRng().TableSnapshot(), 47));
        var noise = Enumerable.Range(0, EngineRng.Size).Select(i => (ushort)(i * 7919)).ToArray();
        Assert.Null(EngineRng.RewindToTable(noise, 3, limit: 10_000));
    }

    /// <summary>Новая игра: состояние после reseed (индекс 59) и один бросок, затем 22·N² бросков Next(3) — y снаружи, x внутри.</summary>
    [Fact]
    public void Draws_FollowEngineOrder()
    {
        var saved = new EngineRng(59);
        saved.Advance(1 + (22 * 4 * 4) + 1000);   // бросок новой игры, земля 4×4 и дальше партия
        var draws = GroundVariantDraws.ForSave(saved.TableSnapshot(), saved.Index, 4);
        Assert.Equal("новая игра, индекс 59", draws.Origin);
        var expected = new EngineRng(59);
        expected.Step();
        int[] first = [.. Enumerable.Range(0, 22 * 2).Select(_ => expected.Next(3))];
        Assert.Equal(first[..22], Enumerable.Range(0, 22).Select(s => draws.At(0, 0, s)!.Value));
        Assert.Equal(first[22..], Enumerable.Range(0, 22).Select(s => draws.At(1, 0, s)!.Value));   // x внутри
        Assert.Null(draws.At(4, 0, 0));
        Assert.Equal("загрузка после запуска", GroundVariantDraws.ForSave(null, 0, 4).Origin);
    }

    /// <summary>Настоящее сохранение (кампания «Дыхание смерти», снимки 27.09): состояние из заголовка перематывается к индексу 59.</summary>
    [SkippableFact]
    public void Save_GivesNewGameIndex()
    {
        // shot01.sg shot_pairs — копия этого сохранения сторожа (раскладка снимков живёт в .cache, не в корпусе).
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260928-shots", "dyhanie-smerti-human-t01-002.sg");
        Skip.IfNot(File.Exists(sg), "нет снимков 20260928-shots");
        var (table, index) = GroundVariantDraws.ReadSaveState(sg)!.Value;
        Assert.Equal(EngineRng.Size, table.Length);
        Assert.Equal(59, EngineRng.RewindToTable(table, index)!.Value.Index);
    }

    /// <summary>Вторая партия запуска: таблица продолжает первую (индекс 59, 50 690 шагов), reseed новой игры сменил индекс на 93 —
    /// встречный поиск находит точку reseed; прямая перемотка к исходной таблице — нет.</summary>
    [Fact]
    public void Reseed_FoundBetweenGames()
    {
        var rng = new EngineRng(59);
        rng.Advance(50_690);
        var atReseed = rng.TableSnapshot();
        var second = EngineRng.FromSeed(atReseed, 93);
        second.Advance(1 + (22 * 4 * 4) + 3000);
        Assert.Null(EngineRng.RewindToTable(second.TableSnapshot(), second.Index, limit: 200_000));
        var found = EngineRng.RewindToReseed(second.TableSnapshot(), second.Index, back: 20_000, ahead: 60_000)!.Value;
        Assert.Equal((1 + (22 * 4 * 4) + 3000, 93), (found.Steps, found.Index));
        Assert.Equal(atReseed, found.Table);
        Assert.StartsWith("новая игра не первой в запуске, индекс 93",
                          GroundVariantDraws.ForSave(second.TableSnapshot(), second.Index, 4).Origin);
    }

    /// <summary>Настоящее сохранение второй партии запуска (ng-diffgame, снимки 5–8 27.09): журнал пишет reseed <c>new_game_time</c> —
    /// индекс 93 (seq 1620133).</summary>
    [SkippableFact]
    public void Save_SecondGameOfLaunch_GivesReseedIndex()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260928-shots", "ng-diffgame-0-human-t01-003.sg");
        Skip.IfNot(File.Exists(sg), "нет снимков 20260928-shots");
        var (table, index) = GroundVariantDraws.ReadSaveState(sg)!.Value;
        Assert.Equal(93, EngineRng.RewindToReseed(table, index)!.Value.Index);
    }

    /// <summary>Земля в 565 (<see cref="GroundComposer.Rgb565"/>): у каждой точки каналы — расширенные 5/6/5 бит, как у снимка.</summary>
    [Fact]
    public void Composer565_GivesExpandedChannels()
    {
        var cells = new GroundType[6, 6];
        for (int x = 0; x < 6; x++)
            for (int y = 0; y < 6; y++)
                cells[x, y] = (x + y) % 3 == 0 ? GroundType.Water : x < 3 ? GroundType.Human : GroundType.Neutral;
        var image = new GroundComposer(cells, 7746743, new GroundComposerTests.FakeArt()) { Rgb565 = true }.Render(-192, 0, 384, 192);
        for (int o = 0; o < image.Pixels.Length; o += 4)
        {
            if (image.Pixels[o + 3] == 0) continue;
            Assert.Equal(image.Pixels[o], MapComposite.Expand565(image.Pixels[o] >> 3, 0));
            Assert.Equal(image.Pixels[o + 1], MapComposite.Expand565(image.Pixels[o + 1] >> 2, 1));
        }
    }
}
