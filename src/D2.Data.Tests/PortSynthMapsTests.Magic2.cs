using System.Text.Json;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>Карта 11 <c>port-synth-11-magic2</c> (<c>tools/synth-maps/maps_magic2.py</c>): литья, которые на карте 7 не сыграны или не
/// сверены числом, — по дням памятки; ослабление «без эффекта» — цели как задумано (ответ движка — окно X005TA0671 в журнале
/// оригинала); отпуск в столице, пустой мешок и частичное обучение.</summary>
public partial class PortSynthMapsTests
{
    private const string Magic2Map = "port-synth-11-magic2";

    private static int HiddenCount(bool[,] fog) => fog.Cast<bool>().Count(h => h);

    /// <summary>Литья памятки по дням: точечные дня 1 (ход, Сокрытие, земля, туман, развеивание), затем лечение и ход на всю карту; день 2 —
    /// туман и развеивание всей карты. Смена земли меняет ровно площадь (<c>AREA</c>²: равнина под маяком без объектов), развеивание в глубине
    /// тумана открывает человеку клетки, туман закрывает их у Империи вокруг её зрителя (у оригинала — сохранение после строки).</summary>
    [SkippableFact]
    public void Magic2_MemoCasts_GoThrough_DayByDay()
    {
        Skip.IfNot(MapIds().Contains(Magic2Map));
        var facts = Facts(Magic2Map);
        var session = Begin(Magic2Map, 1);
        string human = session.ActiveHuman;
        var zone = Zone(facts, "casts");
        string scrolls = Str(Zone(facts, "MG"), "stack"), wands = Str(Zone(facts, "MW"), "stack");
        foreach (var rs in zone.GetProperty("rested").EnumerateArray())
        {
            var stack = session.Map.Stacks[rs.GetString()!];
            session.MoveStack(stack.Id, stack.X, stack.Y + 1, 0, 2);
            Assert.Equal((stack.X, stack.Y + 1), (session.Map.Stacks[stack.Id].X, session.Map.Stacks[stack.Id].Y));
            Assert.True(session.Map.Stacks[stack.Id].Move < session.DayAllowance(session.Map.Stacks[stack.Id]), "запас хода RS полный — ход заклинанием откажет");
        }
        int terrainCasts = 0, unfogCasts = 0;
        string empire = session.Map.Stacks[zone.GetProperty("fog_watchers")[0].GetString()!].Owner;
        var casts = zone.GetProperty("casts").EnumerateArray().ToList();
        foreach (int day in new[] { 1, 2 })
        {
            if (day == 2) session.EndTurn();
            Assert.Equal(day, session.Map.Turn);
            foreach (var cast in casts.Where(c => c.GetProperty("day").GetInt32() == day)
                                      .OrderBy(c => Str(c, "source").Contains("вся карта", StringComparison.Ordinal) ? 1 : 0))
            {
                var (x, y) = XY(cast, "at");
                int cat = cast.GetProperty("category").GetInt32(), area = cast.GetProperty("area").GetInt32();
                var tiles = (D2.Rules.Map.Tile[,])session.Map.Tiles.Clone();
                int ownHidden = HiddenCount(session.Map.Fog[session.Map.Players[human].FogId]);
                int empireHidden = HiddenCount(session.Map.Fog[session.Map.Players[empire].FogId]);
                string spell = Str(cast, "spell");
                string caster = Str(cast, "source") == "посох" ? wands : scrolls;
                string? refused = cast.GetProperty("item").ValueKind == JsonValueKind.Null
                    ? session.CastSpell(human, spell, x, y, useSpellBank: false)
                    : session.CastItem(caster, session.Map.Stacks[caster].Items.First(i => Same(session.Map.ItemTypes[i], Str(cast, "item"))), x, y);
                int changed = Enumerable.Range(0, session.Map.Size).Sum(cx => Enumerable.Range(0, session.Map.Size)
                                                                                       .Count(cy => tiles[cx, cy].Raw != session.Map.Tiles[cx, cy].Raw));
                int opened = ownHidden - HiddenCount(session.Map.Fog[session.Map.Players[human].FogId]);
                int closed = HiddenCount(session.Map.Fog[session.Map.Players[empire].FogId]) - empireHidden;
                Out.WriteLine($"день {day} {Str(cast, "key")} {Str(cast, "source")} {spell} ({x},{y}): {refused ?? "да"}; земля {changed}, " +
                              $"открыто своих {opened}, закрыто у Империи {closed}");
                Assert.True(refused is null, $"{Str(cast, "key")} {spell}: {refused}");
                if (cat == 11)
                {
                    terrainCasts++;
                    Assert.Equal(area * area, changed);   // равнина под маяком без объектов и вдали от края
                }
                if (cat == 7 && area != 999)
                {
                    unfogCasts++;
                    Assert.True(opened > 0, $"{Str(cast, "key")}: в глубине тумана ничего не открыто");
                }
            }
        }
        Assert.Equal(3, terrainCasts);
        Assert.Equal(3, unfogCasts);
        Assert.True(session.IsInvisible(Str(zone, "hidden")));
    }

