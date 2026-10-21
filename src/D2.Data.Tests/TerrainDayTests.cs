using D2.Data.Corpus;

/// <summary>Разбор RLE события <c>terrain_day</c> — на синтетике, без прогона: если испорченный
/// отрезок будет проглочен молча, пропавшая клетка станет выглядеть как правило роста.</summary>
public class TerrainDayTests
{
    [Fact] public void ParseCells_ReadsRowsSpansAndSingleColumns()
    {
        var cells = TerrainDayEvent.ParseCells("5:5-7;6:2,9-10");

        // строка до двоеточия — это y, колонки — x
        Assert.Equal([(2, 6), (5, 5), (6, 5), (7, 5), (9, 6), (10, 6)], cells.Order());
    }

    [Fact] public void ParseCells_ReadsCapitalFootprintOfTheRun()
    {
        var cells = TerrainDayEvent.ParseCells("5:5-9;6:5-9;7:5-9;8:5-9;9:5-9");

        Assert.Equal(25, cells.Count);
        Assert.Contains((7, 7), cells);
        Assert.DoesNotContain((4, 7), cells);
    }

    [Fact] public void ParseCells_EmptyStringIsEmptySet()
    {
        Assert.Empty(TerrainDayEvent.ParseCells(""));
    }

    [Theory]
    [InlineData("5")]              // нет двоеточия
    [InlineData(":5-9")]           // нет строки
    [InlineData("x:5-9")]          // нечисловая строка
    [InlineData("5:a")]            // нечисловая колонка
    [InlineData("5:1-b")]          // нечисловой конец отрезка
    [InlineData("5:9-5")]          // отрезок назад
    public void ParseCells_ThrowsOnMalformedInput(string rle)
    {
        Assert.Throws<FormatException>(() => TerrainDayEvent.ParseCells(rle));
    }

    [Fact] public void Reader_ParsesTerrainDayLine()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path,
            "{\"seq\":4,\"tid\":13360,\"ev\":\"terrain_day\",\"player\":\"S143PL0001\",\"turn\":1," +
            "\"race\":0,\"terrain\":1,\"size\":48,\"owned\":4,\"cells\":\"5:5-6;6:5-6\"}\n");
        try
        {
            var e = Assert.IsType<TerrainDayEvent>(CorpusReader.Read(path).Single());

            Assert.Equal("S143PL0001", e.Player);
            Assert.Equal(1, e.Turn);
            Assert.Equal(1, e.Terrain);
            Assert.Equal(48, e.Size);
            Assert.Equal(e.Owned, e.Cells.Count);
        }
        finally { File.Delete(path); }
    }
}
