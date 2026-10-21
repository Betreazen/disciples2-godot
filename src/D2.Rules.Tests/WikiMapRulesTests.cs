using D2.Data;
using D2.Data.Corpus;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Задача 26 Ф7 — пять утверждений русской вики про карту
/// (<c>disciples-modding/.superpowers/sdd/2026-09-11-f7-port/wiki-effects.md</c> §3 «Карта»,
/// пункты Н16, Н17, Н18, Н19, Н24). Каждое сначала прочитано в дизассемблере
/// <c>game-pristine/Discipl2.exe</c> (GOG, <c>ImageBase 0x400000</c>), и только то, что код
/// подтвердил, перенесено.
///
/// <list type="table">
/// <item><term>Н16 атака стоит половину запаса</term><description>подтверждено кодом
/// (<see cref="Movement.HalfAllowance"/>), перенесено; наблюдений корпуса — см.
/// <c>attack_move_half</c>.</description></item>
/// <item><term>Н17 павший лидер вдвое медленнее</term><description>подтверждено кодом и уже было в
/// порте удвоенной ценой шага; новое — то, что дневной запас при этом НЕ урезается
/// (<see cref="Movement.StartMovement"/>). Счётчик <c>dead_leader_ground</c>.</description></item>
/// <item><term>Н18 длительность эффектов на карте</term><description>подтверждено кодом:
/// <c>TURN_STOP = CUR_TURN + 1</c> (<see cref="SpellCast.MapEffectDays"/>). Счётчик
/// <c>map_effect_turn_stop</c>.</description></item>
/// <item><term>Н19 заклинание карты тратит оберег</term><description><b>не перенесено</b>: выдать
/// оберег на карте в ванили нечем и хранить его расход негде (см. <see cref="SpellCast"/>).
/// Счётчик <c>spell_ward_columns</c> проверяет только посылку.</description></item>
/// <item><term>Н24 цена наёмника растёт с уровнем</term><description>рост перенесён по коду
/// (<see cref="Economy.HireCost(UnitHire, int, int, DynUpgrade?, DynUpgrade?)"/>), а арифметика
/// вики опровергнута таблицей.</description></item>
/// </list>
/// </summary>
[Trait("Category", "Corpus")]
public class WikiMapRulesTests(ITestOutputHelper output)
{
    // ---- модульные: числа из кода движка ---------------------------------------------------------

    /// <summary><c>ceil(MOVE / 2)</c> от полного запаса (<c>0x5CE239</c>: <c>fild; fmul 0.5; ceil</c>).
    /// Числа взяты из корпуса: базовый герой 20, он же с Pathfinding 25, выросший 21 и 35, 0 —
    /// отряд, уже потративший ход целиком.</summary>
    [Fact]
    public void HalfAllowance_IsCeilOfHalf()
    {
        Assert.Equal(10, Movement.HalfAllowance(20));
        Assert.Equal(11, Movement.HalfAllowance(21));
        Assert.Equal(13, Movement.HalfAllowance(25));
        Assert.Equal(18, Movement.HalfAllowance(35));
        Assert.Equal(1, Movement.HalfAllowance(1));
        Assert.Equal(0, Movement.HalfAllowance(0));
        Assert.Equal(0, Movement.HalfAllowance(-4));
        // Дом величины один: действие вора и атака берут одно и то же число.
        Assert.Equal(Movement.HalfAllowance(35), Thieves.MoveCost(35));
    }

    /// <summary>Атака списывает половину ПОЛНОГО запаса с остатка, с полом нуля
    /// (<c>CVisitorChangeStackMoveAllowance</c>, <c>0x5ECFD5</c>).</summary>
    [Fact]
    public void AfterAttack_SubtractsHalfOfFull_FloorZero()
    {
        Assert.Equal(10, Movement.AfterAttack(20, 20));
        Assert.Equal(4, Movement.AfterAttack(20, 14));
        Assert.Equal(0, Movement.AfterAttack(20, 8));     // остатка меньше половины — пол нуля
        Assert.Equal(17, Movement.AfterAttack(35, 35));
    }

