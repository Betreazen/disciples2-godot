using System.Text.Json;
using System.Text.RegularExpressions;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Magic;
using D2.Data.Map;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

// Общее для харнессов задач 3, 4, 6 Ф7: справочники, юнит сохранения глазами правил, сохранения на оси seq
// журнала, пары сохранений без журнала, счётчики «сошлось/разошлось», прогон опыта и обучения юнита.

/// <summary>Юнит сохранения глазами правил: максимум HP и регенерация с ростом и цепочкой, порог опыта,
/// <c>UNIT_CAT</c> и способности лидера из цепочки.</summary>
public sealed record UnitView(int HpMax, int Regen, int XpNext, int Category, IReadOnlyList<int> LeaderAbilities);

/// <summary>Справочники харнессов задач 3, 4, 6.</summary>
public sealed class ProgressBook
{
    private const string EmptyId = "G000000000";

    public required PriceBook Prices { get; init; }
    public required GameRules Rules { get; init; }
    public required MapVariables Vars { get; init; }

    /// <summary><c>Gunits.UPGRADE_B</c> по типу.</summary>
    public required IReadOnlyDictionary<string, string> UpgradeBuilding { get; init; }

    /// <summary><c>GLabi.dbf</c>: врождённые способности лидера по типу юнита (задача 9). Справочник
    /// общий, потому что его спрашивают два харнесса — жезлы (<c>L_ROD</c>) и экипировка; локальный
    /// читатель <c>.dbf</c> задачи 8 снят.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<int>> InnateAbilities { get; init; }

    /// <summary><c>GleaUpg.dbf</c> целиком (391 строка, 15 лордов) — накопительный список навыков
    /// лидера задачи 11. В <c>GameRules</c> едут только id модификаторов
    /// (<c>LeaderUpgradeModifiers</c>), а харнессу нужны ещё лорд, <c>MIN_LEVEL</c> и
    /// <c>PRIORITY</c>.</summary>
    public required IReadOnlyList<LeaderUpgrade> LeaderUpgrades { get; init; }

    /// <summary><c>Gaction.CHANCE</c> и <c>GVars.MORALE_1…6</c> — таблицы вора (задача 12).</summary>
    public required ThiefRules ThiefRules { get; init; }

    /// <summary>Столбцы <c>GVars.D_*</c> — величины дипломатии (задача 13).</summary>
    public required DiplomacyRules DiplomacyRules { get; init; }

    public static ProgressBook Load(string globals)
    {
        var prices = PriceBook.Load(globals);
        return new ProgressBook
        {
            Prices = prices, Rules = RulesLoader.Load(globals), Vars = EconomyLoader.LoadVars(globals),
            UpgradeBuilding = prices.Extras.Units.Values.ToDictionary(u => u.Id, u => u.UpgradeBuildingId,
                                                                      StringComparer.OrdinalIgnoreCase),
            InnateAbilities = EquipLoader.LoadInnateAbilities(globals),
            LeaderUpgrades = ModifierLoader.LoadLeaderUpgrades(globals),
            ThiefRules = EconomyLoader.LoadThieves(globals),
            DiplomacyRules = EconomyLoader.LoadDiplomacy(globals),
        };
    }

    /// <summary>Глобальный id и уровень реализации: строка <c>Gunits</c> как есть, порождённая (<c>UG</c>) —
    /// разбором <see cref="UnitGenerator"/>.</summary>
    public bool TryGlobal(string implId, int level, out string globalId, out int globalLevel)
    {
        globalId = implId;
        globalLevel = level;
        if (Rules.Units.ContainsKey(implId)) return true;
        return Rules.Generator is not null && Rules.Generator.TryDecode(implId, out globalId, out globalLevel);
    }

    /// <summary>Тип, выращенный до уровня (<see cref="UnitGrowth.Of"/>); null — типа нет в справочниках.</summary>
    public GrownUnit? Grown(string implId, int level)
    {
        if (!TryGlobal(implId, level, out var id, out int lv) || !Rules.Units.TryGetValue(id, out var type)
            || !Prices.Extras.Units.TryGetValue(id, out var ext)) return null;
        return UnitGrowth.Of(type, ext.DynUpgLevel, Math.Max(lv, type.Level), Upgrade(ext.DynUpg1Id),
                             Upgrade(ext.DynUpg2Id), ext.Regen, ext.Move);
    }

    public int XpNextOf(string implId, int level) => Grown(implId, level)?.Type.XpNext ?? 0;

