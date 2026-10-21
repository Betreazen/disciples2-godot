using D2.Data;
using D2.Data.Corpus;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Задача 4 Ф7 — опыт за бой, смена типа и «Остановить продвижение» (<see cref="Experience"/>,
/// <see cref="UnitUpgrade"/>). Харнесс: <c>xp_per_kill</c>, <c>xp_award</c>, <c>xp_weapon_master</c>,
/// <c>unit_upgrade_type</c>, <c>build_status_successors</c>.</summary>
[Trait("Category", "Corpus")]
public class ExperienceHarnessTests(ITestOutputHelper output)
{
    // ---- модульные ------------------------------------------------------------------------------------

    /// <summary>run1 §5 (<c>f7-m9-xpstop</c>): волк 90 на четверых — по 22. <c>f7-m17</c>: 35 на четверых — 8. Первая
    /// большая партия, дуэль seq 375338: единственный участник получает все 25. Две смерти копятся отдельно (арифметика
    /// правила): ⌊25/2⌋ + ⌊35/2⌋ = 29, а не ⌊60/2⌋ = 30.</summary>
    [Fact]
    public void Accumulate_PerKill_M9_M17_Duel()
    {
        Assert.Equal(22, Experience.Accumulate([new Kill(90, ["a", "b", "c", "d"])])["d"]);
        Assert.Equal(8, Experience.Accumulate([new Kill(35, ["a", "b", "c", "d"])])["a"]);
        Assert.Equal(25, Experience.Accumulate([new Kill(25, ["S073UN01fa"])])["S073UN01fa"]);
        Assert.Equal(29, Experience.Accumulate([new Kill(25, ["a", "b"]), new Kill(35, ["a", "b"])])["b"]);
        Assert.Empty(Experience.Accumulate([new Kill(90, [])]));
    }

    /// <summary>Призванный в бою юнит (бит <c>st</c> 27) берёт долю, но в делитель не входит: ai-q37 seq 316125 — 5 на
    /// двоих и призыв, каждому по 2 (⌊5/2⌋), а не 1 (⌊5/3⌋); arena1 seq 124407 — 110 на двоих при трёх призывах, всем
    /// по 55. Одни призванные — делить не на кого.</summary>
    [Fact]
    public void Accumulate_SummonedTakeAShareButDoNotDivide_Q37_Arena1()
    {
        var xp = Experience.Accumulate([new Kill(5, ["a", "b", "s"], Summoned: 1)]);
        Assert.Equal([2, 2, 2], new[] { "a", "b", "s" }.Select(r => xp[r]));
        Assert.Equal(55, Experience.Accumulate([new Kill(110, ["a", "b", "s1", "s2", "s3"], Summoned: 3)])["s3"]);
        Assert.Empty(Experience.Accumulate([new Kill(10, ["s"], Summoned: 1)]));
    }

    /// <summary>run1 §5: Squire/Acolyte (наследник открыт) и Archer с <c>DYNLEVEL</c> при опыте 66 и пороге 80/70/80 —
    /// 14/4/14, герой (порог 150) — полные 22. Первая большая партия: герой 144/145 — строка <c>xp</c> 12, но
    /// <c>battle_end.xp</c> 1 (бой seq 281536); последний тир <c>UU0154</c> 1099/1100 — 9 (бой seq 611057); закрытый
    /// наследник 549/550 — <c>battle_end.xp</c> 0 (бой seq 281536).</summary>
    [Fact]
    public void Amount_DependsOnUpgradePath_M9AndBig1()
    {
        var open = Experience.UpgradePath.SuccessorOpen;
        Assert.Equal([14, 4, 14], new[] { 80, 70, 80 }.Select(next => Experience.Amount(22, 66, next, open)));
        Assert.Equal(22, Experience.Amount(22, 66, 150, Experience.UpgradePath.Leader));
        Assert.Equal(12, Experience.Amount(12, 144, 145, Experience.UpgradePath.Leader));
        Assert.Equal(1, Experience.BattleXp(12, 144, 145, Experience.UpgradePath.Leader));
        Assert.Equal(9, Experience.Amount(9, 1099, 1100, Experience.UpgradePath.LastTier));
        Assert.Equal(0, Experience.BattleXp(12, 549, 550, Experience.UpgradePath.SuccessorBlocked));
        Assert.Equal(1, Experience.Amount(12, 549, 550, Experience.UpgradePath.SuccessorBlocked));
        Assert.Equal(27, Experience.Amount(22, 0, 1000, open, weaponMasterPercent: 25));
        Assert.Equal(Experience.UpgradePath.SuccessorOpen, Experience.PathOf(0, dynLevel: true, successors: 1, successorBuilt: false));
        Assert.Equal(Experience.UpgradePath.SuccessorBlocked, Experience.PathOf(0, false, 2, false));
        Assert.Equal(Experience.UpgradePath.LastTier, Experience.PathOf(0, false, 0, false));
        Assert.Equal(Experience.UpgradePath.Leader, Experience.PathOf(2, false, 0, false));
        Assert.Equal(Experience.UpgradePath.LastTier, Experience.PathOf(0, false, 2, false, neutralOwner: true));
    }

    [Fact]
    public void Receives_SkipsDeadHiddenSummonsAndNobles_GuardianSharesWithoutGain()
    {
        Assert.True(Experience.Receives(0, alive: true));
        Assert.True(Experience.Receives(Experience.LeaderCategory, alive: true));
        Assert.False(Experience.Receives(0, alive: false));
        Assert.False(Experience.Receives(0, alive: true, hidden: true));
        Assert.All(new[] { 1, 4, 5 }, cat => Assert.False(Experience.Receives(cat, alive: true)));
        // Страж столицы делит, но опыта не получает (20260919-human, бои seq 15504360 и 15520354).
        Assert.True(Experience.Receives(8, alive: true));
        Assert.All(new[] { 1, 4, 5, 8 }, cat => Assert.False(Experience.GainsXp(cat)));
        Assert.True(Experience.GainsXp(0));
    }

    /// <summary>run3 §3.2–3.3, run4 §7.2: Squire → Knight при Stable, без построек упор в 79 и два здания; Archer с
    /// <c>DYNLEVEL</c> растёт на месте (<c>f7-m9</c>: <c>UU0006</c> ур. 2); наследник Knight — Imperial Knight.</summary>
    [SkippableFact]
    public void Upgrade_SuccessorBuildingAndDynLevel_FromTables()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var book = ProgressBook.Load(GamePaths.Globals);
        UpgradeOutcome Apply(UnitProgress p, params string[] built) => UnitUpgrade.Apply(
            p, book.XpNextOf, book.Rules.Units, book.UpgradeBuilding, built.ToHashSet(StringComparer.OrdinalIgnoreCase));
        IReadOnlyList<string> Buildings(string type, bool dyn) =>
            UnitUpgrade.SuccessorBuildings(book.Rules.Units, book.UpgradeBuilding, new UnitProgress(type, 1, 0, dyn));

        var knight = Apply(new UnitProgress("G000UU0001", 1, 80, false), "g000bb0001");
        Assert.Equal(("g000uu0002", 2, 0), (knight.Unit.TypeId, knight.Unit.Level, knight.Unit.Xp));
        var blocked = Apply(new UnitProgress("G000UU0001", 1, 95, false));
        Assert.Equal(79, blocked.Unit.Xp);
        Assert.Equal(0, Apply(new UnitProgress("G000UU0001", 1, 95, false), "g000bb0001").Unit.Xp);
        Assert.Equal(["g000bb0001", "g000bb0007"], blocked.MissingBuildings);
        var archer = Apply(new UnitProgress("G000UU0006", 1, 70, true));
        Assert.Equal(("G000UU0006", 2, 0, true), (archer.Unit.TypeId, archer.Unit.Level, archer.Unit.Xp, archer.Unit.DynLevel));
        Assert.Equal(["g000bb0010"], Buildings("G000UU0006", false));
        Assert.Equal(["g000bb0002"], Buildings("G000UU0002", false));
        Assert.Empty(Buildings("G000UU0006", true));
    }

    /// <summary>Опыт боя (<see cref="UnitUpgrade.ApplyBattle"/>, та же <c>0x5d7412</c>): у нейтрала закрытый наследник — прибавка не
    /// урезана, и опыт садится на сам порог без повышения (ai-q09 G000UU0006 27 + 96 → 70; карта 8 G000UU0036 0 + 157 → 130); цель есть —
    /// повышение как у <see cref="UnitUpgrade.Apply"/> (Squire 75 + 5 при Stable → Knight).</summary>
    [SkippableFact]
    public void ApplyBattle_NoUpgradeTarget_StopsOnTheThreshold()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var book = ProgressBook.Load(GamePaths.Globals);
        UnitProgress Battle(UnitProgress p, int amount, bool allowed, params string[] built) => UnitUpgrade.ApplyBattle(
            p, amount, book.XpNextOf, book.Rules.Units, book.UpgradeBuilding,
            built.ToHashSet(StringComparer.OrdinalIgnoreCase), allowed).Unit;

        Assert.Equal((1, 70), (Battle(new UnitProgress("G000UU0006", 1, 27, false), 96, true).Level,
                               Battle(new UnitProgress("G000UU0006", 1, 27, false), 96, true).Xp));
        Assert.Equal(("g000uu0002", 2, 0), (Battle(new UnitProgress("G000UU0001", 1, 75, false), 5, true, "g000bb0001").TypeId,
                                             Battle(new UnitProgress("G000UU0001", 1, 75, false), 5, true, "g000bb0001").Level,
                                             Battle(new UnitProgress("G000UU0001", 1, 75, false), 5, true, "g000bb0001").Xp));
        Assert.Equal(79, Battle(new UnitProgress("G000UU0001", 1, 75, false), 4, true).Xp);   // урезанная прибавка закрытого: порог не перейдён
        // Вход расы закрыт: ровно до порога — на 1 меньше (79), дальше — зажим в порог (80), повышения нет.
        Assert.Equal((1, 79), (Battle(new UnitProgress("G000UU0001", 1, 75, false), 5, false, "g000bb0001").Level,
                               Battle(new UnitProgress("G000UU0001", 1, 75, false), 5, false, "g000bb0001").Xp));
        Assert.Equal((1, 80), (Battle(new UnitProgress("G000UU0001", 1, 75, false), 10, false, "g000bb0001").Level,
                               Battle(new UnitProgress("G000UU0001", 1, 75, false), 10, false, "g000bb0001").Xp));
        // И230: нулевую прибавку upgradeGroup в 0x5d7412 не передаёт (0x665762) — опыт на пороге не снимается до XP_NEXT − 1;
        // прибавка — строки xp, не урезанная у нейтрала: replay-legions S073UN004a 59 + 13 → 70 (с применённой 11 вышло бы 69).
        Assert.Equal(70, Battle(new UnitProgress("G000UU0006", 1, 70, false), 0, true).Xp);
        Assert.Equal(70, Battle(new UnitProgress("G000UU0006", 1, 59, false), 13, true).Xp);
        Assert.Equal(69, Battle(new UnitProgress("G000UU0006", 1, 59, false), 11, true).Xp);
    }

    /// <summary>Дневной опыт лидера (И241): тип следующего уровня уже создан в этом запуске — прибавка без «−1», и лидер на пороге растёт
    /// (replay-legions, день 50: нейтральный G000UU5131 245 + 5 → 250 → уровень 2, цель G000UG911d); не создан — 249, повышения нет.</summary>
    [SkippableFact]
    public void ApplyDailyLeader_NextTierCreated_ReachesTheThresholdAndLevelsUp()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var book = ProgressBook.Load(GamePaths.Globals);
        var unit = new UnitProgress("G000UU5131", 1, 245, false);
        Assert.Equal(250, book.XpNextOf("G000UU5131", 1));
        var created = UnitUpgrade.ApplyDailyLeader(unit, 5, book.XpNextOf, 99, nextTierCreated: true);
        Assert.Equal((2, 0), (created.Level, created.Xp));
        var fresh = UnitUpgrade.ApplyDailyLeader(unit, 5, book.XpNextOf, 99);
        Assert.Equal((1, 249), (fresh.Level, fresh.Xp));
    }

    /// <summary>Дневной опыт ИИ (<c>0x5d7412</c> → <c>0x4318ad</c>), probe65 q09: <c>UU0018</c> (наследников нет, вход цели повышения
    /// закрыт — нейтральный хозяин при Империи со столицей, <see cref="UpgradeRace"/>) 456 + 19 → 474, 474 + 19 → 475 без повышения;
    /// при открытом входе — рост на месте; закрытый Squire садится на сам порог; открытый (Stable) — 79 + 1 → Knight, а при закрытом
    /// входе цели нет, и прибавка у порога на 1 меньше (79 + 1 → 79).</summary>
    [SkippableFact]
    public void ApplyDaily_WithoutUpgradeTarget_StopsOnTheThresholdWithoutLevelUp()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var book = ProgressBook.Load(GamePaths.Globals);
        UnitProgress Daily(UnitProgress p, int amount, bool allowed, params string[] built) => UnitUpgrade.ApplyDaily(
            p, amount, book.XpNextOf, book.Rules.Units, book.UpgradeBuilding,
            built.ToHashSet(StringComparer.OrdinalIgnoreCase), allowed).Unit;

        var day1 = Daily(new UnitProgress("G000UU0018", 1, 456, false), 19, allowed: false);
        Assert.Equal((1, 474), (day1.Level, day1.Xp));
        var day2 = Daily(day1, 19, allowed: false);
        Assert.Equal((1, 475), (day2.Level, day2.Xp));
        Assert.Equal((1, 475), (Daily(day2, 19, allowed: false).Level, Daily(day2, 19, allowed: false).Xp));
        Assert.Equal((2, 0), (Daily(day1, 19, allowed: true).Level, Daily(day1, 19, allowed: true).Xp));
        Assert.Equal(80, Daily(new UnitProgress("G000UU0001", 1, 75, false), 10, allowed: true).Xp);
        Assert.Equal("g000uu0002", Daily(new UnitProgress("G000UU0001", 1, 79, false), 1, true, "g000bb0001").TypeId);
        var closed = Daily(new UnitProgress("G000UU0001", 1, 79, false), 1, false, "g000bb0001");
        Assert.Equal(("G000UU0001", 79), (closed.TypeId, closed.Xp));
    }

    /// <summary>Павший (hp 0) — дневной опыт не прибавляется (<c>0x5d7412</c>), а проверка повышения <c>0x4318ad</c> идёт: на пороге
    /// с открытой целью — повышение с опытом 0 (карта 23: S143UN0082 G000UU0063 500 → G000UU0064 ур. 3 опыт 0); с закрытой — всё как
    /// было, без «на 1 меньше» (S143UN008b G000UU0038 1275 = XP_NEXT остаётся 1275); ниже порога — опыт тот же.</summary>
    [SkippableFact]
    public void ApplyDaily_DeadUnit_GetsNoXpButUpgradesOnTheThreshold()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var book = ProgressBook.Load(GamePaths.Globals);
        UnitProgress Dead(UnitProgress p, params string[] built) => UnitUpgrade.ApplyDaily(
            p, 19, book.XpNextOf, book.Rules.Units, book.UpgradeBuilding,
            built.ToHashSet(StringComparer.OrdinalIgnoreCase), raceAllows: true, alive: false).Unit;

        var open = Dead(new UnitProgress("G000UU0001", 1, 80, false), "g000bb0001");
        Assert.Equal(("g000uu0002", 0), (open.TypeId, open.Xp));
        var closed = Dead(new UnitProgress("G000UU0001", 1, 80, false));
        Assert.Equal(("G000UU0001", 1, 80), (closed.TypeId, closed.Level, closed.Xp));
        Assert.Equal(70, Dead(new UnitProgress("G000UU0001", 1, 70, false), "g000bb0001").Xp);
        var leader = UnitUpgrade.ApplyDailyLeader(new UnitProgress("G000UU0044", 1, 224, false), 9, book.XpNextOf, 8, alive: false);
        Assert.Equal((1, 224), (leader.Level, leader.Xp));
    }

    /// <summary>Вход цели повышения (<see cref="UpgradeRace.Allows"/>): раса без столицы — открыт всем; со столицей — только её игроку
    /// и группе без хозяина. Подрасы и столицы — как в <c>ai-q09</c> и <c>port-synth-5</c>.</summary>
    [Fact]
    public void UpgradeRace_OpensOnlyForOwnRaceOrRaceWithoutCapital()
    {
        var map = new MapState
        {
            Size = 1, Tiles = new Tile[1, 1], Turn = 1,
            Stacks = new Dictionary<string, MapStack>(), Units = new Dictionary<string, MapUnit>(),
            Players = new Dictionary<string, MapPlayer>(), Objects = new Dictionary<string, MapObject>(),
            Plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>(), Fog = new Dictionary<string, bool[,]>(),
            Subraces = new Dictionary<string, int> { ["S143SR0000"] = 5, ["S143SR0001"] = 1, ["S143SR0007"] = 8 },
            Sites = [new MapSite { Id = "S143FT0000", Kind = SiteKind.Capital, RawKind = "Capital", Owner = "S143PL0001" }],
        };
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["S143SR0000"] = "S143PL0000", ["S143SR0001"] = "S143PL0001", ["S143SR0007"] = "S143PL0000" };
        Assert.False(UpgradeRace.Allows(map, owners, "S143PL0000", 1));   // Империя со столицей, хозяин — нейтрал
        Assert.True(UpgradeRace.Allows(map, owners, "S143PL0001", 1));    // своя раса
        Assert.True(UpgradeRace.Allows(map, owners, "G000000000", 1));    // без хозяина
        Assert.True(UpgradeRace.Allows(map, owners, "S143PL0000", 8));    // нейтральная подраса — столицы нет
        Assert.True(UpgradeRace.Allows(map, owners, "S143PL0001", 3));    // подрасы на карте нет
    }

    // ---- харнесс ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void XpAward_UnitUpgrade_BuildStatus_MatchCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var book = ProgressBook.Load(GamePaths.Globals);
        var c = new Counters();
        var (unique, _) = JournalSet.Distinct(CorpusFiles.Journals());
        foreach (var path in unique)
        {
            if (!File.ReadLines(path).Any(l => l.Contains("\"ev\":\"xp\"", StringComparison.Ordinal))) continue;
            var j = TreasuryJournal.HasBank(path) ? TreasuryJournal.Load(path) : null;
            var audit = new XpAudit(book, c, j, j is { Saves.Count: > 0 } ? JournalSaves.Of(j) : []);
            foreach (var battle in JournalCache.Battles(path)) audit.Battle(battle);
            audit.Upgrades();
            audit.BuildStatus(path);
        }
        foreach (var (a, b) in LooseSaves.Pairs()) new XpAudit(book, c, null, []).LooseUpgrade(a, b);
        var (wmOk, wmBad) = c.Of("xp_weapon_master");
        c.Note($"   xp_weapon_master: наблюдений {wmOk + wmBad} (Инструктор у лидера победившей стороны)");
        c.Print(output, "опыт, смена типа, build_status (задача 4 Ф7)");
        // unit_upgrade_xp — гейт с 17.10: опыт боя без цели повышения садится на XP_NEXT (UnitUpgrade.ApplyBattle), 4 → 0.
        c.Require("xp_per_kill", "xp_award", "unit_upgrade_type", "unit_upgrade_xp", "build_status_successors");
    }
}

