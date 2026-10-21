using System.Text.Json;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Magic;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;
using KSg = Kaitai.Sg;

namespace D2.Rules.Tests;

/// <summary>Справочники, по которым харнесс казны считает цены.</summary>
public sealed record PriceBook(
    EconomyRules Economy, TreasuryRules Treasury, IReadOnlyDictionary<string, Item> Items,
    IReadOnlyDictionary<string, Spell> Spells, RulesLoader.RulesExtras Extras, AiCheatRules AiCheats)
{
    public static PriceBook Load(string globals) => new(EconomyLoader.Load(globals),
        EconomyLoader.LoadTreasury(globals), ItemLoader.Load(globals), SpellLoader.Load(globals),
        RulesLoader.LoadExtras(globals), EconomyLoader.LoadAiCheats(globals));

    /// <summary>Порог опыта юнита типа <paramref name="type"/> на уровне <paramref name="level"/>:
    /// <c>XP_NEXT</c> плюс шаги <c>GDynUpgr.XP_NEXT</c> (<see cref="UnitGrowth.StepsFor"/>, ранг 1).
    /// null — типа нет в справочниках.</summary>
    public int? XpNext(string type, int level)
    {
        if (!Treasury.Units.TryGetValue(type, out var costs) || !Economy.Hire.TryGetValue(type, out var hire)
            || !Extras.Units.TryGetValue(type, out var ext)) return null;
        if (level <= hire.Level) return costs.XpNext;
        var steps = UnitGrowth.StepsFor(hire.Level, ext.DynUpgLevel, level);
        return costs.XpNext + steps.First * Upgrade(ext.DynUpg1Id) + steps.Second * Upgrade(ext.DynUpg2Id);
    }

    private int Upgrade(string id) => id.Length > 0 && Extras.DynUpgrades.TryGetValue(id, out var u) ? u.XpNext : 0;

    /// <summary>Цена найма юнита типа <paramref name="type"/> уровня <paramref name="level"/>
    /// (<see cref="D2.Rules.Map.Economy.HireCost(UnitHire, int, int, DynUpgrade?, DynUpgrade?)"/>,
    /// задача 26 Ф7). null — типа нет в справочниках.</summary>
    public Bank? HirePrice(string type, int level) =>
        Economy.Hire.TryGetValue(type, out var hire) && Extras.Units.TryGetValue(type, out var ext)
            ? D2.Rules.Map.Economy.HireCost(hire, level, ext.DynUpgLevel, Row(ext.DynUpg1Id), Row(ext.DynUpg2Id))
            : null;

    private DynUpgrade? Row(string id) =>
        id.Length > 0 && Extras.DynUpgrades.TryGetValue(id, out var u) ? u : null;
}

/// <summary>Сохранение драйвера на оси <c>t</c> журнала. Карта, объекты и типы предметов читаются
/// лениво: из 30 снимков партии нужны единицы.</summary>
public sealed class SaveAt(long t, string path, long? seq = null)
{
    public long T { get; } = t;
    public string Path { get; } = path;

    /// <summary><c>seq</c> строки <c>save</c> этого файла (схема 6.0): всё с меньшим <c>seq</c> в файле уже есть.
    /// null — журнал без строк <c>save</c> или пары не построены; тогда ось — <see cref="T"/> по <c>mtime</c>.</summary>
    public long? Seq { get; } = seq;
    private MapState? _map;
    private IReadOnlyList<MapSite>? _sites;
    private IReadOnlyDictionary<string, string>? _items;

    public MapState Map => _map ??= SaveMaps.Map(Path);

    public IReadOnlyList<MapSite> Sites => _sites ??= SaveMaps.Sites(Path);

    /// <summary>Экземпляр предмета (<c>IM</c>) -> тип (<c>IG</c>).</summary>
    public IReadOnlyDictionary<string, string> ItemTypes => _items ??= SgFile.Open(Path).Raw.Objects
        .Select(o => o.Data).OfType<KSg.MidItem>()
        .ToDictionary(i => i.ItemId.String, i => i.ItemType.String, StringComparer.OrdinalIgnoreCase);

