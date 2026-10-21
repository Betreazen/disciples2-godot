using System.Diagnostics;
using D2.Data.Import;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Общее у сверок импорта: копия игры, слой русификации, каталог выхода во временном каталоге, сравнение текстов.</summary>
internal static class ImportParity
{
    /// <summary>Слой русификации: <c>D2_L10N_DIR</c>, иначе <c>disciples-vanilla/l10n/russobit</c> рядом с репозиторием моддинга; нет — <c>null</c>.</summary>
    public static string? L10nDir
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("D2_L10N_DIR") is { Length: > 0 } env
                ? env : Path.GetFullPath(Path.Combine(GamePaths.GameDir, "..", "..", "disciples-vanilla", "l10n", "russobit"));
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>Собранная графика и звук для сверки файлов: <c>D2_ASSETS_DIR</c>, иначе <c>assets</c> репозитория; нет — <c>null</c>.</summary>
    public static string? AssetsDir(string sub)
    {
        string root = Environment.GetEnvironmentVariable("D2_ASSETS_DIR") is { Length: > 0 } env ? env : Path.Combine(RepoRoot, "assets");
        string dir = Path.Combine(root, sub);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any() ? dir : null;
    }

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
            return d?.FullName ?? "";
        }
    }

    public static void RequireGame() => Skip.IfNot(GamePaths.IsAvailable, $"нет копии игры {GamePaths.GameDir} (D2_GAME_DIR)");

    public static string NewOut() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "d2-import-" + Guid.NewGuid().ToString("N"))).FullName;

    public static ImportContext Context(string outRoot, bool l10n, TextWriter log) =>
        new(GamePaths.GameDir, outRoot, new NoEncoder())
        {
            L10nDir = l10n ? L10nDir : null, Log = log,
            Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } ffmpeg ? ffmpeg : null,
            Ffprobe = Environment.GetEnvironmentVariable("FFPROBE") is { Length: > 0 } ffprobe ? ffprobe : null,
        };

    /// <summary>Текст импорта против файла репозитория (<c>data/…</c> рядом со сборкой тестов), концы строк — LF у обоих.</summary>
    public static void SameText(string outRoot, string relative)
    {
        string expected = File.ReadAllText(ImportedData.PathOf(relative)).ReplaceLineEndings("\n");
        string actual = File.ReadAllText(Path.Combine(outRoot, "data", relative)).ReplaceLineEndings("\n");
        Assert.True(expected == actual, $"data/{relative}: расхождение с {FirstDiff(expected, actual)}");
    }

    private static string FirstDiff(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Around(string s) => s.Substring(Math.Max(0, i - 40), Math.Min(s.Length, i + 40) - Math.Max(0, i - 40));
        return $"знака {i}: в репозитории «{Around(a)}», у импорта «{Around(b)}»";
    }

    public static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог — не страшно
        }
    }

    /// <summary>Кодек картинок этим шагам не нужен.</summary>
    private sealed class NoEncoder : IImageEncoder
    {
        public byte[] EncodeWebpLossless(RgbaImage image) => throw new NotSupportedException("сверка импорта WebP не пишет");
        public RgbaImage? DecodeWebp(byte[] data) => null;
    }
}

