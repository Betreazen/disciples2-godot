using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;

namespace D2.Data.Tests;

/// <summary>Окно полученного <c>DLG_ITEM</c> (<see cref="GiveItemLogic"/>, <see cref="GivenWatch"/>; <c>docs/ui-windows.md</c> §14):
/// окна по типу предмета с числом штук, заклинание — своё окно (заголовок библиотеки столицы — свой), пустой мешок — «Ничего не
/// найдено», окна видит только получатель-человек.</summary>
[Trait("Category", "Corpus")]
public class GiveItemWindowTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Human = "S073PL000a", Ai = "S073PL000b";

    [SkippableFact]
    public void Entries_GroupByType_SpellsOneEach_EmptyBag()
    {
        var session = Game();
        var map = session.Map;
        var byType = map.ItemTypes.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() >= 2);
        Skip.If(byType is null, "нет двух предметов одного типа");
        string other = map.ItemTypes.First(p => !p.Value.Equals(byType!.Key, StringComparison.OrdinalIgnoreCase)).Key;
        var pair = byType!.Take(2).Select(p => p.Key).ToList();

        var items = new TurnSight(TurnSightKind.ItemsGiven, Human, 1, 1, "BAG") { Ids = [pair[0], other, pair[1]] };
        var entries = GivenWatch.Entries(items, map);
        Assert.Equal(2, entries.Count);
        Assert.Equal((false, byType.Key, 2), (entries[0]!.Spell, entries[0]!.Id, entries[0]!.Count));
        Assert.Equal(1, entries[1]!.Count);

        var spells = new TurnSight(TurnSightKind.SpellsGiven, Human, 1, 1, "CAP") { Ids = ["G000SS0001", "G000SS0002"], Library = true };
        Assert.All(GivenWatch.Entries(spells, map), e => Assert.Equal((true, 1, true), (e!.Spell, e.Count, e.Library)));
        Assert.Equal([null], GivenWatch.Entries(new TurnSight(TurnSightKind.ItemsGiven, Human, 1, 1, "BAG"), map));

        var watch = new GivenWatch(id => id == Human);
        watch.Happened(items);
        watch.Happened(items with { PlayerId = Ai });
        watch.Happened(new TurnSight(TurnSightKind.Looted, Human, 1, 1, "RUIN"));
        Assert.False(watch.Has(Ai));
        Assert.Single(watch.Take(Human));
        Assert.False(watch.Has(Human));

        // «Горячий стул»: за ПК — Human. Предметы другому человеку — пропущены (0x48ab78), заклинания — окно тому, кто за ПК (0x48ac3e).
        const string Second = "S073PL000c";
        var hotseat = new GivenWatch(id => id is Human or Second, () => Human);
        hotseat.Happened(items with { PlayerId = Second });
        hotseat.Happened(spells with { PlayerId = Second });
        Assert.False(hotseat.Has(Second));
        var seen = Assert.Single(hotseat.Take(Human));
        Assert.Equal((TurnSightKind.SpellsGiven, Second), (seen.Kind, seen.PlayerId));
    }

    [SkippableFact]
    public void Window_TitleNameCountIcon_AndEmptyBag()
    {
        var session = Game();
        var context = Context(session);
        var texts = SpellBookLogic.LoadTexts(Path.Combine(Data, "rules"));
        var item = session.Book!.Items.Values.First();

        var logic = new GiveItemLogic(context, new GivenEntry(false, item.Id, 3), first: true, texts);
        Assert.Equal(GiveItemLogic.ItemScreen, logic.Screen);
        var ui = Open(logic, logic.Screen);
        Assert.Equal(context.Text("X005TA0283"), ui.TextOf("TXT_TITLE"));
        Assert.Equal($"{logic.Name} 3 шт.", ui.TextOf("TXT_ITEM"));
        Assert.NotEqual(item.Id, logic.Name);
        Assert.Equal($"IconItem:{item.Id.ToUpperInvariant()}", ui.Frame("IMG_ITEM"));
        Assert.False(logic.Allows("BTN_OK", ui));
        var icon = ui.Screen.Elements.First(e => e.Id == "IMG_ITEM").Rect;
        logic.RightPress(icon.X + 2, icon.Y + 2, ui);
        Assert.Equal(ItemCardLogic.Screen, Assert.IsType<UiOpen>(logic.TakeRequest()).Screen);
        logic.Handle(new UiClicked("BTN_OK"), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());

        string spell = texts.Keys.First();
        foreach (bool library in new[] { false, true })
        {
            var s = new GiveItemLogic(context, new GivenEntry(true, spell, 1, library), first: false, texts);
            var sui = Open(s, s.Screen);
            Assert.Equal(context.Text(library ? "X005TA0774" : "X005TA0773"), sui.TextOf("TXT_TITLE"));
            Assert.Equal($"IconSpel:{spell.ToUpperInvariant()}", sui.Frame("IMG_ITEM"));
            Assert.Equal($"{s.Name} 1 шт.", sui.TextOf("TXT_ITEM"));
        }

        var empty = new GiveItemLogic(context, null, first: true, texts);
        Assert.Equal(MessageBoxLogic.Screen, empty.Screen);
        var eui = Open(empty, empty.Screen);
        Assert.Equal(context.Text("X005TA0401"), eui.TextOf("TXT_INFO"));
        Assert.True(eui.IsHidden("BTN_YES"));
        empty.Handle(new UiClicked("BTN_OK"), eui);
        Assert.IsType<UiClose>(empty.TakeRequest());
    }

    private static WindowContext Context(GameSession session) =>
        new(session, Human, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")))
        {
            ItemTexts = StackTexts.Load(Path.Combine(Data, "rules")),
        };

    private static UiScreenState Open(IWindowLogic logic, string screen)
    {
        var ui = new UiScreenState(logic.Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == screen)));
        logic.Open(ui);
        return ui;
    }

    private static GameSession Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }
}
