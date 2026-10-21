using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Menu;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Окно армии, третий круг: одинаковые предметы в ячейке со счётчиком, имя города, «Остановить продвижение», герой
/// задания (<c>docs/ui-windows.md</c> §6).</summary>
[Trait("Category", "Corpus")]
public partial class CityWindowTests
{
    /// <summary>Два предмета одного типа на складе — одна ячейка с «2»; тянется последний, в ячейке остаётся «1».</summary>
    [SkippableFact]
    public void Items_SameTypeFoldIntoCountedCell()
    {
        var (session, fort) = CapitalWithGuest();
        var pair = session.Map.ItemTypes.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() >= 2);
        Skip.If(pair is null, "на карте нет двух предметов одного типа");
        string[] ids = [.. pair!.Take(2).Select(p => p.Key)];
        var sites = session.Map.Sites.Select(s => s.Id == fort ? s with { Items = ids } : s).ToList();
        session = Rebuilt(session, GameSession.With(session.Map, sites: sites));
        var (logic, ui) = Window(session, fort);
        var cell = Assert.Single(logic.StoreCells);
        Assert.Equal(ids, cell.Ids);
        var card = Assert.Single(ui.Cards("MLBOX_RINVENTORY"));
        output.WriteLine(card.Text);
        Assert.EndsWith("2  ", card.Text);