    public int? CategoryOf(string implId) =>
        TryGlobal(implId, 0, out var id, out _) && Prices.Extras.Units.TryGetValue(id, out var e) ? e.Category : null;

    public UnitCosts? CostsOf(string implId) =>
        TryGlobal(implId, 0, out var id, out _) && Prices.Treasury.Units.TryGetValue(id, out var c) ? c : null;

    /// <summary><c>Gunits.LEADER_CAT</c> типа (<c>LleadC</c>; 5 — вор). null — типа нет в справочниках.</summary>
    public int? LeaderCategoryOf(string implId) =>
        TryGlobal(implId, 0, out var id, out _) && Prices.Extras.Units.TryGetValue(id, out var e)
            ? e.LeaderCategory : null;

    /// <summary>
    /// <c>NEGOTIATE</c> лидера, ВЫРАЩЕННЫЙ по уровню: база <c>Gunits</c> плюс шаги
    /// <c>GDynUpgr.NEGOTIATE</c> (<see cref="UnitGrowth.StepsFor"/>) — тем же способом, каким
    /// <see cref="PriceBook.XpNext"/> растит порог опыта. Это сопротивление отряда порогу вора
    /// (<see cref="Thieves.StackResistance"/>). null — типа нет в справочниках.
    /// </summary>
    public int? NegotiateOf(string implId, int level)
    {
        if (!TryGlobal(implId, level, out var id, out int lv) || !Prices.Extras.Units.TryGetValue(id, out var ext)
            || !Rules.Units.TryGetValue(id, out var type)) return null;
        int baseLevel = type.Level;
        if (lv <= baseLevel) return ext.Negotiate;
        var steps = UnitGrowth.StepsFor(baseLevel, ext.DynUpgLevel, lv);
        return ext.Negotiate + steps.First * Step(ext.DynUpg1Id) + steps.Second * Step(ext.DynUpg2Id);
    }

    private int Step(string id) =>
        id.Length > 0 && Prices.Extras.DynUpgrades.TryGetValue(id, out var u) ? u.Negotiate : 0;

    /// <summary>Юнит сохранения: рост по уровню, затем цепочка <c>MODIF_ID</c> (<see cref="EffectiveStats.Of"/>).</summary>
    public UnitView? View(MapUnit u, out string reason)
    {
        reason = "";
        if (Grown(u.TypeId, u.Level) is not { } grown || CategoryOf(u.TypeId) is not { } category)
        { reason = $"тип {u.TypeId} не найден в Gunits"; return null; }
        var chain = new List<Modifier>();
        foreach (var id in u.Modifiers.Where(m => m.Length > 0 && !m.Equals(EmptyId, StringComparison.OrdinalIgnoreCase)))
        {
            if (Rules.Modifiers is null || !Rules.Modifiers.TryGetValue(id, out var m))
            { reason = $"модификатора {id} нет в Gmodif"; return null; }
            chain.Add(m);
        }
        var stats = EffectiveStats.Of(grown.Type, chain, regeneration: grown.Regeneration);
        return new UnitView(stats.HitPoints, stats.Regeneration, grown.Type.XpNext, category, stats.LeaderAbilities);
    }

    /// <summary>Предмет по экземпляру <c>IM</c> сохранения: <c>MidItem.ITEM_TYPE</c> -> строка
    /// <c>GItem</c>. null — экземпляра нет в снимке или тип не опознан.</summary>
    public Item? ItemOf(MapState map, string instanceId) =>
        map.ItemTypes.TryGetValue(instanceId, out var type) && Prices.Items.TryGetValue(type, out var item)
            ? item : null;

