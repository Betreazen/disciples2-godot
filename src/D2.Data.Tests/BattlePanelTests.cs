using D2.Data.Gfx;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Battle;
using D2.Rules.Units;

namespace D2.Data.Tests;

/// <summary>Портрет юнита в панели боя (<see cref="BattlePanel"/>): подкраска по состоянию и ранам, значки и их места.</summary>
[Trait("Category", "Corpus")]
public class BattlePanelTests
{
    private static string Rules => Path.Combine(AppContext.BaseDirectory, "data", "rules");
    private static readonly Lazy<GameBook> Book = new(() => GameBook.Load(Rules));

    private static BattleUnit Unit(int hp = 100, bool attacker = true, IReadOnlyList<Modifier>? modifiers = null)
    {
        var rules = Book.Value.Rules;
        var type = rules.Units["G000UU0001"];
        return new BattleUnit
        {
            Id = "U", Type = type, Attack = rules.AttackOf(type)!, Attacker = attacker, Slot = 0, HpMax = 100, Hp = hp,
            Modifiers = modifiers ?? [],
        };
    }

    private static DotEffect Dot(AttackClass kind) => new() { Class = kind, Damage = 10, AppliedRound = 1, Long = false };

    [Fact]
    public void Tint_ByStateAndWounds()
    {
        Assert.Equal((null, true), BattlePanel.Tint(Unit()));
        Assert.Equal(((UiColor?)new UiColor(0, 0, 0), false), BattlePanel.Tint(Unit(hp: 0)));
        var poisoned = Unit();
        poisoned.Dots.Add(Dot(AttackClass.Poison));
        Assert.Equal(new UiColor(128, 128, 0), BattlePanel.Tint(poisoned).Whole);
        poisoned.Paralyzed = true;   // паралич раньше яда в таблице движка
        Assert.Equal(new UiColor(128, 128, 128), BattlePanel.Tint(poisoned).Whole);
        var stone = Unit();
        stone.Petrified = true;      // окаменение без паралича не красит (ошибка движка повторена)
        Assert.Null(BattlePanel.Tint(stone).Whole);

        Assert.Equal(85, BattlePanel.KeptRows(Unit(), 85));
        Assert.Equal(51, BattlePanel.KeptRows(Unit(hp: 60), 85));
        Assert.Equal(0, BattlePanel.KeptRows(Unit(hp: 0), 85));
    }

    /// <summary>Потолок здоровья на экране — <c>CMidUnit::getHitPoints</c> <c>0x5e987c</c>: Бес из гнома 250 (превращение другого, здоровье
    /// сохраняется) — <c>175/250</c> (снимок оригинала 19.10), не <c>175/50</c>; понижение уровня (без сохранения) — потолок нового типа.</summary>
    [Fact]
    public void ShownHpMax_TransformOtherKeepsFormerCeiling()
    {
        var rules = Book.Value.Rules;
        BattleUnit Transformed(bool keepHp)
        {
            var unit = Unit(hp: 175);
            (unit.OrigType, unit.OrigHpMax, unit.TransformKeepHp) = (unit.Type, 250, keepHp);   // как LifeAttacks.Retype: гном 250 → Бес
            (unit.Type, unit.HpMax) = (rules.Units["G000UU5034"], 50);
            return unit;
        }
        var imp = Transformed(keepHp: true);
        Assert.Equal(250, BattlePanel.ShownHpMax(imp));
        Assert.Equal(85 * 70 / 100, BattlePanel.KeptRows(imp, 85));
        Assert.Equal(50, BattlePanel.ShownHpMax(Transformed(keepHp: false)));
        Assert.Equal(100, BattlePanel.ShownHpMax(Unit()));
    }

    /// <summary>Рамки целей <c>HEA</c> — только у шести классов <c>0x630890</c>; у Двойника, превращения себя и призыва — <c>SEL</c> (красные,
    /// снимок оригинала 19.10).</summary>
    [Theory]
    [InlineData(AttackClass.Heal, true)]
    [InlineData(AttackClass.Cure, true)]
    [InlineData(AttackClass.BoostDamage, true)]
    [InlineData(AttackClass.Revive, true)]
    [InlineData(AttackClass.BestowWards, true)]
    [InlineData(AttackClass.GiveAttack, true)]
    [InlineData(AttackClass.Doppelganger, false)]
    [InlineData(AttackClass.TransformSelf, false)]
    [InlineData(AttackClass.Summon, false)]
    [InlineData(AttackClass.Damage, false)]
    public void SupportFrame_IsEngineList(AttackClass cls, bool hea) => Assert.Equal(hea, BattleScene.SupportFrame(cls));

