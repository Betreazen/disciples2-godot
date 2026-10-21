using System.Text.Json;
using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Ход ИИ целиком против журнала (<c>docs/ai-turn-plan.md</c>): сохранение дня N снято прямо перед концом хода
/// человека, поэтому «сохранение → <see cref="GameSession.EndTurn"/>» порта сравнивается с тем, что игроки ИИ сделали
/// в день N по журналу.</summary>
[Trait("Category", "Corpus")]
public class AiTurnHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Постройки ИИ в столице (список 4): множество построенного игроком ИИ за день — порт против строк
    /// <c>build</c> журнала этого дня (партия 20260919-human, 210 дней игроков ИИ: сохранение дня 71 лежит после конца журнала
    /// и не сверяется). Память трат ИИ движок в <c>.sg</c> не пишет — она берётся из журнала. Измерено 23.09: 202 сошлось, 11 —
    /// порт строил здание юнитов, а движок нет (Легионы, дни 38–41 при нуле золота; Империя, дни 25–27). 28.09 разобрано:
    /// построитель столицы зовёт <c>getBuildingStatus</c> (<c>0x446e37</c> → <c>0x5dc9e8</c>) без <c>ignoreCost</c> для здания
    /// юнитов — казна (статус 3) и «сегодня уже строили» (статус 6) проверяются, — осталась 1 из 210. Найм (список 2) — сколько
    /// юнитов в какие укрепления: 207 из 210.</summary>
    [SkippableFact]
    public void Capital_BuildsOfTheDay()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var (builds, spends, lastDay) = Builds(journal);
        var crt = JournalCrt.Of(journal);   // CRT движка в миг конца хода человека (наймы, имена, картинки деревьев роста — И210)
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        int days = 0, same = 0, differ = 0, hireSame = 0, hireDiffer = 0, careSame = 0, careDiffer = 0;
        int leaderSame = 0, leaderFortOnly = 0, leaderDiffer = 0, growSame = 0, growDiffer = 0, spellSame = 0, spellDiffer = 0;
        foreach (string sg in Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal)
                                       .GroupBy(f => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Value).Select(g => g.Last()))
        {
            var looks = MapLooksLoader.Load(sg);
            var map = looks.Map;
            var human = map.Players.Values.First(p => p.IsHuman);
            var info = new GameInfo("PtC", "PtC", [human.Id], map.Limits?.DifficultyGame ?? 1, map.Limits?.SuggestedLevel ?? 1);
            int day = map.Turn;
            // Память трат ИИ движок в .sg не пишет — берём из журнала: последний день траты типа до этого дня.
            var memory = spends.Before(map, day);
            var r250 = new D2.Rules.Rng.EngineRng();
            var random = crt.AtHumanEnd(day) is { } c ? new RandomStreams(r250.Index, r250.TableSnapshot(), c) : null;
            var session = new GameSession(new GameState(info, looks) { Ai = memory, Random = random }, content.MapArt, book);
            var before = map.Players.ToDictionary(p => p.Key, p => p.Value.Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase));
            var sights = new SightRecorder();
            session.Watch(sights);
            var dayReport = session.EndTurn();
            foreach (string line in dayReport.Events.Where(x => x.Contains("не исполнено", StringComparison.Ordinal)).Take(2))
                output.WriteLine($"{Path.GetFileName(sg)}: {line}");
            foreach (var player in map.Players.Values.Where(p => !p.IsHuman && !p.RaceId.EndsWith("RR0004", StringComparison.OrdinalIgnoreCase)))
            {
                var got = session.Map.Players[player.Id].Buildings.Where(b => !before[player.Id].Contains(b))
                                 .Select(b => b.ToUpperInvariant()).Order().ToList();
                // Сохранение после конца журнала (human: день 71 — ходов ИИ в журнале нет): сверять не с чем.
                if (day > lastDay.GetValueOrDefault(player.Id)) continue;
                var logged = builds.GetValueOrDefault((player.Id, day), []);
                var want = logged.Where(b => !b.Contains(':', StringComparison.Ordinal)).Select(b => b.ToUpperInvariant()).Order().ToList();
                days++;
                if (got.SequenceEqual(want)) same++;
                else
                {
                    differ++;
                    output.WriteLine($"{Path.GetFileName(sg)} {player.Id} день {day}: журнал [{string.Join(",", want)}], порт [{string.Join(",", got)}]");
                }
                // Найм: сколько нанято в какие укрепления (типы — поток CRT, он у порта свой). Нанятый — строка стратегии
                // «<игрок> HireUnit <тип> <укрепление>», как строки hire_unit журнала: юниты, погибшие в тот же день, тоже в счёте
                // (у движка в день 2 страж KC002d бьёт свежий отряд PL0009 KC0071 с нанятым UN01ff — hire_unit в журнале остаётся).
                var hiredGot = HiredOf(dayReport.Events, player.Id);
                var hiredWant = logged.Where(b => b.StartsWith("hire:", StringComparison.Ordinal)).Order().ToList();
                // Лечение и воскрешение: кого порт лечил и воскрешал за день (Healed/Revived наблюдателя — и списком 5, и тактикой,
                // которая заводит отряд в укрепление) — против строк heal/revive журнала.
                var caredGot = sights.Sights.Where(x => x.Kind is TurnSightKind.Healed or TurnSightKind.Revived
                                                        && x.PlayerId.Equals(player.Id, StringComparison.OrdinalIgnoreCase))
                                            .Select(x => "care:" + x.Id.ToUpperInvariant()).ToHashSet();
                var caredWant = logged.Where(b => b.StartsWith("care:", StringComparison.Ordinal)).ToHashSet();
                if (caredWant.Count > 0 || caredGot.Count > 0)
                {
                    if (caredWant.SetEquals(caredGot)) careSame++;
                    else
                    {
                        careDiffer++;
                        if (careDiffer <= 10)
                            output.WriteLine($"уход {Path.GetFileName(sg)} {player.Id} день {day}: журнал [{string.Join(",", caredWant)}], порт [{string.Join(",", caredGot)}]; " +
                                string.Join(" ", caredWant.Select(c => c[5..]).Where(map.Units.ContainsKey).Select(id =>
                                    $"{id}: {map.Units[id].TypeId} ур.{map.Units[id].Level} HP {map.Units[id].Hp}/{session.HpMax(map.Units[id])} → {session.Map.Units.GetValueOrDefault(id)?.Hp} где {Where(map, id)}")));
                    }
                }
                // Найм лидеров (список 0): в какие укрепления и каких типов (тип — бросок R250, поток у порта свой). Берётся из
                // строк стратегии «<игрок> HireLeader <тип>|<приказ> <укрепление>»: нанятый лидер в тот же день уходит из
                // укрепления (полный запас хода), поэтому по отрядам конца дня укрепления не узнать.
                var leadersGot = dayReport.Events.Select(e => e.Split(' '))
                    .Where(p => p.Length == 4 && p[0].Equals(player.Id, StringComparison.OrdinalIgnoreCase) && p[1] == "HireLeader")
                    .Select(p => $"leader:{p[3].ToUpperInvariant()}:{p[2].Split('|')[0].ToUpperInvariant()}")
                    .Order().ToList();
                var leadersWant = logged.Where(b => b.StartsWith("leader:", StringComparison.Ordinal)).Order().ToList();
                if (leadersGot.Count > 0 || leadersWant.Count > 0)
                {
                    static string Fort(string k) => k[..k.LastIndexOf(':')];
                    bool fortsSame = leadersGot.Select(Fort).SequenceEqual(leadersWant.Select(Fort));
                    if (leadersGot.SequenceEqual(leadersWant)) leaderSame++;
                    else if (fortsSame) leaderFortOnly++;
                    else
                    {
                        leaderDiffer++;
                        if (leaderDiffer <= 12)
                            output.WriteLine($"лидер {Path.GetFileName(sg)} {player.Id} день {day}: журнал [{string.Join(",", leadersWant)}], порт [{string.Join(",", leadersGot)}]");
                    }
                }
                // Рост деревень (список 3): какие деревни выросли за день.
                var grewGot = session.Map.Sites.Where(x => x.Kind == SiteKind.Village && x.Owner.Equals(player.Id, StringComparison.OrdinalIgnoreCase)
                                                          && (x.Size ?? 0) > (map.Sites.FirstOrDefault(o => o.Id == x.Id)?.Size ?? 0))
                                               .Select(x => "grow:" + x.Id.ToUpperInvariant()).Order().ToList();
                var grewWant = logged.Where(b => b.StartsWith("grow:", StringComparison.Ordinal)).Order().ToList();
                if (grewGot.Count > 0 || grewWant.Count > 0)
                {
                    if (grewGot.SequenceEqual(grewWant)) growSame++;
                    else
                    {
                        growDiffer++;
                        output.WriteLine($"рост {Path.GetFileName(sg)} {player.Id} день {day}: журнал [{string.Join(",", grewWant)}], порт [{string.Join(",", grewGot)}]");
                    }
                }
                // Исследование (список 6): какие заклинания игрок узнал за день.
                var knownBefore = player.KnownSpells.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var learnedGot = session.Map.Players[player.Id].KnownSpells.Where(x => !knownBefore.Contains(x))
                                        .Select(x => "spell:" + x.ToUpperInvariant()).Order().ToList();
                var learnedWant = logged.Where(b => b.StartsWith("spell:", StringComparison.Ordinal)).Order().ToList();
                if (learnedGot.Count > 0 || learnedWant.Count > 0)
                {
                    if (learnedGot.SequenceEqual(learnedWant)) spellSame++;
                    else
                    {
                        spellDiffer++;
                        output.WriteLine($"исследование {Path.GetFileName(sg)} {player.Id} день {day}: журнал [{string.Join(",", learnedWant)}], порт [{string.Join(",", learnedGot)}]");
                    }
                }
                if (hiredGot.SequenceEqual(hiredWant)) hireSame++;
                else
                {
                    hireDiffer++;
                    if (hireDiffer <= 15)
                        output.WriteLine($"найм {Path.GetFileName(sg)} {player.Id} день {day}: журнал [{string.Join(",", hiredWant)}], порт [{string.Join(",", hiredGot)}]");
                }
            }
        }
        output.WriteLine($"дней игроков ИИ {days}: постройки сошлись {same}, разошлись {differ}; найм сошёлся {hireSame}, разошёлся {hireDiffer}; уход сошёлся {careSame}, разошёлся {careDiffer}; " +
                         $"лидеры сошлись {leaderSame}, только укрепление {leaderFortOnly}, разошлись {leaderDiffer}; рост сошёлся {growSame}, разошёлся {growDiffer}; исследование сошлось {spellSame}, разошлось {spellDiffer}");
        Assert.True(days > 30);
        // 28.09: здание юнитов — с проверкой казны и «сегодня уже строили» (0x446e37 → 0x5dc9e8 с флагом IgnoresCost; журнал: у
        // PL000b в день 38 оценка столицы G000BB0055 (200) есть в проходах 1–2 при казне 250 и 234 и пропадает в проходе 3 при 100;
        // у PL0009 в день 25 — G000BB0001 пропадает в проходе 4 при 193): разошлось 11 → 1 (у PL000c в день 34 порт строил два
        // здания за день). Остаток — PL000b день 18: порт нанимает лидера G000UU0074, а движок нет (счётчики ролей [2,2,2], роль
        // бойца, тип пуст — seq 4072166–4072168; расхождение «лидеров» ниже), и на G000BB0064 (300) казны не хватает; прежде порт
        // строил без казны и совпадал случайно. Со старым правилом (ignoreCost всегда) — ровно прежние 11, рост 3/1.
        // 30.09: остаток закрыт — знатный порта открывал свиток вора и издалека (GameSession.ExecuteThief: не достиг цели — только
        // перемещение): в день 18 KC0083 (PL0009) не убивает KC007c, и у PL000b счётчики ролей, как у движка, — лидера нет, казна
        // хватает на G000BB0064. 1 → 0; со старым правилом — прежнее 1.
        Assert.Equal(0, differ);
        // Найм юнитов (список 2): число нанятых в каждое укрепление за день. Измерено 23.09: 210 из 213; с ростом деревень
        // и наймом лидеров — 212. С охраной (стражи нападают, docs/ai-tactic-candidates.md §9) — 211: в день 2 страж KC002d
        // бьёт свежий отряд PL0009 KC0071, отряд с нанятым в него юнитом гибнет (у движка так же — см. ниже).
        // 26.09: бой с карты — первый раунд 1 (две очереди) и боевые предметы лидера: исходы автобоёв сдвинулись, найм 2 → 3
        // (human день 44: PL000b нанял в FT000B), лидеры «только укрепление» 3 → 2 и разошлись 5 → 6, рост 2/2 → 3/1 — тот же день.
        // 28.09: день 2 — не расхождение порта, а счёт харнесса. У движка тоже гибнет: PL0009 нанял три юнита в FT0000
        // (hire_unit seq 1876446/1876534/1876609), UN01ff ушёл в отряд нового лидера KC0071 (unit_transfer seq 1876729), и в ходе
        // нейтралов страж KC002d его разбил (battle_start/battle_end seq 1893880/1894499, UN01ff и UN0202 — HP 0). Журнал считает
        // строки hire_unit, а харнесс считал выживших к концу дня; теперь порт считается по строкам стратегии HireUnit — 3 → 2
        // (остались дни 38 и 44 PL000b: лишний найм в FT000B). Прежний счёт по выжившим — ровно прежние 3.
        // 20.10 (r20/aiA): уход стратегии по юниту (И110), повышение павшего юнита читом опыта (И130), отступление бежавшего защитника (И93): 2 → 1 — лишнего найма PL000b дня 38 нет (остался день 44).
        // 20.10 (r20b/hum): порт тратит CRT на вариант грунта у перекрашенного леса (И210); поток CRT у этого харнесса свой (не движка), типы
        // найма и с ними казна дня сдвинулись: 1 → 2 — день 54 PL0009 нанял в FT0000 двоих, у движка троих (с И210 выключенной — прежнее 1).
        // 20.10 (r20b/q09): поток CRT — движка в миг конца хода человека (JournalCrt.AtHumanEnd): 2 → 1 — день 54 PL0009 снова нанимает троих.
        Assert.Equal(1, hireDiffer);
        // Уход (список 5): юниты, которых движок лечит, в сохранении почти всегда стоят в поле — ИИ заводит отряд
        // в город тактикой (её в порте ещё нет) и лечит там. Измерено 23.09: 6 сошлось, 25 разошлось. С заклинаниями ИИ
        // оперативного слоя (24.09, docs/ai-spells.md) — 5 сошлось, 24 разошлось: литьё тратит броски R250 и казну. С
        // подготовкой к атаке (срез 7) — 25: у PL000b в день 22 порт лечит юнита, который после другой тактики дня ранен. С
        // лечением в крепости при подготовке отряда (0x451d74, docs/ai-items.md §6) — 24: отряд в крепости лечится тактикой.
        // Найм с тех пор считался по новым юнитам (гарнизон или перенос из него в отряд): тактика в тот же день забирает
        // нанятых в отряды; с 28.09 — по строкам стратегии HireUnit. Со слабыми атаками только во втором проходе (docs/ai-attack.md §8) — 23: у порта меньше лишних
        // атак дня и раненых, которых движок не лечит. С раскрытием шпионов в начале хода (docs/ai-tactic-candidates.md §12:
        // бросок R250 на каждого разведанного) — 24: генератор харнесса не движка, лишние броски сдвигают бои дня (день 22,
        // PL000b снова лечит UN01FA); без раскрытия и без знатного — ровно прежние 23.
        // 28.09: уход порта считается по самим лечениям и воскрешениям (Healed/Revived наблюдателя), а не по росту здоровья
        // юнитов, стоявших утром в укреплении: юнит в поле отбрасывался, даже когда тактика порта заводила его отряд в город и
        // лечила (0x451d74, как у движка: human день 7, KC0074 → FT0000, seq 2897972 → heal 2898001). Сошлось 5 → 27, разошлось
        // 24 → 3 (прежним подсчётом — ровно 5 и 24); без дня 71 после конца журнала — 2. Остаток: PL000b день 37 — порт не нанимает
        // лидера в FT0002 (у движка он и берёт FT000B, куда входит и лечится KC0002, seq 10405969); PL0009 день 44 — порт первым
        // ведёт KC0000 и переносит в него павшего UN023E из гарнизона (у движка первым KC008f, переноса нет), после чего вор KC008f
        // входит в столицу и лечится, а у движка его лечение — только выселением KC0000 со счётом 115 против шпионажа 175,5.
        Assert.Equal(27, careSame);
        Assert.Equal(2, careDiffer);
        // Найм лидеров (список 0): укрепление и тип за день. Измерено 23.09 (после базы бюджета по ролям): 51, 3, 5. С
        // подготовкой к атаке (24.09) отряды, призванные тактическим заклинанием, наймом не считаются — 52, 2, 4
        // (R250 харнесса не выставлен, тип лидера — бросок; сдвиг бросков тактики меняет его в пределах пары дней). Со слабыми
        // атаками только во втором проходе (docs/ai-attack.md §8) бросков заклинаний перед атакой меньше — 51, 3, 4. Со знатным
        // (§12 docs/ai-tactic-candidates.md) — 51, 3, 6: воры порта ходят на своих бросках (провал — вор гибнет, дуэль бьёт лидера),
        // и роли лидеров у PL000b и PL0009 сдвигаются (дни 16, 18, 32, 35, 37); без знатного — ровно прежние 51, 3, 4.
        // 25.09 (сессия «день целиком»): рост земли отвергает клетки ландмарков с GLmark.MOUNTAIN = T (HumanDayHarnessTests: земля человека 7 → 69 из 69 дней): земля и доход ИИ другие — 52, 3, 5; без ландмарков — ровно прежние 51, 3, 6.
        // 30.09: разбор PL000b дней 16/18 и 37. Дни 16 и 18 — не найм: знатный PL0009 KC0083 у порта бил вора PL000b KC007c издалека
        // (свиток вора без достижения цели), у движка KC0083 встаёт в 11,29 без действия (seq 4058965) — KC007c жив, воров 2, роль
        // боец, тип по казне пуст (ai_leader_pick seq 4072168, «G000000000»). День 37 — порт не бил по прибытии с нулём очков хода:
        // KC008b встаёт у KC0092 с 0 очков, у движка бой (seq 10269684), жезлоносец KC0092 гибнет, и PL000b нанимает G000UU0073 (seq
        // 10271091). Оба правила (GameSession.ExecuteThief, ExecuteAttack) — 52, 2, 6 → 53, 2, 3; со старыми — прежние.
        // 30.09 (§32): день 23 PL000c — не два найма, а перезагрузка (день сыгран трижды: hire_leader seq 5130899 и 5926220 — первый и третий
        // проходы); считается последний проход — 53, 2, 3 → 54, 2, 2 (со старым подсчётом — прежние). Остаток — дни 35 и 44 PL000b, и это не
        // R250: ai_leader_pick с flag = 1 (помощник), roll = null, rng_before = rng_after — броска нет. Расходится бюджет найма лидера
        // (тип 0): у движка в день 35 — 105 золота (seq 9158098), найма нет; в день 44 — 596, найм. По счётчикам журнала формула сходится
        // (DayBudget_ByType), значит разница — в собственном дне порта (кто жив, жезлоносцев 1 или 2). Бросок типа (flag = 0) журнал пишет
        // целиком: ai_rng site = leader_pick (0x44aee6: n, max, r) и ai_leader_pick.roll/result.
        // 20.10 (r20/rest): бои дня в порядке unitsInfo (Б80) и прочие правки карты 23 — найм лидера дня 35 или 44 PL000b сошёлся: 54, 2, 2 → 55, 1, 2.
        // 20.10 (r20b/hum): тот же сдвиг своего потока CRT (И210) — у PL0009 в день 27 порт не нанимает лидера (казна ушла на найм юнитов
        // других типов): 55, 1, 2 → 54, 1, 3. Бюджет PL000b — на переигровке (replay-human, поток движка) найм лидера 14/14 (И43).
        // 20.10 (r20b/q09): поток CRT — движка в миг конца хода человека (JournalCrt.AtHumanEnd): лидер PL0009 дня 27 снова нанят — 54, 1, 3 → 55, 1, 2.
        Assert.Equal(55, leaderSame);
        Assert.Equal(1, leaderFortOnly);
        Assert.Equal(2, leaderDiffer);
        // Рост деревень (список 3): 2 из 4; оба промаха (Легионы, дни 40 и 44) — порт в этот день строит здание юнитов и
        // тратит золото, а движок нет (то же расхождение, что у построек выше). 28.09 с казной у здания юнитов — 4 из 4 (день 40).
        Assert.Equal(4, growSame);
        Assert.Equal(0, growDiffer);
        // Исследование (список 6): какие заклинания узнаны за день. Измерено 23.09: 21 из 21.
        Assert.Equal(21, spellSame);
        Assert.Equal(0, spellDiffer);
    }

    /// <summary>Постройки игроков ИИ по дням: (игрок, день) → id построек; день — из <c>ai_turn begin</c> игрока. И дни
    /// трат бюджета по типу (<c>ai_budget_spend</c>).</summary>
    private static (Dictionary<(string, long), List<string>> Builds,
                    AiSpendMemory Spends, Dictionary<string, long> LastDay) Builds(string journal)
    {
        var result = new Dictionary<(string, long), List<string>>();
        var spends = new AiSpendMemory();
        var day = new Dictionary<string, long>();
        var begun = new HashSet<(string, long)>();
        var lines = new List<JsonElement>();
        foreach (string line in File.ReadLines(journal))
        {
            if (!line.Contains("\"ev\":\"build\"", StringComparison.Ordinal) && !line.Contains("\"ev\":\"ai_turn\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"ai_budget_spend\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"hire_unit\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"hire_leader\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"city_grow\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"spell_research\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"heal\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"revive\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            lines.Add(doc.RootElement.Clone());
        }
        foreach (var e in lines.OrderBy(e => e.GetProperty("seq").GetInt64()))
        {
            string player = e.GetProperty("player").GetString() ?? "";
            if (e.GetProperty("ev").GetString() == "ai_turn")
            {
                if (e.GetProperty("phase").GetString() == "begin" && e.GetProperty("cause").GetString() == "ai")
                {
                    long turn = e.GetProperty("turn").GetInt64();
                    // Перезагруженный день (human день 23 сыгран трижды, новый процесс движка с того же сохранения): считается последний
                    // проход — прежние строки этого игрока за день отбрасываются (иначе два найма PL000c в FT0003).
                    if (!begun.Add((player, turn))) result.Remove((player, turn));
                    day[player] = turn;
                }
                continue;
            }
            if (!day.TryGetValue(player, out long d)) continue;   // стартовая постройка лорда — до первого хода
            if (e.GetProperty("ev").GetString() == "ai_budget_spend")
            {
                var type = (D2.Rules.Map.AiStepType)e.GetProperty("type").GetInt32();
                spends.Add(player, type, d);
                continue;
            }
            var key = (player, d);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            // Постройка — её id; найм — «hire:укрепление»; лечение и воскрешение — «care:юнит».
            string ev = e.GetProperty("ev").GetString()!;
            if (ev is "heal" or "revive" or "hire_leader" or "city_grow" or "spell_research" && !e.GetProperty("ret").GetBoolean()) continue;
            if (ev == "spell_research" && e.GetProperty("free").GetBoolean()) continue;
            list.Add(ev switch
            {
                "hire_unit" => "hire:" + e.GetProperty("group").GetString()!.ToUpperInvariant(),
                "city_grow" => "grow:" + e.GetProperty("fort").GetString()!.ToUpperInvariant(),
                "spell_research" => "spell:" + e.GetProperty("spell").GetString()!.ToUpperInvariant(),
                "hire_leader" => "leader:" + e.GetProperty("fort").GetString()!.ToUpperInvariant() + ":"
                                 + e.GetProperty("unit_type").GetString()!.ToUpperInvariant(),
                "heal" or "revive" => "care:" + e.GetProperty("unit").GetString()!.ToUpperInvariant(),
                _ => e.GetProperty("building").GetString()!,
            });
        }
        return (result, spends, day);
    }

    /// <summary>Наймы юнитов игрока за день по строкам стратегии «<игрок> HireUnit <тип> <укрепление>» — «hire:укрепление».</summary>
    private static List<string> HiredOf(IEnumerable<string> events, string playerId) =>
        [.. events.Select(e => e.Split(' '))
                  .Where(p => p.Length == 4 && p[0].Equals(playerId, StringComparison.OrdinalIgnoreCase) && p[1] == nameof(AiStepType.HireUnit))
                  .Select(p => "hire:" + p[3].ToUpperInvariant()).Order()];

    private static string Where(MapState map, string unitId) =>
        map.Stacks.Values.FirstOrDefault(s => s.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase)) is { } st
            ? $"отряд {st.Id} inside={st.Inside}"
            : map.Sites.FirstOrDefault(s => s.Guards.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Id ?? "?";
}
