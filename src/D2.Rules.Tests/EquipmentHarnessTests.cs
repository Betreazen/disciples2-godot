using D2.Data;
using D2.Data.Corpus;
using D2.Data.Magic;
using D2.Data.Map;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Харнесс задачи 9 Ф7: гейт экипировки и три источника способностей лидера против корпуса.
///
/// <list type="bullet">
/// <item><c>equip_gate</c> — каждая строка <c>item_equip</c> журналов: у <c>apply = 0</c> наш ответ
/// равен <c>ret</c>, у <c>apply = 1</c> с <c>ret = true</c> предмет проходит наш гейт;</item>
/// <item><c>equip_saves</c> — каждый предмет в каждом слоте каждого сохранения проходит гейт и
/// карту слотов;</item>
/// <item><c>equip_chain</c> — на переходе сохранений <c>MOD_EQUIP</c> надетого появляется у тех
/// юнитов, кому его кладёт модель (знамя — всему отряду, остальное — лидеру), и у остальных не
/// появляется; на снятии — уходит;</item>
/// <item><c>ability_sources</c> — три источника (<c>GLabi</c>, навык уровня, <c>MOD_EQUIP</c> надетого), каждый на
/// наблюдениях, где он — единственный носитель способности надетого предмета (<see cref="SourceKinds"/>; прежде —
/// один герой <c>m8-mage</c>, корпус потерян 18.09.2026, теперь — все слоты всех сохранений).</item>
/// </list>
///
/// <para><b>Состояние гейта берётся из сохранения ПОСЛЕ события</b> (первого, в чьё содержимое
/// событие уже вошло), а не до. Причина измерена: на <c>m8-mage</c> и уровневый навык «Тайное
/// знание» (<c>G000UM4002</c>, способность 10), и надевание книги, которую он разрешает, лежат
/// между сохранениями <c>097</c> и <c>098</c> — по состоянию «до» гейт отказал бы книге, хотя
/// движок её надел. <b>Потолок назван:</b> набор уровневых навыков в снимке «после» может быть
/// надмножеством того, что было в момент события (навык мог быть взят уже после надевания), и
/// такой отказ гейта харнесс не увидит. Что снимет: хук момента выбора навыка в зонде (§3) либо
/// журнал с событием <c>leader_upgrade</c>. Тавтологией это не делает и вот почему: способность
/// проверяемого предмета в набор НЕ входит (<c>excludeSlot</c>), и строгость самой калитки мерит
/// <c>ability_sources</c> — там каждый из трёх источников виден единственным носителем.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class EquipmentHarnessTests(ITestOutputHelper output)
{
    private const string Gate = "equip_gate";
    private const string Saves = "equip_saves";
    private const string Chain = "equip_chain";
    private const string SourcesKey = "ability_sources";

    /// <summary>Три источника способности (<see cref="AbilitySources.Where"/>): у каждого должно быть хоть одно
    /// наблюдение, где он — <b>единственный</b> носитель способности, нужной надетому предмету.</summary>
    private static readonly (string Source, string Situation)[] SourceKinds =
    [
        ("GLabi", "лидер, чья врождённая способность (GLabi) — единственный источник для надетого предмета: например, маг со сферой"),
        ("уровень", "лидер, надевший предмет благодаря навыку уровня: например, взять «Тайное знание» (G000UM4002) и надеть книгу/драгоценность (L_JEWELRY_USE)"),
        ("предмет", "лидер, надевший предмет благодаря MOD_EQUIP другого надетого предмета: например, книга G000IG4007 даёт L_TALISMAN_USE, затем надеть талисман"),
    ];

    [SkippableFact] public void Equipment_MatchesTheCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Dir.Length > 0 && Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var book = ProgressBook.Load(GamePaths.Globals);
        var c = new Counters();

        var categories = EquipLoader.LoadItemCategories(GamePaths.Globals);
        var names = EquipLoader.LoadAbilityNames(GamePaths.Globals);
        Assert.Equal(15, categories.Count);   // LmagItm.dbf: 15 живых строк, одна (L_COIN) удалена
        Assert.Equal(12, names.Count);        // LleadA.dbf: 12 живых из 20
        c.Note($"LmagItm {categories.Count} категорий, LleadA {names.Count} способностей, GLabi " +
               $"{book.InnateAbilities.Count} типов с врождёнными; гейт: " +
               string.Join(", ", categories.OrderBy(p => p.Key)
                   .Select(p => $"{p.Key} {p.Value}→" + (Equipment.RequiredAbility((ItemCategory)p.Key) is { } a
                       ? names.GetValueOrDefault(a, a.ToString()) : "без навыка"))));
        c.Note("состояние гейта — снимок ПОСЛЕ события (см. описание класса); способность проверяемого "
             + "предмета в набор не входит; лидер считается живым — надевали его живым, а умереть он "
             + "мог и позже, вещь при этом остаётся надетой.");

        foreach (var path in CorpusFiles.Journals().Where(p => CorpusFiles.ItemEvents(p) > 0))
            GateOnJournal(path, book, c);
        foreach (var path in AllSaves()) SlotsOfSave(path, book, c);
        foreach (var (name, files) in SaveChains.All(keepSameTurn: true)) ChainOnSaves(name, files, book, c);

        c.Print(output, "задача 9 — инвентарь, слоты, гейт способностей");
        c.Require(Gate, Saves, Chain);
        c.RequireOrSkip([.. SourceKinds.Select(k => ($"{SourcesKey} {k.Source}", k.Situation))]);
    }

    // ------------------------------------------------------------------------------- equip_gate

    /// <summary>Строки <c>item_equip</c> одного журнала. Снятие (<c>item_unequip</c>) своего гейта не
    /// имеет и здесь не мерится — его смотрит <c>equip_chain</c>.</summary>
    private static void GateOnJournal(string path, ProgressBook book, Counters c)
    {
        if (!TreasuryJournal.HasBank(path)) { c.Skip(Gate, $"{Name(path)}: журнал без казны — оси t нет"); return; }
        var j = TreasuryJournal.Load(path);
        if (j.Saves.Count == 0) { c.Skip(Gate, $"{Name(path)}: манифест не объявил сохранений"); return; }

        foreach (var e in j.Events.OfType<ItemEquipEvent>().Where(x => x.Equip && x.Item is not null))
        {
            // Снимок «после» — первый, в чьё содержимое событие уже вошло; в конце журнала его нет.
            var shot = j.SaveAfter(e.T) ?? j.SaveBefore(e.T);
            if (shot is null) { c.Skip(Gate, $"{Name(path)}: снимка в эпохе события нет"); continue; }
            if (!shot.Map.Stacks.TryGetValue(e.Stack, out var stack))
            { c.Skip(Gate, $"{Name(path)}: отряда {e.Stack} нет в снимке"); continue; }
            if (book.ItemOf(shot.Map, e.Item!) is not { } item)
            { c.Skip(Gate, $"{Name(path)}: экземпляра {e.Item} нет в снимке (расходован или продан)"); continue; }
            if (book.Abilities(shot.Map, stack, (int)e.Slot, out var why) is not { } abilities)
            { c.Skip(Gate, $"{Name(path)}: {why}"); continue; }

            var refusal = Equipment.Check(item, (int)e.Slot, abilities.All, leaderAlive: true, book.Rules.Modifiers);
            bool ours = refusal == EquipRefusal.Ok;
            c.Count(Gate, ours == e.Ret,
                    $"{Name(path)} seq {e.Seq}: {e.Item}/{item.Id} {item.Category} в слот {e.Slot} отряда " +
                    $"{e.Stack} — движок {e.Ret}, мы {ours} ({refusal}), способности {Show(abilities.All)}, " +
                    $"снимок {Path.GetFileName(shot.Path)}");
            if (!e.Ret) c.Note($"{Name(path)} seq {e.Seq}: отказ движка ret=false — редкое наблюдение");
        }
    }

    // ------------------------------------------------------------------------------ equip_saves

    /// <summary>Все семь слотов каждого отряда сохранения: надетое обязано проходить и карту слотов,
    /// и гейт способности. Проверяемый предмет своей способности себе не даёт.</summary>
    private static void SlotsOfSave(string path, ProgressBook book, Counters c)
    {
        MapState map;
        try { map = SaveMaps.Map(path); }
        catch (Exception ex) { c.Skip(Saves, $"{Path.GetFileName(path)} не читается: {ex.GetType().Name}"); return; }

        foreach (var stack in map.Stacks.Values)
        {
            var slots = Equipment.Slots(stack);
            for (int slot = 0; slot < slots.Count; slot++)
            {
                if (slots[slot].Length == 0) continue;
                if (book.ItemOf(map, slots[slot]) is not { } item)
                { c.Skip(Saves, $"тип экземпляра {slots[slot]} не опознан"); continue; }
                if (book.Abilities(map, stack, slot, out var why) is not { } abilities)
                { c.Skip(Saves, $"{Path.GetFileName(path)}: {why}"); continue; }
                var refusal = Equipment.Check(item, slot, abilities.All, leaderAlive: true, book.Rules.Modifiers);
                if (refusal != EquipRefusal.Ok && SynthEquipped(path, stack.Id, slot, item.Id))
                {
                    c.Skip(Saves, $"{Path.GetFileName(path)} {stack.Id} слот {slot}: {item.Id} надет генератором синтетической карты " +
                                  $"(facts.json), не окном — гейт надевания движок к слотам сохранения не применяет (отказ порта {refusal})");
                    continue;
                }
                c.Count(Saves, refusal == EquipRefusal.Ok,
                        $"{Path.GetFileName(path)} {stack.Id} слот {slot}: {slots[slot]}/{item.Id} " +
                        $"{item.Category} — наш отказ {refusal}, способности {Show(abilities.All)}");
                // ability_sources: способность, нужная предмету, пришла ровно из одного источника — он несущий.
                if (Equipment.RequiredAbility(item.Category) is { } need && abilities.Where(need) is { Length: > 0 } source
                    && !source.Contains('+'))
                    c.Count($"{SourcesKey} {source}", refusal == EquipRefusal.Ok,
                            $"{Path.GetFileName(path)} {stack.Id} слот {slot}: {item.Id} {item.Category} требует {need} " +
                            $"— единственный источник «{source}», наш отказ {refusal}");
            }
        }
    }

    /// <summary>Вещь в слоте сохранения синтетической карты порта лежит там с начала партии — её положил генератор
    /// (<c>docs/synthetic-maps-port/*/facts.json</c>: отряд → слот → тип), а не надевание. Гейт <c>checkEquip</c> движок
    /// зовёт только при надевании (окно, обмен); загруженный слот он не проверяет и вещь в бою применяет: 16.10, корпус
    /// <c>20261016-port-synth</c> passive-001 — талисман <c>G000IG9116</c> у Архимага I1 (способности 7, 14, без 15)
    /// тратит заряд (<c>talisman_charge</c> seq 567965). Генератор с 16.10 даёт таким лидерам навык (<c>MOD_EQUIP</c> не
    /// годится — нужен навык уровня), но сохранения старой версии карты в корпусе остаются. Сопоставление — по имени
    /// сохранения (<c>passive_run</c> называет его по сценарию), отряду, слоту и типу.</summary>
    private static bool SynthEquipped(string save, string stackId, int slot, string itemType)
    {
        string file = Path.GetFileName(save);
        return SynthEquip.Value.Any(e => file.StartsWith(e.Map + "-", StringComparison.OrdinalIgnoreCase)
            && e.Stack.Equals(stackId, StringComparison.OrdinalIgnoreCase) && e.Slot == slot
            && e.Type.Equals(itemType, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] SlotNames = ["BANNER", "TOME", "BATTLE1", "BATTLE2", "ARTIFACT1", "ARTIFACT2", "BOOTS"];

    private static readonly Lazy<List<(string Map, string Stack, int Slot, string Type)>> SynthEquip = new(() =>
    {
        var rows = new List<(string, string, int, string)>();
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
        string dir = d is null ? "" : Path.Combine(d.FullName, "docs", "synthetic-maps-port");
        if (!Directory.Exists(dir)) return rows;
        foreach (string facts in Directory.EnumerateFiles(dir, "facts.json", SearchOption.AllDirectories))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(facts));
            if (!doc.RootElement.TryGetProperty("id", out var mapId) || mapId.GetString() is not { Length: > 0 } map
                || !doc.RootElement.TryGetProperty("stacks", out var stacks) || stacks.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            foreach (var st in stacks.EnumerateArray())
            {
                if (!st.TryGetProperty("equip", out var equip) || equip.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !st.TryGetProperty("id", out var stackId)) continue;
                string id = stackId.GetString() ?? "";
                foreach (var e in equip.EnumerateObject())
                    if (Array.IndexOf(SlotNames, e.Name) is >= 0 and var k) rows.Add((map, id, k, e.Value.GetString() ?? ""));
            }
        }
        return rows;
    });

    // ------------------------------------------------------------------------------ equip_chain

    /// <summary>Переходы соседних сохранений: слот, в котором вещь появилась или исчезла, против
    /// <c>MODIF_ID</c> юнитов отряда. Сравнение — по юниту: носитель обязан получить (потерять)
    /// <c>MOD_EQUIP</c>, не-носитель — нет.</summary>
    private static void ChainOnSaves(string name, IReadOnlyList<string> files, ProgressBook book, Counters c)
    {
        MapState? prev = null;
        foreach (var file in files)
        {
            var map = SaveMaps.Map(file);
            if (prev is not null)
                foreach (var (id, before) in prev.Stacks)
                {
                    if (!map.Stacks.TryGetValue(id, out var after)) continue;
                    var was = Equipment.Slots(before);
                    var now = Equipment.Slots(after);
                    for (int slot = 0; slot < was.Count; slot++)
                    {
                        if (JournalSaves.Same(was[slot], now[slot])) continue;
                        // Обмен вещей в одном слоте — сначала снятие, потом надевание (так же его
                        // пишет журнал парой item_unequip + item_equip).
                        if (was[slot].Length > 0) Moved(name, file, prev, map, before, after, slot, false, book, c);
                        if (now[slot].Length > 0) Moved(name, file, prev, map, before, after, slot, true, book, c);
                    }
                }
            prev = map;
        }
    }

    /// <summary>Одно надевание (<paramref name="equip"/>) или снятие в одном слоте.</summary>
    private static void Moved(string name, string file, MapState mapBefore, MapState mapAfter,
                              MapStack before, MapStack after, int slot, bool equip,
                              ProgressBook book, Counters c)
    {
        var instance = equip ? Equipment.Slots(after)[slot] : Equipment.Slots(before)[slot];
        if (book.ItemOf(equip ? mapAfter : mapBefore, instance) is not { } item)
        { c.Skip(Chain, "тип переложенного экземпляра не опознан"); return; }
        if (item.EquipModifierId.Length == 0)
        { c.Skip(Chain, $"{item.Category}: MOD_EQUIP пуст — в цепочку предмет ничего не кладёт"); return; }

        EquipOutcome outcome;
        if (equip)
        {
            if (book.Abilities(mapAfter, after, slot, out var why) is not { } abilities)
            { c.Skip(Chain, $"{Path.GetFileName(file)}: {why}"); return; }
            // Надевание считается по состоянию с уже освобождённым слотом: в одном окне вещь в
            // слоте бывает и снята, и заменена другой.
            var freed = Equipment.Slots(before).ToList();
            freed[slot] = "";
            outcome = Equipment.Equip(before, item, instance, slot, abilities.All,
                                      leaderAlive: true, slots: freed);
        }
        else outcome = Equipment.Unequip(before, slot, item);
        if (!outcome.Done)
        { c.Count(Chain, false, $"{name} {Path.GetFileName(file)} {before.Id} слот {slot}: модель отказала — {outcome.Refusal}"); return; }

        var bearers = outcome.Bearers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in before.UnitIds.Union(after.UnitIds, StringComparer.OrdinalIgnoreCase))
        {
            if (!mapBefore.Units.TryGetValue(unit, out var u1) || !mapAfter.Units.TryGetValue(unit, out var u2))
            { c.Skip(Chain, "юнита нет в одном из сохранений окна — цепочку сравнить не на чем"); continue; }
            // Перенос юнита между гарнизоном и отрядом зонд не пишет (T05): вошёл юнит в отряд до надевания или
            // после — неизвестно, а от этого зависит, получит ли он MOD_EQUIP (20260915-passive, t11-013 → t12-014:
            // S143UN009b из гарнизона S143FT0004 в S143KC0001 со знаменем G000IG1005).
            if (!before.UnitIds.Contains(unit, StringComparer.OrdinalIgnoreCase) || !after.UnitIds.Contains(unit, StringComparer.OrdinalIgnoreCase))
            { c.Skip(Chain, "юнит вошёл в отряд или вышел из него внутри окна — порядок с надеванием неизвестен"); continue; }
            bool had = Has(u1, outcome.ModifierId), has = Has(u2, outcome.ModifierId);
            bool bearer = bearers.Contains(unit);
            // Надевание: носитель модификатор получает, остальные остаются как были.
            // Снятие: носитель его теряет, остальные — как были.
            bool want = equip ? had || bearer : had && !bearer;
            c.Count(Chain, has == want,
                    $"{name} {Path.GetFileName(file)} {before.Id} слот {slot} " +
                    $"{(equip ? "надели" : "сняли")} {instance}/{item.Id} {item.Category}: у {unit} " +
                    $"{outcome.ModifierId} было {had}, стало {has}, ждали {want}, носитель {bearer}");
        }
    }

    private static bool Has(MapUnit unit, string modifier) =>
        unit.Modifiers.Any(m => m.Equals(modifier, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------------------------ прочее

    /// <summary>Все сохранения корпуса: каталоги на «_» выведены из оборота (как в
    /// <see cref="SaveChains"/>).</summary>
    private static IEnumerable<string> AllSaves() =>
        Directory.EnumerateDirectories(CorpusFiles.Dir)
            .Where(d => !Path.GetFileName(d).StartsWith('_'))
            .OrderBy(d => d, StringComparer.Ordinal)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.sg").OrderBy(f => f, StringComparer.Ordinal));

    private static string Name(string path) =>
        $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";

    private static string Show(IReadOnlySet<int> abilities) =>
        abilities.Count == 0 ? "нет" : string.Join(",", abilities.Order());
}