    /// <summary>Павший: маска-череп <c>MASKDEAD{S|L}</c> (<c>0x5b1698</c>, состояние 10) смешивается с портретом наполовину вместо чёрного
    /// (<c>0x5b1c12</c>: при маске цвет смешения — точка маски); прозрачная точка портрета не трогается; живой — без маски, раны красным
    /// (пункт 3 прогона 2, снимок 34).</summary>
    [Fact]
    public void Shade_DeadPortraitTakesSkullMaskInsteadOfBlack()
    {
        Assert.Equal("MASKDEADS", BattlePanel.DeadMask(Unit(hp: 0), big: false));
        Assert.Equal("MASKDEADL", BattlePanel.DeadMask(Unit(hp: 0), big: true));
        Assert.Null(BattlePanel.DeadMask(Unit(), big: false));

        byte[] face = [200, 100, 50, 255, 10, 20, 30, 0];          // 2×1: непрозрачная и прозрачная точки
        byte[] skull = [255, 255, 255, 255, 255, 255, 255, 255];
        var (whole, _) = BattlePanel.Tint(Unit(hp: 0));
        BattlePanel.Shade(face, 2, whole, kept: 1, skull, maskWidth: 2, maskHeight: 1);
        Assert.Equal(new byte[] { 227, 177, 152, 255, 10, 20, 30, 0 }, face);   // (200+255)/2 …, не (200+0)/2

        byte[] black = [200, 100, 50, 255];
        BattlePanel.Shade(black, 1, whole, kept: 1);                          // маски нет — как прежде, чёрным наполовину
        Assert.Equal(new byte[] { 100, 50, 25, 255 }, black);

        byte[] wounded = [200, 100, 50, 255, 200, 100, 50, 255];              // 1×2: нижняя строка — раны
        BattlePanel.Shade(wounded, 1, null, kept: 1);
        Assert.Equal(new byte[] { 200, 100, 50, 255, 227, 50, 25, 255 }, wounded);
    }

    /// <summary>Оберег атаки (класс BestowWards — Лесная Дева) — значок заклинания-усиления <c>FIISOSPELLBOOST</c> слева вверху: модификатор
    /// оберега у движка в цепочке юнита и совпадает с модификатором заклинания «Защита от …» (<c>0x5ea6c1</c>). Снят оберег — значка нет.</summary>
    [Fact]
    public void Icons_WardFromAttack_ShowsSpellBoost()
    {
        var rules = Book.Value.Rules;
        var marks = BattleMarkSets.Of(Book.Value);
        var wardType = rules.Units.Values.First(t => rules.AttackOf(t) is { Class: AttackClass.BestowWards } a && rules.WardsOf(a).Count > 0);
        var caster = new BattleUnit { Id = "C", Type = wardType, Attack = rules.AttackOf(wardType)!, Attacker = true, Slot = 1, HpMax = 100, Hp = 100 };
        var target = Unit();
        Assert.DoesNotContain(BattlePanel.Icons(target, false, marks, false, 0), i => i.Name == "FIISOSPELLBOOST");
        Assert.True(Immunity.Bestow(target, rules.WardsOf(caster.Attack), caster) > 0);
        Assert.Contains((13, "FIISOSPELLBOOST"), BattlePanel.Icons(target, false, marks, false, 0));
        Immunity.ClearWards(caster);
        Assert.DoesNotContain(BattlePanel.Icons(target, false, marks, false, 0), i => i.Name == "FIISOSPELLBOOST");
    }