    private (Difficulty Difficulty, int SuggestedLevel)? _info;

    /// <summary>Сложность партии — <c>ScenarioInfo.DIFFGAME</c> (<c>CScenarioInfo +0x40</c>); в <see cref="MapState"/>
    /// её нет. Источник правил сложности харнессов — это поле, а не догадка по журналу.</summary>
    public Difficulty Difficulty => Info.Difficulty;

    /// <summary><c>ScenarioInfo.SUGG_LVL</c> (<c>CScenarioInfo +0x70</c>) — порог бонуса опыта ИИ
    /// (<see cref="Experience.AiXpPercent"/>).</summary>
    public int SuggestedLevel => Info.SuggestedLevel;

    private (Difficulty Difficulty, int SuggestedLevel) Info => _info ??= ReadInfo();

    private (Difficulty, int) ReadInfo()
    {
        var info = SgFile.Open(Path).Raw.Objects.Select(o => o.Data).OfType<KSg.ScenarioInfo>().Single();
        return ((Difficulty)(int)info.Diffgame.Value, (int)info.SuggLvl.Value);
    }
}

/// <summary>
/// Журнал пассивного прогона в части казны: события казны и их возможных причин в порядке файла,
/// загрузки карт и сохранения драйвера на той же оси времени.
/// <para><b>Время сохранения</b> — <c>mtime</c> файла от <c>manifest.started_at</c> в миллисекундах:
/// тот же приём, что во всех разборах прогонов Ф7 (точность — секунды).</para>
/// <para><b>Загрузка карты</b> (эпоха) — смена потока зонда: каждая загрузка идёт в новом игровом потоке, и первая
/// строка с <c>tid</c>, не равным ни <c>tid</c> первой строки журнала (поток интерфейса), ни текущему игровому,
/// открывает эпоху; её начало — <c>t</c> первого события со временем после смены. Ранг 1 по корпусу: на 12 журналах
/// с казной до 16.09 эпохи по <c>tid</c> и по прежнему признаку (<c>income</c>, у которой ход не вырос) совпадают
/// один в один; в <c>20260916-synth</c> прежний признак слил загрузки разных карт с разными id игроков
/// (passive-003: 26 против 32 — ai-q52 seq 10644984, ai-q25 10769548, ai-q57 11981319, ai-q02 12159354,
/// последняя 19547263; passive-002: 9 против 11). Снимок берётся только из той же эпохи: префикс id объектов
/// (<c>S143</c>) у всех синтетических карт общий, и <c>S143PL0001</c> на соседней карте — другой игрок с другим
/// лордом.</para>
/// </summary>
public sealed class TreasuryJournal
{
    public required string Path { get; init; }
    public required IReadOnlyList<CorpusEvent> Events { get; init; }

    /// <summary>Строки начала хода — <c>spell_expire</c>, <c>regen</c>, <c>hp_change</c>, <c>income</c> — отдельно от
    /// <see cref="Events"/> (у части из них нет <c>t</c> в старых журналах, а окна прочих харнессов их не ждут).</summary>
    public IReadOnlyList<CorpusEvent> TurnStarts { get; init; } = [];
    public required IReadOnlyDictionary<long, BankEvent> BankBySeq { get; init; }
    public required IReadOnlyList<long> EpochStarts { get; init; }
    public required IReadOnlyList<SaveAt> Saves { get; init; }

    /// <summary>Есть ли в журнале казна вообще (схема v5). Проверка подстрокой — без разбора JSON.</summary>
    public static bool HasBank(string path) =>
        File.ReadLines(path).Any(l => l.Contains("\"ev\":\"bank\"", StringComparison.Ordinal));

    /// <summary>Номера событий-причин движения казны по возрастанию — границы окна
    /// <see cref="NestedBank"/>. У <c>build</c> граница — <c>seq − 1</c>: его строка <c>bank</c> стоит перед ним.</summary>
    public required List<long> CauseSeqs { get; init; }

