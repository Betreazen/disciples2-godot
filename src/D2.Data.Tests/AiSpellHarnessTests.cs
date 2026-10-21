using System.Text.Json;
using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Заклинания ИИ на карте, оперативный слой до тактики (<c>docs/ai-spells.md</c>, срез 7). «Живой день»
/// (<see cref="AiLiveDay"/>): сохранение дня, поверх — ходы, бои, наймы лидеров и приказы из журнала; у каждого игрока ИИ
/// начало хода и стратегия — портом, затем слой (<see cref="AiSpells"/>). Против журнала того же игрока и дня: (1) число
/// бросков R250 между концом стратегии (последний <c>rng_after</c>) и первой <c>ai_tactic_pick</c> — в нём только выбор
/// заклинаний (<c>0x46e92c</c>: круг категорий, число, перемешивание); (2) литья <c>spell_cast</c> (не бесплатные) в том
/// же окне — заклинание и клетка.
/// </summary>
[Trait("Category", "Corpus")]
public class AiSpellHarnessTests(ITestOutputHelper output)
{
    /// <summary>Партии: каталог, журнал, начало имени сохранений карты и отрезок seq (журнал с несколькими картами одной S143 —
    /// карта отрезком, иначе окна (игрок, день) разных карт сливаются).</summary>
    private static readonly (string Dir, string File, string Stem, long From, long To)[] Sessions =
    [
        ("20260918-probe65", "passive-001.jsonl", "", 0, 0), ("20260919-human", "passive-001.jsonl", "", 0, 0),
        ("20260920-legions", "passive-001.jsonl", "", 0, 0),
        // port-synth-4-ai (лорд-маг Легионов, правитель Нежити): лёгкая, очень трудная, третья и четвёртая партии.
        ("20261016-port-synth", "passive-002.jsonl", "port-synth-4-ai", 0, 0),
        ("20261016b-port-synth", "passive-001.jsonl", "port-synth-4-ai", 2300059, 4727542),
        ("20261016c-port-synth", "passive-001.jsonl", "port-synth-4-ai", 588357, 3394036),
        ("20261016d-port-synth", "passive-001.jsonl", "port-synth-4-ai", 0, 0),
        // Зонд 7.5 (броски слоя — строками ai_rng): карты 16 (SUGG_LVL 10 — «натиск»), 17 (с загрузкой дня 2) и 18 одного журнала.
        ("20261018b-port-synth", "passive-001.jsonl", "port-synth-16-ai-calm", 437, 4507811),
        ("20261018b-port-synth", "passive-001.jsonl", "port-synth-17-mixed", 4508451, 6591373),
        ("20261018b-port-synth", "passive-001.jsonl", "port-synth-18-ai-rows", 6591743, 0),
        // Партия с покрытием (зонд 7.5, Greenskins, человек, дни 1–18): op_spell_start 10 строк.
        ("20261019c-cov", "passive-001.jsonl", "greenskins-human", 0, 0),
    ];

