using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Magic;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Tests;
using D2.Rules.Units;
using Xunit.Abstractions;

/// <summary>Модификатор заклинания карты в бою: «Зов предков» (<c>G000SS0088</c>, <c>AREA</c> 999, <c>g000um0088</c> —
/// <c>GmodifL TYPE 5</c> броня <c>NUMBER 33</c>) — см. <see cref="SaveSnapshots"/> (файл <c>SaveSnapshots.Spells.cs</c>).</summary>
[Trait("Category", "Corpus")]
public class MapSpellBattleTests(ITestOutputHelper output)
{
    private const string AncestorsCall = "g000ss0088";

    [SkippableFact]
    public void AncestorsCall_AddsThirtyThreeArmor()
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var spell = SpellLoader.Load(GamePaths.Globals)[AncestorsCall];
        var rules = RulesLoader.Load(GamePaths.Globals);
        var modifier = SpellCast.ModifierOf(spell, ModifierLoader.Load(GamePaths.Globals).Modifiers)!;
        var dwarf = rules.Units["g000uu0028"];

        Assert.Equal(999, spell.Area);
        Assert.Equal(EffectiveStats.Of(dwarf, []).Armor + 33, EffectiveStats.Of(dwarf, [modifier]).Armor);
    }

    /// <summary><c>20260919-human/passive-001</c>: заклинание брошено в seq 15480877 (ход 62), снимок 103 — 15522643,
    /// снятие — <c>spell_expire</c> заклинателя 15604663. Бои 15504360 и 15520354 — до снимка; нападают лидеры,
    /// нанятые после заклинания, и модификатора у них нет (журнал: броня 0). Бой 15529111 — после снимка, нападает
    /// <c>S073KC0001</c>, на всех пятерых модификатор есть (журнал: 33, 63). Все три боя грузятся, и каждая броня
    /// журнала сходится с нашей.</summary>
    [SkippableTheory]
    [InlineData(15504360L, false)]
    [InlineData(15520354L, false)]
    [InlineData(15529111L, true)]
    public void Corpus_AncestorsCall_ReachesExactlyItsTargets(long battleSeq, bool attackersBuffed)
    {
        Skip.IfNot(GamePaths.IsAvailable, GamePaths.GameDir);
        var path = CorpusFiles.Journals().FirstOrDefault(p => p.Replace('\\', '/').EndsWith("20260919-human/passive-001.jsonl", StringComparison.Ordinal));
        Skip.If(path is null, "нет журнала 20260919-human/passive-001.jsonl");
        var rules = RulesLoader.Load(GamePaths.Globals);
        var battle = JournalCache.Battles(path).Single(b => b.Start.Seq == battleSeq);
        int turn = RouteCasts.TurnAt(RouteCasts.TurnMarks(path), battleSeq);

        Assert.True(CorpusBattleLoader.TryLoad(battle, rules, out var state, out var reason, saves: SaveSnapshots.Of(path),
                                               turn: turn, spells: MapEffectTables.Load(GamePaths.Globals)), reason);

        foreach (var u in state.Units.Where(u => u.Attacker))
            Assert.Equal(attackersBuffed, u.Modifiers.Any(m => m.Id.Equals("g000um0088", StringComparison.OrdinalIgnoreCase)));
        int checkedArmor = 0;
        foreach (var a in battle.Events.OfType<ArmorEvent>().DistinctBy(a => a.Unit))   // первая броня юнита — до защиты
        {
            if (state.Unit(a.Unit) is not { } u) continue;
            output.WriteLine($"{a.Unit}: журнал {a.Ret}, наше {Formulas.Armor(u, rules.Constants)}");
            Assert.Equal(a.Ret, Formulas.Armor(u, rules.Constants));
            checkedArmor++;
        }
        Assert.True(checkedArmor > 0, "в бою нет брони для сверки");
    }
}
