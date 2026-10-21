using D2.Data.Diagnostics;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Инварианты карты (<see cref="MapInvariants"/>): на сохранениях оригинала из корпуса нарушений нет — так проверяется сама
/// проверка; на испорченном состоянии — ловит, с кодом и объектом.</summary>
[Trait("Category", "Corpus")]
public class MapInvariantsTests
{
    private static readonly Lazy<GameBook> Book = new(() => GameBook.Load(Path.Combine(AppContext.BaseDirectory, "data", "rules")));

    /// <summary>Сохранений с партии в проверке: первое, последнее и равномерно между ними (весь корпус — 600+ файлов, разбор каждого
    /// ~0,1 с; выборка покрывает начало, середину и конец каждой партии).</summary>
    private const int PerSession = 8;

    public static TheoryData<string> Sessions => new()
    {
        "20261014-replay", "20260919-human", "20260930-probe72", "20260920-legions", "20260929-hotseat", "20260918-probe65",
    };

    /// <summary>Сохранения партии оригинала: каждое проверяется вместе с предыдущим той же игры (вместимость — по росту группы,
    /// <see cref="MapInvariants"/>). В обычном прогоне — выборка <see cref="PerSession"/> (первое, последнее и равномерно между);
    /// <c>D2_INVARIANTS_ALL=1</c> — все (весь корпус шести партий ~5 с).</summary>
    [SkippableTheory]
    [MemberData(nameof(Sessions))]
    public void OriginalSaves_HaveNoViolations(string session)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), session);
        Skip.IfNot(Directory.Exists(dir), $"нет партии {session} в корпусе");
        // Игра — имя до «-tДД-», порядок внутри — номер сохранения в конце имени.
        var games = Directory.GetFiles(dir, "*.sg")
            .Select(f => (File: f, Match: System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^(.*)-t\d+-(\d+)\.sg$")))
            .Where(f => f.Match.Success)
            .GroupBy(f => f.Match.Groups[1].Value, StringComparer.Ordinal)
            .Select(g => g.OrderBy(f => int.Parse(f.Match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
                          .Select(f => f.File).ToList())
            .ToList();
        var pairs = games.SelectMany(g => g.Select((f, i) => (Before: i > 0 ? g[i - 1] : null, File: f))).ToList();
        Skip.If(pairs.Count == 0, $"в {session} нет сохранений");
        bool all = Environment.GetEnvironmentVariable("D2_INVARIANTS_ALL") == "1";
        var sample = all || pairs.Count <= PerSession ? pairs
            : [.. Enumerable.Range(0, PerSession).Select(i => pairs[i * (pairs.Count - 1) / (PerSession - 1)]).Distinct()];
        var bad = new List<string>();
        foreach (var (before, file) in sample)
        {
            var previous = before is null ? null : MapLoader.Load(before);
            foreach (var v in MapInvariants.Check(MapLoader.Load(file), Book.Value, previous))
                bad.Add($"{Path.GetFileName(file)}: {v.Code} {v.Object} — {v.Detail}");
        }
        // По кодам: сколько и первые — длинный список одного кода прячет остальные.
        var byCode = bad.GroupBy(b => b[(b.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(' ')[0], StringComparer.Ordinal)
                        .Select(g => $"{g.Key} × {g.Count()}: {string.Join(" | ", g.Take(3))}");
        Assert.True(bad.Count == 0, $"{bad.Count} нарушений:\n{string.Join("\n", byCode)}");
    }

    // ------------------------------------------------------------------ испорченное состояние

    private const string Squire = "G000UU0001";   // L1, hp 100

    private static MapUnit Unit(string id, int hp = 100) => new() { Id = id, TypeId = Squire, Level = 1, Hp = hp };

    private static MapStack Stack(string id, int x, int y, params string[] units) => new()
    {
        Id = id, Owner = "S001PL0001", Subrace = "", LeaderId = units.Length > 0 ? units[0] : "", LeaderAlive = true, X = x, Y = y,
        Inside = "", UnitIds = units, Slots = [.. Enumerable.Range(0, 6).Select(c => c < units.Length ? c : -1)],
    };

    /// <summary>Чистая карта 8×8: игрок, отряд S001KC0001 (два оруженосца) в (2,2), деревня 1-го уровня S001FT0001 с одним юнитом в
    /// гарнизоне, предмет S001IM0001 у отряда, отношение 50.</summary>
    private static MapState Clean(Func<MapState, MapState>? change = null)
    {
        var item = Book.Value.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.Armor);
        var stack = Stack("S001KC0001", 2, 2, "S001UN0001", "S001UN0002") with { Items = ["S001IM0001"] };
        var map = new MapState
        {
            Size = 8, Tiles = new Tile[8, 8], Turn = 3,
            Stacks = new Dictionary<string, MapStack>(StringComparer.OrdinalIgnoreCase) { [stack.Id] = stack },
            Units = new Dictionary<string, MapUnit>(StringComparer.OrdinalIgnoreCase)
            {
                ["S001UN0001"] = Unit("S001UN0001"), ["S001UN0002"] = Unit("S001UN0002", 40), ["S001UN0003"] = Unit("S001UN0003"),
            },
            Players = new Dictionary<string, MapPlayer>(StringComparer.OrdinalIgnoreCase)
            {
                ["S001PL0001"] = new()
                {
                    Id = "S001PL0001", RaceId = "G000RR0000", LordId = "", Bank = new Bank(500, 0, 10, 0, 0, 0), FogId = "",
                    BuildingsId = "", Buildings = [],
                },
            },
            Objects = new Dictionary<string, MapObject>(),
            Plan = new Dictionary<(int X, int Y), IReadOnlyList<string>> { [(2, 2)] = ["S001KC0001"] },
            Fog = new Dictionary<string, bool[,]>(),
            Sites =
            [
                new MapSite
                {
                    Id = "S001FT0001", Kind = SiteKind.Village, RawKind = "MidVillage", X = 5, Y = 5, Owner = "S001PL0001", Size = 1,
                    Guards = ["S001UN0003"], GuardSlots = [0, -1, -1, -1, -1, -1],
                },
            ],
            ItemTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["S001IM0001"] = item.Id },
            Relations = [new Relation(0, 1, 50u << 1)],
        };
        return change is null ? map : change(map);
    }

    private static MapState WithStacks(MapState map, params MapStack[] stacks) =>
        GameSession.With(map, stacks: stacks.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase));

    private static MapState WithUnits(MapState map, params MapUnit[] units)
    {
        var all = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var u in units) all[u.Id] = u;
        return GameSession.With(map, units: all);
    }

    private static Violation Single(MapState map, MapState? before = null) =>
        Assert.Single(MapInvariants.Check(map, Book.Value, before));

    [Fact]
    public void CleanMap_HasNoViolations() => Assert.Empty(MapInvariants.Check(Clean(), Book.Value, Clean()));

    [Fact]
    public void UnitInTwoGroups_IsCaught()
    {
        var map = Clean(m => WithStacks(m, m.Stacks["S001KC0001"], Stack("S001KC0002", 4, 4, "S001UN0002") with { Owner = "S001PL0001" }));
        map = GameSession.With(map, plan: new Dictionary<(int X, int Y), IReadOnlyList<string>> { [(2, 2)] = ["S001KC0001"], [(4, 4)] = ["S001KC0002"] });
        var v = Single(map);
        Assert.Equal(("unit_shared", "S001UN0002"), (v.Code, v.Object));
    }

    [Fact]
    public void MissingAndOrphanUnits_AreCaught()
    {
        var units = Clean().Units.Where(p => p.Key != "S001UN0002").ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units["S001UN0009"] = Unit("S001UN0009");
        var found = MapInvariants.Check(GameSession.With(Clean(), units: units), Book.Value);
        Assert.Contains(found, v => v is { Code: "unit_missing", Object: "S001KC0001" });
        Assert.Contains(found, v => v is { Code: "unit_orphan", Object: "S001UN0009" });
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(101)]
    public void HpOutOfRange_IsCaught(int hp)
    {
        var v = Single(Clean(m => WithUnits(m, Unit("S001UN0002", hp))));
        Assert.Equal(("hp", "S001UN0002"), (v.Code, v.Object));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10000, 0)]
    [InlineData(0, 10000)]
    public void BankOutOfRange_IsCaught(long gold, long life)
    {
        var map = Clean(m =>
        {
            var p = m.Players["S001PL0001"];
            return GameSession.With(m, players: new Dictionary<string, MapPlayer> { [p.Id] = p with { Bank = new Bank(gold, 0, life, 0, 0, 0) } });
        });
        var v = Single(map);
        Assert.Equal(("bank", "S001PL0001"), (v.Code, v.Object));
    }

    [Fact]
    public void StackOutsidePlanOrMap_IsCaught()
    {
        var moved = Clean(m => WithStacks(m, m.Stacks["S001KC0001"] with { X = 3 }));
        var found = MapInvariants.Check(moved, Book.Value);
        Assert.Contains(found, v => v is { Code: "plan", Object: "S001KC0001" } && v.Detail.StartsWith("нет в плане", StringComparison.Ordinal));
        Assert.Contains(found, v => v is { Code: "plan", Object: "S001KC0001" } && v.Detail.StartsWith("в плане клетки (2,2)", StringComparison.Ordinal));
        var off = Clean(m => WithStacks(m, m.Stacks["S001KC0001"] with { X = 9 }));
        Assert.Contains(MapInvariants.Check(off, Book.Value), v => v is { Code: "off_map", Object: "S001KC0001" });
    }

    [Fact]
    public void LeaderAliveFlagAgainstHp_IsCaught()
    {
        var stale = Clean(m => WithStacks(m, m.Stacks["S001KC0001"] with { LeaderAlive = false }));
        Assert.Equal(("leader_alive", "S001KC0001"), (Single(stale).Code, Single(stale).Object));
        var dead = Clean(m => WithUnits(m, m.Units["S001UN0001"] with { Hp = 0 }));
        Assert.Equal(("leader_alive", "S001KC0001"), (Single(dead).Code, Single(dead).Object));
    }

    [Fact]
    public void InsideWithoutVisitor_IsCaught()
    {
        var v = Single(Clean(m => WithStacks(m, m.Stacks["S001KC0001"] with { Inside = "S001FT0001" })));
        Assert.Equal(("inside", "S001KC0001"), (v.Code, v.Object));
    }

    [Fact]
    public void UnknownItemAndWrongTalismanCharges_AreCaught()
    {
        var map = Clean(m =>
        {
            var s = m.Stacks["S001KC0001"];
            return GameSession.With(WithStacks(m, s with { Items = [.. s.Items, "S001IM0002"] }),
                                    talismanCharges: new Dictionary<string, int> { ["S001IM0001"] = 3 });
        });
        var found = MapInvariants.Check(map, Book.Value);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, v => v is { Code: "item", Object: "S001IM0002" });
        Assert.Contains(found, v => v is { Code: "talisman", Object: "S001IM0001" });
    }

    /// <summary>Предмет руины — тип из правил; экземпляр (даже живой, с типом) — нарушение: так карта 8 уронила оригинал (20261016e-av).</summary>
    [Fact]
    public void RuinItemInstanceOrUnknownType_IsCaught()
    {
        var type = Clean().ItemTypes["S001IM0001"];
        MapState WithRuin(string item) => Clean(m => GameSession.With(m, sites:
        [
            .. m.Sites,
            new MapSite { Id = "S001RU0001", Kind = SiteKind.Ruin, RawKind = "MidRuin", X = 0, Y = 0, ItemId = item },
        ]));
        Assert.DoesNotContain(MapInvariants.Check(WithRuin(type), Book.Value), v => v.Code == "item");
        Assert.Equal(("item", "S001IM0001"), (Single(WithRuin("S001IM0001")).Code, Single(WithRuin("S001IM0001")).Object));
        Assert.Equal(("item", "G000IGffff"), (Single(WithRuin("G000IGffff")).Code, Single(WithRuin("G000IGffff")).Object));
    }

    [Fact]
    public void RelationOutOfRange_IsCaught()
    {
        var v = Single(Clean(m => GameSession.With(m, relations: [new Relation(0, 1, 120u << 1)])));
        Assert.Equal(("relation", "0-1"), (v.Code, v.Object));
    }

    /// <summary>Вместимость — по росту: деревня 1-го уровня с двумя юнитами чиста сама по себе (сценарий так может, корпус probe72), а
    /// выросшая из одного в два — нарушение.</summary>
    [Fact]
    public void GarrisonGrownOverCapacity_IsCaught_ButScenarioGivenIsNot()
    {
        MapState Grown(MapState m)
        {
            var site = m.Sites[0] with { Guards = ["S001UN0003", "S001UN0004"], GuardSlots = [0, -1, 1, -1, -1, -1] };
            return WithUnits(GameSession.With(m, sites: [site]), Unit("S001UN0004"));
        }
        Assert.Empty(MapInvariants.Check(Clean(Grown), Book.Value));
        Assert.Empty(MapInvariants.Check(Clean(Grown), Book.Value, Clean(Grown)));
        var v = Single(Clean(Grown), Clean());
        Assert.Equal(("capacity", "S001FT0001"), (v.Code, v.Object));
    }
}
