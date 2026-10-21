using D2.Data.Map;
using D2.Rules.Map;

namespace D2.Rules.Tests;

/// <summary>
/// <see cref="LandModel.PlanCells"/> и <see cref="PathFinder.EntranceOf"/> по указателю плана — то же, что прежний проход по
/// всем клеткам плана (<c>Plan.Where(p =&gt; p.Value.Contains(id, OrdinalIgnoreCase))</c>): на плане сохранения живой партии
/// (каждый id в трёх регистрах, чужой id) и на плане с повтором id в клетке в разном регистре.
/// </summary>
[Trait("Category", "Corpus")]
public class PlanIndexTests
{
    [SkippableFact]
    public void PlanCellsAndEntrance_SameAsLinearScan_OnCorpusSave()
    {
        string sg = Path.Combine(CorpusFiles.Dir, "20260919-human", "rastlennaya-vlast-dwarf-t44-077.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var map = MapLooksLoader.Load(sg).Map;
        var ids = map.Plan.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(ids);
        foreach (string id in ids.SelectMany(i => new[] { i, i.ToUpperInvariant(), i.ToLowerInvariant() }).Append("S073FT9999"))
            AssertSame(map, id);
    }

    [Fact]
    public void PlanCellsAndEntrance_DuplicateIdInCell_CountedOnce()
    {
        var plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>
        {
            [(3, 4)] = ["S001FT0001", "s001ft0001"], [(2, 5)] = ["S001FT0001"], [(1, 1)] = ["S001KC0002"], [(2, 2)] = [],
        };
        var map = new MapState
        {
            Size = 8, Tiles = new Tile[8, 8], Turn = 1,
            Stacks = new Dictionary<string, MapStack>(), Units = new Dictionary<string, MapUnit>(),
            Players = new Dictionary<string, MapPlayer>(), Objects = new Dictionary<string, MapObject>(),
            Plan = plan, Fog = new Dictionary<string, bool[,]>(),
        };
        Assert.Equal([(2, 5), (3, 4)], LandModel.PlanCells(map, "s001FT0001"));
        Assert.Equal((3, 5), PathFinder.EntranceOf(map, "S001ft0001"));
        foreach (string id in new[] { "S001FT0001", "S001KC0002", "S001KC0003" }) AssertSame(map, id);
    }

    private static void AssertSame(MapState map, string id)
    {
        var cells = map.Plan.Where(p => p.Value.Contains(id, StringComparer.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
        Assert.Equal(cells.Order(), LandModel.PlanCells(map, id));
        Assert.Equal(cells.Count == 0 ? null : (cells.Max(c => c.X), cells.Max(c => c.Y)), PathFinder.EntranceOf(map, id));
    }
}
