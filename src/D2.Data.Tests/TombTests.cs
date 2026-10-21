using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Могилы (<c>docs/day-cycle.md</c> §13): гибель целого отряда в бою ставит могилу на его клетке (или дописывает запись),
/// кроме леса, воды, отряда в городе, призванного и «знатного» лидера; запись — владелец, имя, убийца, день; сохранение хранит.</summary>
[Trait("Category", "Corpus")]
public class TombTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules"))));

    private static GameSession FromSave(string save)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", save);
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), Shared.Value.Content.MapArt, Shared.Value.Book);
    }

    /// <summary>Могилы сохранения читаются с записями (корпус: t30-055 — у <c>S073TB0000</c> запись о погибшем отряде).</summary>
    [SkippableFact]
    public void Loaded_WithEntries()
    {
        var session = FromSave("rastlennaya-vlast-dwarf-t30-055.sg");
        Assert.NotEmpty(session.Map.Tombs);
        Assert.All(session.Map.Tombs, t => Assert.NotEmpty(t.Entries));
        Assert.All(session.Map.Tombs, t => Assert.Contains(t.Id, session.Map.Plan[(t.X, t.Y)]));
        // Облик для экрана: те же могилы и кадр по почве клетки (0x5c593a).
        Assert.Equal(session.Map.Tombs.Select(t => t.Id).Order(), session.Looks.Tombs.Select(t => t.Id).Order());
        Assert.All(session.Looks.Tombs, t => Assert.Equal(session.Map.At(t.X, t.Y).Ground == Ground.Water ? "G000TB0000W" : "G000TB0000G",
                                                          t.Frame(session.Map)));
    }

    /// <summary>Бой до гибели проигравшего на равнине: могила на его клетке с записью (владелец, убийца — игрок-победитель, день);
    /// тот же бой на лесной клетке — могилы нет.</summary>
    [SkippableTheory]
    [InlineData(Ground.Plain, true)]
    [InlineData(Ground.Forest, false)]
    public void BattleDeath_PutsTomb_UnlessForest(Ground ground, bool tomb)
    {
        var session = FromSave("rastlennaya-vlast-dwarf-t01-004.sg");
        var map = session.Map;
        var mine = map.Stacks.Values.First(s => s.Owner.Equals("S073PL000a", StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var victim = map.Stacks.Values.First(s => !s.Owner.Equals(mine.Owner, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0
                                                  && Shared.Value.Book.CategoryOf(map.Units[s.LeaderId].TypeId) == UnitCategory.Leader);
        // Клетка проигравшего — нужный грунт.
        var units = map.Units;
        var tiles = (Tile[,])map.Tiles.Clone();
        tiles[victim.X, victim.Y] = new Tile((byte)((tiles[victim.X, victim.Y].Raw & ~0b111000) | ((int)ground << 3)));
        var changed = new MapState
        {
            Size = map.Size, Tiles = tiles, Stacks = map.Stacks, Units = units, Players = map.Players, Objects = map.Objects, Plan = map.Plan,
            Fog = map.Fog, Turn = map.Turn, Rods = map.Rods, Sites = map.Sites, Relations = map.Relations, TalismanCharges = map.TalismanCharges,
            SpellCasts = map.SpellCasts, ItemCasts = map.ItemCasts, ItemTypes = map.ItemTypes, Limits = map.Limits, MapSeed = map.MapSeed,
            Subraces = map.Subraces, Destroyed = map.Destroyed, Tombs = map.Tombs,
        };
        session = new GameSession(session.State with { Looks = GameSession.Retold(session.Looks, changed, session.Looks.Stacks) },
                                  session.Art, session.Book);
        var sights = new DeathSights();
        session.Observer = sights;   // показ гибели знает, будет ли могила (сессия Г: могила — на середине анимации)
        var state = session.StartBattle(mine.Id, victim.Id, duel: true);
        foreach (var u in state.Units.Where(u => !u.Attacker)) u.Hp = 0;   // защитники пали (итог боя без розыгрыша)
        session.FinishBattle([]);
        Assert.False(session.Map.Stacks.ContainsKey(victim.Id));
        Assert.Equal(tomb, Assert.Single(sights.Seen, s => s.Kind == TurnSightKind.Death && s.Id == victim.Id).Tomb);

        var at = session.Map.Tombs.Where(t => t.X == victim.X && t.Y == victim.Y).ToList();
        if (!tomb)
        {
            Assert.Empty(at);
            return;
        }
        var entry = Assert.Single(at).Entries[^1];
        Assert.Equal((victim.Owner, "S073PL000A", session.Map.Turn), (entry.StackOwner, entry.Killer.ToUpperInvariant(), entry.Turn));
        Assert.Contains(at[0].Id, session.Map.Plan[(victim.X, victim.Y)]);
        Assert.Contains(session.Looks.Tombs, t => t.Id == at[0].Id);
        var back = GameSave.FromJson(GameSave.ToJson(session.Snapshot()));
        Assert.Equal(at[0].Entries.Count, back.Looks.Map.Tombs.Single(t => t.Id == at[0].Id).Entries.Count);
    }

    private sealed class DeathSights : ITurnObserver
    {
        public readonly List<TurnSight> Seen = [];
        public void PlayerTurn(string playerId) { }
        public void StackMoving(StackMove move) { }
        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;
        public void Happened(TurnSight sight) => Seen.Add(sight);
    }
}
