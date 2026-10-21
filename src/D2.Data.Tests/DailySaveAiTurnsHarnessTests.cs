using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Ходы ИИ каждого дня с сохранения оригинала того же дня — на любой партии корпуса, где человек сохранялся (Q) каждый день: порт проводит
/// конец хода человека и ходы ИИ на R250 и CRT движка, с памятью тактики движка и его счётчиками id (<c>PortSynthMapsTests.SavedAiDays</c>,
/// как у карт 22 и 23); сверяются выбор за выбором (отряд, тип, цель, точка, счёт, статус), шаги стратегии и клетки отрядов следующего утра.
/// Нужен зонд 7.5 (броски R250 вне боя — <c>ai_rng</c>): старые длинные партии (legions, probe65, probe72, human, карта 5) его не имеют —
/// их остатки реестра закрываются переигровкой с Q каждый день (<c>docs/synthetic-maps-port/replay-old-parties.md</c>, разбор
/// <c>docs/synthetic-maps-port/analysis/20261020-replay.md</c>).
/// Переигровки идут на своих картах 24–27 (<c>port-synth-24-replay-q09</c> …: исходный сценарий байт в байт, кроме имени и брифинга —
/// <c>tools/synth-maps/maps_replay.py</c>), чтобы сохранения назывались по карте и не смешивались со старыми партиями.
/// <para>Строка — каталог корпуса (точное имя или окончание после даты: <c>replay-q09</c> найдёт <c>20261021-replay-q09</c>), начало имени
/// сохранений партии, сколько дней сверить не меньше и числа: выборов движка и совпавших, шагов стратегии движка и совпавших, лишних строк
/// у порта, клеток утра разошлось, расхождений состояния дня (необязательно). Число −1 — не закреплено (печать без проверки; закрепить первым замером). Каталога нет — пропуск.</para>
/// </summary>
[Trait("Category", "Corpus")]
public sealed class DailySaveAiTurnsHarnessTests(ITestOutputHelper output)
{
    /// <summary>Переигровки старых партий штатных и ai-карт — карты 24–26. Ждут партий (памятка <c>replay-old-parties.md</c>).</summary>
    [SkippableTheory]
    // Карта 24 — ai-q09 за Империю, средняя, 30 дней, только «Конец хода» (как probe65 и probe72): И31, И32 (Нежить дня 15), И36
    // (Эльфы дней 4–5), И42 (дни 24, 27), И70.
    // 20.10 (r20b/q09; партия 20261020-replay-q09, 29 дней): первый замер 640/661, 139/170, лишних 54, утро 11, состояние дня 36 → сведено к
    // нулю: картинка дерева перекрашенной клетки леса — бросок CRT сервера (И210, 0x5e5f84; шаги 139 → 170), вход в деревню убитого гостя
    // (И211, 0x4413d9), призванный — в начало очереди (И172, 0x467313/0x467439), точка атаки с заклинанием по готовности (И173, 0x45844e),
    // «отряд ещё ходит» фазы 0 (И174, 0x466bcb), цель искателя усечённого пути (И175). Разбор — 20261020-replay-q09.md.
    [InlineData("replay-q09", "port-synth-24-replay-q09-human", 27, 661, 661, 170, 170, 0, 0)]
    public void AiTurns_MatchTheOriginalJournal(string run, string stem, int minDays, int wantPicks, int wantPicksSame, int wantSteps,
                                                int wantStepsSame, int wantExtra, int wantMorning, int wantChecks = -1) =>
        DailySaveAiTurns.Check(output, run, stem, minDays, (wantPicks, wantPicksSame, wantSteps, wantStepsSame, wantExtra, wantMorning), wantChecks);
}

/// <summary>Строка карты 25 <see cref="DailySaveAiTurnsHarnessTests"/> — своим классом: xUnit гонит строки одного класса подряд, а длинные
/// партии (54 и 45 дней) так шли бы одна за другой в хвосте прогона (<c>docs/perf-log.md</c>, «Скорость проверок, 20.10»).</summary>
[Trait("Category", "Corpus")]
public sealed class DailySaveAiTurnsHarnessTests_Legions(ITestOutputHelper output)
{
    [SkippableTheory]
    // Карта 25 — «Растленная власть» за Легионы, средняя, 55 дней (как legions): И31 (дни 9, 20, 44, 52–53), И32, И41 (KC0086 дней
    // 52–53, GiveItems дня 6), И70. Партия 20.10 (corpus/20261020-replay-legions, 54 дня): первый замер 933/940, 156/181, лишних 85,
    // утро 6, состояния дня 20 → 940/940, 181/181, 0, 0, состояния дня 0 (ветка r20b/leg, разбор 20261020-replay-legions.md): деревья
    // перекрашенных лесов — CRT (И210, 25 шагов найма), выселение без пути гостя (И192, 54 лишних SecureCity нейтрала), отход по кругу
    // (И212) и на дорогу (И190), захват после визитёра (И211), «ещё ходит» фазы 0 (И174, r20b/q09); харнесс — события начала хода в сдвиге R250,
    // ответ человека на мир из журнала (r20b/hum), цель и точка литья у заклинания перед атакой (И173, r20b/q09).
    [InlineData("replay-legions", "port-synth-25-replay-legions-heretic", 50, 940, 940, 181, 181, 0, 0, 0)]
    public void AiTurns_MatchTheOriginalJournal(string run, string stem, int minDays, int wantPicks, int wantPicksSame, int wantSteps,
                                                int wantStepsSame, int wantExtra, int wantMorning, int wantChecks = -1) =>
        DailySaveAiTurns.Check(output, run, stem, minDays, (wantPicks, wantPicksSame, wantSteps, wantStepsSame, wantExtra, wantMorning), wantChecks);
}