    /// <summary>Значки высокого уровня и повышения в панели боя — по юниту карты (<c>0x64c000</c>: <c>0x61ea4c</c> — уровень сверх уровня типа,
    /// пороги 5/10/15; <c>0x61e6d9</c> — готов к повышению): прежде панель боя их не рисовала никогда. Призванного на карте нет — значков нет.</summary>
    [SkippableFact]
    public void BattleLevelMarks_FromMapUnit()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = D2.Data.Map.MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks),
                                      SharedContent.Load(Path.Combine(AppContext.BaseDirectory, "data")).MapArt, Book.Value);
        var stack = session.Map.Stacks.Values.First(s => s.UnitIds.Count > 0 && session.Map.Units.ContainsKey(s.UnitIds[0]));
        var unit = session.Map.Units[stack.UnitIds[0]];
        int typeLevel = Book.Value.Rules.Units[unit.TypeId].Level;
        var units = session.Map.Units.ToDictionary(u => u.Key, u => u.Value, StringComparer.OrdinalIgnoreCase);
        units[unit.Id] = unit with { Level = typeLevel + 10 - (typeLevel == 1 ? 1 : 0) };
        var leveled = new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(session.Map, units: units) } },
                                      session.Art, session.Book);
        var context = new WindowContext(leveled, stack.Owner, UiStrings.Load(Path.Combine(AppContext.BaseDirectory, "data", "ui"), "ru"),
                                        UnitFaces.Load(Rules));
        var (upgrade, high) = UnitCellsView.BattleLevelMarks(context, unit.Id);
        Assert.Equal(2, high);
        Assert.Equal(UnitCellsView.ReadyToUpgrade(context, leveled.Map.Units[unit.Id], stack.Owner), upgrade);
        Assert.Equal((false, 0), UnitCellsView.BattleLevelMarks(context, "S000UN0fff"));
        var battle = Unit();
        Assert.Contains((24, "FIHIGHLEVEL2"), BattlePanel.Icons(battle, false, BattleMarkSets.None, upgrade, high));
    }

    [Fact]
    public void Icons_OrderNamesAndPlaces()
    {
        var unit = Unit(attacker: false, modifiers: [new Modifier("M_SPELL", [])]);
        unit.BoostDamageLevel = 2;
        unit.Paralyzed = true;
        unit.Defending = true;
        unit.Retreating = true;
        unit.Dots.Add(Dot(AttackClass.Poison));
        var marks = new BattleMarkSets(new HashSet<string> { "M_SPELL" }, new HashSet<string>(), new HashSet<string>());
        var icons = BattlePanel.Icons(unit, big: false, marks, upgrade: false, highLevel: 2);
        Assert.Equal(["FIBOOST2", "FIPARALYZE", "FIPOISON", "FIISOSPELLBOOST", "FIRETREATSD", "FIDEFENDING", "FIHIGHLEVEL2"],
                     icons.Select(i => i.Name));
        Assert.Empty(BattlePanel.Icons(Unit(hp: 0), false, marks, true, 3));

        (int, int)? Size(string name) => name == "FIHIGHLEVEL2" ? (15, 15) : (23, 28);
        var placed = BattlePanel.Place(icons, 70, 85, Size, rightRetreat: true);
        Assert.Equal(new BattlePanelIcon("FIBOOST2", 47, 57), placed[0]);      // стопка у правого края снизу вверх
        Assert.Equal(new BattlePanelIcon("FIPARALYZE", 47, 29), placed[1]);
        Assert.Equal(new BattlePanelIcon("FIPOISON", 47, 1), placed[2]);
        Assert.Equal(new BattlePanelIcon("FIISOSPELLBOOST", 0, 0), placed[3]);
        Assert.Equal(new BattlePanelIcon("FIRETREATSD", 47, 57), placed[4]);
        Assert.Equal(new BattlePanelIcon("FIDEFENDING", 23, 57), placed[5]);
        Assert.Equal(new BattlePanelIcon("FIHIGHLEVEL2", 27, 0), placed[6]);

        // Четвёртый в стопке не влезает (28·4 > 85) — пропуск; кадра нет — значка нет.
        var many = new List<(int, string)> { (0, "A"), (5, "B"), (9, "C"), (12, "D"), (13, "NONE") };
        var stacked = BattlePanel.Place(many, 70, 85, n => n == "NONE" ? null : (23, 28), rightRetreat: false);
        Assert.Equal(["A", "B", "C"], stacked.Select(p => p.Name));
    }
}
