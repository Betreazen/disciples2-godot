using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Окна заклинаний человека: исследование в столице (<see cref="ResearchSpellLogic"/>) и книга на карте
/// (<see cref="CastSpellLogic"/>) — по разбору движка (<c>docs/ui-windows.md</c>).</summary>
[Trait("Category", "Corpus")]
public class SpellWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Bank Rich = new(99999, 9999, 9999, 9999, 9999, 9999);

    /// <summary>Исследование: список уровня — заклинания лорда; «Исследовать» спрашивает X005TA0269 с ценой, «Да» — заклинание
    /// известно, окно не закрывается, строка помечена ICNSY001; второе за день — X005TA0252.</summary>
    [SkippableFact]
    public void Research_AsksAndLearns_OncePerDay()
    {
        var (session, human) = Game(magic: true);
        var (logic, ui) = Open(new ResearchSpellLogic(Context(session, human), Texts()), ResearchSpellLogic.Screen);
        Assert.NotEmpty(logic.Shown);
        string spell = logic.Shown.First(s => !session.Map.Players[human].KnownSpells.Contains(s, StringComparer.OrdinalIgnoreCase));
        int index = logic.Shown.ToList().IndexOf(spell);
        output.WriteLine($"уровень 1: {string.Join(", ", logic.Shown)}; {ui.TextOf("TXT_COST")}");
        Assert.StartsWith("IconSpel:", ui.Cards("LBOX_SPELL_LIST")[index].Image);
        Assert.False(logic.Allows("BTN_RESEARCH_SPELL", ui));
        Assert.False(logic.Allows("LBOX_SPELL_LIST", ui));

        logic.Handle(new UiListConfirmed("LBOX_SPELL_LIST", index), ui);
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        output.WriteLine(ask.Text);
        Assert.StartsWith("Исследовать заклинание", ask.Text);
        ask.Yes();
        Assert.Contains(spell, session.Map.Players[human].KnownSpells, StringComparer.OrdinalIgnoreCase);
        Assert.Null(logic.TakeRequest());   // окно не закрывается
        logic.Resume(ui);
        Assert.Equal("Icons:ICNSY001", ui.Cards("LBOX_SPELL_LIST")[logic.Shown.ToList().IndexOf(spell)].Overlay);

        string other = logic.Shown.FirstOrDefault(s => !session.Map.Players[human].KnownSpells.Contains(s, StringComparer.OrdinalIgnoreCase)) ?? "";
        Skip.If(other.Length == 0, "на уровне больше нечего исследовать");
        logic.Handle(new UiListConfirmed("LBOX_SPELL_LIST", logic.Shown.ToList().IndexOf(other)), ui);
        Assert.Equal(Text("X005TA0252"), Assert.IsType<UiTell>(logic.TakeRequest()).Text);
    }

    /// <summary>«Исследовать» — кнопка подтверждения списка (<c>LBOX_SPELL_LIST</c> → <c>BTN_RESEARCH_SPELL</c>): щелчок настоящим путём — одно
    /// событие и один вопрос (прежде кнопка шла в логику только как <c>UiListConfirmed</c>, вместе с <c>UiClicked</c>).</summary>
    [SkippableFact]
    public void Research_ButtonClick_AsksOnce()
    {
        var (session, human) = Game(magic: true);
        var (logic, ui) = Open(new ResearchSpellLogic(Context(session, human), Texts()), ResearchSpellLogic.Screen);
        int index = logic.Shown.ToList().FindIndex(s => !session.Map.Players[human].KnownSpells.Contains(s, StringComparer.OrdinalIgnoreCase));
        Skip.If(index < 0, "нечего исследовать");
        ui.Select("LBOX_SPELL_LIST", index);
        logic.Handle(new UiListSelected("LBOX_SPELL_LIST", index), ui);
        var button = ui.Screen.Elements.First(e => e.Id == "BTN_RESEARCH_SPELL").Rect;
        (int x, int y) = (button.X + button.Width / 2, button.Y + button.Height / 2);
        ui.Press(x, y);
        var events = ui.Release(x, y);
        Assert.Equal([new UiClicked("BTN_RESEARCH_SPELL")], events);
        foreach (var e in events) logic.Handle(e, ui);
        Assert.IsType<UiAsk>(logic.TakeRequest());
        Assert.Null(logic.TakeRequest());
    }

    /// <summary>Без постройки магии окна нет (<c>X005TA0405</c> у хозяина); пятый уровень не у мага — X005TA0444.</summary>
    [SkippableFact]
    public void Research_NeedsMagic_FifthLevelForMageOnly()
    {
        var (session, human) = Game(magic: false);
        var book = session.Book!;
        // Лорд — не маг: берём лорда-воина из списков построек лордов.
        string warrior = book.LordBuildings.Keys.First(l => book.LordCategory(l) is >= 0 and not Treasury.MageCategory);
        session = WithPlayer(session, session.Map.Players[human] with { LordId = warrior });
        var context = Context(session, human);
        Assert.False(ResearchSpellLogic.HasMagic(context));
        var (logic, ui) = Open(new ResearchSpellLogic(context, Texts()), ResearchSpellLogic.Screen);
        logic.Handle(new UiGroupChanged("RAD_SPELL_LEVEL", 4, "TOG_LEVEL5"), ui);
        Assert.Equal(5, logic.Level);
        Assert.Empty(logic.Shown);
        Assert.Contains(ui.TextOf("TXT_NO_SPELL"), new[] { Text("X005TA0444"), Text("X005TA0287") });
        Assert.Equal(5, context.Memory.ResearchLevel);   // уровень запомнен
    }

    /// <summary>Книга: изученное заклинание на площадь — «Сотворить» закрывает книгу и начинает прицел; на всю карту — вопрос
    /// X005TA0702; мало маны — X005TA0215.</summary>
    [SkippableFact]
    public void Cast_ClosesAndAims_WholeMapAsks_ManaRefused()
    {
        var (session, human) = Game(magic: true);
        var spells = session.Book!.Spells.Values;
        var aimed = spells.First(s => s.Level == 1 && s.Area != CastSpellLogic.WholeMap);
        var whole = spells.FirstOrDefault(s => s.Area == CastSpellLogic.WholeMap);
        var player = session.Map.Players[human];
        session = WithPlayer(session, player with { KnownSpells = [aimed.Id.ToUpperInvariant(), .. whole is null ? [] : new[] { whole.Id.ToUpperInvariant() }] });
        var context = Context(session, human);
        Assert.True(CastSpellLogic.AnyKnown(context));
        var (logic, ui) = Open(new CastSpellLogic(context, Texts()), CastSpellLogic.Screen);
        Assert.Equal(1, logic.Level);
        int at = logic.Shown.ToList().FindIndex(s => s.Equals(aimed.Id, StringComparison.OrdinalIgnoreCase));
        logic.Handle(new UiListConfirmed("LBOX_SPELL_LIST", at), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
        Assert.Equal(aimed.Id, Assert.IsType<UiCast>(logic.TakeRequest()).SpellId, StringComparer.OrdinalIgnoreCase);

        if (whole is not null)
        {
            var (book2, ui2) = Open(new CastSpellLogic(context, Texts()), CastSpellLogic.Screen);
            book2.Handle(new UiGroupChanged("RAD_SPELL_LEVEL", whole.Level - 1, $"TOG_LEVEL{whole.Level}"), ui2);
            book2.Handle(new UiListConfirmed("LBOX_SPELL_LIST", book2.Shown.ToList().FindIndex(s => s.Equals(whole.Id, StringComparison.OrdinalIgnoreCase))), ui2);
            Assert.IsType<UiClose>(book2.TakeRequest());
            Assert.StartsWith("Вы уверены", Assert.IsType<UiAsk>(book2.TakeRequest()).Text);
        }

        var poor = WithPlayer(session, session.Map.Players[human] with { Bank = Bank.Zero });
        var (book3, ui3) = Open(new CastSpellLogic(Context(poor, human), Texts()), CastSpellLogic.Screen);
        book3.Handle(new UiGroupChanged("RAD_SPELL_LEVEL", 0, "TOG_LEVEL1"), ui3);
        book3.Handle(new UiListConfirmed("LBOX_SPELL_LIST", 0), ui3);
        Assert.Equal(Text("X005TA0215"), Assert.IsType<UiTell>(book3.TakeRequest()).Text);
    }

    /// <summary>Лорда игрока нет в Glord — цены литья нет, «недостаточно могущественны» (0x582afa, код 0xa); книга остаётся.</summary>
    [SkippableFact]
    public void Cast_UnknownLord_NotPowerful()
    {
        var (session, human) = Game(magic: true);
        var spell = session.Book!.Spells.Values.First(s => s.Level == 1 && s.Area != CastSpellLogic.WholeMap);
        session = WithPlayer(session, session.Map.Players[human] with { KnownSpells = [spell.Id.ToUpperInvariant()], LordId = "G000LR9999" });
        var (book, ui) = Open(new CastSpellLogic(Context(session, human), Texts()), CastSpellLogic.Screen);
        book.Handle(new UiListConfirmed("LBOX_SPELL_LIST", 0), ui);
        Assert.Equal(Text("X005TA0213"), Assert.IsType<UiTell>(book.TakeRequest()).Text);
        Assert.Null(book.TakeRequest());
    }

    [Fact]
    public void TargetMessages_ByRefusal()
    {
        Assert.Equal("X005TA0670", CastSpellLogic.TargetMessage("все иммунны"));
        Assert.Equal("X005TA0672", CastSpellLogic.TargetMessage("союзник"));
        Assert.Equal("X005TA0671", CastSpellLogic.TargetMessage("лечить некого"));
        Assert.Equal("X005TA0671", CastSpellLogic.TargetMessage("брони нет"));
        Assert.Null(CastSpellLogic.TargetMessage("уже наложено"));     // код 4 — у клиента сообщения нет (0x4d355f)
        Assert.Null(CastSpellLogic.TargetMessage("лидер — иллюзия"));  // код 2
        Assert.Null(CastSpellLogic.TargetMessage("клетка под туманом"));
    }

    /// <summary>Прицел: курсор по возможности литья, квадрат SPLAREA{сторона} у заклинания с областью, отметка цели на своём отряде;
    /// туман — по клеткам квадрата (SPLAREAFOG/SPLAREAFOGN) и без отметки цели.</summary>
    [SkippableFact]
    public void Aim_CursorAreaFogAndTarget()
    {
        var (session, human) = Game(magic: true);
        var spells = session.Book!.Spells.Values;
        var stack = session.Map.Stacks.Values.First(s => s.Owner == human && s.Inside.Length == 0);

        var area = spells.FirstOrDefault(s => s.Area is > 1 and < 999 && s.Category is D2.Rules.Magic.SpellCategory.Boost or D2.Rules.Magic.SpellCategory.Heal);
        Skip.If(area is null, "нет заклинания с областью");
        var view = SpellAimView.At(session, human, area!.Id, stack.X, stack.Y);
        output.WriteLine($"{area.Id} {view}");
        Assert.Equal(view.CanCast ? "SPELLVAL" : "SPELLINV", view.Cursor);
        Assert.Equal([new AimMark(stack.X, stack.Y, $"SPLAREA{area.Area}")], view.Area);
        Assert.False(view.AreaOverFog);
        var mark = Assert.Single(view.Targets, t => (t.X, t.Y) == (stack.X, stack.Y));
        Assert.Contains(mark.Frame, new[] { "SELECT_NO", "SELECT_YES" });

        var fog = spells.FirstOrDefault(s => s.Category == D2.Rules.Magic.SpellCategory.Fog && s.Area is > 1 and < 999);
        Skip.If(fog is null, "нет заклинания тумана с областью");
        var fogView = SpellAimView.At(session, human, fog!.Id, stack.X, stack.Y);
        Assert.Equal(D2.Rules.Magic.SpellCast.Tiles(fog, stack.X, stack.Y, session.Map.Size, session.Map.Size).Count(), fogView.Area.Count);
        Assert.All(fogView.Area, m => Assert.StartsWith("SPLAREAFOG", m.Frame));
        Assert.True(fogView.AreaOverFog);
        Assert.Empty(fogView.Targets);
        Assert.Equal("SPELLINV", SpellAimView.At(session, human, fog.Id, -5, -5).Cursor);
    }

    // --- помощники ---

    private static IReadOnlyDictionary<string, SpellText>? _texts;
    private static IReadOnlyDictionary<string, string>? _strings;

    private static IReadOnlyDictionary<string, SpellText> Texts() => _texts ??= SpellBookLogic.LoadTexts(Path.Combine(Data, "rules"));

    private static string Text(string id) => (_strings ??= UiStrings.Load(Path.Combine(Data, "ui"), "ru"))[id];

    private static WindowContext Context(GameSession session, string viewer) =>
        new(session, viewer, _strings ??= UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));

    private static (T Logic, UiScreenState Ui) Open<T>(T logic, string screen) where T : IWindowLogic
    {
        var ui = new UiScreenState(logic.Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == screen)));
        logic.Open(ui);
        return (logic, ui);
    }

    private static (GameSession Session, string Human) Game(bool magic)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var human = session.Map.Players.Values.First(p => p.IsHuman);
        var buildings = session.Book!.Economy.Buildings;
        var own = human.Buildings.Where(b => !buildings.TryGetValue(b, out var t) || t.Category != ResearchSpellLogic.MagicCategory).ToList();
        if (magic)
            own.Add(session.Buildable(human.Id).First(b => buildings[b].Category == ResearchSpellLogic.MagicCategory).ToUpperInvariant());
        session = WithPlayer(session, human with { Bank = Rich, Buildings = own, ResearchTurn = 0 });
        return (session, human.Id);
    }

    private static GameSession WithPlayer(GameSession session, MapPlayer player)
    {
        var players = session.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[player.Id] = player;
        var map = GameSession.With(session.Map, players: players);
        return new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);
    }
}