    private List<long>? _bankSeqs;

    /// <summary>
    /// Строка <c>bank</c> (<c>mode 0</c>, того же игрока, если он задан), которую двинул вызов-причина с номером
    /// <paramref name="causeSeq"/>: первая с <c>seq</c> больше <paramref name="causeSeq"/> и меньше номера следующей
    /// причины. Ранг 1 по журналу: <c>site_*</c>, <c>heal</c>, <c>revive</c>, <c>city_grow</c> берут номер до
    /// оригинала, а проверки внутри вызова тоже берут номера — в схеме 6.0 между причиной и <c>bank</c> стоят
    /// <c>sell_check</c>, <c>item_create</c>, <c>grow_check</c> и пропуски номеров (T07 §5.5: разрыв 2…7,
    /// 16 случаев в <c>20260915-passive</c>). В журналах до 6.0 это всегда <c>seq + 1</c>.
    /// </summary>
    public BankEvent? NestedBank(long causeSeq, string? player)
    {
        _bankSeqs ??= [.. BankBySeq.Keys.Order()];
        long stop = NextCause(causeSeq);
        for (int i = FirstAtLeast(_bankSeqs, causeSeq + 1); i < _bankSeqs.Count && _bankSeqs[i] < stop; i++)
        {
            var b = BankBySeq[_bankSeqs[i]];
            if (b.Mode == 0 && (player is null || player.Equals(b.Player, StringComparison.OrdinalIgnoreCase))) return b;
        }
        return null;
    }

    /// <summary>Строки <c>bank</c> <c>mode 0</c> окна причины <paramref name="causeSeq"/> (как у <see cref="NestedBank"/>),
    /// лежащие в тот же миг <paramref name="t"/>, — по порядку <c>seq</c>, не больше <paramref name="max"/>. Для
    /// причин, двигающих две казны разом (захват столицы). В зонде 7.0 между <c>battle_end</c> и ними стоят
    /// <c>ai_order</c> и пропуски номеров (20260919-human: <c>battle_end</c> 15533325, <c>ai_order</c> 15533326, <c>bank</c>
    /// 15533328 и 15533329); до 7.0 это всегда были <c>seq + 1</c> и <c>seq + 2</c>.</summary>
    public IReadOnlyList<BankEvent> NestedBanks(long causeSeq, long t, int max)
    {
        _bankSeqs ??= [.. BankBySeq.Keys.Order()];
        long stop = NextCause(causeSeq);
        var rows = new List<BankEvent>();
        for (int i = FirstAtLeast(_bankSeqs, causeSeq + 1); i < _bankSeqs.Count && _bankSeqs[i] < stop && rows.Count < max; i++)
        {
            var b = BankBySeq[_bankSeqs[i]];
            if (b.T != t) break;
            if (b.Mode == 0) rows.Add(b);
        }
        return rows;
    }

    private long NextCause(long causeSeq)
    {
        int i = FirstAtLeast(CauseSeqs, causeSeq + 1);
        return i < CauseSeqs.Count ? CauseSeqs[i] : long.MaxValue;
    }

    private static int FirstAtLeast(List<long> sorted, long value)
    {
        int i = sorted.BinarySearch(value);
        return i < 0 ? ~i : i;
    }

    private static long? CauseBoundary(CorpusEvent e) => e switch
    {
        BuildEvent => e.Seq - 1,
        SiteBuyItemEvent or SiteSellItemEvent or SiteBuySpellEvent or SiteBuyUnitEvent or SiteTrainEvent
            or HealEvent or ReviveEvent or CityGrowEvent or IncomeEvent or BattleEndEvent or SpendEvent => e.Seq,
        _ => null,
    };

