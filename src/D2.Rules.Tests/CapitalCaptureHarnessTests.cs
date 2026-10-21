using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Харнесс задачи 14 Ф7: гибель игрока и ветка победы против корпуса.
///
/// <list type="bullet">
/// <item><c>capital_capture_state</c> — на каждом переходе сохранений, где у игрока появился
/// <c>CAPT_BY</c>: метка совпала с моделью, владелец столицы не сменился, все жезлы погибшего
/// снесены, у каждого его уцелевшего отряда приказ <see cref="CapitalCapture.BerserkOrder"/>;</item>
/// <item><c>capital_capture_gate</c> — по состоянию ДО боя модель захват отвергает (в столице
/// живой защитник): условие гибели не пустое;</item>
/// <item><c>capital_land_unchanged</c> — пятно 5×5 вокруг взятой столицы не перекрашивается
/// (открытый пункт §3 п. 24 плана);</item>
/// <item><c>income_after_capture</c> — модель даёт захваченному игроку нулевой доход, а в журнале
/// после хода захвата его строк <c>income</c> нет;</item>
/// <item><c>win_branch</c> — на <c>m20-capture</c> модель выбирает победу, и в журнале после того
/// <c>battle_end</c> нет ни одной строки <c>bank</c>; на каждом настоящем захвате модель, наоборот,
/// победу НЕ выбирает.</item>
/// </list>
///
/// <para><b>Состояние на вход модели.</b> Эффект захвата применяется уже после боя, а сохранение
/// «до» снято до него: защитники в нём живы. Поэтому модели подаётся столица предыдущего
/// сохранения с пустым гарнизоном и без отряда внутри — ровно то, что оставил бой. Что калитка при
/// этом не вырождается, отдельно мерит <c>capital_capture_gate</c> на том же, нетронутом
/// состоянии.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class CapitalCaptureHarnessTests(ITestOutputHelper output)
{
    private const string State = "capital_capture_state";
    private const string Gate = "capital_capture_gate";
    private const string Land = "capital_land_unchanged";
    private const string Income = "income_after_capture";
    private const string Win = "win_branch";

    /// <summary>Карта ветки победы: человек берёт столицу единственного живого соперника.
    /// Настоящих захватов на ней нет и быть не может — именно этим она и нужна.</summary>
    private const string WinMap = "f7-m20-capture";

    /// <summary>Сторона пятна земли столицы — 5×5 клеток (план Ф7 §1.6, §3 п. 24).</summary>
    private const int SpotRadius = 2;

    [SkippableFact] public void CapitalCapture_MatchesTheCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Dir.Length > 0 && Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var rules = EconomyLoader.Load(GamePaths.Globals);

        var c = new Counters();
        c.Note($"приказ погибшего L_BEZERK = {CapitalCapture.BerserkOrder}; эффект победы L_WIN = " +
               $"{CapitalCapture.WinEffectCategory}; кубик заклинаний rng({CapitalCapture.SpellChanceDie}) " +
               "против GVars.GAIN_SPELL = 50 (наблюдений в корпусе нет)");
        c.Note("переход казны при захвате мерит харнесс казны задачи 1 по строкам bank (захват столицы "
             + "6 сравнений, 0 расхождений); здесь он заново не измеряется.");

        foreach (var (name, files) in SaveChains.All(keepSameTurn: true)) Chain(name, files, rules, c);
        foreach (var path in JournalSet.Distinct(CorpusFiles.Journals()).Unique.Where(TreasuryJournal.HasBank))
            Journal(TreasuryJournal.Load(path), rules, c);

        c.Print(output, "задача 14 — захват столицы: гибель игрока и ветка победы");
        c.Require(State, Gate, Land, Income, Win);
    }

    // ------------------------------------------------------- цепочки сохранений: состояние захвата

    /// <summary>Цепочка целиком: интересен каждый переход, на котором у игрока появился
    /// <c>CAPT_BY</c>.</summary>
    private void Chain(string name, IReadOnlyList<string> files, EconomyRules rules, Counters c)
    {
        (string Path, MapState Map, IReadOnlyList<MapSite> Sites)? prev = null;
        foreach (var f in files)
        {
            var shot = (Path: f, Map: SaveMaps.Map(f), Sites: SaveMaps.Sites(f));
            if (prev is { } before)
                foreach (var (id, player) in shot.Map.Players)
                    if (player.CapturedBy.Length > 0 && before.Map.Players.TryGetValue(id, out var was)
                        && was.CapturedBy.Length == 0)
                        Captured(name, before.Path, before.Map, before.Sites,
                                 shot.Map, shot.Sites, id, player.CapturedBy, rules, c);
            prev = shot;
        }
    }

    /// <summary>Один захват: модель против пары сохранений.</summary>
    private void Captured(string name, string path, MapState a, IReadOnlyList<MapSite> sitesA, MapState b,
                          IReadOnlyList<MapSite> sitesB, string loser, string captor, EconomyRules rules,
                          Counters c)
    {
        string where = $"{name} {Path.GetFileName(path)} погибший {loser}, захватчик {captor}";
        if (sitesA.FirstOrDefault(s => s.Kind == SiteKind.Capital && Same(s.Owner, loser)) is not { } capital)
        { c.Skip(State, $"{name}: столицы игрока {loser} нет в сохранении — захват не сверяется"); return; }

        // Калитка на НЕТРОНУТОМ состоянии: до боя столица не падает.
        c.Count(Gate, CapitalCapture.Apply(a, capital, captor, rules).Refused is not null,
                $"{where}: модель применила захват к столице с живыми защитниками");

        var got = CapitalCapture.Apply(a, Fallen(capital), captor, rules, EventLoader.Load(path));
        c.Count(Win, !got.Victory, $"{where}: модель выбрала победу там, где движок пометил CAPT_BY");
        c.Count(State, Same(got.CapturedBy, captor),
                $"{where}: CAPT_BY модели {got.CapturedBy}, в сохранении {captor}");

        var after = sitesB.FirstOrDefault(s => Same(s.Id, capital.Id));
        if (after is null) c.Skip(State, $"{name}: столицы {capital.Id} нет в следующем сохранении");
        else c.Count(State, Same(after.Owner, loser) && Same(got.Capital.Owner, loser),
                     $"{where}: владелец столицы {capital.Id} стал {after.Owner}, ожидался {loser}");

        var alive = b.Stacks.Values.Where(s => Same(s.Owner, loser)).ToList();
        c.Note($"   {name}: {Path.GetFileName(path)} -> захват столицы {capital.Id}: у погибшего {loser} "
             + $"жезлов {a.Rods.Values.Count(r => Same(r.Owner, loser))}, отрядов было "
             + $"{a.Stacks.Values.Count(s => Same(s.Owner, loser))} (приказы "
             + $"{Orders(a, loser)}), уцелело {alive.Count} (приказы {Orders(b, loser)})");

        Rods(where, name, a, b, loser, got, c);
        Stacks(where, b, loser, got, c);
        Spot(where, name, a, b, capital, c);
    }

    /// <summary>Все жезлы погибшего снесены: и в ответе модели, и в следующем сохранении.</summary>
    private static void Rods(string where, string name, MapState a, MapState b, string loser,
                             CapitalCaptureOutcome got, Counters c)
    {
        var had = a.Rods.Values.Where(r => Same(r.Owner, loser)).Select(r => r.Id).ToHashSet(Cmp);
        if (had.Count == 0)
        { c.Skip(State, $"{name}: у погибшего жезлов не было — сносить нечего"); return; }
        int left = b.Rods.Values.Count(r => Same(r.Owner, loser));
        c.Count(State, left == 0 && got.RodsRazed.ToHashSet(Cmp).SetEquals(had),
                $"{where}: жезлов было {had.Count}, модель снесла {got.RodsRazed.Count}, в сохранении осталось {left}");
    }

    /// <summary>У каждого уцелевшего отряда погибшего приказ <c>L_BEZERK</c>.</summary>
    private static void Stacks(string where, MapState b, string loser, CapitalCaptureOutcome got, Counters c)
    {
        var alive = b.Stacks.Values.Where(s => Same(s.Owner, loser)).ToList();
        if (alive.Count == 0)
        { c.Skip(State, $"{where}: отрядов погибшего в следующем сохранении нет — приказ не наблюдаем"); return; }
        foreach (var s in alive)
            c.Count(State, s.Order == CapitalCapture.BerserkOrder && got.Berserk.Contains(s.Id, Cmp),
                    $"{where}: у отряда {s.Id} приказ {s.Order}, ожидался {CapitalCapture.BerserkOrder}");
    }

    /// <summary>Пятно 5×5 вокруг взятой столицы: террейн клеток не изменился (§3 п. 24).</summary>
    private static void Spot(string where, string name, MapState a, MapState b, MapSite capital, Counters c)
    {
        var moved = new List<string>();
        for (int x = capital.X - SpotRadius; x <= capital.X + SpotRadius; x++)
            for (int y = capital.Y - SpotRadius; y <= capital.Y + SpotRadius; y++)
            {
                if (!a.Contains(x, y) || !b.Contains(x, y)) continue;
                if (a.At(x, y).Terrain != b.At(x, y).Terrain)
                    moved.Add($"({x},{y}) {a.At(x, y).Terrain}->{b.At(x, y).Terrain}");
            }
        c.Count(Land, moved.Count == 0, $"{where}: пятно столицы перекрашено: {string.Join(", ", moved)}");
        if (moved.Count == 0)
            c.Note($"   {name}: пятно 5×5 вокруг взятой столицы {capital.Id} ({capital.X},{capital.Y}) — "
                 + $"террейн {a.At(capital.X, capital.Y).Terrain}, изменённых клеток 0");
    }

    // --------------------------------------------------------------- журналы: доход и ветка победы

    private void Journal(TreasuryJournal j, EconomyRules rules, Counters c)
    {
        string name = Path.GetFileName(Path.GetDirectoryName(j.Path)) + "/" + Path.GetFileName(j.Path);
        var seen = new HashSet<string>(Cmp);
        foreach (var save in j.Saves.OrderBy(s => s.T))
            foreach (var (id, player) in save.Map.Players)
                if (player.CapturedBy.Length > 0 && seen.Add(id))
                    IncomeAfter(name, j, save, id, rules, c);

        if (j.Saves.LastOrDefault(s => s.Path.Contains(WinMap, StringComparison.OrdinalIgnoreCase)) is { } m20)
            WinBranch(name, j, m20, rules, c);
    }

    /// <summary>Захваченный игрок: модель даёт ноль, журнал молчит.</summary>
    private void IncomeAfter(string name, TreasuryJournal j, SaveAt save, string loser, EconomyRules rules,
                             Counters c)
    {
        int turn = save.Map.Turn, epoch = j.EpochOf(save.T);
        var later = j.Events.OfType<IncomeEvent>()
            .Where(e => Same(e.Player, loser) && e.T is { } t && j.EpochOf(t) == epoch && e.Turn > turn)
            .ToList();
        var last = j.Events.OfType<IncomeEvent>()
            .Where(e => Same(e.Player, loser) && e.T is { } t && j.EpochOf(t) == epoch)
            .Select(e => e.Turn ?? -1).DefaultIfEmpty(-1).Max();

        c.Count(Income, Economy.DailyIncome(save.Map, loser, rules) == Bank.Zero,
                $"{name}: модель дала захваченному {loser} доход " +
                $"{Economy.DailyIncome(save.Map, loser, rules)}");
        c.Count(Income, later.Count == 0,
                $"{name}: у захваченного на {turn}-м ходу {loser} есть {later.Count} строк income позже");
        c.Note($"   {name}: {loser} захвачен к {turn}-му ходу ({Path.GetFileName(save.Path)}), " +
               (last < 0 ? "строк income в этой загрузке карты нет вовсе"
                          : $"последняя строка income — ход {last}") + $", позже хода захвата {later.Count}");
    }

    /// <summary>Ветка победы на <c>m20</c>: модель выбирает победу, журнал после того боя молчит.</summary>
    private void WinBranch(string name, TreasuryJournal j, SaveAt save, EconomyRules rules, Counters c)
    {
        var human = save.Map.Players.Values.FirstOrDefault(p => p.IsHuman);
        var capital = save.Sites.FirstOrDefault(s => s.Kind == SiteKind.Capital && human is not null
                                                     && !Same(s.Owner, human.Id));
        if (human is null || capital is null)
        { c.Skip(Win, $"{name}: в снимке {WinMap} нет человека или чужой столицы"); return; }

        var got = CapitalCapture.Apply(save.Map, Fallen(capital), human.Id, rules, EventLoader.Load(save.Path));

        c.Count(Win, got.Victory, $"{name}: модель на {WinMap} выбрала захват, а не победу: " +
                                  $"живых соперников {CapitalCapture.LiveOpponents(save.Map, human.Id, rules)}");

        int epoch = j.EpochOf(save.T);
        if (j.Events.OfType<BattleEndEvent>().FirstOrDefault(e => e.T is { } t && t > save.T && j.EpochOf(t) == epoch)
            is not { } battle)
        { c.Skip(Win, $"{name}: после снимка {WinMap} в журнале нет battle_end"); return; }
        int banks = j.Events.OfType<BankEvent>().Count(e => e.Seq > battle.Seq && j.EpochOf(e.T) == epoch);
        c.Count(Win, banks == 0, $"{name}: после battle_end seq {battle.Seq} строк bank {banks}, ожидался 0");
        c.Note($"   {name}: {WinMap} — живых соперников у {human.Id} " +
               $"{CapitalCapture.LiveOpponents(save.Map, human.Id, rules)}, событий сценария " +
               $"{EventLoader.Load(save.Path).Events.Count} (значит и L_WIN нет), " +
               $"после battle_end seq {battle.Seq} строк bank {banks} (всего bank в журнале " +
               $"{j.Events.OfType<BankEvent>().Count()})");
    }

    // ------------------------------------------------------------------------------------ мелочи

    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;


    /// <summary>Приказы отрядов игрока через дробь — чтобы в выводе было видно, что было ДО
    /// захвата (в корпусе 1) и стало после (10), а не только «сошлось».</summary>
    private static string Orders(MapState map, string owner) =>
        string.Join("/", map.Stacks.Values.Where(s => Same(s.Owner, owner)).Select(s => s.Order)
                            .Distinct().OrderBy(o => o));

    /// <summary>Столица, какой её оставил бой: защитники пали, внутри никого.</summary>
    private static MapSite Fallen(MapSite capital) => capital with { Guards = [], StackId = "" };

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
