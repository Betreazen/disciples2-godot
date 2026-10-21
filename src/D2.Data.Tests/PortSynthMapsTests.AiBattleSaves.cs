using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 20 против журнала и сохранений оригинала (<c>corpus/20261019b-port-synth</c>, сохранения t01-004 … t03-008, зонд 7.5):
/// ходы ИИ дней 1–2 с сохранения после боёв человека.</summary>
public partial class PortSynthMapsTests
{
    private const string AiBattleRun = "20261019b-port-synth";

    private static readonly string[] AiBattleKinds = ["save", "ai_tactic_pick", "ai_tactic_result", "potion_use", "battle_start", "hire_unit", "hire_leader"];

    /// <summary>
    /// Ходы ИИ дней 1–2 карты 20 с сохранения оригинала t01-006 (после боёв B1–B3 человека, перед «Конец хода»): каждый выбор тактики
    /// (фаза 4 → 0: отряд, тип, цель, точка, счёт) и статус исполнения по порядку, зелья вне боя (<c>potion_use</c>: отряд, предмет, юнит) —
    /// у порта те же; утром дней 2 и 3 клетки всех отрядов, расстановка групп, сумки и склады — как в сохранениях t02-007 и t03-008; наймы стратегии — те же. Ответ на вопрос P памятки: раненый
    /// <c>S143KC000a</c> пьёт оба лечебных на входе в тактику (подготовка <c>0x451b45</c>) и уходит к столице (<c>Retreat</c> → FT0000, 2000).
    /// Прогноз с начала сценария (без боёв человека, <see cref="AiBattle_Fights_RuinsAndPotions_PortForecast"/>) этого не повторяет: рядом
    /// с P стоит не побитый B2 Нежити, и P атакует его — состояние, а не правило.
    /// </summary>
    [SkippableFact]
    public void AiBattle_AiTurnsMatchTheOriginalJournal()
    {
        Skip.IfNot(MapIds().Contains(AiBattleMap));
        string dir = Path.Combine(GameSaveTests.CorpusDir(), AiBattleRun);
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), $"нет {AiBattleRun}");
        var all = JournalCache.Read(journal, AiBattleKinds).OrderBy(e => e.Seq).ToList();
        var saves = AiStrategicOrdersHarnessTests.BoundSaves(dir, "passive-001.jsonl", all.OfType<SaveEvent>().ToList())
                                                 .Where(b => Path.GetFileName(b.Path).StartsWith(AiBattleMap, StringComparison.Ordinal))
                                                 .OrderBy(b => b.Save.Seq).ToList();
        // День 1 — последнее сохранение (t01-006, после боёв), затем утро дня 2 и дня 3.
        Skip.If(!saves.Any(s => s.Save.Turn == 1) || !saves.Any(s => s.Save.Turn == 3), $"сохранений карты 20: {saves.Count}");
        var start = saves.Last(s => s.Save.Turn == 1);
        var mornings = new[] { saves.First(s => s.Save.Turn == 2), saves.First(s => s.Save.Turn == 3) };
        Assert.True(start.Save.Seq < mornings[0].Save.Seq && mornings[0].Save.Seq < mornings[1].Save.Seq, "сохранения карты 20 не по порядку одной партии");
        var events = all.Where(e => e.Seq > start.Save.Seq && e.Seq < mornings[^1].Save.Seq).ToList();
        Assert.Empty(events.OfType<BattleStartEvent>());   // в ходах ИИ боёв нет — броски боя не сдвигают сверку
        string human = Str(Facts(AiBattleMap).GetProperty("human"), "player");
        // Человек в эти дни только жмёт «Конец хода» (памятка): его наймов и зелий порт не повторяет — их не должно быть.
        Assert.DoesNotContain(events, e => e is PotionUseEvent pu && Same(pu.Player, human) || e is HireUnitEvent hu && Same(hu.Player, human)
                                           || e is HireLeaderEvent hl && Same(hl.Player, human));

        // Счёт сверяется числом: трасса порта (TacticTrace) хранит его округлённым до сотых, журнал — полным (−184,6625), допуск — полсотой.
        var engine = new List<(string Pick, double Score)>();
        double pendingScore = 0;
        string? pending = null;
        foreach (var e in events)
        {
            if (e is AiTacticPickEvent { PhaseBefore: 4, Chosen: [{ Action: { } a } c, ..] } p)
            {
                Assert.True(pending is null, $"выбор без строки ai_tactic_result: {pending}");
                Assert.True(c.Score is not null, $"выбор без счёта: seq {p.Seq}");
                pending = $"{p.Player} {a.Stack} {AiRowsNames.GetValueOrDefault(a.Type ?? -1, $"{a.Type}")} {a.Target} ({a.Point?.X},{a.Point?.Y})";
                pendingScore = c.Score!.Value;
            }
            else if (e is AiTacticResultEvent r && pending is not null)
            {
                engine.Add(($"{pending} → {r.Status}", pendingScore));
                pending = null;
            }
        }
        var enginePotions = events.OfType<PotionUseEvent>().Where(x => !x.InBattle && x.Ret)
                                  .Select(x => $"зелье {x.Group} {x.Item} → {x.Target}".ToUpperInvariant()).ToList();
        // До проверки — чтобы смена корпуса падала понятно: выборов ИИ дней 1–2 23, зелий P 2.
        Assert.Equal(23, engine.Count);
        Assert.Equal(2, enginePotions.Count);

        // Каждый день — с сохранения движка (t01-006, затем t02-007) и с потоком CRT движка перед первым ходом ИИ этого дня (JournalCrt):
        // им стратегия выбирает тип найма в гарнизон (0x44c137), а сохранение его не хранит — бои человека и ходы прежних дней его сдвигают.
        var crt = JournalCrt.Of(journal);
        var turns = JournalCache.Read(journal, ["ai_turn"]).OfType<AiTurnEvent>().Where(t => t.Phase == "begin").OrderBy(t => t.Seq).ToList();
        var trace = new TacticTrace();
        var port = new List<(string Pick, double Score)>();
        var portPotions = new List<string>();
        var portHires = new List<string>();
        var diffs = new List<string>();
        var from = start;
        GameSession? yesterday = null;
        foreach (var morning in mornings)
        {
            var first = turns.First(t => t.Seq > from.Save.Seq);
            var looks = MapLooksLoader.Load(from.Path);
            var gameEvents = EventLoader.Load(from.Path);
            var state = new GameState(new GameInfo(AiBattleMap, AiBattleMap, [human], 1, 1), looks)
            {
                Events = gameEvents, EventProgress = EventLoader.LoadProgress(from.Path, gameEvents),
            };
            var rng = new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book).Rng;
            state = state with { Random = new RandomStreams(rng.Index, rng.TableSnapshot(), crt.Before(first.Tid, first.Seq)) };
            // Память статусов тактики (вчерашний статус 5 — ×1,25 к счёту) в .sg не пишется — она из хода прошлого дня порта.
            if (yesterday is not null) state = state with { Tactic = yesterday.State.Tactic };
            var session = new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book) { EventBattlesOnScreen = false, AiTrace = trace };
            int already = trace.Picks.Count;
            var report = session.EndTurn();
            var statuses = report.Events.Where(l => l.StartsWith("тактика ", StringComparison.Ordinal))
                                        .Select(l => l[(l.LastIndexOf('→') + 2)..]).ToList();
            portPotions.AddRange(report.Events.Where(l => l.StartsWith("зелье ", StringComparison.Ordinal) && !l.Contains("отказ", StringComparison.Ordinal))
                                              .Select(l => l.ToUpperInvariant()));
            portHires.AddRange(report.Events.Select(l => l.Split(' ')).Where(q => q.Length == 4 && q[1] is ("HireUnit" or "HireLeader"))
                                            .Select(q => $"{q[0]} {q[2].Split('|')[0]} {q[3]}".ToUpperInvariant()));
            var picks = trace.Picks.Skip(already).ToList();
            Assert.Equal(picks.Count, statuses.Count);
            for (int i = 0; i < picks.Count; i++)
                port.Add(($"{picks[i].Player} {picks[i].Stack} {picks[i].Action} {picks[i].Target} ({picks[i].X},{picks[i].Y}) → {statuses[i]}", picks[i].Score));
            var map = MapLooksLoader.Load(morning.Path).Map;
            // Клетки отрядов и гарнизонов, порядок сумок и складов (расстановка ИИ, LayoutRows карты 21).
            diffs.AddRange(LayoutRows(map, session.Map).Select(r => $"утро дня {morning.Save.Turn}: {r}"));
            foreach (var s in map.Stacks.Values)
            {
                if (!session.Map.Stacks.TryGetValue(s.Id, out var mine)) diffs.Add($"утро дня {morning.Save.Turn}: у порта нет {s.Id}");
                else if ((mine.X, mine.Y, mine.Inside) != (s.X, s.Y, s.Inside))
                    diffs.Add($"утро дня {morning.Save.Turn}: {s.Id} движок ({s.X},{s.Y}) {s.Inside}, порт ({mine.X},{mine.Y}) {mine.Inside}");
            }
            foreach (var s in session.Map.Stacks.Values.Where(s => !map.Stacks.ContainsKey(s.Id)))
                diffs.Add($"утро дня {morning.Save.Turn}: у движка нет {s.Id}");
            from = morning;
            yesterday = session;
        }
        var engineHires = all.Where(e => e.Seq > start.Save.Seq && e.Seq < mornings[^1].Save.Seq)
                             .Select(e => e switch
                             {
                                 HireUnitEvent { Ret: true } h => $"{h.Player} {h.UnitType} {h.Group}".ToUpperInvariant(),
                                 HireLeaderEvent { Ret: true } h => $"{h.Player} {h.UnitType} {h.Fort}".ToUpperInvariant(),
                                 _ => null,
                             }).OfType<string>().ToList();
        Assert.Equal(26, engineHires.Count);
        Out.WriteLine($"наймы: движок [{string.Join("; ", engineHires)}], порт [{string.Join("; ", portHires)}]");
        if (!engineHires.SequenceEqual(portHires)) diffs.Add($"наймы: движок [{string.Join("; ", engineHires)}], порт [{string.Join("; ", portHires)}]");
        foreach (var l in engine) Out.WriteLine($"движок {l.Pick} {l.Score:0.###}");
        foreach (var l in port) Out.WriteLine($"порт   {l.Pick} {l.Score:0.###}");
        for (int i = 0; i < Math.Max(engine.Count, port.Count); i++)
        {
            var (a, sa) = i < engine.Count ? engine[i] : ("—", 0);
            var (b, sb) = i < port.Count ? port[i] : ("—", 0);
            if (a != b || !(Math.Abs(sa - sb) <= 0.0051)) diffs.Add($"выбор {i + 1}: движок {a} {sa:0.###}; порт {b} {sb:0.###}");
        }
        Out.WriteLine($"зелья: движок [{string.Join("; ", enginePotions)}], порт [{string.Join("; ", portPotions)}]");
        if (!enginePotions.SequenceEqual(portPotions)) diffs.Add($"зелья: движок [{string.Join("; ", enginePotions)}], порт [{string.Join("; ", portPotions)}]");
        Out.WriteLine($"выборов {engine.Count}, расхождений {diffs.Count}");
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs.Take(40)));
    }
}