    /// <summary>Способности лидера отряда по сохранению — <b>все три источника задачи 9</b>
    /// (<see cref="LeaderAbilities.Split"/>): <c>GLabi</c> типа, навыки уровня (цепочка
    /// <c>MODIF_ID</c> за вычетом надетого) и <c>MOD_EQUIP</c> надетых предметов.</summary>
    /// <param name="excludeSlot">Слот проверяемого предмета: его способность в источник «предметы»
    /// не идёт — гейт спрашивают до надевания. <c>-1</c> — считать всё надетое (готовое состояние).</param>
    public AbilitySources? Abilities(MapState map, MapStack stack, int excludeSlot, out string reason)
    {
        reason = "";
        if (!map.Units.TryGetValue(stack.LeaderId, out var leader))
        { reason = $"лидера {stack.LeaderId} нет в сохранении"; return null; }
        if (Rules.Modifiers is null) { reason = "справочник Gmodif не загружен"; return null; }

        var slots = Equipment.Slots(stack);
        List<Item> equipped = [], granting = [];
        for (int s = 0; s < slots.Count; s++)
        {
            if (slots[s].Length == 0) continue;
            if (ItemOf(map, slots[s]) is not { } item)
            { reason = $"тип экземпляра {slots[s]} не опознан"; return null; }
            equipped.Add(item);
            if (s != excludeSlot) granting.Add(item);
        }

        var chain = leader.Modifiers
            .Where(m => m.Length > 0 && !m.Equals(EmptyId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (chain.FirstOrDefault(id => !Rules.Modifiers.ContainsKey(id)) is { } missing)
        { reason = $"модификатора {missing} нет в Gmodif"; return null; }

        TryGlobal(leader.TypeId, 0, out var typeId, out _);
        return LeaderAbilities.Split(typeId, InnateAbilities, chain, equipped, granting, Rules.Modifiers);
    }

    private DynUpgrade? Upgrade(string id) =>
        id.Length > 0 && Prices.Extras.DynUpgrades.TryGetValue(id, out var u) ? u : null;
}

/// <summary>Сохранение на оси <c>seq</c> журнала: всё с <c>seq</c> меньше <see cref="Cut"/> в него вошло.</summary>
public sealed record SavePoint(SaveAt Save, long Cut, int Epoch);

/// <summary>
/// Где на оси <c>seq</c> лежит содержимое сохранения драйвера.
/// <para><b>Правило — измерено, ранг 1.</b> Автосохранение снимается в конце хода человека, а файл ложится на диск
/// когда придётся (план Ф7 §1.4 «отстаёт от дневного тика»): в <c>m4-temple</c> — уже после следующего тика человека
/// (сохранение <c>090</c> легло за <c>income turn 3</c>, а Ангел в нём 1/225 — до тика, run2 §0.1), во второй большой
/// партии — посреди его хода, ещё до дохода первого ИИ (<c>t23-026</c>: обучение того же хода в файле уже есть).
/// Ход человека открывает его строка <c>income</c> с <c>turn == CUR_TURN</c> сохранения, а закрывает — первая после неё
/// строка <c>income</c> другого игрока (в <c>m4</c> нейтрал ходит после человека, в больших партиях ИИ — перед ним в том же
/// дне). Но и после конца хода до этой строки бывают события: во второй большой партии бой с отрядом человека кончился
/// через 0,7 с после записи <c>t23-026</c> и за 1,3 с до дохода ИИ, и его опыта в файле нет. Поэтому
/// <see cref="SavePoint.Cut"/> — меньшее из двух: <c>seq</c> той строки <c>income</c> и <c>seq</c> первого события позже
/// времени файла.</para>
/// </summary>
public static class JournalSaves
{
    public static IReadOnlyList<SavePoint> Of(TreasuryJournal j)
    {
        var incomes = j.Events.OfType<IncomeEvent>().Where(e => e.T is not null).OrderBy(e => e.Seq).ToList();
        var points = new List<SavePoint>();
        foreach (var s in j.Saves)
            if (CutOf(j, incomes, s) is { } cut) points.Add(new SavePoint(s, cut, j.EpochOf(s.T)));
        return points.OrderBy(p => p.Cut).ToList();
    }

    private static long? CutOf(TreasuryJournal j, List<IncomeEvent> incomes, SaveAt save)
    {
        var human = save.Map.Players.Values.FirstOrDefault(p => p.IsHuman)?.Id;
        if (human is null) return null;
        if (save.Seq is { } exact) return exact;   // схема 6.0: срез файла — seq строки save (TreasuryJournal.OnSaveAxis)
        int epoch = j.EpochOf(save.T);
        var open = incomes.LastOrDefault(h => h.T <= save.T && Same(h.Player, human) && h.Turn == save.Map.Turn
                                              && j.EpochOf(h.T!.Value) == epoch);
        if (open is null || incomes.FirstOrDefault(e => e.Seq > open.Seq && !Same(e.Player, human)) is not { } close) return null;
        long afterFile = j.Events.Where(e => e.Seq > open.Seq && TreasuryJournal.TimeOf(e) > save.T).Select(e => e.Seq)
                          .DefaultIfEmpty(long.MaxValue).Min();
        return Math.Min(close.Seq, afterFile);
    }

    /// <summary>Вторая строка <c>income</c> ИИ идёт через ~5 <c>seq</c> после первой, ежедневные строки <c>xp</c> — сразу
    /// за ними; 20 — запас. Наблюдение по корпусу (ранг 1 по корпусу; то же окно у харнесса казны задачи 1,
    /// <c>TreasuryAudit.AiIncome</c>), разбора <c>CEffectPlayerTurn</c> для ИИ нет.</summary>
    public const long AiIncomeRepeatSeq = 20;

    /// <summary>Строка <c>xp</c>, пришедшая с дневным тиком игрока (не дальше <see cref="AiIncomeRepeatSeq"/> от его
    /// <c>income</c> в окне), а не боем и не тренером. Источник не разобран (ранг 3): в просмотренных окнах первой и
    /// второй большой партии величины 1/2/5/10/11 при <c>XP_NEXT</c> 80/130/275/500/550, в сохранения такие строки
    /// попадают не каждый день.</summary>
    public static bool IsDailyXp(IEnumerable<CorpusEvent> window, XpEvent x) =>
        window.OfType<IncomeEvent>().Any(i => Same(i.Player, x.Player) && x.Seq > i.Seq && x.Seq - i.Seq <= AiIncomeRepeatSeq);

    public static SavePoint? Before(IReadOnlyList<SavePoint> points, long seq, int epoch) =>
        points.Where(p => p.Cut < seq && p.Epoch == epoch).MaxBy(p => p.Cut);

    public static SavePoint? After(IReadOnlyList<SavePoint> points, long seq, int epoch) =>
        points.Where(p => p.Cut > seq && p.Epoch == epoch).MinBy(p => p.Cut);

    /// <summary>Соседние сохранения одной загрузки карты с разными срезами.</summary>
    public static IEnumerable<(SavePoint A, SavePoint B)> Pairs(IReadOnlyList<SavePoint> points)
    {
        for (int i = 0; i + 1 < points.Count; i++)
            if (points[i].Epoch == points[i + 1].Epoch && points[i].Cut < points[i + 1].Cut)
                yield return (points[i], points[i + 1]);
    }

    /// <summary>События журнала строго между срезами, по <c>seq</c>.</summary>
    public static List<CorpusEvent> Between(TreasuryJournal j, long from, long to) =>
        j.Events.Where(e => e.Seq > from && e.Seq < to).OrderBy(e => e.Seq).ToList();

    public static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Сохранения корпуса, которых не объявляет ни один манифест, — журнала у них нет (или он без строки манифеста):
/// <c>m5-regen</c>, <c>m9-xpstop</c>, <c>sites</c> (журнал прогона 1 не сохранился), <c>m16-regen</c> и прогон 3.
/// Пара — соседние по счётчику файлы одной карты одного каталога, у второго <c>CUR_TURN</c> на единицу больше
/// (между ними ровно один дневной тик, план Ф7 §1.4). Каталоги на «_» не берутся.
/// </summary>
public static class LooseSaves
{
    private static readonly Regex Name = new(@"^(?<map>.+)-t\d+-(?<n>\d+)\.sg$", RegexOptions.IgnoreCase);

    /// <param name="sameDay">Брать и пары одного дня — для действий без дневного тика (найм у наёмника).</param>
    public static IEnumerable<(SaveAt A, SaveAt B)> Pairs(bool sameDay = false)
    {
        var bound = Bound();
        foreach (var dir in Directory.EnumerateDirectories(CorpusFiles.Dir).Where(d => !Path.GetFileName(d).StartsWith('_')))
        {
            var groups = Directory.EnumerateFiles(dir, "*.sg")
                .Where(f => !bound.Contains(Path.GetFullPath(f)))
                .Select(f => (Path: f, Match: Name.Match(Path.GetFileName(f)))).Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups["map"].Value, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var files = g.OrderBy(x => int.Parse(x.Match.Groups["n"].Value)).ToList();
                for (int i = 0; i + 1 < files.Count; i++)
                {
                    if (int.Parse(files[i + 1].Match.Groups["n"].Value) != int.Parse(files[i].Match.Groups["n"].Value) + 1) continue;
                    var (a, b) = (new SaveAt(0, files[i].Path), new SaveAt(0, files[i + 1].Path));
                    if ((b.Map.Turn == a.Map.Turn + 1 || sameDay && b.Map.Turn == a.Map.Turn) && SameBuild(a, b)) yield return (a, b);
                }
            }
        }
    }

    /// <summary>Одна сборка карты: объект с тем же id в обоих сохранениях того же вида и на той же клетке (объекты не
    /// двигаются). Прогон <c>sites</c> — две сборки подряд: в <c>t01-003…005</c> <c>S143SI0003</c> — наёмник в 5,13, в
    /// <c>t01-006…t04-009</c> — башня мага в 4,11, и пара 005 → 006 наблюдением не является.</summary>
    private static bool SameBuild(SaveAt a, SaveAt b) =>
        a.Sites.All(x => b.Sites.FirstOrDefault(y => JournalSaves.Same(x.Id, y.Id)) is not { } y
                         || y.Kind == x.Kind && y.X == x.X && y.Y == x.Y);

    /// <summary>Полные пути всех сохранений, объявленных манифестами каталогов корпуса.</summary>
    private static HashSet<string> Bound()
    {
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in Directory.EnumerateDirectories(CorpusFiles.Dir)
                     .Select(d => Path.Combine(d, "manifest.json")).Where(File.Exists))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.TryGetProperty("saves", out var saves))
                    foreach (var s in saves.EnumerateArray())
                        bound.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest)!,
                            s.ValueKind == JsonValueKind.String ? s.GetString()! : s.GetProperty("file").GetString()!)));
        }
        return bound;
    }
}

