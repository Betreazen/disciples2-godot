using System.Diagnostics;
using System.Runtime;
using System.Text.Json;

namespace D2.Bench;

/// <summary>Один бенчмарк: <see cref="Prepare"/> — подготовка вне замера (свежая партия на каждый прогон, потому что ход ИИ
/// и бой меняют состояние), <see cref="Run"/> — замеряемая часть. Результат <see cref="Run"/> сохраняется в «сток», чтобы JIT
/// не выбросил вызов.</summary>
public sealed record BenchCase(string Name, Func<object?> Prepare, Func<object?, object?> Run)
{
    public static BenchCase Plain(string name, Func<object?> run) => new(name, () => null, _ => run());
}

/// <summary>Харнесс в духе <c>bench.gd</c> скилла: 3 прогрева, 15 замеров, медиана/min/max, JSON того же вида
/// (<c>header</c>, <c>iterations</c>, <c>results</c>: имя → отсортированные замеры в микросекундах) — его читает
/// <c>bench_compare.gd</c>. Случайность логики порта — генератор партии из сохранения (R250 над константной таблицей), он
/// детерминирован; собственный seed харнессу не нужен.</summary>
public static class Harness
{
    public const int Warmup = 3;
    public const int Runs = 15;

    private static object? _sink;

    public static string Header =>
        $"D2.Bench .NET {Environment.Version}, {(IsOptimized ? "Release" : "Debug")}, gc={(GCSettings.IsServerGC ? "server" : "workstation")}, " +
        $"cpu={Environment.ProcessorCount}";

    private static bool IsOptimized =>
        typeof(Harness).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    public static int Run(IReadOnlyList<BenchCase> cases, string[] args)
    {
        var only = Arg(args, "--filter=")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        Console.WriteLine(Header);
        var results = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (only.Length > 0 && !only.Contains(c.Name)) continue;
            results[c.Name] = Measure(c);
        }
        if (Arg(args, "--save=") is { } path) Save(path, results);
        return 0;
    }

    public static long[] Measure(BenchCase c)
    {
        for (int i = 0; i < Warmup; i++) _sink = c.Run(c.Prepare());
        var samples = new long[Runs];
        for (int i = 0; i < Runs; i++)
        {
            object? input = c.Prepare();
            // Сборка мусора от подготовки не должна попасть в замер.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var t0 = Stopwatch.GetTimestamp();
            _sink = c.Run(input);
            samples[i] = (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        }
        Array.Sort(samples);
        long median = samples[samples.Length >> 1];
        long q1 = samples[samples.Length / 4], q3 = samples[3 * samples.Length / 4];
        Console.WriteLine($"{c.Name,-26} median {median,10} us | min {samples[0],10} | max {samples[^1],10} | IQR {q1}-{q3} | {_sink}");
        return samples;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static void Save(string path, SortedDictionary<string, long[]> results)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var report = new Dictionary<string, object> { ["header"] = Header, ["iterations"] = 1, ["results"] = results };
        File.WriteAllText(full, JsonSerializer.Serialize(report, Indented));
        Console.WriteLine($"saved {full}");
    }

    public static string? Arg(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal)) is { } a ? a[prefix.Length..] : null;
}
