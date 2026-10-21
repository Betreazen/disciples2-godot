using D2.Data.Corpus;

namespace D2.Rules.Tests;

/// <summary>
/// События карты (<c>build_status</c>, <c>hire_list</c>, <c>site</c>, <c>xp</c>) по ВСЕМУ корпусу, каждое — со снимком карты
/// на свой момент. Прежде харнессы экономики и сайтов брали один журнал — первый по имени <c>corpus/**/map-*.jsonl</c>
/// (<c>20260907-c4/map-001.jsonl</c>, где этих событий нет вовсе) — и пропускались, хотя партии зонда 6.5+ несут их сотнями.
/// <list type="bullet">
/// <item>Журнал зонда 6.5+ — по сессиям <see cref="AiCorpus"/> (загрузка карты = поток <c>tid</c>); снимок — последнее
/// сохранение сессии с копией раньше события (<see cref="AiSession.SaveBefore"/>). Нет такого — событие пропускается
/// с причиной, а не сверяется с чужой картой.</item>
/// <item>Журнал без сессий (<c>map-*.jsonl</c> Ф6, до зонда 6.5) — снимок один, стартовая карта: первый <c>.sg</c> каталога
/// (<c>map-start.sg</c>), как раньше.</item>
/// </list>
/// </summary>
public static class MapJournalEvents
{
    /// <param name="Journal">Файл журнала.</param>
    /// <param name="From">Срез снимка на оси <c>seq</c>: всё до него включительно в снимок вошло (строка <c>save</c>; у
    /// стартовой карты — −1).</param>
    /// <param name="DayFrom">Начало окна, в котором ищется начало дня: первая строка сессии (у журнала без сессий — −1).</param>
    public sealed record At<T>(string Source, string Journal, long From, long DayFrom, T Event, string? Sg, string? Why)
        where T : CorpusEvent;

    public static IEnumerable<At<T>> Of<T>(string ev) where T : CorpusEvent
    {
        string[] wanted = [ev];
        var bySession = AiCorpus.Sessions.Select(s => s.Journal).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (s, events) in AiCorpus.Split(wanted))
            foreach (var e in events.OfType<T>())
            {
                var save = s.SaveBefore(e.Seq);
                yield return new At<T>(s.Name, s.Journal, save?.Seq ?? -1, s.First, e, save?.File, save is null ? "в сессии нет сохранения до события" : null);
            }
        foreach (var journal in CorpusFiles.Journals().Where(j => !bySession.Contains(j)))
        {
            var events = JournalCache.Read(journal, wanted).OfType<T>().ToList();
            if (events.Count == 0) continue;
            var dir = Path.GetDirectoryName(journal)!;
            var start = Directory.GetFiles(dir, "*.sg").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            string source = $"{Path.GetFileName(dir)}/{Path.GetFileName(journal)}";
            foreach (var e in events)
                yield return new At<T>(source, journal, -1, -1, e, start, start is null ? "рядом с журналом нет .sg" : null);
        }
    }
}
