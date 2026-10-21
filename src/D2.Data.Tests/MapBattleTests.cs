using D2.Data.Battle;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Бой с карты (<see cref="MapBattle"/>): отряды партии собираются в бой, модель доигрывает его, исход
/// возвращается в карту.</summary>
[Trait("Category", "Corpus")]
public class MapBattleTests(ITestOutputHelper output)
{
    private static string Rules => Path.Combine(AppContext.BaseDirectory, "data", "rules");

    /// <summary>Индекс юнита в группе (<see cref="BattleUnit.GroupIndex"/>) — его место в <c>UnitIds</c> группы карты, а не клетка построения:
    /// им раздача излишка DrainOverflow обходит группу (<c>0x65fbb8</c>). Ревью 13.10: бой собирается по клеткам, и порядок состояния
    /// боя с порядком группы не совпадает.</summary>
    [SkippableFact]
    public void Build_SetsGroupIndexFromUnitIds()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = GameBook.Load(Rules);
        var map = MapLoader.Load(path);
        var human = map.Players.Values.First(p => p.IsHuman);
        var attacker = map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0)
                                        .OrderByDescending(s => s.UnitIds.Count).First();
        var defender = map.Stacks.Values.First(s => s.Owner != human.Id && s.Inside.Length == 0 && s.UnitIds.Count > 0);

        var state = MapBattle.Build(map, attacker.Id, defender.Id, book, difficulty: 1, attackerMove: 0);

        foreach (var u in state.Units)
        {
            var group = u.Attacker ? attacker : defender;
            Assert.Equal(group.UnitIds.ToList().IndexOf(u.Id), u.GroupIndex);
        }
    }

    /// <summary>«Меню героя» в конце боя (<c>battleEnd</c> <c>0x637581</c>): только у того, кто играет за нападающий отряд (<c>CTaskBattle</c>
    /// <c>+0x2c</c>, <c>0x4d77b8…0x4d7842</c>), и пока у нападающих есть живой юнит (<c>0x637803</c>); прежде кнопка была у любого конца боя.</summary>
    [SkippableFact]
    public void Paperdoll_OnlyOwnAttackerStackWithLiveUnits()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = GameBook.Load(Rules);
        var map = MapLoader.Load(path);
        var human = map.Players.Values.First(p => p.IsHuman);
        var attacker = map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);
        var defender = map.Stacks.Values.First(s => s.Owner != human.Id && s.Inside.Length == 0 && s.UnitIds.Count > 0);
        var state = MapBattle.Build(map, attacker.Id, defender.Id, book, difficulty: 1, attackerMove: 0);

        Assert.True(MapBattle.OwnsAttackerStack(map, attacker.Id, human.Id));
        Assert.True(MapBattle.OwnsAttackerStack(map, attacker.Id, null));   // вид редактора
        Assert.False(MapBattle.OwnsAttackerStack(map, attacker.Id, defender.Owner));
        Assert.False(MapBattle.OwnsAttackerStack(map, "S999FT9999", human.Id));   // нападает не отряд
        Assert.True(MapBattle.PaperdollOffered(state, ownAttackerStack: true));
        Assert.False(MapBattle.PaperdollOffered(state, ownAttackerStack: false));
        foreach (var u in state.Units.Where(u => u.Attacker)) u.Hp = 0;
        Assert.False(MapBattle.PaperdollOffered(state, ownAttackerStack: true));
    }

    /// <summary>Смерть от тика DoT (<see cref="BattleState.DotDeaths"/>) делится одной суммой с убитыми следующим действием юнитами той
    /// же стороны — как в журнале 20261014-replay ai-q57, бой seq 2669822: ⌊(55 + 20)/3⌋ = 25, а не ⌊55/3⌋ + ⌊20/3⌋ = 24 (харнесс опыта).
    /// Раньше смерть от тика делилась в конце боя отдельно.</summary>
    [SkippableFact]
    public void DotDeath_SharedWithNextActionKillsOfSameSide()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = GameBook.Load(Rules);
        var map = MapLoader.Load(path);
        var human = map.Players.Values.First(p => p.IsHuman);
        var attacker = map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0)
                                        .OrderByDescending(s => s.UnitIds.Count).First();
        var defender = map.Stacks.Values.Where(s => s.Owner != human.Id && s.Inside.Length == 0)
                                        .OrderByDescending(s => s.UnitIds.Count).First();
        var state = MapBattle.Build(map, attacker.Id, defender.Id, book, difficulty: 1, attackerMove: 0);
        var hpBefore = state.Units.ToDictionary(u => u.Id, u => u.Hp);
        var a = state.Units.First(u => u.Attacker);
        var victims = state.Units.Where(u => !u.Attacker).Take(2).ToList();
        Skip.If(victims.Count < 2, "у защитника меньше двух юнитов");
        var (dot, hit) = (victims[0], victims[1]);
        // Раунд 1: удар без смерти; в начале хода dot умирает от тика; раунд 2: удар убивает hit.
        List<TurnRecord> log = [new(1, a.Id, hit.Id, false, 1, Math.Max(1, hit.Hp - 1)), new(2, a.Id, hit.Id, false, hit.Hp, 0)];
        state.DotDeaths.Add(new DotDeath(dot.Id, 1));
        dot.Hp = 0;
        hit.Hp = 0;

        var kills = BattleAftermath.Kills(state, hpBefore, log, book);

        var kill = Assert.Single(kills);
        Assert.Equal(dot.Type.XpKilled + hit.Type.XpKilled, kill.XpKilled);
        Assert.Contains(a.Id, kill.Receivers);
    }

    /// <summary>Убитый превращённый (Ведьмой, понижением уровня) даёт <c>XP_KILLED</c> исходной реализации: сумма опыта <c>0x625a8e</c> берёт
    /// солдата <c>0x5ea4dd</c> (у превращённого — <c>origTypeId</c>, <c>0x625b93</c>). Журнал карты 17 (19.10, бой [5062040]): Бес из Гнома
    /// <c>S143UN000d</c> убит последним — 35 (<c>G000UU5236</c>), у выживших 157, а не 149 (10 Беса).</summary>
    [SkippableFact]
    public void TransformedVictim_GivesOriginalXpKilled()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = GameBook.Load(Rules);
        var map = MapLoader.Load(path);
        var human = map.Players.Values.First(p => p.IsHuman);
        var attacker = map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);
        var defender = map.Stacks.Values.First(s => s.Owner != human.Id && s.Inside.Length == 0 && s.UnitIds.Count > 0);
        var state = MapBattle.Build(map, attacker.Id, defender.Id, book, difficulty: 1, attackerMove: 0);
        var hpBefore = state.Units.ToDictionary(u => u.Id, u => u.Hp);
        var a = state.Units.First(u => u.Attacker);
        var victim = state.Units.First(u => !u.Attacker && u.Type.SizeSmall);
        int original = victim.Type.XpKilled;
        Assert.True(TransformAttacks.TransformOther(book.Rules.Attacks["g000aa0067"], victim, state).Applied);   // проклятие Ведьмы
        Assert.NotEqual(original, victim.Type.XpKilled);
        List<TurnRecord> log = [new(1, a.Id, victim.Id, false, victim.Hp, 0)];
        victim.Hp = 0;

        var kill = Assert.Single(BattleAftermath.Kills(state, hpBefore, log, book));

        Assert.Equal(original, kill.XpKilled);
    }

    /// <summary>Сохранение живой партии: отряд человека против первого нейтрального отряда. Все юниты опознаны,
    /// бой доигрывается, проигравший отряд снимается с карты вместе с юнитами, у выживших растёт опыт.</summary>
    [SkippableFact]
    public void StackVersusStack_FromLiveSave()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = GameBook.Load(Rules);
        var rules = book.Rules;
        var map = MapLoader.Load(path);
        var human = map.Players.Values.First(p => p.IsHuman);
        var attacker = map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0)
                                        .OrderByDescending(s => s.UnitIds.Count).First();
        var neutral = map.Players.Values.First(p => p.RaceId.EndsWith("RR0004", StringComparison.OrdinalIgnoreCase));
        var defender = map.Stacks.Values.Where(s => s.Owner == neutral.Id && s.Inside.Length == 0)
                                        .OrderBy(s => s.Id, StringComparer.Ordinal).First();

        var state = MapBattle.Build(map, attacker.Id, defender.Id, book, difficulty: 1, attackerMove: 0);
        int alive = (attacker.UnitIds.Count(id => map.Units[id].Hp > 0)) + defender.UnitIds.Count(id => map.Units[id].Hp > 0);
        Assert.Equal(alive, state.Units.Count);
        Assert.Contains(state.Units, u => u.Attacker);
        Assert.Contains(state.Units, u => !u.Attacker);
        var xpBefore = state.Units.ToDictionary(u => u.Id, u => (u.Xp, map.Units[u.Id].Level, map.Units[u.Id].TypeId));
        var hpBefore = state.Units.ToDictionary(u => u.Id, u => u.Hp);

        var sim = new BattleSim(state, new EngineRng(), rules.Constants);
        sim.Run(100);
        sim.AfterBattle();
        Assert.NotNull(sim.Winner);
        var kills = BattleAftermath.Kills(state, hpBefore, sim.Log, book);
        var after = BattleAftermath.Award(MapBattle.Apply(map, state), state, kills, book, difficulty: 1, suggestedLevel: 1);

        string loser = sim.Winner!.Equals(attacker.Id, StringComparison.OrdinalIgnoreCase) ? defender.Id : attacker.Id;
        string winner = loser == defender.Id ? attacker.Id : defender.Id;
        output.WriteLine($"{attacker.Id} ({attacker.UnitIds.Count}) против {defender.Id} ({defender.UnitIds.Count}): победил {sim.Winner}, " +
                         $"раундов {state.Round}");
        Assert.False(after.Stacks.ContainsKey(loser), "проигравший отряд снят с карты");
        Assert.DoesNotContain(map.Stacks[loser].UnitIds, after.Units.ContainsKey);
        Assert.DoesNotContain(after.Plan.Values, ids => ids.Contains(loser));
        var survivors = after.Stacks[winner].UnitIds.Where(id => after.Units[id].Hp > 0).ToList();
        Assert.NotEmpty(survivors);
        foreach (var id in survivors)
            output.WriteLine($"  {id}: {xpBefore[id]} -> ({after.Units[id].Xp}, {after.Units[id].Level}, {after.Units[id].TypeId})");
        Assert.NotEmpty(kills);
        Assert.True(survivors.Any(id => after.Units[id] is var u && (u.Xp, u.Level, u.TypeId) != xpBefore[id]),
                    "выжившие получили опыт или уровень");
    }

    /// <summary>Путь на клетку чужого отряда кончается рядом с ним: клетку врага искатель пути снимает (цель
    /// непроходима, <c>docs/map-path.md</c>), отряд встаёт вплотную и нападает. Tutorial: отряд человека в столице,
    /// нейтрал S001KC002b на (42,18) — единственный, до кого из столицы есть путь (север закрыт горами).</summary>
    [SkippableFact]
    public void PathToEnemy_EndsNextToIt()
    {
        string path = Path.Combine(GamePaths.Exports, "Tutorial.sg");
        Skip.IfNot(File.Exists(path), "нет копии игры");
        var map = MapLoader.Load(path);
        var stack = map.Stacks["S001KC0000"];
        var enemy = map.Stacks["S001KC002b"];
        var found = D2.Rules.Map.PathFinder.Find(map, stack, enemy.X, enemy.Y,
            new D2.Rules.Map.MoveOptions(true, false, false, false, false, false), D2.Rules.Map.MovementRules.Vanilla, 20);
        output.WriteLine(string.Join(" ", found.Points.Select(p => $"({p.X},{p.Y}):{p.Cost}")));
        Assert.True(found.Points.Count > 1);
        var end = found.Points[^1];
        Assert.NotEqual((enemy.X, enemy.Y), (end.X, end.Y));
        Assert.True(Math.Abs(end.X - enemy.X) <= 1 && Math.Abs(end.Y - enemy.Y) <= 1, "последняя точка — вплотную к врагу");
    }

    /// <summary>Повышение после боя восстанавливает здоровье до полного (со слов пользователя); опыт без
    /// повышения здоровья не трогает.</summary>
    [SkippableFact]
    public void LevelUp_HealsToFull()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = GameBook.Load(Rules);
        var map = MapLoader.Load(path);
        var human = map.Players.Values.First(p => p.IsHuman);
        var stack = map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.UnitIds.Count > 1);
        var enemy = map.Stacks.Values.First(s => s.Owner != human.Id);
        var leader = map.Units[stack.LeaderId];
        var soldier = map.Units[stack.UnitIds.First(id => id != stack.LeaderId && map.Units[id].Hp > 0)];
        int next = book.XpNextOf(leader.TypeId, leader.Level);
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[leader.Id] = leader with { Xp = next - 1, Hp = 1 };
        units[soldier.Id] = soldier with { Hp = 1 };
        var hurt = GameSession.With(map, units: units);
        var state = MapBattle.Build(hurt, stack.Id, enemy.Id, book, difficulty: 1, attackerMove: 0);

        // Опыт лидеру хватает на уровень (ловушка «тип не создан» снимает единицу — отсюда запас), солдату — нет.
        var after = BattleAftermath.Award(hurt, state, [new D2.Rules.Units.Kill(5, [leader.Id]), new D2.Rules.Units.Kill(1, [soldier.Id])],
                                          book, difficulty: 1, suggestedLevel: 1);
        var grown = after.Units[leader.Id];
        output.WriteLine($"лидер {leader.TypeId}: ур. {leader.Level} → {grown.Level}, HP 1 → {grown.Hp}; солдат HP {after.Units[soldier.Id].Hp}");
        Assert.Equal(leader.Level + 1, grown.Level);
        Assert.True(grown.Hp > 1 && grown.Hp >= leader.Hp, "повышенный лидер вылечен до полного");
        Assert.Equal(1, after.Units[soldier.Id].Hp);
        // Сообщение боя для показа и звука (UPGRADE{раса}, docs/ai-screen.md §11): вырос только лидер.
        Assert.Equal([leader.Id], state.Notes.Where(n => n.Kind == D2.Rules.Battle.BattleNoteKind.LevelUp).Select(n => n.UnitId));
    }

    /// <summary>Солдат, выросший в новый тип, и в бою до «Закрыть» уже нового типа (облик и портрет) с полным здоровьем
    /// (замечание пользователя 14.10: анимация роста была, облик и портрет остались старыми).</summary>
    [SkippableFact]
    public void TypeUpgrade_ChangesFighterType()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = GameBook.Load(Rules);
        var map = MapLoader.Load(path);
        var human = map.Players.Values.First(p => p.IsHuman);
        var built = human.Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (stack, soldier) = map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive)
            .SelectMany(s => s.UnitIds.Where(id => id != s.LeaderId).Select(id => (s, map.Units[id])))
            .First(p => p.Item2.Hp > 0 && D2.Rules.Units.UnitUpgrade.Successors(book.Rules.Units, p.Item2.TypeId)
                .Any(t => book.UpgradeBuilding.GetValueOrDefault(t.Id, "") is var b && (b.Length == 0 || built.Contains(b))));
        var enemy = map.Stacks.Values.First(s => s.Owner != human.Id);
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[soldier.Id] = soldier with { Xp = book.XpNextOf(soldier.TypeId, soldier.Level) - 1, Hp = 1 };
        var hurt = GameSession.With(map, units: units);
        var state = MapBattle.Build(hurt, stack.Id, enemy.Id, book, difficulty: 1, attackerMove: 0);

        var after = BattleAftermath.Award(hurt, state, [new D2.Rules.Units.Kill(5, [soldier.Id])], book, difficulty: 1, suggestedLevel: 1);
        var grown = after.Units[soldier.Id];
        var fighter = state.Units.Single(u => u.Id == soldier.Id);
        output.WriteLine($"{soldier.TypeId} → {grown.TypeId}, в бою {fighter.Type.Id} HP {fighter.Hp}/{fighter.HpMax}");
        Assert.NotEqual(soldier.TypeId, grown.TypeId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(grown.TypeId, fighter.Type.Id, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(grown.Hp, fighter.Hp);
        Assert.True(fighter.HpMax >= fighter.Hp);
    }
}
