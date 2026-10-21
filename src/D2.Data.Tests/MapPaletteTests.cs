using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Палитра карты (<see cref="MapPalette"/>, <see cref="MapPaletteLogic"/>) и перебор «Следующего лидера»
/// (<see cref="NextLeaderRule"/>) на сохранении дня 21 живой партии глазами Кланов.</summary>
[Trait("Category", "Corpus")]
public class MapPaletteTests
{
    private const string Viewer = "S073PL000a";
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static MapLooks? Looks()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t21-037.sg");
        return File.Exists(sg) ? MapLooksLoader.Load(sg) : null;
    }

    private static UiScreenState Screen(string id) =>
        new(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == id));

    /// <summary>Tab перебирает и отряды без очков хода (фильтр движка очков не смотрит, <c>0x41a4bd</c>); отряд в деревне с бунтом —
    /// нет (<c>0x5cdd8c</c>); чужие — нет.</summary>
    [SkippableFact]
    public void NextLeader_IgnoresMovePoints_SkipsRiot()
    {
        var looks = Looks();
        Skip.If(looks is null, "нет корпуса");
        var map = looks!.Map;
        bool Own(MapStack s) => s.Owner.Equals(Viewer, StringComparison.OrdinalIgnoreCase);
        var own = map.Stacks.Values.Where(Own).Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.True(own.Count >= 2);

        var tired = GameSession.With(map, stacks: map.Stacks.ToDictionary(p => p.Key, p => Own(p.Value) ? p.Value with { Move = 0 } : p.Value));
        Assert.Equal(own, NextLeaderRule.Of(tired, Own).Select(s => s.Id));

        var village = map.Sites.First(s => s.Kind == SiteKind.Village);
        string inRiot = own[0];
        var riot = GameSession.With(tired,
            stacks: tired.Stacks.ToDictionary(p => p.Key, p => p.Key == inRiot ? p.Value with { Inside = village.Id } : p.Value),
            sites: [.. tired.Sites.Select(s => s.Id == village.Id ? s with { RiotTurn = map.Turn + 2 } : s)]);
        Assert.Equal(own.Skip(1), NextLeaderRule.Of(riot, Own).Select(s => s.Id));
    }

    /// <summary>В начале партии полоса ресурсов видна (ответ пользователя 11.10), знамён нет (по умолчанию настроек), палитра — мини-карта;
    /// переключатели и
    /// кнопка смены меняют состояние и зовут хозяина; у владений — значки рас и доли равнины.</summary>
    [SkippableFact]
    public void Palette_TogglesAndLand()
    {
        var looks = Looks();
        Skip.If(looks is null, "нет корпуса");
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var palette = new MapPalette();
        MapPalette.Banners = false;
        var logic = new MapPaletteLogic(palette, () => LandShare.Of(looks!.Map, book.Economy), id => id == "X005TA0707" ? "%QTY%%" : id);
        int changed = 0;
        logic.Changed += () => changed++;

        var pal = Screen(MapPalette.Pal);
        logic.Open(pal);
        Assert.True(pal.IsChecked("TOG_RESPOPUP"));
        Assert.False(pal.IsChecked("TOG_BANNERS"));
        Assert.Equal(MapPalette.Pal, palette.Screen);

        logic.Handle(new UiToggled("TOG_RESPOPUP", false), pal);
        Assert.False(palette.Resources);
        logic.Handle(new UiToggled("TOG_RESPOPUP", true), pal);
        logic.Handle(new UiToggled("TOG_BANNERS", true), pal);
        logic.Handle(new UiClicked("BTN_RES_PAL"), pal);
        Assert.Equal(4, changed);
        Assert.True(palette.Resources);
        Assert.True(MapPalette.Banners);
        Assert.Equal(MapPalette.LandScreen, palette.Screen);

        var land = Screen(MapPalette.LandScreen);
        logic.Open(land);
        Assert.True(land.IsChecked("TOG_RESPOPUP"));
        var shares = LandShare.Of(looks!.Map, book.Economy);
        Assert.NotEmpty(shares);
        Assert.Equal(shares[0].Logo, land.Frame("IMG_RACE_1"));
        Assert.Equal(UiImageAlign.Center, land.ImageAlign("IMG_RACE_1"));
        Assert.Equal($"{shares[0].Percent}%", land.TextOf("TXT_RACE_1"));
        Assert.Equal(AiDiplomacy.PlainLandShare(looks.Map, shares[0].RaceType), shares[0].Percent);
        if (shares.Count < 5) Assert.Equal("", land.TextOf($"TXT_RACE_{shares.Count + 1}"));
        MapPalette.Banners = false;
    }
}
