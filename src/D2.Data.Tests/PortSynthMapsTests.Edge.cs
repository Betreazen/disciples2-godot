using System.Text.Json;
using D2.Data.Battle;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Карта 8 <c>port-synth-8-edge</c> (<c>tools/synth-maps/maps_edge.py</c>): правила уровня B и редкие исходы — на полигонах карты
/// в модели порта (ответ движка дадут журнал и сохранения оригинала этой карты).</summary>
public partial class PortSynthMapsTests
{
    private const string EdgeMap = "port-synth-8-edge";

    private BattleState EdgeBattle(GameSession session, JsonElement zone, int seed)
    {
        string stack = Str(zone, "stack");
        var state = MapBattle.Build(session.Map, stack, MapBattle.DefenderOf(session.Map, Str(zone, "target"))!, session.Book!, 0,
                                    session.Map.Stacks[stack].Move);
        state.ProcessRand = new CrtRand((uint)seed);
        return state;
    }

    private static BattleSession Fight(BattleState state, int seed, GameBook book) =>
        new(state, new BattleSim(state, new EngineRng(seed), book.Rules.Constants), book.Rules.Constants);

    /// <summary>W2: Бандит (<c>ATCK_TWICE</c>) жмёт «Ждать» в первом раунде — в конце раунда он бьёт дважды (ячейка с числом оставшихся
    /// действий + 1, <c>0x625525</c>; <see cref="BattleSim.RequeueWaiter"/>).</summary>
    [SkippableFact]
    public void Edge_WaitThenDoubleAttack_TwoHitsAtRoundEnd()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        var book = session.Book!;
        var state = EdgeBattle(session, Zone(facts, "W2"), 1);
        var fight = Fight(state, 1, book);
        var bandit = state.Units.First(u => u.Attacker && u.Type.Id.Equals("g000uu8021", StringComparison.OrdinalIgnoreCase));
        bool waited = false;
        for (int step = 0; step < 400 && !fight.Finished && fight.Round == 1; step++)
        {
            if (!waited && ReferenceEquals(fight.Current, bandit) && fight.WaitsPlayer) waited = fight.Wait();
            else fight.AiStep();
        }
        Assert.True(waited);
        var hits = fight.Log.Where(r => r.Round == 1 && r.Unit == bandit.Id).ToList();
        Out.WriteLine($"Бандит в раунде 1: {hits.Count} ударов ({string.Join(", ", hits.Select(h => $"{h.Target} {(h.Miss ? "промах" : h.Damage)}"))})");
        Assert.Equal(2, hits.Count);
        var round = fight.Log.Where(r => r.Round == 1).ToList();
        Assert.All(round.TakeLast(2), r => Assert.Equal(bandit.Id, r.Unit));   // оба удара — в конце раунда, после всех
    }

    /// <summary>U1: Повелитель стихий один в первом ряду призывает в задний ряд и гибнет от Людоеда — призванный изгоняется в начале своего
    /// хода (<c>0x628a84</c>, заметка <see cref="BattleNoteKind.UnsummonedInBattle"/>); U2: призванный жмёт «Бежать» — изгнан
    /// (<c>0x628fb4</c>).</summary>
    [SkippableFact]
    public void Edge_SummonLost_SummonerDiesOrSummonedFlees()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        var book = session.Book!;
        int banishedU1 = 0, banishedU2 = 0;
        const int seeds = 20;
        for (int seed = 1; seed <= seeds; seed++)
        {
            foreach (string key in new[] { "U1", "U2" })
            {
                var state = EdgeBattle(session, Zone(facts, key), seed);
                var fight = Fight(state, seed, book);
                var summoner = state.Units.First(u => u.Attacker && u.Type.Id.Equals("g000uu0153", StringComparison.OrdinalIgnoreCase));
                bool summoned = false;
                for (int step = 0; step < 600 && !fight.Finished && fight.Round <= 4; step++)
                {
                    var unit = fight.Current;
                    if (!summoned && ReferenceEquals(unit, summoner) && fight.WaitsPlayer && fight.SummonSlots() is { Count: > 0 } cells)
                    {
                        fight.SummonAt(cells.FirstOrDefault(c => c % 2 == 1, cells[0]));   // задний ряд — 1, 3, 5
                        summoned = true;
                    }
                    else if (key == "U2" && unit is { Summoned: true } && fight.WaitsPlayer) fight.Retreat();
                    else fight.AiStep();
                }
                bool banished = state.Notes.Any(n => n.Kind == BattleNoteKind.UnsummonedInBattle);
                if (key == "U1" && banished) banishedU1++;
                if (key == "U2" && banished) banishedU2++;
            }
        }
        Out.WriteLine($"изгнан при гибели призывателя: {banishedU1} из {seeds}; изгнан вместо бегства: {banishedU2} из {seeds}");
        Assert.True(banishedU1 >= seeds / 2, $"U1: {banishedU1}");
        Assert.Equal(seeds, banishedU2);
    }

    /// <summary>BI: у Баньши своей атаки с уроном нет (паралич), есть Сфера грома — порт усиление Травницы на неё не даёт
    /// (<see cref="Attacks.HasDamageAttack"/>: своя первая и вторая атаки), Воина — даёт. Движок тоже не даёт — журнал карты 10, BI2
    /// (<see cref="Edge2_Boost_BansheeIsTheOnlyCandidate"/>).</summary>
    [SkippableFact]
    public void Edge_Boost_TargetWithOnlyItemDamage_PortRefuses()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        var state = EdgeBattle(session, Zone(facts, "BI"), 1);
        var fight = Fight(state, 1, session.Book!);
        var herbalist = state.Units.First(u => u.Attacker && u.Attack.Class == AttackClass.BoostDamage);
        var banshee = state.Units.First(u => u.Attacker && u.Type.Id.Equals("g000uu0099", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(banshee.BattleItems);
        for (int step = 0; step < 50 && !ReferenceEquals(fight.Current, herbalist); step++) fight.AiStep();
        Assert.Same(herbalist, fight.Current);
        var targets = fight.Targets();
        Out.WriteLine("цели Травницы: " + string.Join(", ", targets.Select(t => t.Type.Id)));
        Assert.DoesNotContain(banshee, targets);
        Assert.Contains(targets, t => t.Type.Id.Equals("g000uu0086", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>BI, исход боя: Баньши со Сферой грома пала, сфера не применялась — после боя у отряда с павшим лидером снимаются все слоты
    /// (<c>0x4379c6</c> → <c>0x437c4e</c>, <c>unEquipItem 0x5e746d</c>), сфера остаётся в сумке. Журнал 20261016g-port-synth: item_unequip
    /// слота 2 за battle_end (seq 558981), экземпляр в сохранении t01-002 — в сумке.</summary>
    [SkippableFact]
    public void Edge_FallenLeader_EquipmentGoesToBag()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        var zone = Zone(facts, "BI");
        string stackId = Str(zone, "stack");
        var before = session.Map.Stacks[stackId];
        string sphere = D2.Rules.Magic.Equipment.Slots(before).First(i => i.Length > 0);
        var state = session.StartBattle(stackId, Str(zone, "target"));
        var banshee = state.Units.First(u => u.Id.Equals(before.LeaderId, StringComparison.OrdinalIgnoreCase));
        banshee.Hp = 0;   // лидер пал, остальные бежали — отряд жив
        foreach (var u in state.Units.Where(u => u.Attacker && u.Hp > 0)) u.Retreated = true;
        session.EndBattle([]);
        session.CommitBattle();
        var after = session.Map.Stacks[stackId];
        Assert.False(after.LeaderAlive);
        Assert.DoesNotContain(sphere, D2.Rules.Magic.Equipment.Slots(after), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(sphere, after.Items, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>R10 и R11: путь человека идёт сквозь невидимый чужой отряд (<c>a5</c> = 0, <c>0x5ccc68</c>), команда встаёт перед ним
    /// (<see cref="PlayerMove.Attack"/>, <c>0x5cd11f</c>) и он нападает сам (<c>CEffectBattle</c> <c>0x433a57</c>) — и когда щёлкнули
    /// за ним (R10 → (52, 21)), и у вора (R11: действий нет, бой, <c>0x4407c2</c>). Партия 17.10: порт обходил невидимку и вору давал
    /// «нечего делать».</summary>
    [SkippableFact]
    public void Edge_HiddenStack_PathRunsThrough_ItAmbushes()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        var r10 = Zone(facts, "R10");
        string mover = Str(r10, "stack"), hidden = Str(r10, "target");
        var stack = session.Map.Stacks[mover];
        var path = session.PathTo(stack, 52, 21);
        Assert.Equal([(48, 21), (49, 21), (50, 21), (51, 21), (52, 21)], path.Points.Select(p => (p.X, p.Y)));
        Assert.Equal(path.Points.Count - 1, path.Reachable);   // интерфейс невидимку не видит
        var move = PlayerMove.Plan(session, mover, path, (52, 21));
        Assert.Equal(hidden, move.Attack, StringComparer.OrdinalIgnoreCase);
        // Без очков хода на момент команды нападения нет (0x43fe51: [ebp+0x27]).
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[mover] = stack with { Move = 0 };
        var tired = new GameSession(session.State with
        {
            Looks = GameSession.Retold(session.Looks, GameSession.With(session.Map, stacks: stacks), session.Looks.Stacks),
        }, session.Art, session.Book);
        Assert.Null(PlayerMove.Plan(tired, mover, path, (52, 21)).Attack);
        Assert.Equal((49, 21), (move.Today[^1].X, move.Today[^1].Y));
        session.MoveStack(mover, 49, 21, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
        var ambush = session.StartBattle(mover, hidden);
        Assert.Equal(hidden, ambush.AttackerGroup, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(mover, ambush.DefenderGroup, StringComparer.OrdinalIgnoreCase);
        session.EndBattle([]);
        session.CommitBattle();

        var r11 = Zone(facts, "R11");
        string thief = Str(r11, "stack"), guard = Str(r11, "target");
        Assert.True(session.IsInvisible(guard));
        Assert.Empty(session.ThiefActions(thief, guard));
        var fight = session.StartBattle(thief, guard);
        Assert.Equal(guard, fight.AttackerGroup, StringComparer.OrdinalIgnoreCase);
        Assert.False(session.IsInvisible(guard));
    }

    /// <summary>RL: страж руины (инициатива 60) первым убивает лидера (здоровье 5), задний ряд добивает стража — победитель с павшим лидером
    /// грабит руину (<c>0x41b381</c> → <c>0x44133a</c>). Предмет руины — тип (Эликсир исцеления <c>G000IG0005</c>), и победитель получает
    /// его новым экземпляром: экземпляр в <c>MidRuin.ITEM</c> оригинал не грабит (<c>item_create</c> ret false) и после грабежа вылетел
    /// (журнал 20261016e-av).</summary>
    [SkippableFact]
    public void Edge_RuinWonWithFallenLeader_IsLooted()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var zone = Zone(facts, "RL");
        string elixir = zone.GetProperty("loot").GetProperty("item").GetString()!;
        Assert.Equal("G000IG0005", elixir, StringComparer.OrdinalIgnoreCase);
        int looted = 0, seeds = 10;
        for (int seed = 1; seed <= seeds; seed++)
        {
            var session = Begin(EdgeMap, 2);
            string raider = Str(zone, "stack"), ruin = Str(zone, "target");
            Assert.Equal(elixir, session.Map.Sites.Single(s => Same(s.Id, ruin)).ItemId, StringComparer.OrdinalIgnoreCase);
            int had = session.Map.Stacks[raider].Items.Count;
            var state = session.StartBattle(raider, ruin);
            var fight = Fight(state, seed, session.Book!);
            fight.Auto = true;
            fight.Resolve();
            session.FinishBattle(fight.Log);
            bool fallen = session.Map.Stacks.TryGetValue(raider, out var after) && !after.LeaderAlive;
            if (!fallen || !Same(session.Map.Sites.Single(s => Same(s.Id, ruin)).LooterId, session.ActiveHuman)) continue;
            looted++;
            string gained = Assert.Single(after!.Items.Skip(had));
            Assert.Equal(elixir, session.Map.ItemTypes[gained], StringComparer.OrdinalIgnoreCase);
        }
        Out.WriteLine($"руина разграблена при павшем лидере: {looted} из {seeds}");
        Assert.True(looted >= seeds / 2, $"{looted}");
    }

    /// <summary>Раскрытие невидимого (<c>CEffectShow</c> с 1): вход в свою деревню, руину, лавку, мешок, снос и постановка жезла, выброс мешка,
    /// обрыв пути событием, нападение и цель-невидимка, вор у невидимки (бой); простой ход и обмен со своим — без раскрытия.</summary>
    [SkippableFact]
    public void Edge_Invisible_RevealedEverywhere_ButOnPlainMove()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        string Id(string key) => Str(Zone(facts, key), "stack");
        string Target(string key) => Str(Zone(facts, key), "target");
        (int X, int Y) Cell(string key, string field) => (Zone(facts, key).GetProperty(field)[0].GetInt32(), Zone(facts, key).GetProperty(field)[1].GetInt32());
        string[] hidden = ["R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R12"];
        Assert.All(hidden, k => Assert.True(session.Invisible(Id(k)), k));
        Assert.True(session.Invisible(Target("R10")) && session.Invisible(Target("R11")));
        Assert.True(session.Map.Players[session.ActiveHuman].Bank.Gold >= facts.GetProperty("crystals").GetProperty("day1_gold_min").GetInt32());

        var r0 = Cell("R0", "move_to");
        session.MoveStack(Id("R0"), r0.X, r0.Y, 0, 3);
        foreach (string key in new[] { "R1", "R2", "R3", "R4" }) session.Enter(Id(key), Target(key));
        Assert.Null(session.BreakRod(Id("R5"), Target("R5")));
        var plant = Cell("R6", "plant_at");
        Assert.Null(session.PlaceRod(Id("R6"), plant.X, plant.Y));
        var drop = Cell("R7", "drop_at");
        Assert.Null(session.DropBag(Id("R7"), drop.X, drop.Y, out _));
        var to = Cell("R8", "move_to");
        var path = session.PathTo(session.Map.Stacks[Id("R8")], to.X, to.Y);
        var move = PlayerMove.Plan(session, Id("R8"), path);
        Assert.True(move.Cut);
        var end = move.Today[^1];
        session.MoveStack(Id("R8"), end.X, end.Y, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
        session.StartBattle(Id("R9"), Target("R9"));
        session.EndBattle([]);
        session.StartBattle(Id("R10"), Target("R10"));
        session.EndBattle([]);
        Assert.Empty(session.ThiefActions(Id("R11"), Target("R11")));
        session.StartBattle(Id("R11"), Target("R11"));
        session.EndBattle([]);
        var own = Cell("R12", "target_at");
        Assert.Null(session.ExchangeCheck(Id("R12"), own.X, own.Y, out _));

        Assert.True(session.Invisible(Id("R0")), "простой ход раскрыл");
        Assert.True(session.Invisible(Id("R12")), "обмен со своим раскрыл");
        foreach (string key in new[] { "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10" })
        {
            Assert.True(session.Map.Stacks.ContainsKey(Id(key)), $"{key}: отряда нет — раскрытие не проверено");
            Assert.False(session.Invisible(Id(key)), $"{key} остался невидимым");
        }
        Assert.False(session.Invisible(Target("R10")), "цель-невидимка не раскрыта");
        Assert.False(session.Invisible(Target("R11")), "невидимка у вора не раскрыта");
    }

    /// <summary>Воры: убийство одинокого Гоблина — отряд уничтожен, убийца в <c>CMidStackDestroyed</c> — Нежить, <c>L_KILL_STACK</c>
    /// срабатывает; провал у Хьюберта — вор гибнет, его вещи — мешком на его клетке; дуэль с одиноким Гоблином — отряд уничтожен; вор у
    /// неподкупного Гнома гибнет до свитка, вещи — Гному.</summary>
    [SkippableFact]
    public void Edge_Thieves_KillerHangedDuelIncorruptible()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        var seen = new Recorder();
        session.Watch(seen);
        string human = session.ActiveHuman;
        var k1 = Zone(facts, "K1");
        var kill = session.Thief(Str(k1, "stack"), Str(k1, "target"), ThiefAction.Assassinate, new ScriptedRng([0, 0, 0, 0]));
        Assert.True(kill.Success, kill.Refused);
        Assert.False(session.Map.Stacks.ContainsKey(Str(k1, "target")));
        Assert.Contains(session.Map.Destroyed, d => Same(d.StackId, Str(k1, "target")) && Same(d.KillerId, human));
        Assert.Contains(seen.Events, e => Same(e.Event.Id, Str(k1, "event")));

        var h1 = Zone(facts, "H1");
        var thief = session.Map.Stacks[Str(h1, "stack")];
        var items = thief.Items.ToList();
        var hanged = session.Thief(thief.Id, Str(h1, "target"), ThiefAction.Assassinate, new ScriptedRng([99, 99, 99, 99]));
        Assert.True(hanged.ThiefDied);
        var bag = session.Map.Sites.SingleOrDefault(s => s.Kind == SiteKind.Bag && s.X == thief.X && s.Y == thief.Y);
        Out.WriteLine($"мешок повешенного: {bag?.Id ?? "нет"} — {string.Join(", ", bag?.Items ?? [])}");
        Assert.NotNull(bag);
        Assert.All(items, i => Assert.Contains(i, bag.Items));

        var du = Zone(facts, "DU");
        var duel = session.StartBattle(Str(du, "stack"), Str(du, "target"), duel: true);
        var fight = Fight(duel, 1, session.Book!);
        fight.Auto = true;
        fight.Resolve();
        session.FinishBattle(fight.Log);
        Assert.False(session.Map.Stacks.ContainsKey(Str(du, "target")));
        Assert.Contains(seen.Events, e => Same(e.Event.Id, Str(du, "event")));

        var inc = Zone(facts, "INC");
        var incThief = session.Map.Stacks[Str(inc, "stack")];
        Assert.True(session.ThiefIncorruptible(incThief.Id, Str(inc, "target")));
        Assert.False(session.Map.Stacks.ContainsKey(incThief.Id));
        Assert.All(incThief.Items, i => Assert.Contains(i, session.Map.Stacks[Str(inc, "target")].Items));
    }
}
