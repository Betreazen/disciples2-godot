using System.Security.Cryptography;
using D2.Rules.Rng;

/// <summary>Кеш подборов генератора (<see cref="EngineRng.Fit"/>) на диске: <c>.cache/rng-fit.tsv</c> в корне
/// репозитория (вне git). Подбор на длинном сеансе без перезапуска игры стоит до 250 × 1,2 млн шагов на бой — прогон
/// строгого С2 на <c>20260916-synth</c> шёл 1 ч 38 мин, — а ответ для того же журнала и боя не меняется.
/// <para>Ключ — sha256 журнала (первые 16 знаков), seq начала боя, число бросков и предел шагов подбора. Значение —
/// стартовый индекс и число шагов найденного состояния (<see cref="EngineRng.At"/>) либо «-» (не подобрано).
/// Найденное состояние вызывающий всё равно прогоняет через <see cref="EngineRng.Replay"/> — битая строка кеша
/// даст расхождение, а не молчаливую подмену. Переменная <c>D2_RNG_CACHE=0</c> выключает кеш.</para></summary>
public static class FitDiskCache
{
    private const int MaxSteps = 1_200_000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Hashes = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, (int Start, int Steps)?>? _known;

    private static bool Enabled => Environment.GetEnvironmentVariable("D2_RNG_CACHE") != "0";

    private static string? PathOf() =>
        CorpusFiles.Dir.Length == 0 ? null
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CorpusFiles.Dir).TrimEnd(Path.DirectorySeparatorChar))!,
                           ".cache", "rng-fit.tsv");

    public static EngineRng? Fit(string journal, long battleSeq, IReadOnlyList<(int max, int r)> draws)
    {
        if (!Enabled || PathOf() is not { } path) return EngineRng.Fit(draws, MaxSteps);
        string key = $"{HashOf(journal)}#{battleSeq}#{draws.Count}#{MaxSteps}";
        lock (Gate)
        {
            var known = _known ??= Load(path);
            if (known.TryGetValue(key, out var hit))
                return hit is { } h ? EngineRng.At(h.Start, h.Steps) : null;
        }
        var found = EngineRng.Fit(draws, MaxSteps);
        lock (Gate)
        {
            _known![key] = found is null ? null : (found.Start, found.Steps);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, found is null ? $"{key}\t-\n" : $"{key}\t{found.Start}\t{found.Steps}\n");
        }
        return found;
    }

    private static Dictionary<string, (int, int)?> Load(string path)
    {
        var known = new Dictionary<string, (int, int)?>(StringComparer.Ordinal);
        if (!File.Exists(path)) return known;
        foreach (var line in File.ReadLines(path))
        {
            var cells = line.Split('\t');
            if (cells.Length == 2 && cells[1] == "-") known[cells[0]] = null;
            else if (cells.Length == 3 && int.TryParse(cells[1], out int start) && int.TryParse(cells[2], out int steps))
                known[cells[0]] = (start, steps);
        }
        return known;
    }

    private static string HashOf(string journal)
    {
        if (Hashes.TryGetValue(journal, out var found)) return found;
        using var stream = File.OpenRead(journal);
        return Hashes[journal] = Convert.ToHexString(SHA256.HashData(stream))[..16];
    }
}