    /// <summary>
    /// Вики Н17 «вдвое медленнее» — это удвоенная цена шага, а не половинный запас, и удвоение
    /// считается от цены грунта БЕЗ бонусов лидера и БЕЗ дороги: на дороге мёртвый лидер платит
    /// вчетверо, а под бонусом леса и воды — вчетверо и вшестеро.
    /// </summary>
    [Fact]
    public void DeadLeader_DoublesBareGroundCost_NotAllowance()
    {
        var map = Row((Ground.Plain, false), (Ground.Forest, false), (Ground.Water, false),
                      (Ground.Plain, true));
        var alive = new MoveOptions();
        var dead = alive with { LeaderAlive = false };
        var r = MovementRules.Vanilla;

        Assert.Equal((2, 4), (Movement.Cost(map, 0, 0, alive, r), Movement.Cost(map, 0, 0, dead, r)));
        Assert.Equal((4, 8), (Movement.Cost(map, 1, 0, alive, r), Movement.Cost(map, 1, 0, dead, r)));
        Assert.Equal((6, 12), (Movement.Cost(map, 2, 0, alive, r), Movement.Cost(map, 2, 0, dead, r)));
        // дорога: 1 -> 4, то есть вчетверо
        Assert.Equal((1, 4), (Movement.Cost(map, 3, 0, alive, r), Movement.Cost(map, 3, 0, dead, r)));
        // бонусы лидера у мёртвого не действуют: лес 2 -> 8, вода 2 -> 12
        var bonus = alive with { ForestBonus = true, WaterBonus = true, PlainsBonus = true };
        Assert.Equal((2, 8), (Movement.Cost(map, 1, 0, bonus, r),
                              Movement.Cost(map, 1, 0, bonus with { LeaderAlive = false }, r)));
        Assert.Equal((2, 12), (Movement.Cost(map, 2, 0, bonus, r),
                               Movement.Cost(map, 2, 0, bonus with { LeaderAlive = false }, r)));

        // Запас дня мёртвый лидер не урезает: StartMovement читает MOVE и цепочку, а leaderAlive — нет.
        var leader = new MapUnit { Id = "S001UN0001", TypeId = "g000uu0019", Level = 1, Hp = 10 };
        var stack = Stack("S001KC0000", 0, 0, leaderAlive: false);
        var live = Stack("S001KC0001", 1, 0, leaderAlive: true);
        var withUnits = With(map, [stack, live], [leader]);
        var move = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["g000uu0019"] = 20 };
        Assert.Equal(20, Movement.StartMovement(stack, withUnits, move));
        Assert.Equal(Movement.StartMovement(live, withUnits, move), Movement.StartMovement(stack, withUnits, move));
    }

    /// <summary>Н18: наложенное на карте живёт до следующего хода — <c>TURN_STOP = CUR_TURN + 1</c>
    /// (<c>0x5DC4A0</c> у <c>CMidSpellCast</c>, <c>0x5D7187</c> у <c>CMidSpellEffects</c>).</summary>
    [Fact]
    public void MapEffect_StopsOnNextTurn()
    {
        Assert.Equal(1, SpellCast.MapEffectDays);
        Assert.Equal(8, SpellCast.TurnStop(7));
        Assert.True(SpellCast.Live(turnStop: 8, turn: 7));    // день каста
        Assert.False(SpellCast.Live(turnStop: 8, turn: 8));   // следующий ход — уже не действует
        Assert.False(SpellCast.Live(turnStop: 8, turn: 9));
        // §3 п. 54: снимает начало хода владельца — до него запись дня 7 ещё действует в день 8
        Assert.True(SpellCast.Live(turnStop: 8, turn: 8, ownerStarted: false));
        Assert.False(SpellCast.Live(turnStop: 8, turn: 9, ownerStarted: false));
    }

    /// <summary>
    /// Н24: цена найма растёт шагами <c>GDynUpgr.ENROLL_C</c> — число из таблицы, не из вики.
    /// <para>Числа: у героя <c>g000uu0019</c> (<c>ENROLL_C</c> g0500, <c>DYN_UPG1</c>
    /// <c>g000du0019</c> с прибавкой g0100) уровень 2 стоит 600, уровень 5 — 900. У новобранца
    /// <c>g000uu0001</c> (g0050, прибавка g0005) уровень 3 — 60. У <c>g000uu8001</c> (g9950,
    /// прибавка g1500) второй уровень упирается в потолок валюты 9999.</para>
    /// </summary>
    [SkippableFact]
    public void HireCost_GrowsWithLevel_FromTables()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var prices = PriceBook.Load(GamePaths.Globals);

        long Gold(string type, int level) => prices.HirePrice(type, level)!.Gold;

        Assert.Equal(500, Gold("g000uu0019", 1));
        Assert.Equal(500, Gold("g000uu0019", 0));      // ниже базового уровня — базовая цена
        Assert.Equal(600, Gold("g000uu0019", 2));
        Assert.Equal(900, Gold("g000uu0019", 5));
        Assert.Equal(50, Gold("g000uu0001", 1));
        Assert.Equal(60, Gold("g000uu0001", 3));
        Assert.Equal(9999, Gold("g000uu8001", 2));     // обрезка валюты 9999 после сложения
        Assert.Null(prices.HirePrice("g000uuZZZZ", 1));
    }

    /// <summary>
    /// Арифметика вики Н24 («+10 % от изначальной за уровень, округление до 5, минимум 5,
    /// максимум 700») — это не правило движка, а способ, которым набивали <c>GDynUpgr</c>, и он
    /// описан неверно. Тест печатает счёт и стоит на трёх фактах: совпадений меньше, чем строк;
    /// часть строк совпадает только с неокруглёнными 10 %; потолок 700 в таблице пробит.
    /// </summary>
    [SkippableFact]
    public void HireCost_WikiFormula_IsNotTheRule()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var prices = PriceBook.Load(GamePaths.Globals);
        int rounded = 0, exact = 0, neither = 0, overCap = 0;
        var examples = new List<string>();
        foreach (var (id, hire) in prices.Economy.Hire)
        {
            if (!prices.Extras.Units.TryGetValue(id, out var ext) || ext.DynUpg1Id.Length == 0) continue;
            if (!prices.Extras.DynUpgrades.TryGetValue(ext.DynUpg1Id, out var up)) continue;
            long gold = hire.Cost.Gold, delta = up.EnrollCost.Gold;
            long tenth = gold / 10;
            long wiki = Math.Clamp((long)(Math.Round(gold * 0.10 / 5, MidpointRounding.AwayFromZero) * 5), 5, 700);
            if (delta > 700) overCap++;
            if (delta == wiki) rounded++;
            else if (delta == tenth && gold % 10 == 0) exact++;
            else { neither++; if (examples.Count < 6) examples.Add($"{id}: ENROLL_C {gold}, GDynUpgr {delta}, вики {wiki}"); }
        }
        output.WriteLine($"GDynUpgr.ENROLL_C против формулы вики: совпало с округлением до 5 — {rounded}, " +
                         $"только с неокруглёнными 10 % — {exact}, ни с чем — {neither}; " +
                         $"прибавок больше 700 — {overCap}");
        foreach (var e in examples) output.WriteLine("  " + e);
        Assert.True(neither > 0, "формула вики совпала со всей таблицей — вывод задачи 26 надо переписать");
        Assert.True(overCap > 0, "потолок 700 вики в таблице не пробит — вывод задачи 26 надо переписать");
        Assert.True(rounded > neither, "формула вики не описывает даже большинство строк — проверить чтение");
    }

    // ---- харнесс -------------------------------------------------------------------------------

    /// <summary>
    /// Корпус: шесть счётчиков задачи 26 плюс две гипотезы рядом. В приёмку входят все шесть;
    /// гипотезы печатаются для разбора (обе красные — так и должно быть, см. отчёт задачи 26).
    /// </summary>
    [SkippableFact]
    public void MapWikiRules_MatchCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var book = ProgressBook.Load(GamePaths.Globals);
        var c = new Counters();
        var audit = new MapWikiAudit(book, c);

        audit.WardColumns();
        var (unique, _) = JournalSet.Distinct(CorpusFiles.Journals());
        foreach (var path in unique)
        {
            audit.DeadLeaderSteps(path);
            if (TreasuryJournal.HasBank(path)) audit.AttacksOnMap(path);
        }
        audit.PrintNotDouble();
        audit.PrintSpentOverHalf();
        audit.MapEffects();
        audit.MercenaryOffers();

        c.Print(output, "картовые правила по вики (задача 26 Ф7)");
        c.Require("attack_move_half", "attack_move_idle", "dead_leader_ground", "map_effect_turn_stop",
                  "merc_price_level", "spell_ward_columns");
    }

    // ---- рукодельная карта ----------------------------------------------------------------------

    /// <summary>Карта size×size, в верхней строке — перечисленные грунты, остальное равнина.
    /// Ровно то, что нужно ветвлению <see cref="Movement.Cost"/>; файлов и журналов здесь нет.</summary>
    private static MapState Row(params (Ground Ground, bool Road)[] cells)
    {
        int size = cells.Length;
        var tiles = new Tile[size, size];
        var objects = new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (ground, road) = y == 0 ? cells[x] : (Ground.Plain, false);
                tiles[x, y] = new Tile((byte)(((int)ground << 3) | 1));
                if (!road) continue;
                string id = $"S001RA{x:D2}{y:D2}";
                objects[id] = new MapObject { Id = id, Kind = "RA", X = x, Y = y };
            }
        return new MapState
        {
            Size = size, Tiles = tiles,
            Stacks = new Dictionary<string, MapStack>(), Units = new Dictionary<string, MapUnit>(),
            Players = new Dictionary<string, MapPlayer>(), Objects = objects,
            Plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>(),
            Fog = new Dictionary<string, bool[,]>(), Turn = 1,
        };
    }

    private static MapState With(MapState map, IEnumerable<MapStack> stacks, IEnumerable<MapUnit> units) =>
        new()
        {
            Size = map.Size, Tiles = map.Tiles, Objects = map.Objects, Plan = map.Plan, Fog = map.Fog,
            Turn = map.Turn, Players = map.Players,
            Stacks = stacks.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase),
            Units = units.ToDictionary(u => u.Id, u => u, StringComparer.OrdinalIgnoreCase),
        };

    private static MapStack Stack(string id, int x, int y, bool leaderAlive) =>
        new()
        {
            Id = id, Owner = "S001PL0001", Subrace = "S001SR0001", LeaderId = "S001UN0001",
            LeaderAlive = leaderAlive, X = x, Y = y, Move = 20, Inside = "",
            UnitIds = ["S001UN0001"], Slots = [-1, -1, 0, -1, -1, -1],
        };
}
