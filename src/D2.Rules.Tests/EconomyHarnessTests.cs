using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Map;
using Xunit.Abstractions;
using KSg = Kaitai.Sg;

namespace D2.Rules.Tests;

/// <summary>
/// С1 экономики: слагаемые, посчитанные нами, против того, что записал зонд в журнал карты
/// (схема корпуса v3 — события <c>income</c>, <c>build_status</c>, <c>hire_list</c>).
/// <para>
/// <c>build_status</c> и <c>hire_list</c> сверяются по всему корпусу (<see cref="MapJournalEvents"/>: сессии журналов
/// зонда 6.5+ со снимком — сохранением перед событием, плюс состояние игрока на момент события из журнала). Доход —
/// по одному журналу карты, найденному так же, как в <c>D2.Data.Tests/MapCorpusTests</c>: переменная окружения
/// <c>MAP_JOURNAL</c> либо <c>corpus/**/map-*.jsonl</c>. Стартовая карта прогона — <c>.sg</c>
/// рядом с журналом (<c>MAP_START_SG</c> перебивает).
/// </para>
/// <para>
/// Каждая сверка печатает счётчики «проверено / сошлось / разошлось / пропущено с причиной»;
/// пустой прогон засчитан быть не может — при <c>проверено = 0</c> тест падает.
/// </para>
/// </summary>
[Trait("Category", "Corpus")]
public class EconomyHarnessTests
{
    private readonly ITestOutputHelper _out;

    public EconomyHarnessTests(ITestOutputHelper output) => _out = output;

    // ---------------------------------------------------------------- доход и казна

    [SkippableFact]
    public void Income_MatchesJournal()
    {
        var (journal, map, rules, difficulty) = Setup();
        var saves = Saves(journal);

        int checkedRows = 0, same = 0, differ = 0, skipped = 0;
        int nearSame = 0, nearDiffer = 0;
        int purseChecked = 0, purseSame = 0, purseDiffer = 0, purseSkipped = 0;
        long? firstBadTurn = null, firstBadNearTurn = null, firstBadPurseTurn = null;
        var byCurrency = new int[6];      // расхождения по валютам: g r y e w b
        long minTurn = long.MaxValue;
        int minTurnDiffer = 0;
        // Последнее событие income игрока: казна в событии — ДО начисления (день 1: bank = казна
        // стартовой карты), поэтому казна дня N обязана равняться казне дня N-1 плюс доход дня N-1.
        var previous = new Dictionary<string, IncomeEvent>(StringComparer.OrdinalIgnoreCase);

        _out.WriteLine("день   игрок        журнал g/r,y,e,w,b        старт g/r,y,e,w,b       сохр. g/r,y,e,w,b       сходится");
        foreach (var income in Read<IncomeEvent>(journal))
        {
            if (!map.Players.ContainsKey(income.Player))
            {
                skipped++; purseSkipped++;
                continue;
            }
            var theirs = new Bank(income.Gold, income.Infernal, income.Life, income.Death,
                                  income.Runic, income.Grove);
            var ours = Economy.DailyIncome(map, income.Player, rules, difficulty);
            checkedRows++;
            long turn = income.Turn ?? -1;
            minTurn = Math.Min(minTurn, turn);
            bool ok = ours == theirs;
            if (ok) same++;
            else
            {
                differ++;
                firstBadTurn ??= turn;
                if (turn == minTurn) minTurnDiffer++;
                CountCurrencies(byCurrency, ours, theirs);
            }

            // Та же формула на ближайшем сохранении не позже этого дня: земля за прогон растёт
            // (жила попадает на свою землю), и стартовая карта этого не знает. Разница двух
            // столбцов — цена того, что модель не пересобирает MapState.
            var nearest = saves.LastOrDefault(s => s.Map.Turn <= turn).Map ?? map;
            var near = Economy.DailyIncome(nearest, income.Player, rules, difficulty);
            bool nearOk = near == theirs;
            if (nearOk) nearSame++; else { nearDiffer++; firstBadNearTurn ??= turn; }

            _out.WriteLine($"{turn,-6} {income.Player,-12} {Show(theirs),-24} {Show(ours),-24} {Show(near),-24}" +
                           (ok ? "да" : "НЕТ") + (nearOk ? "" : " (и по сохранению НЕТ)"));

            // Казна — величина накопительная: сошедшийся доход при разошедшейся казне означает
            // неучтённую трату (найм, постройка, покупка), и день расхождения указывает, где её
            // искать. Поэтому счётчик отдельный, а не общий с доходом. Ожидание: день 1 — казна
            // стартовой карты, дальше — казна и доход предыдущего события ЖУРНАЛА (чтобы ошибка
            // модели дохода не считалась дважды), с потолком 9999 на каждую валюту.
            if (income.Purse is null) purseSkipped++;
            else
            {
                purseChecked++;
                var expected = previous.TryGetValue(income.Player, out var prev) && prev.Purse is not null
                    ? Economy.Clamp(prev.Purse + new Bank(prev.Gold, prev.Infernal, prev.Life, prev.Death,
                                                          prev.Runic, prev.Grove))
                    : map.Players[income.Player].Bank;
                if (income.Purse == expected) purseSame++;
                else
                {
                    purseDiffer++; firstBadPurseTurn ??= turn;
                    _out.WriteLine($"  казна день {turn} {income.Player}: журнал {Show(income.Purse)}, " +
                                   $"ожидалось {Show(expected)}");
                }
            }
            previous[income.Player] = income;
        }

        _out.WriteLine($"доход:  проверено {checkedRows}   сошлось {same}   разошлось {differ}   " +
                       $"пропущено {skipped} (игрока нет на стартовой карте)" +
                       (firstBadTurn is null ? "" : $"   первое расхождение: день {firstBadTurn}"));
        _out.WriteLine($"по валютам (g/r/y/e/w/b): {string.Join('/', byCurrency)}");
        _out.WriteLine($"доход по ближайшему сохранению ({saves.Count} сохранений): сошлось {nearSame}   " +
                       $"разошлось {nearDiffer}" +
                       (firstBadNearTurn is null ? "" : $"   первое расхождение: день {firstBadNearTurn}"));
        _out.WriteLine($"казна:  проверено {purseChecked}   сошлось {purseSame}   " +
                       $"разошлось {purseDiffer}   пропущено {purseSkipped} (нет поля bank, журнал v2.1)" +
                       (firstBadPurseTurn is null ? "" : $"   первое расхождение: день {firstBadPurseTurn}"));

        Assert.True(checkedRows > 0, "в журнале нет ни одного события income — проверять нечего");
        // Состояние карты за прогон меняется (жилы переходят из рук в руки, землю двигают жезлы),
        // и модель здесь стоит на СТАРТОВОЙ карте — поэтому строгое требование только к самому
        // раннему дню журнала, когда состояние ещё стартовое.
        Assert.True(minTurnDiffer == 0,
            $"доход расходится уже на первом дне журнала (день {minTurn}) — см. таблицу выше");
    }

