using D2.Data.Diagnostics;

namespace D2.Data.Corpus;

/// <summary>
/// Загрузки карты в журнале зонда — по смене логического потока. Первая строка журнала (<c>start</c>) пишется потоком
/// интерфейса; строка другого потока, не равного текущему потоку партии, открывает новую загрузку (у каждой загрузки сценария
/// поток партии новый — <c>docs/synthetic-maps/journal_maps.py</c> в disciples-modding).
/// <para><b>Звук и анимации загрузку не открывают.</b> С схемы 7.3 конец звука (<c>snd_end</c>) пишет поток обратных вызовов
/// Miles — третий поток, не интерфейс и не партия (<c>20261016e-av</c>: 942 строки <c>snd_end</c> потока 41140 вперемешку с
/// ходами партии). Без этого правила каждая такая строка резала загрузку надвое: 595 «загрузок» вместо 6, одна копия сохранения
/// привязывалась к двум, сессии ИИ теряли карту.</para>
/// </summary>
public sealed class JournalLoads
{
    private long? _ui;
    private long? _game;

    /// <summary>Строка звука, музыки или анимации (вид из <see cref="AvJournal.Events"/>) — по виду строки JSONL.</summary>
    public static bool IsAvLine(string line) => CorpusReader.EvOf(line) is { } ev && AvJournal.Events.Contains(ev);

    /// <summary>Очередная строка журнала по порядку файла: <c>true</c> — она открывает новую загрузку карты. Первая строка
    /// задаёт поток интерфейса и загрузку не открывает; строка звука и анимаций (<paramref name="av"/>) — тоже.</summary>
    public bool Opens(long tid, bool av) => Other(tid) && !av && Take(tid);

    /// <summary>То же для разобранного события.</summary>
    public bool Opens(CorpusEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Opens(e.Tid, e is AvProbeEvent);
    }

    /// <summary>То же для сырой строки с уже прочитанным потоком; вид строки читается, только когда поток сменился.</summary>
    public bool Opens(long tid, string line) => Other(tid) && !IsAvLine(line) && Take(tid);

    /// <summary>Поток не интерфейса и не текущей партии. Первый поток журнала становится потоком интерфейса.</summary>
    private bool Other(long tid)
    {
        if (_ui is not null) return tid != _ui && tid != _game;
        _ui = tid;
        return false;
    }

    private bool Take(long tid)
    {
        _game = tid;
        return true;
    }
}
