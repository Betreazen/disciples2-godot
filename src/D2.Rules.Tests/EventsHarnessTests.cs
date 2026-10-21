using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Map;
using D2.Rules.Rng;
using Xunit.Abstractions;
using KSg = Kaitai.Sg;

namespace D2.Rules.Tests;

/// <summary>
/// События сценария против живых партий (<c>docs/scenario-events.md</c>, <c>docs/day-cycle.md</c> §2): между соседними
/// сохранениями журнал проигрывается через движок событий порта (<see cref="ScenarioEvents.Next"/>) — начало хода игрока
/// (<c>spell_expire</c>), шаги отрядов (<c>stack_move</c>, отряд — триггерер), захваты (<c>fort_capture</c>), бои
/// (<c>battle_end</c>, победитель — триггерер); предсказанные попапы сравниваются с новыми записями квест-лога
/// (<c>MidQuestLog</c>) следующего сохранения: игрок, событие, номер эффекта, день.
/// </summary>
[Trait("Category", "Corpus")]
public class EventsHarnessTests(ITestOutputHelper output)
{
    /// <summary>Измерено 25.09: все попапы квест-лога повторены, лишних нет. Проверяются <c>L_FREQUENCY</c> (интро дня 1 у всех
    /// рас), <c>L_LOCATION</c> (шаги отрядов людей и ИИ), <c>L_OWN_CITY</c> (захваты), <c>L_LOOT_RUIN</c> (победа над охраной
    /// руины) и получатели попапа.</summary>
    [SkippableTheory]
    [InlineData("20260919-human", 18)]
    [InlineData("20260920-legions", 15)]
    [InlineData("20260918-probe65", 12)]
    [InlineData("20260920-gaps", 9)]
    [InlineData("20260919-f7", 0)]
    [InlineData("20260919-f7b", 0)]
    [InlineData("20261019c-cov", 6)]   // 19.10 (r19b/cov): партия с покрытием (Greenskins, 18 дней) — окон 19, попапов 6 из 6
    public void Popups_MatchQuestLog(string corpus, int wantSame) => Popups(corpus, wantSame);

    /// <summary>То же на партии синтетических карт — группа <c>Synth</c>.</summary>
    [SkippableTheory, Trait("Category", "Synth")]
    [InlineData("20261020-port-synth", 0)]   // карта 23 (60 дней): окон 59, попапов 0 — победы нет, событий с окном нет; лишних 0
    [InlineData("20261016b-port-synth", 0)]   // карта 3: все 27 окон дня 1 — до первого снимка; дальше окна без попапов (R250 — тихое событие)
    public void Popups_MatchQuestLog_Synth(string corpus, int wantSame) => Popups(corpus, wantSame);

