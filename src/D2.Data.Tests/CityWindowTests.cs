using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Magic;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Окно армии укрепления (<see cref="CityStackLogic"/>) и найма (<see cref="HireLogic"/>) на живой партии:
/// раскладка ячеек по движку, перетаскивание, лечение, найм по пустой ячейке и пустой левой половине, рост деревни.</summary>
public partial class CityWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Bank Rich = new(99999, 9999, 9999, 9999, 9999, 9999);

    // --- раскладка (числа — из листинга 0x570308, docs/ui-windows.md §2) ---

    /// <summary>Гость (IMG_LSLOT1 190,68): чётные позиции — x 270, нечётные — 190; ряды 68/174/280. Гарнизон (IMG_RSLOT1
    /// 537,68): чётные — 457, нечётные — 537. Отряд (IMG_SLOT1 4,174): чётные — 84, нечётные — 4; ряды 174/280/386.</summary>
    [Theory]
    [InlineData(190, 68, true, 270, 190, 68, 174, 280)]
    [InlineData(537, 68, false, 457, 537, 68, 174, 280)]
    [InlineData(4, 174, true, 84, 4, 174, 280, 386)]
    public void Layout_MatchesEngine(int x, int y, bool left, int evenX, int oddX, int row0, int row1, int row2)
    {
        var first = new UiRect(x, y, 70, 85);
        int[] rows = [row0, row1, row2];
        for (int p = 0; p < 6; p++)
        {
            var cell = UnitGroupLayout.Cell(first, left, p);
            Assert.Equal(p % 2 == 0 ? evenX : oddX, cell.X);
            Assert.Equal(rows[p / 2], cell.Y);
            Assert.Equal((70, 85), (cell.Width, cell.Height));
            Assert.Equal(p, UnitGroupLayout.PositionAt(first, left, cell.X + 5, cell.Y + 5));
        }
        var column = UnitGroupLayout.Column(first, left, 1);
        Assert.Equal((Math.Min(evenX, oddX), rows[1], 150, 85), (column.X, column.Y, column.Width, column.Height));
        // Зазоров нет (0x5704f8, 0x57060b): зазор между колонками — к ближней, под ячейкой (подпись здоровья) — к ней же; дальше — мимо.
        int leftX = Math.Min(evenX, oddX), rightX = Math.Max(evenX, oddX);
        int leftPos = leftX == evenX ? 0 : 1, rightPos = 1 - leftPos;
        Assert.Equal(leftPos, UnitGroupLayout.PositionAt(first, left, leftX + 72, rows[0] + 5));
        Assert.Equal(leftPos, UnitGroupLayout.PositionAt(first, left, leftX + 74, rows[0] + 5));
        Assert.Equal(rightPos, UnitGroupLayout.PositionAt(first, left, leftX + 75, rows[0] + 5));   // середина зазора 70…80 — правая
        Assert.Equal(leftPos, UnitGroupLayout.PositionAt(first, left, leftX + 5, rows[0] + 85 + 10));   // подпись под ячейкой
        Assert.Equal(leftPos + 2, UnitGroupLayout.PositionAt(first, left, leftX + 5, rows[0] + 85 + 11));
        Assert.Equal(leftPos, UnitGroupLayout.PositionAt(first, left, leftX - 6, rows[0] + 5));
        Assert.Null(UnitGroupLayout.PositionAt(first, left, leftX - 7, rows[0] + 5));
        Assert.Null(UnitGroupLayout.PositionAt(first, left, rightX + 70 + 6, rows[0] + 5));
        Assert.Null(UnitGroupLayout.PositionAt(first, left, leftX + 5, rows[2] + 85 + 11));
    }

    /// <summary>Форма окна: ячейки обеих групп, значки найма гарнизона, пустая группа по центру левой половины
    /// ((185,62) и (251,195) — листинг 0x5736aa), фон групп 350×386 — первым, под портретом героя.</summary>
    [SkippableFact]
    public void Shape_AddsCellsAndMovesGroupBackgroundUnderAll()
    {
        var (session, fort) = Game();
        var (_, ui) = Window(session, fort);
        var elements = ui.Screen.Elements;
        Assert.Equal(("IMG_PAPERDOLL_BG", 350, 386), (elements[0].Id, elements[0].Rect.Width, elements[0].Rect.Height));
        foreach (string g in (string[])["L", "R"])
            for (int p = 0; p < 6; p++) Assert.Contains(elements, e => e.Id == UnitGroupLayout.CellImage(g, p));
        Assert.Equal(new UiRect(185, 62, 160, 323), elements.First(e => e.Id == "IMG_LEMPTY").Rect);
        Assert.Equal(new UiRect(251, 195, 28, 43), elements.First(e => e.Id == "IMG_LEMPTYADD").Rect);
        Assert.Equal(new UiRect(537 + 24, 68 + 21, 28, 43), elements.First(e => e.Id == "IMG_RADD1").Rect);
        Assert.True(ui.IsHidden("TOG_ENROLL"));
    }

    // --- модель экрана ---

    [Fact]
    public void Hidden_ElementIsNotDrawnNorHit()
    {
        var box = Screens().First(s => s.Id == MessageBoxLogic.Screen);
        var ui = new UiScreenState(box);
        bool said = false;
        var question = new MessageBoxLogic("вопрос", () => said = true);
        question.Open(ui);
        var ok = box.Elements.First(e => e.Id == "BTN_OK").Rect;
        Assert.True(ui.IsHidden("BTN_OK"));
        Assert.Null(ui.Frame("BTN_OK"));
        Assert.NotNull(ui.Frame("BTN_YES"));
        Assert.Empty(ui.Key(13));   // «ОК» спрятана — Enter её не жмёт
        ui.Press(ok.X + 5, ok.Y + 50);
        var events = ui.Release(ok.X + 5, ok.Y + 50);
        Assert.Contains(events, e => e is UiClicked { Element: "BTN_YES" });
        // Окно закрывает себя само, переход графа запрещён (у BTN_YES в графе есть и чужие окна).
        Assert.False(question.Allows("BTN_YES", ui));
        foreach (var e in events) question.Handle(e, ui);
        Assert.True(said);
        Assert.IsType<UiClose>(question.TakeRequest());

        var tell = new UiScreenState(box);
        new MessageBoxLogic("сообщение", null).Open(tell);
        Assert.True(tell.IsHidden("BTN_YES"));
        Assert.Contains(tell.Key(13), e => e is UiClicked { Element: "BTN_OK" });

        // «ОК» сообщения и «Нет» вопроса — свой обработчик окна (у движка «ОК» окна «расторгните альянс» 0x4d201e и «Нет» сносу жезла
        // 0x4d1f06 стирают путь отряда, реестр Ин82); «Да» его не зовёт.
        int no = 0;
        var notice = new MessageBoxLogic("сообщение", null, () => no++);
        notice.Handle(new UiClicked("BTN_OK"), tell);
        Assert.IsType<UiClose>(notice.TakeRequest());
        var asked = new MessageBoxLogic("вопрос", () => said = false, () => no++);
        asked.Handle(new UiClicked("BTN_NO"), ui);
        asked.Handle(new UiClicked("BTN_YES"), ui);
        Assert.Equal(2, no);
        Assert.False(said);
    }

    /// <summary>Сообщения карты (захват призванным или вором, шпион, руина, повтор миссии) — одна «ОК»: движок открывает их с
    /// <c>yesNo = 0</c> (снимок оригинала 19.10 — свиток <c>X005TA0458</c> с одной печатью, у порта были ✓ и ✗).</summary>
    [Fact]
    public void Notice_ShowsOnlyOk()
    {
        var box = Screens().First(s => s.Id == MessageBoxLogic.Screen);
        foreach (D2.Data.Menu.IScreenLogic logic in new D2.Data.Menu.IScreenLogic[] { new NoticeLogic("захват"), new D2.Data.Menu.MessageLogic("повтор", okOnly: true) })
        {
            var ui = new UiScreenState(box);
            logic.Open(ui);
            Assert.False(ui.IsHidden("BTN_OK"));
            Assert.True(ui.IsHidden("BTN_YES"));
            Assert.True(ui.IsHidden("BTN_NO"));
            Assert.Contains(ui.Key(13), e => e is UiClicked { Element: "BTN_OK" });
        }
        // Окно графа меню — кнопки решает граф (вопросы «Да»/«Нет»).
        var flow = new UiScreenState(box);
        new D2.Data.Menu.MessageLogic("вопрос").Open(flow);
        Assert.False(flow.IsHidden("BTN_NO"));
    }

    // --- перестановка внутри группы ---

    [Fact]
    public void Within_SwapsCellsAndColumns()
    {
        var small = new UnitCells(["a", "b"], ["a", "", "b", "", "", ""]);
        Assert.Equal(["", "", "b", "", "a", ""], GameSession.Within(small, 0, 4)!.Cells);
        Assert.Equal(["b", "", "a", "", "", ""], GameSession.Within(small, 0, 2)!.Cells);

        var big = new UnitCells(["g", "b"], ["g", "g", "", "b", "", ""]);
        Assert.Equal(["", "b", "g", "g", "", ""], GameSession.Within(big, 0, 2)!.Cells);   // колонка целиком
        Assert.Equal(["", "b", "g", "g", "", ""], GameSession.Within(big, 0, 3)!.Cells);   // задняя клетка — та же колонка (0x6116ba)
        Assert.Equal(["", "b", "g", "g", "", ""], GameSession.Within(big, 3, 0)!.Cells);   // малый с задней клетки на большого — колонками
        Assert.Null(GameSession.Within(big, 0, 1));   // в свою же колонку
        Assert.Null(GameSession.Within(small, 1, 0)); // пустая клетка
    }

    // --- окно на живой партии ---

    /// <summary>Портреты: у каждого воина гостя и гарнизона — картинка в своей ячейке (большой — в колонке) и здоровье.</summary>
    [SkippableFact]
    public void Open_ShowsVisitorAndGarrison()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        var guest = session.GroupCells(session.Map.Sites.First(s => s.Id == fort).StackId)!;
        var garrison = session.GroupCells(fort)!;
        int shown = Enumerable.Range(0, 6).Count(p => ui.Frame(UnitGroupLayout.CellImage("L", p)) is not null)
                    + Enumerable.Range(0, 3).Count(r => ui.Frame(UnitGroupLayout.BigImage("L", r)) is not null);
        Assert.Equal(guest.Units.Count, shown);
        Assert.StartsWith("Faces:", ui.Frame(UnitGroupLayout.CellImage("L", guest.PositionOf(guest.Units[0])))
                                    ?? ui.Frame(UnitGroupLayout.BigImage("L", guest.PositionOf(guest.Units[0]) / 2)));
        Assert.Null(ui.Frame("IMG_LEMPTY"));
        int empty = garrison.Cells.Count(c => c.Length == 0);
        int adds = Enumerable.Range(0, 6).Count(p => ui.Frame("IMG_RADD" + p) is not null);
        output.WriteLine($"{fort}: гость {guest.Units.Count}, гарнизон {garrison.Units.Count}, пустых {empty}, значков найма {adds}");
        Assert.Equal(empty, adds);
        Assert.Equal("City:ALD0", ui.Frame("IMG_CITY"));   // Кланы Гор (RACE_TYPE 3)
        Assert.Equal(session.Map.Players.Values.First(p => p.IsHuman).Bank.Gold.ToString(), ui.TextOf("TXT_GOLD"));
    }

    /// <summary>Ячейка как у движка: раненый — красная нижняя доля портрета по потерянному здоровью, павший — маска поверх,
    /// под ячейкой «здоровье/максимум» (X005TA0757).</summary>
    [SkippableFact]
    public void Cells_WoundedTintedDeadMasked()
    {
        var (session, fort) = CapitalWithGuest(extraGuest: true);
        string stackId = session.Map.Sites.First(s => s.Id == fort).StackId;
        var stack = session.Map.Stacks[stackId];
        string leader = stack.LeaderId, other = stack.UnitIds.First(u => u != leader);
        int max = session.HpMax(session.Map.Units[leader]);
        session = Wound(Wound(session, leader, max / 2), other, 0);
        var (_, ui) = Window(session, fort);
        var cells = session.GroupCells(stackId)!;
        string Frame(string id) => ui.Frame(IsBig(cells, id) ? UnitGroupLayout.BigImage("L", cells.PositionOf(id) / 2)
                                                             : UnitGroupLayout.CellImage("L", cells.PositionOf(id)))!;
        string Text(string id) => ui.TextOf(IsBig(cells, id) ? UnitGroupLayout.BigText("L", cells.PositionOf(id) / 2)
                                                             : UnitGroupLayout.CellText("L", cells.PositionOf(id)))!;
        output.WriteLine($"{Frame(leader)} | {Frame(other)} | {Text(leader)}");
        Assert.EndsWith($"#FF0000@{(max / 2) * 100 / max}", Frame(leader));
        Assert.EndsWith("~MASKDEADS", Frame(other));
        Assert.Contains($"{max / 2}/{max}", Text(leader));
        var parsed = UiFrameRef.Parse(Frame(other))!.Value;
        Assert.Equal(("Faces", "MASKDEADS"), (parsed.Archive, parsed.Mask));
    }

    // --- второй круг: отпуск, склад, кукла, подписи, карточка ---

    /// <summary>Отпуск: инструмент и щелчок по нанятому воину гарнизона — вопрос X002TA0033, «Да» — воина нет; страж — X005TA0450.</summary>
    [SkippableFact]
    public void Dismiss_AsksAndRemoves_GuardianRefused()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(fort)!;
        string hired = cells.Units[^1];
        logic.Handle(Toggle(ui, "TOG_DISMISS"), ui);
        Assert.Equal(CityTool.Dismiss, logic.Tool);
        Click(logic, ui, Cell("R", cells.PositionOf(hired), left: false, big: IsBig(cells, hired)));
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        output.WriteLine(ask.Text);
        Assert.StartsWith("Расформировать", ask.Text);
        Assert.Equal(CityTool.None, logic.Tool);
        ask.Yes();
        Assert.DoesNotContain(hired, session.GroupCells(fort)!.Units);
        Assert.False(session.Map.Units.ContainsKey(hired));

        string? guardian = session.GroupCells(fort)!.Units
            .FirstOrDefault(u => session.Book!.CategoryOf(session.Map.Units[u].TypeId) == D2.Rules.Battle.UnitCategory.Guardian);
        Skip.If(guardian is null, "в гарнизоне нет стража");
        var after = session.GroupCells(fort)!;
        logic.Handle(Toggle(ui, "TOG_DISMISS"), ui);
        Click(logic, ui, Cell("R", after.PositionOf(guardian!), left: false, big: IsBig(after, guardian!)));
        Assert.Equal(_c!.Text("X005TA0450"), Assert.IsType<UiTell>(logic.TakeRequest()).Text);
    }

    /// <summary>Склад и сумка: предмет со склада перетаскивается в сумку гостя и обратно (ExchangeItem).</summary>
    [SkippableFact]
    public void Items_DragBetweenStoreAndBag()
    {
        var (session, fort) = CapitalWithGuest();
        var site = session.Map.Sites.First(s => s.Id == fort);
        string item = session.Map.ItemTypes.Keys.First();
        var sites = session.Map.Sites.Select(s => s.Id == fort ? s with { Items = [item] } : s).ToList();
        session = Rebuilt(session, GameSession.With(session.Map, sites: sites));
        var (logic, ui) = Window(session, fort);
        Assert.Equal([item], logic.StoreItems);
        Assert.Single(ui.Cards("MLBOX_RINVENTORY"));
        int bagBefore = logic.BagItems.Count;

        logic.Press(484 + 10, 404 + 10, ui);   // склад, первая ячейка
        logic.Release(40 + 10, 404 + 10, ui);  // сумка
        Assert.Null(logic.LastRefusal);
        Assert.Empty(logic.StoreItems);
        Assert.Contains(item, logic.BagItems);

        int at = logic.BagCells.ToList().FindIndex(c => c.Ids.Contains(item));   // одинаковые — одна ячейка, тянется последний
        string moved = logic.BagCells[at].Top;
        ui.Select("MLBOX_LINVENTORY", at);
        logic.Press(40 + 10 + ((at - ui.ListTop("MLBOX_LINVENTORY")) * 92), 404 + 10, ui);
        logic.Release(484 + 10, 404 + 10, ui);
        Assert.Equal([moved], logic.StoreItems);
        Assert.Equal(bagBefore, logic.BagItems.Count);
        _ = site;
    }

    /// <summary>Кукла: надетый предмет гостя перетаскивается со слота на склад — снят и перенесён, со склада на тот же слот —
    /// надет снова (сперва в сумку). Предмет не теряется и не двоится.</summary>
    [SkippableFact]
    public void Paperdoll_WornItemToStoreAndBack()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var stack = session.Map.Stacks["S073KC0001"];
        var fortSite = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == stack.Owner);
        // Отряд с надетыми предметами — гость столицы.
        var sites = session.Map.Sites.Select(s => s.Id == fortSite.Id ? s with { StackId = stack.Id, Items = [] } : s).ToList();
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = stack with { Inside = fortSite.Id, X = fortSite.X, Y = fortSite.Y };
        session = Rebuilt(session, GameSession.With(session.Map, stacks: stacks, sites: sites));
        _c = new WindowContext(session, stack.Owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var logic = new CityStackLogic(_c, fortSite.Id, Screens());
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == CityStackLogic.Screen)));
        logic.Open(ui);
        logic.Handle(Toggle(ui, "TOG_PAPERDOLL"), ui);
        var worn = Equipment.Slots(session.Map.Stacks[stack.Id]);
        int slot = Enumerable.Range(0, Equipment.SlotCount).First(s => worn[s].Length > 0);
        string item = worn[slot];
        var r = ui.Screen.Elements.First(e => e.Id == ManageStackLogic.SlotImages[slot]).Rect;
        int total = session.Map.Stacks[stack.Id].Items.Count;

        logic.Press(r.X + 5, r.Y + 5, ui);
        logic.Release(484 + 10, 404 + 10, ui);
        Assert.Null(logic.LastRefusal);
        Assert.Equal([item], logic.StoreItems);
        Assert.Equal("", Equipment.Slots(session.Map.Stacks[stack.Id])[slot]);
        Assert.Equal(total - 1, session.Map.Stacks[stack.Id].Items.Count);

        logic.Press(484 + 10, 404 + 10, ui);
        logic.Release(r.X + 5, r.Y + 5, ui);
        Assert.Null(logic.LastRefusal);
        Assert.Empty(logic.StoreItems);
        Assert.Equal(item, Equipment.Slots(session.Map.Stacks[stack.Id])[slot]);
        Assert.Equal(total, session.Map.Stacks[stack.Id].Items.Count);
    }

    /// <summary>Кукла: переключатель убирает группу гостя и показывает слоты; гостя нет — выключен.</summary>
    [SkippableFact]
    public void Paperdoll_ReplacesGuestGroup()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        Assert.True(ui.IsEnabled("TOG_PAPERDOLL"));
        Assert.True(ui.IsHidden("IMG_BANNER"));
        logic.Handle(Toggle(ui, "TOG_PAPERDOLL"), ui);
        Assert.True(logic.Paperdoll);
        Assert.False(ui.IsHidden("IMG_BANNER"));
        Assert.Equal("DLG_PAPERDOLL_PAPERDOLL_BG", ui.Frame("IMG_PAPERDOLL_BG"));
        Assert.Null(ui.Frame("IMG_LEADER"));
        Assert.All(Enumerable.Range(0, 6), p => Assert.Null(ui.Frame(UnitGroupLayout.CellImage("L", p))));
        Assert.Equal("", ui.TextOf("TXT_LSTACK_STATS"));

        var (empty, capital) = Game();
        var (none, noneUi) = Window(empty, capital);
        Assert.False(noneUi.IsEnabled("TOG_PAPERDOLL"));
        Assert.Equal(_c!.Text("X005TA0158"), noneUi.TextOf("TXT_LSTACK_STATS"));
        _ = none;
    }

    /// <summary>Подписи групп: у гостя — «Отряд», скорость и лидерство (занято − 1 / лидерство − 1); у гарнизона — «Гарнизон».</summary>
    [SkippableFact]
    public void Stats_GuestAndGarrison()
    {
        var (session, fort) = CapitalWithGuest(extraGuest: true);
        var (_, ui) = Window(session, fort);
        var stack = session.Map.Stacks[session.Map.Sites.First(s => s.Id == fort).StackId];
        string left = ui.TextOf("TXT_LSTACK_STATS")!;
        output.WriteLine(left.Replace("\\n", " | ") + " || " + ui.TextOf("TXT_RSTACK_STATS"));
        Assert.Contains("Отряд", left);
        Assert.Contains($"{session.GroupCells(stack.Id)!.Occupied - 1} / {session.Leadership(stack) - 1}", left);
        Assert.Contains("Гарнизон", ui.TextOf("TXT_RSTACK_STATS"));
    }

    /// <summary>Щелчок посередине большого — в зазоре между двумя его ячейками — попадает в него (<c>0x5704f8</c>: зазор к ближней колонке):
    /// карточка открывается. Прогон 15.10 (пометка 3): правая кнопка посередине призванного Хуорна в окне отряда карточку не открыла.</summary>
    [SkippableFact]
    public void RightClick_MiddleOfBigUnit_OpensItsCard()
    {
        var (session, fort) = Game();
        var book = session.Book!;
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        string? big = Economy.HireList(session.Map, human.Id, book.Economy).FirstOrDefault(t => !book.Rules.Units[t].SizeSmall);
        Skip.If(big is null, "в столице нечего нанять большого");
        string? refusal = session.HireUnit(fort, big);
        Skip.If(refusal is not null, $"большой не нанимается: {refusal}");
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(fort)!;
        string hired = cells.Units[^1];
        Assert.True(IsBig(cells, hired));
        var column = UnitGroupLayout.Column(GarrisonFirst, false, cells.PositionOf(hired) / 2);
        logic.RightPress(column.X + (column.Width / 2), column.Y + (column.Height / 2), ui);
        var card = Assert.IsType<UnitCardLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        Assert.Equal(hired, card.UnitId);
    }

    /// <summary>Карточка воина: правая кнопка на воине открывает DLG_R_C_UNIT, отпускание закрывает.</summary>
    [SkippableFact]
    public void RightClick_OpensUnitCardWhileHeld()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(fort)!;
        string unit = cells.Units[^1];
        var at = Cell("R", cells.PositionOf(unit), left: false, big: IsBig(cells, unit));
        logic.RightPress(at.X, at.Y, ui);
        var open = Assert.IsType<UiOpen>(logic.TakeRequest());
        var card = Assert.IsType<UnitCardLogic>(open.Logic);
        Assert.Equal(unit, card.UnitId);
        var cardUi = new UiScreenState(Screens().First(s => s.Id == UnitCardLogic.Screen));
        card.Open(cardUi);
        output.WriteLine(cardUi.TextOf("TXT_STATS") + " | " + cardUi.TextOf("TXT_ATTACK_INFO"));
        Assert.EndsWith(_c!.UnitName(session.Map.Units[unit].TypeId), cardUi.TextOf("TXT_UNIT_NAME"));
        Assert.NotEmpty(cardUi.TextOf("TXT_STATS")!);
        card.RightRelease(at.X, at.Y, cardUi);
        Assert.IsType<UiClose>(card.TakeRequest());
    }

    /// <summary>Перетаскивание воина гостя (не героя) в пустую ячейку гарнизона — он уходит в гарнизон.</summary>
    [SkippableFact]
    public void Drag_MovesUnitFromGuestToGarrison()
    {
        var (session, fort) = CapitalWithGuest(extraGuest: true);
        var (logic, ui) = Window(session, fort);
        string stackId = session.Map.Sites.First(s => s.Id == fort).StackId;
        var stack = session.Map.Stacks[stackId];
        var guest = session.GroupCells(stackId)!;
        string mover = guest.Units.First(u => !u.Equals(stack.LeaderId, StringComparison.OrdinalIgnoreCase)
                                              && Small(session, u));
        int from = guest.PositionOf(mover);
        int to = session.GroupCells(fort)!.Cells.ToList().FindIndex(c => c.Length == 0);

        // Тащимый воин — его портрет на курсоре (0x56efc5), пока кнопка держится; после броска — ничего.
        var at = Cell("L", from, left: true);
        logic.Press(at.X, at.Y, ui);
        Assert.Equal(_c!.Faces.Small(session.Map.Units[mover].TypeId), logic.DragImage);
        logic.Release(at.X, at.Y, ui);
        Assert.Null(logic.DragImage);

        Drag(logic, ui, Cell("L", from, left: true), Cell("R", to, left: false));

        Assert.Null(logic.LastRefusal);
        Assert.Equal(mover, session.GroupCells(fort)!.Cells[to]);
        Assert.DoesNotContain(mover, session.Map.Stacks[stackId].UnitIds);
        // Гарнизон — портреты отражены (адаптер группы с флагом 0, 0x4b1d75; снимки армии столицы 28.09), гость — нет.
        Assert.Equal(UiFrameRef.Mirrored(_c!.Faces.Small(session.Map.Units[mover].TypeId)), ui.Frame(UnitGroupLayout.CellImage("R", to)));
    }

    /// <summary>Перетаскивание внутри гарнизона — перестановка, состав не меняется.</summary>
    [SkippableFact]
    public void Drag_WithinGarrison_Rearranges()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(fort)!;
        string unit = cells.Units[^1];   // нанятый в CapitalWithGuest
        int from = cells.PositionOf(unit);
        int to = Enumerable.Range(0, 6).First(p => cells.Cells[p].Length == 0 && p != from);

        Drag(logic, ui, Cell("R", from, left: false), Cell("R", to, left: false));

        Assert.Null(logic.LastRefusal);
        Assert.Equal(unit, session.GroupCells(fort)!.Cells[to]);
        Assert.Equal(cells.Units.Count, session.GroupCells(fort)!.Units.Count);
    }

    /// <summary>Лечение: инструмент, щелчок по раненому — вопрос с ценой (HEAL_C × недостающие HP), «Да» лечит до полного.</summary>
    [SkippableFact]
    public void HealTool_AsksPriceAndHeals()
    {
        var (session, fort) = CapitalWithGuest(temple: true);
        string stackId = session.Map.Sites.First(s => s.Id == fort).StackId;
        string leader = session.Map.Stacks[stackId].LeaderId;
        session = Wound(session, leader, 1);
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(stackId)!;
        var cost = session.Book!.Treasury.Units[session.Map.Units[leader].TypeId].Heal.Gold;
        int max = session.HpMax(session.Map.Units[leader]);

        logic.Handle(Toggle(ui, "TOG_HEAL"), ui);
        Assert.Equal(CityTool.Heal, logic.Tool);
        Click(logic, ui, Cell("L", cells.PositionOf(leader), left: true, big: IsBig(cells, leader)));

        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        Assert.Equal(CityTool.None, logic.Tool);   // отжимается после одного воина
        Assert.False(ui.IsChecked("TOG_HEAL"));
        output.WriteLine(ask.Text);
        Assert.Contains((cost * (max - 1)).ToString(), ask.Text);
        ask.Yes();
        Assert.Equal(max, session.Map.Units[leader].Hp);
    }

    /// <summary>Курсор инструмента (<c>IsoCursr</c>): лечение над раненым — <c>HEALVAL</c>, над пустой клеткой — <c>HEALINV</c>; воскрешение над живым —
    /// <c>REVIVEINV</c>; без инструмента — обычный. Прежде курсор в окне армии не менялся вовсе (замечание пользователя 14.10).</summary>
    [SkippableFact]
    public void ToolCursor_ValidOverWoundedOnly()
    {
        var (session, fort) = CapitalWithGuest(temple: true);
        string stackId = session.Map.Sites.First(s => s.Id == fort).StackId;
        string leader = session.Map.Stacks[stackId].LeaderId;
        session = Wound(session, leader, 1);
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(stackId)!;
        var at = Cell("L", cells.PositionOf(leader), left: true, big: IsBig(cells, leader));

        Assert.Null(logic.ToolCursor(at.X, at.Y, ui));
        logic.Handle(Toggle(ui, "TOG_HEAL"), ui);
        Assert.Equal("HEALVAL", logic.ToolCursor(at.X, at.Y, ui));
        Assert.Equal("HEALINV", logic.ToolCursor(1, 1, ui));
        logic.Handle(Toggle(ui, "TOG_REVIVE"), ui);
        Assert.Equal("REVIVEINV", logic.ToolCursor(at.X, at.Y, ui));
    }

    /// <summary>Здорового не лечат — сообщение X005TA0594.</summary>
    [SkippableFact]
    public void HealTool_Healthy_Tells()
    {
        var (session, fort) = CapitalWithGuest(temple: true);
        string stackId = session.Map.Sites.First(s => s.Id == fort).StackId;
        string leader = session.Map.Stacks[stackId].LeaderId;
        var (logic, ui) = Window(session, fort);
        logic.Handle(Toggle(ui, "TOG_HEAL"), ui);
        Click(logic, ui, Cell("L", session.GroupCells(stackId)!.PositionOf(leader), left: true,
                              big: IsBig(session.GroupCells(stackId)!, leader)));
        var tell = Assert.IsType<UiTell>(logic.TakeRequest());
        Assert.Equal(_c!.Text("X005TA0594"), tell.Text);
    }

    /// <summary>Щелчок по пустой ячейке гарнизона — окно найма воина; найм встаёт в эту ячейку, окно закрывается.</summary>
    [SkippableFact]
    public void EmptyGarrisonCell_HiresIntoThatCell()
    {
        var (session, fort) = CapitalWithGuest();
        var (logic, ui) = Window(session, fort);
        var cells = session.GroupCells(fort)!;
        int empty = Enumerable.Range(0, 6).Last(p => cells.Cells[p].Length == 0);

        Click(logic, ui, Cell("R", empty, left: false));

        var open = Assert.IsType<UiOpen>(logic.TakeRequest());
        Assert.Equal(HireLogic.Screen, open.Screen);
        var hire = Assert.IsType<HireLogic>(open.Logic);
        var hireUi = new UiScreenState(Screens().First(s => s.Id == HireLogic.Screen));
        hire.Open(hireUi);
        Assert.Equal(HireKind.Unit, hire.Kind);
        Assert.Equal(hire.Types.Count, hireUi.Cards("LBOX_LEADER").Count);
        int small = hire.Types.ToList().FindIndex(t => session.Book!.Rules.Units[t].SizeSmall);
        output.WriteLine($"найм: {string.Join(", ", hire.Types)}; карточка: {hireUi.Cards("LBOX_LEADER")[small].Text}");
        Assert.Equal(_c!.Text("X005TA0161"), hireUi.TextOf("TXT_LEGENDE"));

        // «Нанять» — кнопка подтверждения списка: одно нажатие — ровно один вопрос, переход графа запрещён.
        hireUi.Select("LBOX_LEADER", small);
        var button = hireUi.Screen.Elements.First(e => e.Id == "BTN_HIRE_LEADER").Rect;
        hireUi.Press(button.X + 5, button.Y + 5);
        foreach (var e in hireUi.Release(button.X + 5, button.Y + 5)) hire.Handle(e, hireUi);
        Assert.False(hire.Allows("BTN_HIRE_LEADER", hireUi));
        Assert.IsType<UiAsk>(hire.TakeRequest()).Yes();
        Assert.IsType<UiClose>(hire.TakeRequest());
        Assert.Null(hire.TakeRequest());
        var after = session.GroupCells(fort)!;
        Assert.Equal(hire.Types[small], session.Map.Units[after.Cells[empty]].TypeId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Гостя нет — щелчок по пустой левой половине открывает найм героя; нанятый становится гостем.</summary>
    [SkippableFact]
    public void EmptyLeft_HiresLeader()
    {
        var (session, fort) = Game();
        var (logic, ui) = Window(session, fort);
        Assert.Equal("DLG_CITY_STACK_NO_GROUPBNG", ui.Frame("IMG_LEMPTY"));
        // Подсказка пустой половины (CDDUnitNoGroup 0x573131, X005TA0159; замечание 29) — на месте группы гостя.
        Assert.Equal(_c!.Text("X005TA0159"), ui.TextOf("TXT_LEMPTY"));
        Assert.Contains("нанять героя", ui.TextOf("TXT_LEMPTY"), StringComparison.Ordinal);
        Assert.Equal(UnitGroupLayout.Area(ui.Screen.Elements.First(e => e.Id == "IMG_LSLOT1").Rect, leftSide: true),
                     ui.Screen.Elements.First(e => e.Id == "TXT_LEMPTY").Rect);
        Click(logic, ui, (265, 216));
        var hire = Assert.IsType<HireLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        var hireUi = new UiScreenState(Screens().First(s => s.Id == HireLogic.Screen));
        hire.Open(hireUi);
        Assert.Equal(HireKind.Leader, hire.Kind);
        output.WriteLine($"герои: {string.Join(", ", hire.Types)}; {hireUi.Cards("LBOX_LEADER")[0].Text}");
        Assert.Contains("Лидерство", hireUi.Cards("LBOX_LEADER")[0].Text);

        hire.Hire(0);
        var name = Assert.IsType<LeaderNameLogic>(Assert.IsType<UiOpen>(hire.TakeRequest()).Logic);
        var nameUi = new UiScreenState(Screens().First(s => s.Id == LeaderNameLogic.Screen));
        name.Open(nameUi);
        // Портрет героя — в малой рамке карточки (0x4aff40 → 0x5b680b BORDERUNITSMALL), картинка — как у панелей (0x5b1e6a).
        Assert.Equal("Icons:BORDERUNITSMALL&" + _c!.Faces.Panel(hire.Types[0]), nameUi.Frame("IMG_FACE"));
        Assert.Equal(UiImageAlign.Center, nameUi.ImageAlign("IMG_FACE"));
        Assert.Equal("EDIT_GET_INFO", nameUi.Focused);
        Assert.NotEmpty(nameUi.TextOf("EDIT_GET_INFO")!);
        Assert.False(name.Allows("BTN_CLOSE", nameUi));
        nameUi.SetText("EDIT_GET_INFO", "  ");
        name.Handle(new UiClicked("BTN_CLOSE"), nameUi);
        Assert.Equal(_c!.Text("X005TA0345"), Assert.IsType<UiTell>(name.TakeRequest()).Text);   // пустое имя
        nameUi.SetText("EDIT_GET_INFO", "Торин");
        name.Handle(new UiClicked("BTN_CLOSE"), nameUi);
        Assert.IsType<UiClose>(name.TakeRequest());
        Assert.IsType<UiClose>(hire.TakeRequest());
        string visitor = session.Map.Sites.First(s => s.Id == fort).StackId;
        Assert.NotEmpty(visitor);
        Assert.Equal("Торин", session.Looks.Stacks.First(s => s.Id == visitor).LeaderName);
        logic.Resume(ui);
        Assert.Null(ui.Frame("IMG_LEMPTY"));
        Assert.Equal("", ui.TextOf("TXT_LEMPTY"));
        Assert.NotNull(ui.Frame("IMG_LEADER"));
    }

    /// <summary>Рост деревни: вопрос с уровнем и ценой, «Да» растит.</summary>
    [SkippableFact]
    public void Grow_AsksAndGrows()
    {
        var (session, _) = Game();
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        var village = session.Map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Village && (s.Size ?? 1) < 5);
        Skip.If(village is null, "нет деревни ниже 5-го уровня");
        village = village! with { Owner = human.Id, GrowthTurn = 0, RiotTurn = 0 };
        var sites = session.Map.Sites.Select(s => s.Id == village.Id ? village : s).ToList();
        session = Rebuilt(session, GameSession.With(session.Map, sites: sites));
        var (logic, ui) = Window(session, village!.Id);
        Assert.True(ui.IsEnabled("BTN_GROW_CITY"));
        logic.Handle(new UiClicked("BTN_GROW_CITY"), ui);
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        Assert.Contains(((village.Size ?? 1) + 1).ToString(), ask.Text);
        ask.Yes();
        Assert.Equal((village.Size ?? 1) + 1, session.Map.Sites.First(s => s.Id == village.Id).Size);
    }

    /// <summary>Рост спрашивает логика — переход графа к окну сообщения запрещён (иначе два окна).</summary>
    [SkippableFact]
    public void Grow_ForbidsFlowMessage()
    {
        var (session, fort) = Game();
        var (logic, ui) = Window(session, fort);
        Assert.False(logic.Allows("BTN_GROW_CITY", ui));
        Assert.True(logic.Allows("BTN_CLOSE", ui));
    }

    /// <summary>«Вывести героя»: гость выходит на первую годную клетку от входа (E+(1,1), (0,1), (1,0), (−1,1), (1,−1)),
    /// перестаёт быть гостем, очки хода целы; окно просит закрыться и выделить отряд.</summary>
    [SkippableFact]
    public void ExitCity_LeavesToGateCell()
    {
        var (session, fort) = CapitalWithGuest();
        string stackId = session.Map.Sites.First(s => s.Id == fort).StackId;
        int move = session.Map.Stacks[stackId].Move;
        var gate = PathFinder.EntranceOf(session.Map, fort)!.Value;
        var (logic, ui) = Window(session, fort);
        Assert.True(ui.IsEnabled("BTN_EXIT_CITY"));
        Assert.False(logic.Allows("BTN_EXIT_CITY", ui));

        logic.Handle(new UiClicked("BTN_EXIT_CITY"), ui);

        Assert.IsType<UiClose>(logic.TakeRequest());
        Assert.Equal(stackId, Assert.IsType<UiFocus>(logic.TakeRequest()).StackId);
        var stack = session.Map.Stacks[stackId];
        output.WriteLine($"вход {gate}, отряд встал на ({stack.X},{stack.Y})");
        Assert.Equal("", stack.Inside);
        Assert.Contains((stack.X - gate.X, stack.Y - gate.Y), new[] { (1, 1), (0, 1), (1, 0), (-1, 1), (1, -1) });
        Assert.Equal(move, stack.Move);
        Assert.Equal("", session.Map.Sites.First(s => s.Id == fort).StackId);
        Assert.Contains(stackId, session.Map.Plan[(stack.X, stack.Y)]);
    }

    // --- окно отряда ---

    /// <summary>Окно отряда (день 24, S073KC0001): предмет из сумки перетаскивается в слот куклы — надет и ушёл из сумки;
    /// из слота в сумку — снят; неподходящий слот — сообщение отказа.</summary>
    [SkippableFact]
    public void ManageStack_DragsItemsBetweenBagAndDoll()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        _c = new WindowContext(session, "S073PL000a", UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var screens = Screens();
        var logic = new ManageStackLogic(_c, "S073KC0001", screens, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(logic.Shape(screens.First(s => s.Id == ManageStackLogic.Screen)));
        logic.Open(ui);
        output.WriteLine(logic.Summary + " | " + ui.TextOf("TXT_STACK_STATS"));
        Assert.Contains("Скорость", ui.TextOf("TXT_STACK_STATS"));
        var rows = logic.SkillRows(session.Map.Stacks["S073KC0001"]);
        output.WriteLine("навыки: " + string.Join(", ", rows.Select(r => r.Icon)));
        Assert.Equal(rows.Count, ui.Cards("LBOX_ITEM_ICON").Count);
        Assert.All(ui.Cards("LBOX_ITEM_ICON"), c => Assert.StartsWith("Icons:ABIL", c.Image));
        Assert.Equal(Math.Min(3, logic.BagItems.Count), ui.Cards("MLBOX_INVENTORY").Take(3).Count());

        var slots = Equipment.Slots(session.Map.Stacks["S073KC0001"]);
        int slot = Enumerable.Range(0, Equipment.SlotCount).First(sl => slots[sl].Length > 0);
        string item = slots[slot];
        var target = ui.Screen.Elements.First(e => e.Id == ManageStackLogic.SlotImages[slot]).Rect;

        // Из слота в сумку — снят.
        logic.Press(target.X + 5, target.Y + 5, ui);
        logic.Release(484 + 10, 404 + 10, ui);
        Assert.Equal("", Equipment.Slots(session.Map.Stacks["S073KC0001"])[slot]);
        Assert.Null(ui.Frame(ManageStackLogic.SlotImages[slot]));
        int index = logic.BagCells.ToList().FindIndex(c => c.Ids.Contains(item));
        Assert.True(index >= 0);
        string again = logic.BagCells[index].Top;   // одинаковые — одна ячейка, тянется последний

        // Из сумки в слот — надет снова (сумка прокручена к нему).
        ui.Select("MLBOX_INVENTORY", index);
        int column = index - ui.ListTop("MLBOX_INVENTORY");
        logic.Press(484 + 10 + (column * 92), 404 + 10, ui);
        logic.Release(target.X + 5, target.Y + 5, ui);
        Assert.Null(logic.LastRefusal);
        Assert.Equal(again, Equipment.Slots(session.Map.Stacks["S073KC0001"])[slot]);
        Assert.DoesNotContain(again, logic.BagItems);
        Assert.StartsWith("IconItem:", ui.Frame(ManageStackLogic.SlotImages[slot]));

        // Сапоги-слот не примет не-сапоги: рамки нет (0x571c80 → 2), бросок не принимается (0x56eace) — ни отказа, ни сообщения.
        int boots = Equipment.SlotBoots;
        int other = logic.BagCells.ToList().FindIndex(c => !Equipment.SlotAccepts(session.ItemOf(c.Top)!.Category, boots));
        Skip.If(other < 0, "нет предмета для отказа");
        var b = ui.Screen.Elements.First(e => e.Id == "IMG_BOOTS").Rect;
        ui.Select("MLBOX_INVENTORY", other);
        logic.Press(484 + 10 + ((other - ui.ListTop("MLBOX_INVENTORY")) * 92), 404 + 10, ui);
        string held = logic.BagCells[other].Top;
        Assert.False(logic.DropAccepts(b.X + 5, b.Y + 5, ui));
        logic.Release(b.X + 5, b.Y + 5, ui);
        Assert.Null(logic.TakeRequest());
        Assert.Contains(held, logic.BagItems);
    }

    // --- предложение союза ---

    /// <summary>Предложение союза от ИИ: окно с портретом лорда и X005TA0289, «Принять» — союз, окно закрывается, реплика
    /// ИИ — в DLG_CHAT_AI; «Отказ» — союза нет. В обоих случаях предложение снято.</summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllianceOffer_AcceptOrRefuse(bool accept)
    {
        var (session, _) = Game();
        var book = session.Book!;
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        var ai = session.Map.Players.Values.First(p => !p.IsHuman && book.Economy.Races.TryGetValue(p.RaceId, out var r) && r.Playable
                                                       && p.CapturedBy.Length == 0);
        var offer = new AllianceOffer(ai.Id, human.Id, session.Map.Turn);
        session = new GameSession(session.State with { Offers = [offer] }, session.Art, session.Book);
        _c = new WindowContext(session, human.Id, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var logic = new AllianceOfferLogic(_c, offer, RaceNames.Load(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(Screens().First(s => s.Id == AllianceOfferLogic.Screen));
        logic.Open(ui);
        output.WriteLine(ui.TextOf("TXT_MESSAGE"));
        Assert.Contains("союз", ui.TextOf("TXT_MESSAGE"));
        Assert.Contains(ai.Name, ui.TextOf("TXT_MESSAGE"));
        Assert.StartsWith("Lords:LORD", ui.Frame("IMG_FACE"));
        Assert.True(ui.IsHidden("BTN_OK2"));
        Assert.False(logic.Allows("BTN_OK", ui));

        var sights = new SightRecorder();
        session.Observer = sights;
        logic.Handle(new UiClicked(accept ? "BTN_OK" : "BTN_CANCEL"), ui);

        Assert.IsType<UiClose>(logic.TakeRequest());
        Assert.Null(logic.LastRefusal);
        Assert.Empty(session.State.Offers);
        // Отвечающему звука нет: ALLIANCE/REFALLIA движок шлёт предложившему (сообщение 18, 0x4d5429; docs/ai-screen.md) — уведомления нет.
        Assert.DoesNotContain(sights.Sights, s => s.Kind is TurnSightKind.AllianceMade or TurnSightKind.AllianceRefused);
        int a = book.Economy.Races[ai.RaceId].RaceType, h = book.Economy.Races[human.RaceId].RaceType;
        Assert.Equal(accept, Diplomacy.AreAllies(session.Map.Relations, a, h));
        // Реплика предлагавшего — сообщением ИИ человеку (TurnSightKind.AiChat): окно DLG_CHAT_AI открывает сборщик окон сцены.
        Assert.Null(logic.TakeRequest());
        var said = Assert.Single(sights.Sights, s => s.Kind == TurnSightKind.AiChat);
        Assert.Equal((human.Id, ai.Id), (said.PlayerId, said.Id));
        var chat = ChatLogic.Of(said, session.Map, book, RaceNames.Load(Path.Combine(Data, "rules")), _c.Text);
        output.WriteLine($"реплика: {chat.Text}");
        Assert.Contains(said.Text, chat.Text);
        Assert.Contains(ai.Name.Trim(), chat.Text);
        Assert.StartsWith("Lords:LORD", chat.Face);
    }

    /// <summary>Горячий стул: обмен картами от другого человека — то же окно с X160TA0008; «Принять» — туман получателя не больше
    /// прежнего (клетка, открытая у одного, открыта у обоих), «Отказ» — туман прежний; предложение снято, реплики нет.</summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExchangeOffer_FromHuman_AcceptOrRefuse(bool accept)
    {
        var (session, _) = Game();
        var book = session.Book!;
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        var other = session.Map.Players.Values.First(p => p.Id != human.Id && book.Economy.Races.TryGetValue(p.RaceId, out var r)
                                                          && r.Playable && p.CapturedBy.Length == 0);
        var offer = new AllianceOffer(other.Id, human.Id, session.Map.Turn, Exchange: true);
        session = new GameSession(session.State with { Info = session.State.Info with { Humans = [human.Id, other.Id] }, Offers = [offer] },
                                  session.Art, session.Book);
        _c = new WindowContext(session, human.Id, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        int Hidden() => session.Map.Fog[session.Map.Players[human.Id].FogId].Cast<bool>().Count(h => h);
        int before = Hidden();
        var logic = new AllianceOfferLogic(_c, offer, RaceNames.Load(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(Screens().First(s => s.Id == AllianceOfferLogic.Screen));
        logic.Open(ui);
        output.WriteLine(ui.TextOf("TXT_MESSAGE"));
        Assert.Equal("X160TA0008", logic.Question);
        Assert.Contains("обменяться видом карты", ui.TextOf("TXT_MESSAGE"));
        Assert.Contains(other.Name, ui.TextOf("TXT_MESSAGE"));
        Assert.StartsWith("Lords:LORD", ui.Frame("IMG_FACE"));

        var sights = new SightRecorder();
        session.Observer = sights;
        logic.Handle(new UiClicked(accept ? "BTN_OK" : "BTN_CANCEL"), ui);

        // Предложил человек — ответ слышен сразу (сообщение 61 без фильтра игрока, 0x48a8c2 → 0x4d59fa): TRADITEM/REFTRITM.
        Assert.Equal(accept ? TurnSightKind.Traded : TurnSightKind.TradeRefused,
                     Assert.Single(sights.Sights, s => s.Kind is TurnSightKind.Traded or TurnSightKind.TradeRefused).Kind);
        Assert.IsType<UiClose>(logic.TakeRequest());
        // Итог — предложившему-человеку сразу (сообщение 61, «горячий стул»): X160TA0009/X160TA0010 от имени адресата.
        string result = Assert.IsType<UiTell>(logic.TakeRequest()).Text;
        Assert.Contains(human.Name, result);
        Assert.Contains(accept ? "принял" : "отклонил", result);
        Assert.Null(logic.TakeRequest());
        Assert.Null(logic.LastRefusal);
        Assert.Empty(session.State.Offers);
        if (accept) Assert.True(Hidden() <= before);
        else Assert.Equal(before, Hidden());
    }

    // --- постройки ---

    /// <summary>Окно построек: панели веток расы встроены в экран (Кланы — DLG_M_*), видна одна; щелчок по значку
    /// выбирает постройку; «Построить» спрашивает цену и строит, второй раз за день — X005TA0239.</summary>
    [SkippableFact]
    public void Build_SelectsAndBuildsOncePerDay()
    {
        var (session, _) = Game();
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        session = WithPlayer(session, session.Map.Players[human.Id] with { ConstructionTurn = 0 });
        string viewer = human.Id;
        _c = new WindowContext(session, viewer, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var screens = Screens();
        var logic = new BuildLogic(_c, UiFlows.Load(Path.Combine(Data, "ui", "flow.json")), screens,
                                   BuildLogic.LoadTexts(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(logic.Shape(screens.First(s => s.Id == BuildLogic.Screen)));
        logic.Open(ui);
        Assert.Equal("TOG_FIGHTER", logic.Branch);
        Assert.False(ui.IsHidden("IMG_PANEL_TOG_FIGHTER"));
        Assert.True(ui.IsHidden("IMG_PANEL_TOG_MAGE"));
        Assert.True(ui.IsHidden("IMG_37"));   // ветка магов Кланов спрятана

        Assert.Equal(BuildIcon.Built, logic.Icon(human.Buildings[0]));
        string expected = logic.Icon("G000BB0026") switch
        {
            BuildIcon.Built => "IconBld:G000BB0026#0000C4", BuildIcon.NeedPrevious => "IconBld:G000BB0026#FF0000",
            BuildIcon.OtherPath => "IconBld:G000BB0026#000000", _ => "IconBld:G000BB0026",
        };
        Assert.Equal(expected, ui.Frame("IMG_26"));   // построено — синим, нет предыдущей — красным, другой путь — чёрным
        Assert.Equal(new UiFrameRef("IconBld", "G000BB0026", new UiColor(0, 0, 196), 0), UiFrameRef.Parse("IconBld:G000BB0026#0000C4"));
        Assert.Equal(new UiFrameRef("Faces", "X", new UiColor(255, 0, 0), 60), UiFrameRef.Parse("Faces:X#FF0000@60"));
        Assert.Null(UiFrameRef.Parse("DLG_CITY_STACK_ADDSTACK"));
        string pick = session.Buildable(human.Id).First(b => logic.Status(b) == BuildStatus.CanBeBuilt
            && ui.Screen.Elements.Any(e => e.Id == "IMG_" + b[7..].TrimStart('0') && !ui.IsHidden(e.Id)));
        var icon = ui.Screen.Elements.First(e => e.Id == "IMG_" + pick[7..].TrimStart('0')).Rect;
        logic.Release(icon.X + 5, icon.Y + 5, ui);
        Assert.Equal(pick, logic.Selected, StringComparer.OrdinalIgnoreCase);
        // Рамка выбора — контур 1 точка (225,225,0) вокруг значка (0x4adafe).
        string selId = "IMG_" + pick[7..].TrimStart('0') + "_SEL";
        var sel = ui.Screen.Elements.First(e => e.Id == selId).Rect;
        Assert.Equal((icon.X - 1, icon.Y - 1, icon.Width + 2, icon.Height + 2), (sel.X, sel.Y, sel.Width, sel.Height));
        Assert.Equal($"Outline:{icon.Width + 2}x{icon.Height + 2}#E1E100", ui.Frame(selId));
        Assert.True(ui.IsEnabled("BTN_BUILD"));
        Assert.False(logic.Allows("BTN_BUILD", ui));
        logic.DoubleClick(icon.X + 5, icon.Y + 5, ui);   // двойной щелчок = «Построить» (0x4add6c)
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        output.WriteLine(ask.Text);
        ask.Yes();
        Assert.Contains(pick, session.Map.Players[human.Id].Buildings, StringComparer.OrdinalIgnoreCase);
        Assert.IsType<UiClose>(logic.TakeRequest());   // построено — окно закрывается само (0x4ab364 → 0x5c8f71)

        logic.Handle(new UiGroupChanged("RAD_BUILDING_TYPE", 1, "TOG_MAGE"), ui);
        Assert.True(ui.IsHidden("IMG_26"));
        Assert.False(ui.IsHidden("IMG_37"));
        Assert.Equal("TOG_MAGE", _c.Memory.BuildBranch);   // окно откроется на ней снова
        var other = session.Buildable(human.Id).FirstOrDefault(b => logic.Icon(b) == BuildIcon.Can
            && ui.Screen.Elements.Any(e => e.Id == "IMG_" + b[7..].TrimStart('0') && !ui.IsHidden(e.Id)));
        Skip.If(other is null, "в ветке магов нечего строить");
        var r = ui.Screen.Elements.First(e => e.Id == "IMG_" + other![7..].TrimStart('0')).Rect;
        logic.Release(r.X + 5, r.Y + 5, ui);
        logic.Handle(new UiClicked("BTN_BUILD"), ui);
        Assert.Equal(_c.Text("X005TA0239"), Assert.IsType<UiTell>(logic.TakeRequest()).Text);
    }

    // --- столица и навигатор ---

    /// <summary>Слои столицы Империи: без построек — фон, столица, анимации и передний план по номеру; конюшня
    /// (<c>G000BB0001</c>, слой 160) встаёт между передним планом (150) и второй анимацией (500).</summary>
    [Fact]
    public void CapitalLayers_ByBuildingsAndZ()
    {
        var art = CapitalArt.Load(Path.Combine(Data, "ui", "capital.json"));
        Assert.Equal(5, art.Races.Count);
        Assert.Equal(["BG_UNDEAD", "EMP_MAIN", "EMP_ANIME", "EMP_PLAN1", "EMP_ANIME2"], art.Visible(0, []).Select(l => l.Image));
        var built = art.Visible(0, ["g000bb0001"]).Select(l => l.Image).ToList();
        Assert.Equal(["BG_UNDEAD", "EMP_MAIN", "EMP_ANIME", "EMP_PLAN1", "EMP_STABLE", "EMP_ANIME2"], built);
        var elf = art.Visible(5, ["G000BB0100"]).Single(l => l.Image == "EL_CENTORHOLE");
        Assert.Equal((274, 475), (elf.X, elf.Y));   // постройки эльфов — анимации в своей точке
        Assert.Equal(new UiRect(309, 497, 74, 67), elf.Hotspot);
        Assert.Empty(art.Visible(4, []));
    }

    [Fact]
    public void Navigator_PushAndPop()
    {
        var nav = new UiNavigator(new UiFlow(), "DLG_ISO_PAL", UiPhase.Game);
        nav.Push("DLG_CITY_STACK");
        nav.Push("DLG_MESSAGE_BOX");
        Assert.Equal(["DLG_ISO_PAL", "DLG_CITY_STACK", "DLG_MESSAGE_BOX"], nav.Stack);
        Assert.True(nav.Pop());
        Assert.True(nav.Pop());
        Assert.False(nav.Pop());   // корень не снимается
        Assert.Equal("DLG_ISO_PAL", nav.Top);
    }

    // --- помощники ---

    private WindowContext? _c;

    private (CityStackLogic Logic, UiScreenState Ui) Window(GameSession session, string fort)
    {
        string viewer = session.Map.Players.Values.First(p => p.IsHuman).Id;
        _c = new WindowContext(session, viewer, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var logic = new CityStackLogic(_c, fort, Screens());
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == CityStackLogic.Screen)));
        logic.Open(ui);
        return (logic, ui);
    }

    private static IReadOnlyList<UiScreen> Screens() => UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));

    private static (GameSession Session, string Capital) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        session = WithPlayer(session, human with { Bank = Rich });
        var capital = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == human.Id);
        if (capital.StackId.Length > 0)
        {
            // Гость столицы мешает проверке пустой левой половины — уводим его из укрепления.
            var sites = session.Map.Sites.Select(s => s.Id == capital.Id ? s with { StackId = "" } : s).ToList();
            session = Rebuilt(session, GameSession.With(session.Map, sites: sites));
        }
        return (session, capital.Id);
    }

    /// <summary>Панель столицы <c>DLG_CAPITAL</c>: портрет гостя отражён, очки хода — строкой X005TA0396, как у панели команд (снимки 28.09
    /// <c>CCapitalInterf</c>: «Скорость: 20 / 20»).</summary>
    [SkippableFact]
    public void CapitalPanel_GuestFaceMirroredAndMoves()
    {
        var (session, capital) = CapitalWithGuest();
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        _c = new WindowContext(session, human.Id, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(Screens().First(s => s.Id == CapitalPanelLogic.Screen));
        new CapitalPanelLogic(_c, capital).Open(ui);
        var stack = session.Map.Stacks[session.Map.Sites.First(s => s.Id == capital).StackId];
        output.WriteLine($"{ui.Frame("IMG_LEADER")} | {ui.TextOf("TXT_LEADER_STATS")}");
        Assert.EndsWith(UiFrameRef.Mirror.ToString(), ui.Frame("IMG_LEADER"));
        Assert.Contains($"{stack.Move} / {session.DayAllowance(stack)}", ui.TextOf("TXT_LEADER_STATS"));
    }

    /// <summary>Клетка нанятого лидера (<c>0x5d6957</c> → <c>0x5e6e43</c> → <c>CVisitorCreateStackInside</c> → <c>CMidStack</c> <c>0x5ebe7a</c>):
    /// малый с дальней первой атакой — клетка 3, ближней (<c>L_ADJACENT</c>) — 2, большой — 2 и 3. Все лидеры расы игрока.</summary>
    [SkippableFact]
    public void HireLeader_CellByReachAndSize()
    {
        var (first, _) = Game();
        var book = first.Book!;
        var human = first.Map.Players.Values.First(p => p.IsHuman);
        var cells = new List<string>();
        foreach (string type in book.RaceLeaders[human.RaceId])
        {
            var (session, fort) = Game();
            Assert.Null(session.HireLeader(fort, type));
            var stack = session.Map.Stacks[session.Map.Sites.First(s => s.Id == fort).StackId];
            var unit = book.Rules.Units[type];
            bool melee = book.Rules.Attacks[unit.AttackId].Reach == D2.Rules.Battle.AttackReach.Adjacent;
            int[] want = !unit.SizeSmall ? [2, 3] : melee ? [2] : [3];
            var got = Enumerable.Range(0, 6).Where(c => stack.Slots[c] == 0).ToArray();
            cells.Add($"{type} {(unit.SizeSmall ? "малый" : "большой")} {(melee ? "ближний" : "дальний")} [{string.Join(",", got)}]");
            Assert.Equal(want, got);
        }
        output.WriteLine(string.Join("; ", cells));
        Assert.Contains(cells, c => c.EndsWith("[3]", StringComparison.Ordinal));
    }

    /// <summary>Столица с гостем (нанятый герой) и хотя бы одним воином и одной пустой клеткой в гарнизоне.</summary>
    private static (GameSession Session, string Capital) CapitalWithGuest(bool extraGuest = false, bool temple = false)
    {
        var (session, fort) = Game();
        var book = session.Book!;
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        if (temple && !Healing.HasTemple(human.Buildings, book.Economy.Buildings))
        {
            string t = book.Economy.Buildings.Values.First(b => b.Category == Healing.TempleCategory
                                                                && session.Buildable(human.Id).Contains(b.Id, StringComparer.OrdinalIgnoreCase)).Id;
            session = WithPlayer(session, session.Map.Players[human.Id] with { Buildings = [.. human.Buildings, t.ToUpperInvariant()] });
        }
        string leaderType = book.RaceLeaders[human.RaceId].First(t => book.Rules.Units[t].SizeSmall);
        Assert.Null(session.HireLeader(fort, leaderType));
        var smallHire = Economy.HireList(session.Map, human.Id, book.Economy).First(t => book.Rules.Units[t].SizeSmall);
        Skip.If(session.GroupCells(fort)!.Cells.Count(c => c.Length == 0) < 2, "в гарнизоне столицы нет двух свободных клеток");
        Assert.Null(session.HireUnit(fort, smallHire));   // нанятый — не страж: его можно переставлять и уводить
        var garrison = session.GroupCells(fort)!;
        string hired = garrison.Units[^1];
        if (extraGuest)
        {
            // Второй воин в отряд гостя: обменом из гарнизона.
            string stackId = session.Map.Sites.First(s => s.Id == fort).StackId;
            string unit = hired;
            int to = session.GroupCells(stackId)!.Cells.ToList().FindIndex(c => c.Length == 0);
            Assert.Null(session.SwapUnits(fort, garrison.PositionOf(unit), stackId, to));
        }
        return (session, fort);
    }

    private static GameSession WithPlayer(GameSession session, MapPlayer player)
    {
        var players = session.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[player.Id] = player;
        return Rebuilt(session, GameSession.With(session.Map, players: players));
    }

    private static GameSession Wound(GameSession session, string unitId, int hp)
    {
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[unitId] = units[unitId] with { Hp = hp };
        return Rebuilt(session, GameSession.With(session.Map, units: units));
    }

    private static GameSession Rebuilt(GameSession session, MapState map) =>
        new(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);

    private static bool Small(GameSession session, string unitId) =>
        session.Book!.Rules.Units.TryGetValue(session.Map.Units[unitId].TypeId, out var t) && t.SizeSmall;

    private static bool IsBig(UnitCells cells, string id) =>
        cells.Cells[GroupArrange.Pair(cells.PositionOf(id))].Equals(id, StringComparison.OrdinalIgnoreCase);

    private static readonly UiRect GuestFirst = new(190, 68, 70, 85), GarrisonFirst = new(537, 68, 70, 85);

    private static (int X, int Y) Cell(string group, int pos, bool left, bool big = false)
    {
        var r = big ? UnitGroupLayout.Column(left ? GuestFirst : GarrisonFirst, left, pos / 2)
                    : UnitGroupLayout.Cell(left ? GuestFirst : GarrisonFirst, left, pos);
        _ = group;
        return (r.X + 10, r.Y + 10);
    }

    private static void Drag(CityStackLogic logic, UiScreenState ui, (int X, int Y) from, (int X, int Y) to)
    {
        logic.Press(from.X, from.Y, ui);
        logic.Release(to.X, to.Y, ui);
    }

    private static void Click(CityStackLogic logic, UiScreenState ui, (int X, int Y) at) => Drag(logic, ui, at, at);

    private static UiEvent Toggle(UiScreenState ui, string id)
    {
        ui.SetChecked(id, true);
        return new UiToggled(id, true);
    }
}