    /// <summary>Журнал, разобранный один раз на процесс тестов: его читают полтора десятка харнессов, а разбор большого
    /// журнала (80 МБ, human) стоит ~4 с. Журнал и его сохранения только читаются (<see cref="MapSim"/> держит свой туман),
    /// поэтому общий экземпляр безопасен.</summary>
    public static TreasuryJournal Load(string path) =>
        Loaded.GetOrAdd(System.IO.Path.GetFullPath(path), p => new Lazy<TreasuryJournal>(() => LoadCore(p))).Value;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<TreasuryJournal>> Loaded =
        new(StringComparer.OrdinalIgnoreCase);

    private static TreasuryJournal LoadCore(string path)
    {
        var events = new List<CorpusEvent>();
        var turnStarts = new List<CorpusEvent>();
        var causes = new List<long>();
        var saves = new List<SaveEvent>();
        var loads = new List<long>();
        var journalLoads = new JournalLoads();
        bool loadOpen = false;
        foreach (var e in JournalCache.Read(path))
        {
            if (journalLoads.Opens(e)) loadOpen = true;
            if (loadOpen && AnyTimeOf(e) is { } started) { loads.Add(started); loadOpen = false; }
            if (IsRelevant(e)) events.Add(e);
            if (e is RegenEvent or HpChangeEvent or SpellExpireEvent or IncomeEvent) turnStarts.Add(e);
            if (CauseBoundary(e) is { } boundary) causes.Add(boundary);
            if (e is SaveEvent { Ok: true, Temp: false } s) saves.Add(s);
        }
        causes.Sort();
        // Окна «между снимком и событием» молча теряли бы событие без времени — журнал с казной обязан
        // нести t целиком (схема v5), иначе разбирать его нельзя.
        if (events.FirstOrDefault(e => e is not BattleStartEvent && TimeOf(e) is null) is { } untimed)
            throw new InvalidDataException(
                $"{path}: {untimed.GetType().Name} seq {untimed.Seq} без поля t в журнале с событием bank");
        return new TreasuryJournal
        {
            Path = path,
            Events = events,
            TurnStarts = turnStarts,
            BankBySeq = events.OfType<BankEvent>().ToDictionary(b => b.Seq),
            CauseSeqs = causes,
            EpochStarts = loads,
            Saves = OnSaveAxis(SavesOf(path), saves),
        };
    }

    public static long? TimeOf(CorpusEvent e) => e switch
    {
        BankEvent x => x.T, IncomeEvent x => x.T, SiteBuyItemEvent x => x.T, SiteSellItemEvent x => x.T,
        SiteBuySpellEvent x => x.T, SiteBuyUnitEvent x => x.T, SiteTrainEvent x => x.T, HealEvent x => x.T,
        ReviveEvent x => x.T, CityGrowEvent x => x.T, BuildEvent x => x.T, FortDayEvent x => x.T,
        BattleEndEvent x => x.T, XpEvent x => x.T, ItemEquipEvent x => x.T, ThiefRollEvent x => x.T, InputEvent x => x.T,
        RelationEvent x => x.T, PotionUseEvent x => x.T, AllianceChangeEvent x => x.T, SpendEvent x => x.T,
        TradeAcceptEvent x => x.T, NobleActionEvent x => x.T,
        _ => null,
    };

    /// <summary>Время любого события со свойством <c>T</c> (не только из <see cref="Events"/>): начало загрузки карты —
    /// первая строка со временем после смены потока, какой бы она ни была. Отражение зовётся лишь на строках между
    /// сменой потока и первым временем — единицы на загрузку.</summary>
    private static long? AnyTimeOf(CorpusEvent e) =>
        TimeOf(e) ?? e.GetType().GetProperty("T")?.GetValue(e) as long?;

    /// <summary>Последний снимок не позже <paramref name="t"/> в той же эпохе.</summary>
    public SaveAt? SaveBefore(long t) =>
        Saves.Where(s => s.T <= t && EpochOf(s.T) == EpochOf(t)).MaxBy(s => s.T);