/// <summary>
/// Цепочки сохранений корпуса: файлы одной карты одного каталога по возрастанию счётчика, разрезанные там, где
/// ход не вырос (перезагрузка карты — второй прогон той же карты). Каталоги на «_» не берутся, одиночные
/// сохранения — тоже: цепочка без пары сохранений наблюдением не является.
/// <para>Общее место задач 7 (рост земли) и 8 (жезлы): обе смотрят на то, что изменилось между соседними
/// сохранениями одной партии.</para>
/// </summary>
public static class SaveChains
{
    private static readonly Regex SaveName =
        new(@"^(?<map>.+)-t(?<t>\d+)-(?<n>\d+)\.sg$", RegexOptions.IgnoreCase);

    /// <param name="keepSameTurn">Оставлять в цепочке и соседей ОДНОГО хода. По умолчанию такой
    /// сосед считается перезагрузкой карты и цепочку разрезает (так мерят задачи 7 и 8: рост земли и
    /// жезлы наблюдаются на дневном тике). Захват столицы, наоборот, случается внутри хода — оба его
    /// окна корпуса односуточные (<c>m22-capture3</c> <c>t01-057 → 058</c> и второй большой партии
    /// <c>t21-023 → 024</c>), и без этого признака их не видно вовсе (задача 14).</param>
    public static IEnumerable<(string Name, IReadOnlyList<string> Files)> All(bool keepSameTurn = false)
    {
        foreach (var dir in Directory.EnumerateDirectories(CorpusFiles.Dir)
                     .Where(d => !Path.GetFileName(d).StartsWith('_'))
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            var groups = Directory.EnumerateFiles(dir, "*.sg")
                .Select(f => (Path: f, M: SaveName.Match(Path.GetFileName(f)))).Where(x => x.M.Success)
                .GroupBy(x => x.M.Groups["map"].Value, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in groups)
            {
                var chain = new List<string>();
                int prev = 0, part = 0;
                foreach (var f in g.OrderBy(x => int.Parse(x.M.Groups["n"].Value)))
                {
                    int t = int.Parse(f.M.Groups["t"].Value);
                    // Сосед того же хода в ОДНОЙ загрузке карты (журнал зонда 6.5+, AiCorpus) — не перезагрузка, а ещё
                    // одно сохранение того же дня (быстрое посреди хода, автосохранение в конце): дневному тику
                    // соответствует последнее, им и заменяется хвост. 20260919-f7: у f7-m6-rods четыре сохранения дня 2,
                    // между ними три жезла, — разрез по ним не оставлял ни одного дня установки.
                    if (!keepSameTurn && t == prev && chain.Count > 0 && SameLoad(chain[^1], f.Path))
                    {
                        chain[^1] = f.Path;
                        continue;
                    }
                    bool reload = keepSameTurn ? t < prev : t <= prev;
                    if (reload && chain.Count > 1)
                        yield return ($"{Path.GetFileName(dir)}/{g.Key}#{++part}", chain);
                    if (reload) chain = [];
                    chain.Add(f.Path);
                    prev = t;
                }
                if (chain.Count > 1) yield return ($"{Path.GetFileName(dir)}/{g.Key}#{++part}", chain);
            }
        }
    }

