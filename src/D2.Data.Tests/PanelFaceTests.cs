using D2.Data.Content;
using D2.Data.Gfx;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Портрет лидера в панели команд (<see cref="PanelFace"/>, <c>0x48d8f3</c>) и портрет окна итога обучения
/// (<see cref="TrainingDoneLogic.Face"/>) — на сохранении оригинала дня 3 Кланов, где у отряда S073KC0003 лидер пал (<c>LEADR_ALIV</c> 0).
/// Партия 17.10, пометка 3: у порта в панели был обычный портрет павшего лидера BI2.</summary>
[Trait("Category", "Corpus")]
public class PanelFaceTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static WindowContext Ctx()
    {
        var c = Context();
        Skip.If(c is null, "нет корпуса");
        return c!;
    }

    private static WindowContext? Context()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t03-008.sg");
        if (!File.Exists(sg)) return null;
        var looks = MapLooksLoader.Load(sg);
        string rules = Path.Combine(Data, "rules");
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt, GameBook.Load(rules));
        return new WindowContext(session, "S073PL000a", UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(rules));
    }

    [SkippableFact]
    public void DeadLeader_SkullMaskWithoutWounds()
    {
        var c = Ctx();
        var stack = c.Map.Stacks["S073KC0003"];
        Assert.False(stack.LeaderAlive);
        var leader = c.Map.Units[stack.LeaderId];
        Assert.True(leader.Hp <= 0);
        var face = PanelFace.Of(c, leader, "X", PanelFace.SmallWidth);
        Assert.True(face.Dead);
        Assert.Equal("MASKDEADS", face.Mask);
        Assert.Equal(100, face.Percent);   // красной доли у павшего нет — состояние 10 вместо setPercentHp
        Assert.Equal(85, face.KeptRows(85));
        Assert.True(PanelFace.Mirrored);
        // Широкая картинка (большой без своего FACES) — маска L (0x5b16f9: S только у ширины 70).
        Assert.Equal("MASKDEADL", PanelFace.Of(c, leader, "X", 150).Mask);
        Assert.Contains("павший=1 маска=MASKDEADS доля=100", face.Line, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void LiveLeader_WoundShareAndIcons()
    {
        var c = Ctx();
        var stack = c.Map.Stacks.Values.First(s => s.LeaderAlive && s.Owner == c.Viewer);
        var leader = c.Map.Units[stack.LeaderId];
        int max = c.Session.HpMax(leader);
        var healthy = PanelFace.Of(c, leader with { Hp = max }, "X", PanelFace.SmallWidth);
        Assert.False(healthy.Dead);
        Assert.Null(healthy.Mask);
        Assert.Equal(100, healthy.Percent);
        // Ранен на треть: hp·100/max (0x48da9b…0x48daa8), нижние строки — красным.
        var wounded = PanelFace.Of(c, leader with { Hp = max / 3 }, "X", PanelFace.SmallWidth);
        Assert.Equal(max / 3 * 100 / max, wounded.Percent);
        Assert.Equal(85 * wounded.Percent / 100, wounded.KeptRows(85));
        // Значки по цепочке модификаторов (0x5ea500): усиление (0, 0), ослабление (0, 56), зелье (0, 28).
        var marks = BattleMarkSets.Of(c.Session.RequireBook());
        var mods = new[] { marks.SpellBoost.First(), marks.SpellLower.First(), marks.Potion.First() };
        var icons = PanelFace.Of(c, leader with { Hp = max, Modifiers = mods }, "X", PanelFace.SmallWidth).Icons;
        Assert.Equal([("FIISOSPELLBOOST", 0, 0), ("FIISOSPELLLOWER", 0, 56), ("FIPOTIONBOOST", 0, 28)],
                     icons.Where(i => !i.Centered).Select(i => (i.Name, i.X, i.Y)));
        // Высокий уровень (0x61ea4c): +5 к уровню базового — FIHIGHLEVEL1 по середине ширины, у верха.
        var veteran = PanelFace.Of(c, leader with { Level = leader.Level + 15 }, "X", PanelFace.SmallWidth).Icons;
        Assert.Contains(veteran, i => i.Centered && i.Y == 0 && i.Name.StartsWith("FIHIGHLEVEL", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void CapitalGuest_SameStateWithoutIcons()
    {
        var c = Ctx();
        var dead = c.Map.Units[c.Map.Stacks["S073KC0003"].LeaderId];
        string face = c.Faces.Panel(dead.TypeId);
        // 0x486ac7: павший гость — состояние 10, маска поверх лица; отражение — у панели (CapitalPanelLogic).
        Assert.Equal(UiFrameRef.Masked(face, "MASKDEADS"), CapitalGuestFace.Of(c, dead));
        var live = c.Map.Units[c.Map.Stacks.Values.First(s => s.LeaderAlive && s.Owner == c.Viewer).LeaderId];
        int max = c.Session.HpMax(live);
        Assert.Equal(c.Faces.Panel(live.TypeId), CapitalGuestFace.Of(c, live with { Hp = max }));
        Assert.Equal(UiFrameRef.Tinted(c.Faces.Panel(live.TypeId), new UiColor(255, 0, 0), max / 2 * 100 / max),
                     CapitalGuestFace.Of(c, live with { Hp = max / 2 }));
    }

    [SkippableFact]
    public void TrainingDone_FaceInBorderByUnitSize()
    {
        var c = Ctx();
        var book = c.Session.RequireBook();
        var units = c.Map.Units.Values.Where(u => book.Rules.Units.ContainsKey(u.TypeId)).ToList();
        var small = units.First(u => book.Rules.Units[u.TypeId].SizeSmall);
        Assert.Equal(UiFrameRef.Layered(MapCards.SmallBorder, c.Faces.Small(small.TypeId)), TrainingDoneLogic.Face(c, small));
        if (units.FirstOrDefault(u => !book.Rules.Units[u.TypeId].SizeSmall) is { } big)
            Assert.Equal(UiFrameRef.Layered(MapCards.LargeBorder, c.Faces.Small(big.TypeId)), TrainingDoneLogic.Face(c, big));
    }
}

/// <summary>Портрет панели без корпуса: ключ кэша меняется с состоянием, доля — строками сверху, строка проверки.</summary>
public class PanelFaceRecordTests
{
    [Fact]
    public void Key_ChangesWithState_KeptRowsFromTop()
    {
        var healthy = new PanelFace("F", false, null, 100, []);
        var wounded = healthy with { Percent = 40 };
        var dead = healthy with { Dead = true, Mask = "MASKDEADS" };
        var boosted = healthy with { Icons = [new PanelFaceIcon("FIISOSPELLBOOST", 0, PanelFace.BoostY)] };
        Assert.Equal(4, new[] { healthy.Key, wounded.Key, dead.Key, boosted.Key }.Distinct().Count());
        Assert.Equal(85, healthy.KeptRows(85));
        Assert.Equal(34, wounded.KeptRows(85));   // 85·40/100: верхние 34 строки как есть, ниже — красным
        Assert.Equal("отражено=1 павший=1 маска=MASKDEADS доля=100 значки=-", dead.Line);
        Assert.EndsWith("значки=FIISOSPELLBOOST", boosted.Line, StringComparison.Ordinal);
    }
}
