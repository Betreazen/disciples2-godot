using D2.Data.Map;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>Исключения кэша целей при неудаче пути (<c>0x46903e</c> → <c>0x45f902</c>): объекты плана в клетке точки, а не
/// цель действия.</summary>
[Trait("Category", "Corpus")]
public class AiPathExclusionsTests
{
    [SkippableFact]
    public void StackInsideFort_ExcludesFortNotStack()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t35-*.sg").Order(StringComparer.Ordinal).Last();
        var map = MapLooksLoader.Load(sg).Map;
        var inside = map.Stacks["S073KC000D"];
        Assert.Equal("S073FT000A", inside.Inside, ignoreCase: true);

        // Точка атаки на отряд в крепости — вход крепости (4,35); в клетке входа лежит крепость, отряда там нет.
        var atEntrance = AiTactics.PathExclusions(map, 4, 35).ToList();
        Assert.Contains(atEntrance, id => id.Equals("S073FT000A", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(atEntrance, id => id.Equals("S073KC000D", StringComparison.OrdinalIgnoreCase));

        // Отряд в поле лежит в плане своей клетки — неудача пути до него исключает его самого.
        var field = map.Stacks.Values.First(s => s.Inside.Length == 0);
        Assert.Contains(AiTactics.PathExclusions(map, field.X, field.Y), id => id.Equals(field.Id, StringComparison.OrdinalIgnoreCase));
    }
}