    /// <summary>Копия сохранения → сессия журнала (загрузка карты), к строке <c>save</c> которой она привязана.</summary>
    private static readonly Lazy<Dictionary<string, AiSession>> Loads = new(() =>
        AiCorpus.Sessions.SelectMany(s => s.Saves.Where(x => x.File is not null).Select(x => (File: Path.GetFullPath(x.File!), s)))
                         .ToDictionary(x => x.File, x => x.s, StringComparer.OrdinalIgnoreCase));

    /// <summary>Оба сохранения привязаны к одной загрузке карты. Нет привязки (журнал до 6.5) — неизвестно, значит нет.</summary>
    private static bool SameLoad(string a, string b) =>
        Loads.Value.TryGetValue(Path.GetFullPath(a), out var x) && Loads.Value.TryGetValue(Path.GetFullPath(b), out var y) && x == y;
}

/// <summary>Счётчики харнесса: <c>имя сошлось=N разошлось=M</c>, пропуски с причиной, образцы расхождений и
/// дословные строки известных значений.</summary>
public sealed class Counters
{
    private const int MaxSamplesPerName = 12;
    private readonly SortedDictionary<string, (int Ok, int Bad)> _rows = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _skips = new(StringComparer.Ordinal);
    private readonly List<string> _bad = [];
    private readonly List<string> _notes = [];

