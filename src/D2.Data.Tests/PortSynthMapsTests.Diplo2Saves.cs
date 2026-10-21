using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 22 против журнала и сохранений оригинала (<c>corpus/20261019b-port-synth</c>, сохранения t01-022 … t12-033, зонд 7.5):
/// ходы ИИ каждого дня с сохранения того же дня — выбор тактики, счёт, статус; утро следующего дня — клетки отрядов.
/// Разбор — <c>docs/synthetic-maps-port/analysis/20261019b-map22.md</c>.</summary>
public partial class PortSynthMapsTests
{
    private const string Diplo2Run = "20261019b-port-synth";

    /// <summary>Сев карты 22 в журнале (<c>rng_seed</c> <c>new_game_time</c>, seq 4237361): до него — карты 19–21 той же S143.</summary>
    private const long Diplo2From = 4237300;

    private static string Diplo2Dir => Path.Combine(GameSaveTests.CorpusDir(), Diplo2Run);

    private static string Diplo2Journal => Path.Combine(Diplo2Dir, "passive-001.jsonl");

    private static readonly string[] Diplo2Kinds = ["save", "ai_turn", "ai_tactic_pick", "ai_tactic_result", "ai_strategic_step", "ai_exec", "id_alloc",
                                                    "battle_start", "battle_end", "stack_move", "rng", "ai_rng", "land_grow", "terrain_day",
                                                    "alliance_change", "trade_accept", "game_phase"];

    /// <summary>Ход ИИ дня у движка и у порта: выборы тактики со статусом и шаги стратегии по порядку, расхождения клеток утра;
    /// <paramref name="Fled"/> — защитники, бежавшие с поля у движка, и клетка, куда встали (<c>stack_move</c> с ходом 0 сразу за
    /// <c>battle_end</c>, И93); <paramref name="Blind"/> — день сразу после пропущенного сохранения: память стратегии порта не та, что у
    /// движка (сверять нельзя, только печать).</summary>
    private sealed record AiDay(int Day, List<string> Engine, List<string> Port, List<string> EngineSteps, List<string> PortSteps,
                                List<string> Morning, IReadOnlyDictionary<string, (int X, int Y)> Fled, bool Blind = false)
    {
        /// <summary>Расхождения состояния дня с журналом движка (<see cref="DayCheck"/>): бои, R250 у выборов и ходов, земля.</summary>
        public IReadOnlyList<string> Checks { get; init; } = [];

        /// <summary>Клетки отрядов и гарнизонов ИИ, порядок сумок и складов наутро против сохранения движка (расстановка <c>0x5d57de</c>
        /// и подготовка в миг хода, И33).</summary>
        public IReadOnlyList<string> Cells { get; init; } = [];

        /// <summary>Юниты групп ИИ наутро с другим типом, уровнем, здоровьем или опытом (И230; сверяется).</summary>
        public IReadOnlyList<string> UnitStates { get; init; } = [];
    }

