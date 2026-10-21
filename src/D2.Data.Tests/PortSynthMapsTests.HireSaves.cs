using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Units;

namespace D2.Data.Tests;

/// <summary>Карта 21 против партии оригинала: журнал и сохранения ищутся в корпусе по имени карты (партии ещё нет — пропуск).</summary>
public partial class PortSynthMapsTests
{
    /// <summary>
    /// Партия карты 21 портом с начала сценария (человек только выбирает навыки героя дня 1 и жмёт «Конец хода») против оригинала — выбор в
    /// выбор и день в день, как <see cref="Calm_PlayMatchesOriginalJournalAndSaves"/>: выборы тактики (<c>ai_tactic_pick</c> фазы 4 → 0), шаги
    /// стратегии (<c>ai_strategic_step</c> + <c>ai_exec</c>: найм вора, тип каждого найма в гарнизон на CRT, рост деревень), жезлы, сохранения
    /// начала каждого дня (<see cref="CalmRows"/>) и сверх того — клетки каждого отряда и гарнизона (расстановка, И33) и порядок склада
    /// каждой крепости (подготовка в столице, И33). Ответ движка на вопросы карты: тип наймов после имени вора (И43), рост деревни и счёт
    /// мешков по сетке (И20, И44), расстановка трёх ближних и лучника.
    /// <para>Партия 19.10 (<c>20261019b-port-synth</c>, сохранения t01-009 … t12-020): выборов 113/113, шагов 101/101, жезлов 6/6, сохранений
    /// 12/12. До правок <c>r19b/m21</c> — 9/113 и 34/101: вор без гильдии (<c>0x5d4857</c>), навык лидера после дневного чита (<c>UPGCOUNT</c>),
    /// порядок предметов мешка (<c>0x5d6f48</c>) — <c>docs/synthetic-maps-port/analysis/20261019b-map21.md</c>.</para>
    /// </summary>
    [SkippableFact]
    public void Hire_PlayMatchesOriginalJournalAndSaves() => ReplayMatchesOriginal(HireMap);

    /// <summary>Та же сверка на партии карты 16 (19.10, <c>20261018b-port-synth</c>) — с клетками отрядов и гарнизонов и порядком склада,
    /// которых <see cref="Calm_PlayMatchesOriginalJournalAndSaves"/> не сверяет: проверка самой сверки карты 21 на сыгранной партии.</summary>
    [SkippableFact]
    public void Calm_LayoutAndStoreMatchOriginal()
    {
        Skip.IfNot(MapIds().Contains(CalmMap));
        var (journal, saves) = CalmSaves();
        Assert.Equal(CalmSaveCount, saves.Count);
        ReplayMatchesOriginal(CalmMap, journal, saves);
    }

