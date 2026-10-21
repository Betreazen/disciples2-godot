using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Menu;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Харнесс старта партии (<see cref="NewGame"/>, <c>docs/new-game.md</c>): новая партия из того же сценария
/// с тем же выбором, что в живой партии корпуса, против её первого сохранения (день 1, до хода ИИ). Игра грузила
/// русский файл сценария (<c>game-dev</c>), с ним и сверяется.</summary>
[Trait("Category", "Corpus")]
public class NewGameHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Корпус 20260919-human — Кланы, маг (G000LR0006); 20260920-legions — Легионы, воитель (G000LR0007).</summary>
    [SkippableTheory]
    [InlineData("20260919-human/rastlennaya-vlast-dwarf-t01-001.sg", 3, 0)]
    [InlineData("20260920-legions/rastlennaya-vlast-heretic-t01-001.sg", 2, 1)]
    public void PowerToCorrupt_Day1_MatchesTheSave(string save, int race, int lordCategory)
    {
        string savePath = Path.Combine(GameSaveTests.CorpusDir(), save);
        string scenarioPath = Path.Combine(GamePaths.GameDir, "..", "game-dev", "Exports", "Power to Corrupt.sg");
        Skip.IfNot(File.Exists(savePath) && File.Exists(scenarioPath), "нет сохранения корпуса или game-dev");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var expected = MapLoader.Load(savePath);
        var human = expected.Players.Values.Single(p => p.IsHuman);
        var setup = new NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == "Power to Corrupt"), Race = race,
            LordCategory = lordCategory, Face = human.Face, Difficulty = expected.Limits!.DifficultyGame, LordName = human.Name,
        };

        var got = NewGame.Start(MapLooksLoader.Load(scenarioPath), setup, content.Catalog, book, new Random(1)).Looks.Map;

        Assert.Equal(expected.Turn, got.Turn);
        Assert.Equal(human.LordId, got.Players[human.Id].LordId, ignoreCase: true);
        Assert.Equal(human.Buildings.Order(), got.Players[human.Id].Buildings.Order(), StringComparer.OrdinalIgnoreCase);
        foreach (var player in expected.Players.Values)
        {
            // Столичный герой: тот же тип, уровень и здоровье; id — следующий свободный (порядок: человек, потом ИИ).
            var stack = expected.Stacks.Values.FirstOrDefault(s => s.Owner == player.Id && s.UnitIds.Count == 1 && s.Inside.Length > 0);
            if (stack is null) continue;
            var want = expected.Units[stack.LeaderId];
            var have = got.Units[got.Stacks[stack.Id].LeaderId];
            output.WriteLine($"{player.Id}: {want.Id} {want.TypeId} ур.{want.Level} HP {want.Hp} — модель {have.Id} {have.TypeId} ур.{have.Level} HP {have.Hp}");
            Assert.Equal((want.Id.ToLowerInvariant(), want.TypeId.ToUpperInvariant(), want.Level, want.Hp, want.Xp),
                         (have.Id.ToLowerInvariant(), have.TypeId, have.Level, have.Hp, have.Xp));
            Assert.Equal(expected.Stacks[stack.Id].UpgradeCount, got.Stacks[stack.Id].UpgradeCount);
        }
        int checkedFog = 0;
        foreach (var player in expected.Players.Values.Where(p => !p.RaceId.EndsWith("RR0004", StringComparison.OrdinalIgnoreCase)))
        {
            var want = expected.Fog[player.FogId];
            var have = got.Fog[player.FogId];
            int extra = 0, missing = 0;
            for (int x = 0; x < got.Size; x++)
                for (int y = 0; y < got.Size; y++)
                {
                    if (!have[x, y] && want[x, y]) extra++;
                    if (have[x, y] && !want[x, y]) missing++;
                }
            output.WriteLine($"туман {player.Id}: лишних {extra}, недостающих {missing}");
            Assert.Equal((0, 0), (extra, missing));
            checkedFog++;
        }
        Assert.Equal(4, checkedFog);
        foreach (var stack in expected.Stacks.Values.Where(s => s.Owner == human.Id))
            Assert.Equal(stack.Move, got.Stacks[stack.Id].Move);
        // Казна: SPELL_BANK файла (200/20 у всех игроков Power to Corrupt) влит в казну человека при занятии места
        // (CVisitorChangePlayerType, docs/new-game.md §4), затем доход дня 1 и множитель — сходится без поправок; у ИИ SPELL_BANK прежний.
        var bank = got.Players[human.Id].Bank;
        output.WriteLine($"казна человека: сохранение {human.Bank}, модель {bank}");
        Assert.Equal(human.Bank, bank);
        Assert.Equal(D2.Rules.Map.Bank.Zero, got.Players[human.Id].SpellBank);
        foreach (var ai in expected.Players.Values.Where(p => !p.IsHuman))
            Assert.Equal(ai.SpellBank, got.Players[ai.Id].SpellBank);
    }

    /// <summary>Карты <c>ng-diffgame-0/2</c> (Mercantile со «сложностью файла» 0 и 2, новый заголовок; прогон 27.09, корпус
    /// <c>20260928-shots</c>): Империя на средней — к первому ходу 650 золота и 75 жизненной маны на обеих, без поправки; значит,
    /// «+200/+20» Power to Corrupt — не от <c>DIFFGAME</c> (<c>docs/new-game.md</c> §4). Три старта в одном запуске игры и один после
    /// перезапуска.</summary>
    [SkippableTheory]
    [InlineData("ng-diffgame-0", "ng-diffgame-0-human-t01-003.sg")]
    [InlineData("ng-diffgame-0", "ng-diffgame-0-human-t01-006.sg")]
    [InlineData("ng-diffgame-0", "ng-diffgame-0-human-t01-031.sg")]
    [InlineData("ng-diffgame-2", "ng-diffgame-2-human-t01-008.sg")]
    public void NgDiffgame_Day1Bank_NoBonus(string map, string save)
    {
        string savePath = Path.Combine(GameSaveTests.CorpusDir(), "20260928-shots", save);
        string scenarioPath = Path.Combine(GamePaths.GameDir, "..", "game-dev", "Exports", map + ".sg");
        Skip.IfNot(File.Exists(savePath) && File.Exists(scenarioPath), "нет сохранения корпуса или game-dev");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var expected = MapLoader.Load(savePath);
        var human = expected.Players.Values.Single(p => p.IsHuman);
        var scenario = MapLooksLoader.Load(scenarioPath);
        var setup = new NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == "Mercantile"), Race = 0,
            LordCategory = content.Catalog.Race(0).Lords.Single(l => l.Id.Equals(human.LordId, StringComparison.OrdinalIgnoreCase)).Category,
            Face = human.Face, Difficulty = expected.Limits!.DifficultyGame, LordName = human.Name,
        };

        var got = NewGame.Start(scenario, setup, content.Catalog, book, new Random(1)).Looks.Map;

        Assert.Equal(new D2.Rules.Map.Bank(500, 0, 0, 0, 0, 0), scenario.Map.Players[human.Id].Bank);
        Assert.Equal(new D2.Rules.Map.Bank(650, 0, 75, 0, 0, 0), human.Bank);
        Assert.Equal(human.Bank, got.Players[human.Id].Bank);
    }
}
