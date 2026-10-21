using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Атака отряда из карты (срез 6 <c>docs/ai-turn-plan.md</c>, <c>docs/ai-attack.md</c>): на каждой оценке отряда
/// (<c>ai_tactic_stack</c>) — (1) кэш отрядов-целей порта (<see cref="AiTactics.StackTargets"/>) против множества целей
/// строк силы журнала <c>0x45375a</c> (оценщик пишет их на каждую запись кэша, по порядку; цель узнаётся по A); (2) на каждой строке
/// <c>ai_strength</c> оценщика атаки (<c>0x453755</c>) — A, B, C и p порта (<see cref="AiTactics.AttackStrength"/>, кэш —
/// из журнала) против журнала. Состояние — <see cref="AiLiveDay"/> плюс опыт боёв, лечение и здоровье из журнала.
/// Берсерк (<c>0x19</c>) не сверяется: у него свой список целей.
/// </summary>
public abstract class AiAttackStrengthHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly HashSet<long> AttackTypes = [0x03, 0x1b, 0x1c];

    /// <remarks>Измерено 23.09.2026: human — строк силы 823, A сошлось 785, B 264, p 415, кэш сошёлся в 88 оценках;
    /// legions — 507, 501, 303, 394, 70 (с наймами лидеров из журнала). B и p расходятся почти всегда в сторону «порт ниже»: воспроизведению дня не
    /// хватает лечения зельями и найма в отряд (оперативный слой ИИ); где состав и здоровье сходятся, сходится и B.
    /// Кэш — исключения хода и радиус «главного» отряда. С цепочкой модификаторов юнитов в силе (броня и атака
    /// действующие, 23.09 ночь): human — A 816, B 708, p 765; legions — A 507 из 507, B 420, p 456.
    /// Кэш с 24.09 сверяется по строкам силы (они пишутся на каждую запись кэша, в том числе на слабые цели, которым фабрика
    /// действия кандидата не даёт, <c>docs/ai-attack.md</c> §8), ход тактики сквозной (исключения, флаг радиуса, новый выбор
    /// отряда), невидимые не в кэше: кэш сошёлся 490 из 625 и 393 из 494 оценок (было 88 и 70 при сверке по кандидатам).
    /// «Атакующий нейтрал» разобран 24.09 (<c>docs/ai-attack.md</c> §9): это водный берсерк — фильтр кэша <c>0x45e9db</c> берёт у
    /// лидера «только по воде» лишь цели на воде, а <c>0x45ea2e</c> не сравнивает хозяев, так что нейтрал берёт и нейтралов других
    /// подрас: кэш сошёлся 511 и 473 (было 490 и 393), у нейтралов разошлось 1 и 11 (было 22 и 91 — остаток: сила целей в живом дне).
    /// Прежний остаток: нейтралы 22 и 91 (у атакующего нейтрала движок берёт меньше целей — не разобрано), прочие 113 и 10 —
    /// исключения, восстановленные по журналу приближённо, и сила целей в живом дне (и строки силы берсерка — у него свой
    /// список целей, порт его не строит).
    /// Сила против охраны руин (24.09, <c>0x5dda98</c> с поддержкой <c>0x5ddb38</c>; строка <c>0x5ddb31</c> перед кандидатом
    /// <c>VisitRuin</c>): human — строк 146, A 146, C 144, p 113; legions — 219, 219, 219, 201. Расходится B (своя сила в живом
    /// дне), как у атак. 25.09: живой день повторяет зелья вне боя (<c>potion_use</c>, здоровье после) и переносы юнитов из гарнизона
    /// в отряд (<c>unit_transfer</c>, подготовка <c>0x5d5d73</c>) — ровно то, чего не хватало своей силе B: human — A 819, B 755, p 800,
    /// кэш 519, руины p 122 (было 816, 708, 765, 511, 113); legions — B 468, p 486, кэш 478, руины p 212 (было 420, 456, 473, 201).
    /// Без этих двух строк — прежние числа. Исключения кэша по объектам клетки точки (25.09, <c>0x45f902</c>, <c>docs/ai-attack.md</c> §11;
    /// отряд в крепости не исключается — исключается крепость): кэш 519 → 605 и 478 → 479; со старым правилом — прежние. Не подгонять.</remarks>
    [SkippableTheory]
    // 25.09: нанятый отряд в живом дне получает id движка (AiLiveDay.Hired: номера погибших движок не переиспользует) — строки журнала
    // о нём больше не теряются. human — строк силы 823 → 827, A 819 → 823, B 755 → 759, p 800 → 804, кэш 605 → 607; legions без изменений.
    // 25.09: живой день повторяет призыв (AiLiveDay.Cast — призванный с id движка), новый выбор отряда — по выбору фазы 0 → 1.
    // Строк силы 834 и 509, A 830 и 509 (все сошлись), B 766 и 470, p 809 и 488, кэш 610 и 480.
    // 25.09: вес иммунитетов в силе юнита — и выданных модификаторами (варды, навыки лидера; AiTactics.Fighters): human — A 830 → 834
    // (все), B 766 → 818, p 809 → 829, кэш 610 → 623, руины B 97 → 136, p 122 → 142; legions без изменений.
    // 27.09: переносы журнала по id юнитов, когда правило порта их отвергает (клетки порта расходятся с движком — расстановку ИИ
    // 0x5e7991 зонд не пишет), и расстановка групп после серии перестановок ИИ (AiLiveDay.ById/ArrangeAfter): human — A 834 → 832,
    // B 818 → 815, p 829 → 832, кэш 623 → 622, руины p 142 → 143; legions — B 470 → 486, p 488 → 502, кэш 480 → 483, руины B 208, p 212 → 215.
    // 28.09: нанятый в живом дне юнит берёт id движка по строке переноса, при чужих юнитах в клетках порта перенос сразу по id,
    // перестановка ИИ расставляет отряд в начале серии и после каждого обмена (AiLiveDay.Rename/AtCells/ArrangeAfter): human — A 832 → 834,
    // B 815 → 821, кэш 622 → 624; legions — B 486 → 489, p 502 → 504, руины p 215 → 216. Чит опыта стражей столиц их не сдвигает.
    // 30.09: перезагруженный день (тот же игрок ИИ начинает ход второй раз — legions день 12, human день 23) — живой день заново с
    // сохранения этого хода, как в AiTacticFromMapHarnessTests (прежде отряд legions KC0000 нёс в повтор юнита UN01a2, выросшего до UU0002
    // в первом прогоне дня: своя сила B 193,98 вместо 153,98): human кэш 624 → 628; legions строк 509 → 513, A 513, B 489 → 499, p 504 → 508,
    // кэш 483 → 495. Со старым правилом — прежние числа. Остаток B (13/14/15) — D2_B_SHOW: состав сходится (shots — p4_units), расходятся
    // здоровье и модификаторы лидеров (Banner Bearer у лидера с 1 HP после зелья воскрешения, human KC0003 дни 39–42: +0,08; оружие
    // Unholy Chalice в столице, день 45).
    // 30.09 (§28): сила по юнитам сверена на probe72 (Probe72UnitsHarnessTests: 30851 юнит из 30851, 10632 группы) — правило верно,
    // остаток B — состояние живого дня. Регенерация зонда 7.2 (гарнизоны, своя земля до роста, руины): B ai-q09 216 → 222, legions
    // 499 → 501; живой день повторяет надевание предметов (item_equip, MOD_EQUIP — оружие UM2302 и т. п.): B human 821 → 830, legions
    // 501 → 507, ai-q09 222 → 229; вместе p 508 → 510 и 224 → 229, руины p 143 → 146, 216 → 217, 52 → 54. Старые правила — прежние числа.
    // 30.09 (§32, остаток своей силы B — состояние живого дня; каждое правило проверено выключением — прежние числа):
    // (1) наймы юнитов в гарнизон — стратегией порта на потоке CRT движка (JournalCrt: srand(1) и строки crt_rand потока, перед первым
    //     ai_unit_pick хода), тип выбирает правило порта — probe72 день 15: был UU0080 вместо UU0086 (B ниже на 14,28): probe72 B 243 → 253,
    //     p 243 → 252; legions B +2, p +2, руины B и p +1. Найм порта сошёлся с hire_unit в 24/29, 18/23, 45/55, 38/41 ходах (probe72, shots,
    //     human, legions); наймы из журнала (hire_unit) дают те же B, но кэш legions 495 → 494;
    // (2) казна журнала (bank mode 0) — лечение в городе по HEAL_C упирается в неё (human день 45: UN019a 121 золота → 60 HP, 163/170, а не
    //     полное; B «порт выше на 3,08» = 7 HP · 0,44): human B 830 → 834;
    // (3) предметы, созданные и израсходованные посреди дня (item_create/item_consume; shots день 7 — чаша IG3002 из руины, UM2302 +12,0285):
    //     shots B 229 → 231, p 229 → 230; legions B +2, руины B +1;
    // (4) надевание предмета, лежащего у другого отряда (передача предметов журнал не пишет; legions день 34 — знамя S073IM00b6 от KC0089):
    //     legions B +1 (165,109 · 60/51 = 194,246);
    // (5) порт: MOD_EQUIP надетого вошедшему в отряд юниту (GameSession.EquipOnMembership, 0x6147c4/0x61483b; legions день 38 — UN01e9 из
    //     гарнизона со знаменем UM2102, +2,75294): legions B +1, p +1, руины B и p +1.
    // Итог: B 830/507/229/243 → 834/513/231/253 — своя сила B сходится во всех строках; p 832/510/229/243 → 832/513/230/252.
    // 20.10 (r20/batB, И40): строк силы меньше записей кэша — оценщик выходит до строки у проваленной сегодня записи и у отсечённой
    // фильтром приказа, а поддержка C берёт весь кэш (см. Subsequence): строки — часть кэша порта, C — по кэшу порта. Остаток p закрыт:
    // human p 832 → 834 (кэш «по части записей» 7), shots 230 → 231 (33), probe72 252 → 253 (12); без правила — прежние числа.
    [CorpusRow("20260919-human", 834, 834, 834, 834, 628, 146, 146, 144, 146)]
    [CorpusRow("20260920-legions", 513, 513, 513, 513, 495, 219, 219, 219, 219)]
    // 28.09: партия ai-q09 с зондом 7.1 п. 2 (состав групп в ai_strength): строк силы 231, A 231, B 216, p 224, кэш 133; руины — C 54 из
    // 54. Поддержка C (правило «сильнейший в квадрате — сама цель») сходится, значит расхождение бегства KC0082 (human, день 41) — состояние
    // живого дня, а не правило. Захват деревень в живом дне (fort_capture) старых строк не сдвинул.
    [CorpusRow("20260928-shots", 231, 231, 231, 231, 133, 54, 54, 54, 54, "passive-002.jsonl", "ai-q09-ai-ai-diplomacy-human")]
    // 30.09: партия probe72 (ai-q09, зонд 7.2): строк силы 253, A 253, B 237, p 237, кэш 148, руины — 31 из 31. Списки сил заново после
    // уничтоженного отряда (§27, AiTactics.StackDestroyed): кэш 148 → 150; склад крепости в подготовке (AiLiveDay.FortItems) и охрана
    // крепости числа не сдвинули. Со старыми правилами — прежние числа.
    // Вместе с §28 (регенерация, надевание предметов в живом дне): B и p 237 → 243.
    [CorpusRow("20260930-probe72", 253, 253, 253, 253, 150, 31, 31, 31, 31, "passive-001.jsonl", "ai-q09-ai-ai-diplomacy-human")]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — строк силы 173/173 (A, B, p), руины 16/16; кэш 241 из 273:
    // лишние цели у порта (26 + 6 у нейтрала) — тот же остаток кэша, что у human и legions.
    [CorpusRow("20261019c-cov", 173, 173, 173, 173, 241, 16, 16, 16, 16, "passive-001.jsonl", "greenskins-human")]
    // 19.10 (r19b/m1920): карты 19 и 20 (20261019b-port-synth, отрезки партий). Карта 19 — строк силы 148, A, B, C и p сошлись во всех
    // (C не ноль у 8 — поддержка S1 и цели рядом с отрядами-приманками; S2/S3 — ноль, как у порта); кэш 64 из 66 — два призванных
    // (S143KC003c, KC003d) с приказом L_ATTACK_STACK: строки силы движка — только по цели приказа, кандидаты в харнессе тактики при этом
    // сошлись. Карта 20 — 10 из 10, кэш 12, руины 17 из 17 (пустые руины R1/R2: A 0, p 1).
    [CorpusRow("20261019b-port-synth", 148, 148, 148, 148, 64, 0, 0, 0, 0, "passive-001.jsonl", "port-synth-19-ai-spells", 557L, 657903L, 148)]
    [CorpusRow("20261019b-port-synth", 10, 10, 10, 10, 12, 17, 17, 17, 17, "passive-001.jsonl", "port-synth-20-ai-battle", 1164985L, 1947173L, 10)]
    // 20.10 (r20/batB): карта 23 (20261020-port-synth, 60 дней, три ИИ на очень трудной) — строк силы 565: A 564, B 561, p 561, C 564
    // (не ноль у 70 — соседние отряды цели в кэше); кэш 412 + 8 «по части записей» из 429; руины 155/155. Остаток — состояние живого дня:
    // B ниже у KC0007 дня 54 (4 строки: зелья и лечение утра, AiStackItemsHarnessTests_PortSynth23), кэш 9 — сила целей.
    [CorpusRow("20261020-port-synth", 565, 564, 561, 561, 412, 155, 155, 155, 155, "passive-001.jsonl", "port-synth-23-ai-long", 0L, 0L, 564)]
    public void Strength_FromMap(string corpus, int wantRows, int wantA, int wantB, int wantP, int wantCache,
                                 int wantRuinRows, int wantRuinA, int wantRuinC, int wantRuinP,
                                 string journalName = "passive-001.jsonl", string stem = "", long fromSeq = 0, long toSeq = 0,
                                 int wantC = -1)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        string journal = Path.Combine(dir, journalName);
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var saves = Directory.GetFiles(dir, $"{stem}*.sg").Order(StringComparer.Ordinal)
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Last());
        var savesByTurn = Directory.GetFiles(dir, $"{stem}*.sg").Order(StringComparer.Ordinal)
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.ToList());
        var events = JournalCache.Read(journal, ["save", "ai_budget_spend", "ai_turn", "ai_tactic_stack", "ai_tactic_candidate", "ai_tactic_pick", "ai_strength",
                                                 "ai_order", "hire_leader", "stack_move", "battle_end", "ap_restore", "xp", "heal", "hp_change", "revive",
                                                 "potion_use", "unit_transfer", "fort_capture", "spell_cast", "item_equip", "item_unequip", "hire_unit", "item_create", "item_consume", "bank",
                                                 "crt_rand", "ai_unit_pick", "game_phase"])
                                 // Журнал с несколькими картами (20261019b-port-synth: карты 19–22) — партия отрезком seq.
                                 .Where(e => e.Seq >= fromSeq && (toSeq == 0 || e.Seq <= toSeq))
                                 .OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);
        // Перезагруженный день (игрок ИИ начинает ход второй раз, legions день 12, human день 23): живой день заново с сохранения, с
        // которого движок играет этот ход, — как в AiTacticFromMapHarnessTests (строки save по порядку, если файлов за день столько же).
        var saveSeqs = events.OfType<SaveEvent>().Where(x => x.Ok).GroupBy(x => (int)x.Turn)
                             .ToDictionary(g => g.Key, g => g.Select(x => x.Seq).ToList());
        string? PickSave(int turn, long seq)
        {
            if (!savesByTurn.TryGetValue(turn, out var files)) return null;
            if (stem.Length == 0 && saveSeqs.TryGetValue(turn, out var seqs) && seqs.Count == files.Count && seqs.FindLastIndex(q => q < seq) is var i and >= 0)
                return files[i];
            return files[^1];
        }
        string? liveSave = null;
        var begunToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AiLiveDay? live = null;
        var battleXp = new List<XpEvent>();
        var tally = new SortedDictionary<string, int>();
        int shown = 0;
        // Окно одной оценки отряда: снимок состояния на её начало, строки силы и цели кандидатов атаки.
        GameSession? atStart = null;
        string? evaluated = null;
        var strengths = new List<(AiStrengthEvent Row, string Target)>();
        var targets = new List<string>();
        var rows = new List<AiStrengthEvent>();
        var all = new List<(string Target, int X, int Y, double Score)>();
        MapState? checkedMap = null;
        AiStrengthEvent? pending = null, pendingRuin = null;
        var ruinRows = new List<(AiStrengthEvent Row, string Ruin)>();
        int shownCache = 0, shownRuin = 0;
        // Ход тактики игрока сквозной, как в AiTacticFromMapHarnessTests: исключения кэша, флаг радиуса, слабые атаки.
        var turns = new Dictionary<string, AiTacticTurn>(StringComparer.OrdinalIgnoreCase);
        // Списки сил тактики после уничтоженного отряда (CCmdStackDestroyedMsg, 0x471d4b) — на следующем окне игрока.
        var forcesStale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? player = null, selected = null;
        bool? newPick = null;   // выбор фазы 0 → 1: новый отряд или продолжение (как в AiTacticFromMapHarnessTests)

        void Close()
        {
            if (atStart is null || evaluated is null) return;
            var session = atStart;
            atStart = null;
            if (!session.Map.Stacks.TryGetValue(evaluated, out var own)) return;
            IReadOnlyList<MapStack> cache = [.. targets.Distinct(StringComparer.OrdinalIgnoreCase)
                                                       .Select(t => session.Map.Stacks.GetValueOrDefault(t)).OfType<MapStack>()];
            var turn = player is not null ? turns.GetValueOrDefault(player) : null;
            // Кандидаты ставят радиус кэша «главного» отряда — и в окне без атак: по нему судит AfterPathCheck.
            if (turn is not null) _ = AiTactics.Candidates(session, turn, own);
            if (rows.Count > 0 && turn is not null)
            {
                var port = AiTactics.StackTargets(session, turn, own);
                // Строка силы 0x45375a пишется на каждую запись кэша — до фабрики действия, которая слабую цель отбрасывает
                // (0x45129b), — в порядке кэша и заново на каждый тип атаки; цель узнаётся по A.
                var portA = port.Select(t => AiTactics.GroupStrength(session, t, false, own.UnitIds.Count)).ToList();
                bool same = portA.Count > 0 && rows.Count % portA.Count == 0
                            && rows.Select((r, i) => Near(portA[i % portA.Count], r.A)).All(x => x);
                // Строк меньше записей: оценщик 0x453585 до строки силы выходит у записи, проваленной сегодня (0x453ac0 → 0x462135,
                // cmp eax, 3 — 0x4535c7), а фильтр приказа отсекает цели раньше (L_ATTACK_STACK — только цель приказа, L_DEFEND_CITY —
                // радиус; AiTactic.Admits). A и C записи посчитаны при сборке кэша (поля +0x10 и +0x18 записи, 0x453743/0x45371c), поэтому
                // поддержка C берёт ВЕСЬ кэш. Строки — подпоследовательность кэша порта: C сверяется по кэшу порта. Карта 23
                // (20261020-port-synth): KC0036 (L_ATTACK_STACK на KC001b) день 20 — строка одна, C 184 = 48 + 136 у KC0023 и KC002c;
                // KC0041 день 31 — C 417,07 у KC0017.
                bool part = !same && portA.Count > 0 && Subsequence(rows.Select(r => r.A).ToList(), portA);
                bool neutral = AiTactics.NeutralOwner(session, own.Owner);
                Count(tally, same ? "кэш сошёлся" : part ? "кэш сошёлся, строки по части записей" : neutral ? "кэш разошёлся, атакует нейтрал" : "кэш разошёлся");
                if (same || part) cache = port;
                else if ((!neutral || Environment.GetEnvironmentVariable("D2_NEUTRAL_SHOW") is not null) && shownCache++ < 15)
                    output.WriteLine($"кэш {own.Id} день {session.Map.Turn}: журнал A [{string.Join(" ", rows.Select(r => $"{r.A:0.##}"))}] " +
                                     $"(кандидаты [{string.Join(" ", targets.Distinct())}]), порт [{string.Join(" ", port.Select((t, i) => $"{t.Id} A {portA[i]:0.##}"))}]");
            }
            // Руины (VisitRuin, 0x46c02a → 0x5dda98): строка силы 0x5ddb31 прямо перед кандидатом.
            foreach (var (j, ruinId) in ruinRows)
            {
                if (session.Map.Sites.FirstOrDefault(r => r.Id.Equals(ruinId, StringComparison.OrdinalIgnoreCase)) is not { } ruin) continue;
                var p = AiTactics.RuinStrength(session, own, ruin);
                bool a = Near(p.A, j.A), b = Near(p.B, j.B), c = Near(p.C, j.C), r = Near(p.P, j.Ret);
                Count(tally, $"руина A {(a ? "да" : "нет")}");
                Count(tally, $"руина B {(b ? "да" : "нет")}");
                Count(tally, $"руина C {(c ? "да" : "нет")}");
                Count(tally, $"руина p {(r ? "да" : "нет")}");
                if (!(a && b && c) && shownRuin++ < 12)
                    output.WriteLine($"руина {own.Id} → {ruinId}: журнал A {j.A} B {j.B} C {j.C} p {j.Ret}; порт A {p.A} B {p.B} C {p.C} p {p.P}");
            }
            foreach (var (j, targetId) in strengths)
            {
                if (!session.Map.Stacks.TryGetValue(targetId, out var target)) { Count(tally, "нет цели"); continue; }
                var p = AiTactics.AttackStrength(session, own, target, cache);
                bool n = j.P2N == target.UnitIds.Count && j.P4N == own.UnitIds.Count;
                bool a = Near(p.A, j.A), b = Near(p.B, j.B), c = Near(p.C, j.C), r = Near(p.P, j.Ret);
                Count(tally, $"n {(n ? "да" : "нет")}");
                Count(tally, $"A {(a ? "да" : p.A < j.A ? "нет, порт ниже" : "нет, порт выше")}");
                Count(tally, $"B {(b ? "да" : p.B < j.B ? "нет, порт ниже" : "нет, порт выше")}");
                Count(tally, $"C {(c ? "да" : "нет")} (журнал {(j.C == 0 ? "ноль" : "не ноль")})");
                Count(tally, $"p {(r ? "да" : "нет")}");
                if (!b && Environment.GetEnvironmentVariable("D2_B_SHOW") is not null)
                    output.WriteLine($"своя сила B {own.Id} → {target.Id} день {session.Map.Turn}: журнал {j.B} порт {p.B} n {j.P4N}/{own.UnitIds.Count} [" + string.Join(" ", own.UnitIds.Select(u =>
                        session.Map.Units.TryGetValue(u, out var uu) ? $"{uu.Id[^4..]}:{uu.TypeId[^4..]}:L{uu.Level}:{uu.Hp}/{session.HpMax(uu)}:m{uu.Modifiers.Count}" : "?")) + $"] items {own.Items.Count} inside '{own.Inside}' | " + string.Join(" ", own.UnitIds.Select(u => $"{u[^4..]}={AiTactics.UnitsStrength(session, [u], true, target.UnitIds.Count, 0):0.###}[{string.Join(",", session.Map.Units.TryGetValue(u, out var mu) ? mu.Modifiers : [])}]"))
                        + (j.P4Units is { } pu ? $" | журнал юниты [{string.Join(" ", pu)}]" : ""));
                if (a && b && !c && shown++ < 12)
                    output.WriteLine($"C {own.Id} → {target.Id}: журнал {j.C}, порт {p.C}, A {j.A}; кэш [" +
                        string.Join(" ", cache.Select(x => $"{x.Id}@{x.X},{x.Y} вид {AiTactics.TargetKind(session, x)} A {AiTactics.GroupStrength(session, x, false, own.UnitIds.Count):0.##}")) +
                        $"] цель @{target.X},{target.Y}");
            }
        }

        var hiresOfTurn = AiSpellHarnessTests.HiresOfTurns(events);
        var crt = JournalCrt.Of(journal);
        var seenIds = new JournalSeenIds();
        foreach (var e in events)
            switch (e)
            {
                case AiTurnEvent { Phase: "begin" } t:
                    Close();
                    selected = null;
                    newPick = null;
                    bool reloaded = live is not null && live.Day == t.Turn && t.Player is not null && !begunToday.Add(t.Player);
                    string? pickedSave = PickSave((int)t.Turn, t.Seq);
                    if (live is null || live.Day != t.Turn || reloaded || pickedSave != liveSave)
                    {
                        begunToday.Clear();
                        if (t.Player is not null) begunToday.Add(t.Player);
                        liveSave = pickedSave;
                        live = pickedSave is { } sg ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends) : null;
                    }
                    if (t.Player is not null && live is not null)
                    {
                        // Счётчики id движка не ниже виденных в журнале (JournalSeenIds), как в AiTacticFromMapHarnessTests.
                        if (Environment.GetEnvironmentVariable("D2_SEEN_IDS") != "false") live.SyncIds(seenIds.Until(events, t.Seq, t.Player[..4]));
                        var begun = live.BeginTurn(t, hiresOfTurn.GetValueOrDefault(t.Seq), crt, tally);
                        if (AiTactics.Begin(begun ?? live.Session(), t.Player, AiTacticMemo.Empty) is { } fresh) turns[t.Player] = fresh;
                        else turns.Remove(t.Player);
                        forcesStale.Remove(t.Player);
                    }
                    break;
                case AiTacticStackEvent s when live is not null:
                    Close();
                    if (s.Stack is not null) live.FortItems(s.Stack);
                    atStart = live.Session();
                    checkedMap = atStart.Map;
                    evaluated = s.Stack;
                    player = s.Player;
                    // Новый выбор отряда — по выбору фазы 0 → 1 (stack ≠ stack_after), как в AiTacticFromMapHarnessTests.
                    if (player is not null && turns.TryGetValue(player, out var picked) && (newPick ?? !string.Equals(selected, s.Stack, StringComparison.OrdinalIgnoreCase)))
                        picked.Select();
                    if (player is not null && turns.TryGetValue(player, out var rebuilt) && forcesStale.Remove(player))
                        AiTactics.StackDestroyed(atStart, rebuilt);
                    selected = s.Stack;
                    newPick = null;
                    strengths.Clear();
                    targets.Clear();
                    rows.Clear();
                    all.Clear();
                    ruinRows.Clear();
                    pending = pendingRuin = null;
                    break;
                case AiTacticPickEvent { PhaseBefore: 0 } p0:
                    newPick = !string.Equals(p0.Stack, p0.StackAfter, StringComparison.OrdinalIgnoreCase);
                    break;
                case AiTacticPickEvent { PhaseBefore: 3 } pick:
                    Close();
                    if (player is not null && turns.TryGetValue(player, out var checkedTurn))
                        AiJournalTactic.AfterPathCheck(checkedTurn, all, AiJournalTactic.Kept(pick), checkedMap ?? live!.Session().Map);
                    all.Clear();
                    break;
                case AiTacticPickEvent { PhaseBefore: > 3 }:
                    Close();
                    break;
                case AiStrengthEvent st when st.RetAddr == "0x45375a" && atStart is not null:
                    pending = st;
                    rows.Add(st);
                    break;
                case AiStrengthEvent sr when sr.RetAddr == "0x5ddb31" && atStart is not null:
                    pendingRuin = sr;
                    break;
                // Ветка руин обязана стоять раньше общей ветки вставленных кандидатов ниже: иначе та перехватит кандидата.
                case AiTacticCandidateEvent { Action: { Type: 5, Target: { } ruinTarget } } cr when atStart is not null
                                                   && string.Equals(cr.Action.Stack, evaluated, StringComparison.OrdinalIgnoreCase):
                    if (cr.Inserted == true) all.Add((ruinTarget, PointX(cr), PointY(cr), cr.Score ?? 0));
                    if (pendingRuin is not null) ruinRows.Add((pendingRuin, ruinTarget));
                    pendingRuin = null;
                    break;
                case AiTacticCandidateEvent { Inserted: true, Action: { } any } ci when atStart is not null
                                                   && !(any.Type is { } t0 && AttackTypes.Contains(t0) && string.Equals(any.Stack, evaluated, StringComparison.OrdinalIgnoreCase)):
                    all.Add((any.Target ?? "", PointX(ci), PointY(ci), ci.Score ?? 0));
                    break;
                case AiTacticCandidateEvent c when atStart is not null && c.Action is { Type: { } type, Target: { } target } a
                                                   && AttackTypes.Contains(type) && string.Equals(a.Stack, evaluated, StringComparison.OrdinalIgnoreCase):
                    if (c.Inserted == true) all.Add((target, PointX(c), PointY(c), c.Score ?? 0));
                    targets.Add(target);
                    if (pending is not null) strengths.Add((pending, target));
                    pending = null;
                    break;
                case StackMoveEvent m when live is not null:
                    live.Move(m.Stack, (int)m.X, (int)m.Y, (int)m.MoveLeft);
                    break;
                case AiOrderEvent o when live is not null:
                    live.Order(o);
                    break;
                case HireLeaderEvent hl when live is not null:
                    live.HireLeader(hl);
                    break;
                case SpellCastEvent sc when live is not null:
                    live.Cast(sc);
                    break;
                case BattleEndEvent be when live is not null:
                    int gone = live.DestroyedStacks;
                    live.Battle(be);
                    if (live.DestroyedStacks != gone) forcesStale.UnionWith(turns.Keys);
                    // Опыт за бой журнал пишет до battle_end: повышение (лечит до полного) — после итога боя.
                    foreach (var x in battleXp) live.Xp(x);
                    battleXp.Clear();
                    break;
                case XpEvent x when x.RetAddr != AiLiveDay.DailyXpCheat:
                    battleXp.Add(x);
                    break;
                case BankEvent { Mode: 0 } bk when live is not null:
                    live.SetBank(bk.Player, bk.After, journalRow: true);
                    break;
                case HealEvent { Ret: true } h when live is not null:
                    live.Heal(h.Unit);
                    break;
                case HpChangeEvent { Ret: true } hc when live is not null:
                    live.Hp(hc.Unit, (int)hc.HpAfter);
                    break;
                case ReviveEvent { Ret: true, HpAfter: { } hpAfter } rv when live is not null:
                    live.Hp(rv.Unit, (int)hpAfter);
                    break;
                case PotionUseEvent { Ret: true, InBattle: false } pu when live is not null:
                    live.Hp(pu.Target, (int)pu.HpAfter);
                    break;
                case ItemEquipEvent ie when live is not null:
                    live.Equip(ie);
                    break;
                case ItemCreateEvent { Ret: true } ic when live is not null && ic.Cause != "site_buy_item"
                                                          && ic.Ids.TryGetValue("stack", out var holder) && ic.Ids.TryGetValue("item_type", out var made):
                    live.GainItem(holder, made);
                    break;
                case ItemConsumeEvent { Consumed: true } used when live is not null:
                    live.LoseItem(used.ItemBefore);
                    break;
                case FortCaptureEvent fc when live is not null:
                    live.Capture(fc);
                    break;
                case UnitTransferEvent ut when live is not null:
                    live.Transfer(ut);
                    break;
            }
        Close();
        foreach (var (k, n) in tally) output.WriteLine($"{k}: {n}");
        Assert.Equal(wantRows, tally.GetValueOrDefault("p да") + tally.GetValueOrDefault("p нет"));
        Assert.Equal(wantA, tally.GetValueOrDefault("A да"));
        Assert.Equal(wantB, tally.GetValueOrDefault("B да"));
        Assert.Equal(wantP, tally.GetValueOrDefault("p да"));
        Assert.Equal(wantCache, tally.GetValueOrDefault("кэш сошёлся"));
        Assert.Equal(wantRuinRows, tally.GetValueOrDefault("руина p да") + tally.GetValueOrDefault("руина p нет"));
        Assert.Equal(wantRuinA, tally.GetValueOrDefault("руина A да"));
        Assert.Equal(wantRuinC, tally.GetValueOrDefault("руина C да"));
        Assert.Equal(wantRuinP, tally.GetValueOrDefault("руина p да"));
        if (wantC >= 0) Assert.Equal(wantC, tally.GetValueOrDefault("C да (журнал ноль)") + tally.GetValueOrDefault("C да (журнал не ноль)"));
    }

    private static bool Near(double port, double? logged) => logged is { } v && Math.Abs(port - v) < 1e-6;

    /// <summary>Строки силы (A по порядку) — часть записей кэша порта в том же порядке, блоками на каждый тип атаки (построитель 0x4533fb
    /// общий у AttackStack, AttackParty, AttackStackRoam и берсерка): внутри блока строка — у следующей подходящей записи, новый блок
    /// начинается с начала кэша, блоков не больше типов.</summary>
    private static bool Subsequence(IReadOnlyList<double?> rows, IReadOnlyList<double> cache)
    {
        const int MaxBlocks = 4;
        int at = 0, blocks = 1;
        foreach (var a in rows)
        {
            int found = -1;
            for (int k = at; k < cache.Count && found < 0; k++) if (Near(cache[k], a)) found = k;
            if (found < 0)
            {
                if (++blocks > MaxBlocks) return false;
                for (int k = 0; k < cache.Count && found < 0; k++) if (Near(cache[k], a)) found = k;
                if (found < 0) return false;
            }
            at = found + 1;
        }
        return true;
    }

    private static int PointX(AiTacticCandidateEvent c) => (int)(c.Action?.Point?.X ?? -1);

    private static int PointY(AiTacticCandidateEvent c) => (int)(c.Action?.Point?.Y ?? -1);

    private static void Count(IDictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
}

/// <summary>Партии харнесса — отдельными классами, чтобы шли параллельно (<see cref="CorpusRowAttribute"/>).</summary>
[OnlyCorpus("20260919-human")]
[Trait("Category", "Corpus")]
public sealed class AiAttackStrengthHarnessTests_Human(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class AiAttackStrengthHarnessTests_Legions(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);

[OnlyCorpus("20260928-shots")]
[Trait("Category", "Corpus")]
public sealed class AiAttackStrengthHarnessTests_Shots(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);

[OnlyCorpus("20260930-probe72")]
[Trait("Category", "Corpus")]
public sealed class AiAttackStrengthHarnessTests_Probe72(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class AiAttackStrengthHarnessTests_Cov(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);

[OnlyCorpus("20261019b-port-synth", Stem = "port-synth-19-ai-spells")]
[Trait("Category", "Synth")]
public sealed class AiAttackStrengthHarnessTests_PortSynth19(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);

[OnlyCorpus("20261019b-port-synth", Stem = "port-synth-20-ai-battle")]
[Trait("Category", "Synth")]
public sealed class AiAttackStrengthHarnessTests_PortSynth20(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);

[OnlyCorpus("20261020-port-synth", Stem = "port-synth-23-ai-long")]
[Trait("Category", "Synth")]
public sealed class AiAttackStrengthHarnessTests_PortSynth23(ITestOutputHelper output) : AiAttackStrengthHarnessTests(output);
