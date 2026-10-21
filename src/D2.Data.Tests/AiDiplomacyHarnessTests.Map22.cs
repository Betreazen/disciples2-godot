namespace D2.Data.Tests;

// Карта 22 (port-synth-22-ai-diplo2, Империя, 12 дней) партии 19.10 вечером — те же сверки дипломатии, что у карты 15: союзы Кланов с
// хода 0 с Легионами, Нежитью и человеком и их проверки разрыва с дня 6 (2, 4, 6, 8, 10 %), отношения ИИ ↔ ИИ после жезла и вора.
// Разбор — docs/synthetic-maps-port/analysis/20261019b-map22.md.
public partial class AiDiplomacyHarnessTests
{
    /// <summary>Карта 22: в журнале до неё карты 19–21, сев карты (<c>rng_seed</c> <c>new_game_time</c>) — seq 4237361.</summary>
    private static readonly Run[] Map22Runs = [new("20261019b-port-synth", "passive-001.jsonl", "port-synth-22-ai-diplo2", 4237300, 0)];

    /// <summary>Правила по полям строк: разрыв 160 (с дня 6, «сильные» Кланы: 5 крепостей против 1–3), предложение 149, подарок 156,
    /// сообщений 9 — 0 расхождений.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Checks_MatchRules_Map22_Synth() => Assert.Equal((160, 149, 156, 9), CheckRows(Map22Runs));

    /// <summary>Входы первого прохода из сохранения дня: 99 пар, сошлось 94. Остаток — то, что в тот же день до хода игрока сделали
    /// другие: вор Легионов у деревни Нежити (noble −5 дважды, день 1) и снос жезла (rod_destroyed −15) — отношение Нежить–Легионы 40 и 20
    /// у движка, 50 и 25 в сохранении; Легионы берут деревню FT0009 до хода Кланов (день 2, крепостей у цели 2, в сохранении 1); день 10
    /// Кланы рвут союз с Нежитью до её хода (союз), отношение Нежить–Легионы 0 после боя (1 в сохранении).</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void FirstPass_FromSaves_Map22_Synth() => Assert.Equal((99, 94), FirstPass(Map22Runs));

    /// <summary>Исполнений дипломатии 7 (ненависть дня 1 — 4, разрыв 2, предмет 1): выбор порта — первое добавленное — у всех 7.</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Execution_FirstAddedAction_Map22_Synth() => Assert.Equal(7, Executions(Map22Runs));

    /// <summary>Подарков с видом 39, сошлись все 39 (число кандидатов, есть ли действие, предложенное среди кандидатов).</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Gift_FromSaves_Map22_Synth() => Assert.Equal((39, 39), Gifts(Map22Runs));

    /// <summary>
    /// Карта 22 на R250 движка (<see cref="Replay_Synth"/>): оценок 309, сошлись все — в том числе 54 проверки разрыва с броском (дни
    /// 6–11) и два разрыва дня 10 (Кланы → Легионы, бросок 9 при шансе 10, проход 1; Кланы → Нежить, бросок 3, проход 2); исполнений 7,
    /// сошлись все — у разрыва слово пары после (отношение 10, бит союза снят, <c>0x5d98da</c>) и генератор после. Удачные броски дней 6
    /// (Легионы, 1 при 2) и 8 (человек, 4 при 6) не исполнились у обоих: первым в проходе стоял найм лидера и рост деревни (списки
    /// 0 и 3 раньше списка 8, <see cref="D2.Rules.Map.AiStrategic.ListOrder"/>), а на следующем проходе броски новые.
    /// Состояние докручивается строкой <c>fort_capture</c> (день 2: деревня Легионов до хода Кланов; без неё «крепости» у 3 оценок).
    /// </summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void Replay_Map22_Synth() => Assert.Equal(new ReplayCounts(309, 309, 7, 7, 0, 8), Replay(Map22Runs));
}
