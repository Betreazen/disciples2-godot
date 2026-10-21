using D2.Data.Diagnostics;
using D2.Data.Sound;

// Сверка звука, музыки, роликов и анимаций двух журналов зонда (схема 7.3): журнал оригинала (tools/d2probe) и журнал порта
// (godot/autoload/PortProbe.cs). Правила — D2.Data.Diagnostics.AvJournal; docs/probe-av.md.
//
//   dotnet run --project tools/AvCompare -- <оригинал.jsonl> <порт.jsonl> [--out отчёт.md] [--data <каталог data>]
//
// Несколько журналов одной стороны — через «;» (журналы читаются порознь и складываются по порядку: у каждого свой seq).
// Звуки боя по ходящему сверяются по звукам юнитов data/sound/UnitSounds.json (каталог data ищется вверх от текущего, или --data).
// С журналами схемы 7.6 с обеих сторон — ещё портреты боя (вспышки и надписи: battle_face, battle_text_place/persist/wrap,
// battle_flash_ticks), метки карты в начале хода (turn_marks) и погашенные кнопки (ui_enable — в какую сторону менялся элемент,
// ui_enable_click — что погасло и зажглось на экране после нажатия; у порта — с Зс54); docs/probe-av.md §8.
// Выход 0 — расхождений нет, 1 — есть (строки «≠»), 2 — неверные аргументы или журналы без строк схемы 7.3.
if (args.Length < 2)
{
    Console.Error.WriteLine("dotnet run --project tools/AvCompare -- <оригинал.jsonl> <порт.jsonl> [--out отчёт.md] [--data <каталог data>]");
    return 2;
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
var original = AvJournal.Join(args[0].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(AvJournal.Load));
var port = AvJournal.Join(args[1].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(AvJournal.Load));
if (original.All(e => e.Ev == AvJournal.JournalBreak) || port.All(e => e.Ev == AvJournal.JournalBreak))
{
    Console.Error.WriteLine($"строк звука и анимаций: оригинал {original.Count}, порт {port.Count} — нужен журнал схемы 7.3 с обеих сторон");
    return 2;
}
string? data = Option("--data") ?? FindData();
var sounds = data is null ? null : UnitSounds.Load(Path.Combine(data, "sound", "UnitSounds.json"));
var diffs = AvJournal.Compare(original, port, sounds);
var lines = new List<string>
{
    $"# Звук и анимации: `{string.Join("; ", args[0].Split(';').Select(Path.GetFileName))}` ↔ `{string.Join("; ", args[1].Split(';').Select(Path.GetFileName))}`", "",
    $"Строк: оригинал {original.Count}, порт {port.Count}; сверок {diffs.Count}, расхождений {diffs.Count(d => !d.Ok)}" +
    (sounds is null ? "; звуки боя по ходящему не сверены (нет data/sound/UnitSounds.json)." : "."), "",
    "| | Сверка | Ключ | Оригинал | Порт |", "|---|---|---|---|---|",
};
lines.AddRange(diffs.OrderBy(d => d.Ok).ThenBy(d => d.Check, StringComparer.Ordinal)
    .Select(d => $"| {(d.Ok ? "=" : "≠")} | {d.Check} | {d.Key} | {d.Original} | {d.Port} |"));
foreach (var line in lines) Console.WriteLine(line);
if (Option("--out") is { } outPath) File.WriteAllLines(outPath, lines);
return diffs.Any(d => !d.Ok) ? 1 : 0;

string? Option(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

static string? FindData()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "data", "sound", "UnitSounds.json"))) return Path.Combine(dir.FullName, "data");
    return null;
}