    /// <summary>Партия карты <paramref name="map"/> портом с начала сценария против партии оригинала (<see cref="SynthRun"/>): сохранений
    /// не меньше дней памятки — иначе партия неполная, и сверять её нечем.</summary>
    private void ReplayMatchesOriginal(string map)
    {
        Skip.IfNot(MapIds().Contains(map));
        var (journal, saves) = SynthRun(map);
        int days = int.Parse(Str(Facts(map), "days"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(saves.Count >= days, $"{map}: сохранений {saves.Count}, дней памятки {days} — партия неполная");
        ReplayMatchesOriginal(map, journal, saves);
    }

    private void ReplayMatchesOriginal(string map, string journal, List<SaveCopy> saves)
    {
        long to = saves[^1].Seq;
        var events = JournalCache.Read(journal, ["ai_turn", "ai_tactic_pick", "ai_strategic_step", "ai_exec", "rod_drop", "battle_start",
                                                    "skill_pick"])
                                 .Where(e => e.Seq >= saves[0].Seq && e.Seq < to).OrderBy(e => e.Seq).ToList();
        Assert.Empty(events.OfType<BattleStartEvent>());
        var (enginePicks, engineSteps, engineRods) = CalmJournal(events);

        var session = Begin(map, 2);
        string human = Str(Facts(map).GetProperty("human"), "player");
        // Навыки героя человека дня 1 (SUGG_LVL 10) — до первого сохранения; они уже в журнале между загрузкой и t01.
        // Начало партии этой карты — последняя строка game_phase new до t01 (в журнале бывают и другие карты с теми же id отрядов).
        var head = JournalCache.Read(journal, ["skill_pick", "game_phase"]).Where(e => e.Seq < saves[0].Seq).OrderBy(e => e.Seq).ToList();
        long from = head.OfType<GamePhaseEvent>().LastOrDefault(g => g.What == "new")?.Seq ?? 0;
        var skills = new Queue<SkillPickEvent>(head.OfType<SkillPickEvent>()
            .Where(k => k.Seq > from && k.Ret && session.Map.Stacks.TryGetValue(k.Stack, out var st) && Same(st.Owner, human)));
        while (skills.Count > 0)
        {
            var k = skills.Dequeue();
            Assert.Equal(SkillRefusal.None, session.ChooseSkill(k.Stack, k.Modif));
        }
        var trace = new CalmTrace();
        session.AiTrace = trace;
        var seen = new Recorder();
        session.Watch(seen);
        var failed = new List<string>();
        var before = MapLooksLoader.Load(saves[0].Path);
        foreach (var save in saves)
        {
            while (session.Map.Turn < save.Turn && !session.Over) session.EndTurn();
            var engine = MapLooksLoader.Load(save.Path);
            var rows = CalmRows(before, engine, session);
            rows.AddRange(LayoutRows(engine.Map, session.Map));
            Out.WriteLine($"t{save.Turn:D2}: {(rows.Count == 0 ? "совпало" : string.Join("; ", rows))}");
            failed.AddRange(rows.Select(r => $"t{save.Turn:D2}: {r}"));
            before = engine;
        }
        Assert.Empty(seen.Outcomes);
        int picksSame = Matched(enginePicks, trace.Picks, "выбор", failed);
        int stepsSame = Matched(engineSteps, trace.Steps, "шаг", failed);
        var portRods = session.Map.Rods.Values.Select(r => (Up(r.Owner), r.X, r.Y)).ToHashSet();
        int rodsSame = engineRods.Count(r => portRods.Contains((Up(r.Player), r.X, r.Y)));
        if (rodsSame != engineRods.Count || portRods.Count != engineRods.Count)
            failed.Add($"жезлы: у движка {engineRods.Count}, у порта {portRods.Count}, совпало {rodsSame}");
        Out.WriteLine($"выборов {picksSame}/{enginePicks.Count} (порт {trace.Picks.Count}): " +
                      string.Join(", ", enginePicks.GroupBy(p => p.Action).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        Out.WriteLine($"шагов стратегии {stepsSame}/{engineSteps.Count} (порт {trace.Steps.Count}): " +
                      string.Join(", ", engineSteps.GroupBy(s => s.Type).Select(g => $"{g.Key} {g.Count()}")));
        Out.WriteLine($"жезлов {rodsSame}/{engineRods.Count}");
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed.Take(40)));
    }

    /// <summary>Клетки отрядов и гарнизонов (клетка → юнит) и порядок склада крепостей: сохранение движка против порта.</summary>
    private static List<string> LayoutRows(MapState engine, MapState port)
    {
        var rows = new List<string>();
        // Шесть клеток всегда: пустой гарнизон у порта — пустой список клеток, у движка — шесть пустых.
        static string Cells(IReadOnlyList<string> units, IReadOnlyList<int> slots) =>
            string.Join(",", Enumerable.Range(0, UnitCells.CellCount).Select(c => c < slots.Count ? slots[c] : -1)
                                       .Select(i => i >= 0 && i < units.Count ? units[i].ToUpperInvariant() : "-"));
        foreach (var s in engine.Stacks.Values)
        {
            if (!port.Stacks.TryGetValue(s.Id, out var mine)) continue;
            if (Cells(s.UnitIds, s.Slots) != Cells(mine.UnitIds, mine.Slots))
                rows.Add($"клетки {s.Id}: движок [{Cells(s.UnitIds, s.Slots)}], порт [{Cells(mine.UnitIds, mine.Slots)}]");
            // Порядок сумки отряда (мешок отдаёт предметы с конца, 0x5d6f48) — набор сверяет CalmRows, здесь — порядок.
            string bag = string.Join(",", s.Items.Select(i => i.ToUpperInvariant())), mineBag = string.Join(",", mine.Items.Select(i => i.ToUpperInvariant()));
            if (bag != mineBag) rows.Add($"сумка {s.Id}: движок [{bag}], порт [{mineBag}]");
        }
        foreach (var f in engine.Sites.Where(x => x.Kind is SiteKind.Capital or SiteKind.Village))
        {
            if (port.Sites.FirstOrDefault(x => Same(x.Id, f.Id)) is not { } mine) continue;
            if (Cells(f.Guards, f.GuardSlots) != Cells(mine.Guards, mine.GuardSlots))
                rows.Add($"гарнизон {f.Id}: движок [{Cells(f.Guards, f.GuardSlots)}], порт [{Cells(mine.Guards, mine.GuardSlots)}]");
            string store = string.Join(",", f.Items.Select(i => i.ToUpperInvariant())), ours = string.Join(",", mine.Items.Select(i => i.ToUpperInvariant()));
            if (store != ours) rows.Add($"склад {f.Id}: движок [{store}], порт [{ours}]");
        }
        return rows;
    }

    /// <summary>Журнал и сохранения партии оригинала синтетической карты <paramref name="map"/> в корпусе: каталог с сохранениями
    /// <c>&lt;карта&gt;-…-tNN-NNN.sg</c> (последний по имени), журнал — тот <c>passive-*.jsonl</c>, на оси которого они лежат
    /// (<see cref="CheckJournal"/>). Партии нет — пропуск.</summary>
    private static (string Journal, List<SaveCopy> Saves) SynthRun(string map)
    {
        string root = GameSaveTests.CorpusDir();
        Skip.IfNot(Directory.Exists(root), "нет корпуса");
        var dirs = Directory.GetDirectories(root).Where(d => Directory.EnumerateFiles(d, map + "-*.sg").Any())
                            .Order(StringComparer.Ordinal).ToList();
        Skip.If(dirs.Count == 0, $"партии оригинала карты {map} в корпусе нет");
        foreach (string journal in Directory.GetFiles(dirs[^1], "passive-*.jsonl").Order(StringComparer.Ordinal))
        {
            var saves = CheckJournal.Load(journal).Saves
                                    .Where(s => Path.GetFileName(s.Path).StartsWith(map + "-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (saves.Count >= 2) return (journal, saves);
        }
        Skip.If(true, $"{dirs[^1]}: сохранений карты {map} на оси журнала меньше двух");
        return default;
    }
}
