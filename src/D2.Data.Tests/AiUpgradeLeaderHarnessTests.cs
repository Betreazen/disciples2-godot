using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Навык лидера ИИ (список 1, <c>0x44ef81</c>, <c>docs/ai-economy.md</c> §6): на начале хода игрока ИИ —
/// предложение порта (<see cref="GameSession.SkillOffer"/>) против <c>skill_offer</c> журнала и выбор стратегии порта
/// (<see cref="AiLiveDay.Begin"/> → <see cref="GameSession.BeginAiTurn"/>) против исполненных действий
/// <c>ai_exec</c> списка 1 того же хода. Состояние — <see cref="AiLiveDay"/> с сохранения дня.
/// <para>Измерено 23.09.2026: human — предложений 3/3, выбор 5 ходов из 5; legions — 5/5 и 5/5. probe65 не берётся:
/// в его журнале две карты с общими номерами дней, сохранение по дню не сопоставить.</para>
/// </summary>
public abstract class AiUpgradeLeaderHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string UpgradeExec = "0x44f3d2";

    [SkippableTheory]
    [CorpusRow("20260919-human", 5)]
    [CorpusRow("20260920-legions", 5)]
    // 30.09 (§32): probe72 (ai-q09, одна карта) — предложений 3 из 3, выбор 3 хода из 3 (UM4002 у S143UN0246 и S143UN0248, UM4003 у
    // S143UN0247: argmax PRIORITY без врождённых). Навык лидера в живом дне своей силе B не мешает — она сходится во всех строках.
    [CorpusRow("20260930-probe72", 3)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — 12 ходов с повышением лидера ИИ, всё сошлось.
    [CorpusRow("20261019c-cov", 12)]
    public void OfferAndPick_MatchJournal(string corpus, int wantTurns)
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
        var events = JournalCache.Read(journal, ["ai_budget_spend", "ai_turn", "ai_exec", "skill_offer", "hire_leader", "stack_move", "ai_order", "battle_end", "xp"])
                                 .OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);
        AiLiveDay? live = null;
        string? player = null;
        var offers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // лидер → предложение порта
        var picksGot = new List<string>();
        var picksWant = new List<string>();
        int offerSame = 0, offerDiffer = 0, offerUnknown = 0, turnsSame = 0, turnsDiffer = 0;
        void Close()
        {
            if (player is null || picksGot.Count + picksWant.Count == 0) return;
            if (picksGot.Order().SequenceEqual(picksWant.Order())) turnsSame++;
            else
            {
                turnsDiffer++;
                output.WriteLine($"{player} день {live?.Day}: журнал [{string.Join(",", picksWant)}], порт [{string.Join(",", picksGot)}]");
            }
        }
        var hiresOfTurn = AiSpellHarnessTests.HiresOfTurns(events);
        foreach (var e in events)
            switch (e)
            {
                case AiTurnEvent { Phase: "begin", Cause: "ai" } t when t.Player is not null:
                {
                    Close();
                    player = t.Player;
                    picksGot.Clear();
                    picksWant.Clear();
                    offers.Clear();
                    if (live is null || live.Day != t.Turn)
                        live = saves.TryGetValue((int)t.Turn, out var sg) ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends) : null;
                    if (live is null) break;
                    var before = live.Session();
                    if (!before.Map.Players.ContainsKey(t.Player)) break;
                    foreach (var s in before.Map.Stacks.Values.Where(s => s.UpgradeCount > 0 && s.Owner.Equals(t.Player, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Журнал пишет список 0x5d8b84 до фильтра врождённых (фильтр 0x5e84c2 — отдельный вызов, apply = 0):
                        // сверяется список без фильтра, а отсечённое фильтром — подмножество его.
                        var lead = before.Map.Units[s.LeaderId];
                        var all = D2.Rules.Units.LeaderUpgrades.Available(book.LeaderUpgrades, before.Map.Players[t.Player].LordId, lead.Level,
                                                                          lead.Modifiers.Where(id => book.Rules.LeaderUpgradeModifiers?.Contains(id) ?? false));
                        var filtered = before.SkillOffer(s.Id);
                        Assert.All(filtered, u => Assert.Contains(u, all));
                        offers[s.LeaderId] = [.. all.Select(u => u.ModifId.ToUpperInvariant())];
                    }
                    live.Begin(t.Player, hires: hiresOfTurn.GetValueOrDefault(t.Seq));
                    var after = live.Session().Map;
                    foreach (var s in before.Map.Stacks.Values.Where(s => s.Owner.Equals(t.Player, StringComparison.OrdinalIgnoreCase)))
                        if (after.Units.TryGetValue(s.LeaderId, out var lead))
                            picksGot.AddRange(lead.Modifiers.Skip(before.Map.Units[s.LeaderId].Modifiers.Count)
                                                  .Where(id => book.Rules.LeaderUpgradeModifiers?.Contains(id) ?? false)
                                                  .Select(id => $"{s.Id.ToUpperInvariant()}:{id.ToUpperInvariant()}"));
                    break;
                }
                case SkillOfferEvent o when player is not null && o.Player.Equals(player, StringComparison.OrdinalIgnoreCase):
                {
                    if (!offers.Remove(o.Leader, out var got)) { offerUnknown++; break; }   // первое предложение хода
                    var want = o.Ids.Select(x => x.ToUpperInvariant()).ToList();
                    if (got.SequenceEqual(want)) offerSame++;
                    else
                    {
                        offerDiffer++;
                        output.WriteLine($"предложение {o.Leader} ур.{o.Level} день {live?.Day}: журнал [{string.Join(",", want)}], порт [{string.Join(",", got)}]");
                    }
                    break;
                }
                case AiExecEvent x when x.Fn == UpgradeExec && x.Ret && x.Action is { } a:
                    picksWant.Add($"{Field(a, "id4")}:{Field(a, "id8")}");
                    break;
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
                case XpEvent xp when live is not null:
                    live.Xp(xp);
                    break;
            }
        Close();
        output.WriteLine($"предложение сошлось {offerSame}, разошлось {offerDiffer}, без пары {offerUnknown}; выбор за ход сошёлся {turnsSame}, разошёлся {turnsDiffer}");
        Assert.Equal(0, offerDiffer);
        Assert.Equal(0, turnsDiffer);
        Assert.Equal(wantTurns, turnsSame);
    }

    private static string Field(AiObject a, string key) =>
        a.Fields.TryGetValue(key, out var v) ? (v.GetString() ?? "").ToUpperInvariant() : "";
}

/// <summary>Партии харнесса — отдельными классами, чтобы шли параллельно (<see cref="CorpusRowAttribute"/>).</summary>
[OnlyCorpus("20260919-human")]
[Trait("Category", "Corpus")]
public sealed class AiUpgradeLeaderHarnessTests_Human(ITestOutputHelper output) : AiUpgradeLeaderHarnessTests(output);

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class AiUpgradeLeaderHarnessTests_Legions(ITestOutputHelper output) : AiUpgradeLeaderHarnessTests(output);

[OnlyCorpus("20260930-probe72")]
[Trait("Category", "Corpus")]
public sealed class AiUpgradeLeaderHarnessTests_Probe72(ITestOutputHelper output) : AiUpgradeLeaderHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class AiUpgradeLeaderHarnessTests_Cov(ITestOutputHelper output) : AiUpgradeLeaderHarnessTests(output);
