using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Найм лидера ИИ из карты (список 0, <c>0x44a7c2</c>, <c>docs/ai-economy.md</c>): первый вызов выбора роли
/// (<c>ai_leader_role</c>, <c>fn = role</c>) и подприказа в каждом проходе стратегии — счётчики и допуски порта
/// (<see cref="AiEconomy.LeaderCounts"/>, <see cref="AiEconomy.LeaderFlags"/>) против аргументов журнала. Состояние —
/// <see cref="AiLiveDay"/> на начало хода игрока (стратегия идёт до тактики, отряды ещё не ходили).
/// </summary>
public abstract class AiLeaderHireHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <remarks>Измерено 23.09.2026: допуски сошлись везде (human 232 + 59 + 177, legions 272 + 48 + 230); счётчики роли
    /// 228 из 232 и 270 из 272 (найм из журнала — при любой казне порта), подприказа бойца 177/177 и 225/230, вора 58/59 и 48/48 (у зонда имена функций
    /// подприказа переставлены: <c>fighter</c> — это <c>0x44b6f3</c>, подприказ вора). Остаток — отряды, которых
    /// воспроизведение дня не знает (гибель в бою, наймы вне окна).</remarks>
    [SkippableTheory]
    // 25.09: нанятый отряд в живом дне получает id движка (AiLiveDay.Hired: номера погибших движок не переиспользует) — строки журнала
    // о нём больше не теряются. Подкатегория вора human 58 → 59, бойца legions 225 → 230.
    [CorpusRow("20260919-human", 228, 177, 59)]
    [CorpusRow("20260920-legions", 270, 230, 48)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — роль 125/125, подприказ вора 98 из 99 (PL0002 день 14),
    // бойца 54/54.
    [CorpusRow("20261019c-cov", 125, 98, 54)]
    public void RoleInputs_FromMap(string corpus, int wantRole, int wantFighterSub, int wantThiefSub)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var saves = Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal)
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Last());
        var events = JournalCache.Read(journal, ["ai_budget_spend", "ai_turn", "ai_leader_role", "hire_leader", "stack_move", "ai_order", "battle_end"])
                                 .OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);
        AiLiveDay? live = null;
        var tally = new SortedDictionary<string, int>();
        int shown = 0;
        (string Player, long Pass)? seen = null;
        var seenFn = new HashSet<string>();
        foreach (var e in events)
            switch (e)
            {
                case AiTurnEvent { Phase: "begin" } t:
                    if (live is null || live.Day != t.Turn)
                        live = saves.TryGetValue((int)t.Turn, out var sg) ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends) : null;
                    if (t.Player is not null && live is not null) live.Begin(t.Player);
                    break;
                case AiLeaderRoleEvent r when live is not null && r.Player is not null:
                {
                    if (seen != (r.Player, r.Pass)) { seen = (r.Player, r.Pass); seenFn.Clear(); }
                    if (!seenFn.Add(r.Fn)) break;   // только первый вызов каждого вида в проходе: дальше счётчики уже «нанятые»
                    var session = live.Session();
                    if (!session.Map.Players.ContainsKey(r.Player)) break;
                    var c = AiEconomy.LeaderCounts(session, r.Player);
                    var f = AiEconomy.LeaderFlags(session, r.Player, c);
                    var (b, i) = r.Fn switch
                    {
                        "role" => (new[] { true, f.Rod, f.Thief }, new[] { c.Fighters, c.Rods, c.Thieves }),
                        "fighter" => (new[] { f.Assist, f.ThiefNormal, f.Steal }, new[] { c.ThiefAssist, c.ThiefNormal, c.ThiefSteal }),
                        _ => (new[] { true, f.Assist, f.DefendCity }, new[] { c.FighterNormal, c.FighterAssist, c.FighterDefend }),
                    };
                    bool iOk = i.Select(x => (long)x).SequenceEqual(r.I);
                    bool bOk = b.Select(x => x ? 1L : 0L).SequenceEqual(r.B.Select(x => x != 0 ? 1L : 0L));
                    Count(tally, $"{r.Fn}: счётчики {(iOk ? "да" : "нет")}");
                    for (int k = 0; k < 3; k++)
                        if ((b[k] ? 1 : 0) != (r.B[k] != 0 ? 1 : 0)) Count(tally, $"{r.Fn}: допуск {k} нет (журнал {r.B[k]})");
                    if (bOk) Count(tally, $"{r.Fn}: допуски да");
                    if ((!iOk || !bOk) && shown++ < 20)
                        output.WriteLine($"{r.Player} день {live.Day} проход {r.Pass} {r.Fn}: журнал b [{string.Join(",", r.B)}] i [{string.Join(",", r.I)}]; " +
                                         $"порт b [{string.Join(",", b.Select(x => x ? 1 : 0))}] i [{string.Join(",", i)}]");
                    break;
                }
                case StackMoveEvent m when live is not null:
                    live.Move(m.Stack, (int)m.X, (int)m.Y, (int)m.MoveLeft);
                    break;
                case AiOrderEvent o when live is not null:
                    live.Order(o);
                    break;
                case HireLeaderEvent hl when live is not null:
                    live.HireLeader(hl);
                    break;
                case BattleEndEvent be when live is not null:
                    live.Battle(be);
                    break;
            }
        foreach (var (k, n) in tally) output.WriteLine($"{k}: {n}");
        Assert.DoesNotContain(tally.Keys, k => k.Contains("допуск", StringComparison.Ordinal) && k.Contains("нет", StringComparison.Ordinal));
        Assert.Equal(wantRole, tally.GetValueOrDefault("role: счётчики да"));
        Assert.Equal(wantFighterSub, tally.GetValueOrDefault("thief: счётчики да"));
        Assert.Equal(wantThiefSub, tally.GetValueOrDefault("fighter: счётчики да"));
    }

    private static void Count(IDictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
}

/// <summary>Партии харнесса — отдельными классами, чтобы шли параллельно (<see cref="CorpusRowAttribute"/>).</summary>
[OnlyCorpus("20260919-human")]
[Trait("Category", "Corpus")]
public sealed class AiLeaderHireHarnessTests_Human(ITestOutputHelper output) : AiLeaderHireHarnessTests(output);

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class AiLeaderHireHarnessTests_Legions(ITestOutputHelper output) : AiLeaderHireHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class AiLeaderHireHarnessTests_Cov(ITestOutputHelper output) : AiLeaderHireHarnessTests(output);