/// <summary>Шаги импорта на C# дают те же описи, что прежние сборщики на Python (<c>tools/ui-assets/import_{capital,leader_names,fonts}.py</c>):
/// импорт из копии игры во временный каталог и сравнение с файлами репозитория байт в байт; атласы шрифтов — по точкам.</summary>
public class ImportParityTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Capital_SameAsRepository()
    {
        ImportParity.RequireGame();
        ImportedData.Require("ui/capital.json");
        string dir = ImportParity.NewOut();
        try
        {
            var clock = Stopwatch.StartNew();
            CapitalImport.Run(ImportParity.Context(dir, false, TextWriter.Null));
            output.WriteLine($"capital: {clock.ElapsedMilliseconds} мс");
            ImportParity.SameText(dir, "ui/capital.json");
        }
        finally
        {
            ImportParity.Cleanup(dir);
        }
    }

    [SkippableFact]
    public void LeaderNames_SameAsRepository()
    {
        ImportParity.RequireGame();
        ImportedData.Require("ui/leader-names.en.json");
        string dir = ImportParity.NewOut();
        try
        {
            var clock = Stopwatch.StartNew();
            var log = new StringWriter();
            LeaderNamesImport.Run(ImportParity.Context(dir, true, log));
            output.WriteLine($"leader-names: {clock.ElapsedMilliseconds} мс\n{log}");
            ImportParity.SameText(dir, "ui/leader-names.en.json");
            if (ImportParity.L10nDir is null) output.WriteLine("нет слоя русификации — ru не сверялся");
            else ImportParity.SameText(dir, "ui/leader-names.ru.json");
        }
        finally
        {
            ImportParity.Cleanup(dir);
        }
    }

    /// <summary>BMFont: <c>.fnt</c> — байт в байт, атлас PNG — те же точки RGBA, что у собранных прежним сборщиком (PIL пишет PNG иначе).</summary>
    [SkippableFact]
    public void Fonts_SameAsBuiltAssets()
    {
        ImportParity.RequireGame();
        string? reference = ImportParity.AssetsDir("fonts");
        Skip.If(reference is null, "нет собранных шрифтов для сверки (D2_ASSETS_DIR/fonts)");
        Skip.If(ImportParity.L10nDir is null, "нет слоя русификации: шрифты репозитория собраны с кириллицей (D2_L10N_DIR)");
        string dir = ImportParity.NewOut();
        try
        {
            var clock = Stopwatch.StartNew();
            var log = new StringWriter();
            FontsImport.Run(ImportParity.Context(dir, true, log));
            output.WriteLine($"fonts: {clock.ElapsedMilliseconds} мс\n{log}");
            string built = Path.Combine(dir, "assets", "fonts");
            foreach (string name in FontsImport.Names)
            {
                Assert.Equal(File.ReadAllText(Path.Combine(reference!, name + ".fnt")).ReplaceLineEndings("\n"),
                             File.ReadAllText(Path.Combine(built, name + ".fnt")));
                var want = PngCodec.Decode(File.ReadAllBytes(Path.Combine(reference!, name + ".png")));
                var got = PngCodec.Decode(File.ReadAllBytes(Path.Combine(built, name + ".png")));
                Assert.Equal((want.Width, want.Height), (got.Width, got.Height));
                Assert.True(want.Pixels.AsSpan().SequenceEqual(got.Pixels), $"{name}.png: точки атласа разошлись");
            }
        }
        finally
        {
            ImportParity.Cleanup(dir);
        }
    }

    /// <summary>Без слоя русификации шрифт собирается без кириллицы, а журнал предупреждает.</summary>
    [SkippableFact]
    public void Fonts_WithoutL10n_WarnAndSkipCyrillic()
    {
        ImportParity.RequireGame();
        string dir = ImportParity.NewOut();
        try
        {
            var log = new StringWriter();
            FontsImport.Run(ImportParity.Context(dir, false, log));
            Assert.Contains("предупреждение: нет слоя русификации", log.ToString(), StringComparison.Ordinal);
            string fnt = File.ReadAllText(Path.Combine(dir, "assets", "fonts", "normal.fnt"));
            Assert.DoesNotContain("char id=1040 ", fnt, StringComparison.Ordinal);   // «А»
            Assert.Contains("char id=65 ", fnt, StringComparison.Ordinal);
        }
        finally
        {
            ImportParity.Cleanup(dir);
        }
    }

    [Fact]
    public void PyRound_MatchesPythonOnExactValue()
    {
        // round() Python смотрит на точное двоичное значение: 0.0005 чуть больше половины, 2.675 — чуть меньше, 0.0625 — ровно половина.
        Assert.Equal(0.001, PyCompat.Round(0.0005, 3));
        Assert.Equal(2.67, PyCompat.Round(2.675, 2));
        Assert.Equal(0.062, PyCompat.Round(0.0625, 3));
        Assert.Equal(1.045, PyCompat.Round(46080.0 / 44100, 3));
        Assert.Equal(15.002, PyCompat.Round(15.0015, 3));
        Assert.Equal(-0.0, PyCompat.Round(-0.0001, 3));
        Assert.Equal(36.0, PyCompat.Round(36.0, 3));
    }
}

