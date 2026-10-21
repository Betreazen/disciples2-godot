using D2.Data;
using D2.Data.Battle;
using D2.Data.Dbf;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

/// <summary>
/// Задача 10 фазы Ф6: предметы. Числа берутся из живой ванили — <c>Globals/GItem.DBF</c> через
/// <c>D2.Data.Magic.ItemLoader</c>, модификаторы через <c>ModifierLoader</c>, заклинания через
/// <c>SpellLoader</c>; синтетических предметов здесь нет. Игра не запускается.
/// <para>Утверждения ранга 1 — счётчики, распределения колонок и поимённые предметы; помеченное
/// рангом 3 в коде (потолок лечения зельем, смысл <c>UNIT_ID</c>) тестами не «доказывается», а лишь
/// закрепляется в том виде, в каком реализовано.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class ItemTests(ITestOutputHelper output)
{
    // ---- вход: настоящие предметы ванили --------------------------------------------------------

    private static readonly Lazy<IReadOnlyDictionary<string, Item>> Items =
        new(() => D2.Data.Magic.ItemLoader.Load(GamePaths.Globals));

    private static readonly Lazy<IReadOnlyDictionary<string, Spell>> Spells =
        new(() => D2.Data.Magic.SpellLoader.Load(GamePaths.Globals));

    private static readonly Lazy<D2.Data.Battle.ModifierTables> Modifiers =
        new(() => D2.Data.Battle.ModifierLoader.Load(GamePaths.Globals));

    private static IReadOnlyList<Item> All() => Items.Value.Values.ToList();

    private static Item One(string id) => Items.Value[id];

    // ---- 1. каталог ------------------------------------------------------------------------------

    /// <summary>Живых предметов 336 (в заголовке <c>.dbf</c> 339 — три записи удалены), и все
    /// пятнадцать категорий <c>LmagItm.dbf</c> заселены. Распределение — ранг 1 по данным.</summary>
    [SkippableFact]
    public void ItemCatalogue_MatchesVanillaCounts()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var items = All();
        Assert.Equal(336, items.Count);

        var expected = new (ItemCategory Category, int Count)[]
        {
            (ItemCategory.Armor, 7), (ItemCategory.Jewel, 8), (ItemCategory.Weapon, 16),
            (ItemCategory.Banner, 11), (ItemCategory.PotionBoost, 15), (ItemCategory.PotionHeal, 3),
            (ItemCategory.PotionRevive, 1), (ItemCategory.PotionPermanent, 5),
            (ItemCategory.Scroll, 120), (ItemCategory.Wand, 21), (ItemCategory.Valuable, 10),
            (ItemCategory.Orb, 43), (ItemCategory.Talisman, 41), (ItemCategory.TravelItem, 5),
            (ItemCategory.Special, 30),
        };
        foreach (var (category, count) in expected)
        {
            Assert.Equal(count, items.Count(i => i.Category == category));
            output.WriteLine($"{category,-16} {count}");
        }
        Assert.Equal(336, expected.Sum(p => p.Count));
        Assert.Equal(15, expected.Length);

        // Зелья — четыре категории, и ровно их движок считает зельями (0x5df8e8).
        Assert.Equal(24, items.Count(i => i.IsPotion));
    }

    /// <summary>Кросс-таблица «категория × заполненные колонки». Она и есть довод, что разбирать
    /// предмет надо по колонкам: какие колонки у категории есть, задаёт конструктор движка, а не
    /// подпись (см. <see cref="ItemCategory"/>).
    /// <para>Каждая строка проверяется в обе стороны: колонка обязана быть заполнена там, где
    /// конструктор её читает и данные её несут, и обязана быть пуста везде ещё.</para></summary>
    [SkippableFact]
    public void ItemColumns_MatchTheirConstructor()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        int checkedItems = 0;
        var filled = new Dictionary<string, int>();
        foreach (var i in All())
        {
            void Count(string name, bool present) { if (present) filled[name] = filled.GetValueOrDefault(name) + 1; }
            Count("MOD_EQUIP", i.EquipModifierId.Length > 0);
            Count("MOD_POTION", i.PotionModifierId.Length > 0);
            Count("HP_POTION", i.HealHitPoints != 0);
            Count("SPELL_ID", i.SpellId.Length > 0);
            Count("CASTING", i.CastingCost != Bank.Zero);
            Count("ATTACK_ID", i.AttackId.Length > 0);
            Count("UNIT_ID", i.UnitId.Length > 0);

            // Колонки, которых у категории нет вовсе, обязаны быть пусты.
            bool equipment = i.Category is ItemCategory.Armor or ItemCategory.Jewel or ItemCategory.Weapon
                                        or ItemCategory.Banner or ItemCategory.TravelItem;
            bool attackItem = i.Category is ItemCategory.Orb or ItemCategory.Talisman;
            bool potionWithModifier = i.Category is ItemCategory.PotionBoost or ItemCategory.PotionPermanent;

            if (!equipment) Assert.Equal("", i.EquipModifierId);
            if (!potionWithModifier) Assert.Equal("", i.PotionModifierId);
            if (i.Category is not ItemCategory.PotionHeal) Assert.Equal(0, i.HealHitPoints);
            if (i.Category is not (ItemCategory.Scroll or ItemCategory.Wand)) Assert.Equal("", i.SpellId);
            if (i.Category is not ItemCategory.Wand) Assert.Equal(Bank.Zero, i.CastingCost);
            if (!equipment && !attackItem) Assert.Equal("", i.AttackId);

            // А там, где колонка есть, у ванили она заполнена у всех.
            if (potionWithModifier) Assert.NotEqual("", i.PotionModifierId);
            if (i.Category is ItemCategory.PotionHeal) Assert.True(i.HealHitPoints > 0, i.Id);
            if (i.Category is ItemCategory.Scroll or ItemCategory.Wand) Assert.NotEqual("", i.SpellId);
            if (i.Category is ItemCategory.Wand) Assert.NotEqual(Bank.Zero, i.CastingCost);
            if (attackItem) Assert.NotEqual("", i.AttackId);
            // Экипировка: либо модификатор, либо атака. В ванили по данным взаимоисключающи, хотя
            // конструктор 0x59c98d читает оба слота — это ограничение данных, а не кода.
            if (equipment) Assert.True(i.EquipModifierId.Length > 0 ^ i.AttackId.Length > 0, i.Id);
            checkedItems++;
        }

        Assert.Equal(336, checkedItems);
        foreach (var (name, count) in new[] { ("MOD_EQUIP", 41), ("MOD_POTION", 20), ("HP_POTION", 3),
                                              ("SPELL_ID", 141), ("CASTING", 21), ("ATTACK_ID", 90),
                                              ("UNIT_ID", 108) })
        {
            Assert.Equal(count, filled.GetValueOrDefault(name));
            output.WriteLine($"{name,-11} заполнена у {count}");
        }
        output.WriteLine($"предметов проверено {checkedItems}");
    }

    // ---- 2. цена и цена применения ---------------------------------------------------------------

    /// <summary><c>VALUE</c> — та же строковая казна, что у заклинания (общий читатель движка
    /// <c>0x595adf</c>), и её распределение говорит больше подписи: все 336 строк пятивалютные,
    /// ненулевой бывает только золотая часть, а тридцать нулевых цен — ровно все сюжетные предметы
    /// (<see cref="ItemCategory.Special"/>), которые не продаются.</summary>
    [SkippableFact]
    public void ItemValue_ObeysEngineBankGrammar()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        const int fiveCurrencyLength = 29;   // движок: cmp eax, 0x1d (0x58637c)

        int rows = 0, nonGold = 0, sixCurrency = 0;
        foreach (var row in DbfTable.Read(Path.Combine(GamePaths.Globals, "GItem.dbf")).Rows)
        {
            var raw = row.GetString("VALUE");
            Assert.Equal(fiveCurrencyLength, raw.Length);
            var bank = Bank.Parse(raw);
            if ((bank.Infernal, bank.Life, bank.Death, bank.Runic, bank.Grove) != (0L, 0L, 0L, 0L, 0L))
                nonGold++;
            if (raw.Length > fiveCurrencyLength) sixCurrency++;
            rows++;
        }
        Assert.Equal((336, 0, 0), (rows, nonGold, sixCurrency));

        var free = All().Where(i => i.Value == Bank.Zero).ToList();
        Assert.Equal(30, free.Count);
        Assert.All(free, i => Assert.Equal(ItemCategory.Special, i.Category));
        Assert.Equal(306, All().Count(i => i.Value.Gold > 0));
        output.WriteLine($"строк цены проверено {rows}, немонетных {nonGold}, шестивалютных {sixCurrency}; " +
                         $"бесплатных {free.Count} (все Special), с золотом {306}");
    }

    /// <summary><c>CASTING</c> — своя колонка жезла, а не копия цены его заклинания.
    /// <para>Ранг 1 по данным: непуста ровно у 21 предмета (все <see cref="ItemCategory.Wand"/>),
    /// три из них шестивалютные (мана рощи — то самое ветвление <c>0x58637c</c>), золота нет ни
    /// разу. У девятнадцати жезлов цена совпадает с <c>Gspells.CASTING_C</c>, у двух — нет, и оба
    /// названы поимённо: вывести цену жезла из заклинания нельзя.</para></summary>
    [SkippableFact]
    public void WandCasting_IsItsOwnColumn_NotTheSpellsCost()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        int wands = 0, same = 0, sixCurrency = 0;
        var differ = new List<string>();
        foreach (var item in All().Where(i => i.Category == ItemCategory.Wand))
        {
            wands++;
            Assert.Equal(0, item.CastingCost.Gold);
            if (item.CastingCost.Grove > 0) sixCurrency++;
            var spell = ItemUse.SpellOf(item, Spells.Value)!;
            if (item.CastingCost == spell.CastingCost) same++; else differ.Add(item.Id);
        }
        Assert.Equal((21, 19, 3), (wands, same, sixCurrency));
        Assert.Equal(new[] { "G000IG6016", "G000IG6018" }, differ.OrderBy(s => s, StringComparer.Ordinal));

        // «Armageddon» стоит 150/150/300, а жезл берёт 200/200/400.
        Assert.Equal(new Bank(0, 0, 200, 200, 400, 0), One("G000IG6016").CastingCost);
        Assert.Equal(new Bank(0, 0, 150, 150, 300, 0), Spells.Value["g000ss0086"].CastingCost);

        output.WriteLine($"жезлов {wands}, цена совпала с заклинанием у {same}, разошлась у {differ.Count} " +
                         $"({string.Join(", ", differ)}), шестивалютных {sixCurrency}");
    }

    /// <summary>Оплата применения жезла: не та мана не выручает, казна не меняется на месте.
    /// Сравнение шести валют переиспользует <c>Economy.NotEnough</c> — своей арифметики нет.</summary>
    [SkippableFact]
    public void WandCasting_IsPaidFromTreasury()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var staff = One("G000IG6001");                         // «Staff of Thunder», y0100
        Assert.Equal(new Bank(0, 0, 100, 0, 0, 0), staff.CastingCost);

        var enough = new Bank(500, 0, 100, 0, 0, 0);
        Assert.True(ItemUse.CanPay(enough, staff));
        Assert.Equal(new Bank(500, 0, 0, 0, 0, 0), ItemUse.Pay(enough, staff));
        Assert.Equal(new Bank(500, 0, 100, 0, 0, 0), enough);   // прежняя казна не тронута

        var goldOnly = new Bank(100000, 0, 99, 0, 0, 0);        // золота вдоволь, маны жизни не хватает
        Assert.False(ItemUse.CanPay(goldOnly, staff));
        Assert.Throws<InvalidOperationException>(() => ItemUse.Pay(goldOnly, staff));

        // Свиток и всё, что не жезл, применяется даром.
        Assert.True(ItemUse.CanPay(Bank.Zero, One("G000IG5001")));
    }

    // ---- 3. эффекты ------------------------------------------------------------------------------

    /// <summary>Эффект собирается по заполненным колонкам. Ровно 296 предметов несут по одному
    /// эффекту, 40 — ни одного, и эти сорок — только сокровища и сюжетные вещи.</summary>
    [SkippableFact]
    public void Effects_ComeFromColumns_NotCategory()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var byKind = new Dictionary<ItemEffectKind, int>();
        int total = 0, empty = 0;
        foreach (var i in All())
        {
            var effects = ItemUse.Effects(i);
            Assert.True(effects.Count <= 1, $"{i.Id}: эффектов {effects.Count}");
            if (effects.Count == 0)
            {
                empty++;
                Assert.True(i.Category is ItemCategory.Valuable or ItemCategory.Special, i.Id);
                continue;
            }
            byKind[effects[0].Kind] = byKind.GetValueOrDefault(effects[0].Kind) + 1;
            total += effects.Count;
        }
        Assert.Equal((296, 40), (total, empty));

        foreach (var (kind, expected) in new[]
                 {
                     (ItemEffectKind.EquipModifier, 41), (ItemEffectKind.PotionModifier, 20),
                     (ItemEffectKind.Heal, 3), (ItemEffectKind.Revive, 1),
                     (ItemEffectKind.Spell, 141), (ItemEffectKind.Attack, 90),
                 })
        {
            Assert.Equal(expected, byKind.GetValueOrDefault(kind));
            output.WriteLine($"{kind,-15} {expected}");
        }
        Assert.Equal(6, byKind.Count);
        output.WriteLine($"предметов с эффектом {total}, без эффекта {empty} (Valuable 10 + Special 30)");

        // Воскрешение — единственный эффект ванили, у которого нет своей колонки: его несёт категория.
        Assert.Equal(new ItemEffect(ItemEffectKind.Revive), Assert.Single(ItemUse.Effects(One("G000IG0001"))));
    }

    // ---- 4. модификатор: та же цепочка, что у заклинания и варда --------------------------------

    /// <summary>Все 41 <c>MOD_EQUIP</c> и все 20 <c>MOD_POTION</c> разрешаются, и эффект доходит до
    /// <see cref="EffectiveStats"/> — своего механизма у предмета нет.</summary>
    [SkippableFact]
    public void ItemModifiers_ResolveAndReachTheChain()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var catalogue = Modifiers.Value.Modifiers;

        int equip = 0, potion = 0, without = 0;
        foreach (var i in All())
        {
            var mods = ItemUse.ModifiersOf(i, catalogue);
            if (mods.Count == 0) { without++; continue; }
            Assert.All(mods, m => Assert.NotEmpty(m.Elements));
            if (i.EquipModifierId.Length > 0) equip++;
            if (i.PotionModifierId.Length > 0) potion++;
        }
        Assert.Equal((41, 20, 275), (equip, potion, without));

        var unit = new UnitType("g000uu5130", 1, 65, 20, "g000aa0001", "g000000000", false, true, 10, 100);
        var attack = new AttackType("g000aa0001", 50, AttackClass.Damage, 80, 3, 25, 0, false, 0);
        Modifier Mod(string itemId) => ItemUse.ModifiersOf(One(itemId), catalogue).Single();

        // «Runestone» (броня, TYPE 5 Armor +10): 20 -> 30.
        Assert.Equal(30, EffectiveStats.Of(unit, [Mod("G000IG2001")]).Armor);
        // «Tome of Air» (украшение, TYPE 12 Immunity): источник 7 (Air), уровень 2 (Once).
        Assert.Equal(new ImmunityGrant(7, ImmunityLevel.Once),
                     Assert.Single(EffectiveStats.Of(unit, [Mod("G000IG4001")]).Immunities));
        // «Dwarven Bracer» (оружие, TYPE 4 QtyDamage +10 %): 25 -> 27 (усечение, 25*110/100 = 27).
        Assert.Equal(27, EffectiveStats.OfAttack(attack, [Mod("G000IG3001")]).Attack.QtyDamage);
        // «Banner of Striking» (знамя, TYPE 3 Power +10 %): 80 -> 88.
        Assert.Equal(88, EffectiveStats.OfAttack(attack, [Mod("G000IG1003")]).Attack.Power);
        // «Potion of Protection» (зелье усиления, TYPE 5 Armor +15): 20 -> 35.
        Assert.Equal(35, EffectiveStats.Of(unit, [Mod("G000IG0002")]).Armor);
        // «Quicksilver Potion» (постоянное зелье, TYPE 9 Initiative +10 %): 50 -> 55.
        Assert.Equal(55, EffectiveStats.OfAttack(attack, [Mod("G000IG0013")]).Attack.Initiative);
        // «Boots of Speed» (дорожный, TYPE 7 MoveAllowance +20 %): 20 -> 24. Подпись Gmodif.COMMENTS
        // обещает «+25% Move» и врёт — считаем по GmodifL, как установила задача 2.
        Assert.Equal(24, EffectiveStats.Of(unit, [Mod("G000IG8003")], moveAllowance: 20).MoveAllowance);
        // «Unholy Dagger» (оружие, TYPE 15 AttackDrain +25 пунктов вампиризма).
        Assert.Equal(25, EffectiveStats.OfAttack(attack, [Mod("G000IG3017")]).DrainPercent);

        output.WriteLine($"модификаторов разрешено: надетых {equip}, зельевых {potion}; " +
                         $"без модификатора {without}");
    }

    // ---- 5. поимённые предметы -------------------------------------------------------------------

    /// <summary>Десять поимённых предметов с конкретными числами — по одному на каждую ветвь
    /// разбора, включая обе «пустые» категории.</summary>
    [SkippableFact]
    public void NamedItems_HaveMeasuredNumbers()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);

        var runestone = One("G000IG2001");                     // «Runestone», броня
        Assert.Equal((ItemCategory.Armor, "g000um2201", ""), (runestone.Category, runestone.EquipModifierId, runestone.AttackId));
        Assert.Equal(new Bank(500, 0, 0, 0, 0, 0), runestone.Value);

        var dagger = One("G000IG3017");                        // «Unholy Dagger», оружие с модификатором
        Assert.Equal((ItemCategory.Weapon, "g000um2317"), (dagger.Category, dagger.EquipModifierId));

        var wightBlade = One("G000IG3018");                    // «Wight Blade», оружие с атакой
        Assert.Equal((ItemCategory.Weapon, "", "g000aa9018"),
                     (wightBlade.Category, wightBlade.EquipModifierId, wightBlade.AttackId));
        Assert.Equal(new Bank(5000, 0, 0, 0, 0, 0), wightBlade.Value);

        var healing = One("G000IG0005");                       // «Potion of Healing», 50 HP
        Assert.Equal((ItemCategory.PotionHeal, 50, "g000uu0012"),
                     (healing.Category, healing.HealHitPoints, healing.UnitId));
        Assert.Equal(new Bank(150, 0, 0, 0, 0, 0), healing.Value);

        var life = One("G000IG0001");                          // «Life Potion», воскрешение
        Assert.Equal((ItemCategory.PotionRevive, 0, "", "g000uu0150"),
                     (life.Category, life.HealHitPoints, life.PotionModifierId, life.UnitId));

        var scroll = One("G000IG5001");                        // «Air ward scroll»
        Assert.Equal((ItemCategory.Scroll, "g000ss0001", ""), (scroll.Category, scroll.SpellId, scroll.UnitId));
        Assert.Equal(Bank.Zero, scroll.CastingCost);

        var staff = One("G000IG6001");                         // «Staff of Thunder», жезл
        Assert.Equal((ItemCategory.Wand, "g000ss0004"), (staff.Category, staff.SpellId));

        // «Squire Orb» и «Squire Talisman» — по данным неотличимы: одна атака, одна цена, один юнит.
        // Различает их только движок (см. Item.IsDestroyedAfterBattle).
        var orb = One("G000IG9001");
        var talisman = One("G000IG9101");
        Assert.Equal((ItemCategory.Orb, "g000aa9101", "g000uu0153"), (orb.Category, orb.AttackId, orb.UnitId));
        Assert.Equal((ItemCategory.Talisman, "g000aa9101", "g000uu0153"),
                     (talisman.Category, talisman.AttackId, talisman.UnitId));
        Assert.Equal(orb.Value, talisman.Value);

        var ring = One("G000IG7001");                          // «Bronze Ring», сокровище
        Assert.Equal(new Bank(250, 0, 0, 0, 0, 0), ring.Value);
        Assert.Empty(ItemUse.Effects(ring));

        var totem = One("G000IG7101");                         // «Agshlisga Totem», сюжетный
        Assert.Equal((ItemCategory.Special, Bank.Zero), (totem.Category, totem.Value));
        Assert.Empty(ItemUse.Effects(totem));

        output.WriteLine("проверено 11 предметов поимённо");
    }

    // ---- 6. отказы зелья -------------------------------------------------------------------------

    /// <summary>Все восемь кодов <see cref="PotionResult"/> на настоящих зельях, в том порядке, в
    /// каком отвечает движок (<c>canApplyPotionToUnit</c>, Gog <c>0x5de495</c>, ранг 1 —
    /// дизассемблер, а не заголовок тулсета).</summary>
    [SkippableFact]
    public void CanApplyPotion_FollowsEngineOrder()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var boost = One("G000IG0002");        // «Potion of Protection», усиление
        var permanent = One("G000IG0013");    // «Quicksilver Potion», постоянное
        var heal = One("G000IG0005");         // «Potion of Healing», 50
        var revive = One("G000IG0001");       // «Life Potion»
        var scroll = One("G000IG5001");       // не зелье вовсе

        // 1: предмета нет в инвентаре — проверяется раньше всего, даже раньше «а зелье ли это».
        Assert.Equal(PotionResult.NoItemInInventory,
                     ItemUse.CanApplyPotion(scroll, inInventory: false, hp: 10, hpMax: 100));
        Assert.Equal(PotionResult.NoItemInInventory,
                     ItemUse.CanApplyPotion(heal, inInventory: false, hp: 10, hpMax: 100));

        // 8: не зелье. Зельями движок считает ровно четыре категории.
        Assert.Equal(PotionResult.NotAPotion, ItemUse.CanApplyPotion(scroll, true, 10, 100));
        Assert.Equal(PotionResult.NotAPotion, ItemUse.CanApplyPotion(One("G000IG2001"), true, 10, 100));

        // 9 и 10: усиление и постоянное. «Жив» — это hp > 0 (0x5e98ad).
        Assert.Equal(PotionResult.Ok, ItemUse.CanApplyPotion(boost, true, 10, 100));
        Assert.Equal(PotionResult.AlreadyApplied,
                     ItemUse.CanApplyPotion(boost, true, 10, 100, modifierAlreadyApplied: true));
        Assert.Equal(PotionResult.CannotBoostDead, ItemUse.CanApplyPotion(boost, true, 0, 100));
        Assert.Equal(PotionResult.CannotBoostDead,
                     ItemUse.CanApplyPotion(permanent, true, 0, 100, modifierAlreadyApplied: true));

        // 11 и 3: лечение. Мёртвый проверяется раньше полного здоровья.
        Assert.Equal(PotionResult.Ok, ItemUse.CanApplyPotion(heal, true, 99, 100));
        Assert.Equal(PotionResult.AlreadyAtFullHp, ItemUse.CanApplyPotion(heal, true, 100, 100));
        Assert.Equal(PotionResult.AlreadyAtFullHp, ItemUse.CanApplyPotion(heal, true, 120, 100));
        Assert.Equal(PotionResult.CannotHealDead, ItemUse.CanApplyPotion(heal, true, 0, 100));

        // 5: воскрешение. Живому нельзя, мёртвому можно — зеркало предыдущего случая.
        Assert.Equal(PotionResult.AlreadyAlive, ItemUse.CanApplyPotion(revive, true, 1, 100));
        Assert.Equal(PotionResult.Ok, ItemUse.CanApplyPotion(revive, true, 0, 100));

        // Значения 2, 4, 6, 7 в теле движка не встречаются — перечень ровно из восьми.
        Assert.Equal(new[] { 0, 1, 3, 5, 8, 9, 10, 11 },
                     Enum.GetValues<PotionResult>().Select(v => (int)v).OrderBy(v => v));
        output.WriteLine("восемь кодов отказа проверены на четырёх зельях и двух не-зельях");
    }

    /// <summary>Потолок лечения зельем — как у боевого лечения (ранг 3, см.
    /// <see cref="ItemUse.HealAmount"/>). Три лечебных зелья ванили: 50, 100, 200.</summary>
    [SkippableFact]
    public void HealPotions_AreCappedByMissingHitPoints()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var amounts = All().Where(i => i.Category == ItemCategory.PotionHeal)
                           .Select(i => i.HealHitPoints).OrderBy(v => v);
        Assert.Equal(new[] { 50, 100, 200 }, amounts);

        var big = One("G000IG0018");                           // «Potion of Healing», 200
        Assert.Equal(200, ItemUse.HealAmount(big, hp: 10, hpMax: 400));
        Assert.Equal(40, ItemUse.HealAmount(big, hp: 60, hpMax: 100));
        Assert.Equal(0, ItemUse.HealAmount(big, hp: 100, hpMax: 100));
        Assert.Equal(0, ItemUse.HealAmount(big, hp: 120, hpMax: 100));
    }

    // ---- 7. конец боя ----------------------------------------------------------------------------

    /// <summary>Что пропадает по окончании боя: <b>применённые</b> сфера и все четыре вида зелья.
    /// <para>Ранг 1 по <c>checkAndDestroyEquippedBattleItems</c> (Gog <c>0x629755</c>) на перечень
    /// категорий и по прогону <c>f7-m17-charges</c> (П§18) на «только применённое»: неприменённая
    /// сфера остаётся. Талисман здесь намеренно даёт <c>false</c> при любом признаке: в движке он
    /// живёт отдельной веткой и уничтожается на нуле зарядов — это считает
    /// <see cref="Charges.TalismanAfterBattle"/> (задача 10). Это единственная механическая разница
    /// сферы и талисмана: по данным они неотличимы (см. <c>NamedItems_HaveMeasuredNumbers</c>).</para></summary>
    [SkippableFact]
    public void DestroyedAfterBattle_CoversAppliedOrbAndPotions_ButNotTalisman()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var destroyed = All().Where(i => i.IsDestroyedAfterBattle(applied: true)).ToList();
        Assert.Equal(67, destroyed.Count);                     // 43 сферы + 24 зелья
        Assert.All(destroyed, i => Assert.True(i.IsPotion || i.Category == ItemCategory.Orb, i.Id));

        // Без применения не пропадает ни один предмет ванили — правило П§18, а не перечень категорий.
        Assert.DoesNotContain(All(), i => i.IsDestroyedAfterBattle(applied: false));

        Assert.True(One("G000IG9001").IsDestroyedAfterBattle(applied: true));    // применённая сфера
        Assert.False(One("G000IG9001").IsDestroyedAfterBattle(applied: false));  // та же, но в слоте
        Assert.False(One("G000IG9101").IsDestroyedAfterBattle(applied: true));   // талисман — заряды
        Assert.False(One("G000IG2001").IsDestroyedAfterBattle(applied: true));   // надетая броня остаётся
        output.WriteLine($"пропадает после боя с применением {destroyed.Count} предметов: сфер 43, " +
                         "зелий 24; без применения — ни одного; талисманов 41 — ветка зарядов");
    }

    // ---- 8. UNIT_ID: перенесён, но не смоделирован -----------------------------------------------

    /// <summary><c>UNIT_ID</c> — поле базовой записи каждого предмета, а не «призыв», как значилось
    /// в постановке задачи. Здесь закрепляется измеренное: у кого оно заполнено, сколько различных
    /// юнитов и что все ссылки разрешаются. Смысл поля помечен рангом 3 в
    /// <see cref="Item.UnitId"/> и снимается прогоном задачи 13.</summary>
    [SkippableFact]
    public void UnitId_IsBaseField_NotSummon()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var units = D2.Data.Battle.RulesLoader.Load(GamePaths.Globals).Units;

        var withUnit = All().Where(i => i.UnitId.Length > 0).ToList();
        Assert.Equal(108, withUnit.Count);
        Assert.All(withUnit, i => Assert.True(units.ContainsKey(i.UnitId), $"{i.Id} -> {i.UnitId}"));
        Assert.Equal(25, withUnit.Select(i => i.UnitId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Заполнено ровно у применяемых в бою: четыре категории зелий плюс сферы и талисманы.
        Assert.All(withUnit, i => Assert.True(
            i.IsPotion || i.Category is ItemCategory.Orb or ItemCategory.Talisman, i.Id));
        // И ни у одного свитка, жезла, надетой вещи, сокровища или сюжетного предмета.
        Assert.Equal(228, All().Count(i => i.UnitId.Length == 0));

        // Кто именно: юнит, чьё умение предмет изображает, — а не призываемый.
        Assert.Equal("g000uu0012", One("G000IG0005").UnitId);   // лечебное зелье -> Priest
        Assert.Equal("g000uu0150", One("G000IG0001").UnitId);   // воскрешение   -> Hierophant
        Assert.Equal("g000uu0161", One("G000IG0002").UnitId);   // усиление      -> Alchemist
        Assert.Equal("g000uu0153", One("G000IG9001").UnitId);   // сфера         -> Elementalist

        output.WriteLine($"UNIT_ID заполнен у {withUnit.Count} предметов (зелья 24, сферы 43, талисманы 41), " +
                         $"различных юнитов {withUnit.Select(i => i.UnitId).Distinct(StringComparer.OrdinalIgnoreCase).Count()}, " +
                         "разрешились все; предметов-призывов в ванили 0");
    }

    // ---- 9. сплошной проход по корпусу -----------------------------------------------------------

    /// <summary><b>Сторож, сменивший охраняемое ограничение (задача 16), а не снятый.</b>
    ///
    /// <para>Прежде он требовал, чтобы следа предмета в корпусе не было вовсе, и охранял этим
    /// настоящую границу: боевые харнессы собирали юнита из <c>Gunits</c> и уровневого роста и о
    /// надетых вещах не знали ничего. Журнал с <c>item_equip</c> означал бы прокачанного героя,
    /// посчитанного новобранцем, — и именно это давало 183 расхождения С1 на партии
    /// <c>20260910-passive/passive-004.jsonl</c>.</para>
    ///
    /// <para>Теперь харнесс о вещах знает — но не из журнала, а из сохранений рядом с ним
    /// (<see cref="SaveSnapshots"/>: <c>MODIF_ID</c> юнита несёт и способности лидера, и надетые
    /// предметы). Значит ограничение стало другим и звучит так: <b>журнал, в котором надевали
    /// предметы, годен для корпуса только тогда, когда рядом объявлены сохранения</b>. Их и
    /// проверяет сторож — по всем файлам, подходящим под маски <see cref="CorpusFiles.Journals"/>,
    /// а не только по принятым: журнал с вещами и без снимков обязан быть виден и обязан оказаться
    /// ЗА пределами корпуса.</para>
    ///
    /// <para>Пустой результат ничего не доказывает, поэтому тест падает и тогда, когда журналов с
    /// событиями предметов не нашлось вовсе: значит проход не состоялся (маска, каталог, кодировка),
    /// а не «всё чисто».</para></summary>
    [SkippableFact]
    public void Corpus_ItemTraces_HaveSavesBeside()
    {
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var admitted = CorpusFiles.Journals().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var withItems = new List<string>();
        var bad = new List<string>();
        int scanned = 0, outside = 0;

        foreach (var path in AllJournalFiles())
        {
            scanned++;
            int events = CorpusFiles.ItemEvents(path);
            if (events == 0) continue;
            int saves = SaveSnapshots.Of(path).Count;
            withItems.Add($"{Path.GetFileName(path)}: событий предметов {events}, сохранений рядом {saves}");
            if (saves > 0) continue;
            if (!admitted.Contains(path)) { outside++; continue; }
            bad.Add($"{Path.GetFileName(path)}: {events} событий предметов и ни одного сохранения рядом");
        }

        output.WriteLine($"файлов просмотрено {scanned}, из них с событиями предметов {withItems.Count}; " +
                         $"без сохранений и потому вне корпуса {outside}; нарушений {bad.Count}");
        foreach (var f in withItems) output.WriteLine("  " + f);
        foreach (var f in bad) output.WriteLine("  НАРУШЕНИЕ: " + f);
        Assert.True(scanned > 0, "журналов не найдено — проход не состоялся");
        Assert.True(withItems.Count > 0,
                    "ни одного журнала с событиями предметов — проверять нечего, проход не состоялся");
        Assert.Empty(bad);
    }

    /// <summary>Все файлы, подходящие под маски корпуса, — до отбора <see cref="CorpusFiles.Journals"/>.
    /// Сторожу нужны именно они: он проверяет, что отбор сработал, а не повторяет его.</summary>
    private static IEnumerable<string> AllJournalFiles() =>
        CorpusFiles.Dir.Length == 0 || !Directory.Exists(CorpusFiles.Dir) ? []
        : CorpusFiles.Masks
            .SelectMany(m => Directory.EnumerateFiles(CorpusFiles.Dir, m, SearchOption.AllDirectories))
            .Where(f => !(Path.GetFileName(Path.GetDirectoryName(f)) ?? "").StartsWith('_'))
            .OrderBy(f => f, StringComparer.Ordinal);
}
