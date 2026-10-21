using System.Text;
using System.Text.RegularExpressions;
using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Уровень создания отряда (<c>CREAT_LVL</c>, <see cref="D2.Rules.Map.MapStack.CreationLevel"/>) по сохранениям корпуса: движок меняет
/// его в игре только кодами опыта (<see cref="GameSession.CheatExperience"/>, <c>0x4312ba</c>). Харнесс проходит все пары соседних
/// сохранений одной карты: любое изменение уровня — только 1 → 2 и сразу у всех отрядов одного игрока с уровнем 1; на сохранении до
/// изменения порт применяет код и сверяет с сохранением после — уровни отрядов игрока и опыт юнитов, которых код касается.
/// </summary>
[Trait("Category", "Corpus")]
public partial class CreationLevelHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules"))));

    [GeneratedRegex(@"^(?<map>.+)-t\d+-(?<n>\d+)\.sg$")]
    private static partial Regex SaveName();

    [SkippableFact]
    public void CreationLevelChanges_AreTheExperienceCode()
    {
        string corpus = GameSaveTests.CorpusDir();
        Skip.IfNot(Directory.Exists(corpus), "нет корпуса");
        int pairs = 0, codes = 0, unitsChecked = 0;
        var bad = new List<string>();
        foreach (string run in Directory.GetDirectories(corpus).Where(d => !Path.GetFileName(d).StartsWith('_')))
        {
            var saves = Directory.GetFiles(run, "*.sg")
                .Select(p => (Path: p, Match: SaveName().Match(Path.GetFileName(p))))
                .Where(s => s.Match.Success)
                .GroupBy(s => s.Match.Groups["map"].Value);
            foreach (var map in saves)
            {
                var ordered = map.OrderBy(s => int.Parse(s.Match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))
                                 .Select(s => s.Path).ToList();
                var before = Levels(ordered[0]);
                for (int i = 1; i < ordered.Count; i++)
                {
                    var after = Levels(ordered[i]);
                    pairs++;
                    var changed = after.Where(s => before.TryGetValue(s.Key, out int was) && was != s.Value).ToList();
                    if (changed.Count > 0)
                    {
                        string where = $"{Path.GetFileName(ordered[i - 1])} → {Path.GetFileName(ordered[i])}";
                        foreach (var (id, level) in changed)
                            if ((before[id], level) != (1, 2)) bad.Add($"{where}: {id} {before[id]} → {level}");
                        codes++;
                        unitsChecked += Replay(ordered[i - 1], ordered[i], [.. changed.Select(c => c.Key)], where, bad);
                    }
                    before = after;
                }
            }
        }
        output.WriteLine($"пар сохранений {pairs}, изменений уровня создания {codes}, юнитов сверено {unitsChecked}, расхождений {bad.Count}");
        foreach (string line in bad.Take(20)) output.WriteLine("  " + line);
        Assert.Empty(bad);
        Assert.True(codes >= 2, $"изменений уровня создания {codes} — ждали два (20260919-human, ходы 26 и 57)");
        Assert.True(unitsChecked > 0, "ни один юнит не сверен по опыту — код не проверен");
    }

    /// <summary>Сохранение «до» → код опыта игрока изменившихся отрядов → сверка с «после»: уровень создания всех отрядов игрока и опыт
    /// юнитов, которых код коснулся и которые с тех пор не выросли (опыт «после» не меньше данного кодом). Возвращает число юнитов, чей
    /// опыт совпал точно.</summary>
    private static int Replay(string beforePath, string afterPath, IReadOnlyList<string> changed, string where, List<string> bad)
    {
        var looks = MapLooksLoader.Load(beforePath);
        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), Shared.Value.Content.MapArt, Shared.Value.Book);
        var want = MapLooksLoader.Load(afterPath).Map;
        var owners = changed.Select(id => session.Map.Stacks.TryGetValue(id, out var s) ? s.Owner : "")
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (owners.Count != 1 || owners[0].Length == 0) { bad.Add($"{where}: изменились отряды {owners.Count} игроков — код опыта меняет одного"); return 0; }
        string player = owners[0];
        var was = session.Map;
        session.CheatExperience(player, fullLevel: false);
        int units = 0;
        foreach (var stack in session.Map.Stacks.Values.Where(s => s.Owner.Equals(player, StringComparison.OrdinalIgnoreCase)))
        {
            if (!want.Stacks.TryGetValue(stack.Id, out var after)) continue;
            if (after.CreationLevel != stack.CreationLevel)
                bad.Add($"{where}: {stack.Id} уровень создания у порта {stack.CreationLevel}, у движка {after.CreationLevel}");
            foreach (string id in stack.UnitIds)
            {
                if (!session.Map.Units.TryGetValue(id, out var mine) || !was.Units.TryGetValue(id, out var old)) continue;
                if (mine.Xp == old.Xp || !want.Units.TryGetValue(id, out var theirs)) continue;
                if (theirs.TypeId != mine.TypeId || theirs.Level != mine.Level) continue;   // с тех пор вырос — сверять нечего
                // После кода опыт только растёт (бои), поэтому «после» меньше данного кодом — расхождение.
                if (theirs.Xp < mine.Xp) bad.Add($"{where}: {id} опыт у порта {mine.Xp}, у движка {theirs.Xp}");
                else if (theirs.Xp == mine.Xp) units++;
            }
        }
        return units;
    }

    /// <summary>Уровни создания отрядов сохранения без разбора всего файла: поле <c>CREAT_LVL</c> и ближайший перед ним <c>OBJ_ID</c>.</summary>
    private static Dictionary<string, int> Levels(string path)
    {
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const string field = "CREAT_LVL", obj = "OBJ_ID";
        for (int at = text.IndexOf(field, StringComparison.Ordinal); at >= 0; at = text.IndexOf(field, at + field.Length, StringComparison.Ordinal))
        {
            int o = text.LastIndexOf(obj, at, StringComparison.Ordinal);
            if (o < 0 || at + field.Length + 4 > text.Length || o + obj.Length + 4 + 10 > text.Length) continue;
            string id = text.Substring(o + obj.Length + 4, 10);
            int v = text[at + 9] | text[at + 10] << 8 | text[at + 11] << 16 | text[at + 12] << 24;
            result[id] = v;
        }
        return result;
    }
}