    /// <summary>Ослабление «без эффекта»: цели как задумано — Латник LW1 с бронёй 20, у LW2 лидер без брони и Латник-солдат, Паралич гасит
    /// запас хода Гнома P1; литья, где эффект есть по любой гипотезе (LW1, первый Паралич, Опутывание свежего LW5), проходят. Спорные (LW2,
    /// повторный Паралич LW3, Опутывание парализованного LW4) — только печать: ответ — X005TA0671 у движка.</summary>
    [SkippableFact]
    public void Magic2_Weaken_TargetsAsDesigned()
    {
        Skip.IfNot(MapIds().Contains(Magic2Map));
        var facts = Facts(Magic2Map);
        var session = Begin(Magic2Map, 1);
        var book = session.Book!;
        var map = session.Map;
        int Armor(string unit) => book.Rules.Units[map.Units[unit].TypeId].Armor;
        var weaken = Zone(facts, "LW").GetProperty("casts").EnumerateArray().ToDictionary(c => Str(c, "key"));
        var lw1 = map.Stacks[Str(weaken["LW1"], "target")];
        Assert.Equal(20, Armor(lw1.LeaderId));
        var lw2 = map.Stacks[Str(weaken["LW2"], "target")];
        Assert.Equal(0, Armor(lw2.LeaderId));
        Assert.Contains(lw2.UnitIds, u => Armor(u) == 20);
        foreach (string key in new[] { "LW1", "LW2", "LW3a", "LW3", "LW4", "LW5" })
        {
            var c = weaken[key];
            var (x, y) = XY(c, "at");
            string? refused = session.CastSpell(session.ActiveHuman, Str(c, "spell"), x, y, useSpellBank: false);
            var target = session.Map.Stacks[Str(c, "target")];
            Out.WriteLine($"{key} {Str(c, "what")}: {refused ?? "наложено"}; ход цели {target.Move}");
            if (key is "LW1" or "LW3a" or "LW5")
            {
                Assert.True(refused is null, $"{key}: {refused}");
                Assert.Contains(session.Map.SpellEffects, e => Same(e.UnitId, target.LeaderId) && Same(e.ModifierId, session.Book!.Spells[Str(c, "spell")].ModifierId));
            }
            if (key == "LW3a") Assert.Equal(0, target.Move);
        }
    }

    /// <summary>Карта 6, несыгранное: воин гарнизона столицы отпускается, страж — отказ (код 2); бросок мешка на пустую клетку проходит
    /// (что останется после закрытия пустым — ответ движка); у тренера казны (300) меньше цены полного обучения — Горгулья учится частично.</summary>
    [SkippableFact]
    public void Magic2_Actions_GarrisonEmptyBagPartialTraining()
    {
        Skip.IfNot(MapIds().Contains(Magic2Map));
        var facts = Facts(Magic2Map);
        var session = Begin(Magic2Map, 1);
        var capital = facts.GetProperty("capital");
        string cap = Str(capital, "id");
        Assert.Equal(0, session.DismissUnit(cap, capital.GetProperty("garrison")[0].GetString()!));
        Assert.Equal(2, session.DismissUnit(cap, Str(capital, "guardian")));
        var eb = Zone(facts, "EB");
        var (dx, dy) = XY(eb, "drop_at");
        Assert.Null(session.DropBag(Str(eb, "stack"), dx, dy, out string bag));
        session.CloseBag(bag);
        Out.WriteLine($"пустой мешок после закрытия: {(session.Map.Sites.Any(s => Same(s.Id, bag)) ? "остался" : "исчез")}");
        var tr = Zone(facts, "TR");
        string pupil = Str(tr, "stack"), trainer = Str(tr, "target");
        var stack = session.Map.Stacks[pupil];
        var gargoyles = stack.UnitIds.Where(u => !Same(u, stack.LeaderId)).ToList();
        Assert.Equal(2, gargoyles.Count);
        string human = session.ActiveHuman;
        long gold = session.Map.Players[human].Bank.Gold;
        var first = session.Map.Units[gargoyles[0]];
        int next = session.Book!.XpNextOf(first.TypeId, first.Level);
        Assert.Null(session.Train(pupil, trainer, gargoyles[0], out var paid));
        int xp = session.Map.Units[gargoyles[0]].Xp;
        Out.WriteLine($"казна {gold}: Горгулья {first.Xp} → {xp} из {next - 1}, заплачено {paid.Gold}; вторая: " +
                      (session.Train(pupil, trainer, gargoyles[1], out _) ?? "обучена"));
        Assert.True(xp < next - 1, "обучение не частичное — казны хватило");
        Assert.True(paid.Gold > 0 && paid.Gold <= gold);
    }
}
