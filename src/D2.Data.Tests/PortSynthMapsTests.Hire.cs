using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 21 <c>port-synth-21-ai-hire</c> (<c>tools/synth-maps/maps_hire.py</c>): стратегия ИИ без боёв и дипломатии, как карта 16, но
/// с наймом вора посреди наймов юнитов (имя лидера — CRT, реестр И43), захватом и ростом пустых деревень (сетка влияния и счёт мешков,
/// И20/И44), расстановкой бойца из трёх ближних и лучника и складом столицы (И33). Ход ИИ не зависит от R250: на двух сидах партии выборы
/// и шаги одни и те же, боёв нет — партию порта можно сверять с оригиналом выбор в выбор (<see cref="Hire_PlayMatchesOriginalJournalAndSaves"/>).</summary>
public partial class PortSynthMapsTests
{
    private const string HireMap = "port-synth-21-ai-hire";

    [SkippableFact]
    public void Hire_NoBattles_ThiefGrowBags_IndependentOfSeed()
    {
        Skip.IfNot(MapIds().Contains(HireMap));
        var facts = Facts(HireMap);
        int days = int.Parse(Str(facts, "days"), System.Globalization.CultureInfo.InvariantCulture);
        var (first, firstTrace, firstBattles) = PlayMap(HireMap, days, reseed: 1);
        var (second, secondTrace, secondBattles) = PlayMap(HireMap, days, reseed: 2);
        Assert.Equal(2, first.State.Info.Difficulty);
        Assert.Equal(days, first.Map.Turn);
        Assert.Equal(0, firstBattles);
        Assert.Equal(0, secondBattles);
        Assert.False(first.Rng.TableSnapshot().AsSpan().SequenceEqual(second.Rng.TableSnapshot()), "сиды не разошлись — сверка ничего не доказывает");
        var start = Begin(HireMap, 2);
        var buildings = start.Book!.Economy.Buildings;
        foreach (var zone in facts.GetProperty("zones").EnumerateArray())
        {
            string player = Str(zone, "player"), key = Str(zone, "key");
            var picks = firstTrace.Picks.Where(p => Same(p.Player, player)).ToList();
            var steps = firstTrace.Steps.Where(x => Same(x.Player, player)).ToList();
            Out.WriteLine($"{key}: выборов {picks.Count} — " +
                          string.Join(", ", picks.GroupBy(p => p.Action).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
            Out.WriteLine($"{key}: шагов {steps.Count} — " + string.Join(", ", steps.GroupBy(x => x.Type).Select(g => $"{g.Key} {g.Count()}")));
            // Шаги дня найма вора по порядку: наймы юнитов (CRT типа) до и после имени вора (CRT) — то, что сверит партия оригинала.
            var leaders = steps.Where(x => x.Type == AiStepType.HireLeader).ToList();
            Assert.Single(leaders);   // вор — единственный найм лидера (роль по долям 0,33 при бойцах 2, жезлоносце 1, ворах 0; вор у расы один)
            Out.WriteLine($"  день {leaders[0].Day}: " + string.Join(", ", steps.Where(x => x.Day == leaders[0].Day)
                                                                                      .Select(x => $"{x.Type} {x.Id} {x.Target}")));
            foreach (var x in steps.Where(x => x.Type == AiStepType.GrowCities)) Out.WriteLine($"  день {x.Day} рост {x.Id}");
            Assert.Equal(Str(zone, "thief"), leaders[0].Target.Split('|')[0], StringComparer.OrdinalIgnoreCase);
            int thiefAt = steps.IndexOf(leaders[0]);
            // Вор — в списке найма только при гильдии (0x5d4857). Ответ движка 19.10 (20261019b-port-synth): Легионы с гильдией с начала нанимают
            // вора днём 1, проходом 7, и следом юнита (CRT имени, затем CRT типа); Кланы и Нежить строят гильдию днём 1 и нанимают вора днём 2.
            if (Economy.HasGuild(start.Map.Players[player].Buildings, buildings))
            {
                Assert.Equal(1, leaders[0].Day);
                Assert.Contains(steps.Skip(thiefAt + 1), x => x.Type == AiStepType.HireUnit && x.Day == leaders[0].Day);
            }
            else
            {
                int guildAt = steps.FindIndex(x => x.Type == AiStepType.Capital
                                                   && (buildings.TryGetValue(x.Target, out var b) || buildings.TryGetValue(x.Id, out b))
                                                   && b.Category == Economy.GuildCategory);
                Assert.InRange(guildAt, 0, thiefAt - 1);
                Assert.True(leaders[0].Day > steps[guildAt].Day, $"{key}: вор днём {leaders[0].Day}, гильдия днём {steps[guildAt].Day}");
            }
            // Своя деревня растёт; пустые нейтральные порт за 12 дней не берёт (у движка ответ даст журнал: fort_capture).
            Assert.Contains(steps, x => x.Type == AiStepType.GrowCities);
            foreach (string v in new[] { "near", "far" })
            {
                string id = Str(zone.GetProperty("villages"), v);
                Out.WriteLine($"  деревня {v} {id}: хозяин {first.Map.Sites.Single(x => Same(x.Id, id)).Owner}");
            }
            Assert.True(picks.Count(p => p.Action == "TakeBag") >= 2, $"{key}: TakeBag {picks.Count(p => p.Action == "TakeBag")}");
        }
        int diff = Enumerable.Range(0, Math.Min(firstTrace.Picks.Count, secondTrace.Picks.Count))
                             .FirstOrDefault(k => firstTrace.Picks[k] != secondTrace.Picks[k], -1);
        if (diff >= 0) Out.WriteLine($"первое различие #{diff}: {firstTrace.Picks[diff]} против {secondTrace.Picks[diff]}");
        Assert.Equal(firstTrace.Picks, secondTrace.Picks);
        Assert.Equal(firstTrace.Steps, secondTrace.Steps);
        Out.WriteLine($"выборов {firstTrace.Picks.Count}, шагов стратегии {firstTrace.Steps.Count} — на сидах 1 и 2 одни и те же");
    }

    private (GameSession Session, TacticTrace Trace, int Battles) PlayMap(string map, int days, uint reseed)
    {
        var session = Begin(map, 2, reseed);
        var trace = new TacticTrace();
        session.AiTrace = trace;
        var seen = new Recorder();
        session.Watch(seen);
        for (int k = 1; k < days && !session.Over; k++) session.EndTurn();
        return (session, trace, seen.Outcomes.Count);
    }
}