    public void Count(string name, bool ok, string detail)
    {
        var (o, b) = _rows.GetValueOrDefault(name);
        _rows[name] = ok ? (o + 1, b) : (o, b + 1);
        if (!ok && b < MaxSamplesPerName) _bad.Add($"{name}: {detail}");
    }

    public void Skip(string name, string reason) =>
        _skips[$"{name}: {reason}"] = _skips.GetValueOrDefault($"{name}: {reason}") + 1;

    public void Note(string line) => _notes.Add(line);

    public (int Ok, int Bad) Of(string name) => _rows.GetValueOrDefault(name);

    public void Print(ITestOutputHelper o, string title)
    {
        o.WriteLine(title);
        foreach (var n in _notes) o.WriteLine(n);
        foreach (var (name, (ok, bad)) in _rows) o.WriteLine($" {name,-34} сошлось={ok,-6} разошлось={bad}");
        foreach (var (reason, n) in _skips) o.WriteLine($"  пропуск {reason} — {n}");
        foreach (var s in _bad) o.WriteLine($"  РАСХОЖДЕНИЕ {s}");
    }

    /// <summary>Приёмка: у каждого счётчика хоть одно сравнение и ни одного расхождения.</summary>
    public void Require(params string[] names)
    {
        foreach (var name in names)
        {
            var (ok, bad) = Of(name);
            Assert.True(ok + bad > 0, $"{name}: ноль сравнений — сверка не состоялась");
            Assert.True(bad == 0, $"{name}: разошлось {bad} — см. РАСХОЖДЕНИЕ выше");
        }
    }

    /// <summary>Приёмка счётчиков, чьей игровой ситуации в корпусе может не быть (корпус 09.09–18.09 потерян, а
    /// синтетические карты плана ещё не пересняты). Расхождение валит тест, как <see cref="Require"/>; ноль
    /// сравнений — не тихое ослабление, а явный пропуск, в причине которого названо, <b>что сыграть</b>, чтобы
    /// случай появился. Вызывать последним — после <see cref="Require"/> по счётчикам, у которых случаи есть.</summary>
    public void RequireOrSkip(params (string Name, string Situation)[] rows)
    {
        foreach (var (name, _) in rows)
            Assert.True(Of(name).Bad == 0, $"{name}: разошлось {Of(name).Bad} — см. РАСХОЖДЕНИЕ выше");
        var missing = rows.Where(r => Of(r.Name) is (0, 0)).Select(r => $"{r.Name}: нет случая — {r.Situation}").ToList();
        Xunit.Skip.If(missing.Count > 0, string.Join("; ", missing));
    }
}