/// <summary>Разбор харнесса задачи 4 по одному журналу.</summary>
internal sealed partial class XpAudit(ProgressBook book, Counters c, TreasuryJournal? j, IReadOnlyList<SavePoint> points)
{
    private readonly IReadOnlyList<long> _jumps = j is null ? [] : Cheats.JumpSeqs(j);

    /// <summary><c>xp_per_kill</c> — накопление модели (<see cref="Experience.Accumulate"/>, у солдата урезанное
    /// <see cref="Experience.Amount"/>) против поля <c>xp</c> в <c>battle_end</c>; <c>xp_award</c> — строки <c>xp</c>.
    /// Вне сверки с причиной: бой, начатый с уже накопленным опытом (продолжение прерванного боя на аренах — смерти
    /// первой части в этот бой не входят), и бой с бегством (опыт за бегство не моделируется).</summary>
    public void Battle(D2.Data.Corpus.Battle battle)
    {
        _tiersBefore = [.. _seenTiers];
        foreach (var u in battle.Events.OfType<BattleUnitsEvent>().SelectMany(b => b.Units))
            if (u.Type is not null && book.TryGlobal(u.Type, Level(u), out var global, out int level))
                _seenTiers.Add((global.ToUpperInvariant(), level));
        var xp = battle.Events.OfType<XpEvent>().ToList();
        if (xp.Count == 0) return;
        if (battle.End is not { } end) { c.Skip("xp_award", "бой без battle_end"); return; }
        if (battle.Start.Units.Any(u => u.Xp != 0)) { c.Skip("xp_award", "бой начат с накопленным опытом — продолжение прерванного боя"); return; }
        if (Snapshots(battle).Any(units => units.Any(u => u.Retreating)))
        { c.Skip("xp_award", "в бою было бегство — опыт за бегство не моделируется"); return; }
        var identity = battle.Events.OfType<BattleUnitsEvent>().SelectMany(b => b.Units).Where(u => u.Type is not null)
                             .GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        // XP_KILLED убитого — исходной реализации (0x625a8e → 0x5ea4dd: у превращённого — origTypeId), то есть типа первой строки
        // battle_units: превращения кончаются с боем. Карта 17 [5062040]: Бес из Гнома S143UN000d — 35, не 10.
        var original = battle.Events.OfType<BattleUnitsEvent>().SelectMany(b => b.Units).Where(u => u.Type is not null)
                             .GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (Kills(battle, identity, original, out string why) is not { } kills) { c.Skip("xp_award", why); return; }
        var accumulated = Experience.Accumulate(kills);
        var a = end.T is { } t && j is not null ? JournalSaves.Before(points, battle.Start.Seq, j.EpochOf(t)) : null;
        _after = end.T is { } t2 && j is not null ? JournalSaves.After(points, battle.Start.Seq, j.EpochOf(t2)) : null;
        // Превращённый в бою юнит (разные типы в строках battle_units) делит опыт под чужим типом — вне сверки.
        _transformed = battle.Events.OfType<BattleUnitsEvent>().SelectMany(b => b.Units).Where(u => u.Type is not null)
            .GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(u => u.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var u in end.Units.Where(u => !_transformed.Contains(u.Id)))
        {
            var side = SideOf(end.Units, u).ToList();
            // battle_end.xp уже с Инструктором (ai-q18, 20260917-synth: 19 → 23) — как в Award.
            int wm = a is not null && side.Any(id => HasWeaponMaster(a.Save, id)) ? book.Vars.WeaponMaster : 0;
            PerKill(battle, a, u, accumulated, identity, SideBoost(a ?? _after, side), wm);
        }
        foreach (var _ in _transformed) c.Skip("xp_award", "юнит превращён в бою (TransformOther/Self) — опыт под чужим типом");
        Award(battle, end, xp, accumulated, identity);
    }

