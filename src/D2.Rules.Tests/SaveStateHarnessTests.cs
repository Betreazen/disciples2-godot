using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Map;
using Xunit.Abstractions;
using KSg = Kaitai.Sg;

namespace D2.Rules.Tests;

/// <summary>Задача 2 Ф7 — состояние карты из сохранений и события схем 5–5.3 в читателе. Харнессы приёмки
/// плана (§2, «Задача 2»): <c>sg_state</c> (и <c>sg_known</c> — известные значения прогонов), <c>events_v5</c>,
/// <c>diplo_decode</c>. Каждый печатает <c>имя сошлось=N разошлось=M</c> и падает при N = 0 или M &gt; 0.</summary>
[Trait("Category", "Corpus")]
public class SaveStateHarnessTests(ITestOutputHelper output)
{
    private const string EmptyId = "G000000000";

    /// <summary>Записи читателя для событий схем 5–5.3.</summary>
    private static readonly HashSet<Type> SchemaV5 =
    [
        typeof(BankEvent), typeof(SiteBuyItemEvent), typeof(SiteSellItemEvent), typeof(SiteBuySpellEvent),
        typeof(SiteBuyUnitEvent), typeof(SiteTrainEvent), typeof(HealEvent), typeof(ReviveEvent),
        typeof(CityGrowEvent), typeof(BuildEvent), typeof(FortDayEvent), typeof(RelationEvent),
        typeof(ThiefRollEvent), typeof(ItemEquipEvent), typeof(InputEvent), typeof(HireUnitEvent),
        typeof(HireLeaderEvent), typeof(RodDropEvent), typeof(SpellCastEvent), typeof(SpellResearchEvent),
        typeof(WandCastEvent),
    ];

    // ---- sg_state ------------------------------------------------------------------------------------

