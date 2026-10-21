using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 16 <c>port-synth-16-ai-calm</c> (<c>tools/synth-maps/maps_calm.py</c>): ИИ на трудной в своих углах за стенами гор,
/// без нейтралов, руин, воров, маны и дипломатии. Ход ИИ здесь не зависит от R250: за 15 дней ни одного боя, а выборы тактики и шаги
/// стратегии на двух разных сидах партии — одни и те же, день в день. Поэтому партию порта можно сверять с партией оригинала выбор в выбор
/// до конца (карты 5 и 10 расходились после первого боя).</summary>
public partial class PortSynthMapsTests
{
    private const string CalmMap = "port-synth-16-ai-calm";

    /// <summary>Выборов тактики у каждого ИИ за 15 дней — не меньше (в день хоть один).</summary>
    private const int MinCalmPicks = 15;

    [SkippableFact]
    public void Calm_NoBattles_AiTurnIndependentOfSeed()
    {
        Skip.IfNot(MapIds().Contains(CalmMap));
        var facts = Facts(CalmMap);
        int days = int.Parse(Str(facts, "days"), System.Globalization.CultureInfo.InvariantCulture);
        var (first, firstTrace, firstBattles) = PlayCalm(days, reseed: 1);
        var (second, secondTrace, secondBattles) = PlayCalm(days, reseed: 2);
        Assert.Equal(2, first.State.Info.Difficulty);
        Assert.Equal(days, first.Map.Turn);
        Assert.Equal(0, firstBattles);
        Assert.Equal(0, secondBattles);
        Assert.False(first.Rng.TableSnapshot().AsSpan().SequenceEqual(second.Rng.TableSnapshot()), "сиды не разошлись — сверка ничего не доказывает");
        foreach (var zone in facts.GetProperty("zones").EnumerateArray())
        {
            string player = Str(zone, "player");
            var picks = firstTrace.Picks.Where(p => Same(p.Player, player)).ToList();
            Out.WriteLine($"{Str(zone, "key")} ATTITUDE {zone.GetProperty("attitude").GetInt32()}: выборов {picks.Count} — " +
                          string.Join(", ", picks.GroupBy(p => p.Action).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
            Assert.True(picks.Count >= MinCalmPicks, $"{player}: выборов {picks.Count}");
            Assert.Equal(zone.GetProperty("attitude").GetInt32(), first.Map.Players[player].Attitude);
        }
        int diff = Enumerable.Range(0, Math.Min(firstTrace.Picks.Count, secondTrace.Picks.Count))
                             .FirstOrDefault(k => firstTrace.Picks[k] != secondTrace.Picks[k], -1);
        if (diff >= 0)
            Out.WriteLine($"первое различие #{diff}: {firstTrace.Picks[diff]} против {secondTrace.Picks[diff]}");
        Assert.Equal(firstTrace.Picks, secondTrace.Picks);
        Assert.Equal(firstTrace.Steps, secondTrace.Steps);
        Out.WriteLine("шаги стратегии: " + string.Join(", ", firstTrace.Steps.GroupBy(x => x.Type).Select(g => $"{g.Key} {g.Count()}")));
        // Наём лидера — бросок R250 по годным типам роли (0x44aee1): при SUGG_LVL 10 годен только вор (один тип у расы); у порта на этой
        // карте наймов лидеров нет вовсе — печать, у движка ответ даст журнал.
        var leaders = firstTrace.Steps.Where(x => x.Type == AiStepType.HireLeader).ToList();
        Out.WriteLine($"наймов лидера: {leaders.Count}");
        Assert.All(leaders, x => Assert.Equal(AiLeaderRole.Thief, AiStrategicRules.RoleOf(first.Book!.LeaderCategoryOf(x.Target.Split('|')[0]))));
        Out.WriteLine($"выборов {firstTrace.Picks.Count}, шагов стратегии {firstTrace.Steps.Count} — на сидах 1 и 2 одни и те же");
    }

    private (GameSession Session, TacticTrace Trace, int Battles) PlayCalm(int days, uint reseed)
    {
        var session = Begin(CalmMap, 2, reseed);
        var trace = new TacticTrace();
        session.AiTrace = trace;
        var seen = new Recorder();
        session.Watch(seen);
        for (int k = 1; k < days && !session.Over; k++) session.EndTurn();
        return (session, trace, seen.Outcomes.Count);
    }

    /// <summary>Выборы тактики (отряд, действие, цель, клетка, счёт) и шаги стратегии по дням.</summary>
    private sealed class TacticTrace : IAiTrace
    {
        public List<(string Player, int Day, string Stack, string Action, string Target, int X, int Y, double Score)> Picks { get; } = [];

        public List<(string Player, int Day, AiStepType Type, string Id, string Target)> Steps { get; } = [];

        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates) =>
            Picks.Add((playerId, day, action.Stack, action.Type.ToString(), action.Target, action.X, action.Y, Math.Round(score, 2)));

        public void Stepped(string playerId, int day, AiExecuted step, int number) =>
            Steps.Add((playerId, day, step.Action.Type, step.Action.Id, step.Action.Target));

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) { }
    }
}
