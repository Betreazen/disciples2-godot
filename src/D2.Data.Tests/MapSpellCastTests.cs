using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>Литьё на карте (<see cref="GameSession.CastSpell"/>, <c>docs/ai-spells.md</c> §3) на сохранении живой партии.</summary>
[Trait("Category", "Corpus")]
public class MapSpellCastTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// Журнал <c>20260919-human</c>, день 44, <c>seq</c> 12168909: PL000c бросает «G000SS0070» (удар 30) в отряд человека
    /// на (80,74) — три юнита теряют по 30 (180 → 150, 250 → 220, 89 → 59), брони и случайности нет; второй раз за день —
    /// отказ; своё заклинание в свой же отряд — отказ.
    /// </summary>
    [SkippableFact]
    public void Attack_DamagesEveryUnit_OncePerDay()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t44-077.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var game = new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks), content.MapArt, book);
        string[] units = ["S073UN020f", "S073UN0210", "S073UN025a"];
        var before = units.Select(u => game.Map.Units[u].Hp).ToArray();
        Assert.Equal([180, 250, 89], before);

        Assert.Null(game.CastSpell("S073PL000c", "G000SS0070", 80, 74, useSpellBank: true));

        Assert.Equal([150, 220, 59], units.Select(u => game.Map.Units[u].Hp));
        Assert.Equal("уже брошено сегодня", game.SpellRefusal("S073PL000c", "G000SS0070", 80, 74, useSpellBank: true));
        var own = game.Map.Stacks.Values.First(s => s.Owner.Equals("S073PL000c", StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        Assert.Equal("свой отряд", game.SpellRefusal("S073PL000c", "G000SS0067", own.X, own.Y, useSpellBank: true));
    }

    /// <summary>
    /// Клетка призыва (<c>0x5edded</c>, <c>0x5e6db2</c>): журнал human, день 42 — PL000b призывает «G000SS0041» у отряда
    /// человека на (15,17), движок ставит на (16,16). Кольцо 1 проверяется с конца: (16,17) — ориентир, (16,16) — пусто.
    /// Вода годится: пустая клетка воды (14,19) — сама себе клетка; клетка с отрядом — нет (кольцо дальше).
    /// </summary>
    [SkippableFact]
    public void SummonCell_RingFromTheEnd_WaterAllowed()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t42-074.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var game = new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks), content.MapArt, book);
        Assert.Equal(D2.Rules.Map.Ground.Water, game.Map.At(14, 19).Ground);

        Assert.Equal((16, 16), game.SummonCell(15, 17));
        Assert.Equal((14, 19), game.SummonCell(14, 19));
    }
}
