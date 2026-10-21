using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Карта 20 <c>port-synth-20-ai-battle</c> (<c>tools/synth-maps/maps_aibattle.py</c>): прогноз порта по полигонам — бои B1–B3
/// (нападение человека автобоем: кто из защитников ИИ ушёл с поля, какие зелья выпиты) и руины без охраны R1/R2; раненый
/// отряд P — по журналу оригинала, <see cref="AiBattle_AiTurnsMatchTheOriginalJournal"/>. Партия оригинала (<c>20261019b-port-synth</c>): у R1 (Хранитель обетов, первая атака — обереги) <c>VisitRuin</c>
/// нет — «группа может драться» <c>0x5ddf35</c> (<see cref="AiTactics.GroupCanFight"/>), у R2 есть; сверка по журналу —
/// <see cref="AiTacticFromMapHarnessTests_PortSynth20"/>.</summary>
public partial class PortSynthMapsTests
{
    private const string AiBattleMap = "port-synth-20-ai-battle";

    [SkippableFact]
    public void AiBattle_Fights_RuinsAndPotions_PortForecast()
    {
        Skip.IfNot(MapIds().Contains(AiBattleMap));
        var facts = Facts(AiBattleMap);
        var book = Shared.Value.Book;
        foreach (string key in new[] { "B1", "B2", "B3" })
        {
            var session = Begin(AiBattleMap, 1);
            var z = Zone(facts, key);
            // Как в партии: дальний защитник — сперва путь до соседней клетки (по 2 очка за клетку равнины), затем нападение.
            int far = z.GetProperty("distance").GetInt32();
            if (far > 1)
            {
                var h = session.Map.Stacks[Str(z, "stack")];
                session.MoveStack(h.Id, h.X, h.Y + (far - 1) * Math.Sign(z.GetProperty("target_at")[1].GetInt32() - h.Y), 0, 2 * (far - 1));
            }
            var state = session.StartBattle(Str(z, "stack"), Str(z, "target"));
            var lead = state.Units.First(u => !u.Attacker && u.Type.IsLeader);
            Out.WriteLine($"{key}: ход нападающего {state.AttackerSide.Movement}, сила защитника (rel) {D2.Rules.Battle.AiStrength.Relative(state, lead, book.Rules.Constants):0.###}, "
                          + $"бежать можно {D2.Rules.Battle.AiRetreat.CanRetreat(state, lead, false, false)}");
            var fight = new D2.Data.Battle.BattleSession(state, new D2.Rules.Battle.BattleSim(state, new EngineRng(1), book.Rules.Constants),
                                                         book.Rules.Constants);
            for (int step = 0; step < 2000 && !fight.Finished; step++) fight.AiStep();
            Assert.True(fight.Finished, $"{key}: бой не кончился");
            // Прогноз памятки: лидер защитника уходит только без хода у нападающего (B2), при одном живом лидере — нет (B3).
            Assert.Equal(key == "B1" ? 10 : 0, state.AttackerSide.Movement);
            Assert.Equal(key == "B2", lead.Retreated);
            Out.WriteLine($"{key}: раундов {fight.Round}; защитники [{string.Join(" ", state.Units.Where(u => !u.Attacker).Select(u => $"{u.Type.Id[^4..]}{(u.Retreated ? " ушёл" : u.Alive ? " жив" : " пал")}"))}]");
        }
        var day = Begin(AiBattleMap, 1);
        string elves = day.Map.Stacks[Str(Zone(facts, "R1"), "stack")].Owner;
        day.BeginAiTurn(elves);
        foreach (string key in new[] { "R1", "R2" })
        {
            var z = Zone(facts, key);
            var stack = day.Map.Stacks[Str(z, "stack")];
            var turn = AiTactics.Begin(day, elves, AiTacticMemo.Empty)!;
            var cands = AiTactics.Candidates(day, turn, stack);
            Out.WriteLine($"{key} (приказ {stack.Order}/{stack.AiOrder}): [{string.Join("; ", cands.Select(c => $"{c.Action.Type} → {c.Action.Target[^4..]} {c.Score:0.###}"))}]");
            Assert.Equal(key == "R2", cands.Any(c => c.Action.Type == AiActionType.VisitRuin && Same(c.Action.Target, Str(z, "ruin"))));
        }
        // P (раненый с зельями) с начала сценария не прогнозируется: без боёв человека рядом стоит не побитый B2 Нежити, и P атакует его
        // (HireUnit FT0004, затем AttackStack KC0007) — у движка после боёв P пьёт оба зелья и уходит к столице. Сверка P — по журналу и
        // сохранениям оригинала, с сохранения после боёв: AiBattle_AiTurnsMatchTheOriginalJournal.
    }
}
