using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Выигранные сражения отряда (<c>NBBATTLE</c>, <see cref="MapStack.BattlesWon"/>): поле читается из <c>.sg</c>,
/// победитель боя получает +1, запись в сохранение и обратно.</summary>
[Trait("Category", "Corpus")]
public class BattlesWonTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Корпус 20260919-human: после t01-004 у <c>S073KC0001</c> 1 победа, у <c>S073KC0002</c> — 0 (t02-005 — 2).
    /// Бой в партии: победитель +1, проигравший не меняется; сохранение хранит поле.</summary>
    [SkippableFact]
    public void Loaded_Incremented_Saved()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t01-004.sg");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        Assert.Equal((1, 0), (looks.Map.Stacks["S073KC0001"].BattlesWon, looks.Map.Stacks["S073KC0002"].BattlesWon));

        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), content.MapArt, book);
        var mine = session.Map.Stacks.Values.First(s => s.Owner.Equals("S073PL000a", StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var enemy = session.Map.Stacks.Values.First(s => !s.Owner.Equals(mine.Owner, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var state = session.StartBattle(mine.Id, enemy.Id, duel: true);
        var battle = new D2.Data.Battle.BattleSession(state, new BattleSim(state, session.Rng, book.Rules.Constants), book.Rules.Constants) { Auto = true };
        battle.Resolve();
        session.FinishBattle(battle.Log);
        var before = new[] { mine, enemy }.ToDictionary(s => s.Id, s => s.BattlesWon);
        var winner = new[] { mine.Id, enemy.Id }.Single(id => session.Map.Stacks.TryGetValue(id, out var s) && s.BattlesWon == before[id] + 1);
        string loser = winner == mine.Id ? enemy.Id : mine.Id;
        if (session.Map.Stacks.TryGetValue(loser, out var lost)) Assert.Equal(before[loser], lost.BattlesWon);

        var back = GameSave.FromJson(GameSave.ToJson(session.Snapshot()));
        Assert.Equal(session.Map.Stacks[winner].BattlesWon, back.Looks.Map.Stacks[winner].BattlesWon);
    }
}
