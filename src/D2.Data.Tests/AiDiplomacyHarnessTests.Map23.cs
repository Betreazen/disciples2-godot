namespace D2.Data.Tests;

// Карта 23 (port-synth-23-ai-long, Империя за стеной гор, три ИИ на очень трудной, 60 дней) партии 19.10 ночью — те же сверки
// дипломатии, что у карт 15 и 22: отношения всех пар 50 (D_NEUTRAL 66 — ни союзов, ни подарков), бои ИИ ↔ ИИ, воры и жезлы двигают
// отношения. Разбор — docs/synthetic-maps-port/analysis/20261020-map23-battle.md.
public partial class AiDiplomacyHarnessTests
{
    /// <summary>Карта 23 — единственная в журнале.</summary>
    private static readonly Run[] Map23Runs = [new("20261020-port-synth", "passive-001.jsonl", "port-synth-23-ai-long", 0, 0)];

    /// <summary>Правила по полям строк: разрывов 0 (союзов нет), предложений 1977, подарков 0, сообщений 9 — 0 расхождений.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Checks_MatchRules_Map23_Synth() => Assert.Equal((0, 1977, 0, 9), CheckRows(Map23Runs));

    /// <summary>Входы первого прохода из сохранения дня: 531 пара, сошлось 442. Остаток 89 — отношение: в тот же день до хода игрока
    /// его отношение опустили бои ИИ ↔ ИИ, воры и жезлы других (журнал 38 и 23, сохранение 50), как у карты 22.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void FirstPass_FromSaves_Map23_Synth() => Assert.Equal((531, 442), FirstPass(Map23Runs));

    /// <summary>Исполнений дипломатии 9 (ненависть дня 1): выбор порта — первое добавленное — у всех 9.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Execution_FirstAddedAction_Map23_Synth() => Assert.Equal(9, Executions(Map23Runs));

    /// <summary>Подарков нет: отношения не выше D_NEUTRAL.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Gift_FromSaves_Map23_Synth() => Assert.Equal((0, 0), Gifts(Map23Runs));

    /// <summary>На R250 движка: оценок 1977, сошлись все; исполнений 9 — сошлись 7, у двух ненавистей дня 1 сдвиг генератора по опорам
    /// не подобрался (без генератора 2 — не расхождение).</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Replay_Map23_Synth() => Assert.Equal(new ReplayCounts(1977, 1977, 9, 7, 2, 104), Replay(Map23Runs));
}
