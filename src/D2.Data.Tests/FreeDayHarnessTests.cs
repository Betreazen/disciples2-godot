using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// П. 28 <c>docs/port-status-20261016.md</c> — свободный день порта (<see cref="AiTacticTurnHarnessTests.EndCells_OfTheDay"/>) на других
/// партиях корпуса: сохранение дня N → <see cref="Play.GameSession.EndTurn"/> → где каждый отряд ИИ кончил день у порта и у движка.
/// Партии — отдельными классами (<see cref="CorpusRowAttribute"/>), чтобы шли параллельно.
/// </summary>
public abstract class FreeDayHarnessTests(ITestOutputHelper output)
{
    [SkippableTheory]
    // Измерено 16.10 (без дня, у которого в журнале нет хода ИИ). Первый замер → после двух правил порта: вор у торговца, чей путь кончается
    // рядом с входом, действует в том же исполнении (GameSession.ExecuteThief) и гость без очков хода выходит за ворота при выселении
    // (GameSession.KickOutGuest). Сошлось / в разные клетки: legions 4814 / 46 → 4819 / 38, probe72 2463 / 13 → 2467 / 9, карта 4 —
    // 460 / 56 → 484 / 22 (лёгкая), 544 / 22 → 557 / 6 (очень трудная), 566 / 12 → 564 / 10 (третья, отрядо-дней 584 → 578 — у порта
    // теперь крадут и выселяют), 274 / 12 → 279 / 6 (четвёртая). Разбор остатка — docs/port-status-20261016.md, п. 28.
    // 17.10: VisitRuin, дошедший сегодня до входа, бьёт руину тем же исполнением (GameSession.Execute, 20261016e-av карта 5): legions
    // 4871 / 4819 / 38 → 4869 / 4824 / 31 (отрядо-дней меньше на 2 — отряд гибнет у руины в тот же день, как у движка), probe72 2467 / 9 →
    // 2470 / 6.
    // 18.10: оценка найма юнита — с hire_check на казне сейчас (AiEconomy.HireUnit, 0x44c3ab → 0x5deea7): стратегия порта legions больше не
    // прерывается на неисполнимом найме — 4869 / 4824 → 4870 / 4825 (S073KC008f дня 44 доживает до конца дня и стоит, как у движка).
    // 19.10: набор исключений на входе фазы 3 — после сброса кэша фазой 2 (AiTactics.Choose, 0x4665e5): отряд, вставший на новую клетку
    // (вход в крепость), без действия оценивается заново, как у движка (карта 16, S143KC0005 день 6 — 6 окон и 6 перестановок гарнизона).
    // legions 4825 / 31 → 4827 / 29 (S073KC0001, S073KC007c дня 53); probe72 2477 / 2470 / 6 → 2475 / 2467 / 7 — день 16, S143KC0002: после
    // HealRevive (вход в S143FT000e) порт теперь, как движок, оценивает отряд снова — окна 3 и 12 кандидатов, как у движка (seq 3032672,
    // 3032843), и в окне 12 выбирает AttackStack S143KC0047; движок бьёт его помощниками (KC0095, KC0099 — подмога, статус 2), а порт идёт
    // сам и гибнет в бою (известный остаток И29: AttackStack против AttackStack/Assist). Прежнее «совпадение» было случайным: порт не
    // оценивал отряд после входа в крепость и стоял в ней. По окнам при состоянии движка (AiTacticFromMapHarnessTests) числа probe72 те же.
    // 19.10 (r19/airng): оборвавшее путь событием срабатывание проверки пути эффектов в ход не даёт — событие проверяется после хода заново,
    // со вторым броском шанса R250 (0x440284, EventFiring.CutOnly): legions 4825 → 4824 — у порта лишний NobleStack S073KC0090 → KC008c
    // (85,73). День идёт на собственном R250 порта (не движка), и после каждого обрыва его поток теперь на вызов дальше — дальнейшие броски
    // дня другие; правило тактики тут ни при чём (D2_END_SHOW=ALL: прочие отрядо-дни те же).
    // Обе правки вместе (слияние r19/m16 + r19/airng): legions 4826 / 29, probe72 2475 / 2467 / 7.
    // 19.10 вечер (r19b/m22): смена приказа на L_ASSIST без цели тут же назначает помощников (наблюдатель 0x46726f → 0x45603b), и отряд
    // в тот же ход идёт помощником (AssistMove), как у движка — жезлоносцы карты 4 после сноса последнего чужого жезла: 484 / 22 → 488 / 20,
    // 557 → 560, 564 → 566, 279 / 6 → 285 / 1. legions и probe72 — без изменений.
    // 20.10 (r20/aiA): уход стратегии по юниту (И110), повышение павшего юнита читом опыта (И130), отступление бежавшего защитника (И93):
    // legions 4870 / 4826 / 29 → 4868 / 4831 / 24, покрытие 520 / 7 → 522 / 5.
    // 20.10 (r20/rest): юниты боя в порядке unitsInfo (Б80), опыт бежавшего — пол 1 (И154), руина по прибытии с нулём хода (И155), земля захвата и цепочка жил (И152), помощники павшего (И151), отход после дуэли (И153) — бои свободного дня на своём R250 порта сдвигают день: legions 4868 / 4831 / 24 → 4869 / 4835 / 23, probe72 2475 / 2467 / 7 → 2477 / 2468 / 8, карта 4 516 / 488 / 20 → 519 / 495 / 16,
    // 568 / 560 / 6 → 569 / 565 / 2, 578 / 566 / 10 → 579 / 576 / 1, 288 / 285 / 1 → 289 / 285 / 2; cov 528 / 522 / 5 → 527 / 521 / 5;
    // карта 23 1954 / 1907 / 43 → 1952 / 1911 / 38. Со старыми правилами — прежние числа.
    // 20.10 (r20b/hum): захват укрепления после гибели визитёра (И211), отход бежавшего по кругу (И212), CRT варианта грунта (И210) — legions 4869 / 4835 / 23 →
    // 4870 / 4839 / 20, probe72 2477 / 2468 / 8 → 2475 / 2465 / 10, cov 527 / 521 / 5 → 527 / 520 / 6 (бои дня на своём R250 порта, И38).
    // 20.10 (r20b/q09, поверх r20b/hum): поток CRT дня — движка в миг конца хода человека (JournalCrt.AtHumanEnd; прежде srand(1) каждого дня,
    // тип найма и имена — наугад), призванный в очереди (И172), «ещё ходит» (И174), цель усечённого пути (И175): legions 4870 / 4839 / 20 →
    // 4869 / 4842 / 20, probe72 2475 / 2465 / 10 → 2477 / 2471 / 6, карта 23 1952 / 1911 / 38 → 1951 / 1906 / 42 (поток CRT движка; бои карты
    // 23 на R250 порта); cov 527 / 520 / 6 и карты 4, 10–12, 16, 21 — без изменений.
    // 20.10 (r20b/leg, поверх r20b/q09): выселение с путём гостя (И192), отход на дорогу (И190): legions 4869 / 4842 / 20 → 4869 / 4843 / 19;
    // probe72 и cov — без сдвига.
    [CorpusRow("20260920-legions", "passive-001.jsonl", "", 4869, 4843, 19)]
    [CorpusRow("20260930-probe72", "passive-001.jsonl", "", 2477, 2471, 6)]
    [CorpusRow("20261016-port-synth", "passive-002.jsonl", "port-synth-4", 519, 495, 16)]
    [CorpusRow("20261016b-port-synth", "passive-001.jsonl", "port-synth-4", 569, 565, 2)]
    [CorpusRow("20261016c-port-synth", "passive-001.jsonl", "port-synth-4", 579, 576, 1)]
    [CorpusRow("20261016d-port-synth", "passive-001.jsonl", "port-synth-4", 289, 285, 2)]
    // 17.10 вечер: карты 10–12 (20261017-port-synth, зонд 7.4), первый замер: карты 11 и 12 — все отрядо-дни; карта 10 — в разные клетки 3
    // (AttackParty 2, AttackStack 1; карта пути ИИ сквозь невидимых и засады — не разобрано). Ветка r17c/s1012: AttackParty 2 → 0 — команда
    // ИИ, вставшая перед невидимым, списывает цену до последней достижимой своего пути (StackPath.Charge, 0x5cd0e9), и охотники после засады
    // стоят, как у движка: 57 / 3 → 59 / 1. Остаток (ветка r17c/ai1012) — S143KC0003 дня 2: AttackStack → KC0027, бой, затем у порта HealRevive.
    // Бой порта своим R250 оставил лидеру 114/135, у движка 41/135, и движок выпил лечебное зелье (potion_use seq 4144682, 41 → 135, порог
    // 0,75 — AiStackItems.HealBelow): урона нет, HealRevive нет; у порта 114 > 101 — зелья нет, урон 0,16 ≥ 0,1, HealRevive строится. Бой
    // расходится и с R250 движка (D2_JRNG=battle: здоровье разошлось в 8 боях карты из 13) — накопление состояния, не правило тактики.
    [CorpusRow("20261017-port-synth", "passive-001.jsonl", "port-synth-10", 60, 59, 1)]
    [CorpusRow("20261017-port-synth", "passive-001.jsonl", "port-synth-11", 20, 20, 0)]
    [CorpusRow("20261017-port-synth", "passive-001.jsonl", "port-synth-12", 10, 10, 0)]
    // 19.10: карта 16 (ни одного боя, R250 на ход не влияет) — все 168 отрядо-дней в той же клетке, что у движка.
    [CorpusRow("20261018b-port-synth", "passive-001.jsonl", "port-synth-16", 168, 168, 0)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, дни 1–18): 528 отрядо-дней, 520 в той же клетке, 7 в разные, 1 ходил
    // только порт. С Retreat без поддержки (0x462e0d) — было 518 / 8: KC003d и KC0020 дня 15 бегут к столице, как у движка.
    [CorpusRow("20261019c-cov", "passive-001.jsonl", "greenskins-human", 527, 520, 6)]
    // 19.10 вечер: карта 21 (12 дней без боёв, найм вора, рост деревень, мешки) — все 127 отрядо-дней в той же клетке (r19b/m21).
    [CorpusRow("20261019b-port-synth", "passive-001.jsonl", "port-synth-21", 127, 127, 0)]
    // 20.10 (r20/aiA): карта 23 — первый замер: 1954 отрядо-дня, в той же клетке 1907, в разные 43 (остаток — бои ИИ ↔ ИИ на своём R250
    // порта и их следствия, docs/synthetic-maps-port/analysis/20261020-map23-ai.md).
    [CorpusRow("20261020-port-synth", "passive-001.jsonl", "port-synth-23", 1951, 1906, 42)]
    public void EndCells_OfTheDay(string corpus, string journal, string stem, int wantTotal, int wantSame, int wantApart)
    {
        Skip.IfNot(File.Exists(Path.Combine(GameSaveTests.CorpusDir(), corpus, journal)), "нет корпуса");
        var t = AiTacticTurnHarnessTests.EndCellsOfDays(output, corpus, journal, stem);
        if (wantTotal >= 0) Assert.Equal(wantTotal, t.Total);
        if (wantSame >= 0) Assert.Equal(wantSame, t.Same);
        if (wantApart >= 0) Assert.Equal(wantApart, t.Apart);
    }
}

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class FreeDayHarnessTests_Legions(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20260930-probe72")]
[Trait("Category", "Corpus")]
public sealed class FreeDayHarnessTests_Probe72(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261016-port-synth")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth4a(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261016b-port-synth")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth4b(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261016c-port-synth")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth4c(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261016d-port-synth")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth4d(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261017-port-synth")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth17(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261018b-port-synth")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth18b(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class FreeDayHarnessTests_Cov(ITestOutputHelper output) : FreeDayHarnessTests(output);

[OnlyCorpus("20261019b-port-synth")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth19bHire(ITestOutputHelper output) : FreeDayHarnessTests(output);

/// <summary>Карта 23 <c>port-synth-23-ai-long</c> (ждёт партии).</summary>
[OnlyCorpus("20261020-port-synth", Stem = "port-synth-23")]
[Trait("Category", "Synth")]
public sealed class FreeDayHarnessTests_Synth23Long(ITestOutputHelper output) : FreeDayHarnessTests(output);
