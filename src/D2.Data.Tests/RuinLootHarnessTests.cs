using System.Text.Json;
using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Бой у руины по журналам корпуса (<c>CBattleResultRuin</c> <c>0x41b290</c>): победитель-отряд не нейтрала грабит руину — и с павшим
/// лидером (<c>0x41b381</c> → <c>0x44133a</c> → <c>CEffectLootRuin</c> <c>0x439019</c>). Каждый бой <c>battle_start</c> с руиной в защите
/// ставится в порте на сохранение перед ним (<see cref="GameSession.StartBattle"/>), здоровье юнитов — из <c>battle_end</c>, итог —
/// <see cref="GameSession.CommitBattle"/>; сверка — грабитель руины в следующем сохранении журнала и трофей (<c>item_create</c>
/// <c>loot_ruin</c>).
/// </summary>
[Trait("Category", "Corpus")]
public class RuinLootHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private const string LootRuin = "loot_ruin";

    /// <summary>
    /// Бои у руин корпуса: грабитель после боя у порта тот же, что в сохранении после боя. Прежде вход победителя с павшим лидером
    /// отказывал «лидер пал» (<see cref="Sites.Visit"/>): 20260919-human, <c>S073KC0002</c> у <c>S073RU0006</c> — лидер <c>S073UN01fa</c>
    /// пал, а трофей <c>G000IG0003</c> и 100 золота у движка всё равно достались отряду. 16.10: боёв у руин 63, сверено 56 (прочие —
    /// без сохранения до или после боя, отряда или охраны в сохранении нет), грабитель сошёлся у 56 (со старым правилом — 55), новый
    /// трофей отряда — 36 из 36, победа с павшим лидером — 1.
    /// </summary>
    [SkippableFact]
    public void RuinBattles_LootMatchesNextSave()
    {
        string root = GameSaveTests.CorpusDir();
        Skip.IfNot(Directory.Exists(root), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        int battles = 0, checkedBattles = 0, same = 0, items = 0, itemsSame = 0, fallenLeader = 0;
        foreach (var (dir, file) in Journals(root))
        {
            var events = JournalCache.Read(Path.Combine(dir, file), ["save", "battle_start", "battle_end", "item_create"])
                                     .OrderBy(e => e.Seq).ToList();
            var bound = AiStrategicOrdersHarnessTests.BoundSaves(dir, file, events.OfType<SaveEvent>().ToList());
            foreach (var start in events.OfType<BattleStartEvent>().Where(b => b.DefenderGroup.Length == 10 && b.DefenderGroup[4..6] == "RU"))
            {
                battles++;
                var end = events.OfType<BattleEndEvent>().FirstOrDefault(e => e.Tid == start.Tid && e.Seq > start.Seq);
                var before = bound.Where(b => b.Save.Tid == start.Tid && b.Save.Seq < start.Seq).OrderBy(b => b.Save.Seq).LastOrDefault();
                var after = bound.Where(b => b.Save.Tid == start.Tid && b.Save.Seq > start.Seq).OrderBy(b => b.Save.Seq).FirstOrDefault();
                if (end is null || before.Path is null || after.Path is null) continue;
                var game = Session(before.Path, content, book);
                if (!game.Map.Stacks.TryGetValue(start.AttackerGroup, out var attacker)
                    || game.Map.Sites.FirstOrDefault(s => Same(s.Id, start.DefenderGroup)) is not { LooterId.Length: 0 }
                    || MapBattle.DefenderOf(game.Map, start.DefenderGroup) is null) continue;
                var nextRuin = MapLoader.Load(after.Path).Sites.FirstOrDefault(s => Same(s.Id, start.DefenderGroup));
                if (nextRuin is null) continue;
                checkedBattles++;
                var state = game.StartBattle(attacker.Id, start.DefenderGroup);
                foreach (var u in end.Units)
                    if (state.Unit(u.Id) is { } unit) { unit.Hp = (int)Math.Max(0, u.Hp); unit.Retreated = u.Retreating; }
                if (state.Unit(attacker.LeaderId) is { Alive: false } && Same(end.Winner, attacker.Id)) fallenLeader++;
                game.EndBattle([]);
                game.CommitBattle();
                string portLooter = game.Map.Sites.First(s => Same(s.Id, start.DefenderGroup)).LooterId;
                bool ok = Same(portLooter, nextRuin.LooterId);
                if (ok) same++;
                // Трофей движка (item_create loot_ruin до следующего боя) — тип нового предмета отряда у порта.
                long until = events.OfType<BattleStartEvent>().Where(b => b.Tid == start.Tid && b.Seq > end.Seq).Select(b => b.Seq)
                                   .DefaultIfEmpty(long.MaxValue).Min();
                var trophy = events.OfType<ItemCreateEvent>().FirstOrDefault(i => i.Tid == start.Tid && i.Seq > end.Seq && i.Seq < until
                                                                                     && i.Cause == LootRuin);
                bool itemOk = true;
                if (trophy is not null && trophy.Ids.TryGetValue("item_type", out string? type))
                {
                    items++;
                    var gained = (game.Map.Stacks.GetValueOrDefault(attacker.Id)?.Items ?? []).Except(attacker.Items, StringComparer.OrdinalIgnoreCase)
                                                                                         .Select(i => game.Map.ItemTypes.GetValueOrDefault(i, i));
                    itemOk = gained.Contains(type, StringComparer.OrdinalIgnoreCase);
                    if (itemOk) itemsSame++;
                }
                if (!ok || !itemOk)
                    output.WriteLine($"{Path.GetFileName(dir)}/{file} seq {start.Seq}: {attacker.Id} → {start.DefenderGroup}, победитель {end.Winner}; " +
                                     $"грабитель: порт «{portLooter}», сохранение «{nextRuin.LooterId}»; трофей {trophy?.Ids.GetValueOrDefault("item_type") ?? "-"} {(itemOk ? "есть" : "нет у порта")}");
            }
        }
        output.WriteLine($"боёв у руин {battles}, сверено {checkedBattles}: грабитель сошёлся {same}; трофей {itemsSame} из {items}; " +
                         $"победа с павшим лидером {fallenLeader}");
        Assert.True(checkedBattles >= 40, $"сверено {checkedBattles}");
        Assert.Equal(checkedBattles, same);
        Assert.Equal(items, itemsSame);
        Assert.True(fallenLeader >= 1, "нет боя с павшим лидером");
    }

    private static GameSession Session(string sg, GameContent content, GameBook book)
    {
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("", "", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        return new GameSession(new GameState(info, looks), content.MapArt, book);
    }

    /// <summary>Журналы корпуса из манифестов (без неполных и старых прогонов).</summary>
    internal static IEnumerable<(string Dir, string File)> Journals(string root)
    {
        foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            string manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.TryGetProperty("file", out var f) && f.GetString() is { } file && File.Exists(Path.Combine(dir, file))
                    && !file.Contains("_failed", StringComparison.Ordinal) && !file.Contains("_partial", StringComparison.Ordinal)
                    && !file.Contains("_old", StringComparison.Ordinal)
                    // Старые манифесты пишут сохранения объектами — к строкам save их не привязать (BoundSaves).
                    && row.TryGetProperty("saves", out var saves) && saves.ValueKind == JsonValueKind.Array
                    && saves.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
                    yield return (dir, file);
        }
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
