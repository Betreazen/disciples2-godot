using System.Text.Json;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Magic;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Зелья из сумки и со склада на воинов в окнах армии и обмена (<see cref="PotionDrops"/>, рамки <see cref="DropFrames.Potion"/>):
/// разбор <c>0x500d03</c>/<c>0x4ff396</c>/<c>0x5de495</c>/<c>0x5d70f9</c>, журналы человека <c>potion_use</c> в <c>DLG_CITY_STACK</c>.</summary>
public partial class DropFramesTests
{
    /// <summary>Зелья человека в окне армии по журналам (окно сверху — <c>DLG_CITY_STACK</c>, <c>potion_use</c> вне боя): прогон, префикс
    /// сохранений, <c>seq</c> событий. gaps — пять зелий со склада укрепления (<c>MLBOX_RINVENTORY</c>) воинам отряда-гостя, держатель —
    /// укрепление, воин — другой группы; human, день 5 — зелье из сумки гостя (<c>MLBOX_LINVENTORY</c>) его лидеру.</summary>
    private static readonly (string Run, string Saves, long[] Seqs)[] CityPotions =
    [
        ("20260920-gaps", "ai-g3-cure-fear-human-t01-", [516576, 564249, 1234553, 1293690, 1358473]),
        ("20260919-human", "rastlennaya-vlast-dwarf-t05-", [2199562]),
    ];

