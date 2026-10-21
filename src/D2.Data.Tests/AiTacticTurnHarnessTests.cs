using D2.Data.Corpus;
using System.Text.Json;
using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Ход ИИ целиком с тактикой (срез 5, проверка «г» <c>docs/ai-turn-plan.md</c>): «сохранение дня N →
/// <see cref="GameSession.EndTurn"/>» порта против журнала дня N — где каждый отряд ИИ кончил день (последняя
/// <c>stack_move</c> в окне ходов ИИ дня). Порт пока не атакует (срез 6), а у движка атаки — вторая по частоте цель,
/// поэтому число — измеренная доля, не цель.
/// </summary>
[Trait("Category", "Corpus")]
public class AiTacticTurnHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Измерено 23.09.2026 (партия 20260919-human, без атак): отрядов ИИ-дней 4560. Первая версия исполнения —
    /// клетка сошлась 3871, оба ходили, но в разные клетки 440. После разбора исполнения (путь ИИ с масками зоны
    /// контроля, N действия, статус 5 у перемещения, повтор отряда, пока есть очки хода, запас хода по уровню лидера) —
    /// сошлась 4126 (оба на месте 3888), ходил только порт 41, только движок 113, в разные клетки 280. С атаками отряда
    /// (срез 6 v1, бой автобоем; отряды, погибшие у порта, не считаются) — 4466 отрядо-дней, сошлась 3978 (оба на месте
    /// 3727), только порт 125, только движок 62, в разные клетки 301. Остаток: помощники за лидером (136 + 43), атаки
    /// (кэш целей, план операции не разобран), лечение. С наймом лидеров (список 0): 4465, сошлась 3978, в разные клетки
    /// 304. С дипломатией (срез 4: её броски на общем R250, отношения за нападение): сошлась 3981 (оба на месте 3729),
    /// только порт 121, только движок 58, в разные клетки 305. С оперативными приказами, атакой крепости и силой с
    /// модификаторами юнитов: 4469 отрядо-дней, сошлась 3989 (оба на месте 3731), только порт 121, только движок 59, в разные
    /// клетки 300. С заклинаниями ИИ оперативного слоя (24.09, урон и броски R250): 4466 отрядо-дней (на три меньше — погибли у порта,
    /// под заклинаниями или в сдвинутых боях), сошлась 3988 (оба на месте 3730), только порт 122, только движок 63, в разные клетки 293.
    /// С подготовкой к атаке (24.09, срез 7: заклинание и подмога перед атакой, «напрямую» без поиска пути, итоговая атака
    /// только при не взятом пороге, у человека при p 0,5…0,6 без подготовки — атаки нет): 4475 отрядо-дней (у порта меньше
    /// гибнет), сошлась 3998 (оба на месте 3735), только порт 121, только движок 61, в разные клетки 295; из ходивших у
    /// движка сошлось 263 из 619 (было 258 из 614). С клеткой призыва по разбору <c>0x5edded</c>/<c>0x5e6db2</c>: только движок 59,
    /// в разные клетки 297 (призванные отряды встают иначе и сами ходят). С подготовкой отряда (зелья и снаряжение,
    /// <c>docs/ai-items.md</c>): 4473 отрядо-дня, сошлась 4001 (оба на месте 3733), только порт 121, только движок 61, в разные
    /// клетки 290; из ходивших у движка — 268 из 619. С юнитами гарнизона в отряд и лечением в крепости (§6 там же): сошлась
    /// 4003 (оба на месте 3735), только порт 119, только движок 61, в разные клетки 290 — два отряда, что у порта уходили из
    /// крепости, теперь, как у движка, стоят; из ходивших у движка — те же 268. С полным запасом нанятого лидера и
    /// бесплатным первым шагом из ворот: отрядо-дней 4472 (один отряд у порта теперь гибнет в бою того же дня), сошлась
    /// 4020 (оба на месте 3735, из ходивших у движка 285), только порт 118, в разные клетки 273. Со слабыми атаками только во
    /// втором проходе (<c>docs/ai-attack.md</c> §8): отрядо-дней 4468, сошлась 4047 (оба на месте 3732, из ходивших у движка
    /// 315 из 620), только порт 116, только движок 61, в разные клетки 244. С «напрямую» только у слабой атаки (она ждёт на
    /// месте, <c>StackEmptyMoveAllowance</c>) и готовностью атаки по пути «сегодня вплотную» (§8 там же): отрядо-дней 4496 (у
    /// порта меньше лишних боёв), сошлась 4097 (оба на месте 3768, из ходивших у движка 329 из 621), только порт 107, только
    /// движок 63, в разные клетки 229. С посещением руин (<c>VisitRuin</c>, <c>docs/ai-tactic-candidates.md</c> §8): отрядо-дней
    /// 4501, сошлась 4114 (оба на месте 3774, из ходивших у движка 340 из 621), только порт 106, только движок 63, в разные
    /// клетки 218. С охраной (<c>Guard</c>, §9 там же; стражи нападают на пришельцев): отрядо-дней 4488 (у порта больше гибнет
    /// в боях со стражами), сошлась 4107 (оба на месте 3769, из ходивших у движка 338 из 612), только порт 107, только
    /// движок 54 (было 63), в разные клетки 220. С тренером (<c>VisitTrainer</c>, <c>docs/ai-tactic-candidates.md</c> §10: отряд
    /// идёт к тренеру, обучает юнитов на входе, статус всегда 1): отрядо-дней 4491, сошлась 4131 (оба на месте 3773, из
    /// ходивших у движка 358 из 612), только порт 106, только движок 54, в разные клетки 200. С торговцем и магом (посещение
    /// вплотную — покупки на R250 партии, издалека — перемещение, §10 там же): сошлась 4141 (оба на месте 3772, из ходивших у
    /// движка 369 из 612), только порт 107, только движок 53, в разные клетки 190. Со знатным (<c>NobleStack</c>, <c>NobleCity</c>,
    /// <c>NobleMerchant</c>, передача предметов, разведанные игроки и раскрытие шпионов в начале хода — <c>docs/ai-tactic-candidates.md</c>
    /// §12): отрядо-дней 4486 (у порта гибнут воры, проигравшие бросок), сошлась 4160 (оба на месте 3773, из ходивших у движка 387 из
    /// 608), только порт 105, только движок 30 (было 53), в разные клетки 191. С берсерком, «только по воде» и нейтралами других
    /// подрас в кэше (<c>docs/ai-attack.md</c> §9): отрядо-дней 4482, сошлась 4156 (оба на месте 3770, из ходивших у движка 386 из
    /// 605), только порт 107, только движок 25, в разные клетки 194 (берсерк 4 — счёт и путь водного отряда). С наёмниками и храмом (25.09) —
    /// без изменений (у движка в human их кандидатов нет). С главным врагом (<c>0x465fc9</c>), лидерами-бойцами атакующего (<c>0x466f57</c>)
    /// и «сторожит мешок» по цели (<c>docs/ai-attack.md</c> §10): отрядо-дней 4486, сошлась 4195 (оба на месте 3774, из ходивших у движка 421
    /// из 605), только порт 107, только движок 25, в разные клетки 159. С исключениями кэша по объектам клетки точки и водной проверкой
    /// до поиска пути (<c>0x45f902</c>, <c>0x468f81</c>, <c>docs/ai-attack.md</c> §11): отрядо-дней 4484, сошлась 4223 (оба на месте 3799, из
    /// ходивших у движка 424 из 603), только порт 82 (было 107), только движок 25, в разные клетки 154. С флагом радиуса вместо
    /// «радиус > 0» в повторе без действий и силой списков по выросшим юнитам с модификаторами (§12 там же): отрядо-дней 4485, сошлась
    /// 4275 (оба на месте 3846, из ходивших у движка 429 из 603), только порт 36 (было 82), только движок 26, в разные клетки 148; сила
    /// без флага числа не двигает. «Раненые в крепости» по <c>GAI.HP_STAYMIN</c> (<c>AiTactics.InjuredInside</c>): сошлась 4275 → 4278, в разные
    /// клетки 148 → 145. Счёт лечения ×2 при здоровье ниже <c>GAI.HP_HEALMIN</c> (<c>AiTactics.HealthyForHeal</c>): отрядо-дней 4487, сошлась
    /// 4283 (оба на месте 3847, из ходивших у движка 436 из 604), только порт 36, только движок 25, в разные клетки 143. Порог «напрямую»
    /// (<c>0x450558</c>: видна — 10, под туманом — обзор; <c>GameSession.DirectReach</c>): сошлась 4301 (оба на месте 3863, из ходивших у движка
    /// 438 из 604), только порт 20, только движок 23, в разные клетки 143. A* пути как у движка (float, табличный корень,
    /// куча Dinkumware — <c>docs/map-path.md</c> §1): отрядо-дней 4490, сошлась 4351 (оба на месте 3866, из ходивших у движка 485 из 604), только
    /// порт 20, только движок 24, в разные клетки 95. С памятью движка из журнала, которой нет в <c>.sg</c> (движок партию не перезагружал):
    /// статусы тактики до окна дня (<see cref="Memos"/>, «вчера продвинулся» ×1,25 — <c>S073KC008b</c> день 29 берёт мешок раньше атаки) и
    /// траты стратегии (<see cref="AiSpendMemory"/>, бюджет дня) — отрядо-дней 4491, сошлась 4354 (оба на месте 3864), только порт 21, только
    /// движок 21, в разные клетки 95 (по отдельности: память тактики 4352/97, траты 4353/93; разницу дают броски R250, сдвинутые другими
    /// выборами дня, и бои автобоем). <c>D2_END_SHOW=&lt;тип&gt;</c> печатает расхождения этого типа. Не подгонять.</summary>
    [SkippableFact]
    public void EndCells_OfTheDay()
    {
        var t = EndCellsOfDays(output, "20260919-human", "passive-001.jsonl", "");
        int total = t.Total, same = t.Same, bothMovedApart = t.Apart;
        // 25.09 (сессия «день целиком»): рост земли отвергает клетки ландмарков с GLmark.MOUNTAIN = T (HumanDayHarnessTests): «в разные клетки» 95 → 96; без ландмарков — ровно прежние.
        // Эффекты событий (сессия В, второй круг): L_MOVE_STACK события EV0018 в день 3 ставит эльфов KC000a в 90,23, как движок
        // (сохранение t04-010) — «только движок» 20 → 19, сошлась 4356 → 4357. Без новых эффектов — 4356.
        // 28.09 (сессия «тактика»): отрядо-дней 4489 → 4484 — у порта гибнут в боях подмоги и в сдвинутых бросках R250 (см. ниже).
        // 30.09: знатный не открывает свиток вора, не достигнув цели (GameSession.ExecuteThief), и атака бьёт по прибытии даже с
        // нулём очков хода (ExecuteAttack; human день 37, KC008b × KC0092, seq 10269684): 4484 / 4426 / 49 → 4483 / 4431 / 44 (одно вор —
        // 4488 / 4435 / 44; бои по прибытии сдвигают R250 дня). Со старыми правилами — прежние числа.
        // 11.10 (замечание 18): ходы лекарей после победы в бою по шагам (BattleSession.StartHealers, 0x62689c/0x626d49: новый раунд с
        // бросками, очередь лекарей с ранеными, второй раунд с бросками; прежде сессия доигрывала остаток раунда без бросков) — 4483 /
        // 4433 / 42 → 4481 / 4427 / 46: броски R250 после боёв сдвигают день. На бросках движка (D2_JRNG=battleonly) правило ближе к
        // движку: здоровье после боя как у движка 40 → 42 из 78–79 боёв с тем же началом, сошлась 4434 → 4435, в разные клетки 37 → 35.
        // 13.10: призванный S073KC009e (G000UU0076, создан в день 41) уходит в начале хода хозяина дня 42 (GameSession.ExpireSummons,
        // 0x444631; у движка после ap_restore этого хода строк о нём нет) — отрядо-день, прежде «оба на месте», ушёл: 4481 / 4427 → 4480 /
        // 4426. Без правила — прежние числа.
        // 16.10 (П. 28): день без хода ИИ в журнале не считается — журнал human кончился в ходе человека дня 59, и 38 отрядо-дней
        // последнего сохранения (35 «оба на месте», 3 «ходил только порт») были не сверкой, а отсутствием данных движка: 4480 / 4440 →
        // 4442 / 4405, «только порт» 4 → 1. Правила вора у торговца и выселения гостя без очков хода (FreeDayHarnessTests) human не сдвигают.
        // 16.10 (С2): сила бегства в бою через цепочку модификаторов (AiStrength.Effective; ai_strength движка сходится до знака —
        // 20260919-human [12429316]) — бои дня автобоем решают бегство иначе, один отряд к концу дня у порта не доживает: 4442 / 4405 / 32
        // → 4441 / 4405 / 31. Со старой силой — прежние числа.
        // 20.10 (r20/rest): юниты боя в порядке unitsInfo (Б80), опыт бежавшего (И154), руина по прибытии (И155), земля захвата и жилы (И152) —
        // бои дня на R250 порта иные: 4441 / 4418 / 18 → 4443 / 4421 / 16. Со старыми правилами — прежние числа.
        // 20.10 (r20b/hum): захват укрепления после гибели визитёра (И211), отход бежавшего по кругу соседей (И212), CRT варианта грунта (И210) —
        // бои и захваты дня на R250 порта иные: 4443 / 4421 / 16 → 4442 / 4418 / 18 (свободный день, накопленное состояние — И38; сдвиг
        // дают И211 и И212: с выключенной И210 — те же падения).
        // 20.10 (r20b/q09): CRT движка в миг конца хода человека (JournalCrt.AtHumanEnd; прежде поток порта начинался с srand(1)), картинка
        // дерева роста — бросок CRT (И210), вход в деревню убитого гостя (И211), призванный в очереди (И172), «ещё ходит» (И174), цель
        // усечённого пути (И175): после слияния с r20b/hum (И212 — отход по кругу) 4442 / 4418 / 18 → 4441 / 4417 / 19 — бои дня на R250 порта.
        Assert.Equal(4441, total);
        // Слияние второго круга В с мешками ИИ: 4488 / 4378 / 78 (эффект L_MOVE_STACK даёт тот же +1).
        // Бой ИИ (26.09, AiBattleTests): первый раунд боя с карты — 1 с двумя построениями очереди, как у движка (было 2 —
        // MapBattle ставил Round = 1): 4490 / 4383 / 76; предел BAT_ROUND и конец боя (превращения) — без сдвига; боевые
        // предметы лидера в бою и их расход: 4490 / 4386 / 74, «только порт» 13 → 12. Со старыми правилами — прежние числа.
        // Выселение гостя из своей крепости (AiTactics.KickOut, 0x45a699; human день 30: KC0085 выселяет KC008f и входит лечиться):
        // 4490 / 4402 / 63, «ходил только движок» 18 → 13; без выселения — прежние числа. Встал перед мешком на пути — берёт его с
        // соседней клетки (AiMove, 0x43fbac → 0x4408cb → CEffectTakeBag; human день 10, KC0076 и BG0027): 4403 / 62, без — прежние;
        // правило «мешок в первой клетке шага — насквозь» остаётся: без него пути 619 → 616 и 726 → 725, итог 4403 → 4398.
        // Бегство к столице (Retreat, оценщик 0x462cda: человек из чужих сильнейших ближе 7, сила его атаки на нас ≥ 0,8; human дни 40–41
        // KC0082 и KC0098 бегут к FT0003): 4489 / 4407 / 57 (отрядо-дней на один меньше — одного отряда к концу дня у порта нет); без бегства — прежние.
        // 28.09 (листинг, docs/map-path.md §8, поправка): мешок в любой клетке шага обрывает путь, мешок в первой клетке и мешок-цель
        // берут эффекты хода (GameSession.AiBagAtFirstStep, подбор в конце пути); помощник к лидеру в крепости — к её входу
        // (AiTactics.MoveMode): 4407 → 4411, в разные клетки 57 → 53 (Assist +1, мешок-цель +3; одно правило первой клетки — прежние
        // 4407). EnterCityRoam, SummonAttack, MoveToStack (перенесены 28.09) числа не сдвинули.
        // 28.09 (сессия «тактика», каждое правило переключателем проверено — со старым прежние числа):
        // цель пути ИИ, куда можно шагнуть, «обязана быть проходимой» и с масками (PathFinder.Find, 0x5cca55/0x5cc9a2/0x6805b0): помощник
        // не идёт к точке в зоне контроля врага (дни 14, 17, 24, 48) — 4411 → 4423, «только движок» 13 → 11, «только порт» 12 → 7, в разные
        // клетки 53 → 49; ключ памяти подмоги — помощник (AiTacticOption.KeyStack) и помощник вплотную бьёт или шпионит (ExecutePrepared;
        // 16 из 16 таких исполнений в журналах) — 4423 → 4421, «только движок» 11 → 9, отрядо-дней 4490 → 4488 (исходы дуэли вора и
        // боя помощника — R250); земля жезла при постановке (GameSession.PaintRodLand, 0x437606) — 4422, «только движок» 8; с действием
        // выбор не повторяется из-за выросших исключений (AiTactics.Choose, 0x466522) — 4424, «только порт» 5; ходы хода человека не
        // идут в день ИИ (EndCells: KC0048 день 46 — событие) — 4426, «только движок» 7; раскрытые шпионы — по сохранению следующего
        // дня (Unspied, дни 30, 35, 52) — «только движок» 4, отрядо-дней 4484 (R250 дня сдвигается). Остаток «только движок» 4: дуэль
        // KC0083 день 20 (у движка вор гибнет), бой KC008b × KC0091 день 35 (у порта KC0091 гибнет — KC0011 некого сторожить), заклинание
        // перед атакой KC0088 день 38 (призыв у движка — иное заклинание), KC00ac день 58 (человек принял сделку — золото на торговца).
        // 30.09 (разбор, docs/ai-tactic-candidates.md §26; числа прежние): D2_END_SHOW=ALL печатает каждое расхождение с результатами
        // тактики движка и строками порта. D2_JRNG — генератор движка из журнала (JournalRng: rng_seed + счётчик вызовов, сдвиг по строкам
        // rng): «battleonly» — бои дня на бросках движка (76 боёв, начало как у движка 70, итог 35; сошлась 4432, в разные клетки 42,
        // только порт 10 — бой ИИ порта выбирает цели иначе, не взято), «player» — генератор в начале хода каждого игрока (броски хода
        // сошлись в 130 из 280 ходов; 4425).
        // 30.09 (path_expand 7.2): g′ A* с одним округлением до float (PathFinder, 0x680865) — сошлось 4431 → 4433, в разные клетки
        // 44 → 42 (равноценные пути KC0085 день 25, KC0091 день 40 — прямая колонка, как у движка). Со старым сложением — прежние.
        // 14.10: зелье воскрешения лидеру возвращает отряду «лидер жив» (GameSession.DrinkPotion; прежде признак оставался снятым, и
        // тактика до конца дня и дальше видела отряд без лидера — без атак, руин, торговцев): 4426 → 4440, в разные клетки 46 → 32. Без
        // правила — прежние числа. Бесплатные постройки ИИ дня 1 (GameSession.Build) чисел не сдвигают: казна здесь — из журнала.
        // 17.10: VisitRuin у входа — бой тем же исполнением (GameSession.Execute): 4405 / 31 → 4408 / 28.
        // 19.10: набор исключений на входе фазы 3 — после сброса кэша фазой 2 (AiTactics.Choose, 0x4665e5): отряд на новой клетке
        // оценивается заново, как у движка (карта 16, S143KC0005 день 6) — 4408 / 28 → 4409 / 27 (S073KC0098 день 45).
        // 19.10 вечер (r19b/m22): снос чужого жезла за концом пути — тем же исполнением перемещения (GameSession.Execute; карта 22, RD):
        // 4409 / 27 → 4411 / 25. 19.10 (r19b/cov): Retreat — сила угрозы без поддержки (0x462e0d): 4409 / 27 → 4415 / 21.
        // Обе правки вместе (слияние r19b/m22 + r19b/cov): 4417 / 19.
        // 20.10 (r20/aiA): уход стратегии по юниту (И110), повышение павшего юнита читом опыта (И130), отступление бежавшего защитника (И93): 4417 / 19 → 4418 / 18.
        Assert.Equal(4417, same);   // 16.10: без последнего дня (было 4440; 14.10 — зелье воскрешения лидера, 4426 до него)
        // После слияния с памятью движка: 4491 / 4354 / 96 (ландмарки дают тот же +1). События сценария из сохранения (EventLoader):
        // 4490 / 4354 / 95; упреждающая проверка пути событиями (GameSession.PathEvents, 0x440284: KC0002 день 8 встаёт в 7,74 — зона
        // LO0007): 4490 / 4356 / 93. Без проверки — 4354/95, без событий — прежние числа.
        // MoveStack снимает гостя укрепления (отряд ИИ, ушедший из города, оставался его гостем — MapSite.StackId): 4368 / 87,
        // «ходил только порт» 21 → 15; со старым правилом — прежние числа. Мешок в первой клетке шага ИИ не останавливает — отряд
        // наступает, подбирает и идёт дальше; мешок-цель непроходим по нулевым маскам (PathFinder): 4488 / 4377 / 78.
        Assert.Equal(19, bothMovedApart);   // 20.10 (r20b/q09, со слиянием r20b/hum): 18 → 19; 20.10: И110/И130/И93 (было 19); 19.10: снос жезла (27 → 25) и Retreat без поддержки (27 → 21), вместе — 19; 19.10: повтор оценки на новой клетке (было 28); 17.10: VisitRuin у входа (было 31); 16.10: сила бегства с цепочкой (было 32; 14.10 — зелье воскрешения лидера, 46)
    }

    /// <summary>Итог «свободного дня» по партии: отрядо-дней ИИ, клетка сошлась (оба на месте), ходил только порт, только движок, оба —
    /// в разные клетки.</summary>
    internal sealed record EndCellsTally(int Total, int Same, int BothStay, int PortOnly, int JournalOnly, int Apart);

    /// <summary>Генераторы свободного дня: R250 движка (диагностика <c>D2_JRNG</c>) или порта, CRT движка; ничего — null.</summary>
    private static RandomStreams? Streams(D2.Rules.Rng.EngineRng? r250, uint? crt)
    {
        if (r250 is null && crt is null) return null;
        var g = r250 ?? new D2.Rules.Rng.EngineRng();
        return new RandomStreams(g.Index, g.TableSnapshot(), crt ?? 1);
    }

    /// <summary>«Сохранение дня N → <see cref="GameSession.EndTurn"/>» по всем дням партии <paramref name="corpus"/> против клеток
    /// конца дня в журнале (<see cref="EndCells_OfTheDay"/>). <paramref name="stem"/> — начало имени сохранений одной карты (в журнале
    /// нескольких карт строки берутся только из загрузок этой карты, <see cref="MapSeqs"/>).</summary>
    internal static EndCellsTally EndCellsOfDays(ITestOutputHelper output, string corpus, string journalName, string stem)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        string journal = Path.Combine(dir, journalName);
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var inMap = MapSeqs(journal, stem);
        var lines = Lines(journal).Where(e => inMap(e.GetProperty("seq").GetInt64())).ToList();
        var ends = EndCells(lines);
        var memos = Memos(lines);
        var spends = AiSpendMemory.From(lines);
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var why = new Dictionary<string, int>();
        int total = 0, same = 0, bothStay = 0, portOnly = 0, journalOnly = 0, bothMovedApart = 0;
        var saves = Directory.GetFiles(dir, $"{stem}*.sg").Order(StringComparer.Ordinal)
                             .GroupBy(f => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value)
                             .ToDictionary(g => int.Parse(g.Key, System.Globalization.CultureInfo.InvariantCulture), g => g.Last());
        var spiedToday = SpiedToday(lines);
        string jrng = Environment.GetEnvironmentVariable("D2_JRNG") ?? "off";
        // Генератор движка нужен только диагностике D2_JRNG — без неё строки rng журнала не разбираются.
        var rngLines = jrng == "off" ? []
            : JournalCache.Read(journal, ["rng_seed", "rng", "ai_turn", "battle_start", "battle_end"]).Where(e => inMap(e.Seq)).OrderBy(e => e.Seq).ToList();
        var engineBattles = EngineBattles(rngLines);
        var engineRng = new JournalRng(rngLines);
        var dayRng = DayRngStarts(rngLines, jrng);
        // CRT движка в миг конца хода человека (тип найма в гарнизон, имена, картинки деревьев роста — И210): без него поток порта
        // начинался с srand(1) каждого дня, и любой новый бросок двигал наймы свободного дня наугад.
        var crt = JournalCrt.Of(journal);
        var playerRng = PlayerRngTurns(rngLines);
        var rngTally = new SortedDictionary<string, int>();
        // Дни, у которых в журнале есть ход ИИ: у последнего сохранения партии его часто нет (журнал кончился в ходе человека — 16.10:
        // 20261016d день 6, 20261016c день 11, probe72 день 20), и «ходил только порт» там — не расхождение, а нет данных движка.
        var aiDays = lines.Where(e => e.GetProperty("ev").GetString() == "ai_turn" && e.GetProperty("phase").GetString() == "begin"
                                      && e.GetProperty("cause").GetString() == "ai")
                          .Select(e => e.GetProperty("turn").GetInt32()).ToHashSet();
        foreach (string sg in saves.OrderBy(p => p.Key).Where(p => aiDays.Contains(p.Key)).Select(p => p.Value))
        {
            var looks = Unspied(MapLooksLoader.Load(sg), saves, spiedToday);
            var map = looks.Map;
            var human = map.Players.Values.First(p => p.IsHuman);
            var info = new GameInfo("PtC", "PtC", [human.Id], map.Limits?.DifficultyGame ?? 1, map.Limits?.SuggestedLevel ?? 1);
            int day = map.Turn;
            var events = EventLoader.Load(sg);
            var session = new GameSession(new GameState(info, looks)
            {
                Tactic = memos.GetValueOrDefault(day, []),
                Random = Streams(dayRng.TryGetValue(day, out var at) ? engineRng.At(at.Seq, at.Counter) : null, crt.AtHumanEnd(day, inMap)),
                Ai = spends.Before(map, day),
                Events = events,
                EventProgress = EventProgress.From(events),
            }, content.MapArt, book);
            var sync = jrng is "player" or "battle" or "battleonly" ? new EngineRngSync(session, engineRng, jrng == "battleonly" ? new Dictionary<string, (long, long, long)>() : playerRng, day, rngTally)
            {
                Battles = jrng is "battle" or "battleonly" ? [.. engineBattles.GetValueOrDefault(day) ?? []] : [],
            } : null;
            if (sync is not null) session.Watch(sync);
            var report = session.EndTurn();
            sync?.Close();
            foreach (var before in map.Stacks.Values.Where(s => !s.Owner.Equals(human.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (!session.Map.Stacks.TryGetValue(before.Id, out var after)) continue;
                var start = (before.X, before.Y);
                var port = (after.X, after.Y);
                var logged = ends.GetValueOrDefault((before.Id.ToUpperInvariant(), day), start);
                total++;
                bool portMoved = port != start, journalMoved = logged != start;
                if (port == logged) { same++; if (!portMoved) bothStay++; }
                else if (portMoved && !journalMoved)
                {
                    portOnly++;
                    var first = report.Events.FirstOrDefault(r => r.StartsWith($"тактика {before.Id} ", StringComparison.OrdinalIgnoreCase));
                    Count(why, $"только порт: {first?.Split(' ')[2]}");
                    if (Environment.GetEnvironmentVariable("D2_END_SHOW") is { } showP && first?.Split(' ')[2] == showP)
                        output.WriteLine($"только порт {before.Id} день {day}: старт {start} порт {port}; {string.Join(" | ", report.Events.Where(r => r.Contains(before.Id, StringComparison.OrdinalIgnoreCase)).Take(4))}");
                    if (Environment.GetEnvironmentVariable("D2_END_SHOW") == first?.Split(' ')[2])
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(first ?? "", @"\((\d+),(\d+)\)");
                        var s0 = new GameSession(new GameState(info, looks), content.MapArt, book);
                        var pt = m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
                        var ow = s0.Map.Players[before.Owner];
                        output.WriteLine($"  N {s0.DirectReach(before, pt)} обзор {s0.ScoutRange(before)} видна {!s0.Map.Fog[ow.FogId][pt.Item1, pt.Item2]} чеб {Math.Max(Math.Abs(pt.Item1 - start.X), Math.Abs(pt.Item2 - start.Y))} лидер {s0.Map.Units[before.LeaderId].TypeId} ур {s0.Map.Units[before.LeaderId].Level} мод [{string.Join(",", s0.Map.Units[before.LeaderId].Modifiers)}] снар [{string.Join(",", before.Equipment)}] база {book.ScoutByType.GetValueOrDefault(s0.Map.Units[before.LeaderId].TypeId)}");
                    }
                }
                else if (portMoved && journalMoved && port != logged)
                {
                    var first = report.Events.FirstOrDefault(r => r.StartsWith($"тактика {before.Id} ", StringComparison.OrdinalIgnoreCase));
                    Count(why, $"разные клетки: {first?.Split(' ')[2]}");
                    if (Environment.GetEnvironmentVariable("D2_END_SHOW") is { } show && first?.Split(' ')[2] == show)
                    {
                        string lead = AiTactics.OrderOf(before).Target;
                        var lp = session.Map.Stacks.TryGetValue(lead, out var ls) ? (ls.X, ls.Y) : (-1, -1);
                        var lj = map.Stacks.TryGetValue(lead, out var lb) ? ends.GetValueOrDefault((lead.ToUpperInvariant(), day), (lb.X, lb.Y)) : (-1, -1);
                        Count(why, $"  {show}: лидер {(lp == lj ? "сошёлся" : "разошёлся")}");
                        if (lp == lj || show != "AssistMove") output.WriteLine($"{show} {before.Id} день {day}: старт {start} порт {port} журнал {logged}; лидер {lead} {lp}; {first}");
                    }
                    bothMovedApart++;
                }
                else if (!portMoved && journalMoved)
                {
                    journalOnly++;
                    // D2_END_SHOW=ENGINE — «ходил только движок»: результаты тактики и шаги движка за день и строки отчёта порта.
                    if (Environment.GetEnvironmentVariable("D2_END_SHOW") == "ENGINE")
                    {
                        output.WriteLine($"только движок {before.Id} день {day}: старт {start} журнал {logged}");
                        foreach (var r in DayResults(lines, day, before.Id)) output.WriteLine($"   движок: {r}");
                        foreach (var r in report.Events.Where(r => r.Contains(before.Id, StringComparison.OrdinalIgnoreCase)).Take(30)) output.WriteLine($"   порт: {r}");
                    }
                }
                else bothMovedApart++;
                if (Environment.GetEnvironmentVariable("D2_END_SHOW") == "ALL" && port != logged)
                {
                    string kind = portMoved && !journalMoved ? "только-порт" : !portMoved ? "только-движок" : "разные";
                    output.WriteLine($"РАСХ {kind} {before.Id} день {day}: старт {start} порт {port} журнал {logged}");
                    foreach (var r in DayResults(lines, day, before.Id).Where(r => r.Contains("result"))) output.WriteLine($"   движок: {r}");
                    foreach (var r in report.Events.Where(r => r.StartsWith($"тактика {before.Id} ", StringComparison.OrdinalIgnoreCase)).Take(12)) output.WriteLine($"   порт: {r}");
                }
            }
        }
        foreach (var (k, n) in why.OrderByDescending(p => p.Value)) output.WriteLine($"{k}: {n}");
        foreach (var (k, n) in rngTally) output.WriteLine($"R250 движка: {k}: {n}");
        output.WriteLine($"отрядов ИИ-дней {total}: клетка сошлась {same} (оба на месте {bothStay}); " +
                         $"ходил только порт {portOnly}, только движок {journalOnly}, оба, но в разные клетки {bothMovedApart}");
        return new EndCellsTally(total, same, bothStay, portOnly, journalOnly, bothMovedApart);
    }

    /// <summary>Строки одной карты в журнале нескольких карт: загрузки (<see cref="CheckJournal"/>, смена игрового потока), в которых сняты
    /// сохранения <paramref name="stem"/>*. Пустое начало имени — весь журнал.</summary>
    private static Func<long, bool> MapSeqs(string journal, string stem)
    {
        if (stem.Length == 0) return _ => true;
        var j = CheckJournal.Load(journal);
        var epochs = j.Saves.Where(s => Path.GetFileName(s.Path).StartsWith(stem, StringComparison.OrdinalIgnoreCase)).Select(s => s.Epoch).ToHashSet();
        var ranges = epochs.Select(j.EpochSeqs).ToList();
        return seq => ranges.Any(r => seq >= r.From && seq <= r.To);
    }

    /// <summary>
    /// Раскрытые шпионы дня (<c>0x44430f</c>, бросок R250 в начале хода игрока — значений журнал не пишет): запись разведки, которой нет
    /// в сохранении следующего дня и которую игрок в этот день не повторил (<c>noble_action</c> «шпионаж»), снимается до хода — как
    /// <see cref="AiLiveDay.LoseSpied"/> у харнесса кандидатов. Бросок порта по оставшимся записям остаётся (human, дни 30, 35, 52: у движка
    /// раскрыт шпион, и воры <c>KC007c</c>, <c>KC008e</c>, <c>KC009d</c> идут шпионить снова).
    /// </summary>
    private static MapLooks Unspied(MapLooks looks, IReadOnlyDictionary<int, string> saves, IReadOnlyDictionary<int, Dictionary<string, HashSet<string>>> spiedToday)
    {
        var map = looks.Map;
        if (AiTacticFromMapHarnessTests.SpiedOf(saves, map.Turn + 1) is not { } next) return looks;
        var again = spiedToday.GetValueOrDefault(map.Turn);
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var p in map.Players.Values.Where(p => !p.IsHuman))
        {
            var kept = p.Spied.Where(x => next.TryGetValue(p.Id, out var n) && n.Contains(x, StringComparer.OrdinalIgnoreCase)
                                          && !(again?.GetValueOrDefault(p.Id)?.Contains(x) ?? false)).ToList();
            if (kept.Count == p.Spied.Count) continue;
            players[p.Id] = p with { Spied = kept };
            changed = true;
        }
        return changed ? GameSession.Retold(looks, GameSession.With(map, players: players), looks.Stacks) : looks;
    }

    /// <summary>Кого игрок разведал в день (<c>noble_action</c> «шпионаж», поле <c>id</c> — разведанный игрок).</summary>
    private static Dictionary<int, Dictionary<string, HashSet<string>>> SpiedToday(IReadOnlyList<JsonElement> lines)
    {
        var byDay = new Dictionary<int, Dictionary<string, HashSet<string>>>();
        int day = 0;
        foreach (var e in lines)
        {
            string ev = e.GetProperty("ev").GetString()!;
            if (ev == "ai_turn" && e.GetProperty("phase").GetString() == "begin") day = e.GetProperty("turn").GetInt32();
            if (ev != "noble_action" || e.GetProperty("action").GetInt32() != (int)ThiefAction.Spy
                || e.GetProperty("id").GetString() is not { Length: > 0 } id) continue;
            if (!byDay.TryGetValue(day, out var players)) byDay[day] = players = new(StringComparer.OrdinalIgnoreCase);
            string who = e.GetProperty("player").GetString()!;
            if (!players.TryGetValue(who, out var ids)) players[who] = ids = new(StringComparer.OrdinalIgnoreCase);
            ids.Add(id);
        }
        return byDay;
    }

    /// <summary>Результаты тактики и шаги отряда в окне ходов ИИ дня — для <c>D2_END_SHOW=ENGINE</c>.</summary>
    private static IEnumerable<string> DayResults(IReadOnlyList<JsonElement> lines, int day, string stack)
    {
        long from = -1, to = long.MaxValue; int last = -1;
        foreach (var e in lines)
            if (e.GetProperty("ev").GetString() == "ai_turn" && e.GetProperty("phase").GetString() == "begin" && e.GetProperty("cause").GetString() == "ai")
            {
                int t = e.GetProperty("turn").GetInt32();
                if (t != last && t == day) from = e.GetProperty("seq").GetInt64();
                if (t != last && t == day + 1) to = e.GetProperty("seq").GetInt64();
                last = t;
            }
        foreach (var e in lines)
        {
            long q = e.GetProperty("seq").GetInt64();
            if (q < from || q >= to) continue;
            string ev = e.GetProperty("ev").GetString()!;
            if (ev == "ai_tactic_result" && string.Equals(e.GetProperty("stack").GetString(), stack, StringComparison.OrdinalIgnoreCase))
                yield return $"{q} result {e.GetProperty("target")} {e.GetProperty("point")} st {e.GetProperty("status")}";
            if (ev == "stack_move" && string.Equals(e.GetProperty("stack").GetString(), stack, StringComparison.OrdinalIgnoreCase))
                yield return $"{q} move {e.GetProperty("x")},{e.GetProperty("y")} left {e.GetProperty("move_left")}";
        }
    }

    /// <summary>Счётчик R250 движка в начале дня ИИ (последний повтор дня): <paramref name="variant"/> «human» — строка конца хода
    /// человека перед первым ходом ИИ дня, «ai» — начало первого хода ИИ, «off» — без генератора движка.</summary>
    private static Dictionary<int, (long Seq, long Counter)> DayRngStarts(IEnumerable<CorpusEvent> events, string variant)
    {
        var starts = new Dictionary<int, (long, long)>();
        if (variant == "off") return starts;
        AiTurnEvent? prev = null;
        foreach (var t in events.OfType<AiTurnEvent>())
        {
            if (t.Phase == "begin" && t.Cause == "ai" && (prev is null || prev.Cause == "human" || prev.Turn != t.Turn))
                starts[(int)t.Turn] = variant == "human" && prev is { Cause: "human", Phase: "end" } ? (prev.Seq, prev.RngBefore) : (t.Seq, t.RngBefore);
            prev = t;
        }
        return starts;
    }

    /// <summary>Бои ИИ-дней движка (последний повтор дня — от начала его первого хода ИИ до начала следующего дня): атакующий, цель,
    /// строки <c>battle_start</c> первого раунда и <c>battle_end</c>.</summary>
    private static Dictionary<int, List<(string Attacker, string Defender, long Seq, long End, IReadOnlyList<UnitState> After, IReadOnlyList<UnitState> Before)>> EngineBattles(IReadOnlyList<CorpusEvent> events)
    {
        var starts = DayRngStarts(events, "ai");
        var byDay = new Dictionary<int, List<(string, string, long, long, IReadOnlyList<UnitState>, IReadOnlyList<UnitState>)>>();
        var days = starts.OrderBy(p => p.Value.Seq).ToList();
        for (int k = 0; k < days.Count; k++)
        {
            long from = days[k].Value.Seq, to = k + 1 < days.Count ? days[k + 1].Value.Seq : long.MaxValue;
            var list = new List<(string, string, long, long, IReadOnlyList<UnitState>, IReadOnlyList<UnitState>)>();
            BattleStartEvent? open = null;
            foreach (var e in events)
            {
                if (e.Seq < from || e.Seq >= to) continue;
                if (e is BattleStartEvent { Round: 0 } bs) open = bs;
                else if (e is BattleEndEvent be && open is not null)
                {
                    list.Add((open.AttackerGroup, open.DefenderGroup, open.Seq, be.Seq, be.Units, open.Units));
                    open = null;
                }
            }
            byDay[days[k].Key] = list;
        }
        return byDay;
    }

    /// <summary>Счётчики R250 хода каждого игрока ИИ (последний повтор дня): строка начала хода и вызовы генератора до конца хода.</summary>
    private static Dictionary<string, (long Seq, long Before, long After)> PlayerRngTurns(IEnumerable<CorpusEvent> events)
    {
        var turns = new Dictionary<string, (long, long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in events.OfType<AiTurnEvent>().Where(t => t.Cause == "ai" && t.Player is not null))
        {
            string key = $"{t.Turn} {t.Player}";
            if (t.Phase == "begin") turns[key] = (t.Seq, t.RngBefore, -1);
            else if (t.Phase == "end" && turns.TryGetValue(key, out var b)) turns[key] = (b.Item1, b.Item2, t.RngAfter);
        }
        return turns;
    }

    /// <summary>Ставит генератор партии в состояние движка в начале хода каждого игрока ИИ (<see cref="JournalRng"/>) и считает, сошлось
    /// ли число бросков хода порта с движком.</summary>
    private sealed class EngineRngSync(GameSession session, JournalRng rng, IReadOnlyDictionary<string, (long Seq, long Before, long After)> turns,
                                       int day, IDictionary<string, int> tally) : ITurnObserver
    {
        private string? _current;
        private int _stepsAt;
        private long _engineDraws = -1;

        private IReadOnlyList<UnitState>? _pending;
        private bool _startSame;

        /// <summary>Бой с генератором движка сыгран: здоровье его юнитов у порта против <c>battle_end</c> движка.</summary>
        private void Settle()
        {
            if (_pending is not { } after) return;
            _pending = null;
            bool same = after.All(u => (session.Map.Units.TryGetValue(u.Id, out var unit) ? Math.Max(unit.Hp, 0) : 0) == Math.Max(u.Hp, 0));
            Count(tally, (same ? "бой: здоровье как у движка" : "бой: здоровье разошлось") + (_startSame ? " (начало то же)" : ""));
        }

        public void PlayerTurn(string playerId)
        {
            Settle();
            Close();
            _current = playerId;
            _engineDraws = -1;
            if (turns.TryGetValue($"{day} {playerId}", out var t) && rng.At(t.Seq, t.Before) is { } g)
            {
                session.Rng.SetState(g.TableSnapshot(), g.Index);
                _engineDraws = t.After >= 0 ? t.After - t.Before : -1;
                Count(tally, "ход поставлен");
            }
            else Count(tally, "ход без состояния движка");
            _stepsAt = session.Rng.Steps;
        }

        public void Close()
        {
            Settle();
            if (_current is null) return;
            _current = null;
            if (_engineDraws < 0) return;
            int port = session.Rng.Steps - _stepsAt;
            Count(tally, port == _engineDraws ? "бросков хода столько же" : port < _engineDraws ? "бросков хода у порта меньше" : "бросков хода у порта больше");
        }

        public void StackMoving(StackMove move) => Settle();

        /// <summary>Бои дня у движка (последний повтор): атакующий, цель, строка начала и конца.</summary>
        public List<(string Attacker, string Defender, long Seq, long End, IReadOnlyList<UnitState> After, IReadOnlyList<UnitState> Before)> Battles { get; init; } = [];

        public IReadOnlyList<D2.Rules.Battle.TurnRecord>? Battle(D2.Rules.Battle.BattleState state)
        {
            Settle();
            int i = Battles.FindIndex(b => b.Attacker.Equals(state.AttackerGroup, StringComparison.OrdinalIgnoreCase)
                                           && b.Defender.Equals(state.DefenderGroup, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                if (Battles.Count > 0 || turns.Count > 0) Count(tally, "бой без пары у движка");
                return null;
            }
            var b = Battles[i];
            Battles.RemoveAt(i);
            _pending = b.After;
            var mine = state.Units.ToDictionary(u => u.Id, u => u.Hp, StringComparer.OrdinalIgnoreCase);
            bool startSame = b.Before.Count == mine.Count && b.Before.All(u => mine.TryGetValue(u.Id, out int hp) && hp == u.Hp);
            Count(tally, startSame ? "бой: начало как у движка" : "бой: начало разошлось");
            _startSame = startSame;
            if (rng.BeforeDraws(b.Seq, b.End) is { } g)
            {
                session.Rng.SetState(g.TableSnapshot(), g.Index);
                Count(tally, "бой: генератор движка");
            }
            else Count(tally, "бой: бросков нет");
            return null;
        }
    }

    private static void Count(IDictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

    /// <summary>Память тактики движка на начало ходов ИИ дня N (<c>ai_tactic_result</c> до окна дня, последняя запись на тройку
    /// отряд — цель — точка, <see cref="AiTacticMemo"/>): в <c>.sg</c> её нет, а движок партию не перезагружал (кроме дня 23), и
    /// «вчера продвинулся» ×1,25 у него есть. Окно дня — последнее из повторов.</summary>
    private static Dictionary<int, IReadOnlyList<AiTacticRecord>> Memos(IReadOnlyList<JsonElement> lines)
    {
        var starts = new Dictionary<int, long>();
        int last = -1;
        foreach (var e in lines)
            if (e.GetProperty("ev").GetString() == "ai_turn" && e.GetProperty("phase").GetString() == "begin"
                && e.GetProperty("cause").GetString() == "ai")
            {
                int turn = e.GetProperty("turn").GetInt32();
                if (turn != last) starts[turn] = e.GetProperty("seq").GetInt64();
                last = turn;
            }
        var results = lines.Where(e => e.GetProperty("ev").GetString() == "ai_tactic_result"
                                       && e.GetProperty("stack").ValueKind == JsonValueKind.String
                                       && e.GetProperty("point").ValueKind == JsonValueKind.Object
                                       && e.GetProperty("turn").ValueKind == JsonValueKind.Number)
                           .ToList();
        return starts.ToDictionary(p => p.Key, p => (IReadOnlyList<AiTacticRecord>)[.. results
            .Where(e => e.GetProperty("seq").GetInt64() < p.Value)
            .Aggregate(AiTacticMemo.Empty, (m, e) => m.With(e.GetProperty("stack").GetString()!,
                e.GetProperty("target").GetString() ?? "", e.GetProperty("point").GetProperty("x").GetInt32(),
                e.GetProperty("point").GetProperty("y").GetInt32(), e.GetProperty("turn").GetInt32(), e.GetProperty("status").GetInt32()))
            .Records]);
    }

    /// <summary>Строки журнала, нужные харнессу (ходы, окна ИИ, результаты тактики, траты стратегии), по порядку <c>seq</c> — один
    /// разбор на все выборки.</summary>
    private static List<JsonElement> Lines(string journal)
    {
        var lines = new List<JsonElement>();
        foreach (string line in File.ReadLines(journal))
        {
            if (!line.Contains("\"ev\":\"stack_move\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"ai_turn\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"ai_tactic_result\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"ai_budget_spend\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"noble_action\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            lines.Add(doc.RootElement.Clone());
        }
        return [.. lines.OrderBy(e => e.GetProperty("seq").GetInt64())];
    }

    /// <summary>Последняя клетка каждого отряда в окне ходов ИИ дня: от <c>ai_turn begin</c> дня N до первого
    /// <c>ai_turn begin</c> дня N + 1.</summary>
    private static Dictionary<(string Stack, int Day), (int X, int Y)> EndCells(IReadOnlyList<JsonElement> lines)
    {
        var result = new Dictionary<(string, int), (int, int)>();
        int day = -1;
        // Ходы после конца хода ИИ ждут следующей строки ai_turn: начало хода ИИ того же дня — они его (начало хода игрока), конец
        // хода человека — это ход человека следующего дня (события сценария двигают отряды ИИ и в нём: human, день 46 — KC0048), его
        // EndTurn порта не играет. Прежде такие ходы шли в день ИИ.
        var pending = new List<(string Stack, int X, int Y)>();
        int pendingDay = -1;
        foreach (var e in lines)
        {
            string ev = e.GetProperty("ev").GetString()!;
            if (ev is "ai_tactic_result" or "ai_budget_spend" or "noble_action") continue;
            if (ev == "ai_turn")
            {
                string phase = e.GetProperty("phase").GetString()!, cause = e.GetProperty("cause").GetString()!;
                if (phase == "begin" && cause == "ai")
                {
                    int next = e.GetProperty("turn").GetInt32();
                    if (next == pendingDay) foreach (var (st, x, y) in pending) result[(st, next)] = (x, y);
                    pending.Clear();
                    pendingDay = -1;
                    day = next;
                }
                else if (phase == "end" && cause == "ai")
                {
                    pendingDay = day;
                    day = -1;
                }
                else if (phase == "end" && cause == "human")
                {
                    pending.Clear();
                    pendingDay = -1;
                }
                continue;
            }
            string stack = e.GetProperty("stack").GetString()!.ToUpperInvariant();
            if (day >= 0) result[(stack, day)] = (e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32());
            else if (pendingDay >= 0) pending.Add((stack, e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32()));
        }
        return result;
    }
}