    /// <summary>
    /// Ходы ИИ дней 1–11 карты 22 — каждый день с сохранения оригинала того же дня (t01-022 снят после ходов EV1/EV2, t02 … t11 — в
    /// начале дня): порт проводит конец хода человека и ходы трёх ИИ на R250 и CRT движка в миг конца хода (<see cref="JournalRng"/>,
    /// <see cref="JournalCrt"/>), с памятью тактики движка (статусы <c>ai_tactic_result</c> прошлых дней) и его счётчиками id; выбор за
    /// выбором (отряд, тип, цель, точка, счёт, статус) сверяется с <c>ai_tactic_pick</c>/<c>ai_tactic_result</c>, шаги стратегии — с
    /// <c>ai_exec</c>/<c>ai_strategic_step</c>, клетки отрядов — с сохранением следующего утра.
    /// <para>Ответ движка 19.10: 172 выбора, 74 шага; порт — 172/172 и 74/74. До 19.10 (<c>docs/synthetic-maps-port/analysis/20261019b-map22.md</c>)
    /// расходилось: NB — вор Легионов делал шпионаж вместо отравления (список действий не по возрастанию, <c>0x45bc14</c>); RD — снос жезла
    /// вторым выбором (у движка — тем же исполнением перемещения); смена приказа на себя — точка из позиции ИИ вместо клетки отряда
    /// (<c>0x450ddc</c>), у вора с выкупленной лавкой её не было (<c>0x601e55</c>), после неё помощники не назначались (<c>0x46726f</c>);
    /// Кланы нанимали вора без гильдии (<c>0x5d475e</c>); после разрыва союза Нежить держала Кланов разведанными (<c>0x5d91fd</c>).</para>
    /// <para>Клетки наутро — у всех отрядов как у движка: двое защитников, бежавших с поля (день 8 S143KC0013, день 10 S143KC0018),
    /// отходят на клетку прочь от нападающего (<c>stack_move</c> с ходом 0 сразу за <c>battle_end</c>; до 20.10 у порта отступления с
    /// карты не было — реестр И93).</para>
    /// </summary>
    [SkippableFact]
    public void Diplo2_AiTurnsMatchTheOriginalJournal()
    {
        Skip.IfNot(MapIds().Contains(Diplo2Map));
        Skip.IfNot(File.Exists(Diplo2Journal), $"нет {Diplo2Run}");
        var days = SavedAiDays(Diplo2Map, Diplo2Dir, Diplo2Journal, Diplo2From, 12);
        int same = 0, total = 0, stepsSame = 0, steps = 0;
        foreach (var d in days)
        {
            var lines = Aligned(d.Engine, d.Port);
            var stepLines = Aligned(d.EngineSteps, d.PortSteps);
            int ok = lines.Count(l => l.StartsWith('=')), okSteps = stepLines.Count(l => l.StartsWith('='));
            (same, total, stepsSame, steps) = (same + ok, total + d.Engine.Count, stepsSame + okSteps, steps + d.EngineSteps.Count);
            Out.WriteLine($"день {d.Day}: выборов движка {d.Engine.Count}, порта {d.Port.Count}, совпало {ok}; шагов стратегии движка " +
                          $"{d.EngineSteps.Count}, порта {d.PortSteps.Count}, совпало {okSteps}");
            foreach (var l in lines) Out.WriteLine("  " + l);
            foreach (var l in stepLines.Where(l => !l.StartsWith('='))) Out.WriteLine("  шаг " + l);
            foreach (var m in d.Morning) Out.WriteLine("  утро: " + m);
            foreach (var c in d.Checks) Out.WriteLine("  бой: " + c);
        }
        Out.WriteLine($"итого выборов {total}, совпало {same}; шагов стратегии {steps}, совпало {stepsSame}");
        Assert.Equal((172, 74), (total, steps));   // ответ движка — до сверки, чтобы смена корпуса падала понятно
        Assert.Equal((172, 74), (same, stepsSame));
        Assert.True(days.All(d => d.Port.Count == d.Engine.Count && d.PortSteps.Count == d.EngineSteps.Count), "лишние выборы или шаги у порта");
        // Бежавший защитник отходит на клетку прочь от нападающего (GameSession.RetreatFled, И93): день 8 S143KC0013 (81,117) → (80,117),
        // день 10 S143KC0018 (86,74) → (86,75) — клетки утра у всех отрядов как у движка (до 20.10 — эти две расходились).
        var morning = days.SelectMany(d => d.Morning.Select(m => $"день {d.Day}: {m}")).ToList();
        Assert.Empty(morning);
    }

