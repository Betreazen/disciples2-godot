using D2.Rules.Map;

/// <summary>
/// Движение по карте на данных, собранных руками: ни одного файла, ни одного журнала — только
/// клетки, флаги и ожидаемые числа. Всё, что здесь проверяется, снято дизассемблером
/// <c>Discipl2.exe</c> (Gog): <c>computeMovementCost</c> <c>0x6027f3</c>,
/// <c>stackCanMoveToPosition</c> <c>0x4cd1dd</c>, <c>isWaterTileSurroundedByWater</c>
/// <c>0x5e6453</c> — подробности у методов <see cref="Movement"/>. Сверку с настоящим журналом
/// делает <c>C4MoveHarnessTests</c>.
/// </summary>
public class MovementTests
{
    // Буквы карты: . равнина, F лес, W вода, M горы, R равнина с дорогой, C равнина с городом.
    private static MapState Grid(string[] rows, IEnumerable<MapStack>? stacks = null,
                                 IEnumerable<MapUnit>? units = null)
    {
        int size = rows.Length;
        Assert.All(rows, r => Assert.Equal(size, r.Length));   // карта квадратная, как в .sg

        var tiles = new Tile[size, size];
        var objects = new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase);
        var plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>();

        void Put(int x, int y, string id, string kind)
        {
            objects[id] = new MapObject { Id = id, Kind = kind, X = x, Y = y };
            if (!plan.TryGetValue((x, y), out var list)) plan[(x, y)] = list = new List<string>();
            ((List<string>)list).Add(id);
        }

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (ground, extra) = rows[y][x] switch
                {
                    '.' => (Ground.Plain, ""),
                    'F' => (Ground.Forest, ""),
                    'W' => (Ground.Water, ""),
                    'M' => (Ground.Mountain, ""),
                    'R' => (Ground.Plain, "RA"),
                    'C' => (Ground.Plain, "FT"),
                    var c => throw new ArgumentException($"неизвестная клетка '{c}'"),
                };
                tiles[x, y] = new Tile((byte)(((int)ground << 3) | 1));
                if (extra.Length > 0) Put(x, y, $"S001{extra}{x:D2}{y:D2}", extra);
            }

        foreach (var s in stacks ?? [])
        {
            if (!plan.TryGetValue((s.X, s.Y), out var list)) plan[(s.X, s.Y)] = list = new List<string>();
            ((List<string>)list).Add(s.Id);
        }

        return new MapState
        {
            Size = size,
            Tiles = tiles,
            Stacks = (stacks ?? []).ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase),
            Units = (units ?? []).ToDictionary(u => u.Id, u => u, StringComparer.OrdinalIgnoreCase),
            Players = new Dictionary<string, MapPlayer>(),
            Objects = objects,
            Plan = plan,
            Fog = new Dictionary<string, bool[,]>(),
            Turn = 1,
        };
    }

    private static MapStack Stack(string id, int x, int y, int move = 20, string leaderId = "S001UN0001") =>
        new()
        {
            Id = id, Owner = "S001PL0001", Subrace = "S001SR0001", LeaderId = leaderId,
            LeaderAlive = true, X = x, Y = y, Move = move, Inside = "",
            UnitIds = [leaderId], Slots = [-1, -1, 0, -1, -1, -1],
        };

    private static readonly MoveOptions Foot = new();                                  // живой лидер, без бонусов
    private static readonly MovementRules Rules = MovementRules.Vanilla;

    private static int Cost(MapState map, int x, int y, MoveOptions opt) => Movement.Cost(map, x, y, opt, Rules);

    [Fact] public void Cost_ByGround_IsTwoFourSix_AndMountainIsForbidden()
    {
        var map = Grid([".FWM", "....", "....", "...."]);

        Assert.Equal(2, Cost(map, 0, 0, Foot));                 // равнина
        Assert.Equal(4, Cost(map, 1, 0, Foot));                 // лес
        Assert.Equal(6, Cost(map, 2, 0, Foot));                 // вода — проходима и стоит 6
        Assert.Equal(Movement.Forbidden, Cost(map, 3, 0, Foot)); // горы
    }

    [Fact] public void Cost_OutsideMap_IsForbidden()
    {
        var map = Grid(["..", ".."]);

        Assert.Equal(Movement.Forbidden, Cost(map, -1, 0, Foot));
        Assert.Equal(Movement.Forbidden, Cost(map, 0, 2, Foot));
    }

    [Fact] public void Cost_OnRoad_IsCheaperThanPlainUnderIt()
    {
        var map = Grid(["R.", ".."]);

        Assert.Equal(1, Cost(map, 0, 0, Foot));
        Assert.Equal(2, Cost(map, 1, 0, Foot));
    }

    /// <summary>Бонус равнины равнину не удешевляет — он отменяет скидку за дорогу
    /// (<c>0x6029d3</c>). Некрасиво, но так в движке.</summary>
    [Fact] public void PlainsBonus_DoesNotDiscountPlain_ButCancelsTheRoadDiscount()
    {
        var map = Grid(["R.", ".."]);
        var bonus = Foot with { PlainsBonus = true };

        Assert.Equal(2, Cost(map, 0, 0, bonus));
        Assert.Equal(2, Cost(map, 1, 0, bonus));
    }

    [Fact] public void ForestAndWaterBonuses_MakeTheirGroundCostTwo()
    {
        var map = Grid([".F", "W."]);

        Assert.Equal(2, Cost(map, 1, 0, Foot with { ForestBonus = true }));
        Assert.Equal(2, Cost(map, 0, 1, Foot with { WaterBonus = true }));
        Assert.Equal(4, Cost(map, 1, 0, Foot with { WaterBonus = true }));   // чужой бонус не действует
    }

    [Fact] public void DeadLeader_DoublesEveryGround_AndIgnoresBonusesAndRoad()
    {
        var map = Grid([".FWR", "....", "....", "...."]);
        var dead = new MoveOptions(LeaderAlive: false, PlainsBonus: true, ForestBonus: true, WaterBonus: true);

        Assert.Equal(4, Cost(map, 0, 0, dead));    // равнина 2 -> 4
        Assert.Equal(8, Cost(map, 1, 0, dead));    // лес 4 -> 8
        Assert.Equal(12, Cost(map, 2, 0, dead));   // вода 6 -> 12
        Assert.Equal(4, Cost(map, 3, 0, dead));    // дорога не спасает
    }

    [Fact] public void InteractiveObjectOnTile_ForbidsTheStepBeforeGroundIsEvenRead()
    {
        var map = Grid(["C.", ".."]);

        Assert.Equal(Movement.Forbidden, Cost(map, 0, 0, Foot));
        Assert.False(Movement.Passable(map, 0, 0, Foot));
    }

    [Fact] public void WaterOnly_WalksDeepWaterOnly_AndIsStoppedByForbidWaterOnLand()
    {
        var map = Grid(["WWWW", "WWWW", "WWW.", "WWWM"]);
        var ship = new MoveOptions(WaterOnly: true);

        Assert.Equal(2, Cost(map, 1, 1, ship));                       // вода со всех восьми сторон
        Assert.Equal(Movement.Forbidden, Cost(map, 2, 2, ship));      // берег рядом — уже не глубокая
        Assert.Equal(Movement.Forbidden, Cost(map, 3, 2, ship));      // суша
        Assert.Equal(Movement.Forbidden, Cost(map, 3, 3, ship));      // горы

        var shown = ship with { ForbidWaterOnLand = false };
        Assert.Equal(1000, Cost(map, 2, 2, shown));                   // мелкая вода — «нарисовать, но не пройти»
        Assert.Equal(1000, Cost(map, 3, 2, shown));                   // суша
        Assert.Equal(Movement.Forbidden, Cost(map, 3, 3, shown));     // горы запрещены и так
    }

    [Fact] public void DeepWater_AtMapEdge_CountsOnlyNeighboursInsideTheMap()
    {
        var map = Grid(["WWW", "WWW", "WWW"]);

        Assert.True(Movement.IsDeepWater(map, 0, 0));   // соседи за краем карты пропускаются
        Assert.True(Movement.IsDeepWater(map, 1, 1));
    }

    [Fact] public void Passable_IsTerrainOnly_AndSaysNothingAboutOtherStacks()
    {
        var map = Grid([".M", "W."], stacks: [Stack("S001KC0001", 1, 1)]);

        Assert.True(Movement.Passable(map, 0, 0, Foot));
        Assert.False(Movement.Passable(map, 1, 0, Foot));
        Assert.True(Movement.Passable(map, 0, 1, Foot));    // вода пешему разрешена, просто дорога
        Assert.True(Movement.Passable(map, 1, 1, Foot));    // занятая клетка проходима по местности
    }

    [Fact] public void CanMoveTo_RefusesOwnTileAndTileTakenByAnotherStack()
    {
        var mine = Stack("S001KC0001", 0, 0);
        var theirs = Stack("S001KC0002", 1, 1);
        var map = Grid(["..", ".."], stacks: [mine, theirs]);

        Assert.False(Movement.CanMoveTo(map, mine, 0, 0));   // клетка, где я и стою
        Assert.False(Movement.CanMoveTo(map, mine, 1, 1));   // занята чужим отрядом
        Assert.True(Movement.CanMoveTo(map, mine, 1, 0));
    }

    [Fact] public void StartMovement_TakesLeaderMoveAndFallsBackLoudly()
    {
        var stack = Stack("S001KC0001", 0, 0, leaderId: "S001UN0001");
        var map = Grid(["..", ".."], stacks: [stack],
                       units: [new MapUnit { Id = "S001UN0001", TypeId = "G000UU0019" }]);
        var moveByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["g000uu0019"] = 20 };

        Assert.Equal(20, Movement.StartMovement(stack, map, moveByType));
        Assert.Equal(7, Movement.StartMovement(stack, map, new Dictionary<string, int>(), fallback: 7));
    }

    /// <summary>Задача 11 Ф7: уровневые навыки лидера правят запас хода и обзор — Pathfinding
    /// (<c>g000um4004</c>, <c>GmodifL TYPE 7 PERCENT 20</c>) и Keen-Sight (<c>g000um4015</c>,
    /// <c>TYPE 1 NUMBER 3</c>). Числа — из таблицы игры; проверяется, что цепочка доходит до обеих
    /// функций и что без справочника <c>Gmodif</c> ответ остаётся базовым.
    /// <para>Живая половина — счётчик <c>leader_move_max</c> (99/0 на первых ходах дня корпуса);
    /// у обзора наблюдений нет ни одного, поэтому у Keen-Sight только этот тест.</para></summary>
    [Fact] public void StartMovementAndRadius_TakeLeaderLevelSkills()
    {
        var stack = Stack("S001KC0001", 0, 0, leaderId: "S001UN0001");
        var leader = new MapUnit
        {
            Id = "S001UN0001", TypeId = "G000UU0019",
            Modifiers = ["G000UM4004", "G000UM4015", "G000000000"],
        };
        var map = Grid(["..", ".."], stacks: [stack], units: [leader]);
        var moveByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["g000uu0019"] = 20 };
        var scoutByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["g000uu0019"] = 5 };
        var gmodif = new Dictionary<string, D2.Rules.Units.Modifier>(StringComparer.OrdinalIgnoreCase)
        {
            ["g000um4004"] = new("g000um4004",
                [new(D2.Rules.Units.ModifierElementType.MoveAllowance, Percent: 20)]),
            ["g000um4015"] = new("g000um4015",
                [new(D2.Rules.Units.ModifierElementType.ScoutingRange, Number: 3)]),
        };

        Assert.Equal(24, Movement.StartMovement(stack, map, moveByType, modifiers: gmodif));    // 20 + 20 %
        Assert.Equal(8, Fog.Radius(stack, map, scoutByType, modifiers: gmodif));                // 5 + 3
        Assert.Equal(20, Movement.StartMovement(stack, map, moveByType));                       // без Gmodif
        Assert.Equal(5, Fog.Radius(stack, map, scoutByType));
    }

    /// <summary>Потолок обзора <c>stackScoutRange</c> {1, 8} (<c>.rdata 0x6e88fc</c>): Ranger
    /// (<c>g000uu0020</c>, <c>SCOUT</c> 6) с Keen-Sight видит 8, а не 9 — зонд <c>scout</c> 0x5d520f,
    /// (bonus 3, ret 8) у героя с <c>SCOUT</c> 6 (отчёт A §6, ai-q39, 2/0). Снизу — 1.</summary>
    [Fact] public void Radius_IsClampedToOneThroughEight()
    {
        var stack = Stack("S001KC0001", 0, 0, leaderId: "S001UN0001");
        var leader = new MapUnit { Id = "S001UN0001", TypeId = "G000UU0020", Modifiers = ["G000UM4015"] };
        var map = Grid(["..", ".."], stacks: [stack], units: [leader]);
        var gmodif = new Dictionary<string, D2.Rules.Units.Modifier>(StringComparer.OrdinalIgnoreCase)
        {
            ["g000um4015"] = new("g000um4015",
                [new(D2.Rules.Units.ModifierElementType.ScoutingRange, Number: 3)]),
        };
        IReadOnlyDictionary<string, int> Scout(int v) =>
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["g000uu0020"] = v };

        Assert.Equal(8, Fog.Radius(stack, map, Scout(6), modifiers: gmodif));   // 6 + 3 → 8
        Assert.Equal(6, Fog.Radius(stack, map, Scout(6)));
        Assert.Equal(1, Fog.Radius(stack, map, Scout(0)));
    }

    /// <summary>Последний шаг дня делается в долг: очков 5, два леса по 4 — отряд проходит оба и остаётся с нулём
    /// (<c>docs/map-path.md</c> §2, <c>0x5cd5b2</c>). На третий шаг очков уже нет.</summary>
    [Fact] public void Walk_TakesTheLastStepOnCredit()
    {
        var map = Grid([".FF.", "....", "....", "...."]);
        var stack = Stack("S001KC0001", 0, 0, move: 5);

        var walked = Movement.Walk(map, stack, [(1, 0), (2, 0), (3, 0)], Foot, Rules, out var spent);

        Assert.Equal([(1, 0), (2, 0)], walked);
        Assert.Equal(8, spent);
    }

    [Fact] public void Walk_WalksTheWholePathWhenPointsSuffice()
    {
        var map = Grid([".RR.", "....", "....", "...."]);
        var stack = Stack("S001KC0001", 0, 0, move: 20);

        var walked = Movement.Walk(map, stack, [(1, 0), (2, 0), (3, 0)], Foot, Rules, out var spent);

        Assert.Equal(3, walked.Count);
        Assert.Equal(1 + 1 + 2, spent);
    }

    [Fact] public void Walk_BreaksOnAForbiddenTileEvenWithPointsLeft()
    {
        var map = Grid([".M..", "....", "....", "...."]);
        var stack = Stack("S001KC0001", 0, 0, move: 20);

        var walked = Movement.Walk(map, stack, [(1, 0), (2, 0)], Foot, Rules, out var spent);

        Assert.Empty(walked);
        Assert.Equal(0, spent);
    }

    /// <summary>Событие журнала, собранное руками: ровно те поля, что пишет зонд
    /// (<c>docs/corpus-schema.md</c>, <c>move_cost</c>), проходят через тот же переходник, что и в
    /// харнессе, и дают ту же стоимость. Это проверка стыка «журнал -> флаги», а не формулы:
    /// перепутанные местами <c>plains</c> и <c>forest</c> здесь и вылезут.</summary>
    [Fact] public void MoveCostEvent_FlagsMapOntoMoveOptions()
    {
        var map = Grid([".F", "W."]);
        var forestWithBonus = new D2.Data.Corpus.MoveCostEvent(
            Seq: 1, Tid: 7, X: 1, Y: 0, Stack: "S001KC0001", LeaderAlive: true,
            Plains: false, Forest: true, Water: false, WaterOnly: false, ForbidWater: true, Ret: 2);

        Assert.Equal(forestWithBonus.Ret, Movement.Cost(map, 1, 0, MoveEvents.OptionsOf(forestWithBonus), Rules));

        var deadLeaderOnWater = forestWithBonus with { X = 0, Y = 1, LeaderAlive = false, Forest = false, Ret = 12 };
        Assert.Equal(deadLeaderOnWater.Ret,
                     Movement.Cost(map, 0, 1, MoveEvents.OptionsOf(deadLeaderOnWater), Rules));
    }
}