    /// <summary>Первый снимок позже <paramref name="t"/> в той же эпохе.</summary>
    public SaveAt? SaveAfter(long t) =>
        Saves.Where(s => s.T > t && EpochOf(s.T) == EpochOf(t)).MinBy(s => s.T);

    /// <summary>Снимок для величин, которые внутри загрузки карты не меняются (лорд, раса, тип юнита).</summary>
    public SaveAt? SaveNear(long t) => SaveBefore(t) ?? SaveAfter(t);

    /// <summary>События файла со временем в полуинтервале (<paramref name="from"/>, <paramref name="to"/>].</summary>
    public IEnumerable<CorpusEvent> Between(long from, long to) =>
        Events.Where(e => TimeOf(e) is { } t && t > from && t <= to);

    /// <summary>События после снимка <paramref name="save"/> со временем не позже <paramref name="to"/>. Снимок на оси
    /// <c>seq</c> (схема 6.0) режет по <c>seq</c>: автосохранение и <c>income</c> следующего хода ложатся в одну
    /// миллисекунду, и <see cref="Between"/> (<c>t &gt; from</c>) терял тик (разбор 17.09: 20260916-synth ai-q57
    /// seq 12141700, ai-q02 seq 12761972). Без <c>seq</c> — <see cref="Between"/>.</summary>
    public IEnumerable<CorpusEvent> Since(SaveAt save, long to) => save.Seq is { } s
        ? Events.Where(e => e.Seq > s && TimeOf(e) is { } t && t <= to)
        : Between(save.T, to);

    /// <summary>Номер загрузки карты, в которой лежит время <paramref name="t"/> (−1 — до первой).</summary>
    public int EpochOf(long t)
    {
        int epoch = -1;
        for (int i = 0; i < EpochStarts.Count && EpochStarts[i] <= t; i++) epoch = i;
        return epoch;
    }

    private static bool IsRelevant(CorpusEvent e) => e is BankEvent or IncomeEvent or SiteBuyItemEvent
        or SiteSellItemEvent or SiteBuySpellEvent or SiteBuyUnitEvent or SiteTrainEvent or HealEvent
        or ReviveEvent or CityGrowEvent or BuildEvent or FortDayEvent or BattleStartEvent or BattleEndEvent
        or XpEvent or ItemEquipEvent or ThiefRollEvent or InputEvent or RelationEvent or PotionUseEvent
        or AllianceChangeEvent or SpendEvent or TradeAcceptEvent or NobleActionEvent;

    /// <summary>
    /// Ось сохранений схемы 6.0 — правило T02 (ранг 1, <c>20260915-passive</c> 38/38): строки <c>save</c> (<c>ok</c>, не
    /// временные) по <c>seq</c> и копии манифеста по числовому суффиксу имени сопоставляются <b>по позиции</b>, если
    /// их поровну; пара принимается, только если ход строки равен <c>CUR_TURN</c> копии. Тогда время копии — <c>t</c>
    /// строки, а не <c>mtime</c> (тот расходится с журналом на секунды и ставил сохранение не по ту сторону события:
    /// <c>relation</c> seq 5240401 при сохранении seq 5234792 — T07 §5.7). Иначе — прежняя ось по <c>mtime</c>.
    /// </summary>
    private static List<SaveAt> OnSaveAxis(List<SaveAt> copies, List<SaveEvent> saves)
    {
        if (saves.Count == 0) return copies;
        if (saves.Count != copies.Count) return ByNearestSave(copies, saves);
        var bySuffix = copies.OrderBy(Suffix).ToList();
        var byEvent = saves.OrderBy(s => s.Seq).ToList();
        if (bySuffix.Select(Suffix).Distinct().Count() != copies.Count) return copies;
        if (bySuffix.Where((c, i) => c.Map.Turn != byEvent[i].Turn).Any()) return copies;
        return bySuffix.Select((c, i) => new SaveAt(byEvent[i].T, c.Path, byEvent[i].Seq)).OrderBy(s => s.T).ToList();
    }

