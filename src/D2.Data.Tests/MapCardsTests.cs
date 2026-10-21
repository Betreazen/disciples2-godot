using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карточки правой кнопки на карте (<see cref="MapCards"/>) на сохранении дня 21 живой партии глазами Кланов.</summary>
[Trait("Category", "Corpus")]
public class MapCardsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableFact]
    public void Card_ByObjectAndKnowledge()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t21-037.sg");
        Skip.IfNot(File.Exists(sg), "нет корпуса");
        var looks = MapLooksLoader.Load(sg);
        string rules = Path.Combine(Data, "rules");
        var book = GameBook.Load(rules);
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt, book);
        const string viewer = "S073PL000a";
        var strings = UiStrings.Load(Path.Combine(Data, "ui"), "ru");
        var context = new WindowContext(session, viewer, strings, UnitFaces.Load(rules));
        var races = RaceNames.Load(rules);
        var marks = MapCards.LandmarkNames(rules);
        var screens = UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));
        MapCard? At((int X, int Y) c) => MapCards.At(context, races, marks, c.X, c.Y, (_, _) => false);
        UiScreenState Open(MapCard card)
        {
            var ui = new UiScreenState(screens.First(s => s.Id == card.Screen));
            card.Logic.Open(ui);
            return ui;
        }

        var hero = session.Map.Stacks["S073KC0001"];
        var own = At((hero.X, hero.Y))!;
        Assert.Equal(("DLG_R_C_STACK", hero.Id), (own.Screen, own.ObjectId));
        var ownUi = Open(own);
        Assert.False(string.IsNullOrEmpty(ownUi.TextOf("TXT_NAME")));
        // Картина лидера во весь рост из Events по центру места (0x57c0a9, флаги 9), а не круглый портрет FACEB в углу.
        Assert.Equal(context.Faces.Picture(session.Map.Units[hero.LeaderId].TypeId), ownUi.Frame("IMG_LEADER"));
        Assert.Equal(UiImageAlign.Center, ownUi.ImageAlign("IMG_LEADER"));
        Assert.Equal($"Сражений выиграно: {hero.BattlesWon}", ownUi.TextOf("TXT_BATTLES"));
        // Портреты группы — в рамке BORDERUNITSMALL/LARGE (0x57c443 → 0x5b6559, таблица 0x7ae938), по центру места.
        Assert.Contains(Enumerable.Range(1, 6), i => Framed(ownUi.Frame($"IMG_UNIT_{i}")));
        Assert.All(Enumerable.Range(1, 6).Where(i => ownUi.Frame($"IMG_UNIT_{i}") is not null),
                   i => Assert.True(Framed(ownUi.Frame($"IMG_UNIT_{i}")) || ownUi.Frame($"IMG_UNIT_{i}") == MapCards.SmallBorder));
        // Пустые места — пустая малая рамка (пользователь 11.10, снимки 46–47): у отряда меньше шести юнитов — рамки на всех шести местах,
        // кроме второго места большого юнита.
        int units = hero.UnitIds.Count;
        if (units < 6) Assert.Contains(Enumerable.Range(1, 6), i => ownUi.Frame($"IMG_UNIT_{i}") == MapCards.SmallBorder);
        // Герой — реализация лидера: своё имя в заголовке и полужирным в группе.
        string heroName = session.Looks.Stacks.First(s => s.Id == hero.Id).LeaderName;
        Assert.Equal(heroName, ownUi.TextOf("TXT_NAME"));
        Assert.Contains(Enumerable.Range(1, 6), i => ownUi.TextOf($"TXT_UNIT_{i}") is { } t
            && t.Contains("MedBold", StringComparison.OrdinalIgnoreCase) && t.EndsWith(heroName, StringComparison.Ordinal));

        // Отряд чужого играбельного игрока, которого Кланы не разведали, — «зашлите шпиона».
        var me = session.Map.Players[viewer];
        var foreign = session.Map.Stacks.Values.First(s => s.Owner != viewer && !me.Spied.Contains(s.Owner) && s.Inside.Length == 0
            && book.Economy.Races.TryGetValue(session.Map.Players[s.Owner].RaceId, out var r) && r.Playable);
        var nospy = At((foreign.X, foreign.Y))!;
        Assert.Equal("DLG_R_C_STACK_NOSPY", nospy.Screen);
        Assert.Contains("шпиона", Open(nospy).TextOf("TXT_INFO"));

        var capital = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == viewer);
        var city = At((capital.X, capital.Y))!;
        Assert.Equal("DLG_R_C_CITY", city.Screen);
        var cityUi = Open(city);
        Assert.Contains("(Столица)", cityUi.TextOf("TXT_INFO"));
        // Прогон 2 пользователя 11.10, снимок 31: у портретов гостя и гарнизона не было рамок.
        var portraits = Enumerable.Range(0, 6).SelectMany(i => (string[])[$"IMG_STACK_{i}", $"IMG_DEFENSE_{i}"])
                                  .Select(cityUi.Frame).OfType<string>().ToList();
        Assert.NotEmpty(portraits);
        Assert.All(portraits, f => Assert.True(Framed(f) || f == MapCards.SmallBorder, f));

        Assert.Null(MapCards.At(context, races, marks, hero.X, hero.Y, (_, _) => true));   // туман — окна нет
    }

    /// <summary>
    /// «Зашлите шпиона» со значками заклинаний (<c>CEncLayoutStack</c> <c>0x57b947</c>, <c>0x57cac0</c> → <c>0x5ea500</c> по лидеру): ослабление
    /// на лидере чужого неразведанного отряда — красный значок <c>FIISOSPELLLOWER</c> в месте <c>IMG_LOWER</c>, усиление — <c>FIISOSPELLBOOST</c>
    /// в <c>IMG_BOOST</c>; без заклинаний мест пусто (пользователь 17.10: «при ПКМ на вражеском отряде с дебаффами нет красного значка»).
    /// </summary>
    [SkippableFact]
    public void NoSpyCard_ShowsSpellMarksOfLeader()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t21-037.sg");
        Skip.IfNot(File.Exists(sg), "нет корпуса");
        var looks = MapLooksLoader.Load(sg);
        string rules = Path.Combine(Data, "rules");
        var book = GameBook.Load(rules);
        var art = SharedContent.Load(Data).MapArt;
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), art, book);
        const string viewer = "S073PL000a";
        var me = session.Map.Players[viewer];
        var foreign = session.Map.Stacks.Values.First(s => s.Owner != viewer && !me.Spied.Contains(s.Owner) && s.Inside.Length == 0
            && book.Economy.Races.TryGetValue(session.Map.Players[s.Owner].RaceId, out var r) && r.Playable);
        var screens = UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));
        var strings = UiStrings.Load(Path.Combine(Data, "ui"), "ru");
        UiScreenState Card(GameSession s)
        {
            var context = new WindowContext(s, viewer, strings, UnitFaces.Load(rules));
            var card = MapCards.At(context, RaceNames.Load(rules), MapCards.LandmarkNames(rules), foreign.X, foreign.Y, (_, _) => false)!;
            Assert.Equal("DLG_R_C_STACK_NOSPY", card.Screen);
            var ui = new UiScreenState(screens.First(x => x.Id == card.Screen));
            card.Logic.Open(ui);
            return ui;
        }

        var plain = Card(session);
        Assert.Null(plain.Frame("IMG_LOWER"));
        Assert.Null(plain.Frame("IMG_BOOST"));

        var marks = D2.Data.Gfx.BattleMarkSets.Of(book);
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var leader = units[foreign.LeaderId];
        units[foreign.LeaderId] = leader with { Modifiers = [.. leader.Modifiers, marks.SpellLower.First()] };
        var cursed = Card(new GameSession(new GameState(GameInfo.FromMap("x", looks), looks with { Map = GameSession.With(session.Map, units: units) }), art, book));
        Assert.Equal("Battle:FIISOSPELLLOWER", cursed.Frame("IMG_LOWER"));
        Assert.Null(cursed.Frame("IMG_BOOST"));

        units[foreign.LeaderId] = leader with { Modifiers = [.. leader.Modifiers, marks.SpellBoost.First()] };
        var boosted = Card(new GameSession(new GameState(GameInfo.FromMap("x", looks), looks with { Map = GameSession.With(session.Map, units: units) }), art, book));
        Assert.Equal("Battle:FIISOSPELLBOOST", boosted.Frame("IMG_BOOST"));
        Assert.Null(boosted.Frame("IMG_LOWER"));
    }

    /// <summary>Портрет в рамке: кадр из слоёв — рамка карточки снизу, портрет <c>Faces:</c> сверху.</summary>
    private static bool Framed(string? frame) =>
        frame is not null && UiFrameRef.Layers(frame) is var (under, over) && under is MapCards.SmallBorder or MapCards.LargeBorder
        && over.StartsWith("Faces:", StringComparison.Ordinal);

    /// <summary>Большой юнит в карточке города (день 5 живой партии: в гарнизоне столицы Кланов большой на местах 0 и 1, окно армии
    /// «R=bb.x..») — рамка <c>BORDERUNITLARGE</c>, картинка и подпись первого места — на весь ряд (<c>0x57c4ef</c>…<c>0x57c687</c>),
    /// второе место пусто.</summary>
    [SkippableFact]
    public void CityCard_BigUnitSpansRow()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(sg), "нет корпуса");
        var looks = MapLooksLoader.Load(sg);
        string rules = Path.Combine(Data, "rules");
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt, GameBook.Load(rules));
        const string viewer = "S073PL000a";
        var context = new WindowContext(session, viewer, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(rules));
        var capital = session.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == viewer);
        // Клетка столицы без отряда (на входе стоит гость — там карточка отряда).
        var cell = session.Map.Plan.First(p => p.Value.Contains(capital.Id, StringComparer.OrdinalIgnoreCase)
                                               && !p.Value.Any(session.Map.Stacks.ContainsKey)).Key;
        var card = MapCards.At(context, RaceNames.Load(rules), MapCards.LandmarkNames(rules), cell.X, cell.Y, (_, _) => false)!;
        Assert.Equal("DLG_R_C_CITY", card.Screen);
        var layout = UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == card.Screen);
        var shaped = card.Logic.Shape(layout);
        var ui = new UiScreenState(shaped);
        card.Logic.Open(ui);
        UiRect Rect(UiScreen s, string id) => s.Elements.First(e => e.Id == id).Rect;
        Assert.StartsWith(MapCards.LargeBorder + UiFrameRef.Layer, ui.Frame("IMG_DEFENSE_0"));
        Assert.Equal(UiImageAlign.Center, ui.ImageAlign("IMG_DEFENSE_0"));
        Assert.Null(ui.Frame("IMG_DEFENSE_1"));
        Assert.Equal("", ui.TextOf("TXT_DEFENSE_1"));
        // Ряд гарнизона: 434…518 и 543…627 — картинка во весь ряд, подпись 110 точек по его середине (421…531 и 529…639 → 475).
        Assert.Equal(new UiRect(434, 145, 193, 99), Rect(shaped, "IMG_DEFENSE_0"));
        Assert.Equal(new UiRect(475, 244, 110, 33), Rect(shaped, "TXT_DEFENSE_0"));
        Assert.Equal(Rect(layout, "IMG_DEFENSE_2"), Rect(shaped, "IMG_DEFENSE_2"));
        Assert.StartsWith(MapCards.SmallBorder + UiFrameRef.Layer, ui.Frame("IMG_DEFENSE_3"));
    }

    /// <summary>Имя в карточке отряда — <c>CMidUnit::getName</c> (<c>0x5e97dc</c>): у реализации лидера (<c>IUsStackLeader</c>) — своё имя
    /// юнита из сохранения, у прочих — имя типа. Карта пользователя 11.10, снимок 10: нейтральный <c>S001KC0035</c> во главе с лидерским
    /// типом <c>G000UU5103</c> (<c>LEADER_CAT</c> 1) зовётся «Spearman», как записано в сценарии, и полужирный в группе. Тот же отряд с
    /// воином во главе (лидер подменён на <c>G000UU0012</c>, <c>LEADER_CAT</c> 0) зовётся по типу, и воин в группе не полужирный (<c>0x57c2fa</c>).
    /// Картина лидера — во весь рост по центру места.</summary>
    [Fact]
    public void Card_StackName_ByLeaderImplementation()
    {
        var looks = ScenarioFile.Read(ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "'A Return To Simpler Times'")).Looks;
        string rules = Path.Combine(Data, "rules");
        var book = GameBook.Load(rules);
        var screens = UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));
        UiScreenState CardOf(MapLooks l, out WindowContext context, out string type)
        {
            var session = new GameSession(new GameState(GameInfo.FromMap("x", l), l), SharedContent.Load(Data).MapArt, book);
            context = new WindowContext(session, "S001PL0001", UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(rules));
            var stack = session.Map.Stacks["S001KC0035"];
            type = session.Map.Units[stack.LeaderId].TypeId;
            var card = MapCards.At(context, RaceNames.Load(rules), MapCards.LandmarkNames(rules), stack.X, stack.Y, (_, _) => false)!;
            Assert.Equal("DLG_R_C_STACK", card.Screen);
            var ui = new UiScreenState(screens.First(s => s.Id == card.Screen));
            card.Logic.Open(ui);
            return ui;
        }
        bool AnyBold(UiScreenState ui) =>
            Enumerable.Range(1, 6).Any(i => ui.TextOf($"TXT_UNIT_{i}")?.Contains("MedBold", StringComparison.OrdinalIgnoreCase) == true);

        var leaderUi = CardOf(looks, out var ctx, out string leaderType);
        Assert.True(book.LeaderCategoryOf(leaderType) > 0);
        Assert.Equal("Spearman", leaderUi.TextOf("TXT_NAME"));
        Assert.True(AnyBold(leaderUi));
        Assert.Equal($"gfx:Events/{ctx.Faces.Base(leaderType)}", leaderUi.Frame("IMG_LEADER"));
        Assert.Equal(UiImageAlign.Center, leaderUi.ImageAlign("IMG_LEADER"));

        var map = looks.Map;
        var swapped = GameSession.With(map, stacks: map.Stacks.ToDictionary(p => p.Key,
            p => p.Key == "S001KC0035" ? p.Value with { LeaderId = "S001UN00c4" } : p.Value));
        var soldierUi = CardOf(looks with { Map = swapped }, out var ctx2, out string soldierType);
        Assert.Equal(0, book.LeaderCategoryOf(soldierType));
        Assert.Equal(ctx2.UnitName(soldierType), soldierUi.TextOf("TXT_NAME"));
        // Полужирный — по реализации, а не по месту лидера: воин во главе обычным, лидерский тип в группе — полужирным.
        Assert.Contains(Enumerable.Range(1, 6), i => soldierUi.TextOf($"TXT_UNIT_{i}") is { } t
            && t.EndsWith(ctx2.UnitName(soldierType), StringComparison.Ordinal) && !t.Contains("MedBold", StringComparison.OrdinalIgnoreCase));
        Assert.True(AnyBold(soldierUi));
    }

    /// <summary>Могила (<c>CEncLayoutTomb</c>): правый щелчок по клетке без отряда — <c>DLG_TOMB</c>, «Здесь покоится:» и по строке на
    /// запись с именем и победителем (день 30: у <c>S073TB0000</c> запись о погибшем отряде).</summary>
    [SkippableFact]
    public void Tomb_ShowsEpitaphs()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t30-055.sg");
        Skip.IfNot(File.Exists(sg), "нет корпуса");
        var looks = MapLooksLoader.Load(sg);
        string rules = Path.Combine(Data, "rules");
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt, GameBook.Load(rules));
        var context = new WindowContext(session, "S073PL000a", UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(rules));
        var races = RaceNames.Load(rules);
        var tomb = session.Map.Tombs.First(t => !session.Map.Stacks.Values.Any(s => s.X == t.X && s.Y == t.Y));
        var card = MapCards.At(context, races, MapCards.LandmarkNames(rules), tomb.X, tomb.Y, (_, _) => false)!;
        Assert.Equal(("DLG_TOMB", tomb.Id), (card.Screen, card.ObjectId));
        var ui = new UiScreenState(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == "DLG_TOMB"));
        card.Logic.Open(ui);
        string text = ui.TextOf("TXT_INFO") ?? "";
        Assert.Contains("Здесь покоится:", text);
        foreach (var entry in tomb.Entries) Assert.Contains(entry.StackName, text);
        Assert.Equal(tomb.Entries.Count(e => e.Killer.Length > 0), text.Split("победитель").Length - 1);
    }
}