    private SavePoint? _after;
    private HashSet<string> _transformed = [];

    /// <summary>Базовый тип и уровень всех юнитов, виденных в <c>battle_units</c> этого журнала (один журнал — один
    /// запуск игры): порождённые типы движок держит на весь запуск.</summary>
    private readonly HashSet<(string Type, int Level)> _seenTiers = [];
    private HashSet<(string Type, int Level)> _tiersBefore = [];

    /// <summary>Создан ли уже тип следующего уровня лидера (<see cref="Experience.LeaderAmount"/>): юнит того же
    /// базового типа уровнем выше виден до этого боя — в <c>battle_units</c> журнала или в сохранении до боя.
    /// Ранг 2 (T08: «герой один своего типа»). <c>ponytail:</c> тип, созданный повышением другого юнита в этом же
    /// бою (<c>20260909-arena2</c> seq 257837), не виден — нужен порядок строк <c>xp</c> внутри боя.
    /// <para>Созданным считается только тип РОВНО на уровень выше (ранг 3: движок порождает реализацию уровня при
    /// повышении до него, а юнит, загруженный с карты уровнем выше, — открытый вопрос). Было <c>lvl &gt; level</c>:
    /// ai-t08c бой seq 117145 (<c>xp_award</c> 124936) — лорд <c>S135UN0148</c> <c>G000UU0019</c> ур. 3 из сохранения
    /// 047 засчитал созданным ур. 2 → 3 для героя ур. 1, а движок дал ловушку.</para></summary>
    private bool NextTierCreated(SavePoint? a, UnitIdentity who)
    {
        if (!book.TryGlobal(who.Type!, Level(who), out var global, out int level)) return false;
        bool Higher(string type, int lvl) => Same(type, global) && lvl == level + 1;
        return _tiersBefore.Any(t => Higher(t.Type, t.Level))
               || (a is not null && a.Save.Map.Units.Values.Any(u => !Same(u.Id, who.Id) && Higher(u.TypeId, u.Level)));
    }

