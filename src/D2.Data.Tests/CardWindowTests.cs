using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Magic;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Карточки правой кнопки пятого круга (<c>docs/ui-windows.md</c> §9): заклинание <c>DLG_R_C_SPELL</c>, навык героя
/// <c>DLG_R_C_LEADERABIL</c>; тип героя в строке найма.</summary>
[Trait("Category", "Corpus")]
public class CardWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Human = "S073PL000a";

    /// <summary>Заклинание: имя, область (вся карта / одна цель / N x N), уровень, тип (нет — «Нет»), эффект; у призыва — длительность;
    /// стоимость литья по мане или «лорд не может»; свиток прячет её, кроме лесной (как у движка); цена покупки — только у мага.</summary>
    [SkippableFact]
    public void SpellCard_FieldsAndFlags()
    {
        var context = Context(Game());
        var book = context.Session.RequireBook();
        var texts = SpellBookLogic.LoadTexts(Path.Combine(Data, "rules"));
        var whole = book.Spells.Values.First(s => s.Area == 999);
        var summon = book.Spells.Values.First(s => s.Category == SpellCategory.Summon && s.Area == 1);

        var ui = Open(new SpellCardLogic(context, whole.Id, texts));
        output.WriteLine(ui.TextOf("TXT_SPELL_INFO"));
        Assert.Contains(context.Text(texts[whole.Id].NameTxt), ui.TextOf("TXT_SPELL_INFO"));
        Assert.Contains(context.Text("X005TA0713"), ui.TextOf("TXT_SPELL_INFO"));
        Assert.Contains("Уровень", ui.TextOf("TXT_SPELL_INFO"));
        Assert.StartsWith("IconSpel:", ui.Frame("IMG_SPELL_ICON"));
        Assert.True(ui.IsHidden("TXT_BUY_COST"));
        Assert.False(ui.IsHidden("TXT_CASTING_COST"));

        var summonUi = Open(new SpellCardLogic(context, summon.Id, texts));
        Assert.Contains(context.Text("X005TA0711"), summonUi.TextOf("TXT_SPELL_INFO"));
        Assert.Contains(context.Text("X005TA0473"), summonUi.TextOf("TXT_SPELL_INFO"));   // призыв без урона — тип «Нет»
        Assert.Contains("Длительность", summonUi.TextOf("TXT_SPELL_INFO"));

        var scroll = Open(new SpellCardLogic(context, whole.Id, texts, hideCasting: true));
        Assert.True(scroll.IsHidden("TXT_CASTING_COST"));
        Assert.True(scroll.IsHidden("IMG_ORANGE"));
        Assert.False(scroll.IsHidden("IMG_BLUE"));   // ошибка движка: лесная не прячется

        var shop = Open(new SpellCardLogic(context, summon.Id, texts, buy: true));
        Assert.False(shop.IsHidden("TXT_BUY_COST"));
        Assert.Contains($"{summon.BuyCost.Gold}", shop.TextOf("TXT_BUY_COST"));
    }

    /// <summary>Навык героя: имя и описание по строке имени; значок навыка в окне отряда по правой кнопке открывает карточку.</summary>
    [SkippableFact]
    public void LeaderAbility_CardAndFromManageStack()
    {
        var context = Context(Game());
        var ui = Open(new LeaderAbilityCardLogic(context, "X005TA0181"));
        Assert.Contains(context.Text("X005TA0181"), ui.TextOf("TXT_INFO"));
        Assert.Contains(context.Text("X005TA0900"), ui.TextOf("TXT_INFO"));

        var hero = context.Map.Stacks.Values.First(s => s.Owner == Human && HeroSkills.Rows(context, s).Count > 0);
        var manage = new ManageStackLogic(context, hero.Id, Screens(), StackTexts.Load(Path.Combine(Data, "rules")));
        var manageUi = Open(manage);
        var skills = manageUi.Screen.Elements.First(e => e.Id == "LBOX_ITEM_ICON").Rect;
        manage.RightPress(skills.X + 5, skills.Y + 5, manageUi);
        var card = Assert.IsType<LeaderAbilityCardLogic>(Assert.IsType<UiOpen>(manage.TakeRequest()).Logic);
        Assert.Equal($"навык={HeroSkills.Rows(context, hero)[0].Text}", card.Summary);
    }

    /// <summary>Строка героя в найме: последней строкой — тип героя (<c>Gunits.ABIL_TXT</c>).</summary>
    [SkippableFact]
    public void HireRow_EndsWithHeroType()
    {
        var context = Context(Game());
        string capital = context.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == Human).Id;
        var hire = new HireLogic(context, capital, HireKind.Leader);
        var ui = Open(hire);
        string row = ui.Cards("LBOX_LEADER")[0].Text!;
        output.WriteLine(row);
        Assert.EndsWith(context.Text(context.Rules!.UnitAbilities[hire.Types[0]]), row);
    }

    private static WindowContext Context(GameSession session) =>
        new(session, Human, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")))
        {
            Rules = RulesTexts.Load(Path.Combine(Data, "rules")),
        };

    private static IReadOnlyList<UiScreen> Screens() => UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));

    private static UiScreenState Open(IWindowLogic logic)
    {
        string screen = logic switch
        {
            SpellCardLogic => SpellCardLogic.Screen,
            LeaderAbilityCardLogic => LeaderAbilityCardLogic.Screen,
            ManageStackLogic => ManageStackLogic.Screen,
            _ => HireLogic.Screen,
        };
        var ui = new UiScreenState(logic.Shape(Screens().First(s => s.Id == screen)));
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
