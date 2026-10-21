/// <summary>Корпус лежит вне git и не копируется в вывод сборки — ищем его от каталога сборки
/// вверх до корня репозитория (файл Disciples.sln).</summary>
public static class CorpusFiles
{
    public static string Dir { get; } = FindCorpus();
    public static bool Available => Dir.Length > 0 && Journals().Count > 0;

    /// <summary>Журналы corpus_run.py (battle-NNN.jsonl), журналы прогонов карты
    /// (map-NNN.jsonl — с Ф6 бои снимаются в одном длинном сеансе вместе с картой, и без них
    /// боевые харнессы не увидели бы ни одного боя фазы) плюс закоммиченный образец.
    /// Каталог с именем, начинающимся на "_" (например, `_old-20260906-no-order` или
    /// `_partial-20260908-f6-43turns`), — сознательно выведенный из оборота прогон, не
    /// сканируется.
    ///
    /// <para><b>Маска <c>passive-*</c> (журналы пассивных прогонов Ф7, так их называет драйвер
    /// <c>tools/gamedrv</c>) открыта задачей 16 — вместе с тем, что её держало закрытой.</b>
    /// Держало не имя файла: такой журнал несёт события предметов (<c>item_equip</c>/
    /// <c>item_unequip</c>), а личности предмета и цепочки <c>MODIF_ID</c> в схеме зонда нет вовсе.
    /// Харнесс собирал юнита из <c>Gunits</c> и уровневого роста и считал прокачанного героя
    /// новобранцем: на партии <c>20260910-passive/passive-004.jsonl</c> это давало 183 расхождения
    /// С1 (<c>S073UN01f8</c> — ровно ×1,25 от расчёта, <c>S073UN01fc</c> — ×1,10).</para>
    /// <para>Теперь цепочка берётся из сохранений рядом с журналом
    /// (<see cref="D2.Data.Battle.SaveSnapshots"/>), поэтому маска и открыта. Условие приёма
    /// журнала стало содержательным, а не именным (<see cref="Admitted"/>): журнал с событиями
    /// предметов допускается в корпус только тогда, когда рядом объявлены сохранения. Это же
    /// условие сторожит <c>ItemTests.Corpus_ItemTraces_HaveSavesBeside</c> — он падает, если в
    /// корпус попадёт журнал, чью цепочку восстановить неоткуда.</para>
    /// </summary>
    public static IReadOnlyList<string> Journals() => Scanned.Value;

    /// <summary>Список собирается один раз: приём журнала читает файл целиком (см.
    /// <see cref="ItemEvents"/>), а <see cref="Journals"/> зовут все харнессы подряд.</summary>
    private static readonly Lazy<IReadOnlyList<string>> Scanned = new(Scan);

    /// <summary>Маски имён журналов корпуса. Переменная окружения <c>D2_CORPUS_MASKS</c> (через
    /// запятую) сужает корпус без правки кода: <c>battle-*.jsonl,map-*.jsonl</c> — прежний корпус из
    /// 69 файлов, на котором держатся опорные числа С1/С2. Прежде его получали временной правкой
    /// этой строки и пересборкой, и два прогона нельзя было гонять с одной сборки параллельно.</summary>
    public static IReadOnlyList<string> Masks { get; } =
        Environment.GetEnvironmentVariable("D2_CORPUS_MASKS") is { Length: > 0 } env
            ? env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["battle-*.jsonl", "map-*.jsonl", "passive-*.jsonl"];

    private static IReadOnlyList<string> Scan()
    {
        if (Dir.Length == 0 || !Directory.Exists(Dir)) return [];
        // Приём читает каждый журнал и его сохранения — параллельно по файлам: прежде один поток шёл по всему корпусу, а все харнессы
        // процесса ждали этот список (16.10 — ~45 с в начале прогона D2.Rules.Tests). Порядок PLINQ не держит — его задаёт Sort ниже.
        var files = Masks
            .SelectMany(mask => Directory.EnumerateFiles(Dir, mask, SearchOption.AllDirectories))
            .Where(f => !Excluded(Path.GetFileName(Path.GetDirectoryName(f)) ?? ""))
            .ToList()
            .AsParallel()
            .Where(Admitted)
            .ToList();
        var sample = Path.Combine(Dir, "sample", "one_battle.jsonl");
        if (File.Exists(sample)) files.Add(sample);
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>Каталог прогона не идёт в корпус правил: имя с <c>_</c> — сознательно выведенный прогон, имя на
    /// <c>-ui</c> — срез экранов интерфейса (памятки <c>docs/PLAY-ui-screens*.md</c>). Срез снимается живой игрой
    /// человека ради графа экранов: ходов ИИ в нём нет, сохранения сняты не по дням, и харнессы правил на нём
    /// сверять нечего. Граф экранов эти журналы берёт по именам сам (<c>D2.Data.Ui.UiFlowImport</c>).</summary>
    public static bool Excluded(string run) => run.StartsWith('_') || run.EndsWith("-ui", StringComparison.Ordinal);

    /// <summary>Событий предметов в журнале (<c>item_equip</c>/<c>item_unequip</c>). Ноль — журнал
    /// про предметы ничего не знает, и цепочка юнита в нём заведомо та же, что в <c>Gunits</c>.
    /// Берётся из указателя видов общего кэша журналов (<see cref="D2.Data.Corpus.JournalCache"/>): журнал корпуса всё равно
    /// разбирают харнессы, а приём и сторож спрашивают одно и то же.</summary>
    public static int ItemEvents(string path) =>
        D2.Data.Corpus.JournalCache.Count(path, "item_equip") + D2.Data.Corpus.JournalCache.Count(path, "item_unequip");

    /// <summary>Условие приёма журнала в корпус — содержательное, а не именное. Журнал, в котором
    /// надевали предметы, годен только тогда, когда рядом объявлены сохранения: цепочки
    /// <c>MODIF_ID</c> в журнале нет, и без снимка прокачанный герой считается новобранцем.
    /// <para>Так из корпуса выпадал <c>20260910-passive/passive-002.jsonl</c> (7 боёв, 70 событий
    /// предметов): его строку манифеста затёр следующий прогон в тот же каталог. 11.09.2026 строка
    /// восстановлена ДОКАЗАТЕЛЬСТВОМ, а не по имени файла (окно времени журнала, ход и карта внутри
    /// <c>.sg</c>, уровни юнитов — <c>disciples-modding/.superpowers/sdd/2026-09-09-f6plus-coverage/
    /// manifest-restore-check.py</c>), и несёт поле <c>restored</c>. Тем же путём восстановлена строка
    /// <c>20260911-passive/passive-001.jsonl</c>, затёртая прогоном карты M17. Соседние журналы без
    /// событий предметов проходят и без снимков: цепочке взяться неоткуда.</para></summary>
    private static bool Admitted(string path) =>
        ItemEvents(path) == 0 || D2.Data.Battle.SaveSnapshots.HasSaves(path);

    /// <summary>D2_CORPUS_DIR — корпус из другого каталога (рабочее дерево без корпуса, только чтение).</summary>
    private static string FindCorpus()
    {
        if (Environment.GetEnvironmentVariable("D2_CORPUS_DIR") is { Length: > 0 } dir) return dir;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
        return d is null ? "" : Path.Combine(d.FullName, "corpus");
    }
}