    /// <summary>Надбавки группы (<see cref="Experience.GroupAmount"/>) по сохранению до боя (нет его — после, в той же
    /// загрузке карты: сложность и «человек или ИИ» внутри загрузки не меняются): владелец группы — по
    /// первому юниту стороны, найденному в сохранении; человек на лёгкой — +20 %, ИИ играбельной расы — множитель
    /// <see cref="Experience.AiXpPercent"/> по <c>DIFFGAME</c> и <c>SUGG_LVL</c>. null — сохранения нет или владелец не
    /// опознан; тогда надбавок нет, как до переноса сложности.</summary>
    private XpBoost? SideBoost(SavePoint? a, IEnumerable<string> side)
    {
        if (a is null) return null;
        foreach (var id in side)
        {
            if (OwnerOf(a.Save, id) is not { } owner || !a.Save.Map.Players.TryGetValue(owner, out var p)) continue;
            if (p.IsHuman) return new XpBoost(a.Save.Difficulty == Difficulty.Easy, 100);
            bool playable = book.Prices.Economy.Races.TryGetValue(p.RaceId, out var race) && race.Playable;
            return new XpBoost(false, playable ? Experience.AiXpPercent(a.Save.Difficulty, a.Save.SuggestedLevel) : 100);
        }
        return null;
    }

    private static IEnumerable<string> SideOf(IEnumerable<UnitState> units, UnitState u) =>
        units.Where(x => IsAttacker(x) == IsAttacker(u)).Select(x => x.Id);

    private void PerKill(D2.Data.Corpus.Battle battle, SavePoint? a, UnitState u, IReadOnlyDictionary<string, int> accumulated,
                         Dictionary<string, UnitIdentity> identity, XpBoost? boost, int wm)
    {
        int raw = u.Dead ? 0 : accumulated.GetValueOrDefault(u.Id);
        int acc = raw > 0 && wm > 0 ? Experience.GroupAmount(raw, wm, XpBoost.None) : raw;
        if (acc == 0 && u.Xp == 0) return;
        string detail = $"бой seq {battle.Start.Seq} {u.Id}: battle_end.xp {u.Xp}, накоплено моделью {acc}";
        if (u.Dead || !identity.TryGetValue(u.Id, out var who)) { c.Count("xp_per_kill", u.Xp == acc, detail); return; }
        if (book.CategoryOf(who.Type!) is { } cat && !Experience.GainsXp(cat))
        { c.Count("xp_per_kill", u.Xp == 0, $"{detail}, категория {cat} опыта не получает: ждали 0"); return; }
        if (PathIn(a, who) is { } path && XpBefore(a, who, battle.Start.Seq) is { } before)
        {
            int expected = Experience.BattleXp(acc, before, book.XpNextOf(who.Type!, Level(who)), path, boost,
                                               path == Experience.UpgradePath.Leader && NextTierCreated(a, who));
            c.Count("xp_per_kill", u.Xp == expected, $"{detail}, {path}: модель {expected} (опыт до боя {before})");
        }
        else if (u.Xp == acc) c.Count("xp_per_kill · без снимка, без урезания", true, detail);
        else c.Skip("xp_per_kill", "накопление урезано, а опыта до боя в сохранениях нет");
    }

