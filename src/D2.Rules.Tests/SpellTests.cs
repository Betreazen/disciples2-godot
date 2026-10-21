using D2.Data;
using D2.Data.Corpus;
using D2.Data.Dbf;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Tests;
using D2.Rules.Units;
using Xunit.Abstractions;

/// <summary>
/// Задача 9 фазы Ф6: заклинания. Числа берутся из живой ванили — <c>Globals/Gspells.dbf</c> через
/// <c>D2.Data.Magic.SpellLoader</c> и <c>Gmodif</c>/<c>GmodifL</c> через
/// <c>D2.Data.Battle.ModifierLoader</c>; синтетических заклинаний здесь нет. Игра не запускается.
/// <para>Утверждения ранга 1 — счётчики и поимённые заклинания; помеченное рангом 3 в коде
/// (геометрия площади, потолок лечения, порядок эффектов) тестами не «доказывается», а лишь
/// закрепляется в том виде, в каком реализовано.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class SpellTests(ITestOutputHelper output)
{
    // ---- вход: настоящие заклинания и модификаторы ванили --------------------------------------

    /// <summary>Заклинания живой ванили. Задача 9 переводила строку <c>D2.Data.Magic.SpellType</c>
    /// в правило <see cref="Spell"/> тестовым хелпером <c>ToRule</c>, потому что правка
    /// <c>D2.Data</c> ей была запрещена; задача 10 переселила перевод в <c>SpellLoader.Load</c> — и
    /// хелпер вместе с приведением <c>int</c> к <see cref="SpellCategory"/> отсюда ушёл.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, Spell>> Raw =
        new(() => D2.Data.Magic.SpellLoader.Load(GamePaths.Globals));

    private static readonly Lazy<D2.Data.Battle.ModifierTables> Modifiers =
        new(() => D2.Data.Battle.ModifierLoader.Load(GamePaths.Globals));

    private static IReadOnlyList<Spell> All() => Raw.Value.Values.ToList();

    private static Spell One(string id) => Raw.Value[id];

    // ---- 1. каталог ----------------------------------------------------------------------------

    /// <summary>Живых заклинаний 120, уровни 1..5 и распределение по ним — числа задачи 1.
    /// Границы уровня — те, что проверяет сам движок (<see cref="Spell.MinLevel"/>).</summary>
    [SkippableFact]
    public void SpellCatalogue_MatchesVanillaCounts()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var spells = All();
        Assert.Equal(120, spells.Count);

        Assert.All(spells, s => Assert.InRange(s.Level, Spell.MinLevel, Spell.MaxLevel));
        foreach (var (level, expected) in new[] { (1, 25), (2, 25), (3, 25), (4, 25), (5, 20) })
            Assert.Equal(expected, spells.Count(s => s.Level == level));

        var expectedByCategory = new (SpellCategory Category, int Count)[]
        {
            (SpellCategory.Attack, 33), (SpellCategory.Lower, 20), (SpellCategory.Heal, 7),
            (SpellCategory.Boost, 25), (SpellCategory.Summon, 19), (SpellCategory.Fog, 4),
            (SpellCategory.Unfog, 3), (SpellCategory.RestoreMove, 4),
            (SpellCategory.Invisibility, 2), (SpellCategory.RemoveRod, 1),
            (SpellCategory.ChangeTerrain, 2), (SpellCategory.GiveWards, 0),
        };
        foreach (var (category, expected) in expectedByCategory)
        {
            int actual = spells.Count(s => s.Category == category);
            Assert.Equal(expected, actual);
            output.WriteLine($"{category,-14} {actual}");
        }
        Assert.Equal(120, expectedByCategory.Sum(p => p.Count));
    }

    // ---- 2. CASTING_C: правило движка против данных ---------------------------------------------

    /// <summary>Грамматика строковой казны, прочитанная в дизассемблере GOG (см. <see cref="Spell"/>,
    /// параметр <c>CastingCost</c>), проверяется на всех 240 строках цены ванили: буква из
    /// <c>"GRYEWB"</c> плюс ровно четыре цифры, шестая валюта только при длине больше 29, разбор
    /// <see cref="Bank.Parse"/> совпадает с ручным.
    /// <para>Заодно закрепляются два наблюдения ранга 1: золото в <c>CASTING_C</c> не встречается
    /// ни разу, а <c>BUY_C</c> — всегда ровно одно золото.</para></summary>
    [SkippableFact]
    public void CastingCost_ObeysEngineBankGrammar()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        const string letters = "GRYEWB";
        const int fiveCurrencyLength = 29;   // движок: cmp eax, 0x1d (0x58637c)

        int strings = 0, sixCurrency = 0, goldInCasting = 0;
        var byLength = new Dictionary<int, int>();
        foreach (var row in DbfTable.Read(Path.Combine(GamePaths.Globals, "Gspells.dbf")).Rows)
        {
            foreach (var column in new[] { "CASTING_C", "BUY_C" })
            {
                var raw = row.GetString(column);
                byLength[raw.Length] = byLength.GetValueOrDefault(raw.Length) + 1;
                var parts = raw.Split(':');

                foreach (var part in parts)
                {
                    Assert.Equal(5, part.Length);                                  // буква + 4 цифры
                    Assert.Contains(char.ToUpperInvariant(part[0]), letters);
                    Assert.All(part[1..], c => Assert.True(char.IsAsciiDigit(c), part));
                }
                // Шестая валюта (B) читается движком только у строки длиннее 29 символов.
                Assert.Equal(raw.Length > fiveCurrencyLength ? 6 : 5, parts.Length);
                if (parts.Length == 6) sixCurrency++;

                var bank = Bank.Parse(raw);
                long Amount(char letter) =>
                    parts.Where(p => char.ToUpperInvariant(p[0]) == letter)
                         .Select(p => (long)int.Parse(p[1..])).FirstOrDefault();
                Assert.Equal((Amount('G'), Amount('R'), Amount('Y'), Amount('E'), Amount('W'), Amount('B')),
                             (bank.Gold, bank.Infernal, bank.Life, bank.Death, bank.Runic, bank.Grove));

                if (column == "CASTING_C" && bank.Gold != 0) goldInCasting++;
                if (column == "BUY_C")
                {
                    Assert.True(bank.Gold > 0, row.GetString("SPELL_ID"));
                    Assert.Equal((0L, 0L, 0L, 0L, 0L),
                                 (bank.Infernal, bank.Life, bank.Death, bank.Runic, bank.Grove));
                }
                strings++;
            }
        }

        Assert.Equal(240, strings);
        Assert.Equal(0, goldInCasting);
        Assert.Equal(24, sixCurrency);   // 24 из 120 CASTING_C длиной 35; все BUY_C — по 29
        output.WriteLine($"строк цены проверено {strings}, шестивалютных {sixCurrency}, " +
                         $"золота в CASTING_C {goldInCasting}");
        foreach (var (len, n) in byLength.OrderBy(p => p.Key)) output.WriteLine($"  длина {len}: {n}");
    }

    // ---- 3. категория против заполненных колонок ------------------------------------------------

    /// <summary>Кросс-таблица «категория × полезная нагрузка» — то самое распределение, по которому
    /// колонка опознаётся вместо подписи. Проверяется поимённо каждая связка; несовпадение печатает
    /// заклинание.</summary>
    [SkippableFact]
    public void SpellCategories_MatchTheirPayloadColumns()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        int checkedSpells = 0, extraModifier = 0;
        foreach (var s in All())
        {
            switch (s.Category)
            {
                case SpellCategory.Attack:
                    Assert.True(s.DamageQty > 0 && s.DamageSource > 0, s.Id); break;
                case SpellCategory.Lower or SpellCategory.Boost:
                    Assert.NotEqual("", s.ModifierId); break;
                case SpellCategory.Heal:
                    Assert.True(s.HealQty > 0, s.Id); break;
                case SpellCategory.Summon:
                    Assert.NotEqual("", s.SummonUnitId); break;
                case SpellCategory.RestoreMove:
                    Assert.True(s.RestoreMovePercent > 0, s.Id);
                    if (s.ModifierId.Length > 0) extraModifier++;
                    break;
                case SpellCategory.ChangeTerrain:
                    Assert.Equal(1, s.Ground); break;   // Lground L_FOREST у обоих
                case SpellCategory.Fog or SpellCategory.Unfog or SpellCategory.Invisibility
                    or SpellCategory.RemoveRod:
                    Assert.Equal((0, 0, 0, "", ""),
                                 (s.DamageQty, s.HealQty, s.RestoreMovePercent, s.ModifierId, s.SummonUnitId));
                    break;
                default:
                    throw new InvalidDataException($"{s.Id}: категория {s.Category} в ванили не встречается");
            }
            checkedSpells++;
        }
        Assert.Equal(120, checkedSpells);
        Assert.Equal(1, extraModifier);   // только g000ss0100 «Swift»
        output.WriteLine($"проверено {checkedSpells}; категорий с лишней колонкой {extraModifier} (Swift)");
    }

    /// <summary>Обереги читает только результат категории GiveWards (<c>CSpellResultGiveWards</c> <c>0x41ce93</c> → <c>0x582b37</c>):
    /// у него эффекты — только обереги, даже при заполненном <c>MODIF_ID</c>; без оберегов — порча данных; у прочих категорий
    /// <c>WARD1..4</c> не читаются.</summary>
    [Fact]
    public void Effects_WardsOnlyForGiveWardsCategory()
    {
        static Spell Of(SpellCategory category, string modifier, params string[] wards) =>
            new("g999ss0001", category, 1, Bank.Zero, Bank.Zero, 1, modifier, "", 0, 0, 0, 0, 0, wards);

        var wards = SpellCast.Effects(Of(SpellCategory.GiveWards, "g000um0040", "g000um0001", "g000um0005"));
        Assert.Equal([(SpellEffectKind.Ward, "g000um0001"), (SpellEffectKind.Ward, "g000um0005")], wards.Select(e => (e.Kind, e.Id)));
        Assert.Throws<InvalidDataException>(() => SpellCast.Effects(Of(SpellCategory.GiveWards, "")));
        var boost = SpellCast.Effects(Of(SpellCategory.Boost, "g000um0040", "g000um0001"));
        Assert.Equal((SpellEffectKind.Modifier, "g000um0040"), boost.Select(e => (e.Kind, e.Id)).Single());
    }

    /// <summary>Колонка <c>CHNGTERCAT</c> ложна у всех 120 строк, а вардов не несёт ни одно
    /// заклинание — обе подписи, которые план Ф6 называл рабочими, в ванили мертвы.</summary>
    [SkippableFact]
    public void VanillaSpells_CarryNoWards_AndNoChangeTerrainFlag()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        // CHNGTERCAT и QTY_WARDS в запись Spell не взяты (см. SpellLoader), поэтому утверждение о
        // них читается прямо из .dbf: «не взято» не должно тихо превратиться в «не проверено».
        int rows = 0;
        foreach (var row in DbfTable.Read(Path.Combine(GamePaths.Globals, "Gspells.dbf")).Rows)
        {
            var id = row.GetString("SPELL_ID");
            Assert.False(row.GetBool("CHNGTERCAT"), id);
            Assert.Equal(0, row.GetInt("QTY_WARDS"));
            Assert.Empty(Raw.Value[id].Wards);
            rows++;
        }
        Assert.Equal(120, rows);
        output.WriteLine($"строк проверено {rows}: CHNGTERCAT ложно у всех, WARD1..4 пусты у всех");
    }

    // ---- 4. эффекты ------------------------------------------------------------------------------

    /// <summary>Каждое заклинание даёт хотя бы один эффект, «Swift» — два. Разбор идёт по колонкам,
    /// поэтому категория с лишней колонкой не теряется молча.</summary>
    [SkippableFact]
    public void Effects_ComeFromColumns_NotCategory()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var byKind = new Dictionary<SpellEffectKind, int>();
        int total = 0;
        foreach (var s in All())
        {
            var effects = SpellCast.Effects(s);
            Assert.NotEmpty(effects);
            foreach (var e in effects) byKind[e.Kind] = byKind.GetValueOrDefault(e.Kind) + 1;
            total += effects.Count;
        }
        Assert.Equal(121, total);   // 120 заклинаний, у одного два эффекта

        var swift = SpellCast.Effects(One("g000ss0100"));
        Assert.Equal(2, swift.Count);
        Assert.Equal(new SpellEffect(SpellEffectKind.Modifier, Id: "g000um7538"), swift[0]);
        Assert.Equal(new SpellEffect(SpellEffectKind.RestoreMove, 30), swift[1]);

        foreach (var (kind, expected) in new[]
                 {
                     (SpellEffectKind.Damage, 33), (SpellEffectKind.Heal, 7), (SpellEffectKind.Modifier, 46),
                     (SpellEffectKind.Summon, 19), (SpellEffectKind.RestoreMove, 4), (SpellEffectKind.Fog, 4),
                     (SpellEffectKind.Unfog, 3), (SpellEffectKind.Invisibility, 2),
                     (SpellEffectKind.RemoveRod, 1), (SpellEffectKind.ChangeGround, 2),
                 })
        {
            Assert.Equal(expected, byKind.GetValueOrDefault(kind));
            output.WriteLine($"{kind,-13} {expected}");
        }
        Assert.Equal(10, byKind.Count);
    }

    /// <summary>Восемь поимённых заклинаний с конкретными числами — по одному на каждую ветвь
    /// разбора.</summary>
    [SkippableFact]
    public void NamedSpells_HaveMeasuredNumbers()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);

        var airWard = One("g000ss0001");                       // «Air ward»
        Assert.Equal((SpellCategory.Boost, 1, 1, "g000um0001"),
                     (airWard.Category, airWard.Level, airWard.Area, airWard.ModifierId));
        Assert.Equal(new Bank(0, 0, 100, 0, 0, 0), airWard.CastingCost);
        Assert.Equal(new Bank(200, 0, 0, 0, 0, 0), airWard.BuyCost);

        var lightning = One("g000ss0081");                     // «Chain lightning»
        Assert.Equal((SpellCategory.Attack, 3, 3, 40, 7),
                     (lightning.Category, lightning.Level, lightning.Area,
                      lightning.DamageQty, lightning.DamageSource));
        Assert.Equal(new Bank(0, 0, 225, 0, 225, 0), lightning.CastingCost);

        var healing = One("g000ss0020");                       // «Major healing»
        Assert.Equal((SpellCategory.Heal, 5, 150), (healing.Category, healing.Level, healing.HealQty));
        Assert.Equal(new Bank(0, 100, 250, 60, 100, 0), healing.CastingCost);

        var forest = One("g000ss0120");                        // «Create Forest», шесть валют
        Assert.Equal((SpellCategory.ChangeTerrain, 5, 7, 1),
                     (forest.Category, forest.Level, forest.Area, forest.Ground));
        Assert.Equal(new Bank(0, 0, 110, 60, 60, 275), forest.CastingCost);

        var skeleton = One("g000ss0061");                      // «Summon I: Skeleton»
        Assert.Equal((SpellCategory.Summon, "g000uu0101"), (skeleton.Category, skeleton.SummonUnitId));
        Assert.Equal(new Bank(0, 0, 0, 100, 0, 0), skeleton.CastingCost);

        var swift = One("g000ss0100");                         // «Swift», единственная шестая валюта L1
        Assert.Equal((SpellCategory.RestoreMove, 30, "g000um7538"),
                     (swift.Category, swift.RestoreMovePercent, swift.ModifierId));
        Assert.Equal(new Bank(0, 0, 0, 0, 0, 100), swift.CastingCost);

        var sinkhole = One("g000ss0119");                      // «Sinkhole» — единственное RemoveRod
        Assert.Equal(SpellCategory.RemoveRod, sinkhole.Category);
        Assert.Equal(SpellEffectKind.RemoveRod, Assert.Single(SpellCast.Effects(sinkhole)).Kind);

        var daylight = One("g000ss0083");                      // «Daylight» — площадь 999
        Assert.Equal((SpellCategory.Unfog, 999, 499), (daylight.Category, daylight.Area, daylight.AreaRadius));

        output.WriteLine("проверено 8 заклинаний поимённо");
    }

    // ---- 5. модификатор: та же цепочка, что у предмета и варда ----------------------------------

    /// <summary>Все 46 непустых <c>MODIF_ID</c> разрешаются, и эффект доходит до
    /// <see cref="EffectiveStats"/> — своего механизма у заклинания нет.</summary>
    [SkippableFact]
    public void SpellModifiers_ResolveAndReachTheChain()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var catalogue = Modifiers.Value.Modifiers;

        int withModifier = 0, without = 0;
        foreach (var s in All())
        {
            var m = SpellCast.ModifierOf(s, catalogue);
            if (m is null) { without++; continue; }
            Assert.Equal(s.ModifierId, m.Id);
            Assert.NotEmpty(m.Elements);
            withModifier++;
        }
        Assert.Equal((46, 74), (withModifier, without));

        // «Air ward» (g000ss0001) -> TYPE 12 Immunity: источник 7 (Air), уровень 2 (Once).
        var ward = SpellCast.ModifierOf(One("g000ss0001"), catalogue)!;
        var unit = new UnitType("g000uu5130", 1, 65, 20, "g000aa0001", "g000000000", false, true, 10, 100);
        var grant = Assert.Single(EffectiveStats.Of(unit, [ward]).Immunities);
        Assert.Equal(new ImmunityGrant(7, ImmunityLevel.Once), grant);

        // «Maledicere» (g000ss0089) -> TYPE 3 Power -25 %: 80 -> 60 (усечение к нулю, 80*(-25)/100 = -20).
        var attack = new AttackType("g000aa0001", 50, AttackClass.Damage, 80, 3, 25, 0, false, 0);
        var lower = SpellCast.ModifierOf(One("g000ss0089"), catalogue)!;
        Assert.Equal(60, EffectiveStats.OfAttack(attack, [lower]).Attack.Power);

        // «Ymir's blessing» (g000ss0085) -> TYPE 4 QtyDamage +20 %: 25 -> 30.
        var boost = SpellCast.ModifierOf(One("g000ss0085"), catalogue)!;
        Assert.Equal(30, EffectiveStats.OfAttack(attack, [boost]).Attack.QtyDamage);

        // «Swift» (g000ss0100) -> TYPE 7 MoveAllowance +30 %: запас 20 -> 26.
        var swift = SpellCast.ModifierOf(One("g000ss0100"), catalogue)!;
        Assert.Equal(26, EffectiveStats.Of(unit, [swift], moveAllowance: 20).MoveAllowance);

        output.WriteLine($"модификаторов разрешено {withModifier}, без модификатора {without}");
    }

    // ---- 6. цена и площадь ------------------------------------------------------------------------

    /// <summary>Оплата применения: не та мана не выручает, казна не меняется на месте.</summary>
    [SkippableFact]
    public void CastingCost_IsPaidFromTreasury()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var healing = One("g000ss0020");                       // r100 y250 e60 w100
        var enough = new Bank(500, 100, 250, 60, 100, 0);
        var noLifeMana = enough with { Life = 249 };

        Assert.True(SpellCast.CanPay(enough, healing));
        Assert.False(SpellCast.CanPay(noLifeMana, healing));
        Assert.Equal(new Bank(500, 0, 0, 0, 0, 0), SpellCast.Pay(enough, healing));
        Assert.Equal(new Bank(500, 100, 250, 60, 100, 0), enough);   // прежняя казна цела
        Assert.Throws<InvalidOperationException>(() => SpellCast.Pay(noLifeMana, healing));
    }

    /// <summary>Площадь — квадрат со стороной <c>AREA</c>, обрезанный краем карты. Ранг 3 (форма);
    /// тест закрепляет реализованное, а не доказывает его — снимает задача 13 счётом клеток.</summary>
    [SkippableFact]
    public void AreaTiles_AreSquareClampedToMap()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        const int size = 48;

        var single = One("g000ss0001");                        // AREA 1
        Assert.Equal([(10, 10)], SpellCast.Tiles(single, 10, 10, size, size).ToList());

        var lightning = One("g000ss0081");                     // AREA 3
        Assert.Equal(9, SpellCast.Tiles(lightning, 10, 10, size, size).Count());
        Assert.Equal(4, SpellCast.Tiles(lightning, 0, 0, size, size).Count());          // угол карты

        var forest = One("g000ss0120");                        // AREA 7
        Assert.Equal(49, SpellCast.Tiles(forest, 10, 10, size, size).Count());

        var daylight = One("g000ss0083");                      // AREA 999 — вся карта без спецветки
        Assert.Equal(size * size, SpellCast.Tiles(daylight, 10, 10, size, size).Count());
    }

    /// <summary>Потолок лечения — как у боевого (ранг 3 для заклинания, см. <see cref="SpellCast.HealAmount"/>).</summary>
    [SkippableFact]
    public void HealAmount_IsCappedByMissingHitPoints()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var healing = One("g000ss0020");                       // HEAL_QTY 150
        Assert.Equal(150, SpellCast.HealAmount(healing, hp: 10, hpMax: 400));
        Assert.Equal(40, SpellCast.HealAmount(healing, hp: 60, hpMax: 100));
        Assert.Equal(0, SpellCast.HealAmount(healing, hp: 100, hpMax: 100));
        Assert.Equal(0, SpellCast.HealAmount(healing, hp: 120, hpMax: 100));
    }

    // ---- 7. сплошной проход по корпусу -----------------------------------------------------------

    /// <summary><b>Сторож, сменивший охраняемое ограничение (задача 16), а не снятый.</b>
    ///
    /// <para>Прежде он требовал, чтобы следа заклинания в корпусе не было вовсе. Настоящая граница
    /// была не в слове «заклинание», а в одном: <b>наложенный заклинанием модификатор меняет урон и
    /// точность боя, а восстановить его из журнала нельзя</b> — событий <c>castSpell</c> в схеме
    /// зонда нет ни одного (адресов <c>castSpell</c> в версионной таблице движка нет, задача 4 Ф6).
    /// Единственный источник — <c>route.json</c> прогона, которого у пассивной партии не бывает.</para>
    ///
    /// <para>С открытием маски <c>passive-*</c> следы слова «заклинание» в корпусе появились — и
    /// все они безобидны: имена хуков в событии <c>start</c>, имена счётчиков в <c>counts</c> и
    /// покупка заклинания в лавке (<c>site_buy_spell</c>), которая кладёт заклинание в книгу
    /// игрока, а не модификатор на отряд. Поэтому ограничение теперь звучит так: <b>в корпусе не
    /// должно быть события, означающего НАЛОЖЕНИЕ заклинания</b>. Всё, что не входит в список
    /// безобидных видов, — нарушение и требует разбора, а не расширения списка.</para>
    ///
    /// <para>Пустой результат ничего не доказывает: тест падает и тогда, когда следов не нашлось
    /// вовсе, — значит проход не состоялся (маска, каталог, кодировка).</para>
    ///
    /// <para>Схема 6.0 (разбор T07 §5.9) добавила два вида, прошедших разбор, а не просто вписанных:
    /// <list type="bullet">
    /// <item><c>spell_expire</c> — хук на <c>CEffectRemoveSpell::apply</c> <c>0x43b9ef</c>: эффект строится в
    /// начале хода игрока (<c>0x443d01</c>) и <b>снимает</b> записи <c>CMidSpellCast</c> и модификаторы юнитов
    /// с <c>ход ≥ TURN_STOP</c> (<c>0x43baea</c> → снятие <c>0x5e7e74</c>). Ранг 1, листинг
    /// <c>spec-map.md</c> П36/П54. Наложения нет — это его конец.</item>
    /// <item><c>spell_research</c> — хук на <c>0x5dc1d7</c> (← <c>CEffectSpellResearch::apply</c> <c>0x43e84c</c>),
    /// изучение заклинания в столице: проверка <c>0x5dbf3d</c>, цена <c>0x604039</c>, списание казны
    /// <c>0x5e6920</c>, запись в список заклинаний <c>0x5e6afc</c>, выход <c>ret 0x10</c>; ни юнитов, ни
    /// модификаторов функция не касается. Ранг 1, листинг capstone по <c>game-pristine/Discipl2.exe</c>
    /// (16.09.2026). Как <c>site_buy_spell</c>, кладёт заклинание в книгу игрока.</item>
    /// </list></para>
    ///
    /// <para><b>Наложения появились (<c>20260916-synth</c>, разбор 17.09).</b> 9 <c>spell_cast</c> и 3 <c>wand_cast</c>
    /// (passive-003) — поэтому граница сужена до того, что она и охраняла: <b>модификатор заклинания не должен дойти
    /// до боя, которого сохранения не видят</b>. Каждое наложение разбирается: заклинание без <c>MODIF_ID</c> безобидно;
    /// <c>spell_cast</c> с модификатором — отряд берётся по клетке из снимка до заклинания (<c>CastViolation</c>), и
    /// нарушение — бой с ним раньше следующего снимка;
    /// <c>wand_cast</c> с модификатором — нарушение, если в той же загрузке карты (<c>tid</c>) первым после него идёт бой
    /// с его целью, а не строка <c>save</c> снимка. Модификатор кладёт лишь <c>wand_cast</c> 247655 (<c>IG6012</c> →
    /// <c>SS0002</c>, <c>g000um0002</c>) на <c>S135KC002e</c> — и первым за ним идёт снимок 056.
    /// Ещё три вида безобидны по смыслу: <c>relation</c> (причина <c>trade_spell</c>), <c>trade_accept</c> (обмен
    /// заклинанием между игроками кладёт его в книгу) и <c>noble_action</c> (действие 11 — кража заклинания, тоже
    /// книга).</para>
    /// <para><b>Отслеженное наложение — не нарушение (18.09, зонд 7.0).</b> <c>spell_cast</c>, чьи модификатор и цели знает
    /// <see cref="D2.Data.Battle.SaveSnapshots"/> (<c>Tracks</c>: <c>AREA</c> 1 — отряд клетки, <c>AREA</c> 999 — отряды
    /// заклинателя из снимка до заклинания, правило сверено с сохранением после), загрузчик боя сам кладёт в цепочку
    /// целей до <c>spell_expire</c> заклинателя — модификатор доходит до боя учтённым. Нарушение остаётся за
    /// неотслеженными: области 3…9, наложения без снимка до них, посохи. Прежнее нарушение
    /// <c>passive-001</c> seq 15480877 («Зов предков», броня +33) было ложным: из двух боёв до снимка нападали лидеры
    /// <c>S073KC00b2</c>/<c>00b3</c>, нанятые ПОСЛЕ заклинания, — движок им модификатора не дал (журнал: броня 0), а
    /// защищалась крепость другого игрока.</para></summary>
    [SkippableFact]
    public void Corpus_SpellModifiers_NeverReachAnUnseenBattle()
    {
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        // Виды событий, где слово «заклинание» встречается и на отряд ничего не накладывается.
        string[] harmless = ["start", "counts", "site_buy_spell", "spell_expire", "spell_research", "relation",
                             "trade_accept", "noble_action",
                             // зонд 6.4: бюджет ИИ (поле spell) и проверка подарка (вид «заклинание»)
                             "ai_budget", "ai_gift_check",
                             // зонд 6.5 (20260918-probe65, разбор 17.09): списание бюджета ИИ несёт долю на заклинания
                             // (spell_start_before/after) — деньги, не наложение
                             "ai_budget_spend",
                             // зонд 6.5: ответ ИИ на предложение обмена (kind trade, object — заклинание): книга, не отряд
                             "ai_answer",
                             // зонд 7.0 (20260919-human): экраны и нажатия интерфейса — окно изучения заклинаний
                             // (DLG_RESEARCH_SPELL, BTN_RESEARCH_SPELL, «Исследовать заклинание … за … маны?»); игру не меняют
                             "ui_click", "ui_text", "ui_open", "ui_close", "ui_show", "ui_hide", "ui_msgbox",
                             // зонд 7.1 (20260928-shots): снимок F12 поверх того же окна (top DLG_RESEARCH_SPELL)
                             "screenshot",
                             // 20261014-replay ai-q29 (Sinkhole G000SS0119, два каста за день): щелчок по клетке карты в задаче
                             // CTaskCastSpell — ввод, игру не меняет; само наложение — строка spell_cast следом (map_click seq
                             // 441801 → spell_cast 441803, 441915 → 441917), её разбирает CastViolations
                             "map_click",
                             // зонд 7.5 (20261018b-port-synth): броски решений ИИ — место op_spell_start/op_spell_count,
                             // origin op_spell_* (выбор оперативного слоя 0x46e92c); бросок, не наложение
                             "ai_rng"];
        // Зонд 6.5: стратегические действия ИИ. Безобиден только класс изучения заклинания (spell_research,
        // eval_spell_research): все 25 ai_exec этого класса в probe65 парны 25 событиям spell_research (книга игрока).
        // Другой класс со следом заклинания (наложение ИИ) остаётся нарушением.
        string[] aiActions = ["ai_candidate", "ai_eval", "ai_exec"];
        // Дипломатические действия ИИ (dipl_spell — обмен заклинанием, dipl_offer, dipl_break; зонд 6.5, 20260918-probe65 и
        // 20260919-human): заклинание в них — предмет сделки (поле id8 записи действия), наложения нет.
        string[] aiResearch = ["\"class\":\"spell_research\"", "\"class\":\"eval_spell_research\"",
                               "\"class\":\"dipl_"];
        string[] casts = ["spell_cast", "wand_cast"];
        string[] needles = ["spell", "g000ss", "cast"];
        int files = 0, lines = 0, harmlessHits = 0, parsed = 0, tracked = 0;
        var bad = new List<string>();
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        ProgressBook? book = null;

        foreach (var path in CorpusFiles.Journals())
        {
            files++;
            bool hasCasts = false;
            foreach (var line in File.ReadLines(path))
            {
                lines++;
                if (!needles.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                string ev = EventOf(line);
                kinds[ev] = kinds.GetValueOrDefault(ev) + 1;
                if (casts.Contains(ev, StringComparer.Ordinal)) { hasCasts = true; continue; }
                // Зонд 7.3 (20261016e-av): звук, музыка, анимации, курсор, кадры — вывод, игру не меняют; след заклинания в них — верхний
                // экран (top DLG_RESEARCH_SPELL) и имена звуков (SPELLDIS), а не наложение.
                if (harmless.Contains(ev, StringComparer.Ordinal) || D2.Data.Diagnostics.AvJournal.Events.Contains(ev)
                    || (aiActions.Contains(ev, StringComparer.Ordinal) && aiResearch.Any(line.Contains)
                        && !line.Contains("cast", StringComparison.OrdinalIgnoreCase)))
                { harmlessHits++; continue; }
                bad.Add($"{Path.GetFileName(path)}: {line[..Math.Min(160, line.Length)]}");
            }
            if (hasCasts) parsed += CastViolations(path, book ??= ProgressBook.Load(GamePaths.Globals), bad, ref tracked);
        }

        output.WriteLine($"файлов {files}, строк {lines}, следов заклинаний {harmlessHits + parsed} " +
                         $"по видам: {string.Join(", ", kinds.Select(k => $"{k.Key}={k.Value}"))}; " +
                         $"наложений разобрано {parsed}, из них отслежено загрузчиком боя {tracked}, нарушений {bad.Count}");
        foreach (var f in bad.Take(5)) output.WriteLine("  НАРУШЕНИЕ: " + f);
        Assert.True(files > 0, "журналов не найдено — проход не состоялся");
        Assert.True(harmlessHits + parsed + bad.Count > 0, "ни одного следа заклинания — проход не состоялся");
        Assert.True(tracked > 0, "ни одно наложение не отслежено — проверка загрузчика боя не состоялась");
        Assert.Empty(bad);
    }

    /// <summary>Разбор наложений одного журнала; возвращает их число, нарушения дописывает в <paramref name="bad"/>.</summary>
    private static int CastViolations(string path, ProgressBook book, List<string> bad, ref int tracked)
    {
        string name = Path.GetFileName(path);
        if (!TreasuryJournal.HasBank(path)) { bad.Add($"{name}: наложения в журнале без казны — снимков нет, не разобрать"); return 0; }
        var j = TreasuryJournal.Load(path);
        int parsed = 0;
        foreach (var e in JournalCache.Read(path, ["spell_cast", "wand_cast"]).Where(e => e is SpellCastEvent or WandCastEvent))
        {
            parsed++;
            string? why = e switch
            {
                SpellCastEvent { Ret: true } s => ModifierOf(book, s.Spell) switch
                {
                    null => $"spell_cast {s.Spell}: заклинания нет в Gspells",
                    "" => null,
                    var mod => Tracked(path, s, book, ref tracked) ? null : CastViolation(j, book, s, mod),
                },
                WandCastEvent { Ret: true } w => WandViolation(j, book, w),
                _ => null,
            };
            if (why is not null) bad.Add($"{name} seq {e.Seq}: {why}");
        }
        return parsed;
    }

    /// <summary>Отслеживает ли наложение загрузчик боя (<see cref="D2.Data.Battle.SaveSnapshots.Tracks"/>): тогда
    /// модификатор доходит до боя учтённым, и это не нарушение.</summary>
    private static bool Tracked(string path, SpellCastEvent s, ProgressBook book, ref int tracked)
    {
        bool yes = D2.Data.Battle.SaveSnapshots.Of(path).At(s.Seq).Tracks(s.Seq, new D2.Data.Battle.MapEffectTables(book.Prices.Spells, book.Prices.Items));
        if (yes) tracked++;
        return yes;
    }

    private static string? ModifierOf(ProgressBook book, string spell) =>
        book.Prices.Spells.TryGetValue(spell, out var s) ? s.ModifierId : null;

    /// <summary>Нарушение <c>spell_cast</c> с модификатором или null. Журнал называет только клетку — отряд на ней берётся
    /// из снимка той же загрузки карты до заклинания; дальше как у посоха: нарушение, если бой с ним идёт раньше снимка.
    /// Зонд 7.0 (20260919-human): seq 15480841 G000SS0040 и 15480877 G000SS0088 — первые наложения с модификатором.</summary>
    private static string? CastViolation(TreasuryJournal j, ProgressBook book, SpellCastEvent s, string mod)
    {
        // AREA 999 — заклинание на всю карту (все отряды игрока-цели): клетка отряда не называет, и под ударом любой бой
        // той же загрузки до следующего снимка.
        if (book.Prices.Spells.TryGetValue(s.Spell, out var spell) && spell.Area >= WholeMapArea)
        {
            var next = j.Saves.Where(x => x.Seq > s.Seq).Select(x => x.Seq).DefaultIfEmpty(long.MaxValue).Min();
            var battles = j.Events.OfType<BattleStartEvent>().Where(b => b.Seq > s.Seq && b.Seq < next && b.Tid == s.Tid && b.Round == 0)
                .Select(b => b.Seq).ToList();
            return battles.Count == 0 ? null
                : $"spell_cast {s.Spell} ({mod}, AREA {spell.Area} — вся карта): бои seq {string.Join(",", battles)} раньше снимка";
        }
        var stack = j.SaveNear(s.T)?.Map.Stacks.Values.FirstOrDefault(x => x.X == s.X && x.Y == s.Y);
        if (stack is null) return $"spell_cast {s.Spell} с модификатором {mod}: на клетке ({s.X},{s.Y}) отряда в снимке нет";
        return BattleBeforeSave(j, s.Seq, s.Tid, stack.Id) is { } battle
            ? $"spell_cast {s.Spell} ({mod}) на {stack.Id}: бой seq {battle} раньше снимка" : null;
    }

    /// <summary><c>Gspells.AREA</c> заклинания на всю карту.</summary>
    private const int WholeMapArea = 999;

    /// <summary>Первый бой отряда <paramref name="group"/> после <paramref name="seq"/> в той же загрузке, если до него нет снимка.</summary>
    private static long? BattleBeforeSave(TreasuryJournal j, long seq, long tid, string group)
    {
        bool Target(string g) => JournalSaves.Same(g, group);
        var battle = j.Events.OfType<BattleStartEvent>()
            .FirstOrDefault(b => b.Seq > seq && b.Tid == tid && (Target(b.AttackerGroup) || Target(b.DefenderGroup)));
        return battle is null || j.Saves.Any(x => x.Seq > seq && x.Seq < battle.Seq) ? null : battle.Seq;
    }

    /// <summary>Нарушение <c>wand_cast</c> или null: заклинание посоха — из типа предмета в снимке своей загрузки.
    /// <para>Свиток, израсходованный раньше первого снимка своей загрузки (<c>20261016e-av</c>, карта 7, seq 13641504 <c>S143IM000c</c>),
    /// в снимках не найти, и его заклинание неизвестно: тогда нарушение возможно, только если бой цели идёт раньше снимка, — иначе
    /// модификатор до неувиденного боя не дошёл, какой бы он ни был.</para></summary>
    private static string? WandViolation(TreasuryJournal j, ProgressBook book, WandCastEvent w)
    {
        bool Target(string group) => JournalSaves.Same(group, w.Target);
        var battle = j.Events.OfType<BattleStartEvent>()
            .FirstOrDefault(b => b.Seq > w.Seq && b.Tid == w.Tid && (Target(b.AttackerGroup) || Target(b.DefenderGroup)));
        bool unseen = battle is not null && !j.Saves.Any(s => s.Seq > w.Seq && s.Seq < battle.Seq);
        if (j.SaveNear(w.T) is not { } save || book.ItemOf(save.Map, w.Item) is not { } staff)
            return w.Before != w.After ? $"wand_cast {w.Item}: предмета нет в снимке своей загрузки карты, а казна менялась — не свиток"
                 : unseen ? $"wand_cast {w.Item}: предмета нет в снимке своей загрузки карты, а бой seq {battle!.Seq} раньше снимка" : null;
        if (ModifierOf(book, staff.SpellId) is not { } mod)
            return $"wand_cast {w.Item} ({staff.Id}): заклинания {staff.SpellId} нет в Gspells";
        if (mod.Length == 0 || !unseen) return null;
        return $"wand_cast {w.Item} ({staff.Id} → {staff.SpellId}, {mod}) на {w.Target}: бой seq {battle!.Seq} раньше снимка";
    }

    /// <summary>Значение поля <c>ev</c> строки журнала без разбора JSON: строк в корпусе сотни
    /// тысяч, а нужно одно короткое поле.</summary>
    private static string EventOf(string line)
    {
        const string marker = "\"ev\":\"";
        int start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        start += marker.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? "" : line[start..end];
    }
}
