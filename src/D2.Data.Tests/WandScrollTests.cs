using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Magic;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Посох и свиток (<c>DLG_WAND_SCROLL</c>, <c>docs/ui-windows.md</c> §11): окно, литьё предметом — свиток расходуется, посох
/// платит ценой и раз в день.</summary>
[Trait("Category", "Corpus")]
public class WandScrollTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableFact]
    public void WandScroll_WindowScrollConsumedWandOncePerDay()
    {
        var (session, stackId, scroll, wand) = Game();
        var owner = session.Map.Stacks[stackId].Owner;
        var context = new WindowContext(session, owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        Assert.True(WandScrollLogic.CanOpen(context, stackId));

        var logic = new WandScrollLogic(context, stackId, SpellBookLogic.LoadTexts(Path.Combine(Data, "rules")), StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = new UiScreenState(((IWindowLogic)logic).Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == WandScrollLogic.Screen)));
        logic.Open(ui);
        output.WriteLine(logic.Summary);
        Assert.Equal(2, logic.Entries.Count);
        Assert.True(ui.IsEnabled("BTN_CAST_SPELL"));
        Assert.StartsWith("IconSpel:", ui.Frame("IMG_SPELL"));

        var stack = session.Map.Stacks[stackId];
        long gold = session.Map.Players[owner].Bank.Gold;
        var wandDef = session.ItemOf(wand)!;
        Assert.Null(session.CastItem(stackId, scroll, stack.X, stack.Y));
        Assert.DoesNotContain(scroll, session.Map.Stacks[stackId].Items);
        Assert.Null(session.CastItem(stackId, wand, stack.X, stack.Y));
        Assert.Contains(wand, session.Map.Stacks[stackId].Items);
        Assert.Equal(session.Map.Players[owner].Bank, new Bank(gold, 100_000, 100_000, 100_000, 100_000, 100_000) - wandDef.CastingCost);
        // Посох — раз в день, лорду-магу дважды (0x5dbbe8: X005TA0579/0580).
        bool mage = book(session).LordCategory(session.Map.Players[owner].LordId) == Treasury.MageCategory;
        if (mage) Assert.Null(session.CastItem(stackId, wand, stack.X, stack.Y));
        Assert.Equal(mage ? GameSession.ItemCastWandUsedTwice : GameSession.ItemCastWandUsed, session.ItemCastRefusal(stackId, wand, stack.X, stack.Y));
    }

    /// <summary>Запись посоха (<see cref="MapState.ItemCasts"/>) снимается в начале следующего хода владельца, как запись заклинания:
    /// <c>CEffectRemoveSpell</c> (<c>0x443d01</c>) зовёт <c>0x607099</c>, а тот — фильтр <c>0x6070c4</c> (игрок и ход ≥ <c>TURN_STOP</c>)
    /// по ОБОИМ спискам <c>CMidSpellCast</c> (<c>+0x8</c> и <c>+0x18</c>). Прежде порт снимал только заклинания, и записи посохов копились
    /// (поведение спасал фильтр <see cref="SpellCast.Live"/>, но список расходился с сохранениями движка: в корпусе устаревших записей
    /// предметов нет — <c>map_effect_turn_stop</c>, аудит 15.10).</summary>
    [SkippableFact]
    public void Wand_RecordRemovedAtOwnerNextTurnStart()
    {
        var (session, stackId, _, wand) = Game();
        var stack = session.Map.Stacks[stackId];
        Skip.IfNot(session.State.Info.Humans.Contains(stack.Owner, StringComparer.OrdinalIgnoreCase), "герой с посохом — не у человека");
        int day = session.Map.Turn;
        Assert.Null(session.CastItem(stackId, wand, stack.X, stack.Y));
        Assert.Contains(session.Map.ItemCasts, c => c.Origin == wand && c.TurnStop == day + 1);

        session.EndTurn();

        Assert.Equal(day + 1, session.Map.Turn);
        Assert.DoesNotContain(session.Map.ItemCasts, c => c.PlayerId.Equals(stack.Owner, StringComparison.OrdinalIgnoreCase)
                                                          && c.TurnStop <= session.Map.Turn);
        Assert.All(session.Map.ItemCasts, c => Assert.True(c.TurnStop >= session.Map.Turn, $"{c.PlayerId} {c.Origin} {c.TurnStop}"));
    }

    /// <summary>Свиток смены земли и сноса жезла действует, а не только расходуется (<c>0x443b76</c> зовёт ту же фабрику результатов
    /// <c>0x443828</c>, что и литьё). G000IG5107 — смена земли, G000IG5119/5120 — снос жезла; прежде <see cref="GameSession.CastItem"/>
    /// таких веток не имел вовсе (аудит 13.10).</summary>
    [SkippableTheory]
    [InlineData("G000IG5107")]
    [InlineData("G000IG5119")]
    [InlineData("G000IG5120")]
    public void Scroll_TerrainAndRodSpells_TakeEffect(string itemType)
    {
        var (session, stackId, _, _) = Game();
        var book = session.RequireBook();
        var spell = book.Spells[book.Items[itemType].SpellId];
        Assert.Contains(spell.Category, new[] { SpellCategory.ChangeTerrain, SpellCategory.RemoveRod });
        var map = session.Map;
        var types = map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types["S073IMTEST3"] = itemType;
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stackId] = stacks[stackId] with { Items = [.. stacks[stackId].Items, "S073IMTEST3"] };
        var rods = map.Rods.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (spell.Category == SpellCategory.RemoveRod)
        {
            // Чужой жезл в клетке отряда-соседа: видимая клетка, не своя.
            var stack = stacks[stackId];
            string foe = map.Players.Keys.First(p => !p.Equals(stack.Owner, StringComparison.OrdinalIgnoreCase));
            rods["S073RDTEST"] = new MapRod("S073RDTEST", foe, stack.X + 1, stack.Y);
        }
        map = GameSession.WithRods(GameSession.With(map, stacks: stacks, itemTypes: types), rods);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, book);
        (int x, int y) cell = (from x in Enumerable.Range(0, map.Size) from y in Enumerable.Range(0, map.Size) select (x, y))
            .FirstOrDefault(c => session.ItemCastRefusal(stackId, "S073IMTEST3", c.Item1, c.Item2) is null, (-1, -1));
        Skip.If(cell.x < 0, $"для {itemType} на карте нет годной клетки");
        var before = session.Map;

        Assert.Null(session.CastItem(stackId, "S073IMTEST3", cell.x, cell.y));

        var after = session.Map;
        output.WriteLine($"{itemType} ({spell.Category}) в {cell}");
        if (spell.Category == SpellCategory.RemoveRod) Assert.Equal(before.Rods.Count - 1, after.Rods.Count);
        else Assert.Contains((from x in Enumerable.Range(0, map.Size) from y in Enumerable.Range(0, map.Size) select (x, y)),
                             c => before.Tiles[c.x, c.y].Raw != after.Tiles[c.x, c.y].Raw);
    }

    private static GameBook book(GameSession session) => session.RequireBook();

    /// <summary>Сохранение t24: отряд с героем, умеющим посохи и свитки, мана с запасом; в сумку — свиток и посох, чьи заклинания годятся на
    /// клетку отряда.</summary>
    private static (GameSession Session, string Stack, string Scroll, string Wand) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var probe = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), content.MapArt, book);
        var stack = probe.Map.Stacks.Values.FirstOrDefault(s => s.Inside.Length == 0 && probe.Map.Players.ContainsKey(s.Owner)
            && probe.Map.Units.TryGetValue(s.LeaderId, out var l) && l.Hp > 0 && probe.LeaderAbilitiesOf(s).Contains(GameSession.WandScrollAbility));
        Skip.If(stack is null, "в сохранении нет героя с навыком посохов и свитков");

        var map = probe.Map;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var owner = players[stack!.Owner];
        players[owner.Id] = owner with { Bank = owner.Bank with { Infernal = 100_000, Life = 100_000, Death = 100_000, Runic = 100_000, Grove = 100_000 } };
        var types = map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        string? scroll = null, wand = null;
        foreach (var item in book.Items.Values.Where(i => i.SpellId.Length > 0 && i.Category is ItemCategory.Scroll or ItemCategory.Wand))
        {
            bool isScroll = item.Category == ItemCategory.Scroll;
            if ((isScroll ? scroll : wand) is not null) continue;
            string id = isScroll ? "S073IMTEST1" : "S073IMTEST2";
            types[id] = item.Id;
            stacks[stack.Id] = stacks[stack.Id] with { Items = [.. stacks[stack.Id].Items.Where(i => i != id), id] };
            var trial = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks with
                { Map = GameSession.With(map, stacks: stacks, players: players, itemTypes: types) }), content.MapArt, book);
            if (trial.ItemCastRefusal(stack.Id, id, stack.X, stack.Y) is null && book.Spells[item.SpellId].Area == 1)
            {
                if (isScroll) scroll = id;
                else wand = id;
            }
            else stacks[stack.Id] = stacks[stack.Id] with { Items = [.. stacks[stack.Id].Items.Where(i => i != id)] };
        }
        Skip.If(scroll is null || wand is null, "нет свитка и посоха, годных на клетку отряда");
        var final = looks with { Map = GameSession.With(map, stacks: stacks, players: players, itemTypes: types) };
        return (new GameSession(new GameState(GameInfo.FromMap("PtC", final), final), content.MapArt, book), stack.Id, scroll!, wand!);
    }
}
