using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Карта 19 <c>port-synth-19-ai-spells</c> (<c>tools/synth-maps/maps_aispells.py</c>): у каждого полигона атакующий ИИ в день 1 (после
/// стратегии) видит свою цель с силой в окне подготовки, и годность заклинаний перед атакой (<c>0x472142</c>/<c>0x47263e</c>) решает условие
/// полигона — у порта столько категорий и старших годных, сколько записано в <c>facts.json</c> (<c>port_bounds</c>). Ответ движка — строки
/// <c>ai_rng</c> <c>attack_spell_kind</c>/<c>attack_spell_pick</c> (граница — то же число у движка) журнала будущей партии; сверку окон по журналу
/// сделает <see cref="AiTacticFromMapHarnessTests"/> (<see cref="AttackSpellRolls"/>), когда партия ляжет в корпус.</summary>
public partial class PortSynthMapsTests
{
    private const string AiSpellsMap = "port-synth-19-ai-spells";

    /// <summary>Броски окна: граница каждого вызова (ответ — генератор движка с начального сева).</summary>
    private sealed class BoundsRng : IRng
    {
        private readonly EngineRng _inner = new();

        public List<int> Bounds { get; } = [];

        public int Next(int maxValue)
        {
            if (maxValue != 0) Bounds.Add(maxValue);
            return _inner.Next(maxValue);
        }
    }

    /// <summary>Выбор заклинания перед атакой по одной цели (<c>0x471ec7</c> → <c>0x472142</c>) с условиями построителя атаки
    /// отряда (<c>0x453d15</c>): сила и порог оценщика, хозяин цели, второй кошелёк. Границы бросков; null — подготовки нет (сила не ниже
    /// порога) или цели нет в кэше.</summary>
    private static (List<int> Bounds, AiPrep? Prep, double P)? OwnWindow(GameSession session, AiTacticTurn turn, MapStack stack, string targetId)
    {
        var cache = AiTactics.StackTargets(session, turn, stack);
        if (cache.FirstOrDefault(t => Same(t.Id, targetId)) is not { } target) return null;
        var st = AiTactics.AttackStrength(session, stack, target, cache);
        bool human = session.Map.Players.GetValueOrDefault(target.Owner)?.IsHuman ?? false;
        bool neutral = AiTactics.NeutralOwner(session, target.Owner);
        if (!AiBeforeAttack.Prepares(st.P, human, neutral)) return ([], null, st.P);
        bool passed = AiTacticScore.ThresholdPassed(st.P, attackerNeutral: false, human, neutral);
        bool bank = human && turn.Forces.Others.Contains(target.Id, StringComparer.OrdinalIgnoreCase);
        var rng = new BoundsRng();
        var prep = AiSpells.BeforeAttack(session, stack, target.Id, target.UnitIds, target.Inside.Length == 0,
                                         AiTactics.Position(session.Map, target), st.P, passed, bank, rng);
        return (rng.Bounds, prep, st.P);
    }

    /// <summary>Окна дня 1: у каждого игрока ИИ своя партия с начала сценария, начат его ход (стратегия раздала приказы, оперативный
    /// слой бросил своё) — атакующий полигона с действующим <c>L_NORMAL</c>, у его цели границы бросков — как в <c>port_bounds</c>; у
    /// полигонов поддержки C своей цели ненулевая там, где сильный отряд в кэше (<c>port_support</c>). Печать — кэш, сила, кандидаты.</summary>
    [SkippableFact]
    public void AiSpells_DayOneWindows_EachPolygonHasItsCondition()
    {
        Skip.IfNot(MapIds().Contains(AiSpellsMap));
        var facts = Facts(AiSpellsMap);
        var sessions = new Dictionary<string, GameSession>(StringComparer.OrdinalIgnoreCase);
        int checkedBounds = 0, checkedSupport = 0;
        foreach (var z in facts.GetProperty("zones").EnumerateArray().Where(z => z.TryGetProperty("stack", out _)))
        {
            string key = Str(z, "key"), player = Str(z, "player"), stackId = Str(z, "stack"), targetId = Str(z, "target");
            if (!sessions.TryGetValue(player, out var session))
            {
                session = Begin(AiSpellsMap, 1);
                session.BeginAiTurn(player);
                sessions[player] = session;
            }
            var stack = session.Map.Stacks[stackId];
            Assert.Equal(D2.Rules.Battle.StackOrder.Normal, AiTactics.OrderOf(stack).Order);   // раздача приказов не тронула атакующего
            var turn = AiTactics.Begin(session, player, AiTacticMemo.Empty)!;
            turn.Rng = new CountingRng(new BoundsRng());
            var cands = AiTactics.Candidates(session, turn, stack);
            var cache = AiTactics.StackTargets(session, turn, stack);
            Out.WriteLine($"== {key}: {Str(z, "title")}");
            Out.WriteLine($"   кэш [{string.Join(" ", cache.Select(t => { var st = AiTactics.AttackStrength(session, stack, t, cache); return $"{t.Id[^4..]} p{st.P:0.###} A{st.A:0.#} B{st.B:0.#} C{st.C:0.#}"; }))}]");
            Out.WriteLine($"   кандидаты [{string.Join("; ", cands.Select(c => $"{c.Action.Type}{(c.Action.Prep is { } pr ? "/" + pr.Kind + (pr.Id.Length > 0 ? " " + pr.Id : "") : "")} → {c.Action.Target[^4..]} {c.Score:0.###}"))}]");
            var own = OwnWindow(session, turn, stack, targetId);
            Assert.True(own is not null, $"{key}: своей цели нет в кэше атакующего");
            Out.WriteLine($"   своя цель: p {own.Value.P:0.###}, броски [{string.Join(" ", own.Value.Bounds)}], заклинание {own.Value.Prep?.Id ?? "-"}");
            if (z.TryGetProperty("port_bounds", out var want))
            {
                Assert.Equal(want.EnumerateArray().Select(b => b.GetInt32()), own.Value.Bounds);
                checkedBounds++;
            }
            if (z.TryGetProperty("port_support", out var support))
            {
                var target = cache.First(t => Same(t.Id, targetId));
                Assert.Equal(support.GetBoolean(), AiTactics.AttackStrength(session, stack, target, cache).C > 0);
                checkedSupport++;
            }
        }
        Assert.Equal(17, checkedBounds);
        Assert.Equal(3, checkedSupport);
    }

    /// <summary>Партия порта с начала сценария, три дня «Конец хода» (бои — автобоем): какие атакующие полигонов выбрали атаку с заклинанием,
    /// какие заклинания брошены, какие бои прошли. Партия доходит до дня 3, хоть одно литьё перед атакой — со вторым кошельком (U2).</summary>
    [SkippableFact]
    public void AiSpells_ThreeDays_PortPlays()
    {
        Skip.IfNot(MapIds().Contains(AiSpellsMap));
        var facts = Facts(AiSpellsMap);
        var session = Begin(AiSpellsMap, 1);
        var trace = new TacticTrace();
        var preps = new List<string>();
        session.AiTrace = new PrepTrace(trace, preps);
        var seen = new Recorder();
        session.Watch(seen);
        var attackers = facts.GetProperty("zones").EnumerateArray().Where(z => z.TryGetProperty("stack", out _))
                             .ToDictionary(z => Str(z, "stack"), z => Str(z, "key"), StringComparer.OrdinalIgnoreCase);
        var casts = new List<string>();
        for (int day = 1; day <= 2; day++)
        {
            session.EndTurn();
            casts.AddRange(session.Map.SpellCasts.Select(c => $"{c.PlayerId} {c.Origin} до {c.TurnStop}").Except(casts));
        }
        Assert.Equal(3, session.Map.Turn);
        foreach (string p in preps) Out.WriteLine($"выбор с подготовкой: {p}");
        foreach (var p in trace.Picks.Where(p => attackers.ContainsKey(p.Stack)))
            Out.WriteLine($"день {p.Day} {attackers[p.Stack]} {p.Stack}: {p.Action} → {p.Target} ({p.X},{p.Y}) {p.Score}");
        foreach (string c in casts) Out.WriteLine($"литьё: {c}");
        foreach (var o in seen.Outcomes) Out.WriteLine($"бой: {o.Attacker} → {o.Defender}, победил {o.Winner}");
        string wallet = Str(Zone(facts, "U2"), "stack");
        Assert.Contains(preps, p => p.Contains(wallet, StringComparison.OrdinalIgnoreCase) && p.EndsWith("кошелёк", StringComparison.Ordinal));
    }

    /// <summary>Трасса выбора с подготовкой: выбранное действие с заклинанием или подмогой перед атакой.</summary>
    private sealed class PrepTrace(TacticTrace inner, List<string> preps) : IAiTrace
    {
        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates)
        {
            inner.Picked(playerId, day, action, score, candidates);
            if (action.Prep is { } p) preps.Add($"день {day} {action.Stack} → {action.Target}: {p.Kind} {p.Id} ({p.X},{p.Y}){(p.SpellBank ? " кошелёк" : "")}");
        }

        public void Stepped(string playerId, int day, AiExecuted step, int number) => inner.Stepped(playerId, day, step, number);

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) => inner.TurnEnded(playerId, day, ms, steps, picks);
    }
}
