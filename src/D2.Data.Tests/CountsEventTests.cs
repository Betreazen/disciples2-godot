using D2.Data.Corpus;

namespace D2.Data.Tests;

/// <summary>Строка <c>counts</c> зонда: до схемы 7.4 — только общий <c>keys_dropped</c>, с 7.4 — ещё <c>keys_dropped_by</c> (по
/// видам) и <c>keys_resets</c> (сбросы ключей при входе в партию, <c>probe.cpp</c>). Старые строки читаются как прежде.</summary>
public class CountsEventTests
{
    [Fact]
    public void Schema73Line_KeepsCallsAndTotal_NoKinds()
    {
        var e = (CountsEvent)CorpusReader.ParseLine(
            """{"seq":5,"tid":1,"t":0,"ev":"counts","keys_dropped":7,"input_pump":3,"calls":{"move_cost":10,"equip_check":2}}""");
        Assert.Equal(10, e.Calls["move_cost"]);
        Assert.Equal(7, e.KeysDropped);
        Assert.Null(e.KeysDroppedBy);
        Assert.Equal(0, e.KeysResets);
    }

    [Fact]
    public void Schema74Line_ReadsDroppedByKindAndResets()
    {
        var e = (CountsEvent)CorpusReader.ParseLine(
            """{"seq":5,"tid":1,"t":0,"ev":"counts","keys_dropped":9,"keys_dropped_by":{"move_cost":9},"keys_resets":2,"input_pump":0,"calls":{"move_cost":10}}""");
        Assert.Equal(9, e.KeysDropped);
        Assert.Equal(new Dictionary<string, long> { ["move_cost"] = 9 }, e.KeysDroppedBy);
        Assert.Equal(2, e.KeysResets);
    }

    [Fact]
    public void OldestLine_WithoutKeysDropped_StillParses()
    {
        var e = (CountsEvent)CorpusReader.ParseLine("""{"seq":1,"tid":1,"t":0,"ev":"counts","calls":{}}""");
        Assert.Empty(e.Calls);
        Assert.Equal(0, e.KeysDropped);
    }
}

/// <summary>Журнал 7.3 <c>20261016e-av</c> — тот, где общий потолок 200 000 исчерпали ключи <c>move_cost</c>: последняя строка
/// <c>counts</c> читается со старым общим счётом и без полей 7.4.</summary>
[Trait("Category", "Corpus")]
public class CountsEventCorpusTests
{
    [SkippableFact]
    public void Av73Journal_LastCounts_HasTotalDroppedOnly()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20261016e-av", "passive-001.jsonl");
        Skip.IfNot(File.Exists(path), "нет журнала corpus/20261016e-av (корпус вне git)");

        var last = JournalCache.Read(path, "counts").OfType<CountsEvent>().Last();
        Assert.Equal(8182551L, last.KeysDropped);
        Assert.Null(last.KeysDroppedBy);
        Assert.Equal(0, last.KeysResets);
    }
}