    /// <summary>Строк <c>save</c> больше, чем копий (автосохранения с нечитаемым именем сторож отвергает, прогон
    /// <c>20260916-synth</c>: 376 строк на 233 копии). Тогда каждой копии по возрастанию <c>mtime</c> — последняя ещё
    /// не занятая строка с <c>t ≤ mtime + 0,5 с</c>; берутся только строки с ходом, равным <c>CUR_TURN</c>
    /// копии; не нашлось — ось остаётся по <c>mtime</c> целиком. На прогоне 16.09 так привязаны 279 из 279
    /// (<c>docs/synthetic-maps/journal_maps.py</c> в disciples-modding).</summary>
    private static List<SaveAt> ByNearestSave(List<SaveAt> copies, List<SaveEvent> saves)
    {
        const long SlackMs = 500;
        var used = new HashSet<long>();
        var bound = new List<SaveAt>();
        foreach (var c in copies.OrderBy(c => c.T))
        {
            // Ход сверяется ДО выбора: mtime копии дрожит, и в окно +0,5 с попадает строка следующего хода
            // (20260920-gaps: копия t21 mtime 104381, QuickSave хода 22 t 104782) — ось падала на mtime целиком.
            var hit = saves.Where(s => s.T <= c.T + SlackMs && s.Turn == c.Map.Turn && !used.Contains(s.Seq))
                .MaxBy(s => s.Seq);
            if (hit is null) return copies;
            used.Add(hit.Seq);
            bound.Add(new SaveAt(hit.T, c.Path, hit.Seq));
        }
        // Порядок сохраняется: внутри дня k-я по mtime копия — k-я по seq из выбранных строк. «Последняя незанятая» его ломает, когда
        // две быстрые записи легли раньше обеих копий: 20261016e-av, карта 7, t01-050 (mtime 2893789) взяла строку 2893141, а t01-051
        // (2897959) — более раннюю 2889016; по содержимому 050 раньше (7 записей суточного списка посохов против 8).
        var byPath = copies.ToDictionary(c => c.Path, c => c.T, StringComparer.OrdinalIgnoreCase);
        return bound.GroupBy(s => s.Map.Turn)
            .SelectMany(g => g.OrderBy(s => byPath[s.Path]).Zip(g.OrderBy(s => s.Seq),
                                                                 (copy, line) => new SaveAt(line.T, copy.Path, line.Seq)))
            .OrderBy(s => s.T).ToList();
    }

    /// <summary>Числовой суффикс имени копии (<c>set-fardeyla-elf-t12-014.sg</c> → 14); нет суффикса — −1.</summary>
    private static int Suffix(SaveAt s)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(s.Path);
        int dash = name.LastIndexOf('-');
        return dash >= 0 && int.TryParse(name[(dash + 1)..], out int n) ? n : -1;
    }

    /// <summary>Сохранения, объявленные строкой манифеста про этот журнал, со временем от
    /// <c>started_at</c>. Нет манифеста или строки — пусто.</summary>
    private static List<SaveAt> SavesOf(string journal)
    {
        var dir = System.IO.Path.GetDirectoryName(journal) ?? ".";
        var manifest = System.IO.Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var name = System.IO.Path.GetFileName(journal);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("file", out var f) || !name.Equals(f.GetString(), StringComparison.OrdinalIgnoreCase)
                || !row.TryGetProperty("started_at", out var st) || !row.TryGetProperty("saves", out var saves)) continue;
            var start = DateTimeOffset.Parse(st.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            return saves.EnumerateArray()
                .Select(s => System.IO.Path.Combine(dir, s.ValueKind == JsonValueKind.String ? s.GetString()! : s.GetProperty("file").GetString()!))
                .Where(File.Exists)
                .Select(p => new SaveAt((long)(new DateTimeOffset(File.GetLastWriteTimeUtc(p)) - start).TotalMilliseconds, p))
                .OrderBy(s => s.T)
                .ToList();
        }
        return [];
    }
}
