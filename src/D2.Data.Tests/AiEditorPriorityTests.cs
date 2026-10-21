using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Приоритет редактора цели (<c>AIPRIORITY</c>, <c>0x466d5c</c>) на картах корпуса: поле читается у крепостей, руин и
/// площадок, а поправка фазы 2 (<see cref="AiTactics.Adjusted"/>) ставит его до +100 множества целей. Сверка с журналом — харнесс
/// кандидатов (строка «приоритет редактора цели»), здесь — значения, которых в журналах нет (0, 4, 5).</summary>
[Trait("Category", "Corpus")]
public class AiEditorPriorityTests
{
    private static readonly AiWeights NoWeights = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static MapState Map(string corpus, string file)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), corpus, file);
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        return MapLoader.Load(path);
    }

    [SkippableFact]
    public void Priority_ReadFromSave()
    {
        var s073 = Map("20260919-human", "rastlennaya-vlast-dwarf-t04-010.sg");
        Assert.Equal(6, AiTactics.TargetPriority(s073, "S073FT0004"));
        Assert.Equal(6, AiTactics.TargetPriority(s073, "S073FT0007"));
        Assert.Equal(AiTacticScore.DefaultPriority, AiTactics.TargetPriority(s073, s073.Stacks.Keys.First()));
        Assert.Null(AiTactics.TargetPriority(s073, "S073LO0000"));
        Assert.Null(AiTactics.TargetPriority(s073, ""));
        var s135 = Map("20260920-gaps", "ai-g3-cure-fear-human-t01-032.sg");
        Assert.Equal(0, AiTactics.TargetPriority(s135, "S135RU0001"));
        Assert.Equal(4, AiTactics.TargetPriority(s135, "S135FT0001"));
    }

    /// <summary>Приоритет 0 — только цель приказа отряда окна, иначе <see cref="AiTactic.NoScore"/>; 4 — +33, затем +100 множества.</summary>
    [SkippableFact]
    public void Adjusted_PriorityBeforeTargetSet()
    {
        var map = Map("20260920-gaps", "ai-g3-cure-fear-human-t01-032.sg");
        var stack = map.Stacks.Values.First();
        var turn = new AiTacticTurn(stack.Owner, NoWeights, [], map.Turn, AiTacticMemo.Empty)
        {
            WinTargets = new HashSet<string>(["S135FT0001"], StringComparer.OrdinalIgnoreCase),
        };
        AiScored<AiTacticOption>[] list =
        [
            new(new AiTacticOption(AiActionType.VisitRuin, stack.Id, "S135RU0001", 0, 0), 100),
            new(new AiTacticOption(AiActionType.AttackCity, stack.Id, "S135FT0001", 0, 0), 100),
        ];
        var free = AiTactics.Adjusted(map, turn, stack with { OrderTarget = "" }, list);
        Assert.Equal([AiTactic.NoScore, 233], free.Select(c => c.Score));
        var ordered = AiTactics.Adjusted(map, turn, stack with { OrderTarget = "S135RU0001" }, list);
        Assert.Equal([100, 233], ordered.Select(c => c.Score));
    }
}