/// <summary>
/// Чит <c>jump</c> в журнале — по нажатиям клавиш (<c>input</c>, <c>WM_KEYDOWN</c>). Ранг 1: big1 §0.2, big2 §0.2 — чит
/// набирается текстом и виден дословно. Набор воспроизводится как ввод строки: Backspace стирает букву, Enter или Esc
/// заканчивают строку (в первой большой партии один раз набрано «JUMO, Backspace, P, Enter»). Строка, равная
/// <c>JUMP</c>, — чит; отменённый Esc тоже считается (отменил ли Esc действие, из журнала не видно). В корпусе чит есть
/// только в двух больших партиях (11 и 16 раз). Что именно он меняет в опыте, не разбиралось — окна с ним
/// исключаются целиком.
/// </summary>
public static class Cheats
{
    private const long WmKeyDown = 256, Backspace = 8, Enter = 13, Escape = 27;

    /// <summary><c>seq</c> клавиши, закончившей каждую строку <c>JUMP</c>.</summary>
    public static IReadOnlyList<long> JumpSeqs(TreasuryJournal j)
    {
        var seqs = new List<long>();
        var line = new System.Text.StringBuilder();
        foreach (var k in j.Events.OfType<InputEvent>().Where(k => k.Kind == "key" && k.Msg == WmKeyDown).OrderBy(k => k.Seq))
        {
            if (k.Vk is >= 65 and <= 90) line.Append((char)k.Vk);
            else if (k.Vk == Backspace && line.Length > 0) line.Length--;
            else if (k.Vk is Enter or Escape)
            {
                if (line.ToString() == "JUMP") seqs.Add(k.Seq);
                line.Clear();
            }
        }
        return seqs;
    }

    public static bool Between(IReadOnlyList<long> seqs, long from, long to) => seqs.Any(s => s > from && s < to);
}

/// <summary>Опыт и обучение юнита между двумя сохранениями: к опыту сохранения прибавляется каждая строка
/// <c>xp</c> журнала с повышением (<see cref="UnitUpgrade.Apply"/>; дневная строка ИИ <c>0x431d07</c> —
/// <see cref="UnitUpgrade.ApplyDaily"/>), обучение считается <see cref="SiteTrade.Train"/> от казны <c>bank.before</c>.
/// Отказ модели «ни одного очка» — не расхождение: движок в этом случае тоже вернул <c>ret = true</c> и ничего не
/// списал (probe65, rastlennaya seq 13974939: <c>bank</c> 1 → 1, опыт в сохранении без обучения), поэтому строка
/// пропускается и сверка идёт дальше — раньше харнесс здесь обрывал окно и терял опыт боя после визита.</summary>
public static class UnitLedger
{
    /// <summary>Дневной чит опыта ИИ — адрес возврата строки <c>xp</c> (<see cref="Experience.AiDailyXp"/>).</summary>
    public const string DailyXpRetAddr = "0x431d07";

    /// <param name="before">Сохранение до окна: порождённая реализация «тот же тип уровнем выше» считается созданной,
    /// если в нём есть другой юнит этого типа и уровня (ранг 2, как <c>NextTierCreated</c> у лидера).</param>
    public static UnitProgress Run(ProgressBook book, TreasuryJournal j, UnitProgress start, string unitId,
                                   IEnumerable<CorpusEvent> events, IReadOnlySet<string> built, out string? refused,
                                   SaveAt? before = null)
    {
        refused = null;
        var p = start;
        foreach (var e in events)
        {
            if (e is XpEvent { RetAddr: DailyXpRetAddr } d && JournalSaves.Same(d.Unit, unitId)) p = Daily(book, p, d, built, before, unitId);
            else if (e is XpEvent x && JournalSaves.Same(x.Unit, unitId))
                p = UnitUpgrade.ApplyBattle(p, (int)x.Amount, book.XpNextOf, book.Rules.Units, book.UpgradeBuilding, built,
                                            RaceAllows(book, before, unitId, p)).Unit;
            if (e is not SiteTrainEvent { Ret: true } t || !JournalSaves.Same(t.Unit, unitId)) continue;
            if (j.NestedBank(t.Seq, null) is not { } bank || book.CostsOf(p.TypeId) is not { } costs)
            { refused = "у обучения нет строки bank до следующей причины или цены юнита"; return p; }
            int xpNext = book.XpNextOf(p.TypeId, p.Level);
            // Тренер по команде ИИ — с читом SUGG_LVL ≥ 10 (SiteTrade.TrainAi, 0x61eb4a): карта 16 (20261018b-port-synth), сложная.
            var trade = AiTrainer(before, unitId) is { } cheat
                ? SiteTrade.TrainAi(p.Xp, xpNext, costs.Training, bank.Before, cheat.High, cheat.Difficulty)
                : SiteTrade.Train(p.Xp, xpNext, costs.Training, bank.Before);
            if (trade.Done) p = p with { Xp = trade.State };
        }
        return p;
    }

