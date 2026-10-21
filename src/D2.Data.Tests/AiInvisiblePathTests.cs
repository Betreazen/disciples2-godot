using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>
/// Путь ИИ мимо невидимого отряда человека (<c>docs/map-path.md</c> §9): все поиски ИИ идут с «видеть невидимых» <c>a5</c> = 0 (<c>0x46903e</c>
/// и остальные вызовы <c>0x5cc6da</c> из ИИ) — чужой невидимый в масках «можно», зоны контроля нет (<c>0x5ccc68</c>), последняя достижимая его не
/// видит; команда хода видит (исполнение <c>0x45abd8</c> → <c>0x5cc120(…, 1, 1)</c>, генератор <c>0x43fe12</c>): отряд встаёт перед невидимым,
/// объект действия — он (<c>0x5cd11f</c>), и он нападает сам (<c>CEffectBattle</c> с флагом невидимости цели, <c>0x433a57</c>; для ИИ так же, как
/// для человека — в <c>0x440667</c>/<c>0x4338ba</c>/<c>0x433a57</c> проверки «человек ли» нет). Сохранение human, день 8: <c>S073KC0002</c>
/// (Легионы, ИИ) с 16,79 атакует <c>S073KC0027</c>, перенесённого в 6,74 (как в <see cref="AiPathCutTests"/>); на путь ставится отряд человека.
/// </summary>
[Trait("Category", "Corpus")]
public sealed class AiInvisiblePathTests
{
    private const string Mover = "S073KC0002";
    private const string Target = "S073KC0027";

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Невидимый отряд человека на клетке пути: путь ИИ идёт сквозь него, отряд встаёт на клетке перед ним, бой — нападает невидимый,
    /// защищается ИИ. Видимый на той же клетке — путь его обходит (зона контроля, режим 1), и боя с ним нет.</summary>
    [SkippableFact]
    public void HiddenHumanOnThePath_AiStopsBeforeIt_ItAmbushes()
    {
        var attack = new AiTacticOption(AiActionType.AttackStack, Mover, Target, 6, 74);
        var (probe, _) = Session(null, invisible: false);
        var free = AiTactics.Route(probe, probe.Map.Stacks[Mover], attack);
        var cell = (free.Points[3].X, free.Points[3].Y);
        var before = (free.Points[2].X, free.Points[2].Y);

        var (session, battles) = Session(cell, invisible: true);
        string hidden = HumanStack(session);
        var path = AiTactics.Route(session, session.Map.Stacks[Mover], attack);
        Assert.Contains(cell, path.Points.Select(p => (p.X, p.Y)));   // a5 = 0: сквозь невидимку
        session.ExecuteTactic(attack);
        Assert.Equal(before, Stood(battles));
        var fight = Assert.Single(battles.Fights);
        Assert.Equal(hidden, fight.Attacker, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Mover, fight.Defender, StringComparer.OrdinalIgnoreCase);

        var (shown, seen) = Session(cell, invisible: false);
        var around = AiTactics.Route(shown, shown.Map.Stacks[Mover], attack);
        Assert.DoesNotContain(cell, around.Points.Select(p => (p.X, p.Y)));
        shown.ExecuteTactic(attack);
        Assert.DoesNotContain(seen.Fights, b => b.Attacker.Equals(HumanStack(shown), StringComparison.OrdinalIgnoreCase)
                                         || b.Defender.Equals(HumanStack(shown), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Невидимый отряд человека вплотную к пути (не на нём): зоны контроля у него для ИИ нет, путь идёт мимо; команда встаёт на
    /// первой клетке рядом с ним (<c>0x5cd5b2</c> с невидимыми) и объект — он (<c>0x5cd11f</c>): засада.</summary>
    [SkippableFact]
    public void HiddenHumanBesideThePath_AiStopsAtIt_ItAmbushes()
    {
        var attack = new AiTacticOption(AiActionType.AttackStack, Mover, Target, 6, 74);
        var (probe, _) = Session(null, invisible: false);
        var free = AiTactics.Route(probe, probe.Map.Stacks[Mover], attack);
        var on = free.Points.Select(p => (p.X, p.Y)).ToHashSet();
        // Клетка рядом с четвёртой точкой пути, не на пути и не рядом с первыми двумя: отряд встанет на третьей.
        var mid = free.Points[3];
        var cell = new[] { (0, -1), (0, 1), (-1, 0), (1, 0), (-1, -1), (1, -1), (-1, 1), (1, 1) }
            .Select(d => (X: mid.X + d.Item1, Y: mid.Y + d.Item2))
            .First(c => !on.Contains(c) && free.Points.Take(3).All(p => Math.Max(Math.Abs(p.X - c.X), Math.Abs(p.Y - c.Y)) > 1)
                        && probe.Map.Contains(c.X, c.Y) && Movement.Cost(probe.Map, c.X, c.Y, new MoveOptions(false), MovementRules.Vanilla) != Movement.Forbidden
                        && !probe.Map.Plan.ContainsKey(c));
        var (session, battles) = Session(cell, invisible: true);
        string hidden = HumanStack(session);
        session.ExecuteTactic(attack);
        var stand = Stood(battles);
        Assert.Equal(1, Math.Max(Math.Abs(stand.X - cell.X), Math.Abs(stand.Y - cell.Y)));
        var fight = Assert.Single(battles.Fights);
        Assert.Equal(hidden, fight.Attacker, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Mover, fight.Defender, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Отряд человека для опыта — первый вне крепости (по id).</summary>
    private static string HumanStack(GameSession session)
    {
        var human = session.Map.Players.Values.First(p => p.IsHuman).Id;
        return session.Map.Stacks.Values.Where(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0)
                      .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).First().Id;
    }

    private static (int X, int Y) Stood(Watch watch) => watch.Moves.Last(m => m.StackId.Equals(Mover, StringComparison.OrdinalIgnoreCase)).Cells[^1];

    private static (GameSession Session, Watch Battles) Session((int X, int Y)? humanAt, bool invisible)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*-t08-*.sg").Order(StringComparer.Ordinal).Last();
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("AiI", "AiI", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        var art = SharedContent.Load(Data).MapArt;
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var map = looks.Map;
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var plan = map.Plan.ToDictionary(p => p.Key, p => p.Value);
        Place(stacks, plan, Target, (6, 74));
        string mine = stacks.Values.Where(s => s.Owner.Equals(human.Id, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0)
                            .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).First().Id;
        if (humanAt is { } at) Place(stacks, plan, mine, at);
        // В сохранении запас 0: его ставит начало хода ИИ.
        stacks[Mover] = stacks[Mover] with { Move = new GameSession(new GameState(info, looks), art, book).DayAllowance(stacks[Mover]) };
        looks = looks with
        {
            Map = GameSession.With(map, stacks: stacks, plan: plan),
            Stacks = [.. looks.Stacks.Select(s => s.Id.Equals(mine, StringComparison.OrdinalIgnoreCase) && humanAt is { } c
                                                     ? s with { X = c.X, Y = c.Y, Invisible = invisible } : s)],
        };
        var progress = EventProgress.From(events);
        var session = new GameSession(new GameState(info, looks) { Events = events, EventProgress = progress }, art, book);
        var watch = new Watch();
        session.Watch(watch);
        return (session, watch);
    }

    private static void Place(Dictionary<string, MapStack> stacks, Dictionary<(int X, int Y), IReadOnlyList<string>> plan, string id, (int X, int Y) at)
    {
        var s = stacks[id];
        plan[(s.X, s.Y)] = [.. plan[(s.X, s.Y)].Where(o => !o.Equals(id, StringComparison.OrdinalIgnoreCase))];
        plan[at] = [.. plan.GetValueOrDefault(at) ?? [], id];
        stacks[id] = s with { X = at.X, Y = at.Y };
    }

    /// <summary>Наблюдатель: ходы отрядов и итоги боёв (бой — автобоем).</summary>
    private sealed class Watch : ITurnObserver
    {
        public List<StackMove> Moves { get; } = [];

        public List<BattleOutcome> Fights { get; } = [];

        public void PlayerTurn(string playerId)
        {
        }

        public void StackMoving(StackMove move) => Moves.Add(move);

        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;

        public void BattleEnded(BattleOutcome outcome) => Fights.Add(outcome);
    }
}
