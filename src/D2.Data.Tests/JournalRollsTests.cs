using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>
/// Схема 7.5: броски решений игры (<c>ai_rng</c> с новыми местами, <c>outer</c>, <c>origin</c>, <c>player</c>) разбираются, а
/// <see cref="JournalRolls"/> отдаёт их порту по порядку и ловит расхождения границ. Журналы синтетические — по формату
/// <c>tools/d2probe/mss32/src/probeairng.cpp</c>; живых журналов 7.5 ещё нет.
/// </summary>
public class JournalRollsTests
{
    internal static string Start(string schema) =>
        $$"""{"seq":0,"tid":1,"t":0,"ev":"start","exe_size":4474880,"version":"Gog 3.01","probe":"test","schema":"{{schema}}","autobattle":false,"hooks":[]}""";

    internal static string Roll(long seq, string site, long max, long r, string? origin = null, string? outer = null) =>
        $$"""{"seq":{{seq}},"tid":7,"t":1,"ev":"ai_rng","site":"{{site}}","kind":"r250","n":{{seq + 100}},"max":{{max}},"r":{{r}},"raw":null,"battle":false,"ret_addr":"0x472350","player":"S143PL0002","outer":{{outer ?? "null"}},"origin":{{(origin is null ? "null" : $"\"{origin}\"")}}}""";

    internal static List<CorpusEvent> Journal(params string[] lines)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        try { return [.. CorpusReader.Read(path)]; }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parse_V75Fields_AndOldLineWithout()
    {
        var events = Journal(
            Start("7.5"),
            Roll(1, "shuffle_list", 3, 2, "mage", """["0x46aa83","0x46a8e2"]"""),
            """{"seq":2,"tid":7,"t":2,"ev":"ai_rng","site":"leader_pick","kind":"r250","n":40,"max":3,"r":1,"raw":null,"battle":false,"ret_addr":"0x44aee6"}""");

        var start = Assert.IsType<StartEvent>(events[0]);
        Assert.Equal("7.5", start.Schema);
        Assert.True(start.SchemaAtLeast(7, 5));
        Assert.False(start.SchemaAtLeast(7, 6));
        var shuffle = Assert.IsType<AiRngEvent>(events[1]);
        Assert.Equal(("shuffle_list", "mage", "S143PL0002"), (shuffle.Site, shuffle.Origin, shuffle.Player));
        Assert.Equal(["0x46aa83", "0x46a8e2"], shuffle.Outer);
        var old = Assert.IsType<AiRngEvent>(events[2]);
        Assert.Null(old.Outer);
        Assert.Null(old.Origin);
        Assert.Null(old.Player);
    }

    [Fact]
    public void StartWithoutSchema_IsNotV75()
    {
        var start = Assert.IsType<StartEvent>(Journal(
            """{"seq":0,"tid":1,"t":0,"ev":"start","exe_size":1,"version":"v","probe":"p","autobattle":false}""").Single());
        Assert.Null(start.Schema);
        Assert.False(start.SchemaAtLeast(6, 0));
        Assert.True((start with { Schema = "7.10" }).SchemaAtLeast(7, 5));
    }

    [Fact]
    public void Replay_GivesJournalValues_InOrder_AndIsExact()
    {
        var rolls = Journal(Roll(1, "merchant_item", 5, 4), Roll(2, "merchant_item", 4, 0)).OfType<AiRngEvent>().ToList();
        var replay = new JournalRolls(rolls);

        Assert.Equal(0, replay.Next(0));   // 0 — не бросок, как у 0x54851f
        Assert.Equal(4, replay.Next(5));
        Assert.Equal(0, replay.Next(4));
        Assert.True(replay.Exact);
    }

    [Fact]
    public void Replay_RecordsBoundsExtraAndUnused()
    {
        var rolls = Journal(Roll(1, "attack_spell_kind", 2, 1), Roll(2, "attack_spell_pick", 3, 2)).OfType<AiRngEvent>().ToList();

        var differ = new JournalRolls(rolls, new ScriptedRng([0, 0]));
        Assert.Equal(1, differ.Next(2));
        Assert.Equal(0, differ.Next(2));   // журнал 2 из 3 — за границей порта, бросок у запасного
        Assert.Equal(0, differ.Next(7));   // строк больше нет — запасной
        Assert.Equal([(3L, 2)], differ.BoundsDiffer);
        Assert.Equal(1, differ.Extra);
        Assert.Equal(1, differ.OutOfBound);
        Assert.False(differ.Exact);

        var fewer = new JournalRolls(rolls);
        fewer.Next(2);
        Assert.Equal(1, fewer.Unused);
        Assert.False(fewer.Exact);
        Assert.Throws<InvalidOperationException>(() => { fewer.Next(3); fewer.Next(3); });
    }

