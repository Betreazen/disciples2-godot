using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Партия с зондом 7.2 (<c>20260930-probe72</c>, карта ai-q09, 20 дней): правила юнитов порта против строк движка по одной, на
/// сохранении дня (<c>-tNN-</c> снят в ходе человека, до ходов ИИ этого дня). Состояние берётся из сохранения, числа, которые
/// меняет день (здоровье, модификаторы), — из строки журнала: так сверяется правило, а не накопленное состояние.
/// </summary>
[Trait("Category", "Corpus")]
public sealed class Probe72UnitsHarnessTests(ITestOutputHelper output)
{
    private const string CorpusName = "20260930-probe72";
    private const string Stem = "ai-q09-ai-ai-diplomacy-human";
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<GameBook> BookCache = new(() => GameBook.Load(Path.Combine(Data, "rules")));

    private static string? Journal()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), CorpusName, "passive-001.jsonl");
        return File.Exists(path) ? path : null;
    }

    private static readonly Dictionary<int, MapState?> Maps = [];

    /// <summary>Карта сохранения дня <paramref name="turn"/> (последний файл дня); гость крепости (<c>City.STACK</c>) — в ней.</summary>
    private static MapState? MapOf(int turn)
    {
        lock (Maps)
        {
            if (Maps.TryGetValue(turn, out var cached)) return cached;
            string dir = Path.Combine(GameSaveTests.CorpusDir(), CorpusName);
            string? sg = Directory.GetFiles(dir, $"{Stem}-t{turn:00}-*.sg").Order(StringComparer.Ordinal).LastOrDefault();
            MapState? map = null;
            if (sg is not null)
            {
                map = MapLooksLoader.Load(sg).Map;
                var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var fort in map.Sites.Where(f => f.StackId.Length > 0))
                    if (stacks.TryGetValue(fort.StackId, out var s) && s.Inside.Length == 0) stacks[s.Id] = s with { Inside = fort.Id };
                map = GameSession.With(map, stacks: stacks);
            }
            return Maps[turn] = map;
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Строки журнала с днём: день — ход ближайшей следующей строки <c>ai_turn begin</c> (лечение, чит опыта и поиск
    /// цели повышения идут в начале хода, до неё).</summary>
    private static List<(T Row, int Day)> WithDay<T>(IEnumerable<CorpusEvent> events) where T : CorpusEvent
    {
        var rows = new List<(T, int)>();
        var pending = new List<T>();
        foreach (var e in events.OrderBy(e => e.Seq))
        {
            if (e is T row) pending.Add(row);
            else if (e is AiTurnEvent { Phase: "begin" } t)
            {
                rows.AddRange(pending.Select(p => (p, (int)t.Turn)));
                pending.Clear();
            }
        }
        return rows;
    }

    private void Show(SortedDictionary<string, int> tally)
    {
        foreach (var (k, v) in tally) output.WriteLine($"  {k}: {v}");
    }

    private static void Count(SortedDictionary<string, int> tally, string key) => tally[key] = tally.GetValueOrDefault(key) + 1;

    /// <summary>
    /// Дневное лечение (<c>regen</c>, <c>0x5d4099</c>): на каждой строке — слагаемое порта (<see cref="GameSession.RegenTerms"/>) той же
    /// группы и то же здоровье после при здоровье до из строки. Руины — <see cref="MapVariables.RuinRegen"/>.
    /// </summary>
    /// <remarks>30.09, probe72: строк 121 — отряды и гарнизоны (слагаемое юнита) 71, укрепления 13, руины 37 (из них 33 с днём, все о
    /// павших: 0 → 0). Сошлось всё: 71, 13, 33. Прежнее правило (гарнизон не лечится, «своя земля» — после дневного роста, по
    /// <c>terrain_day</c>) — юнит 60 из 71 (+10 % своей земли у отрядов в поле, чья клетка стала своей в этот день: UN001a день 5 —
    /// в живом дне это «порт 111, движок 99»; UN027a день 8, UN0246 и UN0019 день 9; у гарнизонов 0 % — 7 строк), укрепления 2 из 13
    /// (сошлись только гости KC0002 в FT000e и KC00a2 в FT0002; гарнизоны FT000c, FT0002, FT0003, FT0001, FT000e — 0 %).</remarks>
    [SkippableFact]
    public void Regen_MatchesJournal()
    {
        string? journal = Journal();
        Skip.If(journal is null, "нет корпуса probe72");
        var book = BookCache.Value;
        var events = JournalCache.Read(journal!, ["ai_turn", "regen"]);
        var tally = new SortedDictionary<string, int>();
        var terms = new Dictionary<(int, string), IReadOnlyList<RegenTerm>>();
        int shown = 0;
        foreach (var (r, day) in WithDay<RegenEvent>(events))
        {
            if (MapOf(day) is not { } map) { Count(tally, $"{r.Term}: нет сохранения"); continue; }
            if (r.Term == "ruin")
            {
                bool guard = map.Sites.Any(s => s.Kind == SiteKind.Ruin && s.Guards.Any(g => Same(g, r.Unit)));
                long after = r.HpBefore <= 0 ? 0 : Math.Min(r.HpMax, r.HpBefore + r.HpMax * book.Vars.RuinRegen / 100);
                Count(tally, guard && r.Pct == book.Vars.RuinRegen && after == r.HpAfter ? "ruin: сошлось" : "ruin: разошлось");
                continue;
            }
            var fort = map.Sites.FirstOrDefault(s => s.Guards.Any(g => Same(g, r.Unit)));
            string? group = fort?.Id ?? map.Stacks.Values.FirstOrDefault(s => s.UnitIds.Any(u => Same(u, r.Unit)))?.Id;
            string? owner = fort?.Owner ?? (group is null ? null : map.Stacks[group].Owner);
            if (group is null || owner is null || !Same(group, r.Owner)) { Count(tally, $"{r.Term}: группа не та"); continue; }
            if (!terms.TryGetValue((day, owner), out var list)) terms[(day, owner)] = list = GameSession.RegenTerms(map, owner, book);
            var term = list.FirstOrDefault(t => Same(t.UnitId, r.Unit));
            int pct = term is null ? 0 : r.Term == "fort" ? term.FortPercent : term.UnitPercent;
            long max = term?.HpMax ?? 0;
            long hp = r.HpBefore <= 0 ? 0 : Math.Min(max, r.HpBefore + max * pct / 100);
            string verdict = pct != r.Pct ? "процент" : max != r.HpMax ? "максимум" : hp != r.HpAfter ? "здоровье" : "сошлось";
            Count(tally, $"{r.Term}: {verdict}");
            if (verdict != "сошлось" && shown++ < 30)
                output.WriteLine($"{r.Term} {r.Unit} ({r.Type}) день {day} группа {group}: порт {pct}% max {max}, движок {r.Pct}% " +
                                 $"(+{r.Bonus}) max {r.HpMax}, {r.HpBefore} → {r.HpAfter}");
        }
        Show(tally);
        Assert.Equal(71, tally.GetValueOrDefault("unit: сошлось"));
        Assert.Equal(13, tally.GetValueOrDefault("fort: сошлось"));
        Assert.Equal(33, tally.GetValueOrDefault("ruin: сошлось"));
    }

    /// <summary>
    /// Цель повышения не лидера (<c>upgrade_target</c>, <c>0x5d7fe7</c> из порога <c>0x5d7412</c>, <c>ret</c> <c>0x5d7499</c>): есть ли цель и
    /// какая — против порта (<see cref="D2.Rules.Units.UnitUpgrade.Successors"/> с постройками хозяина группы из сохранения дня). Строки
    /// до хода ИИ (чит опыта) — постройки сохранения те же, что у движка в этот миг; в бою посреди хода — постройки могли прибавиться.
    /// </summary>
    /// <remarks>30.09, probe72: строк 23 (чит и бои), сошлось 23 — есть цель 10, нет 13 (без построек дня до строки — есть цель 5 из 10:
    /// повышения в бою после постройки того же хода, UN001a/UN0022/UN0024/UN0015/UN001f). «Гарнизон деревни не повышается» (§26) — не
    /// правило: <c>S143UN0019</c> (UU0057, FT000c) в день 10 без цели, потому что <c>BB0064</c> игрок PL0002 строит в этот же день
    /// <b>после</b> чита (seq 824015 xp → 824091 build), в день 11 цель <c>UU0058</c> и повышение (в строке силы 1015003 уже UU0058 270/270);
    /// <c>S143UN0014</c> (UU8014, отряд KC0003) — то же: без цели до постройки, затем <c>UU8016</c> (seq 2506460). Порт делает так же
    /// (чит — в начале хода, до стратегии); «лишний у порта» в <c>Candidates_MatchJournal</c> был от строки xp, которая пишет юнита до
    /// превращения (<c>0x5d8b17</c>).</remarks>
    [SkippableFact]
    public void UpgradeTarget_MatchesJournal()
    {
        string? journal = Journal();
        Skip.If(journal is null, "нет корпуса probe72");
        var book = BookCache.Value;
        var events = JournalCache.Read(journal!, ["ai_turn", "upgrade_target", "build", "save"]).OrderBy(e => e.Seq).ToList();
        var saveSeq = events.OfType<SaveEvent>().Where(x => x.Ok).GroupBy(x => (int)x.Turn).ToDictionary(g => g.Key, g => g.Max(x => x.Seq));
        var builds = events.OfType<BuildEvent>().Where(b => b.Ret).ToList();
        var tally = new SortedDictionary<string, int>();
        int day = 1;
        foreach (var e in events)
        {
            if (e is AiTurnEvent { Phase: "begin" } t) { day = (int)t.Turn; continue; }
            if (e is not UpgradeTargetEvent { Leader: false, RetAddr: "0x5d7499" } u || u.Type is null) continue;
            // День строки: до ai_turn begin (чит) — день следующего хода, он же день последнего begin + 0 или + 1; сохранение дня —
            // до ходов ИИ, так что строке чита годится сохранение следующего begin, бою — текущего.
            int saveDay = events.OfType<AiTurnEvent>().FirstOrDefault(a => a.Seq > u.Seq && a.Phase == "begin")?.Turn is { } next
                          && next != day ? (int)next : day;
            if (MapOf(saveDay) is not { } map || u.Owner is null || !map.Players.TryGetValue(u.Owner, out var owner))
            { Count(tally, "нет сохранения"); continue; }
            // Постройки сохранения и построенные после него до строки (бой посреди хода: S143UN001a день 7 — BB0050 за 52 тыс. строк).
            long from = saveSeq.GetValueOrDefault(saveDay);
            var built = owner.Buildings.Concat(builds.Where(b => b.Seq > from && b.Seq < u.Seq && Same(b.Player, u.Owner)).Select(b => b.Building))
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var successors = D2.Rules.Units.UnitUpgrade.Successors(book.Rules.Units, u.Type);
            string? port = successors.FirstOrDefault(s => book.UpgradeBuilding.GetValueOrDefault(s.Id, "") is var b
                                                          && (b.Length == 0 || built.Contains(b)))?.Id;
            string verdict = successors.Count == 0 ? "рост на месте (не сверяется)"
                : Same(port, u.Target) || (port is null && u.Target is null) ? u.Target is null ? "сошлось: нет цели" : "сошлось: есть цель"
                : "разошлось";
            Count(tally, verdict);
            if (verdict == "разошлось")
                output.WriteLine($"{u.Unit} ({u.Type}) seq {u.Seq} день {saveDay} {u.Container}:{u.Owner}: порт {port ?? "-"}, движок {u.Target ?? "-"}");
        }
        Show(tally);
        Assert.Equal(10, tally.GetValueOrDefault("сошлось: есть цель"));
        Assert.Equal(13, tally.GetValueOrDefault("сошлось: нет цели"));
        Assert.Equal(0, tally.GetValueOrDefault("разошлось"));
    }

    /// <summary>
    /// Сила юнита для ИИ (<c>ai_strength_units</c>: группа <c>0x5dcec7</c>, юнит <c>0x5dd06c</c>) по юнитам: тип и уровень — из
    /// сохранения дня, здоровье и цепочка модификаторов — из строки, броня укрепления — поле <c>armor</c>, «без города» — <c>a3</c>
    /// (<see cref="AiTactics.Fighter"/>, <see cref="D2.Rules.Battle.AiStrength.Unit"/>). Юнит, чей максимум здоровья у порта другой
    /// (вырос после сохранения, нанят), — «состояние», не сверяется. Вызовы <c>0x623910</c> (аргументы не по сигнатуре) пропущены.
    /// </summary>
    /// <remarks>30.09, probe72: юнитов 30851 — сошлись все (своя сила B атаки 0x453511 тоже), групп 10632 из 10632 (где известны все
    /// юниты); не в сохранении дня 1391 (нанятые, призванные днём), максимум другой 86 (выросли днём). Значит остаток своей силы B в
    /// живом дне (§26) — не правило, а состояние: цепочка модификаторов лидера меняется посреди дня — навык при повышении
    /// (<c>skill_pick</c>: UM4002 у S143UN0246 день 7, UM4003 у S143UN0247 день 16), предмет (<c>item_equip</c>: UM2302 Unholy Chalice у
    /// S143UN0246 день 8, UM2202 у S143UN0248), модификаторы защиты UM3001/UM3002 (те, что в GcityInf) у нейтральных отрядов KC006a/KC0080/
    /// KC0022 только в строках боя 0x5ddacd/0x5ddae5 (в сохранении их нет, источник не разобран), и здоровье.</remarks>
    [SkippableFact]
    public void StrengthUnits_MatchJournal()
    {
        string? journal = Journal();
        Skip.If(journal is null, "нет корпуса probe72");
        var book = BookCache.Value;
        var constants = book.Rules.Constants;
        var tally = new SortedDictionary<string, int>();
        var wrong = new SortedDictionary<string, int>();
        int day = 1, shown = 0;
        foreach (var e in JournalCache.Read(journal!, ["ai_turn", "ai_strength_units"]).OrderBy(e => e.Seq))
        {
            if (e is AiTurnEvent { Phase: "begin" } t) { day = (int)t.Turn; continue; }
            if (e is not AiStrengthUnitsEvent row || row.RetAddr == "0x623910" || MapOf(day) is not { } map) continue;
            double total = 0;
            bool all = true;
            foreach (var ju in row.Units)
            {
                if (ju.Id is null || !map.Units.TryGetValue(ju.Id, out var mu)) { Count(tally, "нет в сохранении"); all = false; continue; }
                if (ju.Type is { } jt && !Same(jt, mu.TypeId) && book.Rules.Units.TryGetValue(jt, out var grown))
                    mu = mu with { TypeId = jt, Level = grown.Level };   // повышен после сохранения — тип строки
                mu = mu with { Hp = (int)ju.Hp, Modifiers = [.. ju.Mods.OfType<string>()] };
                var u = AiTactics.Fighter(book.Rules, mu, row.Full);
                if (u.HpMax != ju.Max) { Count(tally, "максимум другой (состояние)"); all = false; continue; }
                double s = D2.Rules.Battle.AiStrength.Unit(u, row.A3 == 1, (int)row.Enemies, constants, (int)(row.Armor ?? 0));
                total += s;
                bool same = ju.S is { } js && Math.Abs(s - js) <= 1e-6 * Math.Max(1, Math.Abs(js));
                Count(tally, same ? "сошлось" : "разошлось");
                if (same) continue;
                Count(wrong, $"{ju.Type} моды [{string.Join(" ", ju.Mods)}]");
                if (shown++ < 20)
                    output.WriteLine($"{ju.Id} {ju.Type} seq {row.Seq} ret {row.RetAddr}: порт {s:0.####}, движок {ju.S:0.####}; hp {ju.Hp}/{ju.Max} " +
                                     $"врагов {row.Enemies} a3 {row.A3} броня {row.Armor} полное {row.Full} моды [{string.Join(" ", ju.Mods)}]");
            }
            if (all && row.Total is { } jt2) Count(tally, Math.Abs(total - jt2) <= 1e-6 * Math.Max(1, Math.Abs(jt2)) ? "группа: сошлась" : "группа: разошлась");
        }
        Show(tally);
        foreach (var (k, v) in wrong) output.WriteLine($"  разошлось {k}: {v}");
        Assert.Equal(0, tally.GetValueOrDefault("разошлось"));
        Assert.Equal(30851, tally.GetValueOrDefault("сошлось"));
        Assert.Equal(10632, tally.GetValueOrDefault("группа: сошлась"));
    }
}
