using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Dbf;
using D2.Data.Magic;
using D2.Data.Map;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Rng;
using D2.Rules.Units;

namespace D2.Rules.Tests;

/// <summary>Вторая половина харнесса зонда 6.5 (<see cref="AiStrategicHarnessTests"/>): найм юнита, порядок городов,
/// постройка столицы, бюджет и исследование. Общее — в <c>AiStrategicHarnessTests.cs</c>.</summary>
[Trait("Category", "Corpus")]
public partial class AiStrategicHarnessTests
{
    // ── Найм юнита ─────────────────────────────────────────────────────────────────────────

    /// <summary>Поток CRT <c>rand()</c> логического потока сессии: выходы по порядку <c>seq</c> и состояние после каждого.</summary>
    private sealed class CrtStream
    {
        public required List<CrtRandEvent> Calls { get; init; }
        public required List<uint> StateAfter { get; init; }
        public required uint Seed { get; init; }

        /// <summary>Индекс последнего вызова с <c>seq</c> меньше данного; −1 — такого нет.</summary>
        public int LastBefore(long seq)
        {
            int lo = 0, hi = Calls.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Calls[mid].Seq < seq) lo = mid + 1; else hi = mid;
            }
            return lo - 1;
        }

        public uint StateBefore(long seq) => LastBefore(seq) is var i and >= 0 ? StateAfter[i] : Seed;
    }

    private static CrtStream Stream(List<CorpusEvent> events, long tid, Counters c)
    {
        var calls = events.OfType<CrtRandEvent>().Where(e => e.Tid == tid).ToList();
        if (calls.Count >= 2) Fit(calls, tid, c);
        var rand = new CrtRand(1);
        var after = new List<uint>();
        foreach (var x in calls)
        {
            int r = rand.Next();
            c.Count("crt.stream", r == x.R, $"tid {tid} seq {x.Seq}: журнал {x.R}, модель {r}");
            after.Add(rand.State);
        }
        return new CrtStream { Calls = calls, StateAfter = after, Seed = 1 };
    }

    /// <summary>Справка: сколько состояний CRT дают первые выходы и сходится ли <c>srand(1)</c>.</summary>
    private static void Fit(List<CrtRandEvent> calls, long tid, Counters c)
    {
        var fits = CrtRand.FitAfterTwo((int)calls[0].R, (int)calls[1].R)
            .Where(s =>
            {
                var r = new CrtRand(s);
                return calls.Skip(2).Take(30).All(x => r.Next() == x.R);
            }).ToList();
        Assert.True(fits.Count > 0, "состояние CRT не восстановлено");
        var one = new CrtRand(1);
        bool fromOne = one.Next() == calls[0].R && one.Next() == calls[1].R;
        c.Note($"CRT tid {tid}: вызовов {calls.Count}, первые {calls[0].R}, {calls[1].R}; подобрано состояний {fits.Count}; " +
               $"srand(1) (msvcrt без srand) {(fromOne ? "сходится" : "НЕ сходится")}");
    }

    private sealed record Garrison(string?[] Cells, Dictionary<string, string> Types)
    {
        public IEnumerable<string> Units => Cells.OfType<string>().Distinct();

        /// <summary>Клетки в виде <see cref="MapStack.Slots"/>: занята — 0, пуста — −1.</summary>
        public IReadOnlyList<int> Slots => [.. Cells.Select(x => x is null ? -1 : 0)];
    }

    /// <summary>
    /// 2. Выбор юнита найма (<c>0x44bec7</c>/<c>0x44c06a</c>): поток CRT <c>rand()</c> от <c>srand(1)</c> сходится с каждым
    /// <c>crt_rand</c> логического потока; <c>ai_rng unit_pick</c> — это следующий <c>rand()</c> и <c>r = raw % max</c>.
    /// Гарнизон — сохранение дня плюс наймы хода до выборки; пул — <see cref="Economy.HireList"/> (порядок <c>Grace</c>) по
    /// фильтру и <see cref="AiStrategicRules.Placeable"/>; длина пула — <c>max</c>, выбранный — <c>result</c>. Выборка целиком
    /// (<see cref="AiStrategicRules.PickUnits"/> с <see cref="AiStrategicRules.CrtPicker"/> от состояния потока) даёт те же
    /// юниты в том же порядке и тратит столько же <c>rand()</c>, а мест — столько, сколько вызовов «любой» плюс удачных
    /// «боец»/«большой».
    /// </summary>
    [SkippableFact]
    public void UnitPick_ReproducesCrtStreamPoolsAndSelection()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var econ = EconomyLoader.Load(GamePaths.Globals);
        var rules = RulesLoader.Load(GamePaths.Globals);
        var all = new Dictionary<AiSession, Counters>();
        string[] required = [];
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            long tid = session.Tid;
            var stream = Stream(events, tid, c);
            foreach (var r in events.OfType<AiRngEvent>().Where(r => r.Site == "unit_pick"))
            {
                // Строка ai_rng берёт seq до вызова обёртки, crt_rand — внутри: сырой бросок — первый crt_rand после неё.
                int i = stream.LastBefore(r.Seq) + 1;
                c.Count("unit_pick.raw_is_next_rand", i < stream.Calls.Count && stream.Calls[i].R == r.Raw && r.Tid == tid,
                        $"seq {r.Seq}: raw {r.Raw}, crt_rand {(i < stream.Calls.Count ? stream.Calls[i].R : -1)}");
                c.Count("unit_pick.r_is_raw_mod_max", r.Raw % r.Max == r.R, $"seq {r.Seq}: {r.Raw} % {r.Max} ≠ {r.R}");
            }

            var turns = events.OfType<AiTurnEvent>().Where(t => t.Phase == "begin").ToList();
            foreach (var group in events.OfType<AiUnitPickEvent>().GroupBy(p => (p.Player, p.Pass, p.Id, Day: DayOf(turns, p.Seq))))
                CheckHireGroup(c, session, events, stream, [.. group], group.Key.Day, econ, rules);
            c.Print(output, $"── {session.Name}: выбор юнита найма");
            required = ["crt.stream", "unit_pick.raw_is_next_rand", "unit_pick.r_is_raw_mod_max", "unit_pick.list_len",
                      "unit_pick.pool", "unit_pick.result", "unit_pick.free_slots", "unit_pick.selection", "unit_pick.rand_count"];
        }
        AiCorpus.RequireEach(all, required);
    }

    private static int DayOf(List<AiTurnEvent> turnBegins, long seq) =>
        (int)(turnBegins.LastOrDefault(t => t.Seq < seq)?.Turn ?? -1);

    private static void CheckHireGroup(Counters c, AiSession s, List<CorpusEvent> events, CrtStream stream,
                                       List<AiUnitPickEvent> group, int day, EconomyRules econ, GameRules rules)
    {
        var first = group[0];
        string player = first.Player!, fortId = first.Id!;
        string where = $"{player} день {day} проход {first.Pass} {fortId}";
        if (SaveOf(s, day, first.Seq) is not { } save) { c.Skip("unit_pick", "нет сохранения дня"); return; }
        var window = events.Where(e => e.Seq > save.Cut && e.Seq < first.Seq).ToList();
        if (window.Any(e => e is UnitTransferEvent x && (Same(x.From, fortId) || Same(x.To, fortId))
                            || e is FortCaptureEvent f && Same(f.Fort, fortId)))
        {
            c.Skip("unit_pick", "перенос юнитов или захват укрепления между сохранением и выборкой");
            return;
        }
        var (map, forts) = Open(save.Path);
        if (!forts.TryGetValue(fortId, out var fort)) { c.Skip("unit_pick", "укрепления нет в сохранении"); return; }
        if (GarrisonOf(fort, map, window, rules) is not { } g) { c.Skip("unit_pick", "тип юнита гарнизона не опознан"); return; }
        if (GarrisonOf(fort, map, [], rules) is { } saved && Rearranged(events, saved, fortId, save.Cut, first.Seq) is { } moved)
        {
            c.Skip("unit_pick", "гарнизон переставлен ИИ после снимка (расстановка начала стратегии 0x44edee): наём в занятую по снимку клетку");
            c.Note($"   пропуск {where}: наём в клетку {moved}, по снимку занятую");
            return;
        }
        var state = events.OfType<FortDayEvent>().Where(f => f.Seq < first.Seq)
            .SelectMany(f => f.Forts).LastOrDefault(f => Same(f.Id, fortId));
        if (state is null) { c.Skip("unit_pick", "нет fort_day с укреплением"); return; }
        // Предел: деревня — уровень (с ростом в этот же день до выборки), столица — capacity.
        int grown = window.OfType<CityGrowEvent>().Count(x => x.Ret && Same(x.Fort, fortId));
        int limit = state.Size > 0 ? Fortifications.GarrisonCapacity((int)state.Size + grown) : (int)state.Capacity;
        int free = Math.Max(limit - g.Cells.Count(x => x is not null), 0);
        string shape = $"клетки [{string.Join(",", g.Cells.Select(x => x is null ? "_" : x.StartsWith("hire@", StringComparison.Ordinal) ? "h" : "u"))}], " +
                       $"size {state.Size}+{grown} tier {state.Tier} capacity {state.Capacity}";

        var built = window.OfType<BuildEvent>().Where(b => b.Ret && Same(b.Player, player)).Select(b => b.Building);
        var list = Economy.HireList(WithBuildings(map, player, built), player, econ);
        c.Count("unit_pick.list_len", list.Count == first.ListLen, $"{where}: список журнал {first.ListLen}, порт {list.Count}");
        var options = list.Select(id => (Id: id, Impl: rules.ImplOf(id)!))
            .Select(u => new AiHireOption(u.Id, SmallMelee(u.Impl), !u.Impl.Type.SizeSmall))
            .Where(o => AiStrategicRules.Placeable(g.Slots, free, o.Big)).ToList();

        foreach (var p in group)
        {
            List<AiHireOption> pool = p.Filter switch
            {
                "melee_small" => options.Where(o => o.SmallMelee).ToList(),
                "big" => options.Where(o => o.Big).ToList(),
                _ => options,
            };
            var roll = events.OfType<AiRngEvent>()
                .FirstOrDefault(r => r.Site == "unit_pick" && r.Seq > p.Seq && r.Seq <= p.SeqEnd);
            c.Count("unit_pick.pool", pool.Count == (roll?.Max ?? 0),
                    $"{where} {p.Filter}: пул порта {pool.Count} [{string.Join(",", pool.Select(o => o.UnitId))}], max {roll?.Max}; {shape}");
            string ours = roll is not null && roll.R < pool.Count ? pool[(int)roll.R].UnitId : EmptyId;
            c.Count("unit_pick.result", Same(ours, p.Result ?? EmptyId), $"{where} {p.Filter}: журнал {p.Result}, порт {ours}");
        }

        var units = g.Units.Select(u => rules.ImplOf(g.Types[u])!).ToList();
        int anyCalls = group.Count(p => p.Filter == "any");
        int found = group.Count(p => p.Filter != "any" && p.Result is not null);
        c.Count("unit_pick.free_slots", free == anyCalls + found,
                $"{where}: мест порта {free} (предел {limit}, юнитов {units.Count}), вызовов «любой» {anyCalls} + удачных {found}; {shape}");

        var picker = AiStrategicRules.CrtPicker(new CrtRand(stream.StateBefore(first.Seq)));
        int consumed = 0;
        var picked = AiStrategicRules.PickUnits(free, units.Any(SmallMelee), units.Any(u => !u.Type.SizeSmall), options,
            pool =>
            {
                consumed++;
                return picker(pool);
            });
        var logged = group.Where(p => p.Result is not null).Select(p => p.Result!).ToList();
        c.Count("unit_pick.selection", picked.Select(o => o.UnitId).SequenceEqual(logged, StringComparer.OrdinalIgnoreCase),
                $"{where}: журнал {string.Join(",", logged)}, порт {string.Join(",", picked.Select(o => o.UnitId))}");
        int calls = stream.LastBefore(group[^1].SeqEnd + 1) - stream.LastBefore(first.Seq);
        c.Count("unit_pick.rand_count", consumed == calls, $"{where}: rand() журнал {calls}, порт {consumed}");
    }

    private static bool SmallMelee(UnitImpl u) =>
        u.Type.SizeSmall && u.Attack is { Reach: AttackReach.Adjacent, Class: AttackClass.Damage };

    /// <summary>Клетка, куда ИИ нанял юнита в этом же ходу, хотя по снимку она занята, — значит, расстановка
    /// гарнизона в начале стратегии (<c>0x44edee</c> → <c>0x5d57de</c>, в порте <see cref="D2.Data.Play.AiStackUnits.Arrange"/>)
    /// после снимка переложила юнитов, и клетки снимка для выборки не годятся. Строк перестановки внутри группы зонд не пишет.
    /// Пример — <c>20261016b-port-synth</c>, карта 2, день 3: у всех трёх ИИ наём в клетку 0, где по снимку стоит вчерашний наём
    /// (в снимке дня 4 он уже в клетке 2). Наймы — от снимка до конца хода ИИ. Нет такого найма — null.</summary>
    private static int? Rearranged(List<CorpusEvent> events, Garrison g, string fortId, long from, long pick)
    {
        long end = events.OfType<AiTurnEvent>().Where(t => t.Phase == "begin" && t.Seq > pick).Select(t => t.Seq)
                         .DefaultIfEmpty(long.MaxValue).Min();
        foreach (var h in events.OfType<HireUnitEvent>().Where(h => h.Ret && Same(h.Group, fortId) && h.Seq > from && h.Seq < end))
        {
            int pos = (int)h.Position;
            if (pos >= 0 && pos < g.Cells.Length && g.Cells[pos] is { } at && !at.StartsWith("hire@", StringComparison.Ordinal))
                return pos;
        }
        return null;
    }

    /// <summary>Гарнизон на момент выборки: сохранение плюс <c>hire_unit</c> в эту группу (большой — колонка).</summary>
    private static Garrison? GarrisonOf(Fort fort, MapState map, List<CorpusEvent> window, GameRules rules)
    {
        var cells = new string?[6];
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < cells.Length && k < fort.Positions.Count; k++)
        {
            if (fort.Positions[k] < 0) continue;
            string unit = fort.Units[fort.Positions[k]];
            if (!map.Units.TryGetValue(unit, out var u)) return null;
            cells[k] = unit;
            types[unit] = u.TypeId;
        }
        foreach (var h in window.OfType<HireUnitEvent>().Where(h => h.Ret && Same(h.Group, fort.Id)))
        {
            if (rules.ImplOf(h.UnitType) is not { } impl) return null;
            string id = $"hire@{h.Seq}";
            types[id] = h.UnitType;
            int pos = (int)h.Position;
            cells[pos] = id;
            if (!impl.Type.SizeSmall) cells[pos ^ 1] = id;
        }
        return types.Values.All(t => rules.ImplOf(t) is not null) ? new Garrison(cells, types) : null;
    }

    // ── Порядок городов ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 5. Порядок городов <c>0x44809c</c>: приоритеты по списку не растут и кончаются нулём, у роста и найма лидера в одном
    /// проходе список один. Сила города <c>0x5dcec7</c> по сохранению дня (<see cref="Strength"/>) даёт <b>сам приоритет</b>
    /// журнала, а не только порядок: вызов из <c>0x448147</c> и <c>0x4481a2</c> идёт с числом врагов 3, «без города» = 0 и
    /// бронёй укрепления <c>GVars.PROT_*</c> (<see cref="AiStrength.Unit"/>), приоритет — усечение суммы
    /// (<c>0x66c000</c> в <c>0x4481d0</c>), <c>max − v</c> по усечённым.
    /// <para>Сверяется каждая строка каждого списка; требование предъявляется там, где состав гарнизона между
    /// снимком и чтением заведомо не менялся (<see cref="Frozen"/>) — допуск 0,5 % силы сильнейшего города. Точные
    /// совпадения печатаются отдельной строкой: в корпусе 18.09 (ai-q09 зонда 6.5) это 113 из 137 по всем спискам и
    /// 35 из 37 по «замороженным» (до потери корпуса — 143 из 166 и 43 из 47, все расхождения ниже 0,4 %); в партии
    /// человека «замороженных» списков нет — сохранение там снято до его же боёв и найма. Ноль без допуска
    /// недостижим по корпусу, а не по формуле: сохранение снято на ходу человека, а ИИ читает города на своём, и
    /// между этими моментами гарнизон успевает поменяться. Пятизначные значения (38736 и прочие) сходятся до
    /// единицы — именно они и подтверждают формулу.</para>
    /// </summary>
    [SkippableFact]
    public void CityRank_OrderAndStrengthFromSaves()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var rules = RulesLoader.Load(GamePaths.Globals);
        var all = new Dictionary<AiSession, Counters>();
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            var turns = events.OfType<AiTurnEvent>().Where(t => t.Phase == "begin").ToList();
            var ranks = events.OfType<AiCityRankEvent>().ToList();
            foreach (var r in ranks)
            {
                var prios = r.Cities.Select(x => x.Prio ?? -1).ToList();
                c.Count("city_rank.weakest_first", prios.Count > 0 && prios[^1] == 0 && prios.SequenceEqual(prios.OrderDescending()),
                        $"{r.Player} seq {r.Seq}: {string.Join(",", prios)}");
            }
            foreach (var pair in ranks.GroupBy(r => (r.Player, r.Pass, Day: DayOf(turns, r.Seq))).Where(g => g.Count() == 2))
                c.Count("city_rank.grow_equals_hire_leader", pair.First().Cities.SequenceEqual(pair.Last().Cities),
                        $"{pair.Key}: списки роста и найма лидера разные");

            var prot = Protections(GamePaths.Globals);
            var samples = new List<string>();
            int exact = 0, total = 0, frozenExact = 0, frozenTotal = 0;
            foreach (var r in ranks.Where(r => r.Pass == 1 && r.Cities.Count >= 2 && r.RetAddr == "0x449769"))
            {
                int day = DayOf(turns, r.Seq);
                if (SaveOf(session, day, r.Seq) is not { } save) continue;
                var (map, forts) = Open(save.Path);
                if (r.Cities.Any(x => x.Id is null || !forts.ContainsKey(x.Id))) continue;
                var v = r.Cities.Select(x => (x.Id!, Strength(forts[x.Id!], map, rules, prot))).ToList();
                var ranked = AiStrategicRules.RankCities(v);
                bool frozen = Frozen(events, map, rules, r.Player ?? "", save.Cut, r.Seq,
                                     r.Cities.Select(x => forts[x.Id!]));
                double tolerance = v.Max(x => x.Item2) / 200.0;
                // Порядок сверяется, если он задан силами из снимка: состав не менялся или соседние по силе города разнесены больше
                // допуска. Иначе (20261016b, карта 2, день 5: деревни Кланов 38737 и 38730 у движка, раны и опыт гарнизона после
                // снимка) порядок решают единицы силы, которых снимок не знает, — это пропуск, а не расхождение.
                var strengths = v.Select(x => x.Item2).Order().ToList();
                bool decisive = frozen || strengths.Zip(strengths.Skip(1)).All(p => p.Second - p.First > tolerance);
                string order = $"{r.Player} день {day}: журнал {string.Join(" ", r.Cities.Select(x => x.Id))}, " +
                               $"порт {string.Join(" ", ranked.Select(x => x.Id))}";
                if (decisive) c.Count("city_rank.order", ranked.Select(x => x.Id).SequenceEqual(r.Cities.Select(x => x.Id!)), order);
                else c.Skip("city_rank.order", "состав менялся после снимка, а силы соседних городов ближе допуска 0,5 %");
                var ours = ranked.ToDictionary(x => x.Id, x => x.Priority);
                foreach (var x in r.Cities)
                {
                    total++;
                    long diff = Math.Abs(ours[x.Id!] - (x.Prio ?? -1));
                    if (diff == 0) exact++;
                    if (frozen)
                    {
                        frozenTotal++;
                        if (diff == 0) frozenExact++;
                        c.Count("city_rank.priority", diff <= tolerance,
                                $"{r.Player} день {day} {x.Id}: журнал {x.Prio}, порт {ours[x.Id!]}");
                    }
                }
                if (samples.Count < 4)
                    samples.Add($"{r.Player} день {day}: " +
                                string.Join(" ", r.Cities.Select(x => $"{x.Id} журнал {x.Prio} порт {ours[x.Id!]}")));
            }
            c.Note($"приоритет по силе из сохранения: точно {exact} из {total}; там, где между снимком и чтением " +
                   $"состав не менялся — {frozenExact} из {frozenTotal}");
            foreach (var line in samples) c.Note($"  {line}");
            if (c.Of("city_rank.priority") is (0, 0))
                c.Skip("city_rank.priority", "нет списка с неизменным составом между снимком и чтением");
            c.Print(output, $"── {session.Name}: порядок городов");
        }
        // order/priority есть не на всех картах: списков из двух городов и больше на первом проходе нет там, где у ИИ всю
        // партию одна столица («Растленная власть» зонда 6.5: 155 списков, все из одного города); «замороженных» — где у
        // столицы всё время побит страж. Требование «хоть одно сравнение во всём корпусе» — общее (AiCorpus.Require).
        AiCorpus.RequireEach(all, "city_rank.weakest_first", "city_rank.grow_equals_hire_leader", "city_rank.order", "city_rank.priority");
    }

    /// <summary>
    /// Можно ли сверять приоритет по сохранению: сила зависит от состава и здоровья гарнизона, а сохранение снято на
    /// ходу человека — до боёв дня и до хода самого ИИ. Годится список, у которого все считаемые юниты в сохранении
    /// целы (иначе их подлечит восстановление в начале хода владельца) и между сохранением и чтением не было ни боя с
    /// участием игрока, ни лечения его юнита, ни переноса юнита, ни найма.
    /// </summary>
    private static bool Frozen(List<CorpusEvent> events, MapState map, GameRules rules, string player,
                               long from, long to, IEnumerable<Fort> forts)
    {
        foreach (var fort in forts)
        {
            var ids = fort.Units.Where(u => u.Length > 0 && !Same(u, EmptyId)).ToList();
            if (map.Stacks.TryGetValue(fort.Guest, out var guest)) ids.AddRange(guest.UnitIds);
            foreach (var id in ids)
                if (map.Units.TryGetValue(id, out var mu) && FortUnit(mu, rules) is { } unit && unit.Hp < unit.HpMax) return false;
        }
        (long lo, long hi) = (Math.Min(from, to), Math.Max(from, to));
        bool Between(long seq) => seq > lo && seq < hi;
        return !events.OfType<BattleStartEvent>()
                      .Any(e => Between(e.Seq) && (Same(e.AttackerPlayer, player) || Same(e.DefenderPlayer, player)))
            && !events.OfType<HealEvent>().Any(e => Between(e.Seq) && Same(e.Player, player))
            && !events.OfType<UnitTransferEvent>().Any(e => Between(e.Seq) && e.Ret)
            && !events.OfType<HireUnitEvent>().Any(e => Between(e.Seq) && e.Ret)
            // Повышение юнита игрока дневным читом опыта (0x473439 → 0x4318ad) между снимком и строкой — сила гарнизона уже не та, что в
            // снимке: карта 23 (20261020-port-synth), день 55, S143PL0003 — S143UN0065 G000UU0064 → G000UU0066 в S143FT000f
            // (upgrade_target seq 9052556), и гарнизон столицы.
            && !events.OfType<UpgradeTargetEvent>().Any(e => Between(e.Seq) && Same(e.Owner, player) && e.Target is { Length: > 0 } t && !Same(t, EmptyId));
    }

    /// <summary>Броня укреплений из <c>GVars</c>: <c>PROT_1..5</c> по уровню деревни и <c>PROT_CAP</c> у столицы
    /// (<c>0x5dcf60</c> зовёт <c>0x5878ee</c>/<c>0x58790c</c>, поля <c>+0xf0</c> и <c>+0x104</c>).</summary>
    private static (IReadOnlyList<int> City, int Capital) Protections(string globalsDir)
    {
        var g = DbfTable.Read(AiStrategicCorpusTests.Find(globalsDir, "GVars.dbf")).Rows[0];
        return ([.. Enumerable.Range(1, 5).Select(i => g.GetInt($"PROT_{i}"))], g.GetInt("PROT_CAP"));
    }

    /// <summary>
    /// Сила города <c>0x5dcec7</c>: сумма <see cref="AiStrength.Unit"/> по живым юнитам гарнизона (<c>+0x3c</c>) и
    /// гостя (<c>+0x30</c>) — два вызова подряд с одними и теми же аргументами (<c>0x448147</c> и <c>0x4481a2</c>).
    /// Аргументы вызова из <c>0x44809c</c> — ранг 1 по листингу: «без города» = 0, число врагов = 3 (влияет только на
    /// охват <c>ALL</c>), «считать мёртвых» = 0 (здоровье текущее, <c>0x479bd6</c> = <c>CMidUnit +0x20</c>), броня
    /// укрепления — <c>PROT_*</c> уровня города, у столицы <c>PROT_CAP</c>, вместе с бронёй юнита не выше 90.
    /// </summary>
    private static double Strength(Fort fort, MapState map, GameRules rules, (IReadOnlyList<int> City, int Capital) prot)
    {
        int fortArmor = fort.Capital ? prot.Capital
            : fort.Size >= 1 && fort.Size <= prot.City.Count ? prot.City[fort.Size - 1] : 0;
        var ids = fort.Units.Where(u => u.Length > 0 && !Same(u, EmptyId)).ToList();
        if (map.Stacks.TryGetValue(fort.Guest, out var guest)) ids.AddRange(guest.UnitIds);
        double sum = 0;
        foreach (var id in ids)
            if (map.Units.TryGetValue(id, out var mu) && FortUnit(mu, rules) is { } unit)
                sum += AiStrength.Unit(unit, ignoreCity: false, enemies: 3, rules.Constants, fortArmor);
        return sum;
    }

    /// <summary>Юнит гарнизона или гостя так, как его считает движок (<see cref="D2.Data.Play.AiTactics.Fighter"/> — тот же путь,
    /// что у порта в <c>AiEconomy.CityRank</c>): выросший уровнем — порождённой реализацией (<c>G000UG…</c>), броня, атака и
    /// иммунитеты — через цепочку модификаторов, потолок здоровья — с ней же. До 13.10 харнесс брал базовую реализацию без
    /// цепочки: r2b, Легионы день 1, гость столицы KC0001 с героем <c>G000UU0070</c> уровня 10 (движок — <c>G000UG30a4</c>,
    /// здоровье 285) — FT0010 выходил 38906 против 39388 журнала. Выросшую реализацию без цепочки взять нельзя: стражи столиц
    /// «Растленной власти» с модификаторами сложности (<c>G000UM9002</c>…, здоровье 190 из базовых 900) по потолку цепочки
    /// «целы», и без снятой цепочкой брони их сила в десять раз больше журнальной. Юнит без атаки не считается.</summary>
    private static BattleUnit? FortUnit(MapUnit mu, GameRules rules) =>
        rules.ImplOf(mu.TypeId) is { Attack: not null } ? D2.Data.Play.AiTactics.Fighter(rules, mu) : null;

    // ── Столица, бюджет, исследование ──────────────────────────────────────────────────────

    /// <summary>
    /// 6а. Постройка столицы (<c>0x446ccb</c>): вызовы стратегии (<c>ai_build_pick</c>, храм — <c>0x446d27</c>) идут в
    /// порядке «нет храма (2) — S1, иначе S2; пусто — S2, затем S1»; статус 2 ⇔ нет постройки лечения. Итог — первый
    /// непустой; он же — <see cref="AiStrategicRules.PickBuilding"/> по сохранению дня плюс постройкам хода
    /// (<see cref="AiStrategicCorpusTests.Choice"/>).
    /// </summary>
    /// <remarks>Тест — в <see cref="AiStrategicHarnessTests_BuildPick"/> (своим классом, параллельно остальным).</remarks>
    internal void BuildPick_OrderAndChoiceFromSaves()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var econ = EconomyLoader.Load(GamePaths.Globals);
        var world = AiStrategicCorpusTests.Gunits.Load(GamePaths.Globals);
        var cheats = EconomyLoader.LoadAiCheats(GamePaths.Globals);
        var lordLists = DbfTable.Read(AiStrategicCorpusTests.Find(GamePaths.Globals, "GbuiList.dbf")).Rows
            .GroupBy(r => r.GetString("LORD_ID"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.GetString("BUILD_ID")).ToList(), StringComparer.OrdinalIgnoreCase);
        var all = new Dictionary<AiSession, Counters>();
        string[] required = [];
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            var turns = events.OfType<AiTurnEvent>().Where(t => t.Phase == "begin").ToList();
            var calls = events.OfType<AiBuildPickEvent>().Where(e => e.Strategy != "temple" || e.RetAddr == "0x446d27");
            var statuses = new HashSet<long>();
            var battles = JournalCache.Read(session.Journal, ["battle_start", "battle_end"]).Where(e => session.Contains(e.Seq)).OrderBy(e => e.Seq).ToList();
            foreach (var pass in calls.GroupBy(e => (e.Player, e.Pass, Day: DayOf(turns, e.Seq))))
            {
                var list = pass.ToList();
                string where = $"{pass.Key.Player} день {pass.Key.Day} проход {pass.Key.Pass}";
                if (list[0].Strategy != "temple" || list[0].Status is not { } status) { c.Skip("build_pick", "проход без вызова храма"); continue; }
                statuses.Add(status);
                var order = list.Skip(1).Select(e => (e.Strategy, e.RetAddr, Empty: e.Result is null || Same(e.Result, EmptyId))).ToList();
                bool orderOk = status == 2
                    ? order.Count >= 1 && order[0].RetAddr == "0x446d43" && (order[0].Empty ? order.Count >= 2 : order.Count == 1)
                    : order.Count >= 1 && order[0].RetAddr == "0x446d6c"
                      && (order[0].Empty ? order.Count == 2 && order[1].RetAddr == "0x446d95" : order.Count == 1);
                c.Count("build_pick.order", orderOk, $"{where}: статус {status}, вызовы {string.Join(" ", order)}");
                string final = order.FirstOrDefault(o => !o.Empty) is { Strategy: not null } hit
                    ? list.Skip(1).First(e => e.Result is not null && !Same(e.Result, EmptyId)).Result! : EmptyId;

                if (SaveOf(session, pass.Key.Day, list[0].Seq) is not { } save) { c.Skip("build_pick", "нет сохранения дня"); continue; }
                var earlier = events.OfType<BuildEvent>()
                    .Where(b => b.Ret && Same(b.Player, pass.Key.Player) && b.Seq > save.Cut && b.Seq < list[0].Seq)
                    .Select(b => b.Building).ToList();
                var map = Open(save.Path).Map;
                bool temple = map.Players[pass.Key.Player!].Buildings.Concat(earlier)
                    .Any(id => econ.Buildings.TryGetValue(id, out var b) && b.Category == 1);
                c.Count("build_pick.temple_status", temple == (status != 2), $"{where}: статус {status}, храм {(temple ? "есть" : "нет")}");
                var xpNow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in events.OfType<XpEvent>().Where(x => x.Ret && x.XpAfter is not null && x.Seq > save.Cut && x.Seq < list[0].Seq))
                    xpNow[x.Unit] = (int)x.XpAfter!.Value;
                var pick = AiStrategicCorpusTests.Choice(save.Path, pass.Key.Player!, earlier, econ, world, lordLists,
                    cheats.UnitXp(new SaveAt(0, save.Path).Difficulty), out string how, xpNow, KilledStacksUnits(battles, map, save.Cut, list[0].Seq));
                // Ничья S2 (И113): первое по id из равных — строгий максимум 0x4476fc по списку доступных.
                if (how.Contains(AiStrategicCorpusTests.TieMark, StringComparison.Ordinal))
                    c.Count("build_pick.tie", Same(pick?.Id ?? EmptyId, final), $"{where}: журнал {final}, порт {pick?.Id} ({how})");
                c.Count("build_pick.port", Same(pick?.Id ?? EmptyId, final), $"{where}: журнал {final}, порт {pick?.Id} ({how})");
            }
            c.Note($"статусы храма в корпусе: {string.Join(",", statuses.Order())}");
            c.Print(output, $"── {session.Name}: постройка столицы");
            required = ["build_pick.order", "build_pick.temple_status", "build_pick.port"];
        }
        AiCorpus.RequireEach(all, required);
    }

    /// <summary>Юниты отрядов, павших в бою между сохранением (<paramref name="from"/>) и выбором (<paramref name="to"/>): проигравшая
    /// группа боя — отряд сохранения, и в <c>battle_end</c> ни один её юнит не жив (бежавший жив — отряд остаётся). Такой отряд движок снимает с
    /// карты вместе с юнитами (<c>CEffectKillStack</c>; порт — <c>GameSession.RemoveStack</c>), и счёт S2 <c>0x44773c</c> их не видит. Карта 23,
    /// день 56 Кланов: S143KC0058 (с UU0038 S143UN0088 у порога) пал в бою с S143KC0007 (seq 9250578) до выбора (seq 9262236) — UU0038 7,
    /// UU0035 8, движок строго берёт BB0039 (И113).</summary>
    private static HashSet<string> KilledStacksUnits(IReadOnlyList<CorpusEvent> battles, MapState map, long from, long to)
    {
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BattleStartEvent? start = null;
        foreach (var e in battles)
        {
            if (e.Seq <= from) continue;
            if (e.Seq >= to) break;
            if (e is BattleStartEvent b) { start = b; continue; }
            if (e is not BattleEndEvent end || start is null) continue;
            string loser = Same(end.Winner, start.AttackerGroup) ? start.DefenderGroup : start.AttackerGroup;
            if (map.Stacks.TryGetValue(loser, out var stack)
                && stack.UnitIds.All(u => end.Units.FirstOrDefault(x => Same(x.Id, u)) is not { Hp: > 0 }))
                gone.UnionWith(stack.UnitIds);
            start = null;
        }
        return gone;
    }

    /// <summary>
    /// 6б. Бюджет (<see cref="AiBudget.Spend"/>): каждое <c>ai_budget_spend</c> — трата по казне после исполнения (последняя
    /// строка <c>bank</c> игрока) с резервом типа (<c>ai_budget.flag</c>); бюджет до траты — цепочка от <c>ai_budget</c> хода
    /// через траты того же типа.
    /// </summary>
    /// <remarks>Тест — в <see cref="AiStrategicHarnessTests_Budget"/> (своим классом, параллельно остальным).</remarks>
    internal void BudgetSpend_MatchesModelAndChains()
    {
        var all = new Dictionary<AiSession, Counters>();
        string[] required = [];
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            var start = new Dictionary<(string, long), AiBudgetEvent>();
            var chain = new Dictionary<string, AiBudget>(StringComparer.OrdinalIgnoreCase);
            var bank = new Dictionary<string, Bank>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in events)
            {
                switch (e)
                {
                    case BankEvent b when b.Ret && b.Player is not null:
                        bank[b.Player] = b.After;
                        break;
                    case AiBudgetEvent b when b.Player is not null && b.Budget is not null && b.Purse is not null:
                        start[(b.Player, b.Type)] = b;
                        var prev = chain.GetValueOrDefault(b.Player);
                        var byType = prev?.ByType.ToDictionary() ?? [];
                        byType[(AiStepType)b.Type] = b.Budget;
                        var reserved = new HashSet<AiStepType>(prev?.Reserved ?? new HashSet<AiStepType>());
                        if (b.Flag) reserved.Add((AiStepType)b.Type); else reserved.Remove((AiStepType)b.Type);
                        chain[b.Player] = new AiBudget(byType, b.Purse) { SpellStart = b.SpellBank ?? Bank.Zero, Reserved = reserved };
                        break;
                    case AiBudgetSpendEvent s when s.Player is not null:
                        CheckSpend(c, s, start, chain, bank);
                        break;
                }
            }
            c.Print(output, $"── {session.Name}: бюджет");
            required = ["budget.spend", "budget.reserve_is_flag", "budget.chain"];
        }
        AiCorpus.RequireEach(all, required);
    }

    private static void CheckSpend(Counters c, AiBudgetSpendEvent s, Dictionary<(string, long), AiBudgetEvent> start,
                                   Dictionary<string, AiBudget> chain, Dictionary<string, Bank> bank)
    {
        string where = $"{s.Player} проход {s.Pass} тип {s.Type} seq {s.Seq}";
        var type = (AiStepType)s.Type;
        if (s.BudgetBefore is null || s.BudgetAfter is null || s.StartBefore is null || s.StartAfter is null
            || !bank.TryGetValue(s.Player!, out var now) || !chain.TryGetValue(s.Player!, out var model))
        { c.Skip("budget", "не прочитано или нет казны"); return; }
        var local = new AiBudget(new Dictionary<AiStepType, Bank> { [type] = s.BudgetBefore }, s.StartBefore)
        {
            SpellStart = s.SpellStartBefore ?? Bank.Zero,
            Reserved = s.Reserve == 1 ? new HashSet<AiStepType> { type } : new HashSet<AiStepType>(),
        }.Spend(type, now);
        c.Count("budget.spend", local.ByType[type] == s.BudgetAfter && local.TurnStart == s.StartAfter,
                $"{where}: журнал {s.BudgetAfter}/{s.StartAfter}, порт {local.ByType[type]}/{local.TurnStart}");
        bool flag = start.TryGetValue((s.Player!, s.Type), out var b) && b.Flag;
        c.Count("budget.reserve_is_flag", (s.Reserve == 1) == flag, $"{where}: reserve {s.Reserve}, flag {flag}");
        c.Count("budget.chain", model.ByType.GetValueOrDefault(type) == s.BudgetBefore && model.TurnStart == s.StartBefore,
                $"{where}: журнал до {s.BudgetBefore}/{s.StartBefore}, цепочка {model.ByType.GetValueOrDefault(type)}/{model.TurnStart}");
        chain[s.Player!] = model.Spend(type, now);
    }

    /// <summary>
    /// 6в. Исследование (<see cref="AiStrategicRules.PickResearch"/>): оценка есть только в дни, кратные 5; её заклинание —
    /// выбор порта по списку лорда (<c>GspellR</c>, порядок файла, AI-категория — <c>Gspells.AI_CAT</c>) среди
    /// изучаемых. Изучаемое — <c>0x5dbf3d(…, 1)</c> == 0: не известно (сохранение дня плюс исследования хода) и цена
    /// изучения (<see cref="Treasury.SpellResearchPrice"/>, <c>GspellR.RESEARCH</c>) по карману казне — флаг 1 включает
    /// проверку <c>0x604039</c> (код 1). Казна — последняя строка <c>bank</c> игрока до оценки. Без цены порт брал у
    /// Империи 20260919-human день 60 атаку g000ss0014 (y450 + w450 при рунической мане 400), движок — g000ss0013
    /// (r450 + y450).
    /// </summary>
    [SkippableFact]
    public void SpellResearch_DayAndChoice()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var spells = SpellLoader.Load(GamePaths.Globals);
        var lords = EconomyLoader.LoadTreasury(GamePaths.Globals).LordCategory;
        var byLord = DbfTable.Read(AiStrategicCorpusTests.Find(GamePaths.Globals, "GspellR.dbf")).Rows
            .GroupBy(r => r.GetString("LORD_ID"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => (Id: r.GetString("SPELL_ID"), Price: Bank.Parse(r.GetString("RESEARCH")))).ToList(),
                          StringComparer.OrdinalIgnoreCase);
        var aiCategory = DbfTable.Read(AiStrategicCorpusTests.Find(GamePaths.Globals, "Gspells.dbf")).Rows
            .ToDictionary(r => r.GetString("SPELL_ID"), r => r.GetInt("AI_CAT"), StringComparer.OrdinalIgnoreCase);
        var all = new Dictionary<AiSession, Counters>();
        string[] required = [];
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            foreach (var turn in Turns(events))
            {
                var evals = turn.Events.OfType<AiEvalEvent>()
                    .Where(e => Same(e.Player, turn.Player) && TypeOf(e.Eval) == AiStepType.SpellResearch).ToList();
                c.Count("research.day_multiple_of_5", evals.Count == 0 || turn.Day % 5 == 0, $"{turn.Player} день {turn.Day}: оценок {evals.Count}");
                if (SaveOf(session, turn.Day, turn.Seq) is not { } save) continue;
                var player = Open(save.Path).Map.Players[turn.Player];
                var known = new HashSet<string>(player.KnownSpells, StringComparer.OrdinalIgnoreCase);
                foreach (var e in turn.Events.Where(e => e is AiEvalEvent or SpellResearchEvent))
                {
                    if (e is SpellResearchEvent r) { if (r.Ret && Same(r.Player, turn.Player)) known.Add(r.Spell); continue; }
                    if (e is not AiEvalEvent ev || !evals.Contains(ev)) continue;
                    var bank = events.OfType<BankEvent>().LastOrDefault(b => b.Ret && b.Seq < ev.Seq && Same(b.Player, turn.Player))?.After
                               ?? player.Bank;
                    int lord = lords[player.LordId];
                    var options = byLord.GetValueOrDefault(player.LordId, [])
                        .Where(x => spells.ContainsKey(x.Id))
                        .Select(x => new AiSpellOption(x.Id, spells[x.Id].Level, spells[x.Id].Category,
                            !known.Contains(x.Id) && !Economy.NotEnough(bank, Treasury.SpellResearchPrice(x.Price, lord)),
                            aiCategory[x.Id])).ToList();
                    var pick = AiStrategicRules.PickResearch(turn.Day, known.Count, (LordCategory)lord, options);
                    c.Count("research.choice", Same(pick?.Id, Str(ev.Eval, "id4")),
                            $"{turn.Player} день {turn.Day} проход {ev.Pass}: журнал {Str(ev.Eval, "id4")}, порт {pick?.Id} (известно {known.Count}: " +
                            $"{string.Join(",", known.Order())}; лорд {player.LordId}; казна {bank})");
                }
            }
            c.Print(output, $"── {session.Name}: исследование");
            required = ["research.day_multiple_of_5", "research.choice"];
        }
        AiCorpus.RequireEach(all, required);
    }
}