/// <summary>Звук: описи <c>data/sound/*.json</c> — байт в байт с репозиторием, сами файлы — байт в байт с собранными прежним сборщиком
/// (<c>tools/sound-assets/build_sound_assets.py</c>), если они есть (<c>D2_ASSETS_DIR/sound</c>).</summary>
public class ImportParitySoundTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Sound_SameAsRepository()
    {
        ImportParity.RequireGame();
        ImportedData.Require("sound/Midgard.json");
        string dir = ImportParity.NewOut();
        try
        {
            var clock = Stopwatch.StartNew();
            var log = new StringWriter();
            SoundImport.Run(ImportParity.Context(dir, false, log));
            output.WriteLine($"sound: {clock.ElapsedMilliseconds} мс\n{log}");
            var produced = Directory.GetFiles(Path.Combine(dir, "data", "sound")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["AudioRgn.json", "Battle.json", "Midgard.json", "Music.json", "UnitSounds.json"], produced);
            foreach (string name in produced) ImportParity.SameText(dir, "sound/" + name);
            if (ImportParity.AssetsDir("sound") is not { } reference)
            {
                output.WriteLine("нет собранного звука (D2_ASSETS_DIR/sound) — файлы не сверялись");
                return;
            }
            string built = Path.Combine(dir, "assets", "sound");
            var files = Directory.GetFiles(built, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(built, p)).Order(StringComparer.Ordinal).ToList();
            var want = Directory.GetFiles(reference, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(reference, p)).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(want, files);
            foreach (string rel in files)
                Assert.True(File.ReadAllBytes(Path.Combine(reference, rel)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(built, rel))), $"{rel}: байты разошлись");
            output.WriteLine($"файлов звука сверено: {files.Count}");
        }
        finally
        {
            ImportParity.Cleanup(dir);
        }
    }
}

/// <summary>Ролики: опись <c>data/video/videos.json</c> в режиме «только опись» (ffprobe и хеши исходников) — байт в байт с репозиторием.</summary>
public class ImportParityVideoTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void VideoManifest_SameAsRepository()
    {
        ImportParity.RequireGame();
        ImportedData.Require("video/videos.json");
        string dir = ImportParity.NewOut();
        try
        {
            var clock = Stopwatch.StartNew();
            var log = new StringWriter();
            bool done = VideoImport.Run(ImportParity.Context(dir, false, log), manifestOnly: true);
            output.WriteLine($"video (опись): {clock.ElapsedMilliseconds} мс\n{log}");
            Skip.IfNot(done, $"нет ffprobe: {log}");
            Assert.False(Directory.Exists(Path.Combine(dir, "assets")), "в режиме «только опись» роликов быть не должно");
            ImportParity.SameText(dir, "video/videos.json");
        }
        finally
        {
            ImportParity.Cleanup(dir);
        }
    }
}

/// <summary>Кодирование роликов — своим классом (xUnit гонит его параллельно с описью): самопроверка кодировщика и три самых коротких ролика
/// меню, результат читается обратно декодером ffmpeg без ошибок. Зовёт ffmpeg (~10 с) — вне smoke.</summary>
[Trait("Category", "Corpus")]
public class ImportParityVideoEncodeTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void VideoEncode_ShortClipsDecodeBack()
    {
        ImportParity.RequireGame();
        string ffmpeg = Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } f ? f : "ffmpeg";
        string ffprobe = Environment.GetEnvironmentVariable("FFPROBE") is { Length: > 0 } p ? p : "ffprobe";
        string dir = ImportParity.NewOut();
        try
        {
            string? why;
            try
            {
                why = VideoImport.CheckEncoder(ffmpeg, dir, CancellationToken.None);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                why = e.Message;
            }
            Skip.If(why is not null, $"ffmpeg негоден: {why}");
            var clock = Stopwatch.StartNew();
            var jobs = VideoImport.Collect(GamePaths.GameDir, dir).Where(j => j.Source == "MenuAnim")
                                  .OrderBy(j => new FileInfo(j.Src).Length).Take(3).ToList();
            Assert.Equal(3, jobs.Count);
            foreach (var job in jobs)
            {
                var meta = VideoImport.Probe(ffprobe, job, CancellationToken.None);
                string ogv = Path.Combine(dir, "out", job.Rel);
                VideoImport.Encode(ffmpeg, job, (bool)meta["audio"]!, ogv, CancellationToken.None);
                var check = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-i", ogv, "-f", "null", "-"])
                {
                    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                })!;
                string err = check.StandardError.ReadToEnd();
                check.WaitForExit();
                Assert.True(check.ExitCode == 0 && err.Trim().Length == 0, $"{job.Rel}: декодер ffmpeg: {err}");
                output.WriteLine($"{job.Rel}: {new FileInfo(ogv).Length} байт, кадров {meta["frames"]}");
            }
            output.WriteLine($"кодирование трёх роликов: {clock.ElapsedMilliseconds} мс");
        }
        finally
        {
            ImportParity.Cleanup(dir);
        }
    }
}