    /// <summary>Дни партии с сохранений оригинала (карты 21–23 и любая партия корпуса с Q в начале каждого дня —
    /// <see cref="DailySaveAiTurnsHarnessTests"/>): сессия с последнего сохранения дня, R250 и CRT движка, конец хода; выборы порта и
    /// движка, клетки утра. <paramref name="map"/> — начало имени сохранений партии (карта, у штатной — и раса человека); журнал — с
    /// <paramref name="from"/> (вход в партию этой карты), сохранений нужно не меньше <paramref name="minSaves"/>.</summary>
    private List<AiDay> SavedAiDays(string map, string dir, string journal, long from, int minSaves)
    {
        var events = JournalCache.Read(journal, Diplo2Kinds).Where(e => e.Seq >= from).OrderBy(e => e.Seq).ToList();
        var saves = AiStrategicOrdersHarnessTests.BoundSaves(dir, Path.GetFileName(journal), events.OfType<SaveEvent>().ToList())
                                                 .Where(b => Path.GetFileName(b.Path).StartsWith(map, StringComparison.Ordinal))
                                                 .GroupBy(b => b.Save.Turn).Select(g => g.OrderBy(b => b.Save.Seq).Last())
                                                 .OrderBy(b => b.Save.Seq).ToList();
        Skip.If(saves.Count < minSaves, $"сохранений карты {map}: {saves.Count}");
        // Человек — из сохранения (IS_HUMAN игрока), а не из facts.json синтетической карты: так же идут партии штатных карт корпуса.
        var first = MapLooksLoader.Load(saves[0].Path).Map;
        var humans = first.Players.Values.Where(p => p.IsHuman).Select(p => p.Id).ToList();
        // Счётчики id с входа в партию: движок считает их по объектам только при загрузке (0x5ee996), дальше лишь растит выдачей
        // (0x5ee8bd) — номер объекта, бывшего на карте с входа и снятого до сохранения дня, у движка занят, а пересчёт по сохранению
        // отдаёт его снова. Карта 27 (20261020-replay10): отряд S143KC0029 есть в сохранениях дня 1 и пал в ходе ИИ того же дня (бой S143KC0028 × S143KC0029, seq 775403); наём Империи
        // дня 2 у движка — S143KC002a (id_alloc seq 790574), у порта был KC0029 и все номера дня — на один меньше (И213, харнесс).
        var entryIds = new Dictionary<string, int>(first.IdNext, StringComparer.OrdinalIgnoreCase);
        long entered = from;
        // Броски R250 движка: строки rng (бои) и ai_rng (схема 7.5, номер вызова n — счётчик после броска): до первого боя карты
        // строк rng нет, опоры дня 1 — броски дипломатии и вора.
        var draws = JournalCache.Read(journal, ["ai_rng"]).OfType<AiRngEvent>().Where(r => r.Kind == "r250" && r.Seq >= from)
                                .Select(r => new RngEvent(r.Seq, r.Tid, r.N, r.Max, r.R, false));
        var rng = new JournalRng(JournalCache.Read(journal, ["rng_seed", "rng"]), draws);
        // CRT rand() сервера (тип юнита найма в гарнизон, имя лидера) — поток движка в миг конца хода (JournalCrt).
        var crt = JournalCrt.Of(journal);
        // Порождённые реализации, уже созданные движком в этом запуске (GeneratedImpls, И230): id G000UG… из строк xp и upgrade_target.
        var impls = JournalCache.Read(journal, ["xp", "upgrade_target"]).Where(e => e.Seq >= from)
                                .SelectMany(e => (e switch
                                {
                                    XpEvent x => [x.Impl],
                                    UpgradeTargetEvent u => [u.Type, u.Target],
                                    _ => Array.Empty<string?>(),
                                }).OfType<string>().Where(id => id.StartsWith("G000UG", StringComparison.OrdinalIgnoreCase))
                                  .Select(id => (e.Seq, Id: id)))
                                .ToList();
        var tactic = new List<AiTacticRecord>();
        var memory = (IReadOnlyDictionary<string, AiMemory>?)null;
        bool blind = false;
        var result = new List<AiDay>();
        for (int k = 0; k + 1 < saves.Count; k++)
        {
            long start = saves[k].Save.Seq, to = saves[k + 1].Save.Seq;
            var slice = events.Where(e => e.Seq > start && e.Seq < to).ToList();
            if (saves[k + 1].Save.Turn != saves[k].Save.Turn + 1)
            {
                // Нет сохранения следующего утра (пропущенный Q): между сохранениями не один день — такой отрезок не сверяется. Память
                // тактики движка — из журнала, а памяти стратегии за пропущенное нет: следующий день идёт с пустой и не сверяется (Blind).
                Out.WriteLine($"день {saves[k].Save.Turn}: следующее сохранение — день {saves[k + 1].Save.Turn}, отрезок не сверяется");
                tactic.AddRange(TacticRecords(slice));
                (memory, blind) = (null, true);
                continue;
            }
            var end = slice.OfType<AiTurnEvent>().First(t => t is { Phase: "end", Cause: "human" });
            var engine = EnginePicks(slice);
            // День 1 карты 23: до конца хода человека бросков в журнале нет — сдвиг по броскам после.
            var g = rng.AtAhead(end.Seq, end.RngBefore + 1) ?? rng.At(end.Seq, end.RngBefore + 1);
            if (g is null) Out.WriteLine($"день {saves[k].Save.Turn}: R250 движка не подобран");
            var random = g is null ? null : new RandomStreams(g.Index, g.TableSnapshot(), crt.Before(end.Tid, end.Seq));
            // Счётчики id движка в живой партии (id_alloc: номера погибших не переиспользуются, 0x5ee8bd), а не пересчёт по объектам
            // после загрузки (0x5ee996): иначе вор, нанятый Нежитью в день 11 взамен павшего в дуэли дня 10, у порта — KC001b, у движка — KC001c.
            if (events.OfType<GamePhaseEvent>().LastOrDefault(e => e.IsEnter && e.Seq > entered && e.Seq < start) is { } reload)
                (entered, entryIds) = (reload.Seq, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));   // загрузка — пересчёт заново
            var ids = events.OfType<IdAllocEvent>().Where(a => a.Seq > entered && a.Seq < start && a is { Id.Length: 10, Index: not null })
                            .GroupBy(a => a.Id![4..6].ToUpperInvariant()).ToDictionary(g => g.Key, g => (int)g.Max(a => a.Index!.Value) + 1,
                                                                                        StringComparer.OrdinalIgnoreCase);
            foreach (var (kind, n) in entryIds) ids[kind] = Math.Max(ids.GetValueOrDefault(kind), n);
            var generated = impls.Where(i => i.Seq > entered && i.Seq < start).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var session = SavedDaySession(map, saves[k].Path, humans, random, tactic, memory, ids, generated);
            var trace = new Diplo2Trace();
            var sync = new DayCheck(session, rng, slice);
            session.AiTrace = new CheckedTrace(trace, sync);
            session.Observer = sync;
            DayReport report;
            try { report = session.EndTurn(); }
            finally { session.Observer = null; }
            // Отладка дня: D2_DAY_SHOW=<день> печатает события дня порта (ход ИИ, бои, заклинания).
            if (Environment.GetEnvironmentVariable("D2_DAY_SHOW") == saves[k].Save.Turn.ToString(System.Globalization.CultureInfo.InvariantCulture))
                foreach (var l in report.Events) Out.WriteLine("  порт: " + l);
            var checks = sync.Notes.Concat(sync.Remaining).ToList();
            var statuses = report.Events.Where(l => l.StartsWith("тактика ", StringComparison.Ordinal))
                                        .Select(l => l[(l.LastIndexOf('→') + 2)..]).ToList();
            var port = trace.Picks.Select((p, i) => $"{p} → {(i < statuses.Count ? statuses[i] : "?")}").ToList();
            var morning = new List<string>();
            var next = MapLooksLoader.Load(saves[k + 1].Path).Map;
            foreach (var (kind, n) in next.IdNext) entryIds[kind] = Math.Max(entryIds.GetValueOrDefault(kind), n);
            foreach (var s in next.Stacks.Values)
                if (!session.Map.Stacks.TryGetValue(s.Id, out var mine)) morning.Add($"у порта нет {s.Id}");
                else if ((mine.X, mine.Y, mine.Inside) != (s.X, s.Y, s.Inside))
                    morning.Add($"{s.Id} движок ({s.X},{s.Y}) {s.Inside}, порт ({mine.X},{mine.Y}) {mine.Inside}");
            foreach (var s in session.Map.Stacks.Values.Where(s => !next.Stacks.ContainsKey(s.Id))) morning.Add($"у движка нет {s.Id}");
            result.Add(new AiDay((int)saves[k].Save.Turn, engine, port, EngineSteps(slice), trace.Steps, morning, FledDefenders(slice), blind)
                       { Checks = checks, Cells = CellRows(next, session.Map, humans), UnitStates = UnitRows(next, session.Map, humans) });
            blind = false;
            // Память тактики движка (статусы дня — «вчера продвинулся» ×1,25) — из строк ai_tactic_result; память стратегии — порта.
            tactic.AddRange(TacticRecords(slice));
            memory = session.State.Ai;
        }
        return result;
    }

    /// <summary>Группы ИИ наутро (не человека: его ход порт не играет) — сохранение движка против порта: клетки отрядов и гарнизонов,
    /// порядок сумок и складов (<see cref="LayoutRows"/>). Сверяет подготовку и расстановку (<c>0x5d57de</c>) в миг хода — то, что
    /// харнессы по паре сохранений (<c>AiGroupArrangeHarnessTests</c>, <c>AiStackItemsHarnessTests</c>) берут из сохранения следующего
    /// дня (И33).</summary>
    private static List<string> CellRows(MapState engine, MapState port, IReadOnlyList<string> humans)
    {
        var human = HumanGroups(engine, humans);
        return [.. LayoutRows(engine, port).Where(r => !human.Contains(r.Split(' ')[1].TrimEnd(':')))];
    }

    /// <summary>Юниты групп ИИ наутро: тип, уровень, здоровье и опыт — сохранение движка против порта (И230: сверяется, 0 во всех
    /// посуточных партиях).</summary>
    private static List<string> UnitRows(MapState engine, MapState port, IReadOnlyList<string> humans)
    {
        var human = HumanGroups(engine, humans);
        var rows = new List<string>();
        var units = engine.Stacks.Values.Where(s => !human.Contains(s.Id)).SelectMany(s => s.UnitIds)
                          .Concat(engine.Sites.Where(f => f.Owner.Length > 0 && !human.Contains(f.Id)).SelectMany(f => f.Guards));
        foreach (var id in units.Distinct(StringComparer.OrdinalIgnoreCase))
            if (engine.Units.TryGetValue(id, out var e) && port.Units.TryGetValue(id, out var m)
                && (e.TypeId.ToUpperInvariant(), e.Level, e.Hp, e.Xp) != (m.TypeId.ToUpperInvariant(), m.Level, m.Hp, m.Xp))
                rows.Add($"{id}: движок {e.TypeId} ур {e.Level} {e.Hp} HP, опыт {e.Xp}; порт {m.TypeId} ур {m.Level} {m.Hp} HP, опыт {m.Xp}");
        return rows;
    }

    private static HashSet<string> HumanGroups(MapState engine, IReadOnlyList<string> humans)
    {
        bool Human(string? owner) => humans.Any(h => string.Equals(h, owner, StringComparison.OrdinalIgnoreCase));
        return engine.Stacks.Values.Where(s => Human(s.Owner)).Select(s => s.Id)
                     .Concat(engine.Sites.Where(f => Human(f.Owner)).Select(f => f.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Память тактики движка (статусы исполнения — «вчера продвинулся» ×1,25) из строк <c>ai_tactic_result</c> отрезка.</summary>
    private static IEnumerable<AiTacticRecord> TacticRecords(IEnumerable<CorpusEvent> slice) =>
        slice.OfType<AiTacticResultEvent>().Where(r => r is { Stack: not null, Target: not null, Point: not null, Turn: not null })
             .Select(r => new AiTacticRecord(r.Stack!, r.Target!, (int)r.Point!.X, (int)r.Point.Y, (int)r.Turn!.Value, (int)r.Status));

    /// <summary>Партия с сохранения оригинала: события сценария, генератор, память тактики и стратегии прошлых дней, счётчики id.</summary>
    /// <param name="generated">Порождённые реализации, созданные движком в этом запуске до сохранения (<see cref="GameState.GeneratedUnits"/>).</param>
    private static GameSession SavedDaySession(string map, string sg, IReadOnlyList<string> humans, RandomStreams? random, IReadOnlyList<AiTacticRecord> tactic,
                                             IReadOnlyDictionary<string, AiMemory>? memory, IReadOnlyDictionary<string, int> idNext,
                                             IReadOnlySet<string>? generated = null)
    {
        var looks = MapLooksLoader.Load(sg);
        var counters = looks.Map.IdNext.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, next) in idNext) counters[kind] = Math.Max(counters.GetValueOrDefault(kind), next);
        looks = GameSession.Retold(looks, GameSession.With(looks.Map, idNext: counters), looks.Stacks);
        var events = EventLoader.Load(sg);
        // Сложность и уровень сценария — из сохранения (DIFFGAME, SUGG_LVL): карта 22 — средняя, карта 23 — очень трудная.
        var limits = looks.Map.Limits;
        var state = new GameState(new GameInfo(map, map, humans, limits?.DifficultyGame ?? 1, limits?.SuggestedLevel ?? 1), looks)
        {
            Events = events, EventProgress = EventLoader.LoadProgress(sg, events), Random = random, Tactic = [.. tactic],
            Ai = memory ?? new Dictionary<string, AiMemory>(), GeneratedUnits = generated ?? new HashSet<string>(),
        };
        return new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book) { EventBattlesOnScreen = false };
    }

    /// <summary>Защитники, бежавшие с поля у движка: первая строка <c>stack_move</c> после <c>battle_end</c> — защитник того же боя с ходом 0
    /// (карта 22: день 8 <c>S143KC0013</c> (81,117) → (80,117), seq 5522152; И93).</summary>
    private static Dictionary<string, (int X, int Y)> FledDefenders(IEnumerable<CorpusEvent> events)
    {
        var fled = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase);
        string? defender = null;
        bool ended = false;
        foreach (var e in events)
            switch (e)
            {
                case BattleStartEvent b: (defender, ended) = (b.DefenderGroup, false); break;
                case BattleEndEvent: ended = true; break;
                case StackMoveEvent m when ended && defender is not null:
                    if (m.MoveLeft == 0 && Same(m.Stack, defender)) fled[m.Stack] = ((int)m.X, (int)m.Y);
                    (defender, ended) = (null, false);
                    break;
            }
        return fled;
    }

    /// <summary>Два списка выборов, выровненные по наибольшей общей подпоследовательности: «=» — у обоих, «−» — только у движка, «+» —
    /// только у порта (лишний или недостающий выбор не сдвигает сверку остальных).</summary>
    private static List<string> Aligned(IReadOnlyList<string> engine, IReadOnlyList<string> port)
    {
        var lcs = new int[engine.Count + 1, port.Count + 1];
        for (int i = engine.Count - 1; i >= 0; i--)
            for (int j = port.Count - 1; j >= 0; j--)
                lcs[i, j] = engine[i] == port[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var lines = new List<string>();
        int a = 0, b = 0;
        while (a < engine.Count || b < port.Count)
        {
            if (a < engine.Count && b < port.Count && engine[a] == port[b]) { lines.Add("= " + engine[a]); a++; b++; }
            else if (b >= port.Count || (a < engine.Count && lcs[a + 1, b] >= lcs[a, b + 1])) lines.Add("− движок " + engine[a++]);
            else lines.Add("+ порт   " + port[b++]);
        }
        return lines;
    }

    /// <summary>Выборы движка в отрезке журнала: фаза 4 → 0 и статус следующей строки <c>ai_tactic_result</c>.</summary>
    private static List<string> EnginePicks(IEnumerable<CorpusEvent> events)
    {
        var picks = new List<string>();
        string? pending = null;
        foreach (var e in events)
        {
            if (e is AiTacticPickEvent { PhaseBefore: 4, Chosen: [{ Action: { } a } c, ..] } p)
            {
                // Смена приказа (CAiChangeOrderAction, vft 0x6cfbec) — зонд читает слот типа как 0x1b; счёт — как у трассы порта (до сотых).
                var type = a.Vft == "0x6cfbec" ? AiActionType.ChangeOrder : (AiActionType)(a.Type ?? -1);
                pending = Diplo2Trace.Pick(p.Player ?? "", a.Stack ?? "", type, a.Target ?? "", (int)(a.Point?.X ?? -1), (int)(a.Point?.Y ?? -1), c.Score ?? 0);
            }
            else if (e is AiTacticResultEvent r && pending is not null)
            {
                picks.Add($"{pending} → {r.Status}");
                pending = null;
            }
        }
        return picks;
    }

    /// <summary>Шаги стратегии движка в отрезке журнала: <c>ai_exec</c> с успехом и следом <c>ai_strategic_step</c> — игрок, день, проход,
    /// тип и цель (найм — крепость и тип, дипломатия — игрок, прочие — объект <c>id8</c>).</summary>
    private static List<string> EngineSteps(IEnumerable<CorpusEvent> events)
    {
        var steps = new List<string>();
        var day = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AiExecEvent? exec = null;
        foreach (var e in events)
            switch (e)
            {
                case AiTurnEvent { Phase: "begin", Player: { } p } t: day[p] = (int)t.Turn; break;
                case AiExecEvent { Ret: true } x: exec = x; break;
                case AiStrategicStepEvent { Player: { } p } s when exec is { Action: { } act }:
                    string F(string k) => act.Fields.TryGetValue(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : "";
                    var type = (AiStepType)s.Type;
                    string target = type switch
                    {
                        AiStepType.HireUnit or AiStepType.HireLeader => $"{F("id8")} {F("id12")}",
                        AiStepType.Diplomacy => F("id4"),
                        // Уход (0x449f7b): место (отряд в крепости или крепость) и юнит — по шагу на юнита.
                        AiStepType.HealRevive => $"{F("id8")} {F("id12")}",
                        _ => F("id8"),
                    };
                    steps.Add(Diplo2Trace.Step(p, day.GetValueOrDefault(p), (int)exec.Pass, type, target));
                    exec = null;
                    break;
            }
        return steps;
    }

    /// <summary>Выборы тактики и шаги стратегии порта строками в форме журнала движка (id — заглавными: движок пишет шестнадцатеричные
    /// знаки id как в сохранении, порт — как в своей карте).</summary>
    private sealed class Diplo2Trace : IAiTrace
    {
        public List<string> Picks { get; } = [];

        public List<string> Steps { get; } = [];

        public static string Pick(string player, string stack, AiActionType type, string target, int x, int y, double score) =>
            $"{Up(player)} {Up(stack)} {type} {Up(target)} ({x},{y}) {Math.Round(score, 2):0.##}";

        public static string Step(string player, int day, int pass, AiStepType type, string target) =>
            $"{Up(player)} день {day} проход {pass} {type} {target.ToUpperInvariant()}";

        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates) =>
            Picks.Add(Pick(playerId, action.KeyStack, action.Type, action.Target, action.X, action.Y, score));

        public void Stepped(string playerId, int day, AiExecuted step, int number)
        {
            var a = step.Action;
            string target = a.Type switch
            {
                AiStepType.HireUnit or AiStepType.HireLeader => $"{a.Id} {a.Target.Split('|')[0]}",
                AiStepType.Diplomacy => a.Id,
                AiStepType.HealRevive => $"{a.Id} {a.Target}",
                _ => a.Target.Split('|')[0],
            };
            Steps.Add(Step(playerId, day, step.Pass, a.Type, target));
        }

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) { }
    }
}