    private void Popups(string corpus, int wantSame)
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        string dir = Path.Combine(CorpusFiles.Dir, corpus);
        Skip.IfNot(Directory.Exists(dir), $"нет корпуса {corpus}");
        var economy = EconomyLoader.Load(GamePaths.Globals);
        var rules = new EventRules(economy.Races, DiplomacyRules.Vanilla);
        int same = 0, missing = 0, extra = 0, windows = 0;
        foreach (string path in Directory.GetFiles(dir, "passive-*.jsonl").Order(StringComparer.Ordinal))
        {
            var j = TreasuryJournal.Load(path);
            var saves = j.Saves.Where(s => s.Seq is not null).OrderBy(s => s.Seq).ToList();
            var lines = Lines(path);
            for (int i = 0; i + 1 < saves.Count; i++)
            {
                var (a, b) = (saves[i], saves[i + 1]);
                if (j.EpochOf(a.T) != j.EpochOf(b.T)) continue;
                var got = Replay(a, lines.Where(e => e.Seq > a.Seq && e.Seq < b.Seq), rules);
                var before = Log(a.Path).ToHashSet();
                var want = Log(b.Path).Where(l => !before.Contains(l)).ToHashSet();
                windows++;
                same += got.Intersect(want).Count();
                foreach (var m in want.Except(got))
                    if (++missing <= 15) output.WriteLine($"{Path.GetFileName(a.Path)} → {Path.GetFileName(b.Path)}: нет у порта {m}");
                foreach (var x in got.Except(want))
                    if (++extra <= 15) output.WriteLine($"{Path.GetFileName(a.Path)} → {Path.GetFileName(b.Path)}: лишнее у порта {x}");
            }
        }
        output.WriteLine($"{corpus}: окон {windows}, попапов сошлось {same}, нет у порта {missing}, лишних {extra}");
        Assert.Equal((wantSame, 0, 0), (same, missing, extra));
    }

    /// <summary>
    /// Событие «шанс каждый ход» (<c>port-synth-3-events</c>, EV001b: <c>L_FREQUENCY</c> 1, <c>CHANCE</c> 50, <c>PS_CHANCE += 1</c>) против
    /// оригинала на том же генераторе: состояние R250 — из строки <c>rng_seed</c> загрузки карты (<see cref="C2HarnessTests.StartFromSeed"/>
    /// по первым броскам боя дня 1), затем вызов за вызовом до проверки начала хода человека дней 2 и 3 — единственного вызова R250 в окне
    /// конца хода нейтралов (<c>ai_turn</c> <c>rng_before</c>/<c>rng_after</c>). Известные по пути броски (<c>rng</c> боёв, подарок ИИ
    /// <c>ai_gift_check</c>: <c>R250(100)</c> и вид <c>R250(3)</c>) сверяются — поток идёт шаг в шаг; неизвестные вызовы — по шагу. В точке
    /// проверки порт сам решает события (<see cref="ScenarioEvents.Next"/> по сохранению прошлого дня с ходом нового) — сработало ли EV001b
    /// и сколько шагов взяло, — против переменной <c>PS_CHANCE</c> сохранений (1, 1, 1: дни 2 и 3 — без срабатывания).
    /// </summary>
    [SkippableFact, Trait("Category", "Synth")]
    public void ChanceEvent_R250OnEngineSeed_MatchesVariable()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        string path = Path.Combine(CorpusFiles.Dir, "20261016b-port-synth", "passive-001.jsonl");
        Skip.IfNot(File.Exists(path), "нет корпуса 20261016b-port-synth");
        var economy = EconomyLoader.Load(GamePaths.Globals);
        var rules = new EventRules(economy.Races, DiplomacyRules.Vanilla);
        var all = JournalCache.Read(path, ["rng_seed", "rng", "ai_gift_check", "ai_turn"]).OrderBy(e => e.Seq).ToList();
        var seeds = all.OfType<RngSeedEvent>().Where(x => x.Cause == "new_game_time").ToList();
        var (seed, end) = (seeds[2], seeds[3].Seq);   // загрузки: карта 1, 2, 3, 4
        var window = all.Where(e => e.Seq > seed.Seq && e.Seq < end).ToList();

        // Известные вызовы R250 по номеру n (после вызова): броски боёв и подарков ИИ.
        var known = new SortedDictionary<long, (int Max, int R)>();
        foreach (var r in window.OfType<RngEvent>().Where(r => !r.Std)) known[r.N] = ((int)r.Max, (int)r.R);
        foreach (var g in window.OfType<AiGiftCheckEvent>().Where(g => g.Roll is not null && g.RngAfter > g.RngBefore))
        {
            known[g.RngBefore + 1] = (100, (int)g.Roll!.Value);
            if (g.KindRoll is { } kind) known[g.RngBefore + 2] = (3, (int)kind);
        }
        // Проверка событий в начале хода человека: единственный вызов в окне конца хода нейтралов.
        var checks = window.OfType<AiTurnEvent>()
            .Where(t => t.Phase == "end" && t.Player == "S143PL0000" && t.RngAfter - t.RngBefore == 1)
            .ToDictionary(t => t.RngAfter, t => (int)t.Turn + 1);
        var battle = window.OfType<RngEvent>().Where(r => !r.Std).OrderBy(r => r.N).ToList();
        Assert.NotEmpty(battle);
        var rng = C2HarnessTests.StartFromSeed(seed, battle.TakeWhile((r, k) => k == 0 || r.N == battle[k - 1].N + 1).ToList());
        Assert.NotNull(rng);
        output.WriteLine($"сид seq {seed.Seq} n {seed.N}: первый бросок боя n {battle[0].N} — шагов от сида {rng!.Steps}, вызовов {battle[0].N - seed.N - 1}");
        // День 1: проверка начала хода человека идёт сразу за загрузкой — первый вызов после сида (справочно: строки со счётчиком между
        // сидом и проверкой нет, а за днём 1 — ~456 тыс. вызовов интерфейса).
        int day1 = EngineRng.FromSeed(seed.Table, (int)seed.IndexAfter).Next(ScenarioEvents.ChanceDie);
        Assert.True(day1 <= 50);   // день 1: PS_CHANCE 0 → 1 (снимок t01) — срабатывание
        output.WriteLine($"день 1 (первый вызов после сида, n {seed.N + 1}): R250(100) = {day1} — {(day1 <= 50 ? "срабатывание" : "нет")}");

        var saves = TreasuryJournal.Load(path).Saves
            .Where(x => Path.GetFileName(x.Path).StartsWith("port-synth-3-events", StringComparison.Ordinal)).OrderBy(x => x.T).ToList();
        Assert.Equal(3, saves.Count);   // по сохранению на день 1–3 — индексы дня ниже на это опираются
        output.WriteLine($"сохранения: {string.Join(", ", saves.Select(x => $"{Path.GetFileName(x.Path)} PS_CHANCE {ChanceVariable(x.Path)}"))}");
        int matched = 0, mismatched = 0, decided = 0;
        long last = checks.Keys.Where(n => checks[n] <= saves.Count).Max();
        for (long n = battle[0].N; n <= last; n++)
        {
            if (checks.TryGetValue(n, out int day))
            {
                var save = saves[day - 2];   // сохранение дня day − 1 (конец хода человека) — до проверки начала дня day
                var set = EventLoader.Load(save.Path);
                var progress = EventProgress.From(set) with { Variables = new Dictionary<int, int>(set.Variables) };
                var probe = rng.Clone();
                int before = probe.Steps;
                var fired = ScenarioEvents.Next(set, progress, MapOf(save, day), rules, "S143PL0001", "", probe,
                                                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                int value = rng.Clone().Next(ScenarioEvents.ChanceDie);
                bool portFired = fired.Any(f => f.Event.Id.Equals("S143EV001b", StringComparison.OrdinalIgnoreCase));
                int was = ChanceVariable(save.Path) ?? -1, now = ChanceVariable(saves[day - 1].Path) ?? -1;
                output.WriteLine($"день {day}: вызов n {n}, R250(100) = {value}, порт: сработало {portFired}, шагов {probe.Steps - before}; " +
                                 $"движок: PS_CHANCE {was} → {now}");
                Assert.Equal(1, probe.Steps - before);   // у движка в окне ровно один вызов R250
                Assert.Equal(value <= 50, portFired);    // правило порта: R250(100) ≤ CHANCE
                Assert.Equal(now > was, portFired);      // и исход движка по переменной
                decided++;
                rng.Next(ScenarioEvents.ChanceDie);
                continue;
            }
            if (known.TryGetValue(n, out var draw))
            {
                if (rng.Next(draw.Max) == draw.R) matched++;
                else mismatched++;
                continue;
            }
            rng.Step();
        }
        output.WriteLine($"известных бросков по пути: сошлось {matched}, разошлось {mismatched}; проверок события {decided}");
        Assert.Equal(0, mismatched);
        Assert.True(matched > 0);
        Assert.Equal(2, decided);
    }

    /// <summary>Переменная, которую двигает событие <c>S143EV001b</c> (<c>PS_CHANCE</c>), в сохранении; нет — null.</summary>
    private static int? ChanceVariable(string sg)
    {
        var set = EventLoader.Load(sg);
        var modify = set.Events.FirstOrDefault(e => e.Id.Equals("S143EV001b", StringComparison.OrdinalIgnoreCase))
                        ?.Effects.OfType<ModifyVariableEffect>().FirstOrDefault();
        return modify is not null && set.Variables.TryGetValue(modify.Variable, out int v) ? v : null;
    }

    /// <summary>Состояние карты сохранения с ходом <paramref name="turn"/> — начало хода следующего дня.</summary>
    private static MapState MapOf(SaveAt save, int turn)
    {
        var map = save.Map;
        return new MapState
        {
            Size = map.Size, Tiles = map.Tiles, Stacks = map.Stacks, Units = map.Units, Players = map.Players, Objects = map.Objects,
            Plan = map.Plan, Fog = map.Fog, Turn = turn, Rods = map.Rods, Sites = map.Sites, Relations = map.Relations,
            ItemTypes = map.ItemTypes, Limits = map.Limits, Subraces = map.Subraces, Destroyed = map.Destroyed,
        };
    }

    /// <summary>Журнал окна через движок событий: состояние — из сохранения <paramref name="a"/>, позиции и владельцы —
    /// по строкам журнала.</summary>
    private static HashSet<string> Replay(SaveAt a, IEnumerable<CorpusEvent> window, EventRules rules)
    {
        var set = EventLoader.Load(a.Path);
        var progress = EventProgress.From(set) with { Variables = new Dictionary<int, int>(set.Variables) };
        var map = a.Map;
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var sites = map.Sites.ToList();
        int turn = map.Turn;
        var popups = new HashSet<string>();
        BattleStartEvent? battle = null;
        var rng = new ScriptedRng(Enumerable.Repeat(0, 1_000_000));
        MapState Now() => new()
        {
            Size = map.Size, Tiles = map.Tiles, Stacks = stacks, Units = map.Units, Players = map.Players, Objects = map.Objects,
            Plan = map.Plan, Fog = map.Fog, Turn = turn, Rods = map.Rods, Sites = sites, Relations = map.Relations,
            ItemTypes = map.ItemTypes, Limits = map.Limits, Subraces = map.Subraces, Destroyed = map.Destroyed,
        };
        void Check(string turnPlayer, string triggerer)
        {
            var executed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var now = Now();
                var fired = ScenarioEvents.Next(set, progress, now, rules, turnPlayer, triggerer, rng, executed);
                if (fired.Count == 0) return;
                foreach (var f in fired)
                {
                    if (f.DisablesItself) progress = progress.Enable(f.Event.Id, false);
                    for (int n = 0; n < f.Event.Effects.Count; n++)
                        switch (f.Event.Effects[n])
                        {
                            case PopupEffect p:
                                foreach (string to in ScenarioEvents.PopupRecipients(p, f.Event, f.PlayerId, now, rules))
                                    popups.Add(Key(to, now.Turn, f.Event.Id, n));
                                break;
                            case EnableEventEffect en:
                                progress = progress.Enable(en.EventId, en.Enable);
                                break;
                            case ModifyVariableEffect v:
                                progress = progress.Modify(v);
                                break;
                        }
                }
            }
        }
        foreach (var e in window.OrderBy(e => e.Seq))
        {
            switch (e)
            {
                case StackMoveEvent m when stacks.TryGetValue(m.Stack, out var s):
                    var fort = sites.FirstOrDefault(x => x.Kind is SiteKind.Capital or SiteKind.Village && x.X == m.X && x.Y == m.Y);
                    stacks[s.Id] = s with { X = (int)m.X, Y = (int)m.Y, Inside = fort?.Id ?? "" };
                    Check("", s.Id);
                    break;
                case BattleStartEvent b:
                    battle = b;
                    break;
                case BattleEndEvent b when stacks.TryGetValue(b.Winner, out var winner):
                    // Победа над охраной руины — руина разграблена победителем до проверки событий (эффекты боя идут раньше).
                    if (battle is { } start && start.AttackerGroup.Equals(b.Winner, StringComparison.OrdinalIgnoreCase))
                        sites = [.. sites.Select(x => x.Id.Equals(start.DefenderGroup, StringComparison.OrdinalIgnoreCase) && x.Kind == SiteKind.Ruin
                                                    ? x with { LooterId = winner.Owner } : x)];
                    Check("", b.Winner);
                    break;
                case SpellExpireEvent x:
                    turn = (int)x.Turn;
                    Check(x.Player, "");
                    break;
                case FortCaptureEvent c when c.OwnerAfter.Length > 0:
                    sites = [.. sites.Select(x => x.Id.Equals(c.Fort, StringComparison.OrdinalIgnoreCase) ? x with { Owner = c.OwnerAfter } : x)];
                    break;
            }
        }
        return popups;
    }

    /// <summary>Строки журнала, нужные проигрышу событий, по <c>seq</c>; разбираются раз на процесс.</summary>
    private static IReadOnlyList<CorpusEvent> Lines(string path) =>
        Cache.GetOrAdd(Path.GetFullPath(path), p => new Lazy<IReadOnlyList<CorpusEvent>>(
            () => [.. JournalCache.Read(p, ["stack_move", "spell_expire", "fort_capture", "battle_start", "battle_end"]).OrderBy(e => e.Seq)])).Value;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IReadOnlyList<CorpusEvent>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string player, int turn, string eventId, int effect) =>
        $"{player.ToUpperInvariant()} день {turn} {eventId.ToUpperInvariant()} эфф {effect}";

    private static IEnumerable<string> Log(string sg) =>
        SgFile.Open(sg).Raw.Objects.Select(o => o.Data).OfType<KSg.MidQuestLog>().SelectMany(l => l.Sequences)
              .Select(q => Key(q.IdPlayer.String, (int)q.CurTurn.Value, q.IdEvent.String, (int)q.EffNum.Value));
}