/// <summary>Строка карты 26 <see cref="DailySaveAiTurnsHarnessTests"/> — своим классом (см. <see cref="DailySaveAiTurnsHarnessTests_Legions"/>).</summary>
[Trait("Category", "Corpus")]
public sealed class DailySaveAiTurnsHarnessTests_Human(ITestOutputHelper output)
{
    [SkippableTheory]
    // Карта 26 — «Растленная власть» за Кланы, средняя, 46 дней (как human, но без действий человека): И43 (бюджет найма лидера
    // Легионов PL000b дней 35 и 44). Партия 20.10 (20261020-replay-human, 45 дней): первый замер 806/810, шаги 140/163, лишних 27, утро 6;
    // r20b/hum — 810/810, 163/163, лишних 0, утро 0, сверка дня 0 (разбор 20261020-replay-human.md): вариант грунта у перекрашенного
    // леса тратит CRT (И210 — типы найма в гарнизон), захват укрепления после гибели визитёра (И211), отход бежавшего по кругу (И212),
    // ответ человека на союз в ходе ИИ (харнесс, дни 31 и 41). Найм лидера Легионов PL000b — 14/14.
    [InlineData("replay-human", "port-synth-26-replay-human-dwarf", 42, 810, 810, 163, 163, 0, 0)]
    public void AiTurns_MatchTheOriginalJournal(string run, string stem, int minDays, int wantPicks, int wantPicksSame, int wantSteps,
                                                int wantStepsSame, int wantExtra, int wantMorning, int wantChecks = -1) =>
        DailySaveAiTurns.Check(output, run, stem, minDays, (wantPicks, wantPicksSame, wantSteps, wantStepsSame, wantExtra, wantMorning), wantChecks);
}

/// <summary>Та же сверка на синтетических картах (<c>corpus/*-port-synth</c>): карты 21 и 22 — проверка самой сверки на сыгранных
/// партиях (числа — как у <c>Hire_PlayMatchesOriginalJournalAndSaves</c> и <c>Diplo2_AiTurnsMatchTheOriginalJournal</c>); карта 23 — в
/// <c>Long_AiTurnsMatchTheOriginalJournal</c> (тот же <c>SavedAiDays</c> и поиск партии <c>DailyJournals</c>); переигровки карт 5 и 10.</summary>
[Trait("Category", "Synth")]
public sealed class DailySaveAiTurnsHarnessTests_Synth(ITestOutputHelper output)
{
    /// <summary>Синтетические карты: сыгранные (21, 22, 5) и переигровка карты 10 (карта 27). Ждёт партии только строка карты 27.</summary>
    [SkippableTheory]
    // Карта 21 (20.10): дни 1–11 с сохранений t01-009 … t12-020 — выборов 113/113, шагов 101/101, клетки утра — 0 (с начала сценария —
    // те же 113 и 101, Hire_PlayMatchesOriginalJournalAndSaves).
    [InlineData("20261019b-port-synth", "port-synth-21-ai-hire", 11, 113, 113, 101, 101, 0, 0)]
    // Карта 22: дни 1–11 — 172/172, 74/74, утро 0 (Diplo2_AiTurnsMatchTheOriginalJournal; там вход в партию задан seq, здесь — game_phase).
    [InlineData("20261019b-port-synth", "port-synth-22-ai-diplo2", 11, 172, 172, 74, 74, 0, 0)]
    // Карта 5 (20261016e-av, Эльфы, трудная): партия 16.10 уже шла с Q каждый день и только «Конец хода» — переигровка не нужна.
    // Дни 1–15: выборов 385/385 (И21: TakeBag 8/8, AttackStack 40/40, берсерк 20/20 — остаток живого дня AiTacticFromMapHarnessTests
    // здесь не появляется; И31), шагов 89/91; расходится только исход боя дня 10 (S143KC0011 × S143KC0016: у движка защитник бежал
    // на (100,119), у порта погиб — тот же класс, что день 22 карты 23) и его следствия: повышение лидера Нежити дня 12, VisitRuin и
    // AssistMove дня 13 (лишних 3, клеток утра 3). 20.10 (ветка r20/rest): бой дня 10 сошёлся — юниты боя в порядке unitsInfo (Б80); опыт
    // бежавшего с поля лидера S143UN002a — пол 1 (И154), и дневной чит дня 12 поднимает его до UpgradeLeader; руина дня 13 — бой по прибытии
    // с нулём очков хода (И155): шагов 89 → 91, клеток утра 3 → 0. Остаток — лишний шаг Diplomacy PL0003 дня 12 (проход 3): журнал схемы 7.3,
    // у ai_rng нет игрока хода, и положение R250 между боями по счётчику не восстанавливается (сдвиг 28–32 шага у всех выборов).
    [InlineData("20261016e-av", "port-synth-5-ai-hard-elf", 15, 385, 385, 91, 91, 1, 0)]
    // Карта 27 — переигровка карты 10 (Нежить, трудная, день 1 по шагам памятки, дни 2–3 и утро дня 4): И26 (бой KC0003 дня 2, KC0001
    // Легионов дня 3), И25. Партия 20.10 (20261020-replay10): первый замер 72/77, 47/47, лишних 5, утро 4 — номера отрядов дня 2 на один
    // меньше: KC0029 пал в бою ИИ дня 1 (KC0028 × KC0029), а счётчик движка с входа в партию его помнит (И213, харнесс); r20b/hum — 77/77, 47/47, 0, 0.
    [InlineData("replay10", "port-synth-27-replay10-undead", 3, 77, 77, 47, 47, 0, 0)]
    public void AiTurns_MatchTheOriginalJournal(string run, string stem, int minDays, int wantPicks, int wantPicksSame, int wantSteps,
                                                int wantStepsSame, int wantExtra, int wantMorning) =>
        DailySaveAiTurns.Check(output, run, stem, minDays, (wantPicks, wantPicksSame, wantSteps, wantStepsSame, wantExtra, wantMorning));
}