        int bag = logic.BagItems.Count;
        logic.Press(484 + 10, 404 + 10, ui);
        logic.Release(40 + 10, 404 + 10, ui);
        Assert.Null(logic.LastRefusal);
        Assert.Contains(ids[1], logic.BagItems);
        Assert.Equal(bag + 1, logic.BagItems.Count);
        Assert.EndsWith("1  ", Assert.Single(ui.Cards("MLBOX_RINVENTORY")).Text);   // и «1» рисуется (ответ пользователя 26.09)
    }

    [SkippableFact]
    public void CityName_FromScenario()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        output.WriteLine(logic.FortName);
        Assert.NotEmpty(logic.FortName);
        Assert.Equal(logic.FortName, ui.TextOf("TXT_CITYNAME"));
    }

    /// <summary>«Остановить продвижение»: воин гарнизона — вопрос и флаг навсегда, второй раз — «уже не нужно»; герой — отказ.</summary>
    [SkippableFact]
    public void StopGrowth_AsksOnceRefusesHero()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(fort)!;
        string hired = cells.Units[^1];
        var at = Cell("R", cells.PositionOf(hired), left: false, big: IsBig(cells, hired));
        logic.Handle(Toggle(ui, "TOG_DYNLEVEL"), ui);
        Assert.Equal(CityTool.StopGrowth, logic.Tool);
        Click(logic, ui, at);
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        output.WriteLine(ask.Text);
        Assert.Contains("остановил", ask.Text);
        Assert.Equal(CityTool.None, logic.Tool);
        ask.Yes();
        Assert.True(session.Map.Units[hired].DynLevel);

        logic.Handle(Toggle(ui, "TOG_DYNLEVEL"), ui);
        Click(logic, ui, at);
        Assert.Equal(_c!.Text("X005TA0939"), Assert.IsType<UiTell>(logic.TakeRequest()).Text);

        string guest = session.Map.Sites.First(s => s.Id == fort).StackId;
        var guestCells = session.GroupCells(guest)!;
        string leader = session.Map.Stacks[guest].LeaderId;
        logic.Handle(Toggle(ui, "TOG_DYNLEVEL"), ui);
        Click(logic, ui, Cell("L", guestCells.PositionOf(leader), left: true, big: IsBig(guestCells, leader)));
        Assert.Equal(_c!.Text("X005TA0936"), Assert.IsType<UiTell>(logic.TakeRequest()).Text);
    }

    /// <summary>Герой, чей отряд назван в условии события с «Победой» последним эффектом, не отпускается (<c>X005TA0883</c>).</summary>
    [SkippableFact]
    public void Dismiss_QuestHeroRefused()
    {
        var (session, fort) = CapitalWithGuest();
        string guest = session.Map.Sites.First(s => s.Id == fort).StackId;
        string human = session.Map.Stacks[guest].Owner;
        var quest = new ScenarioEvent("E_TEST", true, true, 100, 0, [], [new StackExistsCondition(guest, 0)], [new WinEffect(true, human)]);
        var events = session.State.Events with { Events = [.. session.State.Events.Events, quest] };
        session = new GameSession(session.State with { Events = events }, session.Art, session.Book);
        var (logic, ui) = Window(session, fort);
        string leader = session.Map.Stacks[guest].LeaderId;
        Assert.True(session.NeededByQuest(guest, leader));
        Assert.False(session.NeededByQuest(fort, session.GroupCells(fort)!.Units[^1]));
        var cells = session.GroupCells(guest)!;
        logic.Handle(Toggle(ui, "TOG_DISMISS"), ui);
        Click(logic, ui, Cell("L", cells.PositionOf(leader), left: true, big: IsBig(cells, leader)));
        Assert.Equal(_c!.Text("X005TA0883"), Assert.IsType<UiTell>(logic.TakeRequest()).Text);
        Assert.Null(logic.TakeRequest());
    }

    /// <summary>Значки ячейки зеркальны (0x56e2e7): у гостя слева повышение справа вверху, у гарнизона — слева; усиление и
    /// ослабление — по модификаторам заклинаний в цепочке воина.</summary>
    [SkippableFact]
    public void Cells_MirroredIconsAndSpellMarks()
    {
        var (session, fort) = CapitalWithGuest();
        var book = session.Book!;
        string hired = session.GroupCells(fort)!.Units[^1];
        string boost = book.Spells.Values.First(s => s.Category == D2.Rules.Magic.SpellCategory.Boost && s.ModifierId.Length > 0).ModifierId;
        string lower = book.Spells.Values.First(s => s.Category == D2.Rules.Magic.SpellCategory.Lower && s.ModifierId.Length > 0).ModifierId;
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[hired] = units[hired] with { Modifiers = [.. units[hired].Modifiers, boost, lower] };
        session = Rebuilt(session, GameSession.With(session.Map, units: units));
        var (_, ui) = Window(session, fort);

        Rect Of(string id) => ui.Screen.Elements.First(e => e.Id == id).Rect is var r ? new Rect(r.X, r.Y) : default;
        var guestCell = Of(UnitGroupLayout.CellImage("L", 0));
        var garrisonCell = Of(UnitGroupLayout.CellImage("R", 0));
        Assert.Equal(guestCell.X + 70 - 23, Of(UnitGroupLayout.Icon("L", "0", UnitGroupLayout.UpgradeIcon)).X);
        Assert.Equal(guestCell.X, Of(UnitGroupLayout.Icon("L", "0", UnitGroupLayout.BoostIcon)).X);
        Assert.Equal(garrisonCell.X, Of(UnitGroupLayout.Icon("R", "0", UnitGroupLayout.UpgradeIcon)).X);
        Assert.Equal(garrisonCell.X + 70 - 23, Of(UnitGroupLayout.Icon("R", "0", UnitGroupLayout.LowerIcon)).X);
        Assert.Equal(garrisonCell.Y + 85 - 28, Of(UnitGroupLayout.Icon("R", "0", UnitGroupLayout.LowerIcon)).Y);
        Assert.Equal(guestCell.X + 28, Of(UnitGroupLayout.Icon("L", "0", UnitGroupLayout.HighLevelIcon)).X);

        var cells = session.GroupCells(fort)!;
        string key = IsBig(cells, hired) ? $"B{cells.PositionOf(hired) / 2}" : $"{cells.PositionOf(hired)}";
        Assert.Equal("Battle:FIISOSPELLBOOST", ui.Frame(UnitGroupLayout.Icon("R", key, UnitGroupLayout.BoostIcon)));
        Assert.Equal("Battle:FIISOSPELLLOWER", ui.Frame(UnitGroupLayout.Icon("R", key, UnitGroupLayout.LowerIcon)));
    }

    private readonly record struct Rect(int X, int Y);

    /// <summary>Имя героя по умолчанию (0x4b02f3): русские имена расы и пола, из наименее занятых лидерами на карте.</summary>
    [Fact]
    public void LeaderNames_RussianLeastTaken()
    {
        var list = LeaderNameList.Load(Path.Combine(Data, "ui"), "ru");
        string first = list.Suggest("G000RR0003", male: true, [], () => 0)!;
        output.WriteLine(first);
        Assert.Matches("^[А-ЯЁ]", first);
        string second = list.Suggest("G000RR0003", male: true, [first], () => 0)!;
        Assert.NotEqual(first, second);   // занятое имя уступает свободным
        Assert.NotNull(list.Suggest("g000rr0003", male: false, [], () => 5));
        Assert.Null(list.Suggest("G000RR9999", male: true, [], () => 0));
    }

    /// <summary>Окно построек: у постройки отрядов — «Тренируется» и «В вашей армии»; выбор постройки «можно» красит «другим путём»
    /// соседнюю постройку ветки со статусом «можно», до следующего выбора.</summary>
    [SkippableFact]
    public void Build_UnitLinesAndSiblingShade()
    {
        var (session, _) = Game();
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        _c = new WindowContext(session, human.Id, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")))
        {
            Rules = RulesTexts.Load(Path.Combine(Data, "rules")),
        };
        var screens = Screens();
        var logic = new BuildLogic(_c, UiFlows.Load(Path.Combine(Data, "ui", "flow.json")), screens, BuildLogic.LoadTexts(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(logic.Shape(screens.First(s => s.Id == BuildLogic.Screen)));
        logic.Open(ui);
        var buildings = session.Book!.Economy.Buildings;
        string? Visible(Func<string, bool> fits) => session.Buildable(human.Id).FirstOrDefault(b => fits(b)
            && ui.Screen.Elements.Any(e => e.Id == "IMG_" + b[7..].TrimStart('0') && !ui.IsHidden(e.Id)));
        void Select(string b)
        {
            var r = ui.Screen.Elements.First(e => e.Id == "IMG_" + b[7..].TrimStart('0')).Rect;
            logic.Release(r.X + 5, r.Y + 5, ui);
        }

        string? pair = Visible(b => logic.Icon(b) == BuildIcon.Can && buildings.Values.Any(o => Siblings(b, o.Id) && logic.Icon(o.Id) == BuildIcon.Can));
        Skip.If(pair is null, "нет двух соседних построек «можно»");
        string sibling = buildings.Values.First(o => Siblings(pair!, o.Id) && logic.Icon(o.Id) == BuildIcon.Can).Id;
        Select(pair!);
        output.WriteLine($"{pair} → {sibling}: {logic.Icon(sibling)} | {ui.TextOf("TXT_UPGRADED")} | {ui.TextOf("TXT_UPGRADING")} | {ui.TextOf("TXT_UPGRADE_INFO")}");
        Assert.Equal(BuildIcon.OtherPath, logic.Icon(sibling));
        Assert.StartsWith("IconBld:" + sibling.ToUpperInvariant() + "#000000", ui.Frame("IMG_" + sibling[7..].TrimStart('0')), StringComparison.OrdinalIgnoreCase);
        Select(sibling);
        Assert.Equal(BuildIcon.OtherPath, logic.Icon(pair!));
        Assert.Equal(BuildIcon.Can, logic.Icon(sibling));

        string? unitBuilding = Visible(b => session.Book.UpgradeBuilding.Values.Contains(b, StringComparer.OrdinalIgnoreCase));
        Skip.If(unitBuilding is null, "нет видимой постройки отрядов");
        Select(unitBuilding!);
        output.WriteLine($"{ui.TextOf("TXT_UPGRADED")} | {ui.TextOf("TXT_UPGRADING")} | {ui.TextOf("TXT_UPGRADE_INFO")}");
        Assert.NotEmpty(ui.TextOf("TXT_UPGRADED")!);
        Assert.StartsWith(_c.Text("X005TA0282")[..10], ui.TextOf("TXT_UPGRADE_INFO"));

        // Правая кнопка по значку — энциклопедия постройки (DLG_R_C_BUILDING): воин постройки и из кого он растёт.
        var r2 = ui.Screen.Elements.First(e => e.Id == "IMG_" + unitBuilding![7..].TrimStart('0')).Rect;
        logic.RightPress(r2.X + 5, r2.Y + 5, ui);
        var card = Assert.IsType<BuildingCardLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        var cardUi = new UiScreenState(((IWindowLogic)card).Shape(screens.First(s => s.Id == BuildingCardLogic.Screen)));
        card.Open(cardUi);
        output.WriteLine(card.Summary + " | " + cardUi.TextOf("TXT_BUILDING_NAME"));
        Assert.StartsWith(@"\fLarge;", cardUi.TextOf("TXT_BUILDING_NAME"));
        Assert.NotNull(card.To);
        Assert.NotNull(cardUi.Frame("IMG_FROM"));
        Assert.Equal(card.From is null, cardUi.IsHidden("IMG_ARROW"));

        // Правая кнопка по портрету воина постройки (IMG_FACE, «Для деталей — правый клик», 0x4ac52d) — карточка его типа (замечание 20).
        var face = ui.Screen.Elements.First(e => e.Id == "IMG_FACE").Rect;
        logic.RightPress(face.X + (face.Width / 2), face.Y + (face.Height / 2), ui);
        var unitCard = Assert.IsType<UnitCardLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        var unitUi = new UiScreenState(((IWindowLogic)unitCard).Shape(screens.First(s => s.Id == UnitCardLogic.Screen)));
        unitCard.Open(unitUi);
        string unitType = session.Book.UpgradeBuilding.First(p => p.Value.Equals(unitBuilding, StringComparison.OrdinalIgnoreCase)).Key;
        output.WriteLine($"{unitCard.Summary} | {unitUi.TextOf("TXT_UNIT_NAME")} | {unitUi.TextOf("TXT_STATS")?[..Math.Min(60, unitUi.TextOf("TXT_STATS")!.Length)]}");
        Assert.Equal(unitType, unitCard.TypeId, StringComparer.OrdinalIgnoreCase);
        // Карточка воина — картина во весь рост по базовому типу (0x5b891e; группа B2 прогона 11.10), не круглый портрет FACEB.
        Assert.Equal(_c.Faces.Picture(unitType), unitUi.Frame("IMG_UNIT_ICON"));
        Assert.NotEmpty(unitUi.TextOf("TXT_STATS")!);
        Assert.NotEmpty(unitUi.TextOf("TXT_ATTACK_INFO")!);

        bool Siblings(string a, string b) => !a.Equals(b, StringComparison.OrdinalIgnoreCase) && buildings.TryGetValue(a, out var x)
            && buildings.TryGetValue(b, out var y) && x.Branch == y.Branch && x.Category == y.Category
            && x.RequiredId.Equals(y.RequiredId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Портрет воина постройки — в рамке, как у карточки отряда (пометка 1 партии 15.10): малый — <c>BORDERUNITSMALL</c> на месте
    /// <c>IMG_FACE</c> (<c>0x5b680b</c>), большой — <c>BORDERUNITLARGE</c> (<c>0x5b6870</c>), место портрета — во всю ширину рамки от левого
    /// края, подпись «Для деталей — правый клик» правее на ту же величину, правый край на месте (<c>0x4abb08</c>…<c>0x4abbd7</c>); правая кнопка
    /// по широкой части — карточка воина.</summary>
    [SkippableFact]
    public void Build_UnitFaceFramedBySize()
    {
        var (session, _) = Game();
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        _c = new WindowContext(session, human.Id, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")))
        {
            Rules = RulesTexts.Load(Path.Combine(Data, "rules")),
        };
        var screens = Screens();
        var logic = new BuildLogic(_c, UiFlows.Load(Path.Combine(Data, "ui", "flow.json")), screens, BuildLogic.LoadTexts(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(logic.Shape(screens.First(s => s.Id == BuildLogic.Screen)));
        logic.Open(ui);
        var book = session.Book!;
        UiRect Rect(string id) => ui.Screen.Elements.First(e => e.Id == id).Rect;
        var group = ui.Screen.Elements.First(e => e.Id == "RAD_BUILDING_TYPE").Members;
        var found = new Dictionary<bool, string>();
        foreach (string toggle in group)
        {
            logic.Handle(new UiGroupChanged("RAD_BUILDING_TYPE", group.ToList().IndexOf(toggle), toggle), ui);
            foreach (var (unit, building) in book.UpgradeBuilding.Where(p => p.Value.Length > 7))
            {
                string id = "IMG_" + building[7..].TrimStart('0');
                if (!book.Rules.Units.TryGetValue(unit, out var type) || found.ContainsKey(type.SizeSmall)
                    || !ui.Screen.Elements.Any(e => e.Id == id) || ui.IsHidden(id) || logic.Icon(building) == BuildIcon.Hidden) continue;
                var r = Rect(id);
                logic.Release(r.X + 5, r.Y + 5, ui);
                if (logic.Selected is null || !logic.Selected.Equals(building, StringComparison.OrdinalIgnoreCase)) continue;
                found[type.SizeSmall] = unit;
                output.WriteLine($"{toggle} {building} {unit} малый={type.SizeSmall}: {logic.Summary}");
                string border = type.SizeSmall ? "Icons:BORDERUNITSMALL&" : "Icons:BORDERUNITLARGE&";
                string face = type.SizeSmall ? "IMG_FACE" : "IMG_FACE_BIG", other = type.SizeSmall ? "IMG_FACE_BIG" : "IMG_FACE";
                Assert.Equal(border + _c.Faces.Small(unit), ui.Frame(face));
                Assert.Equal(UiImageAlign.Center, ui.ImageAlign(face));
                Assert.Null(ui.Frame(other));
                Assert.Equal(_c.Text("X005TA0382"), ui.TextOf(type.SizeSmall ? "TXT_UNIT_INFO" : "TXT_UNIT_INFO_BIG"));
                Assert.Equal("", ui.TextOf(type.SizeSmall ? "TXT_UNIT_INFO_BIG" : "TXT_UNIT_INFO"));
                var at = Rect(face);
                logic.RightPress(at.Right - 3, at.Y + 3, ui);
                Assert.Equal(unit, Assert.IsType<UnitCardLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic).TypeId, StringComparer.OrdinalIgnoreCase);
            }
        }
        // Место большого: от левого края IMG_FACE на всю рамку 164; подпись правее на 164 − 84, правый край прежний.
        var small = Rect("IMG_FACE");
        Assert.Equal(small with { Width = 164 }, Rect("IMG_FACE_BIG"));
        var info = Rect("TXT_UNIT_INFO");
        Assert.Equal(info with { X = info.X + 164 - small.Width, Width = info.Width - (164 - small.Width) }, Rect("TXT_UNIT_INFO_BIG"));
        Assert.True(found.ContainsKey(true), "нет малого воина постройки");
        Skip.IfNot(found.ContainsKey(false), "у расы нет видимого большого воина постройки");
    }

    /// <summary>Картинка столицы по расе владельца (0x4b3d81): Легионы — E, эльфы — F (Lrace 2 и 5).</summary>
    [SkippableTheory]
    [InlineData(2, "City:ALE0")]
    [InlineData(5, "City:ALF0")]
    [InlineData(0, "City:ALH0")]
    public void CityPicture_ByRaceLetter(int raceType, string frame)
    {
        var (session, fort) = Game();
        var race = session.Book!.Economy.Races.FirstOrDefault(r => r.Value.RaceType == raceType).Key;
        Skip.If(race is null, $"нет расы {raceType}");
        var owner = session.Map.Players[session.Map.Sites.First(s => s.Id == fort).Owner];
        session = WithPlayer(session, owner with { RaceId = race! });
        var (_, ui) = Window(session, fort);
        Assert.Equal(frame, ui.Frame("IMG_CITY"));
    }

    /// <summary>Карточка воина: имя и описание, опыт «n / до уровня», атака из двух частей; у героя — лидерство и навыки, у воина —
    /// без навыков; источник «земля» — строка «Земля» (у движка строки земли и воздуха переставлены).</summary>
    [SkippableFact]
    public void UnitCard_FieldsForSoldierAndHero()
    {
        var (session, fort) = CapitalWithGuest();
        var (_, _) = Window(session, fort);
        var rules = RulesTexts.Load(Path.Combine(Data, "rules"));
        var context = _c! with { Rules = rules };
        var screen = Screens().First(s => s.Id == UnitCardLogic.Screen);
        UiScreenState Card(string unit)
        {
            var logic = new UnitCardLogic(context, unit);
            var ui = new UiScreenState(((IWindowLogic)logic).Shape(screen));
            logic.Open(ui);
            return ui;
        }

        string soldier = session.GroupCells(fort)!.Units[^1];
        var ui = Card(soldier);
        output.WriteLine(ui.TextOf("TXT_STATS"));
        output.WriteLine(ui.TextOf("TXT_ATTACK_INFO"));
        Assert.StartsWith(@"\fLarge;", ui.TextOf("TXT_UNIT_NAME"));
        Assert.True(ui.TextOf("TXT_UNIT_INFO")!.Length > 30, "описание типа");
        Assert.DoesNotContain("%XP", ui.TextOf("TXT_STATS"));
        Assert.DoesNotContain("%ARMOR%", ui.TextOf("TXT_STATS"));
        Assert.Contains(" + 50", ui.TextOf("TXT_STATS"));   // гарнизон столицы — Grace.PROTECTION (+50 брони)
        Assert.Contains($"{session.Map.Units[soldier].Xp} / ", ui.TextOf("TXT_STATS"));
        Assert.Contains("Тип оружия", ui.TextOf("TXT_ATTACK_INFO"));
        Assert.Contains("Зона поражения", ui.TextOf("TXT_ATTACK_INFO"));
        Assert.True(ui.IsHidden("MLBOX_ITEM_ICON"));

        string guest = session.Map.Sites.First(s => s.Id == fort).StackId;
        var hero = Card(session.Map.Stacks[guest].LeaderId);
        output.WriteLine(hero.TextOf("TXT_LEADER_INFO"));
        Assert.Contains("Лидерство", hero.TextOf("TXT_LEADER_INFO"));
        Assert.Contains("Выиграно сражений", hero.TextOf("TXT_LEADER_INFO"));
        Assert.True(hero.IsHidden("TXT_NEED_UPGRADE"));

        var book = session.Book!;
        var earth = book.Rules.Units.Values.FirstOrDefault(u => book.Rules.Attacks.TryGetValue(u.AttackId, out var a) && a.Source == 6);
        Skip.If(earth is null, "нет воина с атакой землёй");
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[soldier] = units[soldier] with { TypeId = earth!.Id.ToUpperInvariant(), Level = earth.Level };
        session = Rebuilt(session, GameSession.With(session.Map, units: units));
        context = context with { Session = session };
        Assert.Contains("Земля", Card(soldier).TextOf("TXT_ATTACK_INFO"));
    }

    /// <summary>Карточка воина в бою с карты (прогон 2 пользователя 11.10, снимок 33: «Опыт:0» вместо «Опыт: 55 / 80», «Иммунитет:Стойкость:»
    /// слитно) — та же, что на карте (<see cref="UnitCardLogic.InBattle"/>): здоровье и опыт из боя, опыт «N / до уровня». Разбор разметки
    /// строки игры: <c>\s110;</c> — место табуляции 110, каждая подпись отделена от значения табуляцией, <c>\mL0;</c> переводит строку —
    /// «Стойкость» на своей строке.</summary>
    [SkippableFact]
    public void UnitCard_InBattle_SharesMapCardAndKeepsSpacing()
    {
        var (session, fort) = CapitalWithGuest();
        var (_, _) = Window(session, fort);
        var context = _c! with { Rules = RulesTexts.Load(Path.Combine(Data, "rules")) };
        var screen = Screens().First(s => s.Id == UnitCardLogic.Screen);
        string soldier = session.GroupCells(fort)!.Units[^1];
        var unit = session.Map.Units[soldier];
        int next = session.Book!.XpNextOf(unit.TypeId, unit.Level);
        Skip.If(next <= 0, "воин последнего уровня");
        var logic = UnitCardLogic.InBattle(context, soldier, unit.TypeId, unit.Level, hp: 3, xp: next - 1)!;
        var ui = new UiScreenState(((IWindowLogic)logic).Shape(screen));
        logic.Open(ui);
        var stats = UiTextStyle.Parse(ui.TextOf("TXT_STATS"));
        output.WriteLine(stats.Text.Replace("\t", "→", StringComparison.Ordinal));
        Assert.Equal(110, stats.Tab);
        Assert.Empty(stats.Unknown);
        Assert.Contains($"Уровень:\t{unit.Level}\n", stats.Text);
        Assert.Contains($"Опыт:\t{next - 1} / {next}\n", stats.Text);
        Assert.Contains($"Здоровье:\t3 / {session.HpMax(unit)}\n", stats.Text);
        var lines = stats.Text.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("Иммунитет:\t", StringComparison.Ordinal) && l.Length > "Иммунитет:\t".Length);
        Assert.Contains(lines, l => l.StartsWith("Стойкость:\t", StringComparison.Ordinal) && l.Length > "Стойкость:\t".Length);
        var attack = UiTextStyle.Parse(ui.TextOf("TXT_ATTACK_INFO"));
        Assert.Equal(110, attack.Tab);
        Assert.Contains("Инициатива:\t", attack.Text);
        Assert.Null(UnitCardLogic.InBattle(context, "S999UN9999", unit.TypeId, unit.Level, 1, 0));   // не с карты — простая карточка боя
    }

    /// <summary>Строка действия карточки при двух атаках (<c>0x5764e1</c>). Прогон 15.10 (пометка 1): у Лесной Девы (первая —
    /// «даровать защиту», вторая — лечение) и её наследников было «Лечить: 0» — движок (<c>0x576551</c>) пишет лечение второй атаки.
    /// Лекарь с одной атакой и с лечением первой — своё лечение; урон с ядом второй — «урон / урон яда».</summary>
    [SkippableFact]
    public void UnitCard_TwoAttacks_EffectLineAsEngine()
    {
        var (session, fort) = CapitalWithGuest();
        var (_, _) = Window(session, fort);
        var context = _c! with { Rules = RulesTexts.Load(Path.Combine(Data, "rules")) };
        var screen = Screens().First(s => s.Id == UnitCardLogic.Screen);
        var book = session.Book!;
        string Line(string typeId)
        {
            var logic = UnitCardLogic.OfType(context, typeId);
            var ui = new UiScreenState(((IWindowLogic)logic).Shape(screen));
            logic.Open(ui);
            string text = UiTextStyle.Parse(ui.TextOf("TXT_ATTACK_INFO")).Text;
            output.WriteLine($"{typeId}: {text.Replace("\n", " | ", StringComparison.Ordinal)}");
            return text.Split('\n').First(l => l.StartsWith("Лечить:", StringComparison.Ordinal) || l.StartsWith("Повреждения:", StringComparison.Ordinal));
        }
        AttackType Attack(string id) => book.Rules.Attacks[id];

        // Лесная Дева (g000uu8033 и g000uu8133), Танцующая с солнцем, Сильфида: лечение второй атаки 50, 50, 50, 55
        foreach (var (unit, heal) in new[] { ("g000uu8033", 50), ("g000uu8133", 50), ("g000uu8034", 50), ("g000uu8035", 55) })
        {
            var type = book.Rules.Units[unit];
            Assert.Equal((AttackClass.BestowWards, AttackClass.Heal), (Attack(type.AttackId).Class, Attack(type.Attack2Id).Class));
            Assert.Equal($"Лечить:\t{heal}", Line(unit));
        }
        Assert.Equal("Лечить:\t40", Line("g000uu0017"));   // Матриарх: лечение первой, вторая — исцеление

        var single = book.Rules.Units.Values.First(u => !book.Rules.Attacks.ContainsKey(u.Attack2Id) && Attack(u.AttackId).Class == AttackClass.Heal);
        Assert.Equal($"Лечить:\t{Attack(single.AttackId).QtyHeal}", Line(single.Id));

        var poisoner = book.Rules.Units.Values.FirstOrDefault(u => book.Rules.Attacks.ContainsKey(u.Attack2Id) && Attack(u.AttackId).Class == AttackClass.Damage
                                                                   && Attack(u.Attack2Id).Class == AttackClass.Poison && !u.IsLeader);
        Skip.If(poisoner is null, "нет воина с уроном и ядом");
        Assert.Equal($"Повреждения:\t{Attack(poisoner!.AttackId).QtyDamage} / {Attack(poisoner.Attack2Id).QtyDamage}", Line(poisoner.Id));
    }

    /// <summary>Окно отряда: «Остановить продвижение» инструментом по воину, отпуск — вопросом.</summary>
    [SkippableFact]
    public void ManageStack_ToolsStopGrowthAndDismiss()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        _c = new WindowContext(session, "S073PL000a", UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var screens = Screens();
        const string stack = "S073KC0001";
        var logic = new ManageStackLogic(_c, stack, screens, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(logic.Shape(screens.First(s => s.Id == ManageStackLogic.Screen)));
        logic.Open(ui);
        var cells = session.GroupCells(stack)!;
        string soldier = cells.Units.First(u => u != session.Map.Stacks[stack].LeaderId && session.DynLevelCheck(u) == 0);
        var r = cells.PositionOf(soldier) is var p && IsBig(cells, soldier)
            ? UnitGroupLayout.Column(new UiRect(4, 174, 70, 85), true, p / 2)
            : UnitGroupLayout.Cell(new UiRect(4, 174, 70, 85), true, p);
        Assert.False(logic.Allows("TOG_DYNLEVEL", ui));
        logic.Handle(Toggle(ui, "TOG_DYNLEVEL"), ui);
        logic.Press(r.X + 10, r.Y + 10, ui);
        logic.Release(r.X + 10, r.Y + 10, ui);
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        ask.Yes();
        Assert.True(session.Map.Units[soldier].DynLevel);
        Assert.Equal(CityTool.None, logic.Tool);

        logic.Handle(Toggle(ui, "TOG_DISMISS"), ui);
        logic.Press(r.X + 10, r.Y + 10, ui);
        logic.Release(r.X + 10, r.Y + 10, ui);
        Assert.StartsWith("Расформировать", Assert.IsType<UiAsk>(logic.TakeRequest()).Text);
    }
}