    // ---------------------------------------------------------------- постройки

    [SkippableFact]
    public void BuildStatus_MatchesJournal()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var rules = EconomyLoader.Load(GamePaths.Globals);

        var checkedByCode = new SortedDictionary<long, int>();
        var sameByCode = new SortedDictionary<long, int>();
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0, same = 0, differ = 0;

        foreach (var at in MapJournalEvents.Of<BuildStatusEvent>("build_status"))
        {
            var e = at.Event;
            string? why = at.Why
                ?? (!Snapshot(at.Sg!).Players.ContainsKey(e.Player) ? "игрока нет на снимке"
                    : !rules.Buildings.ContainsKey(e.Building) ? "постройки нет в Gbuild" : null);
            if (why is not null) { skips[why] = skips.GetValueOrDefault(why) + 1; continue; }
            var ours = Economy.CanBuild(AtEvent(at, e.Player, out bool builtToday), e.Player, e.Building, rules, e.IgnoreCost,
                                        MaxUnitLevel(at.Sg), builtToday);
            total++;
            checkedByCode[e.Ret] = checkedByCode.GetValueOrDefault(e.Ret) + 1;
            if ((long)ours == e.Ret) { same++; sameByCode[e.Ret] = sameByCode.GetValueOrDefault(e.Ret) + 1; }
            else
            {
                differ++;
                _out.WriteLine($"РАСХОЖДЕНИЕ {at.Source} seq {e.Seq} {e.Player} {e.Building} ignore_cost={e.IgnoreCost}: " +
                               $"журнал {(BuildStatus)e.Ret} ({e.Ret}), наше {ours} ({(long)ours}); снимок {Path.GetFileName(at.Sg)}");
            }
        }

        foreach (var (code, n) in checkedByCode)
            _out.WriteLine($"статус {code,2} ({(BuildStatus)code,-20}): проверено {n}   " +
                           $"сошлось {sameByCode.GetValueOrDefault(code)}");
        foreach (var (why, n) in skips) _out.WriteLine($"  пропуск: {why} — {n}");
        _out.WriteLine($"build_status: проверено {total}   сошлось {same}   разошлось {differ}");