    /// <summary>
    /// Харнесс по журналу: каждое зелье человека в окне армии повторяется тем же жестом в окне порта — нажатие на ячейку предмета в списке
    /// держателя (сумка гостя или склад), бросок на воина. Рамка воина при нажатии — жёлтая, бросок проходит без отказа, здоровье после — как
    /// <c>hp_after</c> журнала, у держателя предметов этого типа на один меньше. Сохранение — то, где у держателей есть все предметы событий
    /// (между событиями человек ещё лечил в храме, поэтому здоровье цели перед каждым событием берётся из <c>hp_before</c>).
    /// </summary>
    [SkippableFact]
    public void CityStack_PotionsMatchJournal()
    {
        int done = 0;
        foreach (var (run, prefix, seqs) in CityPotions)
        {
            string dir = Path.Combine(GameSaveTests.CorpusDir(), run);
            Skip.IfNot(Directory.Exists(dir), $"нет {run}");
            var events = Directory.GetFiles(dir, "passive-*.jsonl").Order(StringComparer.Ordinal).SelectMany(PotionUses)
                                  .Where(e => seqs.Contains(e.Seq)).ToList();
            Assert.Equal(seqs.Length, events.Count);
            var looks = Directory.GetFiles(dir, prefix + "*.sg").Order(StringComparer.Ordinal).Select(Loaded2)
                                 .LastOrDefault(l => events.All(e => Holds(l, e.Holder, e.Item)));
            Assert.NotNull(looks);
            var session = Session(looks);
            foreach (var e in events)
            {
                var map = session.Map;
                var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                units[e.Target] = units[e.Target] with { Hp = e.HpBefore };
                session = Rebuilt(session, GameSession.With(map, units: units));
                string fortId = map.Stacks.TryGetValue(e.Holder, out var guest) ? guest.Inside : e.Holder;
                var fort = session.Map.Sites.First(s => string.Equals(s.Id, fortId, StringComparison.OrdinalIgnoreCase));
                if (!InWindow(session, fort, e.Target)) session = Enter(session, fort, e.Target);
                fort = session.Map.Sites.First(s => s.Id == fort.Id);
                var logic = new CityStackLogic(Context(session, fort.Owner), fort.Id, Screens());
                var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == CityStackLogic.Screen)));
                logic.Open(ui);
                string type = session.Map.ItemTypes[e.Item];
                bool store = string.Equals(e.Holder, fort.Id, StringComparison.OrdinalIgnoreCase);
                var list = store ? logic.StoreCells : logic.BagCells;
                int index = list.ToList().FindIndex(c => c.Ids.Contains(e.Item, StringComparer.OrdinalIgnoreCase));
                int before = Count(session, e.Holder, type);
                var (group, pos) = TargetCell(session, fort, e.Target);
                var (ix, iy) = ListCell(ui, store ? "MLBOX_RINVENTORY" : "MLBOX_LINVENTORY", index);
                logic.Press(ix, iy, ui);
                char frame = DropFrames.Line(ui, group)[pos];
                var (tx, ty) = CityCell(ui, group, pos);
                logic.Release(tx, ty, ui);
                int hp = session.Map.Units[e.Target].Hp;
                output.WriteLine($"{run} seq={e.Seq} {e.Holder}:{e.Item} ({type}) → {group}{pos} {e.Target}: рамка {frame}, " +
                                 $"здоровье {e.HpBefore}→{hp} (журнал {e.HpAfter}), отказ {logic.LastRefusal ?? "-"}");
                Assert.Equal('v', char.ToLowerInvariant(frame));
                Assert.Null(logic.LastRefusal);
                Assert.Equal(e.HpAfter, hp);
                Assert.Equal(before - 1, Count(session, e.Holder, type));
                Assert.Equal("......", DropFrames.Line(ui, group));   // после броска рамок нет
                done++;
            }
        }
        Assert.Equal(6, done);
    }

    /// <summary>
    /// Окно армии, день 5: зелье лечения из сумки гостя — на раненого воина гарнизона (держатель — сумка, воин — чужой группы окна:
    /// <c>0x5de495</c> группу не спрашивает); со склада — на раненого гостя. Рамки при нажатии — у обеих групп; здоровый — красная, бросок на
    /// него — сообщение <c>X005TA0594</c> и зелье на месте. Зелье воскрешения на живого — рамка жёлтая (<c>0x500d03</c>: код 5 у воскрешения —
    /// 0), бросок — сообщение <c>X005TA0595</c>, зелье не тратится (ответ пользователя 15.10: «навестись можно, но не применяется»).
    /// </summary>
    [SkippableFact]
    public void City_PotionAcrossGroups_FramesAndDrop()
    {
        var session = Session(Day5());
        var w = City(session);
        var visitor = GroupCells(w, CityStackLogic.Visitor)!;
        var garrison = GroupCells(w, CityStackLogic.Garrison)!;
        var fort = session.Map.Sites.First(s => s.Id == w.Logic.FortId);
        string guestId = fort.StackId;
        int gPos = Enumerable.Range(0, 6).First(p => garrison.Cells[p].Length > 0 && garrison.PositionOf(garrison.Cells[p]) == p);
        int vPos = Enumerable.Range(0, 6).First(p => visitor.Cells[p].Length > 0 && visitor.PositionOf(visitor.Cells[p]) == p);
        int vFull = Enumerable.Range(0, 6).Last(p => visitor.Cells[p].Length > 0 && visitor.PositionOf(visitor.Cells[p]) == p);
        Assert.NotEqual(vPos, vFull);
        var book = session.Book!;
        var heal = book.Items.Values.First(i => i.Category == ItemCategory.PotionHeal);
        var revive = book.Items.Values.First(i => i.Category == ItemCategory.PotionRevive);
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string id in visitor.Units.Concat(garrison.Units)) units[id] = units[id] with { Hp = session.HpMax(units[id]) };
        units[garrison.Cells[gPos]] = units[garrison.Cells[gPos]] with { Hp = 1 };
        units[visitor.Cells[vPos]] = units[visitor.Cells[vPos]] with { Hp = 1 };
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[guestId] = stacks[guestId] with { Items = [.. stacks[guestId].Items, "S073IMfff1", "S073IMfff2"] };
        var sites = session.Map.Sites.Select(s => s.Id == fort.Id ? s with { Items = [.. s.Items, "S073IMfff3"] } : s).ToList();
        var types = session.Map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        (types["S073IMfff1"], types["S073IMfff2"], types["S073IMfff3"]) = (heal.Id, revive.Id, heal.Id);
        session = Rebuilt(session, GameSession.With(session.Map, units: units, stacks: stacks, sites: sites, itemTypes: types));

        // Сумка гостя → раненый гарнизона.
        w = City(session);
        string Press(bool store, string item)
        {
            var cells = store ? w.Logic.StoreCells : w.Logic.BagCells;
            int index = cells.ToList().FindIndex(c => c.Ids.Contains(item, StringComparer.OrdinalIgnoreCase));
            var (x, y) = ListCell(w.Ui, store ? "MLBOX_RINVENTORY" : "MLBOX_LINVENTORY", index);
            w.Logic.Press(x, y, w.Ui);
            string line = DropFrames.Line(w.Ui, CityStackLogic.Visitor) + "|" + DropFrames.Line(w.Ui, CityStackLogic.Garrison);
            output.WriteLine($"{item} ({(store ? "склад" : "сумка")}): {line}");
            return line.ToLowerInvariant();
        }
        void Drop(string group, int pos)
        {
            var (x, y) = CityCell(w.Ui, group, pos);
            w.Logic.Release(x, y, w.Ui);
        }
        string bagLine = Press(store: false, "S073IMfff1");
        Assert.Equal('v', bagLine[7 + gPos]);
        Assert.Equal('v', bagLine[vPos]);
        Assert.Equal('i', bagLine[vFull]);
        Drop(CityStackLogic.Garrison, gPos);
        Assert.Null(w.Logic.LastRefusal);
        Assert.True(session.Map.Units[garrison.Cells[gPos]].Hp > 1);
        Assert.DoesNotContain("S073IMfff1", session.Map.Stacks[guestId].Items);
        Assert.Equal("......|......", DropFrames.Line(w.Ui, CityStackLogic.Visitor) + "|" + DropFrames.Line(w.Ui, CityStackLogic.Garrison));

        // Склад → раненый гость; здоровый — красная, отказ с сообщением.
        Press(store: true, "S073IMfff3");
        Drop(CityStackLogic.Visitor, vFull);
        Assert.Equal(nameof(PotionResult.AlreadyAtFullHp), w.Logic.LastRefusal);
        Assert.Contains(Tells(w.Logic), t => t == Strings.Value["X005TA0594"]);
        Assert.Contains("S073IMfff3", session.Map.Sites.First(s => s.Id == fort.Id).Items);
        Press(store: true, "S073IMfff3");
        Drop(CityStackLogic.Visitor, vPos);
        Assert.Null(w.Logic.LastRefusal);
        Assert.True(session.Map.Units[visitor.Cells[vPos]].Hp > 1);
        Assert.DoesNotContain("S073IMfff3", session.Map.Sites.First(s => s.Id == fort.Id).Items);

        // Воскрешение живому: рамка жёлтая, бросок — X005TA0595, зелье остаётся.
        string reviveLine = Press(store: false, "S073IMfff2");
        Assert.Equal('v', reviveLine[vFull]);
        Drop(CityStackLogic.Visitor, vFull);
        Assert.Equal(nameof(PotionResult.AlreadyAlive), w.Logic.LastRefusal);
        Assert.Contains(Tells(w.Logic), t => t == Strings.Value["X005TA0595"]);
        Assert.Contains("S073IMfff2", session.Map.Stacks[guestId].Items);
    }

    /// <summary>Окно обмена, день 24: зелье лечения из сумки левого отряда — на раненого воина правого (обе группы — <c>CDDStackGroup</c>):
    /// рамка жёлтая у обеих групп, бросок лечит, зелье уходит из левой сумки.</summary>
    [SkippableFact]
    public void Exchange_PotionFromOtherBag_Heals()
    {
        const string main = "S073KC0001", second = "S073KC0075", instance = "S073IMfff4";
        var session = Session(Day24());
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[second] = stacks[second] with { X = stacks[main].X + 1, Y = stacks[main].Y };
        stacks[main] = stacks[main] with { Items = [.. stacks[main].Items, instance] };
        var right = session.GroupCells(second)!;
        int pos = Enumerable.Range(0, 6).First(p => right.Cells[p].Length > 0 && right.PositionOf(right.Cells[p]) == p);
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[right.Cells[pos]] = units[right.Cells[pos]] with { Hp = 1 };
        var types = session.Map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[instance] = session.Book!.Items.Values.First(i => i.Category == ItemCategory.PotionHeal).Id;
        session = Rebuilt(session, GameSession.With(session.Map, stacks: stacks, units: units, itemTypes: types));
        var logic = new ExchangeLogic(Context(session, stacks[main].Owner), main, second, Screens());
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == ExchangeLogic.Screen)));
        logic.Open(ui);
        int index = logic.BagCells(ExchangeLogic.Left).ToList().FindIndex(c => c.Ids.Contains(instance, StringComparer.OrdinalIgnoreCase));
        var (ix, iy) = ListCell(ui, "MLBOX_LINVENTORY", index);
        logic.Press(ix, iy, ui);
        string r = DropFrames.Line(ui, ExchangeLogic.Right);
        output.WriteLine($"зелье слева: L={DropFrames.Line(ui, ExchangeLogic.Left)} R={r}");
        Assert.Equal('v', r[pos]);
        Assert.True(logic.DropAccepts(ExchangeCell(ui, ExchangeLogic.Right, pos).X, ExchangeCell(ui, ExchangeLogic.Right, pos).Y, ui));
        var (tx, ty) = ExchangeCell(ui, ExchangeLogic.Right, pos);
        logic.Release(tx, ty, ui);
        Assert.Null(logic.LastRefusal);
        Assert.True(session.Map.Units[right.Cells[pos]].Hp > 1);
        Assert.DoesNotContain(instance, session.Map.Stacks[main].Items);
        Assert.Equal("......", DropFrames.Line(ui, ExchangeLogic.Right));
    }

    // --- общее ---

    private sealed record PotionUse(long Seq, string Holder, string Target, string Item, int HpBefore, int HpAfter);

    private static IEnumerable<PotionUse> PotionUses(string journal)
    {
        foreach (string line in File.ReadLines(journal))
        {
            if (!line.Contains("\"ev\":\"potion_use\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.GetProperty("in_battle").GetBoolean()) continue;
            yield return new PotionUse(e.GetProperty("seq").GetInt64(), e.GetProperty("group").GetString()!, e.GetProperty("target").GetString()!,
                                       e.GetProperty("item").GetString()!, e.GetProperty("hp_before").GetInt32(), e.GetProperty("hp_after").GetInt32());
        }
    }

    private static MapLooks Loaded2(string path) => Loaded.GetOrAdd(path, MapLooksLoader.Load);

    private static bool Holds(MapLooks looks, string holder, string item)
    {
        var map = looks.Map;
        return map.Stacks.TryGetValue(holder, out var s) ? s.Items.Contains(item, StringComparer.OrdinalIgnoreCase)
            : map.Sites.Any(f => string.Equals(f.Id, holder, StringComparison.OrdinalIgnoreCase) && f.Items.Contains(item, StringComparer.OrdinalIgnoreCase));
    }

    private static int Count(GameSession session, string holder, string type) =>
        (session.ItemsOf(holder) ?? []).Count(i => string.Equals(session.Map.ItemTypes[i], type, StringComparison.OrdinalIgnoreCase));

    private static bool InWindow(GameSession session, MapSite fort, string unit) =>
        (string[])[fort.StackId, fort.Id] is var ids && ids.Any(id => id.Length > 0 && session.GroupCells(id)?.PositionOf(unit) >= 0);

    /// <summary>Отряд воина входит в укрепление гостем (gaps: сохранение — после боя, до входа в столицу; журнал — <c>stack_move</c> в клетку
    /// столицы, seq 399518, до открытия окна).</summary>
    private static GameSession Enter(GameSession session, MapSite fort, string unit)
    {
        var map = session.Map;
        var stack = map.Stacks.Values.First(s => s.UnitIds.Contains(unit, StringComparer.OrdinalIgnoreCase));
        Assert.True(fort.StackId.Length == 0, $"в {fort.Id} уже гость {fort.StackId}");
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = stack with { X = fort.X, Y = fort.Y, Inside = fort.Id };
        var sites = map.Sites.Select(s => s.Id == fort.Id ? s with { StackId = stack.Id } : s).ToList();
        return Rebuilt(session, GameSession.With(map, stacks: stacks, sites: sites));
    }

    /// <summary>Группа окна армии (гость или гарнизон) и позиция воина.</summary>
    private static (string Group, int Pos) TargetCell(GameSession session, MapSite fort, string unit)
    {
        foreach (var (group, id) in (ReadOnlySpan<(string, string)>)[(CityStackLogic.Visitor, fort.StackId), (CityStackLogic.Garrison, fort.Id)])
            if (id.Length > 0 && session.GroupCells(id) is { } cells && cells.PositionOf(unit) is var p and >= 0) return (group, p);
        throw new InvalidOperationException($"{unit} нет в окне {fort.Id}");
    }

    /// <summary>Точка ячейки списка картинок: список прокручен к ячейке, точка — в её левом верхнем углу.</summary>
    private static (int X, int Y) ListCell(UiScreenState ui, string list, int index)
    {
        Assert.True(index >= 0, $"нет ячейки в {list}");
        ui.Select(list, index);
        var e = ui.Screen.Elements.First(x => x.Id == list);
        var spec = e.List ?? new UiList();
        int col = index - ui.ListTop(list);
        return (e.Rect.X + (col * (spec.CellWidth + spec.GapX)) + 2, e.Rect.Y + 2);
    }

    private static List<string> Tells(IWindowLogic logic)
    {
        var tells = new List<string>();
        while (logic.TakeRequest() is { } r)
            if (r is UiTell t) tells.Add(t.Text);
        return tells;
    }
}