/// <summary>Общая проверка строк <see cref="DailySaveAiTurnsHarnessTests"/>: партия в корпусе, дни, числа (−1 — только печать).</summary>
internal static class DailySaveAiTurns
{
    /// <summary>Сохранений партии в одном журнале — не меньше (день и утро следующего).</summary>
    private const int MinSaves = 2;

    /// <param name="wantChecks">Расхождений состояния дня (<c>DayCheck</c>: бои, R250 у выборов и ходов, земля); −1 — не сверяется.</param>
    public static void Check(ITestOutputHelper output, string run, string stem, int minDays,
                             (int Picks, int PicksSame, int Steps, int StepsSame, int Extra, int Morning) want, int wantChecks = -1)
    {
        var runs = PortSynthMapsTests.DailyJournals(stem, run).ToList();
        Skip.If(runs.Count == 0, $"партии {stem} в каталоге {run} корпуса нет — ждёт партии");
        var t = PortSynthMapsTests.DailyAiTurns(output, stem, runs, MinSaves);
        Assert.True(t.Days >= minDays, $"сверено дней {t.Days}, нужно не меньше {minDays} — партия короче памятки или пропущены сохранения");
        if (want.Picks < 0)
        {
            output.WriteLine($"не закреплено: закрепить первым замером — {t.Picks}, {t.PicksSame}, {t.Steps}, {t.StepsSame}, {t.Extra}, {t.Morning}");
            return;
        }
        Assert.Equal((want.Picks, want.Steps), (t.Picks, t.Steps));   // ответ движка — до сверки, чтобы смена корпуса падала понятно
        Assert.Equal((want.PicksSame, want.StepsSame, want.Extra, want.Morning), (t.PicksSame, t.StepsSame, t.Extra, t.Morning));
        if (wantChecks >= 0) Assert.Equal(wantChecks, t.Checks);
        // Клетки отрядов и гарнизонов ИИ наутро — как у движка: расстановка 0x5d57de с силой юнитов в миг хода (И33; 20.10, r20/last —
        // 0 на всех строках; остаток AiGroupArrangeHarnessTests — сила из сохранения следующего дня, артефакт той сверки).
        Assert.Equal(0, t.Cells);
        // Тип, уровень, здоровье и опыт юнитов ИИ наутро — как у движка (И230; 20.10, r20/xp: было legions 44, human 18, карта 22 — 3,
        // у карты 23 — 114; стало 0 везде): нулевая прибавка боя не зовёт 0x5d7412 (0x665762), в 0x5d7412 идёт прибавка строки xp,
        // ушедший с поля не делит (0x625bd0), тип следующего уровня лидера — созданный в этом запуске (GeneratedImpls).
        Assert.Equal(0, t.UnitStates);
    }
}