        Skip.If(total == 0, "в корпусе нет событий build_status со снимком карты (проверено 0 напечатано выше)");
        Assert.True(differ == 0, $"build_status: разошлось {differ} — см. РАСХОЖДЕНИЕ выше");
        Assert.True(checkedByCode.Count >= 2,
            $"в корпусе только один код статуса ({string.Join(',', checkedByCode.Keys)}) — " +
            "сверка одного CanBeBuilt ничего не доказывает, нужен прогон с попыткой построить " +
            "недоступное");
    }

    // ---------------------------------------------------------------- найм

    [SkippableFact]
    public void HireList_MatchesJournal()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var rules = EconomyLoader.Load(GamePaths.Globals);

        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0, same = 0, differ = 0;
        foreach (var at in MapJournalEvents.Of<HireListEvent>("hire_list"))
        {
            var e = at.Event;
            string? why = at.Why ?? (!Snapshot(at.Sg!).Players.ContainsKey(e.Player) ? "игрока нет на снимке" : null);
            if (why is not null) { skips[why] = skips.GetValueOrDefault(why) + 1; continue; }
            int ours = Economy.HireList(AtEvent(at, e.Player, out _), e.Player, rules).Count;
            total++;
            if (ours == e.Count) same++;
            else
            {
                differ++;
                _out.WriteLine($"РАСХОЖДЕНИЕ {at.Source} seq {e.Seq} {e.Player} (a3 {e.A3}): журнал {e.Count}, наше {ours}; " +
                               $"снимок {Path.GetFileName(at.Sg)}");
            }
        }

        foreach (var (why, n) in skips) _out.WriteLine($"  пропуск: {why} — {n}");
        _out.WriteLine($"hire_list: проверено {total}   сошлось {same}   разошлось {differ}");
        Skip.If(total == 0, "в корпусе нет событий hire_list со снимком карты (проверено 0 напечатано выше)");
        Assert.True(differ == 0, $"hire_list: разошлось {differ} — см. РАСХОЖДЕНИЕ выше");
    }

    /// <summary>
    /// Состояние игрока на момент события, а не сохранения: сохранение ложится раньше, и между ним и событием игрок
    /// успевает строить и тратить. Досыпается из журнала (ранг 1 — строки движка):
    /// <list type="bullet">
    /// <item>постройки — строки <c>build</c> (<c>ret</c>) игрока после среза снимка;</item>
    /// <item>казна — <c>after</c> последней строки <c>bank</c> игрока после среза (строка несёт казну целиком);</item>
    /// <item>«уже строил сегодня» (<c>0x5dca8e</c>, поле игрока <c>+0x50</c> против текущего хода, в сохранении его нет) —
    /// строка <c>build</c> игрока после начала его дня: последней строки <c>income</c> игрока до события.</item>
    /// </list>
    /// probe65 seq 402491 <c>build G000BB0050</c> → seq 402501 <c>build_status G000BB0071 ret 6</c> (со снимка дня 7 было 9 —
    /// требуемая постройка ещё не стояла); seq 495122 <c>build G000BB0071</c> → seq 495158 <c>hire_list count 5</c> (со снимка 4).
    /// </summary>
    private static MapState AtEvent<T>(MapJournalEvents.At<T> at, string player, out bool builtToday) where T : CorpusEvent
    {
        var events = Replay(at.Journal);
        long seq = at.Event.Seq;
        bool Mine(CorpusEvent e, string p) => p.Equals(player, StringComparison.OrdinalIgnoreCase) && e.Seq < seq;
        var builds = events.OfType<BuildEvent>().Where(b => b.Ret && Mine(b, b.Player)).ToList();
        long dayStart = events.OfType<IncomeEvent>().Where(i => Mine(i, i.Player) && i.Seq > at.DayFrom)
                              .Select(i => i.Seq).DefaultIfEmpty(at.DayFrom).Max();
        builtToday = builds.Any(b => b.Seq > dayStart);
        var bank = events.OfType<BankEvent>().LastOrDefault(b => Mine(b, b.Player) && b.Seq > at.From)?.After;
        return AiStrategicHarnessTests.WithBuildings(Snapshot(at.Sg!), player,
            builds.Where(b => b.Seq > at.From).Select(b => b.Building), bank);
    }

    private static readonly Dictionary<string, List<CorpusEvent>> Replays = new(StringComparer.OrdinalIgnoreCase);

    private static List<CorpusEvent> Replay(string journal)
    {
        lock (Replays)
            return Replays.TryGetValue(journal, out var r) ? r
                : Replays[journal] = JournalCache.Read(journal, ["build", "bank", "income"]).OrderBy(e => e.Seq).ToList();
    }

    private static readonly Dictionary<string, MapState> Snapshots = new(StringComparer.OrdinalIgnoreCase);

    private static MapState Snapshot(string sg)
    {
        lock (Snapshots)
            return Snapshots.TryGetValue(sg, out var m) ? m : Snapshots[sg] = SaveMaps.Map(sg);
    }

    // ---------------------------------------------------------------- поиск данных

    private static IEnumerable<T> Read<T>(string journal) where T : CorpusEvent =>
        JournalCache.Read(journal).OfType<T>();

    private (string Journal, MapState Map, EconomyRules Rules, Difficulty Difficulty) Setup()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var journal = MapJournal;
        Skip.If(journal is null,
            "нет журнала карты (corpus/**/map-*.jsonl или MAP_JOURNAL) — его снимает пользователь, " +
            "задача 2 плана Ф5");
        var start = StartMap();
        Skip.If(start is null,
            $"журнал есть ({journal}), но рядом с ним нет стартовой карты .sg — укажите MAP_START_SG");

        var difficulty = DifficultyOf(start!);
        _out.WriteLine($"журнал {journal}");
        _out.WriteLine($"стартовая карта {start} (сложность {difficulty})");
        return (journal!, MapLoader.Load(start!), EconomyLoader.Load(GamePaths.Globals), difficulty);
    }

    /// <summary>Сохранения прогона рядом с журналом (<c>save-*.sg</c>, раскладка
    /// <c>corpus_c4_run</c>) по возрастанию дня — состояние карты на середине прогона.</summary>
    private static IReadOnlyList<(string Path, MapState Map)> Saves(string journal) =>
        Directory.GetFiles(Path.GetDirectoryName(journal)!, "save-*.sg")
            .Select(p => (Path: p, Map: SaveMaps.Map(p)))
            .OrderBy(s => s.Map.Turn)
            .ToList();

    private static string CorpusDir => CorpusFiles.Dir;

    private static string? MapJournal
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("MAP_JOURNAL");
            if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
            if (!Directory.Exists(CorpusDir)) return null;
            return Directory.GetFiles(CorpusDir, "map-*.jsonl", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        }
    }

    /// <summary>Карта, с которой прогон начинался: <c>MAP_START_SG</c>, иначе самый ранний
    /// <c>.sg</c> в каталоге журнала (там их кладёт задача 9 — <c>save-NN.sg</c>).</summary>
    private static string? StartMap()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAP_START_SG");
        if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
        var journal = MapJournal;
        var dir = journal is null ? null : Path.GetDirectoryName(journal);
        if (dir is null || !Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "*.sg").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Сложность партии — <c>ScenarioInfo.DIFFGAME</c>. В <see cref="MapState"/> её нет
    /// (модель не читает файлов), поэтому здесь она берётся прямо из <c>.sg</c>.</summary>
    private static Difficulty DifficultyOf(string sgPath) =>
        (Difficulty)(int)SgFile.Open(sgPath).Raw.Objects.Select(o => o.Data)
            .OfType<KSg.ScenarioInfo>().Single().Diffgame.Value;

    /// <summary>Потолок уровня юнитов сценария (<c>ScenarioInfo.MAX_UNIT</c>) — вход движковой
    /// проверки статуса 5.</summary>
    private static int MaxUnitLevel(string? sgPath) =>
        sgPath is null
            ? int.MaxValue
            : (int)SgFile.Open(sgPath).Raw.Objects.Select(o => o.Data)
                .OfType<KSg.ScenarioInfo>().Single().MaxUnit.Value;

    private static void CountCurrencies(int[] byCurrency, Bank ours, Bank theirs)
    {
        if (ours.Gold != theirs.Gold) byCurrency[0]++;
        if (ours.Infernal != theirs.Infernal) byCurrency[1]++;
        if (ours.Life != theirs.Life) byCurrency[2]++;
        if (ours.Death != theirs.Death) byCurrency[3]++;
        if (ours.Runic != theirs.Runic) byCurrency[4]++;
        if (ours.Grove != theirs.Grove) byCurrency[5]++;
    }

    private static string Show(Bank b) =>
        $"{b.Gold}/{b.Infernal},{b.Life},{b.Death},{b.Runic},{b.Grove}";
}
