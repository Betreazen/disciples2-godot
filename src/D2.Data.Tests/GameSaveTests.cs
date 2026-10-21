using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Сохранение порта (<see cref="GameSave"/>): записанное и прочитанное обратно — та же партия, числами.</summary>
[Trait("Category", "Corpus")]
public class GameSaveTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("Exports/Power to Corrupt.sg")]
    [InlineData("Exports/Tutorial.sg")]
    [InlineData("corpus:20260919-human/rastlennaya-vlast-dwarf-t05-011.sg")]
    public void RoundTrip_KeepsTheGame(string source)
    {
        string path = source.StartsWith("corpus:", StringComparison.Ordinal)
            ? Path.Combine(CorpusDir(), source["corpus:".Length..])
            : Path.Combine(GamePaths.GameDir, source);
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        var state = new GameState(GameInfo.FromMap(Path.GetFileNameWithoutExtension(path), looks), looks);

        var at = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
        string json = GameSave.ToJson(state, savedAt: at);
        var back = GameSave.FromJson(json);
        output.WriteLine($"{source}: {json.Length / 1024} КБ");

        // Запись прочитанного даёт тот же текст — ничего не потеряно и не добавлено.
        Assert.Equal(json, GameSave.ToJson(back, savedAt: at));
        var a = state.Looks.Map;
        var b = back.Looks.Map;
        Assert.Equal(a.Turn, b.Turn);
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(a.MapSeed, b.MapSeed);
        for (int x = 0; x < a.Size; x++)
            for (int y = 0; y < a.Size; y++) Assert.Equal(a.Tiles[x, y], b.Tiles[x, y]);
        foreach (var (id, player) in a.Players)
        {
            Assert.Equal(player.Bank, b.Players[id].Bank);
            Assert.Equal(Hidden(a.Fog[player.FogId]), Hidden(b.Fog[player.FogId]));
        }
        foreach (var (id, stack) in a.Stacks)
            Assert.Equal((stack.X, stack.Y, stack.Move, stack.UnitIds.Count), (b.Stacks[id].X, b.Stacks[id].Y, b.Stacks[id].Move, b.Stacks[id].UnitIds.Count));
        Assert.Equal(a.Plan.Count, b.Plan.Count);
        Assert.Equal(state.Looks.Stacks.Count, back.Looks.Stacks.Count);
        Assert.Equal(state.Looks.Roads.Count, back.Looks.Roads.Count);
        Assert.True(a.Stacks.Keys.All(k => b.Stacks.ContainsKey(k.ToLowerInvariant())), "id после чтения — без учёта регистра");
    }

    [SkippableFact]
    public void Slots_ListWrittenSaves_NewestFirst_AndSkipForeignFiles()
    {
        string path = Path.Combine(GamePaths.Exports, "Tutorial.sg");
        Skip.IfNot(File.Exists(path), "нет копии игры");
        var looks = MapLooksLoader.Load(path);
        var state = new GameState(GameInfo.FromMap("Tutorial", looks), looks);
        string dir = Path.Combine(Path.GetTempPath(), "d2-saves-" + Guid.NewGuid().ToString("N"));
        try
        {
            GameSave.Write(SaveSlots.PathFor(dir, "первое: день/0"), state, "первое: день/0");
            Thread.Sleep(20);
            GameSave.Write(SaveSlots.PathFor(dir, "второе"), state, "второе");
            File.WriteAllText(Path.Combine(dir, "чужой" + SaveSlots.Extension), "{}");
            var slots = SaveSlots.List(dir);
            Assert.Equal(["второе", "первое: день/0"], slots.Select(s => s.Title));
            Assert.All(slots, s => Assert.Equal("Tutorial", s.Scenario));
            Assert.Equal(looks.Map.Turn, GameSave.Read(slots[1].Path).Looks.Map.Turn);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Потоки случайных чисел пишутся в сохранение: загруженная партия бросает те же числа, что бросила бы
    /// незаписанная (R250 — таблица меняется с каждым шагом, поэтому пишется целиком).</summary>
    [SkippableFact]
    public void Snapshot_KeepsRandomStreams()
    {
        string path = Path.Combine(GamePaths.Exports, "Tutorial.sg");
        Skip.IfNot(File.Exists(path), "нет копии игры");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("Tutorial", looks), looks), MapArtData.Empty);
        for (int i = 0; i < 1000; i++) session.Rng.Next(100);
        for (int i = 0; i < 7; i++) session.Crt.Next();

        var loaded = new GameSession(GameSave.FromJson(GameSave.ToJson(session.Snapshot())), MapArtData.Empty);

        Assert.Equal(Enumerable.Range(0, 300).Select(_ => session.Rng.Next(1000)),
                     Enumerable.Range(0, 300).Select(_ => loaded.Rng.Next(1000)));
        Assert.Equal(Enumerable.Range(0, 20).Select(_ => session.Crt.Next()),
                     Enumerable.Range(0, 20).Select(_ => loaded.Crt.Next()));
    }

    /// <summary>Итоги хода людей, ещё не показанные (у движка — <c>CTurnSummary</c> в <c>.sg</c>), идут в сохранение v9: снимок берёт их у
    /// поставщика сцены, загрузка отдаёт их сцене один раз; сохранение v8 читается без них.</summary>
    [SkippableFact]
    public void Snapshot_KeepsPendingTurnSummaries_OldSaveWithout()
    {
        string path = Path.Combine(GamePaths.Exports, "Tutorial.sg");
        Skip.IfNot(File.Exists(path), "нет копии игры");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("Tutorial", looks), looks), MapArtData.Empty);
        var entry = new D2.Data.Ui.Windows.TurnSummaryEntry(1, "Отряд атакован", "S001KC0001");
        session.SummariesForSave = () => new Dictionary<string, PendingSummary> { ["S001PL0001"] = new(4, [entry]) };

        string json = GameSave.ToJson(session.Snapshot());
        var loaded = new GameSession(GameSave.FromJson(json), MapArtData.Empty);

        var pending = loaded.TakeSummaries();
        Assert.Equal(4, pending["s001pl0001"].Day);
        Assert.Equal([entry], pending["S001PL0001"].Entries);
        Assert.Empty(loaded.TakeSummaries());
        Assert.Empty(loaded.Snapshot().Summaries);
        var old = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        old["Version"] = 8;
        old.Remove("Summaries");
        Assert.Empty(GameSave.FromJson(old.ToJsonString()).Summaries);
    }

    /// <summary>Сохранения прежних сборок согласуются при чтении: «лидер жив» — по здоровью лидера (до f782245 зелье воскрешения признак
    /// не возвращало: «Power To Corrupt — день 20» от 14.10, <c>S073KC0001</c> с лидером на 225 HP и <c>S073KC0003</c> на 150 — нарушение
    /// <c>leader_alive</c> зонда при подключении), облик подобранного мешка снят. Здесь — то же поверх «Power to Corrupt».</summary>
    [SkippableFact]
    public void Read_HealsLeaderAliveAndTakenBag_OfOlderBuilds()
    {
        string path = Path.Combine(GamePaths.Exports, "Power to Corrupt.sg");
        Skip.IfNot(File.Exists(path), "нет копии игры");
        var looks = MapLooksLoader.Load(path);
        var map = looks.Map;
        var stack = map.Stacks.Values.OrderBy(s => s.Id).First(s => s.LeaderAlive && map.Units[s.LeaderId].Hp > 0);
        var bag = map.Sites.First(s => s.Kind == SiteKind.Bag);
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = stack with { LeaderAlive = false };
        var sites = map.Sites.Select(s => s.Id == bag.Id ? s with { Gone = true, Items = [] } : s).ToList();
        var stale = looks with
        {
            Map = GameSession.With(map, stacks: stacks, sites: sites),
            Stacks = [.. looks.Stacks.Select(s => s.Id == stack.Id ? s with { LeaderAlive = false } : s)],
        };
        Assert.Contains(D2.Data.Diagnostics.MapInvariants.Check(stale.Map, null), v => v.Code == "leader_alive" && v.Object == stack.Id);
        Assert.Contains(stale.Bags, b => b.Id == bag.Id);

        var back = GameSave.FromJson(GameSave.ToJson(new GameState(GameInfo.FromMap("PtC", stale), stale)));

        Assert.True(back.Looks.Map.Stacks[stack.Id].LeaderAlive);
        Assert.True(back.Looks.Stacks.Single(s => s.Id == stack.Id).LeaderAlive);
        Assert.DoesNotContain(D2.Data.Diagnostics.MapInvariants.Check(back.Looks.Map, null), v => v.Code == "leader_alive");
        Assert.DoesNotContain(back.Looks.Bags, b => b.Id == bag.Id);
        Assert.Equal(looks.Bags.Count - 1, back.Looks.Bags.Count);
    }

    [Fact]
    public void Read_RejectsForeignFormat_AndNewerVersion()
    {
        Assert.Throws<InvalidDataException>(() => GameSave.FromJson("{\"Format\":\"other\",\"Version\":1}"));
        Assert.Throws<InvalidDataException>(() => GameSave.FromJson($"{{\"Format\":\"{GameSave.Format}\",\"Version\":{GameSave.Version + 1}}}"));
        Assert.Throws<InvalidDataException>(() => GameSave.FromJson("не json"));
    }

    private static int Hidden(bool[,] fog)
    {
        int n = 0;
        foreach (bool cell in fog) if (cell) n++;
        return n;
    }

    internal static string CorpusDir()
    {
        if (Environment.GetEnvironmentVariable("D2_CORPUS_DIR") is { Length: > 0 } dir) return dir;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
        return d is null ? "" : Path.Combine(d.FullName, "corpus");
    }
}