    /// <summary>Путь повышения юнита (<see cref="Experience.PathOf"/>). Лидер и последний тир — по таблицам; открыт ли
    /// наследник — по постройкам и <c>DYNLEVEL</c> из сохранения до боя (null — сохранения нет).</summary>
    private Experience.UpgradePath? PathIn(SavePoint? a, UnitIdentity who)
    {
        if (book.CategoryOf(who.Type!) is not { } category || !book.TryGlobal(who.Type!, Level(who), out var global, out _)) return null;
        var successors = UnitUpgrade.Successors(book.Rules.Units, global);
        if (category == Experience.LeaderCategory || successors.Count == 0) return Experience.PathOf(category, false, successors.Count, false);
        if (a is null || !a.Save.Map.Units.TryGetValue(who.Id, out var unit) || OwnerOf(a.Save, who.Id) is not { } owner) return null;
        // У нейтрала путь от построек не зависит (Experience.PathOf, IsUnitTierMax 0x5d8d58).
        if (a.Save.Map.Players.TryGetValue(owner, out var player)
            && book.Prices.Economy.Races.TryGetValue(player.RaceId, out var race) && race.RaceType == NeutralRaceType)
            return Experience.PathOf(category, unit.DynLevel, successors.Count, false, neutralOwner: true);
        // Постройка наследника, появившаяся между сохранениями, могла открыть путь до боя — момент неизвестен.
        if (_after is null || Built(a.Save, _after.Save, unit) is not { } built) return null;
        bool open = successors.Any(s => book.UpgradeBuilding.GetValueOrDefault(s.Id, "") is var b && (b.Length == 0 || built.Contains(b)));
        return Experience.PathOf(category, unit.DynLevel, successors.Count, open);
    }

