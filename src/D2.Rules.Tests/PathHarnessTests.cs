using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Map;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Путь отряда против журнала зонда: каждое событие <c>path</c> (движок построил путь под курсором) строится
/// заново <see cref="PathFinder"/> и сравнивается по длине и по последней достижимой клетке.
///
/// <para>Позиции и очки хода отрядов живые: они берутся из событий <c>stack_move</c> по ходу журнала, карта — из
/// самого раннего сохранения рядом с журналом. Чего сверить нельзя: маски пути движка (<c>a6</c>/<c>a7</c>), цена
/// клетки в кэше движка на момент наведения и точный порядок равных узлов A* (<c>docs/map-path.md</c>, «Не
/// закрыто»). Поэтому порог — доля совпадений, а не ноль расхождений; число в тесте — измеренное, его нельзя
/// подгонять под алгоритм, только пересчитывать вместе с разбором.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class PathHarnessTests(ITestOutputHelper output)
{
    /// <summary>Доля путей, у которых длина совпала с журналом: измерено 20.09.2026 — 88,0 % (1267 из 1440); 23.09.2026,
    /// с погибшими в бою отрядами, — 88,6 % (1132 из 1278), последняя достижимая клетка — 912; 16.10.2026, только отрезок
    /// журнала от сохранения до следующей загрузки (<see cref="LoadOf"/>), — 91,4 % (1561 из 1707).
    /// Остаток — журналы, рядом с которыми лежит сохранение другой карты (харнесс берёт первое по имени), и
    /// случаи из «Не закрыто» <c>docs/map-path.md</c>: порядок равных узлов A* и устаревшая цена клетки в кэше
    /// движка. Число нельзя подгонять под алгоритм — только пересчитывать вместе с разбором.</summary>
    private const double MinShare = 0.85;

    [SkippableFact]
    public void Paths_MatchJournal()
    {
        Skip.IfNot(CorpusFiles.Available, "нет корпуса");
        var art = MapArtData.Load(Path.Combine(RepoRoot, "data", "map"));
        int total = 0, same = 0, sameEnd = 0;
        var reasons = new SortedDictionary<string, int>();
        var files = CorpusFiles.Journals().Where(f => Directory.GetFiles(Path.GetDirectoryName(f)!, "*.sg").Length > 0).ToList();
        Skip.If(files.Count == 0, "нет журналов с сохранением рядом");

        foreach (string journal in files)
        {
            string? sg = Directory.GetFiles(Path.GetDirectoryName(journal)!, "*.sg").Order(StringComparer.Ordinal).FirstOrDefault();
            if (sg is null) continue;
            MapState map;
            try { map = MapLoader.Load(sg); }
            catch (InvalidDataException) { continue; }
            var (done, ok, okEnd) = Replay(map, journal, sg, art, reasons);
            output.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(journal))}/{Path.GetFileName(journal)}: путей {done}, длина {ok}, последняя {okEnd}");
            total += done;
            same += ok;
            sameEnd += okEnd;
        }

        Skip.If(total == 0, "в журналах нет событий path");
        double share = same / (double)total;
        output.WriteLine($"путей {total}, длина совпала {same} ({share:P1}), последняя клетка совпала {sameEnd}");
        foreach (var (reason, _) in reasons.Where(r => r.Key.StartsWith('[')).Take(8)) output.WriteLine(reason);
        foreach (var (reason, count) in reasons.Where(r => !r.Key.StartsWith('[')).OrderByDescending(p => p.Value).Take(8))
            output.WriteLine($"  {reason}: {count}");
        Assert.True(share >= MinShare, $"совпало {share:P1}, ждали не меньше {MinShare:P0}");
    }

    private static (int Total, int Same, int SameEnd) Replay(MapState map, string journal, string sg, MapArtData art,
                                                             IDictionary<string, int> reasons)
    {
        var all = JournalCache.Read(journal, ["path", "stack_move", "battle_start", "battle_end", "save", "rng_seed"]).ToList();
        var (lo, hi) = LoadOf(journal, sg, all);
        var events = all.Where(e => e.Seq >= lo && e.Seq < hi);
        var live = map.Stacks.Values.ToDictionary(s => s.Id, s => (s.X, s.Y, s.Move), StringComparer.OrdinalIgnoreCase);
        // Отряд, который за прогон сходил с места, в крепости уже не сидит: поле INSIDE сохранения устарело.
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Отряд, проигравший бой, врагом больше не считается: иначе «мёртвый» сосед обрывает путь (docs/map-path.md §2).
        // Клетку он держит — путь движка в неё после боя не заходит (f6: наведение на неё даёт путь из одной точки).
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BattleStartEvent? battle = null;
        var statics = map.Plan.ToDictionary(p => p.Key, p => p.Value.Where(id => Kind(id) != "KC").ToList());
        int total = 0, same = 0, sameEnd = 0;

        foreach (var e in events)
        {
            if (e is StackMoveEvent m)
            {
                if (live.ContainsKey(m.Stack)) live[m.Stack] = ((int)m.X, (int)m.Y, (int)m.MoveLeft);
                moved.Add(m.Stack);
                gone.Remove(m.Stack);   // отступил из боя и ходит дальше
                continue;
            }
            if (e is BattleStartEvent b) { battle = b; continue; }
            if (e is BattleEndEvent end)
            {
                if (battle is not null)
                {
                    string loser = end.Winner.Equals(battle.AttackerGroup, StringComparison.OrdinalIgnoreCase)
                        ? battle.DefenderGroup : battle.AttackerGroup;
                    if (Kind(loser) == "KC") gone.Add(loser);
                }
                battle = null;
                continue;
            }
            if (e is not PathEvent p || !map.Stacks.TryGetValue(p.Stack, out var baseStack)) continue;
            if (!live.TryGetValue(p.Stack, out var now) || gone.Contains(p.Stack)) continue;

            var state = With(map, live, statics, moved, gone);
            var stack = state.Stacks[p.Stack];
            var options = Options(map, baseStack, art);
            total++;
            var path = PathFinder.Find(state, stack, (int)p.EndX, (int)p.EndY, options, MovementRules.Vanilla,
                                       dayAllowance: Math.Max(1, baseStack.Move));
            int len = path.Points.Count;   // журнал считает точки вместе с клеткой отряда
            if (len == p.Len) same++;
            else if (Environment.GetEnvironmentVariable("PATH_DEBUG") is not null && reasons.Count < 60)
                reasons[$"[{total}] отряд ({now.X},{now.Y}) очки {now.Move} цель ({p.EndX},{p.EndY}): наш {len}, журнал {p.Len}"] = 1;
            else reasons[$"длина {(len > p.Len ? "+" : "−")}{Math.Abs(len - p.Len)}"] = reasons.TryGetValue($"длина {(len > p.Len ? "+" : "−")}{Math.Abs(len - p.Len)}", out int c) ? c + 1 : 1;
            var last = path.Points.Count == 0 ? (now.X, now.Y) : (path.Points[path.Reachable].X, path.Points[path.Reachable].Y);
            if (last == ((int)p.LastX, (int)p.LastY)) sameEnd++;
        }
        return (total, same, sameEnd);
    }

    /// <summary>Отрезок журнала, который можно сверять с сохранением <paramref name="sg"/>: от его строки <c>save</c> до
    /// следующей загрузки (<c>rng_seed</c> <c>new_game_time</c> — новая партия или загрузка). Раньше сохранения карта
    /// другая: отряды, павшие до записи, в файле отсутствуют — в 20261016-port-synth passive-001 единственное сохранение
    /// карты 1 записано после всех боёв, и 102 пути «вплотную к нейтралу» (журнал: 1 точка — атака) порт строил в пустую
    /// клетку (2 точки). Сохранение привязано к строке <c>save</c> по времени
    /// файла от <c>started_at</c> манифеста (как <c>SaveSnapshots</c>); не привязалось — весь журнал, как до 16.10. Пути
    /// другой загрузки — другая карта: в 20261016-port-synth passive-001 за картой 1 идёт карта 2 на той же основе (id отрядов
    /// <c>S143KC…</c> общие), и её 106 путей сверялись с сохранением карты 1. Измерено 16.10: путей 1707, длина совпала
    /// 1561 (91,4 %; до правки 1838 и 86,2 %), последняя клетка — 1250; карта 1 port-synth — 0 путей (все до сохранения),
    /// hotseat — 444 из 473 (совпало 434).</summary>
    private static (long Lo, long Hi) LoadOf(string journal, string sg, IReadOnlyList<CorpusEvent> events)
    {
        string manifest = Path.Combine(Path.GetDirectoryName(journal)!, "manifest.json");
        if (!File.Exists(manifest)) return (long.MinValue, long.MaxValue);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return (long.MinValue, long.MaxValue);
        var row = doc.RootElement.EnumerateArray().FirstOrDefault(r => r.TryGetProperty("file", out var f)
            && f.GetString() == Path.GetFileName(journal));
        if (row.ValueKind != System.Text.Json.JsonValueKind.Object || !row.TryGetProperty("saves", out var names)
            || !row.TryGetProperty("started_at", out var at)
            || !names.EnumerateArray().Any(n => n.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(n.GetString(), Path.GetFileName(sg), StringComparison.OrdinalIgnoreCase)))
            return (long.MinValue, long.MaxValue);
        var started = DateTimeOffset.Parse(at.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        double mt = (new DateTimeOffset(File.GetLastWriteTimeUtc(sg)) - started).TotalMilliseconds;
        var save = events.OfType<SaveEvent>().Where(e => e.Ok && !e.Temp && e.T <= mt + 500).MaxBy(e => e.Seq);
        if (save is null) return (long.MinValue, long.MaxValue);
        var loads = events.OfType<RngSeedEvent>().Where(e => e.Cause == "new_game_time").Select(e => e.Seq).Order().ToList();
        long lo = loads.LastOrDefault(x => x < save.Seq, long.MinValue);
        long hi = loads.FirstOrDefault(x => x > save.Seq, long.MaxValue);
        return (Math.Max(lo, save.Seq), hi);
    }

    /// <summary>Карта с живыми позициями отрядов: план пересобирается из неподвижных объектов и текущих клеток.</summary>
    private static MapState With(MapState map, IReadOnlyDictionary<string, (int X, int Y, int Move)> live,
                                 IReadOnlyDictionary<(int X, int Y), List<string>> statics, IReadOnlySet<string> moved,
                                 IReadOnlySet<string> gone)
    {
        var stacks = new Dictionary<string, MapStack>(StringComparer.OrdinalIgnoreCase);
        var plan = statics.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[.. p.Value]);
        foreach (var (id, s) in map.Stacks)
        {
            var (x, y, move) = live.TryGetValue(id, out var l) ? l : (s.X, s.Y, s.Move);
            string inside = moved.Contains(id) ? "" : s.Inside;
            stacks[id] = new MapStack
            {
                Id = s.Id, Owner = s.Owner, Subrace = s.Subrace, LeaderId = s.LeaderId, LeaderAlive = s.LeaderAlive,
                X = x, Y = y, Move = move, Inside = inside, UnitIds = s.UnitIds, Slots = s.Slots,
                Items = s.Items, Equipment = s.Equipment, Order = s.Order, OrderTarget = s.OrderTarget,
                AiOrder = s.AiOrder, AiOrderTarget = s.AiOrderTarget, UpgradeCount = s.UpgradeCount,
            };
            if (gone.Contains(id)) stacks.Remove(id);   // врагом больше не считается, клетку держит (см. Replay)
            if (inside.Length > 0) continue;   // отряд в крепости клетку не занимает
            var list = plan.TryGetValue((x, y), out var ids) ? ids.ToList() : [];
            list.Add(id);
            plan[(x, y)] = list;
        }
        return new MapState
        {
            Size = map.Size, Tiles = map.Tiles, Stacks = stacks, Units = map.Units, Players = map.Players,
            Objects = map.Objects, Plan = plan, Fog = map.Fog, Turn = map.Turn, Rods = map.Rods, Sites = map.Sites,
            Relations = map.Relations, TalismanCharges = map.TalismanCharges, SpellCasts = map.SpellCasts,
            ItemCasts = map.ItemCasts, ItemTypes = map.ItemTypes, Limits = map.Limits, MapSeed = map.MapSeed,
        };
    }

    /// <summary>Флаги отряда: жив ли лидер и его бонусы хода (<c>GMabi</c> — <c>data/map/move-bonus.json</c>).
    /// У интерфейса игрока суша водному отряду разрешена (<c>forbidWaterOnLand = 0</c>).</summary>
    private static MoveOptions Options(MapState map, MapStack stack, MapArtData art)
    {
        string type = map.Units.TryGetValue(stack.LeaderId, out var leader) ? leader.TypeId : "";
        var bonus = art.MoveBonuses.FirstOrDefault(b => b.Unit.Equals(type, StringComparison.OrdinalIgnoreCase));
        var grounds = bonus?.Grounds ?? [];
        return new MoveOptions(
            LeaderAlive: stack.LeaderAlive,
            PlainsBonus: grounds.Contains(0),
            ForestBonus: grounds.Contains(1),
            WaterBonus: grounds.Contains(3),
            WaterOnly: false,
            ForbidWaterOnLand: false);
    }

    private static string Kind(string id) => id.Length >= 6 ? id.Substring(4, 2).ToUpperInvariant() : "";

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
            return d?.FullName ?? "";
        }
    }
}