    /// <summary><c>sg_state</c>: каждое сохранение корпуса (все каталоги, в том числе выведенные из оборота —
    /// открываться должны все) читается <see cref="MapLoader"/> и <see cref="SiteLoader"/> без исключения, и число
    /// загруженных записей каждого вида равно числу записей Kaitai. Сверка по числу ловит молча схлопнутые
    /// повторы id и выброшенные записи; сами значения — <see cref="SgState_KnownValuesOfRuns"/>.</summary>
    [SkippableFact] public void SgState_EveryCorpusSaveLoadsEveryRecord()
    {
        Skip.IfNot(Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var files = Directory.EnumerateFiles(CorpusFiles.Dir, "*.sg", SearchOption.AllDirectories)
                             .Order(StringComparer.Ordinal).ToList();
        var byKind = new SortedDictionary<string, (int Ok, int Bad)>(StringComparer.Ordinal);
        var mismatches = new List<string>();
        foreach (var file in files)
            foreach (var (kind, raw, loaded) in Comparisons(file))
            {
                var (ok, bad) = byKind.GetValueOrDefault(kind);
                byKind[kind] = raw == loaded ? (ok + 1, bad) : (ok, bad + 1);
                if (raw != loaded) mismatches.Add($"{Path.GetRelativePath(CorpusFiles.Dir, file)} {kind}: Kaitai {raw}, загружено {loaded}");
            }

        output.WriteLine($"sg_state: сохранений {files.Count}");
        foreach (var (kind, (ok, bad)) in byKind) output.WriteLine($"  {kind,-22} сошлось={ok,-5} разошлось={bad}");
        int total = byKind.Values.Sum(v => v.Ok), totalBad = byKind.Values.Sum(v => v.Bad);
        output.WriteLine($"sg_state сошлось={total} разошлось={totalBad}");
        Assert.True(totalBad == 0, string.Join("\n", mismatches.Take(20)));
        Assert.True(total > 0, "sg_state: ни одной сверки");
    }

    private static IEnumerable<(string Kind, long Raw, long Loaded)> Comparisons(string file)
    {
        var data = SgFile.Open(file).Raw.Objects.Select(o => o.Data).ToList();
        var map = SaveMaps.Map(file);
        return StackAndUnitCounts(data, map).Concat(PlayerAndWorldCounts(data, map))
                                            .Concat(SiteCounts(data, SaveMaps.Sites(file)));
    }

    private static IEnumerable<(string Kind, long Raw, long Loaded)> StackAndUnitCounts(
        IReadOnlyList<Kaitai.KaitaiStruct> data, MapState map)
    {
        var stacks = data.OfType<KSg.MidStack>().ToList();
        yield return ("stacks", stacks.Count, map.Stacks.Count);
        yield return ("stack_items", stacks.Sum(s => Filled(s.Items)), map.Stacks.Values.Sum(s => s.Items.Count));
        yield return ("stack_equipment",
            stacks.Sum(s => Filled([s.Banner, s.Tome, s.Battle1, s.Battle2, s.Artifact1, s.Artifact2, s.Boots])),
            map.Stacks.Values.Sum(s => s.Equipment.Count(e => e.Length > 0)));
        yield return ("stack_orders", stacks.Count(s => s.Order.Value != 0), map.Stacks.Values.Count(s => s.Order != 0));
        var units = data.OfType<KSg.MidUnit>().ToList();
        yield return ("units", units.Count, map.Units.Count);
        yield return ("unit_modifiers", units.Sum(u => u.Modifiers.Count), map.Units.Values.Sum(u => u.Modifiers.Count));
        yield return ("unit_dynlevel", units.Count(u => u.Dynlevel is { Value: not 0 }), map.Units.Values.Count(u => u.DynLevel));
    }

    private static IEnumerable<(string Kind, long Raw, long Loaded)> PlayerAndWorldCounts(
        IReadOnlyList<Kaitai.KaitaiStruct> data, MapState map)
    {
        var players = data.OfType<KSg.MidPlayer>().ToList();
        yield return ("players", players.Count, map.Players.Count);
        yield return ("players_captured", players.Count(p => Filled([p.CaptBy]) == 1),
                      map.Players.Values.Count(p => p.CapturedBy.Length > 0));
        yield return ("known_spells", data.OfType<KSg.PlayerKnownSpells>().Sum(k => Filled(k.Spells)),
                      map.Players.Values.Sum(p => p.KnownSpells.Count));
        yield return ("rods", data.OfType<KSg.MidRod>().Count(), map.Rods.Count);
        yield return ("relations", data.OfType<KSg.MidDiplomacy>().Sum(d => d.Entries.Count), map.Relations.Count);
        yield return ("talisman_charges", data.OfType<KSg.MidTalismanCharges>().Sum(c => c.Charges.Count),
                      map.TalismanCharges.Count);
        yield return ("spell_casts", data.OfType<KSg.MidSpellCast>().Sum(c => c.Items.Count), map.SpellCasts.Count);
        yield return ("item_casts", data.OfType<KSg.MidSpellCast>().Sum(c => c.ItemCasts.Count), map.ItemCasts.Count);
        yield return ("items", data.OfType<KSg.MidItem>().Count(), map.ItemTypes.Count);
        var info = data.OfType<KSg.ScenarioInfo>().Single();
        yield return ("scenario_max_unit", info.MaxUnit.Value, map.Limits?.MaxUnit ?? -1);
        yield return ("scenario_max_leader", info.MaxLeader.Value, map.Limits?.MaxLeader ?? -1);
    }

    private static IEnumerable<(string Kind, long Raw, long Loaded)> SiteCounts(
        IReadOnlyList<Kaitai.KaitaiStruct> data, IReadOnlyList<MapSite> sites)
    {
        long Of(SiteKind k) => sites.Count(s => s.Kind == k);
        yield return ("sites_capital", data.OfType<KSg.Capital>().Count(), Of(SiteKind.Capital));
        yield return ("sites_village", data.OfType<KSg.MidVillage>().Count(), Of(SiteKind.Village));
        yield return ("sites_ruin", data.OfType<KSg.MidRuin>().Count(), Of(SiteKind.Ruin));
        yield return ("sites_merchant", data.OfType<KSg.MidSiteMerchant>().Count(), Of(SiteKind.Merchant));
        yield return ("sites_mercs", data.OfType<KSg.MidSiteMercs>().Count(), Of(SiteKind.Mercenaries));
        yield return ("sites_trainer", data.OfType<KSg.MidSiteTrainer>().Count(), Of(SiteKind.Trainer));
        yield return ("sites_mage", data.OfType<KSg.MidSiteMage>().Count(), Of(SiteKind.Mage));
        yield return ("village_size_sum", data.OfType<KSg.MidVillage>().Sum(v => (long)v.Size.Value),
                      sites.Sum(s => (long)(s.Size ?? 0)));
        var cities = data.OfType<KSg.MidVillage>().Select(v => v.City).Concat(data.OfType<KSg.Capital>().Select(c => c.City));
        yield return ("fort_visitor_stacks", cities.Count(c => Filled([c.Stack]) == 1), sites.Count(s => s.StackId.Length > 0));
        yield return ("merchant_stock", data.OfType<KSg.MidSiteMerchant>().Sum(m => m.Items.Count), sites.Sum(s => s.Stock.Count));
        yield return ("mercs_roster", data.OfType<KSg.MidSiteMercs>().Sum(m => m.Units.Count), sites.Sum(s => s.Roster.Count));
        yield return ("mage_spells", data.OfType<KSg.MidSiteMage>().Sum(m => Filled(m.Spells)), sites.Sum(s => s.Spells.Count));
        yield return ("site_visitors", VisitorCount(data), sites.Sum(s => s.Visitors.Count));
    }

    private static long VisitorCount(IReadOnlyList<Kaitai.KaitaiStruct> data) =>
        data.OfType<KSg.MidSiteMerchant>().Sum(m => Filled(m.Visitors.Select(v => v.Visitor)))
        + data.OfType<KSg.MidSiteMercs>().Sum(m => Filled(m.Visitors.Select(v => v.Visitor)))
        + data.OfType<KSg.MidSiteTrainer>().Sum(m => Filled(m.Visitors.Select(v => v.Visitor)))
        + data.OfType<KSg.MidSiteMage>().Sum(m => Filled(m.Visitors.Select(v => v.Visitor)))
        + data.OfType<KSg.MidRuin>().Sum(r => Filled(r.Visitors.Select(v => v.Visiter)));

    private static int Filled(IEnumerable<KSg.StringRecord> records) =>
        records.Count(r => r.String.Length > 0 && !r.String.Equals(EmptyId, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>sg_known</c>: известные значения прогонов, прочитанные из сохранений. Прежние образцы (прогоны
    /// <c>20260911-passive</c>: <c>f7-m22-capture3</c>, <c>f7-m19-diplo2</c>, <c>f7-m17-charges</c>) потеряны 18.09.2026;
    /// те же три величины взяты из партии <c>20260919-human</c> (зонд 7.0), каждое значение — из строки журнала:
    /// <list type="bullet">
    /// <item><c>CAPT_BY</c> Нежити после захвата её столицы человеком: <c>battle_end</c> 15533325 у
    /// <c>S073FT0003</c>, окно «Вы покорили расу: Undead Hordes» (seq 15534266); в t62-103 поле ещё пусто;</item>
    /// <item>отношения после сноса жезла: <c>relation</c> seq 3720604, <c>rod_destroyed</c> (Кланы у Нежити), слова
    /// <c>changed</c> — нейтралы—Кланы raw 32 (cur 16), Империя—Кланы raw 20 (cur 10); сверяются в ближайшем
    /// сохранении после строки (<see cref="TreasuryJournal.SaveAfter"/>), других строк этих пар до него нет;</item>
    /// <item>заряды неприменённого талисмана <c>S073IM0055</c> — 5 во всех сохранениях партии.</item>
    /// </list>
    /// <para><b>Расход зарядов применявшегося талисмана</b> — <c>S143IM0004</c> карты <c>f7-m17-charges</c> прогона
    /// <c>20260919-f7</c> (<c>passive-002</c>): пять строк <c>talisman_charge</c> (<c>apply</c>) — seq 1027851, 1101218,
    /// 1116474, 1145845, 1161859, — и в последней <c>item_consume</c> (seq 1161858) слот опустел: 5→4→3→2→1→нет.
    /// Без этого прогона — пропуск с причиной после сверки остальных значений.</para></summary>
    [SkippableFact] public void SgState_KnownValuesOfRuns()
    {
        var dir = Path.Combine(CorpusFiles.Dir, "20260919-human");
        Skip.IfNot(Directory.Exists(dir) && Directory.Exists(GamePaths.Globals), "нет corpus/20260919-human или Globals");
        // Grace.RACE_TYPE 1 — Нежить (LRaceCategory: 0 Империя, 1 Нежить, 2 Легионы, 3 Кланы, 4 нейтралы).
        var undead = EconomyLoader.Load(GamePaths.Globals).Races.Values.Single(r => r.RaceType == 1).Id;
        var checks = new List<(string What, string Expected, string Actual)>();

        string CaptBy(string file) => SaveMaps.Map(Path.Combine(dir, file)).Players.Values
            .Single(p => p.RaceId.Equals(undead, StringComparison.OrdinalIgnoreCase)).CapturedBy.ToUpperInvariant();
        checks.Add(($"t62-103: CAPT_BY у Нежити ({undead}) до захвата", "", CaptBy("rastlennaya-vlast-dwarf-t62-103.sg")));
        checks.Add(($"t62-104: CAPT_BY у Нежити ({undead}) после захвата", "S073PL000A", CaptBy("rastlennaya-vlast-dwarf-t62-104.sg")));

        var journal = TreasuryJournal.Load(Path.Combine(dir, "passive-001.jsonl"));
        const long rodSeq = 3720604;
        var rod = journal.Events.OfType<RelationEvent>().Single(e => e.Seq == rodSeq);
        var after = journal.SaveAfter(rod.T);
        Assert.NotNull(after);
        output.WriteLine($"  снос жезла seq {rodSeq} ({rod.Cause}) → ближайшее сохранение {Path.GetFileName(after!.Path)}");
        checks.Add(("после сноса жезла: нейтралы—Кланы (4-3)", "16", $"{after.Map.RelationOf(4, 3)?.Current}"));
        checks.Add(("после сноса жезла: Империя—Кланы (0-3)", "10", $"{after.Map.RelationOf(0, 3)?.Current}"));

        checks.Add(("заряды неприменённого талисмана S073IM0055", "5",
                    ChargesTrail(dir, "rastlennaya-vlast-dwarf-*.sg", "S073IM0055")));
        var f7 = Path.Combine(CorpusFiles.Dir, "20260919-f7");
        bool hasCharges = Directory.Exists(f7);
        if (hasCharges)
            checks.Add(("заряды применявшегося талисмана S143IM0004 (f7-m17-charges)", "5→4→3→2→1→нет",
                        ChargesTrail(f7, "f7-m17-charges-*.sg", "S143IM0004")));

        int ok = 0, bad = 0;
        foreach (var (what, expected, actual) in checks)
        {
            if (expected == actual) ok++; else bad++;
            output.WriteLine($"  {what}: ожидалось «{expected}», прочитано «{actual}»");
        }
        output.WriteLine($"sg_known сошлось={ok} разошлось={bad}");
        Assert.Equal(0, bad);
        Skip.IfNot(hasCharges, "sg_known: нет corpus/20260919-f7 — случая расхода зарядов талисмана (5→4→…→нет); нужен бой, " +
                               "где герой с талисманом в боевом слоте применяет его несколько раз (карта f7-m17-charges)");
    }

    /// <summary>Заряды талисмана <paramref name="talisman"/> по сохранениям маски <paramref name="mask"/> в порядке
    /// съёмки: число из <c>CMidTalismanCharges</c> или «нет», если записи нет; одинаковые соседние значения свёрнуты.
    /// Полный ряд каждого сохранения печатается.</summary>
    private string ChargesTrail(string dir, string mask, string talisman)
    {
        var trail = new List<string>();
        foreach (var f in Directory.GetFiles(dir, mask).Order(StringComparer.Ordinal))
        {
            var charges = SaveMaps.Map(f).TalismanCharges;
            string value = charges.TryGetValue(talisman, out int left) ? $"{left}" : "нет";
            output.WriteLine($"  {Path.GetFileName(f)} {talisman}: {value}");
            if (trail.Count == 0 || trail[^1] != value) trail.Add(value);
        }
        return string.Join("→", trail);
    }

    // ---- events_v5 -----------------------------------------------------------------------------------

    /// <summary><c>events_v5</c>: ни одна строка ни одного журнала корпуса не падает в <see cref="UnknownEvent"/>;
    /// «сошлось» — число строк событий схем 5–5.3, разобранных своей записью. Журналы — все файлы масок
    /// <see cref="CorpusFiles.Masks"/> (<c>battle-*</c>, <c>map-*</c>, <c>passive-*</c>) без фильтра приёма по
    /// сохранениям, то есть и те, что С1/С2 не берут. Вне сверки — каталоги на «_» (сознательно выведенные из оборота,
    /// в <c>_old-20260906-no-order</c> схема старше поля <c>order</c>) и сырой <c>corpus/first-battle-raw.jsonl</c>
    /// схемы 1 (без <c>order</c>); читатель на них падает раньше, чем доходит до схемы 5. Их число печатается.</summary>
    [SkippableFact] public void EventsV5_NoUnknownEventInAnyJournal()
    {
        Skip.IfNot(Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        int all = Directory.EnumerateFiles(CorpusFiles.Dir, "*.jsonl", SearchOption.AllDirectories).Count();
        var files = CorpusFiles.Masks
            .SelectMany(mask => Directory.EnumerateFiles(CorpusFiles.Dir, mask, SearchOption.AllDirectories))
            .Where(f => !(Path.GetFileName(Path.GetDirectoryName(f)) ?? "").StartsWith('_'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        output.WriteLine($"events_v5: журналов вне масок корпуса или в каталогах на «_» {all - files.Count}");
        var known = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var unknown = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long lines = 0;
        foreach (var file in files)
            foreach (var e in JournalCache.Read(file))
            {
                lines++;
                if (e is UnknownEvent u) unknown[u.Ev] = unknown.GetValueOrDefault(u.Ev) + 1;
                else if (SchemaV5.Contains(e.GetType())) known[e.GetType().Name] = known.GetValueOrDefault(e.GetType().Name) + 1;
            }

        output.WriteLine($"events_v5: журналов {files.Count}, строк {lines}");
        foreach (var (name, n) in known) output.WriteLine($"  {name,-20} {n}");
        foreach (var (ev, n) in unknown) output.WriteLine($"  НЕ РАЗОБРАНО {ev,-14} {n}");
        long ok = known.Values.Sum(), bad = unknown.Values.Sum();
        output.WriteLine($"events_v5 сошлось={ok} разошлось={bad}");
        Assert.Equal(0, bad);
        Assert.True(ok > 0, "events_v5: ни одного события схемы 5");
    }

    // ---- diplo_decode --------------------------------------------------------------------------------

    /// <summary><c>diplo_decode</c>: для каждой пары рас, которую тронули события <c>relation</c> до ближайшего
    /// следующего сохранения той же загрузки карты (<see cref="TreasuryJournal.SaveAfter"/>), текущее отношение
    /// из слова <c>RELATION</c> сохранения (<see cref="Relation.Current"/>) равно <c>cur</c> последнего слова пары в
    /// журнале (<c>after</c> основной пары и элементы <c>changed</c>). <c>diplo_raw</c> — то же для слова целиком
    /// (биты хода союза и флаги); печатается, но в приёмку не входит.</summary>
    [SkippableFact] public void DiploDecode_SaveRelationMatchesLastRelationEvent()
    {
        Skip.IfNot(Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var journals = Directory.EnumerateFiles(CorpusFiles.Dir, "*.jsonl", SearchOption.AllDirectories)
            .Where(f => !(Path.GetFileName(Path.GetDirectoryName(f)) ?? "").StartsWith('_'))
            .Where(f => File.ReadLines(f).Any(l => l.Contains("\"ev\":\"relation\"", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToList();
        var counts = new SortedDictionary<string, (int Ok, int Bad)>(StringComparer.Ordinal);
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in journals) DecodeJournal(path, counts, skips);

        output.WriteLine($"diplo_decode: журналов с relation {journals.Count}");
        foreach (var (key, (ok, bad)) in counts) output.WriteLine($"{key} сошлось={ok} разошлось={bad}");
        foreach (var (why, n) in skips) output.WriteLine($"  пропуск: {why} — {n}");
        var (decoded, wrong) = counts.GetValueOrDefault("diplo_decode");
        Assert.Equal(0, wrong);
        Assert.True(decoded > 0, "diplo_decode: ни одной сверки");
    }

    private void DecodeJournal(string path, SortedDictionary<string, (int Ok, int Bad)> counts,
                               SortedDictionary<string, int> skips)
    {
        var journal = TreasuryJournal.Load(path);
        var relations = JournalCache.Read(path, "relation").OfType<RelationEvent>().OrderBy(r => r.Seq).ToList();
        foreach (var group in relations.GroupBy(r => journal.SaveAfter(r.T)))
        {
            if (group.Key is null)
            {
                const string why = "нет сохранения позже события в той же загрузке карты";
                skips[why] = skips.GetValueOrDefault(why) + group.Count();
                continue;
            }
            var map = group.Key.Map;
            foreach (var ((a, b), (raw, seq)) in FinalWords(group))
            {
                var saved = map.RelationOf((int)a, (int)b);
                long cur = (raw >> 1) & 0x7F;
                Tally(counts, "diplo_decode", saved?.Current == cur);
                Tally(counts, "diplo_raw", saved?.Raw == (uint)raw);
                output.WriteLine($"  {Path.GetFileName(path)} seq {seq} пара {a}-{b}: журнал cur {cur} raw {raw}; " +
                                 $"{Path.GetFileName(group.Key.Path)} cur {saved?.Current} raw {saved?.Raw}");
            }
        }
    }

    /// <summary>Последнее слово каждой неупорядоченной пары рас в событиях группы (в порядке <c>seq</c>).</summary>
    private static Dictionary<(long A, long B), (long Raw, long Seq)> FinalWords(IEnumerable<RelationEvent> events)
    {
        var last = new Dictionary<(long A, long B), (long Raw, long Seq)>();
        foreach (var e in events)
        {
            if (e.After is { } after) last[Pair(e.RaceA, e.RaceB)] = (after.Raw, e.Seq);
            foreach (var c in e.Changed) last[Pair(c.Race1, c.Race2)] = (c.After, e.Seq);
        }
        return last;
    }

    private static (long A, long B) Pair(long a, long b) => a <= b ? (a, b) : (b, a);

    private static void Tally(SortedDictionary<string, (int Ok, int Bad)> counts, string key, bool ok)
    {
        var (o, b) = counts.GetValueOrDefault(key);
        counts[key] = ok ? (o + 1, b) : (o, b + 1);
    }
}
