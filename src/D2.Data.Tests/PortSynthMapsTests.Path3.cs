using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 13 <c>port-synth-13-path3</c> (<c>tools/synth-maps/maps_path3.py</c>): ситуации памятки складываются в порте так, как
/// задумано. Спорное правило (что списывает движок при обрыве и сквозь невидимку, сработает ли стоящий в зоне, куда целит щелчок над
/// союзником) — только печать: его ответ дадут журнал и сохранения оригинала.</summary>
public partial class PortSynthMapsTests
{
    private const string Path3Map = "port-synth-13-path3";

    /// <summary>HM1/HM2: путь интерфейса к клетке щелчка идёт сквозь невидимого Гоблина (и мимо него), команда встаёт перед ним
    /// (<c>0x5cd38a(1)</c>) и нападает невидимка — правило по листингу и журналу (<c>docs/map-path.md</c> §9: R10 карты 8, AI1/AI2 карты 10);
    /// после невидимки на пути — ещё клетки сегодняшней части. Списание у человека посреди пути — спорное, печать.</summary>
    [SkippableFact]
    public void Path3_HumanThroughHidden_MidPath_StopsBeforeIt()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var facts = Facts(Path3Map);
        var session = Begin(Path3Map, 1);
        foreach (string key in new[] { "HM1", "HM2" })
        {
            var z = Zone(facts, key);
            var stack = session.Map.Stacks[Str(z, "stack")];
            var (cx, cy) = XY(z, "click_at");
            var path = session.PathTo(stack, cx, cy);
            Assert.Equal((cx, cy), (path.Points[path.Reachable].X, path.Points[path.Reachable].Y));   // интерфейс невидимку не видит
            var move = PlayerMove.Plan(session, stack.Id, path, (cx, cy));
            Out.WriteLine($"{key}: путь {path.Points.Count} точек до ({cx},{cy}), команда встанет ({move.Today[^1].X},{move.Today[^1].Y}), " +
                          $"нападёт {move.Attack}, списание порта {move.Charge?.ToString() ?? $"цена клетки {move.Today[^1].Cost}"}");
            Assert.False(move.Cut);
            Assert.Equal(XY(z, "stop_at"), (move.Today[^1].X, move.Today[^1].Y));
            Assert.Equal(Str(z, "hidden"), move.Attack, StringComparer.OrdinalIgnoreCase);
            Assert.True(path.Points.Count - 1 > move.Today.Count, $"{key}: за невидимкой нет клеток пути");
        }
    }

    /// <summary>CH1/CH2: путь сквозь невидимку обрывается событием — на клетке раньше той, где команда встала бы перед невидимкой (CH1), и на
    /// ней самой (CH2): обрыв, нападения нет (<c>0x43ff9d</c>); второй ход (событие одноразовое) встаёт перед невидимкой, и она нападает.
    /// Списание при обрыве — печать (ответ движка — <c>ap_restore</c>).</summary>
    [SkippableFact]
    public void Path3_CutBeforeHidden_ThenSecondMoveAmbush()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var facts = Facts(Path3Map);
        var session = Begin(Path3Map, 1);
        foreach (string key in new[] { "CH1", "CH2" })
        {
            var z = Zone(facts, key);
            string id = Str(z, "stack"), hidden = Str(z, "hidden");
            var (cx, cy) = XY(z, "click_at");
            var path = session.PathTo(session.Map.Stacks[id], cx, cy);
            var move = PlayerMove.Plan(session, id, path, (cx, cy));
            var stop = move.Today[^1];
            Out.WriteLine($"{key}: обрыв {move.Cut} на ({stop.X},{stop.Y}), нападение {move.Attack ?? "нет"}, цена клетки обрыва {stop.Cost}");
            Assert.True(move.Cut, $"{key}: путь не оборван");
            Assert.Null(move.Attack);
            Assert.Equal(XY(z, "cut_at"), (stop.X, stop.Y));
            session.MoveStack(id, stop.X, stop.Y, 0, stop.Cost, move.Fired, move.Today);
            // Невидимка на остатке показанного пути — интерфейс его стирает (0x4cfead; у движка после обрыва CH1/CH2 пути нет).
            Assert.False(session.ShownPathKept(session.Map.Stacks[id], path, move.Today.Count, (cx, cy)), $"{key}: путь после обрыва не стёрт");
            var again = session.PathTo(session.Map.Stacks[id], cx, cy);
            var second = PlayerMove.Plan(session, id, again, (cx, cy));
            var at = second.Today.Count > 0 ? (second.Today[^1].X, second.Today[^1].Y) : (session.Map.Stacks[id].X, session.Map.Stacks[id].Y);
            // CH2: обрыв уже поставил отряд вплотную к невидимке — второй команде идти некуда (последняя достижимая — старт), но объект
            // действия у неё есть (0x5cd11f у старта): движок нападает и без шага, списав конец списка (журнал 18.10 — Path3_HumanCommandsLikeTheOriginal).
            Out.WriteLine($"{key}: второй ход — клеток {second.Today.Count}, встанет {at}, нападёт {second.Attack ?? "нет"}, " +
                          $"списание {second.Charge?.ToString() ?? (second.Today.Count > 0 ? $"цена клетки {second.Today[^1].Cost}" : "0")}");
            Assert.False(second.Cut, $"{key}: второй ход оборван");
            Assert.Equal((XY(z, "hidden_at").Item1 - 1, cy), at);
            // Шаг перед невидимкой (или без шага, CH2) и засада — правило по листингу и журналу (docs/map-path.md §9, R10, AI1, карта 13).
            Assert.Equal(hidden, second.Attack, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>C5t: ворота создают Гоблина по шаблону вплотную к ходоку — нападение без шага, победа — <c>L_KILL_STACK</c> по шаблону.</summary>
    [SkippableFact]
    public void Path3_TemplateGoblin_NextToWalker_KilledFiresC5t()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var facts = Facts(Path3Map);
        var session = Begin(Path3Map, 1);
        session.EventBattlesOnScreen = false;
        var seen = new EventDays(() => session.Map.Turn);
        session.Watch(seen);
        var z = Zone(facts, "C5t");
        string walker = Str(z, "stack");
        var (gx, gy) = XY(z, "gate_at");
        session.MoveStack(walker, gx, gy, 0, 4);
        var made = session.Map.Stacks.Values.Single(s => Same(s.TemplateId, Str(z, "template")));
        Assert.Equal(XY(z, "made_at"), (made.X, made.Y));
        var w = session.Map.Stacks[walker];
        Assert.Equal(1, Math.Max(Math.Abs(w.X - made.X), Math.Abs(w.Y - made.Y)));   // Гоблин вплотную к ходоку
        var battle = session.StartBattle(walker, made.Id);
        var fight = new D2.Data.Battle.BattleSession(battle, new D2.Rules.Battle.BattleSim(battle, session.Rng, session.Book!.Rules.Constants),
                                                     session.Book!.Rules.Constants) { Auto = true };
        fight.Resolve();
        session.FinishBattle(fight.Log);
        Assert.False(session.Map.Stacks.ContainsKey(made.Id));
        Assert.Contains(seen.Fired, f => Same(f.Id, Str(z, "event")));
    }

    /// <summary>Обрыв пути событием перед отрядом, мешком и своей деревней и второй ход — как карта 12 (<see cref="Events3_PathCut_ThenSecondClickGoesThrough"/>).</summary>
    [SkippableFact]
    public void Path3_PathCut_ThenSecondMoveGoesThrough()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var facts = Facts(Path3Map);
        var session = Begin(Path3Map, 1);
        var keys = facts.GetProperty("cuts").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(3, keys.Count);
        foreach (string key in keys)
        {
            var z = Zone(facts, key);
            string stack = Str(z, "stack"), target = Str(z, "target");
            var aim = session.Map.Stacks.TryGetValue(target, out var t) ? (t.X, t.Y) : PathFinder.EntranceOf(session.Map, target)
                      ?? throw new InvalidOperationException($"{key}: у цели {target} нет входа");
            var path = session.PathTo(session.Map.Stacks[stack], aim.Item1, aim.Item2);
            var move = PlayerMove.Plan(session, stack, path);
            Assert.True(move.Cut, $"{key}: путь не оборван");
            var end = move.Today[^1];
            session.MoveStack(stack, end.X, end.Y, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
            // Путь к цели после обрыва помнится (0x4cfead): остаток списка — клетка отряда (у движка path len 1, seq 706470/761996/773735).
            var kept = session.KeptPath(session.Map.Stacks[stack], path, move.Today.Count);
            Assert.True(session.ShownPathKept(session.Map.Stacks[stack], path, move.Today.Count, aim), $"{key}: путь после обрыва стёрт");
            Assert.Equal((end.X, end.Y), (kept.Points[0].X, kept.Points[0].Y));
            Assert.False(PlayerMove.Plan(session, stack, session.PathTo(session.Map.Stacks[stack], aim.Item1, aim.Item2)).Cut, $"{key}: второй ход оборван");
        }
    }

    /// <summary>PRE1/PRE2 и союз: E07p включает PRE1 при отряде в зоне; союз с Эльфами — с начала дня 1 (событие частоты); три дня проходят.
    /// Сработают ли PRE1/PRE2 у стоящих — печать (у движка — нет, <see cref="Path3_StandingInZone_NeverFires_LikeTheOriginal"/>); щелчок над
    /// союзником целит в него и упирается в союз (<see cref="GameSession.AllyRefuses"/>).</summary>
    [SkippableFact]
    public void Path3_StandingInZone_AndAlly_ThreeDays()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var facts = Facts(Path3Map);
        var session = Begin(Path3Map, 1);
        var seen = new EventDays(() => session.Map.Turn);
        session.Watch(seen);
        Assert.True(Diplomacy.AreAllies(session.Map.Relations, 1, 5), "союза Нежити и Эльфов в начале дня 1 нет");
        var al = Zone(facts, "AL");
        var walker = session.Map.Stacks[Str(al, "stack")];
        var (ax, ay) = XY(al, "click_at");
        // Щелчок над союзником целит в него (0x4d04c3) и упирается в союз: пути нет, окно X005TA0303 (журнал — Path3_ClickOnAlly_RefusedLikeTheOriginal).
        var aim = session.ClickAim(walker, ax, ay);
        Assert.Equal(XY(al, "ally_at"), aim);
        Assert.True(session.AllyRefuses(walker, aim), "AL: щелчок над союзником не упёрся в союз");
        var gate = Zone(facts, "E07p");
        var (gx, gy) = XY(gate, "gate_at");
        session.MoveStack(Str(gate, "stack"), gx, gy, 0, 4);
        Assert.Contains(seen.Fired, f => Same(f.Id, Str(gate, "event")));
        string When(string key) => string.Join(", ", seen.Fired.Where(f => Same(f.Id, Str(Zone(facts, key), "event"))).Select(f => $"день {f.Day}"))
                                   is { Length: > 0 } days ? days : "нет";
        for (int day = 1; day <= 3; day++)
        {
            Out.WriteLine($"день {session.Map.Turn}: PRE1 {When("PRE1")}, PRE2 {When("PRE2")}");
            foreach (string key in new[] { "PRE1", "PRE2" })
            {
                var pre = Zone(facts, key);
                var standing = session.Map.Stacks[Str(pre, "stack")];
                Assert.Equal(XY(pre, "at"), (standing.X, standing.Y));
            }
            if (day < 3) session.EndTurn();
        }
        Assert.Equal(3, session.Map.Turn);
    }

    /// <summary>MS: бой с Толстыми бесами идёт несколько раундов, промахи в нём — почти наверняка (Тени — точность 50, Привидения — 65,
    /// бесы — 70), и отряд человека побеждает: десять сидов партии, автобой.</summary>
    [SkippableFact]
    public void Path3_MissBattle_HasMisses()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var facts = Facts(Path3Map);
        var z = Zone(facts, "MS");
        int noMiss = 0, lost = 0;
        for (uint seed = 1; seed <= 10; seed++)
        {
            var session = Begin(Path3Map, 1, seed);
            var battle = session.StartBattle(Str(z, "stack"), Str(z, "target"));
            var fight = new D2.Data.Battle.BattleSession(battle, new D2.Rules.Battle.BattleSim(battle, session.Rng, session.Book!.Rules.Constants),
                                                         session.Book!.Rules.Constants) { Auto = true };
            fight.Resolve();
            int misses = fight.Log.Count(r => r.Miss), rounds = fight.Log.Count == 0 ? 0 : fight.Log.Max(r => r.Round);
            bool won = Same(fight.Winner ?? "", battle.AttackerGroup);
            Out.WriteLine($"MS сид {seed}: раундов {rounds}, ходов {fight.Log.Count}, промахов {misses}, победил {(won ? "отряд человека" : "нейтрал")}");
            noMiss += misses == 0 ? 1 : 0;
            lost += won ? 0 : 1;
        }
        Assert.True(noMiss <= 1, $"боёв без промаха {noMiss} из 10");
        Assert.True(lost <= 2, $"проиграно {lost} из 10");
    }

    /// <summary>CA1/CA2: охотники Кланов в трубках гор в день 1 идут на приманку, событие их расы обрывает путь перед невидимым отрядом
    /// человека. Где встали, списание и засада — печать (ответ движка — <c>ap_restore</c> и <c>battle_start</c>).</summary>
    [SkippableFact]
    public void Path3_AiHunters_CutBeforeHidden_OnDayOne()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        var facts = Facts(Path3Map);
        var session = Begin(Path3Map, 1);
        session.EventBattlesOnScreen = false;
        var seen = new EventDays(() => session.Map.Turn);
        session.Watch(seen);
        session.EndTurn();
        foreach (string key in new[] { "CA1", "CA2" })
        {
            var z = Zone(facts, key);
            string hunter = Str(z, "stack");
            string at = session.Map.Stacks.TryGetValue(hunter, out var h) ? $"({h.X},{h.Y}), ход {h.Move}" : "нет (погиб)";
            Out.WriteLine($"{key}: охотник после дня 1 — {at}; событие {(seen.Fired.Any(f => Same(f.Id, Str(z, "event"))) ? "сработало" : "нет")}, " +
                          $"невидимка {(session.Map.Stacks.ContainsKey(Str(z, "hidden")) ? "жива" : "погибла")}");
            Assert.Contains(seen.Fired, f => Same(f.Id, Str(z, "event")));
        }
    }
}
