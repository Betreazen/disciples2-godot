using D2.Data;
using D2.Data.Corpus;
using D2.Rules.Magic;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Один бой глазами задачи 10: когда он кончился и какие экземпляры предметов в нём
/// применяли. <see cref="Applied"/> — наше «применён»: событие <c>immune</c> с
/// <c>always_only = false</c> по атаке предмета или (схема 6.0) <c>attack_hit</c> по <c>attack_new</c> экземпляра.
/// <see cref="Ambiguous"/> — атака, которую в этом бою
/// строили два разных экземпляра: кто из них применён, журнал не различает.</summary>
public sealed record BattleItemFacts(long EndSeq, long EndT, IReadOnlySet<string> Applied,
                                     IReadOnlySet<string> Ambiguous);

/// <summary>Одно применение посоха, восстановленное по <b>журналу</b>: строка <c>wand_cast</c> (тогда известен и
/// экземпляр <paramref name="Instance"/>) или, в журналах без этого хука, трата казны, равная <c>CASTING</c> посоха
/// этого игрока (экземпляр null — подбирается по типу).</summary>
public sealed record StaffCast(long Seq, long T, long Day, string Player, Item Item, Bank Before, string? Instance = null);

/// <summary>
/// Харнесс задачи 10 Ф7: расход сферы, талисмана и посоха против корпуса.
///
/// <list type="bullet">
/// <item><c>talisman_charges</c> — остаток зарядов каждого талисмана в каждом сохранении против
/// модели (<see cref="Charges.TalismanAfterBattle"/>), прогнанной по боям между снимками;</item>
/// <item><c>orb_consumed</c> — применённая сфера из боевого слота исчезает из сохранения,
/// неприменённая остаётся;</item>
/// <item><c>item_applied</c> — наше «применён» совпадает со следствием: снятием на
/// <c>battle_end</c>, исчезновением экземпляра или тратой заряда;</item>
/// <item><c>staff_daily</c> — длина второго списка <c>CMidSpellCast</c> сохранения равна нашей;</item>
/// <item><c>staff_keys</c> — и сами ключи (игрок плюс экземпляр <c>IM</c>) те же;</item>
/// <item><c>staff_mana</c> — сдвиг маны в <c>bank</c> равен <c>CASTING</c> применённых посохов.</item>
/// </list>
///
/// <para><b>Два независимых водителя, чтобы сверки не стали тавтологией.</b> Посох виден с двух
/// сторон: в журнале — строкой <c>wand_cast</c>, если хук объявлен в строке <c>start</c> (строки есть только в
/// <c>20260916-synth</c>), иначе тратой казны, в сохранении — записью второго списка <c>CMidSpellCast</c>. Поэтому
/// <c>staff_daily</c>/<c>staff_keys</c> считаются от <b>журнала</b> (какие траты равны <c>CASTING</c>
/// посоха этого игрока) и сверяются с сохранением, а <c>staff_mana</c> — наоборот, от
/// <b>сохранения</b> (какие записи появились) и сверяется с журналом. Ни одна из двух сверок не
/// смотрит на ту же величину, из которой выведена.</para>
///
/// <para><b>День берётся у сохранения, а не у файла.</b> Автосохранение ложится на диск с
/// отставанием (план Ф7 §1.4, П§11.5): в <c>m17-charges</c> файл <c>034</c> моложе дневного тика
/// второго дня, а содержимое у него первого дня (<c>CUR_TURN = 1</c> и суточный список ещё полон).
/// Поэтому ожидаемый список строится по <c>CUR_TURN</c> самого снимка, а траты журнала относятся к
/// дню по последней строке <c>income</c> перед ними.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class ChargesHarnessTests(ITestOutputHelper output)
{
    private const string ChargesKey = "talisman_charges";
    private const string OrbKey = "orb_consumed";
    private const string AppliedKey = "item_applied";
    private const string DailyKey = "staff_daily";
    private const string KeysKey = "staff_keys";
    private const string ManaKey = "staff_mana";

    /// <summary>Боевые слоты отряда — 2 и 3 (план Ф7 §1.5, задача 9): только они и уходят в бой.</summary>
    private static readonly int[] BattleSlots = [2, 3];

    [SkippableFact] public void Charges_MatchTheCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Dir.Length > 0 && Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var book = ProgressBook.Load(GamePaths.Globals);
        var c = new Counters();
        c.Note($"GVars.TALIS_CHRG = {book.Vars.TalismanCharges} — заряды нового талисмана; " +
               "применение = immune с always_only = false по атаке предмета (attack_new с impl = IM " +
               "применением не является, П§16.4).");

        foreach (var path in CorpusFiles.Journals())
        {
            if (!TreasuryJournal.HasBank(path)) { c.Skip(AppliedKey, "журнал без казны — оси t нет, следствия к бою не привязать"); continue; }
            var j = TreasuryJournal.Load(path);
            if (j.Saves.Count == 0) { c.Skip(AppliedKey, "манифест не объявил сохранений — следствий не видно"); continue; }

            var battles = Battles(path, j);
            var casts = StaffCasts(j, book, c, path);
            foreach (var epoch in j.Saves.GroupBy(s => j.EpochOf(s.T)).OrderBy(g => g.Key))
            {
                var saves = epoch.OrderBy(s => s.T).ToList();
                Talismans(Name(path), j, saves, battles, book, c);
                // Загрузка карты меняет карту целиком: применения чужой эпохи к этим снимкам не относятся.
                Staffs(Name(path), j, saves, casts.Where(x => j.EpochOf(x.T) == epoch.Key).ToList(), book, c);
            }
        }

        c.Print(output, "задача 10 — сфера, талисман, посох: расход");
        c.Require(ChargesKey, OrbKey, AppliedKey, DailyKey, KeysKey, ManaKey);
    }

    // ------------------------------------------------------- что в бою применяли (журнал целиком)

    /// <summary>Бои журнала: конец боя и применённые в нём экземпляры. Полный поток событий читается
    /// только у журналов, где вообще строилась атака предмета (<c>impl</c> вида <c>S…IM…</c>): без
    /// такой строки применения быть не может — атаку предмета движок строит перед каждым его
    /// применением (<c>getItemAttack</c>), поэтому пустое множество тут не «не смотрели», а
    /// «применений нет».</summary>
    private static IReadOnlyList<BattleItemFacts> Battles(string path, TreasuryJournal j)
    {
        bool itemAttacks = File.ReadLines(path).Any(l => l.Contains("\"impl\":\"S", StringComparison.OrdinalIgnoreCase)
                                                      || l.Contains("\"impl\": \"S", StringComparison.OrdinalIgnoreCase));
        if (!itemAttacks)
            return j.Events.OfType<BattleEndEvent>().Where(e => e.T is not null)
                .Select(e => new BattleItemFacts(e.Seq, e.T!.Value, new HashSet<string>(), new HashSet<string>()))
                .ToList();

        var facts = new List<BattleItemFacts>();
        // Атака предмета -> экземпляры, которые её строили в этом бою; и атаки, у которых журнал
        // спросил иммунитет «по-настоящему» (always_only = false).
        var byAttack = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var checkedAttacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Схема 6.0: attack_hit называет attack_new своего удара (new_seq), и экземпляр известен без immune.
        // Ранг 1 по журналу: ai-q37 бой 2 — талисман-призыв (класс 17) immune не зовёт, но attack_new 316095 →
        // attack_hit 316097 → talisman_charge 316270, заряды 5 → 4; бой 1 — attack_new 312241 без attack_hit, заряд цел.
        var built = new Dictionary<long, string>();
        var struck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in JournalCache.Read(path))
        {
            switch (e)
            {
                case AttackNewEvent a when IsInstance(a.Impl):
                    if (!byAttack.TryGetValue(a.Attack, out var set))
                        byAttack[a.Attack] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(a.Impl.ToUpperInvariant());
                    built[a.Seq] = a.Impl.ToUpperInvariant();
                    break;
                case AttackHitEvent h when built.TryGetValue(h.NewSeq, out var instance):
                    struck.Add(instance);
                    break;
                case ImmuneEvent i when !i.AlwaysOnly:
                    checkedAttacks.Add(i.Attack);
                    break;
                case BattleEndEvent { T: not null } end:
                    var applied = new HashSet<string>(struck, StringComparer.OrdinalIgnoreCase);
                    var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (attack, instances) in byAttack.Where(p => checkedAttacks.Contains(p.Key)))
                        if (instances.Count == 1) applied.Add(instances.First());
                        else foreach (var id in instances.Where(id => !struck.Contains(id))) ambiguous.Add(id);
                    facts.Add(new BattleItemFacts(end.Seq, end.T!.Value, applied, ambiguous));
                    byAttack.Clear();
                    checkedAttacks.Clear();
                    built.Clear();
                    struck.Clear();
                    break;
            }
        }
        return facts;
    }

    /// <summary>Экземпляр (<c>S143IM0004</c>), а не тип (<c>G000IG9137</c>): у экземпляра третья и
    /// четвёртая буквы класса — <c>IM</c>, а сам id начинается с номера сценария.</summary>
    private static bool IsInstance(string id) =>
        id.Length > 6 && (id[0] is 'S' or 's') && id.Contains("IM", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------ талисман, сфера и «что считать применением»

    /// <summary>Прогон по снимкам одной загрузки карты: между двумя снимками применяются бои, и
    /// модель зарядов сверяется с записями <c>CMidTalismanCharges</c>, а исчезновение сфер — с
    /// составом <c>CMidItem</c>.</summary>
    private static void Talismans(string name, TreasuryJournal j, List<SaveAt> saves,
                                  IReadOnlyList<BattleItemFacts> battles, ProgressBook book, Counters c)
    {
        var model = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long prevT = long.MinValue;
        SaveAt? prev = null;
        foreach (var save in saves)
        {
            var window = battles.Where(b => b.EndT > prevT && b.EndT <= save.T).OrderBy(b => b.EndSeq).ToList();
            if (prev is not null && window.Count > 0) Window(name, j, prev, save, window, model, book, c);
            // Проданный талисман теряет запись продажей, а не боем (20260919-human: S073IM00be = IG9138 продан
            // site_sell_item seq 15347639 между t60-098 и t60-099 с 5 зарядами).
            var sold = j.Events.OfType<SiteSellItemEvent>().Where(e => e.Ret && e.T > prevT && e.T <= save.T)
                .Select(e => e.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var id in save.Map.TalismanCharges.Keys.Union(model.Keys, StringComparer.OrdinalIgnoreCase).ToList())
            {
                bool known = model.TryGetValue(id, out int ours);
                bool read = save.Map.TalismanCharges.TryGetValue(id, out int theirs);
                if (!known)
                {
                    // Талисман, которого модель ещё не видела: первый снимок эпохи или новая вещь.
                    model[id] = theirs;
                    c.Skip(ChargesKey, prev is null
                        ? "начальное состояние: заряды первого снимка эпохи взяты как есть"
                        : "талисман появился между снимками (куплен или создан) — прошлого остатка нет");
                    continue;
                }
                if (!read && sold.Contains(id))
                {
                    c.Skip(ChargesKey, "талисман продан между снимками — запись снята продажей, а не расходом");
                    model.Remove(id);
                    continue;
                }
                c.Count(ChargesKey, read ? ours == theirs : Charges.Destroyed(ours),
                        $"{name} {Path.GetFileName(save.Path)}: талисман {id} — наше {ours}, " +
                        $"в сохранении {(read ? theirs.ToString() : "записи нет")}");
                if (!read) model.Remove(id);
            }
            (prev, prevT) = (save, save.T);
        }

        if (saves.Any(s => s.Map.TalismanCharges.Count > 0)) c.Note($"{name}: заряды по снимкам — {Series(saves)}");
    }

    /// <summary>Ряд остатков по снимкам эпохи, соседние повторы свёрнуты (так же печатает ряд
    /// <c>sg_known</c> задачи 2): у <c>m17-charges</c> это ровно восемь точек таблицы П§18.</summary>
    private static string Series(List<SaveAt> saves)
    {
        var rows = saves.Select(s => $"ход {s.Map.Turn}: " + (s.Map.TalismanCharges.Count == 0 ? "записей нет"
            : string.Join(",", s.Map.TalismanCharges.OrderBy(p => p.Key, StringComparer.Ordinal)
                                .Select(p => $"{p.Key}={p.Value}")))).ToList();
        var folded = new List<string>();
        foreach (var row in rows)
            if (folded.Count == 0 || Tail(folded[^1]) != Tail(row)) folded.Add(row);
        return string.Join("; ", folded);

        static string Tail(string row) => row[(row.IndexOf(':') + 1)..];
    }

    /// <summary>Что было в боевых слотах в этом окне: экземпляр, отряд, слот, предмет.
    ///
    /// <para>Слоты снимка «до» — плюс то, что надели в боевой слот <b>уже внутри окна</b>, до
    /// последнего боя. Второе нужно не для полноты: вторая сфера <c>m17-charges</c> попала в слот 3
    /// между снимками <c>037</c> и <c>038</c> и была применена и израсходована в бою 3 — по слотам
    /// снимка «до» её не видно вовсе, и без этой добавки самое сильное наблюдение сверки выпадало.
    /// Отряд и слот называет само событие <c>item_equip</c>, а тип экземпляра ещё есть в снимке
    /// (расходуется он позже).</para>
    /// <para>Экземпляр берётся один раз (<c>seen</c>): в окне вещь бывает переложена туда-обратно.
    /// <c>ponytail:</c> надетое и снятое внутри окна ДО боя здесь неразличимо от надетого к бою —
    /// таких случаев в корпусе нет, а различит их только событие расхода в схеме зонда (§3).</para></summary>
    private static List<(string Instance, MapStack Stack, int Slot, Item Item)> Carried(
        TreasuryJournal j, SaveAt before, SaveAt after, List<BattleItemFacts> battles, ProgressBook book)
    {
        var carried = new List<(string Instance, MapStack Stack, int Slot, Item Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stack in before.Map.Stacks.Values)
        {
            var slots = Equipment.Slots(stack);
            foreach (int slot in BattleSlots)
            {
                if (slots.Count <= slot || slots[slot].Length == 0) continue;
                if (book.ItemOf(before.Map, slots[slot]) is { } item && seen.Add(slots[slot].ToUpperInvariant()))
                    carried.Add((slots[slot].ToUpperInvariant(), stack, slot, item));
            }
        }

        long last = battles.Max(b => b.EndT);
        foreach (var e in j.Events.OfType<ItemEquipEvent>()
                     .Where(e => e.Equip && e.Apply && e.Ret && e.Item is not null
                                 && e.T > before.T && e.T <= last && BattleSlots.Contains((int)e.Slot))
                     .OrderBy(e => e.Seq))
        {
            if (!seen.Add(e.Item!.ToUpperInvariant())) continue;
            if (!before.Map.Stacks.TryGetValue(e.Stack, out var stack)) continue;
            var fresh = book.ItemOf(before.Map, e.Item!) ?? book.ItemOf(after.Map, e.Item!);
            if (fresh is null) continue;
            carried.Add((e.Item!.ToUpperInvariant(), stack, (int)e.Slot, fresh));
        }
        return carried;
    }

    /// <summary>Окно между двумя снимками: бои окна против следствий применения. Окно, а не бой:
    /// в больших партиях между автосохранениями лежит до нескольких боёв, и «кто из них съел вещь»
    /// по снимкам не разделить, а <b>множество</b> применённого за окно — разделить можно.
    /// <para>Заряды талисмана при этом считаются <b>по каждому бою</b> (иначе два применения в одном
    /// окне стоили бы один заряд), а сверка идёт со снимком в конце окна.</para>
    /// <para>Зелья из сверки исключены: их пьют и вне боя, а своей боевой атаки у зелья нет вовсе —
    /// поэтому исчезновение зелья применения в бою не доказывает (в первой большой партии ровно так
    /// и вышло: четыре лечебных зелья исчезли за окно с четырьмя боями).</para></summary>
    private static void Window(string name, TreasuryJournal j, SaveAt before, SaveAt after,
                               List<BattleItemFacts> battles, Dictionary<string, int> model,
                               ProgressBook book, Counters c)
    {
        var carried = Carried(j, before, after, battles, book);

        // Слоты, снятые ровно на конце боя: так журнал пишет расход применённой вещи (П§18).
        var ends = battles.Select(b => b.EndT).ToHashSet();
        var unequipped = j.Events.OfType<ItemEquipEvent>()
            .Where(e => !e.Equip && e.Apply && e.Ret && ends.Contains(e.T))
            .Select(e => (e.Stack, (int)e.Slot)).ToHashSet();
        var sold = j.Events.OfType<SiteSellItemEvent>().Where(e => e.T > before.T && e.T <= after.T)
            .Select(e => e.Item.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Заряды — по каждому бою окна в порядке журнала.
        foreach (var b in battles)
            foreach (var (instance, _, _, item) in carried)
            {
                if (item.Category != ItemCategory.Talisman || !model.TryGetValue(instance, out int charges)) continue;
                int next = Charges.TalismanAfterBattle(charges, b.Applied.Contains(instance));
                if (Charges.Destroyed(next)) model.Remove(instance);
                else model[instance] = next;
            }

        var (ours, evidence, whole) = Judge(name, before, after, battles, carried, unequipped, sold, c);

        if (ours.Count > 0)
            c.Note($"{name} {Path.GetFileName(after.Path)}: применено [{Show(ours)}], заряды после окна — " +
                   (model.Count == 0 ? "записей нет"
                    : string.Join(",", model.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"))));
        if (!whole) { c.Skip(AppliedKey, "в окне есть неразделимый экземпляр — множество применённого не сверить"); return; }
        c.Count(AppliedKey, ours.SetEquals(evidence),
                $"{name} окно {Path.GetFileName(before.Path)} → {Path.GetFileName(after.Path)} " +
                $"(боёв {battles.Count}, seq {string.Join(",", battles.Select(b => b.EndSeq))}): " +
                $"наше применение [{Show(ours)}], следствия [{Show(evidence)}]");
    }

    /// <summary>Что мы считаем применённым в окне и что об этом говорят сохранения с журналом.
    /// <list type="bullet">
    /// <item><b>наше</b> — экземпляр, чью атаку журнал проверял на иммунитет
    /// (<c>always_only = false</c>) хотя бы в одном бою окна;</item>
    /// <item><b>следствие</b> — экземпляр исчез из состава <c>CMidItem</c>, потерял заряд или был
    /// снят ровно на <c>battle_end</c>.</item>
    /// </list>
    /// Попутно считается <c>orb_consumed</c>: сфера исчезает тогда и только тогда, когда применена.
    /// <c>whole = false</c> — в окне есть экземпляр, по которому судить нельзя (неразделимая атака,
    /// продажа, пропавший отряд), и множества за окно сверять уже не на чем.</summary>
    private static (HashSet<string> Ours, HashSet<string> Evidence, bool Whole) Judge(
        string name, SaveAt before, SaveAt after, List<BattleItemFacts> battles,
        List<(string Instance, MapStack Stack, int Slot, Item Item)> carried,
        HashSet<(string Stack, int Slot)> unequipped, HashSet<string> sold, Counters c)
    {
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool whole = true;
        foreach (var (instance, stack, slot, item) in carried)
        {
            if (item.IsPotion) continue;
            if (battles.Any(b => b.Ambiguous.Contains(instance)))
            { whole = false; c.Skip(AppliedKey, "атаку экземпляра в бою строил не он один — применение не разделить"); continue; }
            if (sold.Contains(instance))
            { whole = false; c.Skip(AppliedKey, "экземпляр в этом окне продан — исчезновение не от боя"); continue; }
            if (!after.Map.Stacks.ContainsKey(stack.Id))
            { whole = false; c.Skip(AppliedKey, "отряда нет в снимке после окна"); continue; }

            bool applied = battles.Any(b => b.Applied.Contains(instance));
            bool gone = !after.ItemTypes.ContainsKey(instance);
            bool spent = item.Category == ItemCategory.Talisman
                         && (gone || before.Map.TalismanCharges.TryGetValue(instance, out int had)
                                     && after.Map.TalismanCharges.TryGetValue(instance, out int left) && left < had);
            if (applied) ours.Add(instance);
            // Снятие на конце боя — расход, только если лидер жив: у отряда с павшим лидером движок снимает все слоты сам (0x4379c6 →
            // 0x437c4e, unEquipItem 0x5e746d), и вещь остаётся в сумке (20261016g-port-synth, карта 8, BI: S143IM000c, seq 558981).
            bool fallenLeader = after.Map.Stacks.TryGetValue(stack.Id, out var later) && !later.LeaderAlive;
            if (gone || spent || (unequipped.Contains((stack.Id, slot)) && !fallenLeader)) evidence.Add(instance);

            // Сфера: исчезает применённая, надетая без применения остаётся.
            if (item.Category == ItemCategory.Orb)
                c.Count(OrbKey, gone == item.IsDestroyedAfterBattle(applied),
                        $"{name} окно {Path.GetFileName(before.Path)} → {Path.GetFileName(after.Path)}: " +
                        $"{instance}/{item.Id} {item.Category} слот {slot} " +
                        $"{(applied ? "применена" : "не применялась")} — в снимке {(gone ? "нет" : "есть")}");
        }
        return (ours, evidence, whole);
    }

    // ------------------------------------------------------------------------------------- посохи

    /// <summary>Применения посоха по журналу: строка <c>bank</c>, в которой игрок потерял ровно
    /// <c>CASTING</c> одного из своих посохов и не потерял золота (у <c>CASTING</c> золотой части нет
    /// ни у одного из 21 посоха ванили — <see cref="Item.CastingCost"/>). Так — только в журналах, где хука
    /// <c>wand_cast</c> нет в строке <c>start</c>; где он есть, источник — сами строки <c>wand_cast</c> с экземпляром:
    /// подбор по цене ошибается, когда у игрока два посоха одной цены (ai-q34: <c>S135IM003f</c> = IG6001 и
    /// <c>S135IM0040</c> = IG6012, <c>wand_cast</c> 191240 и 239796 — первый, 247655 — второй).
    /// <para>День — последний ход <c>income</c> <b>своей загрузки карты</b>: максимум по файлу унёс применение дня 1
    /// seq 191240 в день 2 (предыдущая карта ai-t08c дошла до хода 2).</para></summary>
    private static IReadOnlyList<StaffCast> StaffCasts(TreasuryJournal j, ProgressBook book, Counters c, string path)
    {
        bool wandHook = File.ReadLines(path).FirstOrDefault()?.Contains("\"wand_cast\"", StringComparison.Ordinal) == true;
        var casts = new List<StaffCast>();
        long day = -1;
        int epoch = int.MinValue;
        foreach (var e in j.Events.OrderBy(e => e.Seq))
        {
            if (TreasuryJournal.TimeOf(e) is { } t && j.EpochOf(t) != epoch) (epoch, day) = (j.EpochOf(t), -1);
            if (e is IncomeEvent { Turn: { } turn } && turn > day) day = turn;
            if (wandHook)
            {
                if (e is not WandCastEvent { Ret: true } w) continue;
                // Свиток тоже идёт строкой wand_cast и тоже ложится в суточный список (StaffUses.Use).
                if (j.SaveNear(w.T) is not { } save) { c.Skip(DailyKey, $"{Name(path)}: wand_cast {w.Seq} — в загрузке нет снимков"); continue; }
                Item? wand = book.ItemOf(save.Map, w.Item);
                if (wand is null && UsedUpScroll(j, w, book) is { } scroll)
                {
                    c.Skip(DailyKey, $"{Name(path)}: wand_cast {w.Seq} — свиток {w.Item} израсходован до первого снимка загрузки, тип не назвать; " +
                                     "в суточный список идёт как свиток (казна не изменилась)");
                    wand = scroll;
                }
                if (wand is not { Category: ItemCategory.Wand or ItemCategory.Scroll })
                { c.Skip(DailyKey, $"{Name(path)}: wand_cast {w.Seq} — {w.Item} в снимке своей загрузки не посох и не свиток"); continue; }
                casts.Add(new StaffCast(w.Seq, w.T, day, w.Player, wand, w.Before, w.Item.ToUpperInvariant()));
                continue;
            }
            if (e is not BankEvent bank) continue;
            var loss = bank.Before - bank.After;
            if (loss.Gold != 0 || loss == Bank.Zero) continue;
            if (loss.Infernal < 0 || loss.Life < 0 || loss.Death < 0 || loss.Runic < 0 || loss.Grove < 0) continue;

            var near = j.SaveNear(bank.T);
            if (near is null) continue;
            var staffs = StaffsOf(near.Map, bank.Player, book);
            var item = staffs.Values.FirstOrDefault(i => i.CastingCost == loss);
            if (item is null)
            { c.Skip(DailyKey, $"{Name(path)}: трата маны не равна CASTING ни одного посоха игрока — не применение посоха"); continue; }
            casts.Add(new StaffCast(bank.Seq, bank.T, day, bank.Player, item, bank.Before));
        }
        return casts;
    }

    /// <summary>Свиток, применённый и израсходованный раньше первого снимка своей загрузки (<c>20261016e-av</c>, карта 7, seq 13641504:
    /// <c>S143IM000c</c> есть в суточном списке <c>t01-045</c>, а самого экземпляра ни в одном снимке нет): его нет ни в одном снимке
    /// загрузки, а казна не изменилась (<c>before</c> = <c>after</c>) — посох так не применяется, если у каждого посоха <c>GItem</c>
    /// непуст <c>CASTING</c>. Суточному списку (<see cref="StaffUses.Use"/>) от предмета нужна только категория, поэтому берётся
    /// любой свиток справочника. Иначе — null.</summary>
    private static Item? UsedUpScroll(TreasuryJournal j, WandCastEvent w, ProgressBook book)
    {
        if (w.Before != w.After) return null;
        if (book.Prices.Items.Values.Any(i => i.Category == ItemCategory.Wand && i.CastingCost == Bank.Zero)) return null;
        if (j.Saves.Any(s => j.EpochOf(s.T) == j.EpochOf(w.T) && book.ItemOf(s.Map, w.Item) is not null)) return null;
        // Свиток расходуется: после израсходованного применений того же экземпляра быть не может.
        if (j.Events.OfType<WandCastEvent>().Any(x => x.Seq > w.Seq && JournalSaves.Same(x.Item, w.Item) && j.EpochOf(x.T) == j.EpochOf(w.T)))
            return null;
        return book.Prices.Items.Values.Where(i => i.Category == ItemCategory.Scroll).OrderBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Посохи игрока в этом снимке: экземпляр -> предмет. Берутся и инвентарь отряда, и
    /// слоты (посох не надевается — план Ф7 §1.5, — но искать его в слотах дешевле, чем доказывать
    /// обратное).</summary>
    private static Dictionary<string, Item> StaffsOf(MapState map, string player, ProgressBook book)
    {
        var found = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var stack in map.Stacks.Values.Where(s => JournalSaves.Same(s.Owner, player)))
            foreach (var instance in stack.Items.Concat(stack.Equipment).Where(x => x.Length > 0))
                if (book.ItemOf(map, instance) is { Category: ItemCategory.Wand } item)
                    found[instance.ToUpperInvariant()] = item;
        return found;
    }

    /// <summary>Суточный список посохов: наш против второго списка <c>CMidSpellCast</c> снимка.
    /// Экземпляр под трату выбирается первым неприменённым сегодня посохом того же типа у того же
    /// игрока — так и работает правило «раз в сутки на экземпляр» (П§19: два одинаковых
    /// <c>IG6001</c> у двух героев сработали в один день оба).</summary>
    private static void Staffs(string name, TreasuryJournal j, List<SaveAt> saves, IReadOnlyList<StaffCast> casts,
                               ProgressBook book, Counters c)
    {
        foreach (var save in saves)
        {
            var today = casts.Where(x => x.Day == save.Map.Turn && x.T <= save.T).OrderBy(x => x.Seq).ToList();
            var staff = new StaffUses();
            bool complete = true;
            foreach (var cast in today)
            {
                var mine = StaffsOf(save.Map, cast.Player, book)
                    .Where(p => p.Value.Id.Equals(cast.Item.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
                var free = cast.Instance ?? mine.FirstOrDefault(id => !staff.UsedToday(cast.Player, id));
                if (free is null)
                { complete = false; c.Skip(DailyKey, $"{name}: посоха {cast.Item.Id} игрока {cast.Player} в снимке нет — экземпляр не назвать"); continue; }
                var use = staff.Use(cast.Item, cast.Player, free, cast.Before);
                if (!use.Done)
                { complete = false; c.Count(DailyKey, false, $"{name}: модель отказала посоху {free}: {use.Refusal}"); continue; }
            }
            if (!complete) continue;

            var theirs = save.Map.ItemCasts.Select(r => (r.PlayerId.ToUpperInvariant(), r.Origin.ToUpperInvariant()))
                              .ToHashSet();
            c.Count(DailyKey, staff.Count == save.Map.ItemCasts.Count,
                    $"{name} {Path.GetFileName(save.Path)} (ход {save.Map.Turn}): наших применений {staff.Count}, " +
                    $"в сохранении {save.Map.ItemCasts.Count}");
            c.Count(KeysKey, staff.Keys.ToHashSet().SetEquals(theirs),
                    $"{name} {Path.GetFileName(save.Path)}: наши ключи [{Keys(staff.Keys)}], " +
                    $"в сохранении [{Keys(theirs)}]");
            if (staff.Count > 0 || theirs.Count > 0)
                c.Note($"{name} {Path.GetFileName(save.Path)} (ход {save.Map.Turn}): суточный список — наш " +
                       $"[{Keys(staff.Keys)}], в сохранении [{Keys(theirs)}]");
        }

        Mana(name, j, saves, book, c);
    }

    /// <summary>Цена применения: записи, появившиеся между снимками, против траты казны в том же
    /// окне. Здесь водитель обратный — сохранение говорит, ЧТО применили, журнал показывает,
    /// СКОЛЬКО ушло.</summary>
    private static void Mana(string name, TreasuryJournal j, List<SaveAt> saves, ProgressBook book, Counters c)
    {
        for (int i = 1; i < saves.Count; i++)
        {
            var (before, after) = (saves[i - 1], saves[i]);
            var fresh = after.Map.ItemCasts
                .Where(r => !before.Map.ItemCasts.Any(x => JournalSaves.Same(x.Origin, r.Origin)
                                                        && JournalSaves.Same(x.PlayerId, r.PlayerId)))
                .ToList();
            if (fresh.Count == 0) continue;
            if (i == 1 && before.Map.ItemCasts.Count > 0)
            { c.Skip(ManaKey, "в первом снимке эпохи список уже непуст — казны до применения нет"); continue; }

            var price = Bank.Zero;
            bool known = true;
            foreach (var r in fresh)
            {
                // Свиток после применения исчезает — его тип берётся из снимка «до».
                if ((book.ItemOf(after.Map, r.Origin) ?? book.ItemOf(before.Map, r.Origin))
                    is { Category: ItemCategory.Wand or ItemCategory.Scroll } item) price += item.CastingCost;
                else { known = false; c.Skip(ManaKey, $"{name}: запись CMidSpellCast на {r.Origin} — это не посох и не свиток снимка"); }
            }
            if (!known) continue;

            var human = after.Map.Players.Values.FirstOrDefault(p => JournalSaves.Same(p.Id, fresh[0].PlayerId));
            var moved = human is null ? null : before.Map.Players.TryGetValue(human.Id, out var was) ? was.Bank - human.Bank : null;
            if (moved is null) { c.Skip(ManaKey, $"{name}: игрока {fresh[0].PlayerId} нет в обоих снимках"); continue; }

            // Прочие траты окна с причиной в журнале (заклинания, наём, жезлы, изучение) вычитаются: 20260919-human
            // t62-102 → t62-103 — два свитка с пустым CASTING и два spell_cast (G000SS0040, G000SS0088) на 220/290/240/750.
            moved -= j.Events.OfType<SpendEvent>()
                .Where(e => e is not WandCastEvent && e.Ret && e.T > before.T && e.T <= after.T && JournalSaves.Same(e.Player, human!.Id))
                .Aggregate(Bank.Zero, (sum, e) => sum + e.Price);
            // В окне между снимками бывает и дневной доход: сверяется не вся разность, а то, что
            // ушло сверх прихода — по каждой валюте отдельно (доход маны прибавляет, применение отнимает).
            var lost = new Bank(Math.Max(0, moved.Gold), Math.Max(0, moved.Infernal), Math.Max(0, moved.Life),
                                Math.Max(0, moved.Death), Math.Max(0, moved.Runic), Math.Max(0, moved.Grove));
            c.Count(ManaKey, lost == price,
                    $"{name} {Path.GetFileName(before.Path)} → {Path.GetFileName(after.Path)}: " +
                    $"записей {fresh.Count} ({string.Join(",", fresh.Select(r => r.Origin))}), CASTING {price}, " +
                    $"казна просела на {lost} (вся разность {moved})");
        }
    }

    // ------------------------------------------------------------------------------------- прочее

    private static string Name(string path) =>
        $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";

    private static string Show(IReadOnlySet<string> ids) =>
        ids.Count == 0 ? "пусто" : string.Join(",", ids.Order(StringComparer.Ordinal));

    private static string Keys(IEnumerable<(string Player, string Item)> keys) =>
        string.Join(",", keys.Select(k => $"{k.Player}/{k.Item}").Order(StringComparer.Ordinal));
}
