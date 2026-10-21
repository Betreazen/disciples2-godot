using System.Text.Json;

namespace D2.Rules.Tests;

/// <summary>Строки журнала с одной из меток <c>wanted</c> (номер первой подходящей — как <c>Array.FindIndex</c> по строке) и их
/// <c>seq</c>: один проход по файлу на журнал, а не на каждую его сессию. Харнессы, которые шли по <see cref="AiCorpus.Sessions"/>
/// и для каждой сессии читали и разбирали весь журнал (<c>AiTacticEvalHarnessTests</c>, <c>AiTacticHarnessTests</c>), на корпусе
/// 20.10 (99 сессий, у <c>20261014-replay</c> — 21 в двух журналах) разбирали одни и те же строки десятки раз. Помнится один журнал —
/// последний (сессии одного журнала в списке идут подряд).</summary>
internal sealed class JournalLines(string[] wanted)
{
    private string? _journal;
    private List<(int Kind, long Seq, string Line)> _lines = [];

    /// <summary>Строки сессии <paramref name="session"/> в порядке файла.</summary>
    public IEnumerable<(int Kind, long Seq, string Line)> Of(AiSession session)
    {
        if (_journal != session.Journal)
        {
            var lines = new List<(int, long, string)>();
            foreach (var l in File.ReadLines(session.Journal))
            {
                int k = Array.FindIndex(wanted, w => l.Contains(w, StringComparison.Ordinal));
                if (k < 0) continue;
                using var doc = JsonDocument.Parse(l);
                lines.Add((k, doc.RootElement.GetProperty("seq").GetInt64(), l));
            }
            (_journal, _lines) = (session.Journal, lines);
        }
        return _lines.Where(l => session.Contains(l.Seq));
    }
}
