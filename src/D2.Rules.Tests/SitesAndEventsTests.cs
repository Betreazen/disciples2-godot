using D2.Data;
using D2.Data.Corpus;
using D2.Data.Battle;
using D2.Data.Map;
using D2.Rules.Battle;
using D2.Rules.Map;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Объекты карты и события сценария — подмножество контрольной карты С4 (задача 8 плана Ф5).
/// Проверяется троекратно: на синтетике (каждое правило отдельно), на настоящих <c>.sg</c> из
/// <c>Exports</c> (разбор всех карт, ссылки условий на области) и по журналу зонда
/// (<see cref="Sites_VisitedObjectsMatchJournal"/> — включится, когда журнал появится).
///
/// <para>Вне подмножества всё бросает <see cref="NotSupportedException"/>, и это проверяется
/// отдельными тестами: молчаливая заглушка выглядела бы в сверке С4 как ошибка формулы.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class SitesAndEventsTests
{
    private readonly ITestOutputHelper _out;

    public SitesAndEventsTests(ITestOutputHelper output) => _out = output;

    private const string Player = "S143PL0001";
    private const string Enemy = "S143PL0002";

    // ---------------------------------------------------------------- посещение объектов

    [Fact]
    public void Visit_RuinGivesCashAndItemOnceAndNothingAfter()
    {
        var ruin = new MapSite
        {
            Id = "S143RU0001", Kind = SiteKind.Ruin, RawKind = "MidRuin", X = 5, Y = 5,
            Cash = Bank.Parse("g0250:r0000:y0010:e0000:w0000"), ItemId = "S143IG0007",
        };

        var first = Sites.Visit(Map(), Stack(), ruin, Rules);
        Assert.Null(first.Blocked);
        Assert.Equal(250, first.Gained.Gold);
        Assert.Equal(10, first.Gained.Life);
        Assert.Equal(["S143IG0007"], first.ItemsGained);

        var looted = Sites.AfterVisit(ruin, Stack(), first);
        Assert.Equal(Player, looted.LooterId);
        Assert.Equal(Bank.Zero, looted.Cash);
        Assert.Equal("", looted.ItemId);
        // Исходный объект не тронут: AfterVisit возвращает новый.
        Assert.Equal(250, ruin.Cash.Gold);

        var second = Sites.Visit(Map(), Stack(), looted, Rules);
        Assert.Contains("разграблена", second.Blocked);
        Assert.Equal(Bank.Zero, second.Gained);
        Assert.Empty(second.ItemsGained);
    }

    [Fact]
    public void Visit_GuardedRuinIsBlockedAndKeepsItsCash()
    {
        var ruin = new MapSite
        {
            Id = "S143RU0002", Kind = SiteKind.Ruin, RawKind = "MidRuin", X = 5, Y = 5,
            Cash = Bank.Parse("g0500:r0000:y0000:e0000:w0000"),
            Guards = ["S143UN0010", "S143UN0011"],
        };

        var result = Sites.Visit(Map(), Stack(), ruin, Rules);

        Assert.Contains("стережёт", result.Blocked);
        Assert.Equal(Bank.Zero, result.Gained);
        // Отказ ничего не меняет: добыча остаётся победителю боя.
        Assert.Same(ruin, Sites.AfterVisit(ruin, Stack(), result));
    }

    [Fact]
    public void Visit_BagGivesItemsAndDisappears()
    {
        var bag = new MapSite
        {
            Id = "S143BG0001", Kind = SiteKind.Bag, RawKind = "MidBag", X = 4, Y = 4,
            Items = ["S143IG0001", "S143IG0002"],
        };

        var taken = Sites.Visit(Map(), Stack(), bag, Rules);
        Assert.Null(taken.Blocked);
        Assert.Equal(["S143IG0001", "S143IG0002"], taken.ItemsGained);

        var after = Sites.AfterVisit(bag, Stack(), taken);
        Assert.True(after.Gone);
        Assert.Empty(after.Items);
        Assert.Contains("больше нет", Sites.Visit(Map(), Stack(), after, Rules).Blocked);
    }

    [Fact]
    public void Visit_CrystalPaysThroughDailyIncomeNotThroughVisiting()
    {
        var crystal = new MapSite
        {
            Id = "S143CR0001", Kind = SiteKind.Crystal, RawKind = "MidCrystal",
            X = 6, Y = 6, Resource = 2,
        };

        var result = Sites.Visit(Map(), Stack(), crystal, Rules);

        Assert.Equal(Bank.Zero, result.Gained);
        Assert.Contains("дневным доходом", result.Blocked);
    }

    [Fact]
    public void Visit_EmptyEnemyVillageChangesOwner()
    {
        var village = new MapSite
        {
            Id = "S143FT0002", Kind = SiteKind.Village, RawKind = "MidVillage",
            X = 8, Y = 8, Owner = Enemy,
        };

        var result = Sites.Visit(Map(), Stack(), village, Rules);
        Assert.Null(result.Blocked);
        Assert.True(result.Captured);
        Assert.Equal(Player, Sites.AfterVisit(village, Stack(), result).Owner);

        var garrisoned = village with { Guards = ["S143UN0020"] };
        Assert.Contains("гарнизон", Sites.Visit(Map(), Stack(), garrisoned, Rules).Blocked);

        var mine = village with { Owner = Player };
        var already = Sites.Visit(Map(), Stack(), mine, Rules);
        Assert.False(already.Captured);
        Assert.Contains("уже принадлежит", already.Blocked);
    }

    /// <summary>Вход в любой из четырёх сайтов (план Ф7 §1.2) фиксирует посетителя и сам ничего не даёт:
    /// покупка, продажа, найм, обучение и заклинание — отдельные действия <see cref="SiteTrade"/>.</summary>
    [Theory]
    [InlineData(SiteKind.Merchant, "MidSiteMerchant")]
    [InlineData(SiteKind.Mercenaries, "MidSiteMercs")]
    [InlineData(SiteKind.Trainer, "MidSiteTrainer")]
    [InlineData(SiteKind.Mage, "MidSiteMage")]
    public void Visit_SitesRecordTheVisitAndTradeSeparately(SiteKind kind, string raw)
    {
        var shop = new MapSite { Id = "S143SI0001", Kind = kind, RawKind = raw, X = 9, Y = 9 };

        var result = Sites.Visit(Map(), Stack(), shop, Rules);

        Assert.Null(result.Blocked);
        Assert.Equal(Bank.Zero, result.Gained);
        Assert.Empty(result.UnitsHired);      // найм — SiteTrade.HireUnit, не вход
        Assert.Equal(0, result.XpGained);
        Assert.Equal("S143KC0001", Sites.AfterVisit(shop, Stack(), result).VisitorId);
    }

    /// <summary>Жезл с задачи 8 Ф7 — свой вид, и вход в него не исключение, а отказ с причиной:
    /// постановка и снос жезла это отдельные команды <see cref="Rods"/>, а не посещение.</summary>
    [Fact] public void Visit_RodIsRefusedWithItsCommandsNamed()
    {
        var rod = new MapSite { Id = "S143RD0000", Kind = SiteKind.Rod, RawKind = "MidRod", X = 9, Y = 9 };

        var result = Sites.Visit(Map(), Stack(), rod, Rules);

        Assert.Contains("S143RD0000", result.Blocked);
        Assert.Contains("Rods.Break", result.Blocked);
    }

    /// <summary>ai-q55 (ранг 1 по сохранениям, отказы — ранг 2): отряд с павшим лидером подбирает мешок, но
    /// не входит ни в лавку, ни в руину; прочие виды отказаны тем же правилом.</summary>
    [Theory]
    [InlineData(SiteKind.Ruin, "MidRuin")]
    [InlineData(SiteKind.Merchant, "MidSiteMerchant")]
    [InlineData(SiteKind.Mercenaries, "MidSiteMercs")]
    [InlineData(SiteKind.Trainer, "MidSiteTrainer")]
    [InlineData(SiteKind.Mage, "MidSiteMage")]
    [InlineData(SiteKind.Village, "MidVillage")]
    [InlineData(SiteKind.Other, "MidTomb")]
    public void Visit_DeadLeader_IsRefusedEverywhereButBag(SiteKind kind, string raw)
    {
        var dead = new MapStack
        {
            Id = "S143KC0001", Owner = Player, Subrace = "S143SR0001", LeaderId = "S143UN0001",
            LeaderAlive = false, X = 10, Y = 10, Move = 20, Inside = "",
            UnitIds = ["S143UN0001"], Slots = [-1, -1, 0, -1, -1, -1],
        };
        var site = new MapSite { Id = "S143SI0009", Kind = kind, RawKind = raw, X = 1, Y = 1, Owner = Enemy };
        var bag = new MapSite { Id = "S143BG000F", Kind = SiteKind.Bag, RawKind = "MidBag", X = 1, Y = 1,
                                Items = ["S143IM003F"] };

        Assert.Contains("лидер", Sites.Visit(Map(), dead, site, Rules).Blocked);
        var taken = Sites.Visit(Map(), dead, bag, Rules);
        Assert.Null(taken.Blocked);
        Assert.Equal(["S143IM003F"], taken.ItemsGained);
    }

    [Theory]
    [InlineData(SiteKind.Other, "MidTomb")]
    public void Visit_KindOutsideC4SubsetThrowsWithItsName(SiteKind kind, string raw)
    {
        var site = new MapSite { Id = "S143SI0009", Kind = kind, RawKind = raw, X = 1, Y = 1 };

        var error = Assert.Throws<NotSupportedException>(
            () => Sites.Visit(Map(), Stack(), site, Rules));

        Assert.Contains(raw, error.Message);
        Assert.Contains("S143SI0009", error.Message);
    }

    /// <summary>Ранг 1 (дизассемблер, см. <see cref="Sites.Entrance"/>): вход в город и руину —
    /// правый нижний угол занимаемого прямоугольника.</summary>
    [Theory]
    [InlineData(10, 10, 1, 1, 10, 10)]
    [InlineData(10, 10, 3, 3, 12, 12)]
    [InlineData(20, 7, 5, 4, 24, 10)]
    public void Entrance_IsBottomRightCornerOfTheFootprint(
        int x, int y, int sizeX, int sizeY, int expectedX, int expectedY) =>
        Assert.Equal((expectedX, expectedY), Sites.Entrance(x, y, sizeX, sizeY));

    // ---------------------------------------------------------------- настоящие карты

    [SkippableFact]
    public void SiteLoader_ReadsEveryScenarioInExports()
    {
        var files = Scenarios();
        Skip.If(files.Count == 0, $"нет сценариев в {GamePaths.Exports}");

        var byKind = new SortedDictionary<SiteKind, int>();
        int ruinsWithCash = 0, ruinsLooted = 0, offMap = 0, guarded = 0;
        foreach (var file in files)
        {
            var map = SaveMaps.Map(file);
            foreach (var site in SaveMaps.Sites(file))
            {
                byKind[site.Kind] = byKind.GetValueOrDefault(site.Kind) + 1;
                if (!map.Contains(site.X, site.Y)) offMap++;
                if (site.Guards.Count > 0) guarded++;
                if (site.Kind == SiteKind.Ruin)
                {
                    if (site.Cash != Bank.Zero) ruinsWithCash++;
                    if (site.LooterId.Length > 0) ruinsLooted++;
                }
            }
        }

        foreach (var (kind, n) in byKind)
            _out.WriteLine($"{kind,-12} {n}");
        _out.WriteLine($"карт {files.Count}; руин с добычей {ruinsWithCash}, разграбленных " +
                       $"{ruinsLooted}, объектов с охраной {guarded}, вне карты {offMap}");

        Assert.Equal(0, offMap);
        // Подмножество С4 требует, чтобы эти виды вообще читались; ноль означал бы, что класс
        // записи опознан неверно и объекты молча потерялись.
        foreach (var kind in new[] { SiteKind.Capital, SiteKind.Village, SiteKind.Ruin,
                                     SiteKind.Bag, SiteKind.Crystal, SiteKind.Merchant,
                                     SiteKind.Mercenaries, SiteKind.Trainer })
            Assert.True(byKind.GetValueOrDefault(kind) > 0, $"ни одного объекта вида {kind}");
        Assert.True(ruinsWithCash > 0, "ни одной руины с добычей — казна руин читается неверно");
    }

    [SkippableFact]
    public void EventLoader_ReadsEveryScenarioAndEveryZoneReferenceResolves()
    {
        var files = Scenarios();
        Skip.If(files.Count == 0, $"нет сценариев в {GamePaths.Exports}");

        var conditions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var effects = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int events = 0, supportedConditions = 0, supportedEffects = 0, danglingZones = 0;

        foreach (var file in files)
        {
            var set = EventLoader.Load(file);
            events += set.Events.Count;
            foreach (var e in set.Events)
            {
                foreach (var c in e.Conditions)
                {
                    conditions[c.Name] = conditions.GetValueOrDefault(c.Name) + 1;
                    if (c is not UnsupportedCondition) supportedConditions++;
                    if (c is ZoneCondition z && !set.Zones.ContainsKey(z.LocationId))
                    {
                        danglingZones++;
                        _out.WriteLine($"{Path.GetFileName(file)}: {e.Id} ссылается на область " +
                                       $"{z.LocationId}, которой нет");
                    }
                }
                foreach (var f in e.Effects)
                {
                    effects[f.Name] = effects.GetValueOrDefault(f.Name) + 1;
                    if (f is not UnsupportedEffect) supportedEffects++;
                }
            }
        }

        _out.WriteLine($"карт {files.Count}, событий {events}");
        _out.WriteLine("условия:");
        foreach (var (name, n) in conditions) _out.WriteLine($"  {name,-22} {n}");
        _out.WriteLine("эффекты:");
        foreach (var (name, n) in effects) _out.WriteLine($"  {name,-22} {n}");
        _out.WriteLine($"в подмножестве С4: условий {supportedConditions} из " +
                       $"{conditions.Values.Sum()}, эффектов {supportedEffects} из " +
                       $"{effects.Values.Sum()}");

        Assert.True(events > 0, "ни одного события ни на одной карте — разбор сбит");
        // Область, на которую ссылается условие, обязана лежать в том же файле: это проверка
        // того, что мы читаем ССЫЛКУ, а не мусор на её месте.
        Assert.Equal(0, danglingZones);
        Assert.True(supportedConditions > 0, "ни одного условия из подмножества С4 на 33 картах");
    }

    // ---------------------------------------------------------------- сверка с журналом

    /// <summary>
    /// Событие зонда <c>site</c> (<c>updateEncLayoutSite</c>, Gog <c>0x578b2b</c>): движок открыл
    /// карточку объекта. Сверяется, что посещённые объекты — те же, что знаем мы, и что
    /// категория <c>LSite.dbf</c> отвечает нашему виду. Журнал карты снимает пользователь
    /// (задача 2 плана Ф5) — пока его нет, тест пропускается с причиной.
    /// </summary>
    [SkippableFact]
    public void Sites_VisitedObjectsMatchJournal()
    {
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0, known = 0, kindSame = 0, kindDiffer = 0, unknown = 0, noCategory = 0;
        foreach (var at in MapJournalEvents.Of<SiteEvent>("site"))
        {
            var e = at.Event;
            if (at.Why is not null) { skips[at.Why] = skips.GetValueOrDefault(at.Why) + 1; continue; }
            total++;
            var site = SitesOf(at.Sg!).FirstOrDefault(s => s.Id.Equals(e.Site, StringComparison.OrdinalIgnoreCase));
            if (site is null)
            {
                unknown++;
                _out.WriteLine($"РАСХОЖДЕНИЕ {at.Source} seq {e.Seq}: объекта {e.Site} нет на снимке {Path.GetFileName(at.Sg)}");
                continue;
            }
            known++;
            var expected = KindOfCategory(e.Category);
            if (expected is null) { noCategory++; continue; }
            if (expected == site.Kind) kindSame++;
            else
            {
                kindDiffer++;
                _out.WriteLine($"РАСХОЖДЕНИЕ {at.Source} seq {e.Seq} {e.Site}: журнал категория {e.Category} ({expected}), " +
                               $"наш вид {site.Kind}");
            }
        }

        foreach (var (why, n) in skips) _out.WriteLine($"  пропуск: {why} — {n}");
        _out.WriteLine($"site: всего {total}   объект на снимке {known}   " +
                       $"вид совпал {kindSame}   разошёлся {kindDiffer}   " +
                       $"объекта нет на снимке {unknown}   категория не из LSite {noCategory}");
        Skip.If(total == 0, "в корпусе нет событий site со снимком карты (проверено 0 напечатано выше)");
        Assert.Equal(0, unknown);
        Assert.Equal(0, kindDiffer);
        Assert.True(kindSame > 0, "ни одной категории из LSite — сверять вид не с чем");
    }

    /// <summary>
    /// Событие <c>xp</c> (<c>changeUnitXpCheckUpgrade</c>, Gog <c>0x5d7412</c>). Величину опыта
    /// модель не считает (см. <see cref="VisitResult.XpGained"/>), поэтому здесь честные
    /// счётчики: сколько начислений журнал знает и скольким из них соответствует юнит снимка
    /// карты перед событием. Строгая часть — «юнит на снимке есть, и он того же вида»: тип
    /// реализации (<c>impl</c>) и уровень до вызова из журнала против юнита сохранения; расхождение
    /// означало бы, что мы читаем не то поле.
    /// </summary>
    [SkippableFact]
    public void Xp_EventsReferToUnitsWeKnow()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var rules = RulesLoader.Load(GamePaths.Globals);
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var firstSince = new HashSet<(string Sg, long From, string Unit)>();
        int total = 0, known = 0, unknown = 0, typeChecked = 0, typeDiffer = 0;
        long granted = 0;
        foreach (var at in MapJournalEvents.Of<XpEvent>("xp"))
        {
            var e = at.Event;
            if (at.Why is not null) { skips[at.Why] = skips.GetValueOrDefault(at.Why) + 1; continue; }
            total++;
            granted += e.Amount;
            if (!SnapshotOf(at.Sg!).Units.TryGetValue(e.Unit, out var unit)) { unknown++; continue; }
            known++;
            // Вид сверяется только у первого начисления юниту после среза снимка: до него опыта не было, значит, и
            // повышения (UG — порождённая реализация, сравнивается глобальный тип и уровень).
            if (e.Impl is null || e.Level is null || !firstSince.Add((at.Sg!, at.From, e.Unit.ToUpperInvariant()))) continue;
            typeChecked++;
            var theirs = Global(rules, e.Impl, (int)e.Level);
            var ours = Global(rules, unit.TypeId, unit.Level);
            if (theirs != ours && typeDiffer++ < 12)
                _out.WriteLine($"РАСХОЖДЕНИЕ {at.Source} seq {e.Seq} {e.Unit}: журнал {e.Impl} ур. {e.Level} = {theirs}, " +
                               $"снимок {unit.TypeId} ур. {unit.Level} = {ours} ({Path.GetFileName(at.Sg)})");
        }

        foreach (var (why, n) in skips) _out.WriteLine($"  пропуск: {why} — {n}");
        _out.WriteLine($"xp: всего {total}   юнит есть на снимке {known}   " +
                       $"юнита нет (нанят или создан после снимка) {unknown}   опыта начислено {granted}   " +
                       $"вид сверен {typeChecked}, разошёлся {typeDiffer}");
        _out.WriteLine("величина опыта моделью не считается: за руину её даёт бой (Ф4), " +
                       "у наставника — покупка (пропущено с причиной)");
        Skip.If(total == 0, "в корпусе нет событий xp со снимком карты (проверено 0 напечатано выше)");
        Assert.True(typeChecked > 0, "ни одного вида не сверено — читаем не то поле");
        Assert.Equal(0, typeDiffer);
    }

    /// <summary>Глобальный тип и уровень реализации: строка Gunits как есть, порождённая (UG) — разбором генератора.</summary>
    private static (string Id, int Level) Global(GameRules rules, string impl, int level) =>
        rules.Units.ContainsKey(impl) || rules.Generator is null || !rules.Generator.TryDecode(impl, out var id, out int lv)
            ? (impl.ToUpperInvariant(), level) : (id.ToUpperInvariant(), lv);

    private static readonly Dictionary<string, (MapState Map, IReadOnlyList<MapSite> Sites)> Snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    private static MapState SnapshotOf(string sg) => Snap(sg).Map;

    private static IReadOnlyList<MapSite> SitesOf(string sg) => Snap(sg).Sites;

    private static (MapState Map, IReadOnlyList<MapSite> Sites) Snap(string sg)
    {
        lock (Snapshots)
            return Snapshots.TryGetValue(sg, out var m) ? m
                : Snapshots[sg] = (SaveMaps.Map(sg), SaveMaps.Sites(sg).ToList());
    }

    /// <summary>Вид объекта по категории <c>LSite.dbf</c> (0 L_SAGE, 1 L_OBELISK, 2 L_MERCHANT,
    /// 3 и 6 L_TRAINER, 4 L_MAGE_TOWER, 5 L_MERCENARIES). Ранг 1 — таблица прочитана из
    /// <c>Globals/LSite.dbf</c>; ранг 3 — что <c>site.category</c> зонда это именно она,
    /// поэтому неизвестная категория считается пропуском, а не расхождением.</summary>
    private static SiteKind? KindOfCategory(long category) => category switch
    {
        2 => SiteKind.Merchant,
        3 or 6 => SiteKind.Trainer,
        4 => SiteKind.Mage,
        5 => SiteKind.Mercenaries,
        _ => null,
    };

    // ---------------------------------------------------------------- сборка синтетики

    /// <summary>Справочники экономики подмножеству С4 не нужны (ни одна цена в посещении не
    /// считается), но параметр обязателен — здесь он пустой и не читает файлов.</summary>
    private static readonly EconomyRules Rules = new(
        new Dictionary<string, RaceInfo>(), [], new Dictionary<string, BuildingType>(),
        new Dictionary<string, UnitHire>(), [0, 0, 0, 0], 50, 0, 0);

    private static MapStack Stack() => new()
    {
        Id = "S143KC0001", Owner = Player, Subrace = "S143SR0001", LeaderId = "S143UN0001",
        LeaderAlive = true, X = 10, Y = 10, Move = 20, Inside = "",
        UnitIds = ["S143UN0001"], Slots = [-1, -1, 0, -1, -1, -1],
    };

    private static MapState Map() => new()
    {
        Size = 16,
        Tiles = new Tile[16, 16],
        Stacks = new Dictionary<string, MapStack> { [Stack().Id] = Stack() },
        Units = new Dictionary<string, MapUnit>(),
        Players = new Dictionary<string, MapPlayer>(),
        Objects = new Dictionary<string, MapObject>(),
        Plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>(),
        Fog = new Dictionary<string, bool[,]>(),
        Turn = 1,
    };

    private static IReadOnlyList<string> Scenarios() =>
        Directory.Exists(GamePaths.Exports)
            ? Directory.EnumerateFiles(GamePaths.Exports, "*.sg")
                .OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];
}
