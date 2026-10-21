using System.Text.Json;
using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Рамки допустимости при перетаскивании (<see cref="DropFrames"/>, <c>docs/ui-windows.md</c> §2): разбор
/// <c>0x56c58a</c>/<c>0x56eaef</c>/<c>0x56e02d</c>/<c>0x56e487</c>, правила <c>0x500d03</c>/<c>0x56e75e</c>, журнал <c>swap_check</c>.</summary>
public partial class DropFramesTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Журналы с началами перетаскивания и сохранение, в котором есть все воины их групп (подобрано перебором сохранений
    /// прогона: первое по порядку, где есть обе группы и все воины клеток; клетки берутся из журнала, из сохранения — только сами воины,
    /// лидеры и лидерство).</summary>
    private static readonly (string Run, string Save, long[] Times)[] Drags =
    [
        ("20260919-human", "rastlennaya-vlast-dwarf-t01-001.sg", [255031, 265422, 267266, 269110, 276172]),
        ("20260919-human", "rastlennaya-vlast-dwarf-t02-006.sg", [525563, 526203]),
        ("20260919-human", "rastlennaya-vlast-dwarf-t06-016.sg", [869578, 870766]),
        ("20260919-human", "rastlennaya-vlast-dwarf-t30-055.sg", [2360531]),
        ("20260920-legions", "rastlennaya-vlast-heretic-t09-023.sg", [420063]),
        ("20260929-hotseat", "vertigo-heretic-undead-t01-001.sg", [595312, 603671, 608109, 610921, 1349453, 1350656]),
        ("20260928-shots", "dyhanie-smerti-human-t01-001.sg", [242625, 254188]),
        ("20260928-shots", "ng-diffgame-0-human-t01-003.sg", [482657]),
    ];

    /// <summary>
    /// Харнесс по журналу: в начале перетаскивания движок проверяет все шесть позиций каждой группы окна (<c>0x56eaef</c> → <c>0x500d03</c>
    /// → <c>swap_check</c> <c>0x5df4fb</c>), и рамка жёлтая ровно там, где проверка дала 0 (дальше <c>0x56e75e</c> зовёт тот же
    /// <c>canApply</c> <c>0x61111f</c>), иначе рамки нет. 20 начал перетаскивания в четырёх прогонах (240 позиций): своя группа — все
    /// шесть, с большим юнитом и своей клеткой тоже; чужая — лидер (код 3), страж (2) и обмен с лидером (0xB) — без рамки.
    /// </summary>
    [SkippableFact]
    public void UnitFrames_MatchJournalSwapChecks()
    {
        int checkedClusters = 0, positions = 0;
        var mismatches = new List<string>();
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var art = SharedContent.Load(Data).MapArt;
        foreach (var (run, save, times) in Drags)
        {
            string dir = Path.Combine(GameSaveTests.CorpusDir(), run);
            string path = Path.Combine(dir, save);
            Skip.IfNot(File.Exists(path), $"нет {run}/{save}");
            var clusters = Directory.GetFiles(dir, "passive-*.jsonl").Order(StringComparer.Ordinal).SelectMany(f => Clusters(f))
                                    .Where(c => times.Contains(c.T)).ToList();
            Assert.Equal(times.Length, clusters.Count);
            var looks = MapLooksLoader.Load(path);
            foreach (var c in clusters)
            {
                var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), art, book);
                foreach (var (group, g) in c.Groups) session.SetCells(group, CellsOf(session, group, g.Occupants));
                foreach (var (group, g) in c.Groups)
                    for (int p = 0; p < UnitGroupLayout.Positions; p++)
                    {
                        var want = g.Ret[p] == 0 ? DropFrame.Valid : DropFrame.None;
                        var got = DropFrames.Unit(session, c.From, c.FromPos, group, p);
                        positions++;
                        if (got != want) mismatches.Add($"{run} t={c.T} {c.From}:{c.FromPos} → {group}:{p} журнал {g.Ret[p]:x} порт {got}");
                    }
                output.WriteLine($"{run} t={c.T} {c.From}:{c.FromPos} " + string.Join(" ", c.Groups.Select(kv =>
                    $"{kv.Key}={string.Concat(kv.Value.Ret.Select(r => r.ToString("x")))}")));
                checkedClusters++;
            }
        }
        output.WriteLine($"начал {checkedClusters}, позиций {positions}, расхождений {mismatches.Count}");
        foreach (string m in mismatches) output.WriteLine(m);
        Assert.Equal(20, checkedClusters);
        Assert.Empty(mismatches);
    }

    /// <summary>Клетки группы по журналу (занятые позиции; большой — один id в двух клетках); порядок воинов — как в сохранении, новые — в
    /// конец.</summary>
    private static UnitCells CellsOf(GameSession session, string group, string?[] occupants)
    {
        string[] cells = [.. occupants.Select(o => o ?? "")];
        var present = cells.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var before = session.GroupCells(group)?.Units ?? [];
        var units = before.Where(id => present.Contains(id, StringComparer.OrdinalIgnoreCase))
                          .Concat(present.Where(id => !before.Contains(id, StringComparer.OrdinalIgnoreCase))).ToList();
        return new UnitCells(units, cells);
    }

    // --- окна ---

    /// <summary>Рамка — место ячейки (колонки), картинка по середине: у 74×89 сдвиг −2,−2, у 152×85 — −1,0 (<c>0x56e487</c>); элемент
    /// стоит после портрета и подписи и до значков ячейки (<c>0x56e02d</c>: рамка — в первом проходе, значки <c>0x56e2e7</c> — во втором).</summary>
    [Fact]
    public void Layout_FrameElementsCenteredBetweenTextAndIcons()
    {
        var first = new UiRect(190, 68, 70, 85);
        var screen = UnitGroupLayout.Expand(new UiScreen { Id = "T", Elements = [new UiElement { Id = "IMG_LSLOT1", Kind = UiKind.Image, Rect = first }] },
                                            "IMG_LSLOT1", "L", leftSide: true);
        var ids = screen.Elements.Select(e => e.Id).ToList();
        for (int p = 0; p < UnitGroupLayout.Positions; p++)
        {
            var e = screen.Elements.First(x => x.Id == UnitGroupLayout.DropImage("L", p));
            Assert.Equal(UnitGroupLayout.Cell(first, true, p), e.Rect);
            int at = ids.IndexOf(e.Id);
            Assert.True(at > ids.IndexOf(UnitGroupLayout.CellText("L", p)));
            Assert.True(at < ids.IndexOf(UnitGroupLayout.Icon("L", p.ToString(System.Globalization.CultureInfo.InvariantCulture), UnitGroupLayout.UpgradeIcon)));
        }
        for (int row = 0; row < 3; row++)
        {
            var e = screen.Elements.First(x => x.Id == UnitGroupLayout.BigDropImage("L", row));
            Assert.Equal(UnitGroupLayout.Column(first, true, row), e.Rect);
            Assert.True(ids.IndexOf(e.Id) > ids.IndexOf(UnitGroupLayout.BigText("L", row)));
            Assert.True(ids.IndexOf(e.Id) < ids.IndexOf(UnitGroupLayout.Icon("L", $"B{row}", UnitGroupLayout.UpgradeIcon)));
        }
        Assert.Equal((-2, -2), UiImagePlacement.Offset(UiImageAlign.Center, 70, 85, 74, 89));
        Assert.Equal((-1, 0), UiImagePlacement.Offset(UiImageAlign.Center, 150, 85, 152, 85));
        Assert.Equal("DRAGDROP_LARGE_INVALID", DropFrames.Image(DropFrame.Invalid, large: true));
        Assert.Equal("DRAGDROP_SMALL_VALID", DropFrames.Image(DropFrame.Valid, large: false));
        Assert.Null(DropFrames.Image(DropFrame.None, large: true));
    }

    /// <summary>
    /// Окно армии столицы, день 5 (гость <c>.xxx.x</c>, гарнизон <c>bb.x..</c> — большой в колонке 0): до нажатия рамок нет; нажатие на
    /// воина — рамки у обеих групп сразу; после броска — снова нет. Рамка и бросок не расходятся: жёлтая там и только там, где бросок
    /// переставляет клетки (или это своя клетка/колонка — обмен с собой). У большого — одна рамка на колонку (<c>VV</c>).
    /// </summary>
    [SkippableFact]
    public void City_UnitDrag_FramesAgreeWithDrop()
    {
        var looks = Day5();
        var probe = City(Session(looks));
        Assert.Equal("......", DropFrames.Line(probe.Ui, CityStackLogic.Visitor));
        Assert.Equal("......", DropFrames.Line(probe.Ui, CityStackLogic.Garrison));
        var sources = new List<(string Group, int Pos)>();
        foreach (string g in (string[])[CityStackLogic.Visitor, CityStackLogic.Garrison])
            for (int p = 0; p < UnitGroupLayout.Positions; p++)
                if (GroupCells(probe, g) is { } cells && cells.Cells[p].Length > 0 && cells.PositionOf(cells.Cells[p]) == p) sources.Add((g, p));
        int valid = 0, none = 0, big = 0;
        foreach (var from in sources)
        {
            var w = City(Session(looks));
            var (fx, fy) = CityCell(w.Ui, from.Group, from.Pos);
            w.Logic.Press(fx, fy, w.Ui);
            string l = DropFrames.Line(w.Ui, CityStackLogic.Visitor), r = DropFrames.Line(w.Ui, CityStackLogic.Garrison);
            output.WriteLine($"{from.Group}{from.Pos}: L={l} R={r}");
            if (r.Contains("VV", StringComparison.Ordinal) || l.Contains("VV", StringComparison.Ordinal)) big++;
            w.Logic.Release(fx, fy, w.Ui);
            Assert.Equal("......", DropFrames.Line(w.Ui, CityStackLogic.Visitor));
            foreach (string g in (string[])[CityStackLogic.Visitor, CityStackLogic.Garrison])
                for (int p = 0; p < UnitGroupLayout.Positions; p++)
                {
                    char frame = char.ToLowerInvariant((g == CityStackLogic.Visitor ? l : r)[p]);
                    var drop = City(Session(looks));
                    string before = Both(drop);
                    drop.Logic.Press(fx, fy, drop.Ui);
                    var (tx, ty) = CityCell(drop.Ui, g, p);
                    drop.Logic.Release(tx, ty, drop.Ui);
                    bool moved = Both(drop) != before;
                    bool own = g == from.Group && SameColumn(GroupCells(probe, g)!, from.Pos, p);
                    Assert.True(frame == 'v' == (moved || own), $"{from.Group}{from.Pos} → {g}{p}: рамка {frame}, бросок {(moved ? "переставил" : "нет")}");
                    if (frame == 'v') valid++;
                    else none++;
                }
        }
        output.WriteLine($"источников {sources.Count}: жёлтых {valid}, без рамки {none}, с колонкой большого {big}");
        Assert.True(valid > 0 && none > 0 && big > 0);
    }

    /// <summary>Инструменты окна армии (<c>CMidDropAction</c>): рамки, пока инструмент нажат. Лечение — раненый жёлтая, здоровый и павший
    /// красная; воскрешение — павший жёлтая, живой красная; без храма — рамок нет; «Остановить продвижение» — лидер и страж красная;
    /// отпуск — лидер при воинах без рамки, страж гарнизона красная.</summary>
    [SkippableFact]
    public void City_Tools_Frames()
    {
        var session = Session(Day5());
        var w = City(session);
        var visitor = GroupCells(w, CityStackLogic.Visitor)!;
        var fort = session.Map.Sites.First(s => s.Id == w.Logic.FortId);
        string leader = session.Map.Stacks[fort.StackId].LeaderId;
        int lead = visitor.PositionOf(leader);
        int[] others = [.. Enumerable.Range(0, 6).Where(p => visitor.Cells[p].Length > 0 && p != lead)];
        Assert.True(others.Length >= 2);
        int wounded = others[0], dead = others[1];
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string id in visitor.Units) units[id] = units[id] with { Hp = session.HpMax(units[id]) };
        units[visitor.Cells[wounded]] = units[visitor.Cells[wounded]] with { Hp = 1 };
        units[visitor.Cells[dead]] = units[visitor.Cells[dead]] with { Hp = 0 };
        var human = session.Map.Players[fort.Owner];
        var book = session.RequireBook();
        string temple = book.Economy.Buildings.First(b => b.Value.Category == Healing.TempleCategory).Key;
        var players = session.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[human.Id] = human with { Bank = new Bank(99999, 0, 0, 0, 0, 0), Buildings = [.. human.Buildings, temple] };
        w = City(Rebuilt(session, GameSession.With(session.Map, units: units, players: players)));

        string Tool(string toggle)
        {
            w.Logic.Handle(new UiToggled(toggle, true), w.Ui);
            string line = DropFrames.Line(w.Ui, CityStackLogic.Visitor) + "|" + DropFrames.Line(w.Ui, CityStackLogic.Garrison);
            w.Logic.Handle(new UiToggled(toggle, false), w.Ui);
            output.WriteLine($"{toggle}: {line}");
            return line;
        }

        string heal = Tool("TOG_HEAL");
        Assert.Equal('v', heal[wounded]);
        Assert.Equal('i', heal[dead]);
        Assert.Equal('i', heal[lead]);   // лидер здоров
        string revive = Tool("TOG_REVIVE");
        Assert.Equal('v', revive[dead]);
        Assert.Equal('i', revive[wounded]);
        Assert.All(Enumerable.Range(0, 6).Where(p => visitor.Cells[p].Length == 0), p => Assert.Equal('.', revive[p]));
        string grow = Tool("TOG_DYNLEVEL");
        Assert.Equal('i', grow[lead]);
        Assert.Equal('v', grow[wounded]);
        string dismiss = Tool("TOG_DISMISS");
        Assert.Equal('.', dismiss[lead]);   // лидер при воинах (0x5df266 код 5)
        Assert.Equal('v', dismiss[wounded]);
        Assert.Equal("......|......", DropFrames.Line(w.Ui, CityStackLogic.Visitor) + "|" + DropFrames.Line(w.Ui, CityStackLogic.Garrison));
        var garrison = GroupCells(w, CityStackLogic.Garrison)!;
        int guardian = Enumerable.Range(0, 6).FirstOrDefault(p => garrison.Cells[p].Length > 0
            && book.CategoryOf(session.Map.Units[garrison.Cells[p]].TypeId) == D2.Rules.Battle.UnitCategory.Guardian, -1);
        output.WriteLine($"страж гарнизона: {guardian}");
        if (guardian >= 0)
        {
            int at = UnitGroupLayout.Positions + 1 + guardian;
            Assert.Equal('i', char.ToLowerInvariant(dismiss[at]));
            Assert.Equal('i', char.ToLowerInvariant(grow[at]));
        }

        // Без храма лечения и воскрешения рамок нет (0x5dcc28).
        players[human.Id] = human with { Buildings = [.. human.Buildings.Where(b => !(book.Economy.Buildings.TryGetValue(b, out var t) && t.Category == Healing.TempleCategory))] };
        w = City(Rebuilt(session, GameSession.With(session.Map, units: units, players: players)));
        Assert.Equal("......|......", Tool("TOG_HEAL"));
    }

    /// <summary>
    /// Окно отряда (день 24, <c>S073KC0001</c>): воин — жёлтые рамки у всех шести позиций своей группы (пустых тоже); зелье лечения из
    /// сумки — раненый жёлтая, здоровый и павший красная, пустая позиция — нет (<c>0x5de495</c>); отпуск — лидер при воинах без рамки.
    /// </summary>
    [SkippableFact]
    public void ManageStack_UnitPotionAndTool_Frames()
    {
        const string stackId = "S073KC0001", instance = "S073IMfffd";
        var session = Session(Day24());
        var stack = session.Map.Stacks[stackId];
        var cells = session.GroupCells(stackId)!;
        int lead = cells.PositionOf(stack.LeaderId);
        int[] others = [.. Enumerable.Range(0, 6).Where(p => cells.Cells[p].Length > 0 && p != lead && cells.PositionOf(cells.Cells[p]) == p)];
        Assert.True(others.Length >= 2);
        var potion = session.Book!.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.PotionHeal);
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stackId] = stack with { Items = [.. stack.Items, instance] };
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string id in cells.Units) units[id] = units[id] with { Hp = session.HpMax(units[id]) };
        units[cells.Cells[others[0]]] = units[cells.Cells[others[0]]] with { Hp = 1 };
        units[cells.Cells[others[1]]] = units[cells.Cells[others[1]]] with { Hp = 0 };
        var types = session.Map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[instance] = potion.Id;
        session = Rebuilt(session, GameSession.With(session.Map, stacks: stacks, units: units, itemTypes: types));
        var c = Context(session, stack.Owner);
        var logic = new ManageStackLogic(c, stackId, Screens(), StackTexts.Empty);
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == ManageStackLogic.Screen)));
        logic.Open(ui);
        Assert.Equal("......", DropFrames.Line(ui, ManageStackLogic.Group));

        var anchor = ui.Screen.Elements.First(e => e.Id == "IMG_SLOT1").Rect;
        var (ux, uy) = Center(UnitGroupLayout.Cell(anchor, true, others[0]));
        logic.Press(ux, uy, ui);
        string unit = DropFrames.Line(ui, ManageStackLogic.Group);
        output.WriteLine($"воин {others[0]}: {unit} | {UnitCellsView.Line(c, stackId)}");
        Assert.All(unit, ch => Assert.Equal('v', char.ToLowerInvariant(ch)));
        logic.Release(ux, uy, ui);
        Assert.Equal("......", DropFrames.Line(ui, ManageStackLogic.Group));

        int index = logic.BagCells.ToList().FindIndex(x => x.Ids.Contains(instance, StringComparer.OrdinalIgnoreCase));
        Assert.True(index >= 0);
        ui.Select("MLBOX_INVENTORY", index);
        logic.Press(484 + 10 + ((index - ui.ListTop("MLBOX_INVENTORY")) * 92), 404 + 10, ui);
        string drink = DropFrames.Line(ui, ManageStackLogic.Group);
        output.WriteLine($"зелье: {drink}");
        Assert.Equal('v', drink[others[0]]);
        Assert.Equal('i', drink[others[1]]);
        Assert.Equal('i', char.ToLowerInvariant(drink[lead]));
        Assert.All(Enumerable.Range(0, 6).Where(p => cells.Cells[p].Length == 0), p => Assert.Equal('.', drink[p]));
        logic.Release(10, 10, ui);
        Assert.Equal("......", DropFrames.Line(ui, ManageStackLogic.Group));

        logic.Handle(new UiToggled("TOG_DISMISS", true), ui);
        string dismiss = DropFrames.Line(ui, ManageStackLogic.Group);
        output.WriteLine($"отпуск: {dismiss}");
        Assert.Equal('.', char.ToLowerInvariant(dismiss[lead]));
        Assert.Equal('v', dismiss[others[0]]);
    }

    /// <summary>
    /// «Нельзя»: у полной чужой группы пустые позиции без рамки (обмен с занятой — жёлтая), лидер на месте цели — без рамки
    /// (<c>0x56e839</c>); тащат лидера — у чужой группы рамок нет вовсе (код 3), своя — все жёлтые. Окно обмена, день 24.
    /// </summary>
    [SkippableFact]
    public void Exchange_LeadershipAndLeader_NoFrame()
    {
        const string main = "S073KC0001", second = "S073KC0075";
        var session = Session(Day24());
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[second] = stacks[second] with { X = stacks[main].X + 1, Y = stacks[main].Y };
        session = Rebuilt(session, GameSession.With(session.Map, stacks: stacks));
        var mainCells = session.GroupCells(main)!;
        bool full = mainCells.Occupied >= session.GroupCapacity(main);
        var c = Context(session, stacks[main].Owner);
        var logic = new ExchangeLogic(c, main, second, Screens());
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == ExchangeLogic.Screen)));
        logic.Open(ui);
        var secondCells = session.GroupCells(second)!;
        int from = Enumerable.Range(0, 6).First(p => secondCells.Cells[p].Length > 0 && secondCells.Cells[p] != stacks[second].LeaderId
                                                     && secondCells.PositionOf(secondCells.Cells[p]) == p);
        var (fx, fy) = ExchangeCell(ui, ExchangeLogic.Right, from);
        logic.Press(fx, fy, ui);
        string l = DropFrames.Line(ui, ExchangeLogic.Left), r = DropFrames.Line(ui, ExchangeLogic.Right);
        output.WriteLine($"воин R{from}: L={l} R={r} L-клетки={UnitCellsView.Line(c, main)} полон={full}");
        Assert.Equal('.', char.ToLowerInvariant(l[mainCells.PositionOf(stacks[main].LeaderId)]));
        Assert.True(full);
        Assert.All(Enumerable.Range(0, 6).Where(p => mainCells.Cells[p].Length == 0), p => Assert.Equal('.', l[p]));
        Assert.All(r, ch => Assert.Equal('v', char.ToLowerInvariant(ch)));
        logic.Release(fx, fy, ui);

        int leader = secondCells.PositionOf(stacks[second].LeaderId);
        (fx, fy) = ExchangeCell(ui, ExchangeLogic.Right, leader);
        logic.Press(fx, fy, ui);
        output.WriteLine($"лидер R{leader}: L={DropFrames.Line(ui, ExchangeLogic.Left)} R={DropFrames.Line(ui, ExchangeLogic.Right)}");
        Assert.Equal("......", DropFrames.Line(ui, ExchangeLogic.Left));
        Assert.All(DropFrames.Line(ui, ExchangeLogic.Right), ch => Assert.Equal('v', char.ToLowerInvariant(ch)));
    }

    /// <summary>Наёмник из списка (хвост <c>0x500d03</c>): малый — пустые позиции при свободных клетках; большой — пустая колонка и больше
    /// одной свободной клетки; красной нет.</summary>
    [Fact]
    public void Mercenary_FramesByFreeCells()
    {
        var cells = new UnitCells(["lead", "a"], ["", "", "lead", "", "a", ""]);
        Assert.Equal("vv.v.v", Line(p => DropFrames.Mercenary(false, 2, cells, p)));
        Assert.Equal("......", Line(p => DropFrames.Mercenary(false, 0, cells, p)));
        Assert.Equal("vv....", Line(p => DropFrames.Mercenary(true, 2, cells, p)));
        Assert.Equal("......", Line(p => DropFrames.Mercenary(true, 1, cells, p)));
    }

    private static string Line(Func<int, DropFrame> state) =>
        new([.. Enumerable.Range(0, 6).Select(p => state(p) switch { DropFrame.Valid => 'v', DropFrame.Invalid => 'i', _ => '.' })]);

    private static MapLooks Day5() => Save("rastlennaya-vlast-dwarf-t05-011.sg");

    private static MapLooks Day24() => Save("rastlennaya-vlast-dwarf-t24-045.sg");

    private static MapLooks Save(string name)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", name);
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        return Loaded.GetOrAdd(path, MapLooksLoader.Load);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MapLooks> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<GameBook> Book = new(() => GameBook.Load(Path.Combine(Data, "rules")));
    private static readonly Lazy<MapArtData> Art = new(() => SharedContent.Load(Data).MapArt);
    private static readonly Lazy<IReadOnlyList<UiScreen>> ScreenSet = new(() => UiScreens.Load(Path.Combine(Data, "ui", "screens.json")));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Strings = new(() => UiStrings.Load(Path.Combine(Data, "ui"), "ru"));
    private static readonly Lazy<UnitFaces> Faces = new(() => UnitFaces.Load(Path.Combine(Data, "rules")));

    /// <summary>Сессия из облика сохранения — заново на каждый бросок (бросок меняет сессию).</summary>
    private static GameSession Session(MapLooks looks) => new(new GameState(GameInfo.FromMap("PtC", looks), looks), Art.Value, Book.Value);

    private static GameSession Rebuilt(GameSession session, MapState map) =>
        new(session.State with { Looks = GameSession.Retold(session.Looks, map, session.Looks.Stacks) }, session.Art, session.Book);

    private static IReadOnlyList<UiScreen> Screens() => ScreenSet.Value;

    private static WindowContext Context(GameSession session, string viewer) => new(session, viewer, Strings.Value, Faces.Value);

    private sealed record CityWindow(CityStackLogic Logic, UiScreenState Ui, GameSession Session);

    private static CityWindow City(GameSession session)
    {
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        var capital = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == human.Id);
        var logic = new CityStackLogic(Context(session, human.Id), capital.Id, Screens());
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == CityStackLogic.Screen)));
        logic.Open(ui);
        return new CityWindow(logic, ui, session);
    }

    private static UnitCells? GroupCells(CityWindow w, string group)
    {
        string id = group == CityStackLogic.Visitor ? w.Session.Map.Sites.First(s => s.Id == w.Logic.FortId).StackId : w.Logic.FortId;
        return id.Length > 0 ? w.Session.GroupCells(id) : null;
    }

    private static string Both(CityWindow w) =>
        $"{string.Join(",", GroupCells(w, CityStackLogic.Visitor)?.Cells ?? [])}|{string.Join(",", GroupCells(w, CityStackLogic.Garrison)?.Cells ?? [])}";

    private static bool SameColumn(UnitCells cells, int from, int to)
    {
        bool big = cells.Cells[from] == cells.Cells[GroupArrange.Pair(from)]
                   || (cells.Cells[to].Length > 0 && cells.Cells[to] == cells.Cells[GroupArrange.Pair(to)]);
        return big ? GroupArrange.ColumnFront(from) == GroupArrange.ColumnFront(to) : from == to;
    }

    private static (int X, int Y) CityCell(UiScreenState ui, string group, int pos) =>
        GroupCell(ui, group == CityStackLogic.Visitor, pos);

    private static (int X, int Y) ExchangeCell(UiScreenState ui, string side, int pos) => GroupCell(ui, side == ExchangeLogic.Left, pos);

    private static (int X, int Y) GroupCell(UiScreenState ui, bool left, int pos)
    {
        var first = ui.Screen.Elements.First(e => e.Id == (left ? "IMG_LSLOT1" : "IMG_RSLOT1")).Rect;
        return Center(UnitGroupLayout.Cell(first, left, pos));
    }

    private static (int X, int Y) Center(UiRect r) => (r.X + (r.Width / 2), r.Y + (r.Height / 2));

    /// <summary>Начало перетаскивания в журнале: шесть проверок <c>swap_check</c> на каждую группу окна с одной и той же клеткой
    /// источника и временем.</summary>
    internal sealed record Cluster(long T, string From, int FromPos, Dictionary<string, (string?[] Occupants, int[] Ret)> Groups);

    internal static List<Cluster> Clusters(string journal)
    {
        var list = new List<Cluster>();
        foreach (string line in File.ReadLines(journal))
        {
            if (!line.Contains("\"ev\":\"swap_check\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            long t = e.GetProperty("t").GetInt64();
            string from = e.GetProperty("from").GetString()!, to = e.GetProperty("to").GetString()!;
            int fromPos = e.GetProperty("from_pos").GetInt32(), toPos = e.GetProperty("to_pos").GetInt32();
            string? other = e.TryGetProperty("other", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            int ret = e.GetProperty("ret").GetInt32();
            var last = list.Count > 0 ? list[^1] : null;
            if (last is null || last.T != t || last.From != from || last.FromPos != fromPos || last.Groups.Values.Sum(g => g.Ret.Count(r => r >= 0)) >= 12)
                list.Add(last = new Cluster(t, from, fromPos, []));
            if (!last.Groups.TryGetValue(to, out var g))
                last.Groups[to] = g = (new string?[UnitGroupLayout.Positions], Enumerable.Repeat(-1, UnitGroupLayout.Positions).ToArray());
            g.Occupants[toPos] = other;
            g.Ret[toPos] = ret;
        }
        return [.. list.Where(c => c.Groups.Values.All(g => g.Ret.All(r => r >= 0)))];
    }
}