    /// <summary>Места бросков оперативного слоя до тактики (<c>0x46d602</c>): «натиск» <c>0x46fe5e</c>, выбор и литьё <c>0x46e92c</c>,
    /// перемешивания слоя (<c>0x4495d8</c> из <c>0x46a9f5</c> у «натиска» и вторая часть лечения, <c>0x43949e</c> из <c>0x470abb</c>).</summary>
    private static bool Operational(AiRngEvent r) => r.Kind == "r250" && (r.Site is "harass_chance" or "harass_count" or "op_spell_start" or "op_spell_count"
        || r is { Site: "shuffle_list", Origin: "harass_targets" or "harass_spells" or "op_spell_part2" }
        || r is { Site: "shuffle_misfit", Origin: "op_spell" });

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Измерено 28.09.2026 (три партии): ходов ИИ 732; броски и литья сошлись у всех 732, все 60 литий журнала
    /// повторены (заклинание и клетка), лишних нет; здоровье юнитов после литья — 126 из 126 (было 125: human PL000b день 67, <c>S073UN0295</c> журнал 0, порт 100 — живой
    /// день не повторял перенос <c>unit_transfer</c> <c>ai_reorder</c> seq 17170090 из гарнизона <c>FT0000</c> в <c>KC00af</c> в ходе PL0009,
    /// а найм в гарнизон шёл портом: в клетке 2 у порта <c>G000UU0001</c> (100), у движка <c>G000UU0006</c> (45, seq 17169233; тип
    /// выбирает CRT <c>rand()</c>) — теперь переносы и наймы юнитов из журнала, прочие числа не сдвинулись). Было 727 и 731 (лишнее литьё 1):
    /// (1) найм лидера из журнала шёл после оперативного слоя, а у движка он в стратегии — новый отряд-гость закрывает крепость
    /// для защиты <c>0x46e37c</c> (визитёр <c>+0x8c</c>); human PL0009 день 45, PL000c день 61, probe65 PL0002 день 2 — +3;
    /// (2) день, переигранный после загрузки (human PL0009 день 23, три записи), шёл с живого дня первой записи — теперь заново
    /// с сохранения, +2. Списки сил (<c>0x4760b0</c>) тут ни при чём. Без земли из terrain_day было 722, без ходов предыдущих
    /// игроков — 671. Не подгонять. 16.10 вечер: четыре партии <c>port-synth-4-ai</c> (лорд-маг Легионов, правитель Нежити, лёгкая и
    /// очень трудная) — ещё 136 ходов, броски сошлись у всех; литий оперативного слоя ни у движка, ни у порта нет (журналы без
    /// <c>spell_cast</c> ИИ). Исследования ИИ на этих картах сверяет <c>AiStrategicHarnessTests.SpellResearch_DayAndChoice</c>: 5 + 12 +
    /// 12 + 5 выборов, разошлось 0. 19.10: первый журнал зонда 7.5 (<c>20261018b-port-synth</c>, карты 16–18) — ещё 76 ходов (212), броски и
    /// литья сошлись у всех; на бросках журнала (строки <c>ai_rng</c> слоя: «натиск» <c>harass_chance</c> 84 и <c>harass_count</c> 19 у карты
    /// 16 с <c>SUGG_LVL</c> 10, <c>op_spell_start</c> 4 у карты 18 — 107 строк) порт потратил ровно их и с теми же границами у 76 из 76. 19.10 (r19b/cov): партия с покрытием <c>20261019c-cov</c> (Greenskins, человек, 18 дней) —
    /// ещё 68 ходов (800), литий слоя нет; на бросках журнала (<c>op_spell_start</c> 10 строк) точно у 68 из 68.</summary>
    [SkippableFact]
    public void OperationalSpells_MatchJournal() => Check(synth: false, wantTurns: 800, wantCasts: 60, wantHp: 126, wantRollTurns: 68);

    /// <summary>То же на партиях синтетических карт (<c>port-synth-4-ai</c>) — отдельной группой (<c>Category=Synth</c>).</summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void OperationalSpells_MatchJournal_Synth() => Check(synth: true, wantTurns: 212, wantCasts: 0, wantHp: 0, wantRollTurns: 76);

