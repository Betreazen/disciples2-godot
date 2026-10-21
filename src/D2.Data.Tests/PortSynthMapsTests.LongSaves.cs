namespace D2.Data.Tests;

/// <summary>Карта 23 против журнала и сохранений оригинала: партию (сохранения <c>port-synth-23-ai-long-*.sg</c>) харнесс находит в корпусе
/// сам — каталог, журнал по <c>manifest.json</c>, вход в партию (<c>game_phase</c>) перед первым её сохранением. Пока партии нет — пропуск.
/// Разбор замысла — <c>docs/synthetic-maps-port/analysis/20261019c-map23.md</c>.</summary>
public partial class PortSynthMapsTests
{
    /// <summary>Сохранений карты 23 в одном журнале — не меньше (день и утро следующего).</summary>
    private const int LongMinSaves = 2;

    /// <summary>Сверенных дней партии — не меньше (памятка: до дня 60, не меньше 50; запас на пропущенные Q).</summary>
    private const int LongMinDays = 45;

    /// <summary>
    /// Ходы ИИ каждого дня карты 23 — с сохранения оригинала того же дня (Q в начале дня, памятка): порт проводит конец хода человека и
    /// ходы трёх ИИ на R250 и CRT движка в миг конца хода, с памятью тактики движка прошлых дней и его счётчиками id (как
    /// <see cref="Diplo2_AiTurnsMatchTheOriginalJournal"/>): выбор за выбором (отряд, тип, цель, точка, счёт, статус) — с
    /// <c>ai_tactic_pick</c>/<c>ai_tactic_result</c>, шаги стратегии — с <c>ai_exec</c>/<c>ai_strategic_step</c>, клетки отрядов — с
    /// сохранением следующего утра. Расхождение дня не тянется в следующий: каждый день — с состояния движка.
    /// <para>Бои дня порт играет на R250 движка (перед боем и после него генератор ставится как у движка — <c>DayCheck</c>), так что
    /// исход боя не сдвигает броски остатка дня; сверка боёв, R250 у выборов и земли — строки «бой:» (<c>DayCheck</c>, у карты 23 их 0).
    /// Числа — <see cref="LongPicksSame"/>. Печать — выборы движка по типам и бегства защитника.</para>
    /// </summary>
    /// <remarks>Тест — в <see cref="PortSynthMapsTests_Long"/> (своим классом, параллельно остальным).</remarks>
    internal void Long_AiTurnsMatchTheOriginalJournal()
    {
        Skip.IfNot(MapIds().Contains(LongMap));
        var runs = LongJournals().ToList();
        Skip.If(runs.Count == 0, $"партии {LongMap} в корпусе нет");
        var t = DailyAiTurns(Out, LongMap, runs, LongMinSaves);
        Assert.True(t.Days >= LongMinDays, $"сверено дней {t.Days} — партия короче памятки или пропущены сохранения");
        Assert.Equal((LongEnginePicks, LongEngineSteps), (t.Picks, t.Steps));   // ответ движка — до сверки, чтобы смена корпуса падала понятно
        Assert.Equal((LongPicksSame, LongStepsSame, LongExtra, LongMorning), (t.PicksSame, t.StepsSame, t.Extra, t.Morning));
        Assert.Equal(0, t.Checks);   // бои, R250 у выборов и ходов, земля — как у движка (DayCheck)
        // Клетки отрядов и гарнизонов ИИ наутро (расстановка в миг хода, И33): 0 — а у AiGroupArrangeHarnessTests_PortSynth23 по той же
        // партии 28 групп из 1527 «не сходятся»: та сверка берёт силу из сохранения следующего дня.
        Assert.Equal(0, t.Cells);
        // Тип, уровень, здоровье и опыт юнитов ИИ наутро (И230; 20.10, r20/xp: было 114 юнито-дней, 113 «+1» — 0).
        Assert.Equal(0, t.UnitStates);
    }

    /// <summary>Ответ движка партии 20261020-port-synth (59 сверенных дней): выборов тактики и шагов стратегии.</summary>
    private const int LongEnginePicks = 2057, LongEngineSteps = 488;

    /// <summary>
    /// Первый замер 20.10 (ветка <c>r20/aiA</c>, <c>docs/synthetic-maps-port/analysis/20261020-map23-ai.md</c>): было 1988/2057 выборов и
    /// 425/488 шагов; после правок И110, И130, И93 и харнесса (R250 дня 1 и у боёв, слот 2 подмоги — И112) — 2034/2057 и 487/488, лишних
    /// 23, клеток утра 20. 20.10 (ветка <c>r20/rest</c>, <c>docs/synthetic-maps-port/analysis/20261020-map23-rest.md</c>) — <b>всё сошлось</b>:
    /// юниты боя в порядке <c>unitsInfo</c> — от последнего юнита группы к первому (<c>0x62136d</c>; Б80): 2034 → 2039; R250 после боя —
    /// по броскам не меньше 24 бит (<c>JournalRng.BeforeDraws</c>, харнесс: восемь <c>rng(1)</c> найма лидера не задают сдвиг) — 2044;
    /// помощники павшего лидера отпускаются и назначаются заново (<c>0x438d31</c>, <c>0x45603b</c>; И151) — 2053; земля взятой деревни
    /// сразу (<c>CEffectCapture</c> <c>0x4341bb</c>) и квадраты жил с цепочкой (<c>0x60b966</c>; И152) — 2057/2057, 488/488; отход отряда,
    /// проигравшего дуэль вора, с прежним запасом хода (И153) — клетки утра 0.
    /// </summary>
    private const int LongPicksSame = 2057, LongStepsSame = 488, LongExtra = 0, LongMorning = 0;

    /// <summary>Журналы корпуса с сохранениями карты 23 (<see cref="DailyJournals"/>).</summary>
    private static IEnumerable<(string Dir, string Journal, long From)> LongJournals() => DailyJournals(LongMap);
}