    /// <summary>Строка дневного чита опыта ИИ (<c>0x431d07</c>) — как <c>GameSession.AiXpCheat</c>: павшему прибавки нет (<c>0x5d7412</c>,
    /// xp_after = xp_before при hp 0; ai-q09 27.09, S143UN001a 94 → 94), но проверка повышения <c>0x4318ad</c> идёт и у него (карта 23:
    /// S143UN0082 G000UU0063 на 500 = XP_NEXT с hp 0 — G000UU0064 ур. 3 опыт 0, когда цель открылась); категория без опыта (<c>0x5d8f84</c>:
    /// вор <c>L_NOBLE</c> и др.) — опыт зажат в [0, XP_NEXT] без повышения, хотя у вора-лидера строка с leader = true (карта 23: S143UN008f
    /// G000UU0048 119 + 7 → 120 и дальше 120 → 120); лидер — свой путь цели (<see cref="UnitUpgrade.ApplyDailyLeader"/>).</summary>
    private static UnitProgress Daily(ProgressBook book, UnitProgress p, XpEvent x, IReadOnlySet<string> built, SaveAt? before, string unitId)
    {
        int amount = (int)x.Amount;
        bool alive = x.Hp is not <= 0;
        if (book.CategoryOf(p.TypeId) is { } category && !Experience.GainsXp(category))
            return alive ? p with { Xp = Experience.LeaderXpAfter(p.Xp, amount, book.XpNextOf(p.TypeId, p.Level), nextTierCreated: false) } : p;
        if (x.Leader == true) return UnitUpgrade.ApplyDailyLeader(p, amount, book.XpNextOf, (int)(x.MaxLeader ?? 0), alive);
        return UnitUpgrade.ApplyDaily(p, amount, book.XpNextOf, book.Rules.Units, book.UpgradeBuilding, built,
                                      RaceAllows(book, before, unitId, p), alive).Unit;
    }

    /// <summary>Хозяин юнита — ИИ (группа юнита в сохранении до окна): тогда обучение идёт командой ИИ, и у него <c>SUGG_LVL</c> и
    /// сложность партии (<see cref="SiteTrade.TrainAi"/>). Человек или юнит не найден — null.</summary>
    public static (bool High, int Difficulty)? AiTrainer(SaveAt? before, string unitId)
    {
        if (before is null) return null;
        var map = before.Map;
        string? owner = map.Stacks.Values.FirstOrDefault(x => x.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner
                        ?? map.Sites.FirstOrDefault(x => x.Guards.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner;
        if (owner is null || !map.Players.TryGetValue(owner, out var player) || player.IsHuman) return null;
        return (map.Limits?.SuggestedLevel >= D2.Data.Play.AiStackUnits.HighSuggestedLevel, map.Limits?.DifficultyGame ?? 1);
    }

    /// <summary>Вход цели повышения <c>0x5d7fe7</c> (<see cref="UpgradeRace.Allows"/>) по сохранению до окна: хозяин группы юнита
    /// (отряд или гарнизон), подраса типа (<c>Gunits.SUBRACE</c>), игроки подрас и столицы. Нет сохранения или юнит в нём не найден —
    /// разрешено (как у порта: дневной опыт идёт только юнитам групп игрока).</summary>
    private static bool RaceAllows(ProgressBook book, SaveAt? before, string unitId, UnitProgress p)
    {
        if (before is null || !book.TryGlobal(p.TypeId, p.Level, out var global, out _)
            || !book.Prices.Extras.Units.TryGetValue(global, out var extra)) return true;
        var map = before.Map;
        string? owner = map.Stacks.Values.FirstOrDefault(x => x.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner
                        ?? map.Sites.FirstOrDefault(x => x.Guards.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner;
        if (owner is null) return true;
        var subraceOwners = SubraceOwners.GetOrAdd(System.IO.Path.GetFullPath(before.Path),
                                                   path => EventLoader.Load(path).SubraceOwners);
        return UpgradeRace.Allows(map, subraceOwners, owner, extra.Subrace);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> SubraceOwners =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Show(UnitProgress p) => $"{p.TypeId} ур. {p.Level} опыт {p.Xp}{(p.DynLevel ? " DYNLEVEL" : "")}";
}