    private void Check(bool synth, int wantTurns, int wantCasts, int wantHp, int wantRollTurns = 0)
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        int turns = 0, drawsSame = 0, castsSame = 0, journalCasts = 0, portCasts = 0, bothCast = 0, printed = 0, hpSame = 0, hpAll = 0;
        int rollTurns = 0, rollRows = 0, rollsExact = 0;
        foreach (var (session, file, stem, from, to) in Sessions.Where(x => CorpusSets.IsSynth(x.Dir) == synth))
        {
            string dir = Path.Combine(GameSaveTests.CorpusDir(), session);
            string path = Path.Combine(dir, file);
            Skip.IfNot(File.Exists(path), $"нет журнала {session}/{file}");
            bool InRange(long seq) => seq >= from && (to == 0 || seq <= to);
            var journal = Windows(path, InRange);
            var saves = Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal)
                .Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => (Prefix: System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^(.*)-t\d+-").Groups[1].Value,
                               Day: int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                              System.Globalization.CultureInfo.InvariantCulture)))
                .ToDictionary(g => g.Key, g => g.Last());
            var events = JournalCache.Read(path, ["ai_budget_spend", "ai_turn", "stack_move", "battle_end", "hire_leader", "ai_order", "terrain_day", "unit_transfer", "hire_unit"])
                                     .Where(e => InRange(e.Seq)).OrderBy(e => e.Seq).ToList();
            // Схема 7.5: броски слоя — строками ai_rng окна «начало хода → первый выбор тактики» игрока; порт бросает ими (JournalRolls).
            bool v75 = JournalCache.Read(path, "start").OfType<StartEvent>().Any(st => st.SchemaAtLeast(7, 5));
            var layerRolls = v75 ? JournalCache.Read(path, "ai_rng").OfType<AiRngEvent>().Where(r => InRange(r.Seq) && Operational(r)).OrderBy(r => r.Seq).ToList() : [];
            var windowEnds = v75 ? JournalCache.Read(path, ["ai_tactic_pick", "ai_turn"]).Where(e => InRange(e.Seq)).Select(e => e.Seq).Order().ToList() : [];
            var spends = AiSpendMemory.From(events);
            var hires = HiresOfTurns(events);
            var pendingLand = new List<TerrainDayEvent>();
            var landSinceTurn = new List<TerrainDayEvent>();
            var begun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AiLiveDay? live = null;
            D2.Rules.Map.MapState? saved = null;
            string liveMap = "";
            foreach (var e in events)
                switch (e)
                {
                    case AiTurnEvent { Phase: "begin", Cause: "ai", Player: { } player } t:
                    {
                        string prefix = player[..4];
                        var key = saves.Keys.FirstOrDefault(k => k.Day == t.Turn && MapPrefix(saves[k]) == prefix);
                        // Тот же игрок в тот же день второй раз — день переигран после загрузки: живой день заново с
                        // сохранения, земля — из terrain_day этого хода.
                        bool replay = live is not null && live.Day == t.Turn && liveMap == prefix && begun.Contains(player);
                        if (live is null || live.Day != t.Turn || liveMap != prefix || replay)
                        {
                            var looks = saves.TryGetValue(key, out var sg) && key.Day == t.Turn ? MapLooksLoader.Load(sg) : null;
                            live = looks is null ? null : new AiLiveDay(looks, (int)t.Turn, content.MapArt, book, spends);
                            saved = looks?.Map;
                            foreach (var land in (replay ? landSinceTurn : pendingLand)
                                         .Where(l => l.Turn == t.Turn && l.Player.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                                live?.Land(land);
                            pendingLand.Clear();
                            begun.Clear();
                            liveMap = prefix;
                        }
                        begun.Add(player);
                        landSinceTurn.Clear();
                        if (live is null || !journal.TryGetValue((player, (int)t.Turn), out var want)) break;
                        // D2_SPELL_SHOW=<игрок>:<день> — списки сил и крепости игрока перед ходом (для разбора).
                        if (Environment.GetEnvironmentVariable("D2_SPELL_SHOW") is { } who && who.Equals($"{player}:{t.Turn}", StringComparison.OrdinalIgnoreCase))
                            Show(live.Session(), player, book, saved!);
                        var report = new List<string>();
                        JournalRolls? rolls = null;
                        if (v75)
                        {
                            int at = windowEnds.BinarySearch(t.Seq);
                            long until = at >= 0 && at + 1 < windowEnds.Count ? windowEnds[at + 1] : long.MaxValue;
                            rolls = new JournalRolls(JournalRolls.Between(layerRolls.Where(r => string.Equals(r.Player, player, StringComparison.OrdinalIgnoreCase)),
                                                                          t.Seq, until, [.. layerRolls.Select(r => r.Site).Distinct()]),
                                                     new D2.Rules.Rng.EngineRng());
                        }
                        if (live.Begin(player, report, hireUnits: false, hires: hires.GetValueOrDefault(t.Seq), operationalRolls: rolls) is not { } game) break;
                        if (rolls is not null)
                        {
                            rollTurns++;
                            rollRows += rolls.Used + rolls.Unused;
                            if (rolls.Exact) rollsExact++;
                            else if (printed++ < 40)
                                output.WriteLine($"{session}/{stem} {player} день {t.Turn}: броски слоя 7.5 — журнал {rolls.Used + rolls.Unused}, порт {rolls.Used + rolls.Extra}, "
                                                 + $"вне границы {rolls.OutOfBound}, границы (журнал/порт) [{string.Join(" ", rolls.BoundsDiffer.Select(b => $"{b.JournalMax}/{b.PortMax}"))}]");
                        }
                        var mine = report.Where(r => r.StartsWith("заклинание ", StringComparison.Ordinal) && !r.Contains("отказ", StringComparison.Ordinal))
                                         .Select(r => string.Join(' ', r.Split(' ').Skip(2))).ToList();
                        turns++;
                        journalCasts += want.Casts.Count;
                        portCasts += mine.Count;
                        bool draws = want.Draws is null || want.Draws == game.LastOperationalDraws;
                        bool casts = mine.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(want.Casts.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                        if (draws) drawsSame++;
                        if (casts) castsSame++;
                        if (casts)
                            foreach (var (unit, after) in want.Hp)
                            {
                                hpAll++;
                                int hp = game.Map.Units.TryGetValue(unit, out var u) ? u.Hp : 0;
                                if (hp == after) hpSame++;
                                else if (printed++ < 40) output.WriteLine($"{session} {player} день {t.Turn}: здоровье {unit} журнал {after} порт {hp}");
                            }
                        if (casts && mine.Count > 0) bothCast++;
                        if ((!draws || !casts) && printed++ < 40)
                            output.WriteLine($"{session} {player} день {t.Turn}: бросков журнал {want.Draws?.ToString() ?? "?"} порт {game.LastOperationalDraws}; "
                                             + $"журнал [{string.Join(", ", want.Casts)}] порт [{string.Join(", ", mine)}] "
                                             + string.Join(" | ", report.Where(r => r.StartsWith("выбор ", StringComparison.Ordinal)
                                                                                     || (r.StartsWith("заклинание ", StringComparison.Ordinal) && r.Contains("отказ", StringComparison.Ordinal))).Take(6)));
                        break;
                    }
                    case StackMoveEvent m when live is not null:
                        live.Move(m.Stack, (int)m.X, (int)m.Y, (int)m.MoveLeft);
                        break;
                    case AiOrderEvent o when live is not null:
                        live.Order(o);
                        break;
                    case HireLeaderEvent hl when live is not null:
                        live.HireLeader(hl);
                        break;
                    case BattleEndEvent b when live is not null:
                        live.Battle(b);
                        break;
                    // Найм юнитов в гарнизоны — из журнала: тип у движка выбирает CRT rand() (поток в харнессе не выставлен), а
                    // перестановки ИИ (ai_reorder) предыдущих игроков уводят нанятых в отряды — цели литья следующего игрока.
                    case HireUnitEvent hu when live is not null:
                        live.HireUnit(hu);
                        break;
                    case UnitTransferEvent x when live is not null:
                        live.Transfer(x);
                        break;
                    case AiTurnEvent:
                        landSinceTurn.Clear();
                        break;
                    case TerrainDayEvent td:
                        landSinceTurn.Add(td);
                        if (live is not null && live.Day == td.Turn && td.Player.StartsWith(liveMap, StringComparison.OrdinalIgnoreCase)) live.Land(td);
                        else pendingLand.Add(td);
                        break;
                }
        }
        output.WriteLine($"ходов ИИ {turns}: броски сошлись {drawsSame}, литья сошлись {castsSame} (из них с литьём {bothCast}); литий журнал {journalCasts}, порт {portCasts}; "
                         + $"здоровье после литья сошлось {hpSame} из {hpAll}; на бросках журнала 7.5 ходов {rollTurns} (строк {rollRows}), точно {rollsExact}");
        Assert.Equal(wantTurns, turns);
        Assert.Equal(wantTurns, drawsSame);
        Assert.Equal(wantTurns, castsSame);
        Assert.Equal(wantCasts, journalCasts);
        Assert.Equal(wantCasts, bothCast);
        Assert.Equal(wantCasts, portCasts);
        Assert.Equal(wantHp, hpAll);
        Assert.Equal(wantHp, hpSame);
        Assert.Equal(wantRollTurns, rollTurns);
        Assert.Equal(rollTurns, rollsExact);
    }

    private void Show(GameSession game, string player, GameBook book, D2.Rules.Map.MapState saved)
    {
        var f = AiSpells.Forces(game, player);
        string Stack(string id) => game.Map.Stacks.TryGetValue(id, out var s)
            ? $"{id}@{AiTactics.Position(game.Map, s)} {s.Owner} o{s.Order}/{s.AiOrder} {AiTactics.Strength(game, s):0.#} в[{s.Inside}]" : id;
        output.WriteLine($"  свои [{string.Join("; ", f.Own.Select(Stack))}]");
        output.WriteLine($"  чужие [{string.Join("; ", f.Others.Select(Stack))}]");
        foreach (var fort in AiTactics.Forts(game.Map).Where(x => x.Owner.Equals(player, StringComparison.OrdinalIgnoreCase)))
        {
            var at = AiTactics.Entrance(fort);
            var near = game.Map.Stacks.Values.Where(s => !s.Owner.Equals(player, StringComparison.OrdinalIgnoreCase)
                                                       && Math.Max(Math.Abs(AiTactics.Position(game.Map, s).X - at.X), Math.Abs(AiTactics.Position(game.Map, s).Y - at.Y)) <= 7);
            output.WriteLine($"  крепость {fort.Id}{at} визитёр [{fort.StackId}] рядом [{string.Join("; ", near.Select(s => Stack(s.Id)))}]");
        }
        var race = book.Economy.Races[game.Map.Players[player].RaceId];
        output.WriteLine($"  раса {race.RaceType} земля {D2.Rules.Map.Economy.TerrainOfRace(race.RaceType)} союзы ["
                         + string.Join(" ", game.Map.Players.Keys.Where(p => AiTactics.Allied(game, player, p))) + "]");
        foreach (var s in game.Map.Stacks.Values.Where(s => !s.Owner.Equals(player, StringComparison.OrdinalIgnoreCase)
                                                         && game.Map.Units.TryGetValue(s.LeaderId, out var l) && book.LeaderCategoryOf(l.TypeId) is 4 or 5))
        {
            var near = game.Map.Objects.Values.Where(o => o.Kind == "CR" && Math.Max(Math.Abs(o.X - s.X), Math.Abs(o.Y - s.Y)) <= 5)
                .Select(o => $"{o.Id}({o.X},{o.Y}) земля {game.Map.At(o.X, o.Y).Terrain} (в сохранении {saved.At(o.X, o.Y).Terrain})/{game.Map.At(o.X, o.Y).Ground}");
            output.WriteLine($"  вор/жезл {Stack(s.Id)} кристаллы [{string.Join("; ", near)}]");
        }
    }

    /// <summary>Наймы хода ИИ (<c>hire_leader</c> и <c>hire_unit</c> игрока между началом его хода и следующей строкой <c>ai_turn</c>,
    /// в порядке <c>seq</c>) по <c>seq</c> начала хода: у движка они идут в стратегии, до оперативного слоя.</summary>
    internal static Dictionary<long, List<CorpusEvent>> HiresOfTurns(IEnumerable<CorpusEvent> events)
    {
        var result = new Dictionary<long, List<CorpusEvent>>();
        AiTurnEvent? turn = null;
        foreach (var e in events)
            switch (e)
            {
                case AiTurnEvent t:
                    turn = t is { Phase: "begin", Cause: "ai" } ? t : null;
                    break;
                case HireLeaderEvent h when turn is not null && string.Equals(h.Player, turn.Player, StringComparison.OrdinalIgnoreCase):
                    if (!result.TryGetValue(turn.Seq, out var list)) result[turn.Seq] = list = [];
                    list.Add(h);
                    break;
                case HireUnitEvent u when turn is not null && string.Equals(u.Player, turn.Player, StringComparison.OrdinalIgnoreCase):
                    if (!result.TryGetValue(turn.Seq, out var units)) result[turn.Seq] = units = [];
                    units.Add(u);
                    break;
            }
        return result;
    }

    /// <summary>Префикс id карты сохранения (<c>S073</c>, <c>S143</c>) — по первому игроку.</summary>
    private static string MapPrefix(string sg)
    {
        if (!Prefixes.TryGetValue(sg, out var p))
            Prefixes[sg] = p = MapLooksLoader.Load(sg).Map.Players.Keys.First()[..4];
        return p;
    }

    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Окна «стратегия → тактика» по (игрок, день): броски R250 и литья.</summary>
    private static Dictionary<(string, int), (int? Draws, List<string> Casts, List<(string Unit, int After)> Hp)> Windows(string path,
                                                                                                                   Func<long, bool> inRange)
    {
        var rows = new List<JsonElement>();
        foreach (string line in File.ReadLines(path))
        {
            if (!line.Contains("\"rng_after\"", StringComparison.Ordinal) && !line.Contains("\"ev\":\"spell_cast\"", StringComparison.Ordinal)
                && !line.Contains("\"ev\":\"ai_turn\"", StringComparison.Ordinal) && !line.Contains("\"ev\":\"hp_change\"", StringComparison.Ordinal))
                continue;
            using var doc = JsonDocument.Parse(line);
            if (inRange(doc.RootElement.GetProperty("seq").GetInt64())) rows.Add(doc.RootElement.Clone());
        }
        var result = new Dictionary<(string, int), (int?, List<string>, List<(string, int)>)>();
        (string Player, int Day)? current = null;
        long? lastRng = null;
        List<string>? casts = null;
        List<(string, int)>? hp = null;
        bool afterCast = false;
        bool open = false;
        foreach (var e in rows.OrderBy(e => e.GetProperty("seq").GetInt64()))
        {
            string ev = e.GetProperty("ev").GetString()!;
            if (ev == "ai_turn")
            {
                if (e.GetProperty("phase").GetString() == "begin" && e.GetProperty("cause").GetString() == "ai")
                {
                    current = (e.GetProperty("player").GetString()!, (int)e.GetProperty("turn").GetInt64());
                    casts = [];
                    hp = [];
                    afterCast = false;
                    open = true;
                    lastRng = e.TryGetProperty("rng_after", out var ra) && ra.ValueKind == JsonValueKind.Number ? ra.GetInt64() : null;
                }
                else if (open && current is { } c)
                {
                    // Хода тактики не было — окно без конца, бросков не считаем.
                    result[c] = (null, casts!, hp!);
                    open = false;
                }
                continue;
            }
            if (!open || current is not { } cur) continue;
            if (ev == "ai_tactic_pick")
            {
                long before = e.GetProperty("rng_before").GetInt64();
                result[cur] = (lastRng is { } l ? (int)(before - l) : null, casts!, hp!);
                open = false;
                continue;
            }
            if (ev == "spell_cast")
            {
                if (!e.GetProperty("free").GetBoolean() && e.GetProperty("player").GetString() is { } p && p.Equals(cur.Player, StringComparison.OrdinalIgnoreCase))
                {
                    casts!.Add($"{e.GetProperty("spell").GetString()} ({e.GetProperty("x").GetInt64()},{e.GetProperty("y").GetInt64()})");
                    afterCast = true;
                }
                continue;
            }
            if (ev == "hp_change")
            {
                // Урон и лечение литья идут сразу после его строки; последнее значение юнита за окно — итог.
                if (afterCast && e.TryGetProperty("unit", out var unit) && e.TryGetProperty("hp_after", out var ha) && ha.ValueKind == JsonValueKind.Number)
                {
                    hp!.RemoveAll(h => h.Item1.Equals(unit.GetString(), StringComparison.OrdinalIgnoreCase));
                    hp.Add((unit.GetString()!, (int)ha.GetInt64()));
                }
                continue;
            }
            afterCast = false;
            // Смена приказов (ai_order) идёт уже после выбора заклинаний (оперативные приказы, помощники в начале тактики)
            // и бросков не тратит — концом стратегии её не считать.
            if (ev != "ai_order" && e.TryGetProperty("rng_after", out var after) && after.ValueKind == JsonValueKind.Number) lastRng = after.GetInt64();
        }
        return result;
    }
}
