using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;

/// <summary>Превращение Ведьмой по журналу оригинала — бой карты 17 <c>port-synth-17-mixed</c> (<c>20261018b-port-synth [5062040]</c>,
/// партия 19.10): потолок HP превращённого и сила бегства ИИ по нему. Каждая строка <c>ai_strength_units</c> с Бесом
/// (<c>G000UU5034</c>, превращён <c>G000AA0067</c>) сверяется с моделью: юнит боя с HP строки, превращение
/// <see cref="TransformAttacks.TransformOther"/>, затем <c>max</c> строки (<c>getHpMax</c> <c>0x5e987c</c>) против
/// <see cref="BattleUnit.HpMax"/> и <c>s</c> строки (<c>0x5dd06c</c>) против <see cref="AiStrength.Seen"/>.</summary>
[Trait("Category", "Corpus")]
public class TransformOtherCorpusTests
{
    private static readonly Lazy<GameRules> Rules = new(() => RulesLoader.Load(GamePaths.Globals));
    private const string Imp = "G000UU5034";

    [SkippableFact] public void Corpus_WitchImp_KeepsOriginalHpMax_AndStrength()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        var path = Path.Combine(CorpusFiles.Dir, "20261018b-port-synth", "passive-001.jsonl");
        Skip.IfNot(File.Exists(path), "corpus/20261018b-port-synth вне git");
        var battle = JournalCache.Battles(path).Single(b => b.Start.Seq == 5062040);
        var c = Rules.Value.Constants;
        var rows = JournalCache.Read(path, "ai_strength_units").OfType<AiStrengthUnitsEvent>()
            .Where(e => e.Seq > battle.Start.Seq && e.Seq < battle.End!.Seq)
            .SelectMany(e => e.Units.Where(u => string.Equals(u.Type, Imp, StringComparison.OrdinalIgnoreCase)).Select(u => (e, u)))
            .ToList();
        Assert.True(rows.Count >= 10, $"строк с Бесом {rows.Count}");   // 0f: раунды 2–4, 0d: раунды 3–4

        int checkedRows = 0;
        foreach (var (e, row) in rows)
        {
            Assert.True(CorpusBattleLoader.TryLoad(battle, Rules.Value, out var state, out var reason), reason);
            var witch = state.Unit("S143UN000c")!;
            var unit = state.Unit(row.Id!)!;
            unit.Hp = (int)row.Hp;
            Assert.True(TransformAttacks.TransformOther(witch.Attack, unit, state).Applied);
            Assert.Equal(Imp, unit.Type.Id, ignoreCase: true);

            Assert.Equal(row.Max, unit.HpMax);   // 250 / 150 — потолок исходного, не 50 Беса
            double s = AiStrength.Unit(AiStrength.Seen(unit), e.A3 == 1, (int)e.Enemies, c, (int)(e.Armor ?? 0));
            Assert.Equal(row.S!.Value, s, 6);
            checkedRows++;
        }
        Assert.Equal(rows.Count, checkedRows);
    }
}