    [Fact]
    public void Between_TakesWindowSitesAndOrigin()
    {
        var all = Journal(Roll(5, "merchant_item", 3, 1), Roll(6, "shuffle_list", 2, 1, "gift_spell"), Roll(7, "shuffle_list", 2, 0, "mage"),
                          Roll(9, "merchant_item", 2, 1)).OfType<AiRngEvent>().ToList();

        Assert.Equal([5L, 7L], JournalRolls.Between(all, 4, 9, ["merchant_item", "shuffle_list"]).Where(r => r.Origin != "gift_spell").Select(r => r.Seq));
        Assert.Equal([7L], JournalRolls.Between(all, 0, 100, JournalRolls.ShuffleSites, "mage").Select(r => r.Seq));
        Assert.Empty(JournalRolls.Between(all, 9, 100, JournalRolls.AttackSpellSites));
    }
}

/// <summary>
/// Решения порта на синтетическом журнале 7.5 — состояние из сохранений корпуса: покупка у торговца (<c>0x46b6f8</c>) и выбор
/// заклинания перед атакой (<c>0x472142</c>) на бросках журнала дают ровно то, что выбрал бы движок с этими значениями R250.
/// </summary>
[Trait("Category", "Corpus")]
public class JournalRollsDecisionTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Торговец: строки <c>merchant_item</c> с <c>r = max − 1</c> — каждый раз последний экземпляр развёрнутого
    /// склада; граница каждой строки — длина оставшегося списка.</summary>
    [SkippableFact]
    public void Merchant_OnJournalRolls_BuysLastInstancesExactly()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt, book);
        var map = session.Map;
        var stack = map.Stacks.Values.First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman && !AiTactics.NeutralOwner(session, s.Owner)
                                                 && AiTactics.MerchantWants(session, s) >= 1);
        var merchant = map.Sites.First(s => s.Kind == SiteKind.Merchant && s.Stock.Sum(r => Math.Max(r.Count, 0)) >= 2);
        var pool = merchant.Stock.SelectMany(r => Enumerable.Repeat(r.Item, Math.Max(r.Count, 0))).ToList();
        int k = Math.Min(AiTactics.MerchantWants(session, stack), pool.Count);
        var lines = new List<string> { JournalRollsTests.Start("7.5") };
        for (int i = 0; i < k; i++) lines.Add(JournalRollsTests.Roll(10 + i, "merchant_item", pool.Count - i, pool.Count - i - 1));
        var rolls = JournalRollsTests.Journal([.. lines]).OfType<AiRngEvent>().ToList();
        var replay = new JournalRolls(JournalRolls.Between(rolls, 0, long.MaxValue, JournalRolls.MerchantSites));
        var turn = new AiTacticTurn(stack.Owner, book.AiWeights.Values.First(), [], map.Turn, AiTacticMemo.Empty) { Purse = new Bank(99999, 0, 0, 0, 0, 0) };

        var bought = AiTactics.BuyAtMerchant(session, turn, replay, stack.Id, merchant.Id);

        Assert.Equal(pool.AsEnumerable().Reverse().Take(k), bought);
        Assert.True(replay.Exact);
    }

    /// <summary>Перед атакой: границы бросков порта (категория, старшие) — <c>max</c> строк журнала; на последних значениях
    /// выбор тот же, что у записанной последовательности, и броски потрачены ровно. Граница не та — <see cref="JournalRolls.BoundsDiffer"/>.</summary>
    [SkippableFact]
    public void BeforeAttack_OnJournalRolls_PicksTheRolledSpell()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t44-077.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var game = new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks), SharedContent.Load(Data).MapArt, book);
        var attacker = game.Map.Stacks["S073KC0003"];
        var target = game.Map.Stacks["S073KC0075"];
        AiPrep? Pick(IRng rng) => AiSpells.BeforeAttack(game, attacker, target.Id, target.UnitIds, targetStackOutside: true, (80, 74), p: 0.55,
                                                       passed: true, spellBank: true, rng);
        var bounds = new Bounds();
        Assert.NotNull(Pick(bounds));
        Assert.Equal(2, bounds.Seen.Count);
        var rolls = JournalRollsTests.Journal(JournalRollsTests.Start("7.5"),
                                              JournalRollsTests.Roll(1, "attack_spell_kind", bounds.Seen[0], bounds.Seen[0] - 1),
                                              JournalRollsTests.Roll(2, "attack_spell_pick", bounds.Seen[1], bounds.Seen[1] - 1))
            .OfType<AiRngEvent>().ToList();
        var replay = new JournalRolls(rolls);

        var prep = Pick(replay);

        Assert.True(replay.Exact);
        Assert.Equal(Pick(new ScriptedRng([bounds.Seen[0] - 1, bounds.Seen[1] - 1]))!.Id, prep!.Id);
        var wrong = new JournalRolls([rolls[0], rolls[1] with { Max = bounds.Seen[1] + 1 }]);
        Pick(wrong);
        Assert.Equal([(bounds.Seen[1] + 1L, bounds.Seen[1])], wrong.BoundsDiffer);
    }

    private sealed class Bounds : IRng
    {
        public List<int> Seen { get; } = [];

        public int Next(int maxValue)
        {
            if (maxValue != 0) Seen.Add(maxValue);
            return 0;
        }
    }
}