    /// <summary>Действия, убившие юнитов, по снимкам (<c>battle_start</c>, <c>round</c>, <c>turn_*</c>, <c>battle_end</c>):
    /// юниты одной стороны, впервые помеченные мёртвыми в одном снимке, убиты одним действием — их <c>XP_KILLED</c>
    /// складываются и делятся между делящими противоположной стороны в том же снимке.
    /// <para>Умерший без удара (яд, мороз, ожог в начале своего хода) лежит со <c>st</c> = 2 — Dead без XpCounted: опыт
    /// за него движок раздаёт следующей раздачей, в конце следующего действия. Если то действие убило юнитов той же
    /// стороны, его <c>XP_KILLED</c> входит в их сумму и делится одним делением. 20261014-replay (S135, ai-q57) бой seq
    /// 2669822: S135UN0111 (G000UU0009 ур. 2, XP_KILLED 55) умер от тика DoT S135UN0145 (turn_after 2670029, st 2),
    /// следом S135UN0144 убил S135UN0118 (20) — у троих победителей 25 = ⌊75/3⌋ (turn_after 2670074), а не
    /// ⌊55/3⌋ + ⌊20/3⌋ = 24; итог <c>xp_group</c> acc 51, а не 50. Иначе — своей раздачей (все такие смерти стороны — одной суммой, делящие — снимка последней), как
    /// раньше: в корпусе ещё 11 таких смертей (probe65 seq 3950575, human 2868863, legions 902595…), сосчитанных
    /// следующим снимком без других смертей своей стороны, — делящие у них те же.</para></summary>
    private List<Kill>? Kills(D2.Data.Corpus.Battle battle, Dictionary<string, UnitIdentity> identity,
                              Dictionary<string, UnitIdentity> original, out string why)
    {
        why = "";
        var kills = new List<Kill>();
        var dead = battle.Start.Units.Where(u => u.Dead).Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Мёртвые, чей опыт ещё не сосчитан (бит 1 без бита 0 XpCounted), в прошлом снимке — см. Experience.Receives.
        var uncounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Умершие без удара, чей опыт ещё не роздан: сторона, свой XP_KILLED и раздача по снимку смерти.
        var pending = new Dictionary<string, (bool Attacker, int Xp, Kill AtDeath)>(StringComparer.OrdinalIgnoreCase);
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // порядок смертей без удара
        int deaths = 0;
        foreach (var units in Snapshots(battle))
        {
            // Воскрешённый (зелье, seq 13263789 probe65) снова живой — его вторая смерть снова даёт XP_KILLED:
            // бой seq 13263066, UU0062 убит дважды, строка xp 123 = 103 + 20.
            foreach (var u in units.Where(u => !u.Dead)) { dead.Remove(u.Id); pending.Remove(u.Id); }
            var counted = units.Where(u => pending.ContainsKey(u.Id) && Counted(u)).Select(u => u.Id).ToList();
            foreach (var side in units.Where(u => u.Dead && dead.Add(u.Id)).ToList().GroupBy(IsAttacker))
            {
                var xp = new List<int>();
                foreach (var u in side)
                {
                    if (!original.TryGetValue(u.Id, out var who) || book.Grown(who.Type!, Level(who)) is not { } grown)
                    { why = $"убитый {u.Id} без типа в battle_units"; return null; }
                    xp.Add(grown.Type.XpKilled);
                }
                var receivers = units.Where(r => IsAttacker(r) != side.Key && Shares(r, identity, uncounted.Contains(r.Id))).ToList();
                var kill = new Kill(xp.Sum(), receivers.Select(r => r.Id).ToList(),
                                    receivers.Count(r => (r.St & (1L << Experience.SummonStatusBit)) != 0));
                if (side.All(u => !Counted(u)))
                {
                    foreach (var (u, x) in side.Zip(xp)) { pending[u.Id] = (side.Key, x, kill); order[u.Id] = deaths++; }
                    continue;
                }
                var merged = counted.Where(id => pending[id].Attacker == side.Key).ToList();
                kills.Add(kill with { XpKilled = kill.XpKilled + merged.Sum(id => pending[id].Xp) });
                foreach (var id in merged) { pending.Remove(id); counted.Remove(id); }
            }
            kills.AddRange(Alone(counted));
            foreach (var id in counted) pending.Remove(id);
            uncounted = units.Where(u => u.Dead && !Counted(u)).Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        kills.AddRange(Alone([.. pending.Keys]));
        return kills;

        // Раздача без других смертей своей стороны: все умершие от тика одной стороны, чей опыт роздан одной раздачей, — одной суммой
        // (как у порта, BattleAftermath.Kills: все тики до действия — в его раздачу), делящие — снимка последней смерти. 20261020-faces бой
        // seq 538752: S143UN0025 и S143UN0024 (G000UU8015 ур. 2, XP_KILLED 88) умерли от яда в начале своих ходов (снимки 543794 и
        // 543797), опыт роздан следующим действием — у пятерых 35 = ⌊176/5⌋, а не ⌊88/5⌋ + ⌊88/5⌋ = 34 (battle_end.xp 63 = (18 + 35) · 1,2).
        IEnumerable<Kill> Alone(List<string> ids) => ids.GroupBy(id => pending[id].Attacker)
            .Select(g => pending[g.MaxBy(id => order[id])!].AtDeath with { XpKilled = g.Sum(id => pending[id].Xp) });
    }

    private static bool Counted(UnitState u) => (u.St & (1L << Experience.XpCountedStatusBit)) != 0;

    /// <summary>Снимки юнитов боя по порядку <c>seq</c>: <c>battle_start</c> (и его копии), <c>round</c>, <c>turn_*</c>,
    /// <c>battle_end</c>.</summary>
    private static IEnumerable<IReadOnlyList<UnitState>> Snapshots(D2.Data.Corpus.Battle battle) =>
        battle.Events.Select(e => e switch
        {
            BattleStartEvent s => s.Units, RoundEvent r => r.Units, TurnEvent t => t.Units, BattleEndEvent b => b.Units, _ => null,
        }).OfType<IReadOnlyList<UnitState>>();

    /// <param name="uncountedBefore">В снимке до действия юнит уже мёртв, но его опыт не сосчитан (яд, ожог).</param>
    private bool Shares(UnitState u, Dictionary<string, UnitIdentity> identity, bool uncountedBefore) =>
        identity.TryGetValue(u.Id, out var who) && book.CategoryOf(who.Type!) is { } category
        && Experience.Receives(category, !u.Dead || uncountedBefore, (u.St & (1L << Experience.HiddenStatusBit)) != 0);

    private void Award(D2.Data.Corpus.Battle battle, BattleEndEvent end, List<XpEvent> xp,
                       IReadOnlyDictionary<string, int> accumulated, Dictionary<string, UnitIdentity> identity)
    {
        bool? attackersWon = Same(end.Winner, battle.Start.AttackerGroup) ? true : Same(end.Winner, battle.Start.DefenderGroup) ? false : null;
        if (attackersWon is null) { c.Skip("xp_award", "победитель — не группа боя"); return; }
        var got = xp.GroupBy(x => x.Unit, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(x => (int)x.Amount), StringComparer.OrdinalIgnoreCase);
        var winners = end.Units.Where(u => IsAttacker(u) == attackersWon && !u.Dead && identity.ContainsKey(u.Id)).ToList();
        var a = end.T is { } t && j is not null ? JournalSaves.Before(points, battle.Start.Seq, j.EpochOf(t)) : null;
        bool master = a is not null && winners.Any(w => HasWeaponMaster(a.Save, w.Id));
        var boost = SideBoost(a ?? _after, winners.Select(w => w.Id));
        foreach (var w in winners.Where(w => !_transformed.Contains(w.Id)))
        {
            int acc = accumulated.GetValueOrDefault(w.Id), amount = got.GetValueOrDefault(w.Id);
            if (acc == 0 && !got.ContainsKey(w.Id)) continue;
            if (book.CategoryOf(identity[w.Id].Type!) is { } cat && !Experience.GainsXp(cat))
            {
                c.Count("xp_award", !got.ContainsKey(w.Id),
                        $"бой seq {battle.Start.Seq}; {w.Id} категории {cat}: накоплено {acc}, строка xp {amount} — ждали её отсутствия");
                continue;
            }
            AwardOne(battle, a, identity[w.Id], acc, amount, master, boost);
        }
        foreach (var extra in got.Keys.Where(k => !winners.Any(w => Same(w.Id, k))))
            c.Count("xp_award", false, $"бой seq {battle.Start.Seq}: опыт {got[extra]} получил {extra}, а он не выживший победитель");
    }

    private void AwardOne(D2.Data.Corpus.Battle battle, SavePoint? a, UnitIdentity who, int acc, int amount, bool master,
                          XpBoost? boost)
    {
        var path = PathIn(a, who);
        int next = book.XpNextOf(who.Type!, Level(who)), wm = master ? book.Vars.WeaponMaster : 0;
        string detail = $"бой seq {battle.Start.Seq}; {who.Id} {who.Type} ур. {who.Level} {path}: накоплено {acc}, журнал {amount}";
        // Последнему тиру опыт до боя не нужен — строка xp у него не урезается; лидеру — только пока тип его следующего
        // уровня не создан (Experience.LeaderAmount).
        bool created = path == Experience.UpgradePath.Leader && NextTierCreated(a, who);
        int? before = path is Experience.UpgradePath.LastTier || (path == Experience.UpgradePath.Leader && !created)
            ? 0 : XpBefore(a, who, battle.Start.Seq);
        if (path is { } p && before is { } xpBefore)
        {
            int expected = Experience.Amount(acc, xpBefore, next, p, wm, boost, created);
            // Строка xp лидера — переход уровня лидера, задача 11: печатается отдельно и в приёмку задачи 4 не входит
            // (корпус показал у двух лидеров урезание, которого правило задачи 4 не объясняет).
            string row = p == Experience.UpgradePath.Leader ? "xp_award · лидер (задача 11)" : "xp_award";
            c.Count(row, amount == expected, $"{detail}, модель {expected} (опыт до боя {xpBefore}, порог {next})");
            if (master) c.Count("xp_weapon_master", amount == expected, detail);
            if (p == Experience.UpgradePath.Leader) LeaderHypotheses(battle, a, who, acc, amount, next, wm, detail, boost);
            return;
        }
        if (amount == Experience.Amount(acc, 0, 0, Experience.UpgradePath.LastTier, wm)) c.Count("xp_award · без снимка, без урезания", true, detail);
        else c.Skip("xp_award", "опыт урезан, а опыта до боя в сохранениях нет");
    }

    /// <summary>Строка <c>xp</c> лидера с опытом до боя из сохранения — две гипотезы рядом: «= накоплено» и
    /// «min(накоплено, порог − опыт)». Печатаются для разбора (переход уровня лидера — задача 11), в приёмку задачи 4 не
    /// входят.</summary>
    private void LeaderHypotheses(D2.Data.Corpus.Battle battle, SavePoint? a, UnitIdentity who, int acc, int amount, int next,
                                  int wm, string detail, XpBoost? boost)
    {
        if (XpBefore(a, who, battle.Start.Seq) is not { } real)
        { c.Skip("xp_award · лидер", "опыта лидера до боя в сохранениях нет (или уровень менялся)"); return; }
        var stack = a!.Save.Map.Stacks.Values.FirstOrDefault(s => s.UnitIds.Contains(who.Id, StringComparer.OrdinalIgnoreCase));
        string more = $"{detail}; опыт до боя {real}, порог {next}, UPGCOUNT {stack?.UpgradeCount}, MAX_LEADER {a.Save.Map.Limits?.MaxLeader}";
        c.Count("xp_award · лидер · гипотеза «= накоплено»",
                amount == Experience.Amount(acc, real, next, Experience.UpgradePath.Leader, wm, boost), more);
        c.Count("xp_award · лидер · гипотеза «min(·, порог − опыт)»",
                amount == Experience.Amount(acc, real, next, Experience.UpgradePath.SuccessorOpen, wm, boost), more);
    }

    /// <summary>Опыт юнита к началу боя: сохранение до и строки <c>xp</c> между его срезом и боем. null — снимка нет,
    /// уровень менялся или между ними обучение.</summary>
    private int? XpBefore(SavePoint? a, UnitIdentity who, long battleSeq)
    {
        if (a is null || j is null || !a.Save.Map.Units.TryGetValue(who.Id, out var unit) || Cheats.Between(_jumps, a.Cut, battleSeq)) return null;
        if (!book.TryGlobal(who.Type!, Level(who), out _, out int level) || level != unit.Level) return null;
        int xp = unit.Xp;
        foreach (var e in JournalSaves.Between(j, a.Cut, battleSeq))
        {
            if (e is SiteTrainEvent tr && Same(tr.Unit, who.Id)) return null;
            // Опыт после строки — её xp_after (у порога прибавка на 1 меньше и зажим, 0x5d7412: amount ≠ приросту; probe72 UN001f 66, а не 67).
            if (e is XpEvent x && Same(x.Unit, who.Id)) xp = x.XpAfter is { } after ? (int)after : xp + (int)x.Amount;
        }
        return xp < book.XpNextOf(unit.TypeId, unit.Level) ? xp : null;
    }

    private bool HasWeaponMaster(SaveAt save, string unitId) =>
        save.Map.Units.TryGetValue(unitId, out var u) && book.View(u, out _) is { } v
        && v.LeaderAbilities.Contains(Experience.WeaponMasterAbility);

    /// <summary><c>UnitFlags</c> бит 3 — юнит атакующей стороны (так же читает <c>CorpusBattleLoader</c>).</summary>
    private static bool IsAttacker(UnitState u) => (u.Flags & 0b1000) != 0;

    private static int Level(UnitIdentity who) => (int)(who.Level ?? 1);

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Смена типа и <c>build_status</c> (задача 4).</summary>
internal sealed partial class XpAudit
{
    /// <summary><c>LunitC.L_GUARDIAN</c> = 8 — страж столицы: <c>UU3002</c> второй большой партии с опытом 8999 из 9000
    /// получил строку <c>xp</c> 360, а в сохранении после — 9000 и прежний уровень. Ранг 2 (одно наблюдение).</summary>
    private const int GuardianCategory = 8;
    /// <summary><c>Grace.RACE_TYPE</c> нейтралов (<c>LRaceCategory</c> 4).</summary>
    private const int NeutralRaceType = 4;

    /// <summary>Строки <c>build_status</c> обучения идут сразу за <c>site_train</c> (прогон 4, вторая большая партия);
    /// 8 <c>seq</c> — запас, следующее обучение окно обрезает. Наблюдение по корпусу.</summary>
    private const long BuildStatusWindowSeq = 8;

    /// <summary><c>unit_upgrade_type</c> по журналу: юниты с опытом или обучением между соседними сохранениями.</summary>
    public void Upgrades()
    {
        if (j is null) return;
        foreach (var (a, b) in JournalSaves.Pairs(points))
        {
            var window = JournalSaves.Between(j, a.Cut, b.Cut);
            var touched = window.OfType<XpEvent>().Select(x => x.Unit)
                                .Concat(window.OfType<SiteTrainEvent>().Where(t => t.Ret).Select(t => t.Unit))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (Cheats.Between(_jumps, a.Cut, b.Cut))
            { foreach (var _ in touched) c.Skip("unit_upgrade_type", "в окне набран чит jump"); continue; }
            foreach (var id in touched) Upgrade(a.Save, b.Save, id, window);
        }
    }

    private void Upgrade(SaveAt a, SaveAt b, string id, List<CorpusEvent> window)
    {
        if (!a.Map.Units.TryGetValue(id, out var ua) || !b.Map.Units.TryGetValue(id, out var ub))
        { c.Skip("unit_upgrade_type", "юнита нет в одном из сохранений (нанят, призван или погиб насовсем)"); return; }
        if (book.CategoryOf(ua.TypeId) == Experience.LeaderCategory) { c.Skip("unit_upgrade_type", "лидер — переход уровня лидера в задаче 11"); return; }
        if (book.CategoryOf(ua.TypeId) == GuardianCategory) { c.Skip("unit_upgrade_type", "страж столицы (UNIT_CAT 8) уровней не берёт"); return; }
        if (ua.DynLevel != ub.DynLevel) { c.Skip("unit_upgrade_type", "DYNLEVEL переключён внутри окна"); return; }
        if (Built(a, b, ua) is not { } built) { c.Skip("unit_upgrade_type", "постройки наследников менялись между сохранениями или нет владельца"); return; }
        var start = new UnitProgress(ua.TypeId, ua.Level, ua.Xp, ua.DynLevel);
        var model = UnitLedger.Run(book, j!, start, id, window, built, out var refused, a);
        var saved = new UnitProgress(ub.TypeId, ub.Level, ub.Xp, ub.DynLevel);
        string detail = $"{Path.GetFileName(a.Path)} → {Path.GetFileName(b.Path)} {id}: было {UnitLedger.Show(start)}, " +
                        $"модель {UnitLedger.Show(model)}{(refused is null ? "" : $" ({refused})")}, сохранение {UnitLedger.Show(saved)}";
        bool same = refused is null && SameType(model, saved);
        c.Count("unit_upgrade_type", same, detail);
        c.Count(SameType(model, start) ? "unit_upgrade_type · без повышения" : "unit_upgrade_type · повышение", same, detail);
        c.Count("unit_upgrade_xp", refused is null && model.Xp == saved.Xp, detail);
        bool daily = window.OfType<XpEvent>().Any(x => Same(x.Unit, id) && JournalSaves.IsDailyXp(window, x));
        c.Count(daily ? "unit_upgrade_xp · окно с ежедневной строкой xp" : "unit_upgrade_xp · окно без ежедневных строк xp",
                refused is null && model.Xp == saved.Xp, detail);
    }

    /// <summary>Без журнала (<see cref="LooseSaves"/>): юнит поднялся ровно на уровень — опыт принимается равным
    /// порогу, и модель выбирает, во что он вырос (<c>f7-m9-xpstop</c>: Knight, Priest и Archer с <c>DYNLEVEL</c>).</summary>
    public void LooseUpgrade(SaveAt a, SaveAt b)
    {
        foreach (var ua in a.Map.Units.Values)
        {
            if (!b.Map.Units.TryGetValue(ua.Id, out var ub) || (Same(ua.TypeId, ub.TypeId) && ua.Level == ub.Level)) continue;
            if (book.CategoryOf(ua.TypeId) is Experience.LeaderCategory or null) { c.Skip("unit_upgrade_type", "лидер или неизвестный тип (без журнала)"); continue; }
            if (ua.DynLevel != ub.DynLevel || Built(a, b, ua) is not { } built)
            { c.Skip("unit_upgrade_type", "DYNLEVEL или постройки менялись (без журнала)"); continue; }
            var start = new UnitProgress(ua.TypeId, ua.Level, book.XpNextOf(ua.TypeId, ua.Level), ua.DynLevel);
            var model = UnitUpgrade.Apply(start, book.XpNextOf, book.Rules.Units, book.UpgradeBuilding, built).Unit;
            var saved = new UnitProgress(ub.TypeId, ub.Level, ub.Xp, ub.DynLevel);
            c.Count("unit_upgrade_type", SameType(model, saved),
                    $"{Path.GetFileName(a.Path)} → {Path.GetFileName(b.Path)} {ua.Id} (без журнала, опыт = порогу): " +
                    $"было {UnitLedger.Show(start)}, модель {UnitLedger.Show(model)}, сохранение {UnitLedger.Show(saved)}");
        }
    }

    /// <summary><c>build_status_successors</c>: после обучения юнита, вставшего на <c>XP_NEXT − 1</c>, строки
    /// <c>build_status</c> (<c>ignore_cost</c>) называют постройки наследников — по одной на наследника; у юнита не на
    /// потолке строк нет. Событие пишется по ключу, поэтому постройка, чей ключ уже был в журнале раньше, может
    /// не повториться (печатается).</summary>
    public void BuildStatus(string path)
    {
        if (j is null) return;
        var trains = j.Events.OfType<SiteTrainEvent>().Where(t => t.Ret).OrderBy(t => t.Seq).ToList();
        if (trains.Count == 0) return;
        if (points.Count == 0) { foreach (var _ in trains) c.Skip("build_status_successors", "у журнала нет сохранений в манифесте"); return; }
        var statuses = JournalCache.Read(path, "build_status").OfType<BuildStatusEvent>().Where(s => s.IgnoreCost).OrderBy(s => s.Seq).ToList();
        for (int i = 0; i < trains.Count; i++)
            Successors(trains[i], i + 1 < trains.Count ? trains[i + 1].Seq : long.MaxValue, statuses);
    }

    private void Successors(SiteTrainEvent train, long nextTrain, List<BuildStatusEvent> statuses)
    {
        var a = JournalSaves.Before(points, train.Seq, j!.EpochOf(train.T));
        if (a is null || !a.Save.Map.Units.TryGetValue(train.Unit, out var ua) || OwnerOf(a.Save, train.Unit) is not { } owner)
        { c.Skip("build_status_successors", "юнита нет в сохранении до обучения"); return; }
        if (Cheats.Between(_jumps, a.Cut, train.Seq)) { c.Skip("build_status_successors", "между сохранением и обучением набран чит jump"); return; }
        // Строки build_status с ignore_cost пишет только проверка постройки ИИ (0x446e37 → 0x5dc9e8 с IgnoresCost, AiEconomy) — у
        // человека их нет по определению: 20261016e-av, карта 6 — человек PL0004 учит S143UN0031/0032 до XP_NEXT − 1, строк 0.
        if (a.Save.Map.Players[owner].IsHuman)
        { c.Skip("build_status_successors", "хозяин — человек: строки build_status с ignore_cost пишет только ИИ"); return; }
        var built = a.Save.Map.Players[owner].Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var events = JournalSaves.Between(j, a.Cut, train.Seq + 1);
        var p = UnitLedger.Run(book, j, new UnitProgress(ua.TypeId, ua.Level, ua.Xp, ua.DynLevel), train.Unit, events, built, out var refused, a.Save);
        if (refused is not null) { c.Count("build_status_successors", false, $"{train.Unit}: {refused}"); return; }
        bool atCap = p.Xp == book.XpNextOf(p.TypeId, p.Level) - 1;
        // Постройка наследника, уже стоящая у хозяина, строки не даёт: строить нечего (карта 23, 20261020-port-synth: S143UN005f G000UU0052
        // на 94 после тренера seq 2713477, G000BB0050 у PL0003 с seq 2006317 — строк нет, а следующий день 94 + 5 → повышение в G000UU0053).
        // Ранг 2: выбор постройки 0x446ccb, проверка 0x446e37 → 0x5dc9e8 — только у выбранной, выбор уже стоящей не берёт.
        var expected = atCap ? UnitUpgrade.SuccessorBuildings(book.Rules.Units, book.UpgradeBuilding, p).Where(b => !built.Contains(b)).ToList() : [];
        var observed = statuses.Where(s => s.Seq > train.Seq && s.Seq < Math.Min(train.Seq + BuildStatusWindowSeq, nextTrain) && Same(s.Player, owner))
                               .Select(s => s.Building).ToList();
        var keyed = expected.Where(e => !observed.Contains(e, StringComparer.OrdinalIgnoreCase)
                                        && statuses.Any(s => s.Seq < train.Seq && Same(s.Player, owner) && Same(s.Building, e))).ToList();
        bool ok = observed.All(o => expected.Contains(o, StringComparer.OrdinalIgnoreCase))
                  && expected.All(e => observed.Contains(e, StringComparer.OrdinalIgnoreCase) || keyed.Contains(e));
        string detail = $"{train.Unit} {UnitLedger.Show(p)}{(atCap ? ", на потолке" : "")}: наследники [{string.Join(", ", expected)}], " +
                        $"строки [{string.Join(", ", observed)}]{(keyed.Count > 0 ? $", ключ уже был [{string.Join(", ", keyed)}]" : "")}";
        c.Count("build_status_successors", ok, detail);
        c.Note($"   build_status_successors {(ok ? "сошлось" : "разошлось")}: {detail}");
    }

    private IReadOnlySet<string>? Built(SaveAt a, SaveAt b, MapUnit unit)
    {
        if (OwnerOf(a, unit.Id) is not { } owner || !a.Map.Players.TryGetValue(owner, out var pa)) return null;
        var before = pa.Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = b.Map.Players.TryGetValue(owner, out var pb) ? pb.Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase) : before;
        var relevant = UnitUpgrade.Successors(book.Rules.Units, unit.TypeId)
                                  .Select(s => book.UpgradeBuilding.GetValueOrDefault(s.Id, "")).Where(x => x.Length > 0);
        return relevant.All(x => before.Contains(x) == after.Contains(x)) ? before : null;
    }

    private static string? OwnerOf(SaveAt s, string unitId) =>
        s.Map.Stacks.Values.FirstOrDefault(x => x.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner
        ?? s.Sites.FirstOrDefault(x => x.Guards.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner;

    private static bool SameType(UnitProgress x, UnitProgress y) =>
        Same(x.TypeId, y.TypeId) && x.Level == y.Level && x.DynLevel == y.DynLevel;
}
