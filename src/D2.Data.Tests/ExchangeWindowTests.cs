using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Окно обмена между отрядами (<c>DLG_EXCHANGE</c>, <c>docs/ui-windows.md</c> §8). Сохранение t24: у человека два отряда —
/// второй ставится вплотную к первому.</summary>
[Trait("Category", "Corpus")]
public class ExchangeWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Human = "S073PL000a", Main = "S073KC0001", Second = "S073KC0075";

    /// <summary>Выбор: соседний свой отряд годен, дальний нет. Окно: имена, подписи, правый портрет зеркально; воин меняется местами
    /// с воином другой стороны; предмет из левой сумки — в правую; кукла — фоны кукол (правый зеркально) вместо групп; «Закрыть».</summary>
    [SkippableFact]
    public void Exchange_CheckSwapItemsAndPaperdoll()
    {
        var session = Game();
        var main = session.Map.Stacks[Main];
        Assert.Null(session.ExchangeCheck(Main, main.X + 1, main.Y, out string other));
        Assert.Equal(Second, other);
        Assert.True(session.CanExchange(Main));
        Assert.NotNull(session.ExchangeCheck(Main, main.X + 3, main.Y, out _));
        Assert.NotNull(session.ExchangeCheck(Main, main.X, main.Y, out _));

        var context = Context(session);
        var logic = new ExchangeLogic(context, Main, Second, Screens());
        var ui = Open(logic);
        output.WriteLine(logic.Summary + " | " + ui.TextOf("TXT_LSTACK_STATS"));
        Assert.Null(logic.TakeRequest());   // оба лидера живы — без сообщения
        Assert.NotEmpty(ui.TextOf("TXT_LEFT_NAME")!);
        Assert.NotEmpty(ui.TextOf("TXT_RIGHT_NAME")!);
        Assert.Contains("/", ui.TextOf("TXT_RSTACK_STATS"));
        Assert.StartsWith("Faces:", ui.Frame("IMG_LLEADER"));
        Assert.EndsWith(UiFrameRef.Mirror.ToString(), ui.Frame("IMG_RLEADER"));
        Assert.Equal("DLG_EXCHANGE_GROUP_BGR", ui.Frame("IMG_RPAPERDOLL_BG"));

        // Воин второго отряда (не лидер) — на клетку малого воина первого (не лидера): обмен.
        var mainCells = session.GroupCells(Main)!;
        var secondCells = session.GroupCells(Second)!;
        int from = Small(session, secondCells, session.Map.Stacks[Second].LeaderId);
        int to = Small(session, mainCells, main.LeaderId);
        string moving = secondCells.Cells[from], staying = mainCells.Cells[to];
        Drag(logic, ui, Cell(ui, "IMG_RSLOT1", false, from), Cell(ui, "IMG_LSLOT1", true, to));
        output.WriteLine(logic.Summary);
        Assert.Null(logic.LastRefusal);
        Assert.Equal(moving, session.GroupCells(Main)!.Cells[to]);
        Assert.Equal(staying, session.GroupCells(Second)!.Cells[from]);

        // Предмет: левая сумка → правая.
        string item = logic.BagCells(ExchangeLogic.Left)[0].Top;
        var rightBag = ui.Screen.Elements.First(e => e.Id == "MLBOX_RINVENTORY").Rect;
        Drag(logic, ui, ListCell(ui, "MLBOX_LINVENTORY"), (rightBag.X + 10, rightBag.Y + 10));
        Assert.Null(logic.LastRefusal);
        Assert.Contains(item, session.Map.Stacks[Second].Items);
        Assert.DoesNotContain(item, session.Map.Stacks[Main].Items);

        logic.Handle(new UiToggled("TOG_PAPERDOLL", true), ui);
        Assert.True(logic.Paperdoll);
        Assert.Null(ui.Frame("IMG_LLEADER"));
        Assert.False(ui.IsHidden("IMG_RBANNER"));
        Assert.EndsWith(UiFrameRef.Mirror.ToString(), ui.Frame("IMG_RPAPERDOLL_BG"));
        Assert.DoesNotContain(UiFrameRef.Mirror, ui.Frame("IMG_LPAPERDOLL_BG")!);

        // Надетое на левом герое — со слота куклы в правую сумку: снимается и переносится.
        var worn = D2.Rules.Magic.Equipment.Slots(session.Map.Stacks[Main]);
        int slot = Enumerable.Range(0, worn.Count).First(s => worn[s].Length > 0);
        var slotRect = ui.Screen.Elements.First(e => e.Id == ExchangeSlot(slot)).Rect;
        Drag(logic, ui, (slotRect.X + 5, slotRect.Y + 5), (rightBag.X + 10, rightBag.Y + 10));
        Assert.Null(logic.LastRefusal);
        Assert.Contains(worn[slot], session.Map.Stacks[Second].Items);

        Assert.False(logic.Allows("BTN_CLOSE", ui));
        logic.Handle(new UiClicked("BTN_CLOSE"), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
    }

    /// <summary>Лидер правого отряда пал: при показе X005TA0212, воинов из его отряда не вывести.</summary>
    [SkippableFact]
    public void Exchange_DeadLeaderRefusesUnits()
    {
        var session = Game(deadSecondLeader: true);
        var logic = new ExchangeLogic(Context(session), Main, Second, Screens());
        var ui = Open(logic);
        Assert.Equal(Context(session).Text("X005TA0212"), Assert.IsType<UiTell>(logic.TakeRequest()).Text);
        var secondCells = session.GroupCells(Second)!;
        int from = Small(session, secondCells, session.Map.Stacks[Second].LeaderId);
        int to = Small(session, session.GroupCells(Main)!, session.Map.Stacks[Main].LeaderId);
        // Курсор броска (0x56c158): ничего не тащат — null; обмен не пройдёт — false (NODRAGDROP); своя сторона — true.
        Assert.Null(logic.DropAccepts(10, 10, ui));
        var (fx, fy) = Cell(ui, "IMG_RSLOT1", false, from);
        logic.Press(fx, fy, ui);
        var (tx, ty) = Cell(ui, "IMG_LSLOT1", true, to);
        Assert.False(logic.DropAccepts(tx, ty, ui));
        Assert.False(logic.DropAccepts(400, 590, ui));
        var (ox, oy) = Cell(ui, "IMG_RSLOT1", false, from ^ 1);
        Assert.True(logic.DropAccepts(ox, oy, ui));
        logic.Release(tx, ty, ui);
        Assert.Equal("лидер отряда мёртв — воинов не вывести", logic.LastRefusal);
        Assert.Equal("лидер отряда мёртв — воинов не вывести", session.SwapRefusal(Second, from, Main, to));   // и общий обмен (ИИ)
    }


    private static string ExchangeSlot(int slot) => "IMG_L" + ManageStackLogic.SlotImages[slot]["IMG_".Length..];

    /// <summary>Позиция малого воина группы, не лидера.</summary>
    private static int Small(GameSession session, UnitCells cells, string leader) =>
        Enumerable.Range(0, UnitGroupLayout.Positions).First(p => cells.Cells[p].Length > 0 && cells.Cells[p] != leader
            && cells.Cells[GroupArrange.Pair(p)] != cells.Cells[p] && session.Map.Units[cells.Cells[p]].Hp > 0);

    private static (int X, int Y) Cell(UiScreenState ui, string anchor, bool left, int pos)
    {
        var r = UnitGroupLayout.Cell(ui.Screen.Elements.First(e => e.Id == anchor).Rect, left, pos);
        return (r.X + (r.Width / 2), r.Y + (r.Height / 2));
    }

    private static (int X, int Y) ListCell(UiScreenState ui, string list)
    {
        var e = ui.Screen.Elements.First(el => el.Id == list);
        return (e.Rect.X + (e.List!.CellWidth / 2), e.Rect.Y + (e.List.CellHeight / 2));
    }

    private static void Drag(ExchangeLogic logic, UiScreenState ui, (int X, int Y) from, (int X, int Y) to)
    {
        logic.Press(from.X, from.Y, ui);
        logic.Release(to.X, to.Y, ui);
    }

    private static WindowContext Context(GameSession session) =>
        new(session, Human, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));

    private static IReadOnlyList<UiScreen> Screens() => UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));

    private static UiScreenState Open(IWindowLogic logic)
    {
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == ExchangeLogic.Screen)));
        logic.Open(ui);
        return ui;
    }

    /// <summary>Сохранение t24, второй отряд человека — справа вплотную к первому; <paramref name="deadSecondLeader"/> — его лидер пал.</summary>
    private static GameSession Game(bool deadSecondLeader = false)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var map = looks.Map;
        var main = map.Stacks[Main];
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[Second] = stacks[Second] with { X = main.X + 1, Y = main.Y };
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (deadSecondLeader) units[stacks[Second].LeaderId] = units[stacks[Second].LeaderId] with { Hp = 0 };
        looks = looks with { Map = GameSession.With(map, stacks: stacks, units: units) };
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }
}
