using D2.Data.Corpus;

namespace D2.Data.Tests;

/// <summary>Загрузки карты журнала по смене потока (<see cref="JournalLoads"/>): строки звука и анимаций другого потока (обратные
/// вызовы Miles пишут <c>snd_end</c>, <c>20261016e-av</c>) загрузку не открывают.</summary>
public class JournalLoadsTests
{
    private static string Line(long seq, long tid, string ev) => $"{{\"seq\":{seq},\"tid\":{tid},\"t\":0,\"ev\":\"{ev}\"}}";

    [Fact]
    public void SoundThreadLines_DoNotOpenLoads_GameThreadChangeDoes()
    {
        var loads = new JournalLoads();
        var opened = new List<long>();
        (long Seq, long Tid, string Ev)[] lines =
        [
            (1, 616, "start"),        // поток интерфейса
            (2, 35468, "rng_seed"),   // первая загрузка
            (3, 41140, "snd_end"),    // поток Miles — не загрузка
            (4, 35468, "bank"),
            (5, 616, "snd"),          // интерфейс
            (6, 41140, "snd_end"),
            (7, 38348, "rng_seed"),   // вторая загрузка
            (8, 41140, "snd_end"),
            (9, 38348, "stack_move"),
        ];
        foreach (var (seq, tid, ev) in lines)
            if (loads.Opens(tid, Line(seq, tid, ev))) opened.Add(seq);
        Assert.Equal([2L, 7L], opened);
    }

    [Fact]
    public void ParsedEvents_FollowTheSameRule()
    {
        var loads = new JournalLoads();
        Assert.False(loads.Opens(new UnknownEvent(1, 616, "start", "")));
        Assert.True(loads.Opens(new UnknownEvent(2, 35468, "rng_seed", "")));
        var av = (AvProbeEvent)CorpusReader.ParseLine("{\"seq\":3,\"tid\":41140,\"t\":0,\"ev\":\"snd_end\",\"name\":\"X\"}");
        Assert.False(loads.Opens(av));
        Assert.False(loads.Opens(new UnknownEvent(4, 35468, "bank", "")));
    }
}
