using D2.Data.Corpus;
using D2.Rules.Rng;

/// <summary>С2 со схемы 6.2: состояние генератора перед боем берётся из последней строки <c>rng_seed</c>
/// после предыдущего боя (C2HarnessTests.SeedBefore / StartFromSeed), а не подбором Fit. Живых журналов 6.2
/// нет — строка синтетическая: таблица и индекс из генератора, броски из его клона.</summary>
public class RngSeedChainTests
{
    private static RngSeedEvent SeedLine(long seq, long n, EngineRng state)
    {
        string hex = string.Concat(state.TableSnapshot().Select(w => w.ToString("x4")));
        var path = Path.GetTempFileName();
        File.WriteAllText(path, $$"""{"seq":{{seq}},"tid":1,"t":9,"ev":"rng_seed","n":{{n}},"arg":{{state.Index + 250}},"index_before":3,"index_after":{{state.Index}},"via":"reseed","cause":"new_game_time","ret_addr":"0x429dcd","wide":0,"table":"{{hex}}"}""" + "\n");
        try { return Assert.IsType<RngSeedEvent>(CorpusReader.Read(path).Single()); }
        finally { File.Delete(path); }
    }

    [Fact] public void StartFromSeed_ReproducesBattleDrawsAfterGap()
    {
        // Пересев после мутаций таблицы (загрузка сохранения в середине сеанса).
        var engine = new EngineRng(200);
        engine.Advance(70_000);
        var seed = SeedLine(seq: 500, n: 41_000, engine);

        // Между пересевом и боем 900 вызовов (n) и ещё 37 шагов отбраковки.
        const int gap = 900;
        var game = engine.Clone();
        for (int k = 0; k < gap; k++) game.Next(k % 3 == 0 ? 7 : 65_000);
        var all = new List<RngEvent>();
        for (int k = 0; k < 25; k++)
        {
            int max = 2 + k % 5;
            all.Add(new RngEvent(Seq: 900 + k, Tid: 1, N: seed.N + 1 + gap + k, Max: max, R: game.Next(max), Std: false));
        }

        var before = C2HarnessTests.StartFromSeed(seed, all);
        Assert.NotNull(before);
        Assert.True(before!.Replay(all.Select(e => ((int)e.Max, (int)e.R)).ToList()));
    }

    [Fact] public void SeedBefore_TakesLastSeedAfterPreviousBattleOnly()
    {
        var state = new EngineRng();
        var seeds = new[] { SeedLine(10, 0, state), SeedLine(50, 5, state), SeedLine(120, 9, state) };
        Assert.Equal(50, C2HarnessTests.SeedBefore(seeds, afterSeq: 20, beforeSeq: 100)!.Seq);
        Assert.Equal(10, C2HarnessTests.SeedBefore(seeds, afterSeq: 0, beforeSeq: 40)!.Seq);
        Assert.Null(C2HarnessTests.SeedBefore(seeds, afterSeq: 60, beforeSeq: 100));
    }
}
