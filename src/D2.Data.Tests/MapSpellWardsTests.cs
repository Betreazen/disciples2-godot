using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;

namespace D2.Data.Tests;

/// <summary>Заклинание оберегов (<c>L_GIVE_WARDS</c>, <c>CSpellResultGiveWards</c> <c>0x41ce93</c>) — модовое: в ванили таких строк
/// нет. Мод дописывает в <c>Gspells</c> заклинание категории 12 с двумя оберегами ванили (<c>g000um0001</c> — воздух, <c>g000um0005</c> —
/// вода, оба <c>Once</c>) и площадью 3, которую результат оберегов не читает.</summary>
[Trait("Category", "Corpus")]
public class MapSpellWardsTests
{
    private const string Human = "S073PL000a";
    private const string WardSpell = "g999ss0001";
    private const string AirWard = "g000um0001", WaterWard = "g000um0005";

    private static readonly Lazy<GameContent> Content = new(() => SharedContent.Load(Path.Combine(AppContext.BaseDirectory, "data")));

    private static GameSession Load(ModRules mod)
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t62-102.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        mod.Row("Gspells", new Dictionary<string, object?>
        {
            ["SPELL_ID"] = WardSpell, ["CATEGORY"] = (int)SpellCategory.GiveWards, ["LEVEL"] = 1,
            ["CASTING_C"] = "g0000:r0000:y0010:e0000:w0000", ["BUY_C"] = "g0100:r0000:y0000:e0000:w0000",
            ["NAME_TXT"] = "x000tg4001", ["DESC_TXT"] = "x000tg4201", ["UNIT_ID"] = "g000000000", ["AREA"] = 3,
            ["MODIF_ID"] = "g000000000", ["MODIF_TXT"] = "g000000000", ["AI_CAT"] = 0, ["QTY_WARDS"] = 2,
            ["WARD1"] = AirWard, ["WARD2"] = WaterWard,
        });
        var looks = MapLooksLoader.Load(sg);
        var game = new GameSession(new GameState(new GameInfo("", "", [Human], 1, 1), looks), Content.Value.MapArt, GameBook.Load(mod.Dir));
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = players[Human] with { KnownSpells = [.. players[Human].KnownSpells, WardSpell.ToUpperInvariant()],
                                               Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        return new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);
    }

    /// <summary>Свой отряд в клетке: каждому юниту — оба оберега записями <c>CMidSpellEffects</c> со сроком «ход + 1», как у усиления
    /// (<c>CEffectModifier</c> <c>0x4396e2</c>); соседние отряды площади 3 не задеты; в бою юнит иммунен к воздуху и воде (<c>Once</c>).
    /// Чужой отряд — отказ «нет цели» (<c>0x5db2cb</c>: отказ 6), оплаты и записи нет.</summary>
    [SkippableFact]
    public void GiveWards_WardsEveryUnitOfOwnStackInCell_Only()
    {
        using var mod = new ModRules();
        var game = Load(mod);
        var spell = game.Book!.Spells[WardSpell];
        Assert.Equal([AirWard, WaterWard], spell.Wards, StringComparer.OrdinalIgnoreCase);
        Assert.Equal([SpellEffectKind.Ward, SpellEffectKind.Ward], SpellCast.Effects(spell).Select(e => e.Kind));

        var own = game.Map.Stacks.Values.First(s => Same(s.Owner, Human) && s.Inside.Length == 0);
        var others = game.Map.Stacks.Values.Where(s => !Same(s.Id, own.Id) && Math.Abs(s.X - own.X) <= 1 && Math.Abs(s.Y - own.Y) <= 1)
                         .SelectMany(s => s.UnitIds).ToList();
        var enemy = game.Map.Stacks.Values.First(s => !Same(s.Owner, Human) && s.Inside.Length == 0);
        int effects = game.Map.SpellEffects.Count, casts = game.Map.SpellCasts.Count;

        Assert.Equal("нет цели", game.CastSpell(Human, WardSpell, enemy.X, enemy.Y, useSpellBank: false));
        Assert.Equal((effects, casts), (game.Map.SpellEffects.Count, game.Map.SpellCasts.Count));

        Assert.Null(game.CastSpell(Human, WardSpell, own.X, own.Y, useSpellBank: false));
        var added = game.Map.SpellEffects.Skip(effects).ToList();
        Assert.Equal(own.UnitIds.Count * 2, added.Count);
        int stop = SpellCast.TurnStop(game.Map.Turn);
        foreach (string id in own.UnitIds)
        {
            Assert.Contains(AirWard, game.Map.Units[id].Modifiers, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(WaterWard, game.Map.Units[id].Modifiers, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, added.Count(r => Same(r.UnitId, id) && Same(r.OriginId, WardSpell) && Same(r.CasterId, Human) && r.TurnStop == stop));
        }
        Assert.DoesNotContain(added, r => others.Contains(r.UnitId, StringComparer.OrdinalIgnoreCase));

        var leader = MapBattle.Unit(game.Map.Units[own.LeaderId], 0, game.Book!.Rules, isAttacker: false);
        var granted = EffectiveStats.Of(leader.Type, leader.Chain).Immunities;
        Assert.Contains(new ImmunityGrant(7, ImmunityLevel.Once), granted);   // воздух
        Assert.Contains(new ImmunityGrant(5, ImmunityLevel.Once), granted);   // вода
        Assert.NotEqual(ImmunityLevel.NotImmune, Immunity.LevelsFor(leader, source: 7, AttackClass.Damage).BySource);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
