using System.Text.Json;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 5 <c>port-synth-5-ai-hard</c> (<c>tools/synth-maps/maps_ai_hard.py</c>): ИИ на трудной с <c>ATTITUDE</c> 4–6, подарок
/// золотом, <c>SummonAttack</c> призванного без цели и обрыв пути ИИ событием на клетке перед целью — в модели порта всё это складывается
/// за дни памятки.</summary>
public partial class PortSynthMapsTests
{
    private const string AiHardMap = "port-synth-5-ai-hard";

    /// <summary>Дни памятки на трудной: у каждого ИИ — выбор тактики по весам своего <c>ATTITUDE</c> (у 4–6 веса городов и жезлов — 0, у 6
    /// и руин; счёт аддитивный — <c>I_P_ROD + 4g − 3d</c>, поэтому жезл по сетке влияния и при нуле выпадает); призванный с приказом
    /// «обычный» выбирает <c>SummonAttack</c>; хоть один ИИ дарит Эльфам золото (<c>CAiDiploGold</c>: предмета и заклинания для сделки у
    /// ИИ нет).</summary>
    [SkippableFact]
    public void AiHard_AttitudesHold_SummonAttacks_AndGoldIsGiven()
    {
        Skip.IfNot(MapIds().Contains(AiHardMap));
        var facts = Facts(AiHardMap);
        int days = int.Parse(facts.GetProperty("days").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var (session, trace) = PlayAiHard(facts, days, reseed: null);
        Assert.Equal(2, session.State.Info.Difficulty);
        foreach (var zone in facts.GetProperty("zones").EnumerateArray())
        {
            string player = zone.GetProperty("player").GetString()!;
            int attitude = zone.GetProperty("attitude").GetInt32();
            Assert.Equal(attitude, session.Map.Players[player].Attitude);
            var picks = trace.Picks.Where(p => Same(p.Player, player)).ToList();
            Out.WriteLine($"{zone.GetProperty("key").GetString()} ATTITUDE {attitude}: " +
                          string.Join(", ", picks.GroupBy(p => p.Action).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
            Assert.True(picks.Count >= 10, $"{player}: выборов {picks.Count}");
            var weights = session.Book!.AiWeights[attitude];
            Assert.Equal((0, 0, 0, 0), (weights.CitiesN, weights.CitiesR, weights.CitiesP, weights.PlantRod));
            Assert.Equal(attitude == 6 ? 0 : 100, weights.Ruins);
            string summon = zone.GetProperty("summon").GetString()!;
            Assert.Contains(picks, p => p.Action == "SummonAttack" && Same(p.Stack, summon));
        }
        // Подарок золотом: на ИИ в день ≈ 0,95 · 0,178 · ⅓ (бросок подарка при отношении 89, вид — золото), и его заслоняет предложение
        // союза (5 %) — за 15 дней у трёх ИИ подарок бывает не на каждом сиде; ищется сид, на котором он есть.
        string human = session.ActiveHuman;
        var found = Gifts(trace, human);
        uint seed = 0;
        while (found.Count == 0 && seed < 8)
        {
            (_, var again) = PlayAiHard(facts, days, ++seed);
            found = Gifts(again, human);
        }
        Out.WriteLine($"подарки (сид {(seed == 0 ? "партии" : seed)}): " + string.Join(", ", found.Select(g => $"день {g.Day} {g.Player}")));
        Assert.NotEmpty(found);
    }

    private (GameSession Session, StepTrace Trace) PlayAiHard(JsonElement facts, int days, uint? reseed)
    {
        var session = Begin(AiHardMap, facts.GetProperty("human").GetProperty("difficulties")[0].GetInt32(), reseed);
        var trace = new StepTrace();
        session.AiTrace = trace;
        for (int k = 1; k < days && !session.Over; k++) session.EndTurn();
        return (session, trace);
    }

    private static List<(string Player, int Day, AiExecuted Step)> Gifts(StepTrace trace, string human) =>
        [.. trace.Steps.Where(s => s.Step.Action.Type == AiStepType.Diplomacy && s.Step.Action.Target == nameof(AiDiploAction.Gift)
                                   && Same(s.Step.Action.Id, human))];

    /// <summary>Обрыв пути ИИ событием на клетке перед целью (ветка <c>fix/aicut</c>, <c>0x43fc3c</c>): у каждого ИИ событие места вокруг
    /// нейтрала-цели срабатывает от его отряда, и ход, на котором оно сработало, кончается на клетке вплотную к цели — без нападения (остались
    /// очки хода — отряд оценивается снова и может напасть тем же днём); место вокруг мешка срабатывает хоть у одного ИИ (мешок ИИ берёт не
    /// всегда).</summary>
    [SkippableFact]
    public void AiHard_EventCutsAiPath_OnTheCellBeforeTheTarget()
    {
        Skip.IfNot(MapIds().Contains(AiHardMap));
        var facts = Facts(AiHardMap);
        var session = Begin(AiHardMap, 2);
        var seen = new DayRecorder(session);
        session.Watch(seen);
        for (int k = 0; k < 4; k++) session.EndTurn();
        int bagCuts = 0;
        foreach (var zone in facts.GetProperty("zones").EnumerateArray())
        {
            string player = zone.GetProperty("player").GetString()!, target = zone.GetProperty("cut_target").GetString()!;
            var cuts = zone.GetProperty("cut_events");
            var onStack = Assert.Single(seen.Events, e => Same(e.Firing.Event.Id, cuts.GetProperty("stack").GetString()!));
            var bag = seen.Events.Where(e => Same(e.Firing.Event.Id, cuts.GetProperty("bag").GetString()!)).ToList();
            Out.WriteLine($"{zone.GetProperty("key").GetString()}: перед отрядом — день {onStack.Day} {onStack.Firing.TriggererId}, " +
                          $"перед мешком — {string.Join(", ", bag.Select(b => $"день {b.Day} {b.Firing.TriggererId}"))}");
            bagCuts += bag.Count;
            Assert.Equal(player, onStack.Firing.PlayerId, StringComparer.OrdinalIgnoreCase);
            Assert.True(seen.Moves.TryGetValue(onStack.Firing.TriggererId, out var cell), $"{onStack.Firing.TriggererId}: хода нет");
            var at = zone.GetProperty("cut_at");
            int ring = Math.Max(Math.Abs(cell.X - at[0].GetInt32()), Math.Abs(cell.Y - at[1].GetInt32()));
            Out.WriteLine($"   ход с обрывом кончился в ({cell.X},{cell.Y}), от цели {ring}; боёв с целью в тот день: " +
                          seen.Outcomes.Count(o => o.Day == onStack.Day && Same(o.Defender, target)));
            Assert.Equal(1, ring);
        }
        Assert.True(bagCuts > 0, "ни один ИИ не шёл к мешку за 4 дня");
    }

    /// <summary>Выборы тактики и шаги стратегии ИИ по дням.</summary>
    private sealed class StepTrace : IAiTrace
    {
        public List<(string Player, int Day, string Stack, string Action)> Picks { get; } = [];

        public List<(string Player, int Day, AiExecuted Step)> Steps { get; } = [];

        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates) =>
            Picks.Add((playerId, day, action.Stack, action.Type.ToString()));

        public void Stepped(string playerId, int day, AiExecuted step, int number) => Steps.Add((playerId, day, step));

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) { }
    }

    /// <summary>События и итоги боёв с днём партии.</summary>
    private sealed class DayRecorder(GameSession session) : ITurnObserver
    {
        public List<(EventFiring Firing, int Day)> Events { get; } = [];

        public List<BattleOutcome> Outcomes { get; } = [];

        public List<TurnSight> Sights { get; } = [];

        public void PlayerTurn(string playerId) { }

        /// <summary>Отряд-триггерер → клетка конца хода, на котором сработало событие (ход сообщается до событий пути и хода,
        /// <see cref="GameSession.MoveStack"/>).</summary>
        public Dictionary<string, (int X, int Y)> Moves { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, (int X, int Y)> _last = new(StringComparer.OrdinalIgnoreCase);

        public void StackMoving(StackMove move)
        {
            if (move.Cells.Count > 0) _last[move.StackId] = move.Cells[^1];
        }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;

        public void Happened(TurnSight sight) => Sights.Add(sight);

        public void BattleEnded(BattleOutcome outcome) => Outcomes.Add(outcome);

        public void EventFired(EventFiring firing)
        {
            Events.Add((firing, session.Map.Turn));
            if (_last.TryGetValue(firing.TriggererId, out var cell)) Moves[firing.TriggererId] = cell;
        }
    }
}
