using D2.Data.Battle;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Бой ИИ без экрана (<c>GameSession.AiBattle</c>): предел раундов <c>BAT_ROUND</c> (<see cref="BattleSim.BeginRound"/>),
/// доигрыш без обрыва (<see cref="BattleSession.PlayOut"/>), боевые предметы лидера из снаряжения и их расход на карте
/// (<see cref="MapBattle.SpendItems"/>), воскрешение зельем в бою.</summary>
[Trait("Category", "Corpus")]
public class AiBattleTests(ITestOutputHelper output)
{
    private static string Rules => Path.Combine(AppContext.BaseDirectory, "data", "rules");
    private static readonly Lazy<GameBook> Book = new(() => GameBook.Load(Rules));

    private const string Squire = "G000UU0001";      // L1, hp 100
    private const string Orb = "G000IG9001";         // «Squire Orb», категория 11
    private const string Talisman = "G000IG9101";    // «Squire Talisman», категория 12
    private const string LifePotion = "G000IG0001";  // воскрешение, категория 6

    private static BattleUnit Unit(string id, bool attacker, int slot, int hp)
    {
        var rules = Book.Value.Rules;
        var type = rules.Units[Squire];
        return new BattleUnit
        {
            Id = id, Type = type, Attack = rules.AttackOf(type)!, Attacker = attacker, Slot = slot, HpMax = hp, Hp = hp,
        };
    }

    /// <summary>Два оруженосца, которые за десять раундов друг друга не убьют.</summary>
    private static BattleState Endless(bool limit) => new()
    {
        Units = [Unit("A1", attacker: true, slot: 0, hp: 100_000), Unit("D1", attacker: false, slot: 0, hp: 100_000)],
        AttackerGroup = "A", DefenderGroup = "D", Round = 0, Rules = Book.Value.Rules, LimitRounds = limit,
    };

    private static BattleSession Session(BattleState state) =>
        new(state, new BattleSim(state, new EngineRng(), Book.Value.Rules.Constants), Book.Value.Rules.Constants,
            attackerIsPlayer: false) { Auto = true };

    /// <summary>Бой без человека: после раунда <c>BAT_ROUND</c> (10) нападающие отступают в свой ход раунда 11
    /// (<c>0x62689c</c> → <c>0x626b2c</c>), бой кончается победой защитника, раненые живы.</summary>
    [SkippableFact]
    public void LimitedBattle_AttackersRetreatInRoundEleven()
    {
        Skip.IfNot(File.Exists(Path.Combine(Rules, "GVars.json")), "нет данных правил");
        Assert.Equal(10, Book.Value.Rules.Constants.BatRound);
        var state = Endless(limit: true);
        var battle = Session(state);

        battle.PlayOut();

        var attacker = state.Units.Single(u => u.Attacker);
        output.WriteLine($"раунд {state.Round}, нападающий {attacker.Hp} ушёл={attacker.Retreated}, победил {battle.Winner}");
        Assert.True(battle.Finished);
        Assert.True(attacker.Retreated);
        Assert.True(attacker.Alive);
        Assert.Equal("D", battle.Winner);
        Assert.InRange(state.Round, 11, 12);
    }

    /// <summary>Бой с человеком без быстрого боя предела не знает: за 60 действий он уходит дальше раунда 11, и никто не отступил.</summary>
    [SkippableFact]
    public void UnlimitedBattle_GoesPastRoundEleven()
    {
        Skip.IfNot(File.Exists(Path.Combine(Rules, "GVars.json")), "нет данных правил");
        var state = Endless(limit: false);
        var battle = Session(state);

        battle.PlayOut(maxActions: 60);

        Assert.False(battle.Finished);
        Assert.True(state.Round > 11);
        Assert.DoesNotContain(state.Units, u => u.Retreating || u.Retreated);
    }

    /// <summary>Быстрый бой (<c>BTN_RESOLVE</c>) включает предел и в бою с человеком (флаг <c>0xf48</c> бит 7).</summary>
    [SkippableFact]
    public void Resolve_TurnsOnTheLimit()
    {
        Skip.IfNot(File.Exists(Path.Combine(Rules, "GVars.json")), "нет данных правил");
        var state = Endless(limit: false);
        var battle = Session(state);

        battle.Resolve();

        Assert.True(state.LimitRounds);
        Assert.True(battle.Finished);
        Assert.True(state.Units.Single(u => u.Attacker).Retreated);
    }

    /// <summary>Зелье воскрешения в бою: то же <c>applyPotion</c>, что на карте, — HP 1; статусы павшего сняты, предмет в
    /// списке применённых (его тратит хозяин на карте).</summary>
    [SkippableFact]
    public void RevivePotion_InBattle_RaisesToOneHp()
    {
        Skip.IfNot(File.Exists(Path.Combine(Rules, "GVars.json")), "нет данных правил");
        var bearer = Unit("A1", attacker: true, slot: 0, hp: 100);
        var fallen = Unit("A2", attacker: true, slot: 1, hp: 100);
        fallen.Hp = 0;
        fallen.Retreating = true;
        bearer.BattleItems.Add(Book.Value.Items[LifePotion]);
        var state = new BattleState
        {
            Units = [bearer, fallen, Unit("D1", attacker: false, slot: 0, hp: 100)],
            AttackerGroup = "A", DefenderGroup = "D", Round = 1, Rules = Book.Value.Rules,
        };
        var sim = new BattleSim(state, new ScriptedRng([]), Book.Value.Rules.Constants);

        Assert.Equal(PotionResult.AlreadyAlive, sim.DrinkPotion(bearer, 0, bearer));
        Assert.Equal(PotionResult.Ok, sim.DrinkPotion(bearer, 0, fallen));

        Assert.Equal(1, fallen.Hp);
        Assert.False(fallen.Retreating);
        Assert.Equal([new UsedItem("A1", 0)], state.UsedItems);
        sim.AfterBattle();
        Assert.Empty(bearer.BattleItems);            // применённое зелье уничтожено концом боя
        Assert.Single(state.UsedItems);              // а список применённых остался хозяину
    }

    /// <summary>Выпитое зелье уходит из слота сразу, а не в конце боя (прогон 15.10, пометка 4): движок кладёт в слот пустой id, если
    /// предмет уже в <c>usedItemIds</c> (<c>0x649939</c> → <c>0x622fee</c>), — значков на один меньше, второй слот на месте; брать
    /// выпитое снова нельзя (<see cref="BattleSession.ItemTargets"/>).</summary>
    [SkippableFact]
    public void DrunkPotion_LeavesSlotAtOnce()
    {
        Skip.IfNot(File.Exists(Path.Combine(Rules, "GVars.json")), "нет данных правил");
        var heal = Book.Value.Items.Values.First(i => i.Category == ItemCategory.PotionHeal);
        var bearer = Unit("A1", attacker: true, slot: 0, hp: 100);
        bearer.Type = bearer.Type with { IsLeader = true };
        bearer.Hp = 10;
        bearer.BattleItems.Add(heal);
        bearer.BattleItems.Add(heal);
        var state = new BattleState
        {
            Units = [bearer, Unit("D1", attacker: false, slot: 0, hp: 100)],
            AttackerGroup = "A", DefenderGroup = "D", Round = 1, Rules = Book.Value.Rules,
        };
        var sim = new BattleSim(state, new ScriptedRng([]), Book.Value.Rules.Constants);
        Assert.Equal([0, 1], D2.Data.Gfx.BattleScene.ShownItems(bearer, humanTurn: true));

        Assert.Equal(PotionResult.Ok, sim.DrinkPotion(bearer, 0, bearer));

        Assert.Equal(2, bearer.BattleItems.Count);                                          // в снаряжении до конца боя (0x629755)
        Assert.Equal([1], D2.Data.Gfx.BattleScene.ShownItems(bearer, humanTurn: true));     // в слоте — только невыпитое
        Assert.Empty(D2.Data.Gfx.BattleScene.ShownItems(bearer, humanTurn: false));
    }

    /// <summary>Бой с карты: лидер берёт в бой предметы слотов <c>BATTLE1</c>/<c>BATTLE2</c>; после боя применённая сфера
    /// уходит из отряда, применённый талисман теряет заряд (записи не было — заряды нового, <c>TALIS_CHRG</c>).</summary>
    [SkippableFact]
    public void MapBattle_LeaderCarriesBattleItems_AndSpendsThem()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var book = Book.Value;
        var map = MapLoader.Load(path);
        var neutral = map.Players.Values.First(p => p.RaceId.EndsWith("RR0004", StringComparison.OrdinalIgnoreCase));
        var human = map.Players.Values.First(p => p.IsHuman);
        var attacker = map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0)
                                        .OrderBy(s => s.Id, StringComparer.Ordinal).First();
        var defender = map.Stacks.Values.Where(s => s.Owner == neutral.Id && s.Inside.Length == 0)
                                        .OrderBy(s => s.Id, StringComparer.Ordinal).First();
        const string orb = "S073IM0f01", talisman = "S073IM0f02";
        var equipment = attacker.Equipment.ToList();
        equipment[Equipment.SlotBattle1] = orb;
        equipment[Equipment.SlotBattle2] = talisman;
        var armed = attacker with { Items = [.. attacker.Items, orb, talisman], Equipment = equipment };
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[armed.Id] = armed;
        var types = map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[orb] = Orb;
        types[talisman] = Talisman;
        map = GameSession.With(map, stacks: stacks, itemTypes: types);

        var state = MapBattle.Build(map, armed.Id, defender.Id, book, difficulty: 1, attackerMove: 0);

        Assert.Equal(0, state.Round);                                   // первый раунд боя — 1, как у движка
        Assert.False(state.LimitRounds);                                // нападает человек
        var leader = state.Unit(armed.LeaderId)!;
        Assert.Equal([Orb, Talisman], leader.BattleItems.Select(i => i.Id.ToUpperInvariant()).ToArray());
        state.UsedItems.Add(new UsedItem(leader.Id, 0));
        state.UsedItems.Add(new UsedItem(leader.Id, 1));
        state.UsedItems.Add(new UsedItem(leader.Id, 1));              // повтор в том же бою заряда не тратит

        var spent = MapBattle.SpendItems(map, state, book);

        var after = spent.Stacks[armed.Id];
        Assert.DoesNotContain(orb, after.Items);
        Assert.Equal("", after.Equipment[Equipment.SlotBattle1]);
        Assert.Contains(talisman, after.Items);
        Assert.Equal(book.Vars.TalismanCharges - 1, spent.TalismanCharges[talisman]);
        output.WriteLine($"{armed.Id}: сфера ушла, талисман {spent.TalismanCharges[talisman]} зарядов");
    }
}
