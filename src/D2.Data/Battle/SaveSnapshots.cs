using System.Text.Json;
using D2.Data.Corpus;
using D2.Data.Sg;
using KSg = Kaitai.Sg;

namespace D2.Data.Battle;

/// <summary>Юнит в снимке <c>.sg</c>: базовый тип, уровень и цепочка <c>MODIF_ID</c> в порядке
/// хранения (тот же порядок, в котором её накладывает <c>EffectiveStats</c>).
/// <para><b>Цепочка — это ВСЁ, что движок навесил на юнита</b>: и выбранные при повышении уровня
/// способности лидера (<c>GleaUpg.MODIF_ID</c>), и надетые предметы. Предмет отдельного места не
/// занимает: на <c>item_equip</c> движок кладёт <c>GItem.MOD_EQUIP</c> прямо в
/// <c>MidUnit.Modifiers</c>, и знаменосец (<c>G000UM2107</c> «Banner boost damage 1») на ходу 47
/// партии <c>20260910-passive</c> появляется сразу у пяти юнитов одного отряда. Поэтому ни
/// <c>GItem</c>, ни слоты <c>MidStack</c> (<c>BANNER</c>, <c>TOME</c>, …) здесь не читаются: они
/// говорят, ЧТО надето, а цепочка — что из этого уже действует.</para>
/// <para>Мимо цепочки идут только сфера и талисман: у них заполнен <c>ATTACK_ID</c>, а не
/// <c>MOD_EQUIP</c> (все 84 записи ванили, см. <c>ItemCategory</c>), и в бой они входят своей
/// атакой, а не прибавкой к чужой.</para></summary>
public sealed record SavedUnit(string Type, int Level, IReadOnlyList<string> Modifiers);

/// <summary>Укрепление, внутри которого стоит юнит снимка, — в том объёме, в каком его нужно знать
/// защите (<c>D2.Rules.Map.Fortifications.Protection</c>, задача 5 Ф7): столица или деревня, уровень
/// деревни (<c>SIZE</c>), её <c>PROTECT_B</c> и раса владельца (<c>MidPlayer.RACE_ID</c>).
/// <para>Внутри укрепления стоят двое: гарнизон (<c>City.GROUP</c>) и отряд-визитёр
/// (<c>City.STACK</c>) — оба получают защиту, и оба попадают сюда (план Ф7 §1.4).</para></summary>
public sealed record SavedFort(bool Capital, int Size, string ProtectBonus, string RaceId);

/// <summary>Сторона юнита для правил сложности: сложность партии (<c>ScenarioInfo.DIFFGAME</c>, <c>Ldiff.dbf</c>
/// 0…3) и человек ли владелец его группы (<c>MidPlayer.IS_HUMAN</c> владельца отряда <c>MidStack.OWNER</c> или
/// гарнизона <c>City.OWNER</c>).</summary>
public sealed record SavedSide(int Difficulty, bool Human);

/// <summary>Отряд из сохранения — для бегства ИИ (<c>D2.Rules.Battle.AiRetreat</c>): приказ и его цель
/// (<c>MidStack.ORDER</c>/<c>ORDER_TARG</c>, <c>CMidStack+0xa8/+0xac</c>), приказ ИИ (<c>AIORDER</c>, <c>+0xb8</c>),
/// очки хода (<c>MOVE</c>, <c>+0x98</c>), игрок-человек (<c>MidPlayer.IS_HUMAN</c>) и раса владельца
/// (<c>MidPlayer.RACE_ID</c>).</summary>
/// <para><see cref="LeaderId"/> и <see cref="BattleItems"/> — лидер и типы (<c>GItem</c>) предметов в слотах
/// <c>BATTLE1</c>/<c>BATTLE2</c> (пустой слот — пустая строка): политика ИИ применяет их в бою.</para>
public sealed record SavedStack(int Order, string OrderTarget, int AiOrder, int Move, bool Human, string RaceId)
{
    public string LeaderId { get; init; } = "";
    public IReadOnlyList<string> BattleItems { get; init; } = [];
}

/// <summary>Сохранения <c>.sg</c>, снятые сторожем драйвера во время того же прогона, что и журнал.
///
/// <para><b>Зачем.</b> Журнал зонда не пишет ни <c>MODIF_ID</c>, ни надетых предметов — этого нет
/// ни в одном событии схемы v5. Без них боевые харнессы считают прокачанного героя новобранцем:
/// на партии <c>20260910-passive/passive-004.jsonl</c> это давало 183 расхождения С1, все на юнитах
/// с чужой цепочкой (<c>S073UN01f8</c> — ровно ×1,25 от расчёта, <c>G000UM4008</c> «Leader Upgrade:
/// Might» <c>QtyDamage +25 %</c>; <c>S073UN01fc</c> — ×1,10, <c>G000UM2301</c> «Rune Blade»
/// <c>+10 %</c>, а на 12-м уровне ×2,3 = 1,25·1,10·1,40·1,20 по всей его цепочке).</para>
///
/// <para><b>Какой снимок к какому бою — не догадка.</b> Связь журнала со снимками объявляет сам
/// драйвер: <c>manifest.json</c> рядом с журналом несёт строку прогона с полем <c>saves</c> —
/// список файлов в порядке съёмки (<c>tools/gamedrv/passive_run.py</c>). Имена файлов здесь не
/// разбираются: в каталоге партии лежат 88 <c>.sg</c> от двух десятков разных прогонов, и выбирать
/// их по маске имени — ровно та ошибка, из-за которой <c>C4MoveHarnessTests</c> отказывался брать
/// этот журнал. Номер хода берётся из самого файла (<c>ScenarioInfo.CUR_TURN</c>), а не из
/// имени.</para>
///
/// <para><b>Правило сопоставления (см. <see cref="TryChain"/>) и чем оно проверено.</b> Снимок
/// снимается раз в ход, бой стоит где-то внутри хода, и точного места снимка на оси <c>seq</c>
/// не знает никто. Поэтому берётся не «ближайший снимок», а ОКНО из трёх — ходы
/// <c>T−1</c>, <c>T</c>, <c>T+1</c>, — и цепочка принимается только тогда, когда все снимки окна,
/// где юнит есть, дают её одинаковой: тогда она не менялась нигде внутри окна, и где именно внутри
/// него стоял бой, уже неважно. Проверка правила — на величинах, которые журнал пишет САМ:
/// <c>battle_units.level</c> обязан совпасть с уровнем снимка, а <c>hp_max</c> — с тем, что даёт
/// цепочка (счётчики <c>chain_level</c> и <c>chain_hp</c> в <c>C1HarnessTests</c>: измерено
/// <b>813/3</b> и <b>861/0</b>; три «разошлось» у уровня законны — цепочка принимается и тогда,
/// когда она одна на всё окно, а уровень внутри окна успел смениться).</para>
///
/// <para>Нет манифеста, нет строки про этот журнал, нет поля <c>saves</c> — <see cref="None"/>,
/// и харнесс работает ровно как до задачи: пустая цепочка у всех. Молчаливой подстановки чужого
/// снимка здесь нет по построению.</para></summary>
public sealed partial class SaveSnapshots
{
    /// <summary>Снимков нет — цепочек не будет, отказов тоже: <see cref="TryChain"/> отдаёт пустую
    /// цепочку. Это поведение харнессов до задачи 16, и оно обязано сохраниться на 69 прежних
    /// журналах, рядом с которыми сохранений нет вовсе.</summary>
    public static readonly SaveSnapshots None = new([]);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<SaveSnapshots>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Один снимок: ход из <c>ScenarioInfo.CUR_TURN</c> и состав юнитов.
    /// <para>Снимков одного хода бывает несколько — пользователь жмёт быстрое сохранение когда
    /// захочет, — и это не мусор, а лишние точки: чем их больше, тем строже смыкается окно
    /// <see cref="TryChain"/>. Поэтому список, а не «по одному на ход».</para></summary>
    private sealed record Snapshot(int Turn, IReadOnlyDictionary<string, SavedUnit> Units,
                                   IReadOnlyDictionary<string, SavedStack> Stacks,
                                   IReadOnlyDictionary<string, SavedFort> Forts,
                                   IReadOnlyDictionary<string, SavedSide> Sides,
                                   IReadOnlyDictionary<string, (bool Human, string RaceId)> Players)
    {
        /// <summary>Номер загрузки карты в журнале (см. <see cref="At"/>); −1 — не опознана.</summary>
        public int Session { get; init; } = -1;

        /// <summary><c>seq</c> строки <c>save</c>, к которой привязан файл; −1 — не привязан.</summary>
        public long Seq { get; init; } = -1;

        /// <summary>Сложность партии — <c>ScenarioInfo.DIFFGAME</c>.</summary>
        public int Difficulty { get; init; }

        /// <summary>Файл снимка.</summary>
        public string SavePath { get; init; } = "";

        /// <summary>Отряды снимка (<c>MidStack</c>): владелец, клетка, члены — цели заклинаний карты.</summary>
        public IReadOnlyList<Squad> Squads { get; init; } = [];
    }

    /// <summary>Пустая ссылка в <c>.sg</c> — десять знаков нулевого <c>CMidgardID</c>.</summary>
    private const string EmptyId = "G000000000";

    private readonly IReadOnlyList<Snapshot> _snapshots;

    /// <summary>Первый <c>seq</c> каждой загрузки карты журнала; пусто — загрузки не размечены.</summary>
    private readonly IReadOnlyList<long> _sessionStarts;

    /// <summary>Строки журнала для запаса хода, каждая по порядку seq: <c>stack_move</c> (поток, отряд, остаток),
    /// <c>ap_apply</c> (отряд, остаток после списания) и <c>income</c> (игрок — начало его дня).</summary>
    private sealed record MoveLog(List<(long Seq, long Tid, string Stack, int MoveLeft)> Moves,
                                  List<(long Seq, string Stack, int MoveAfter)> Charges,
                                  List<(long Seq, string Player)> Days)
    {
        /// <summary><c>spell_cast</c>/<c>potion_use</c> с <c>ret = true</c> и <c>spell_expire</c> — см. <see cref="TrackedCasts"/>.</summary>
        public List<CastLine> Casts { get; init; } = [];
        public List<(long Seq, string Player)> Expires { get; init; } = [];
        /// <summary>Смены цепочки юнита в журнале (см. <see cref="BracketChain"/>): <c>skill_pick</c> — отряд и лидер,
        /// <c>item_equip</c>/<c>item_unequip</c> — отряд и пустой юнит.</summary>
        public List<(long Seq, string Stack, string Unit)> Edits { get; init; } = [];
    }

    private MoveLog _moves = new([], [], []);

    private SaveSnapshots(IReadOnlyList<Snapshot> snapshots, IReadOnlyList<long>? sessionStarts = null)
    {
        _snapshots = snapshots;
        _sessionStarts = sessionStarts ?? [];
    }

    /// <summary>Только снимки той загрузки карты, в которой лежит <paramref name="seq"/> журнала (и снимки с
    /// неопознанной загрузкой).
    /// <para>Зачем: игра между картами не перезапускается, и один журнал несёт подряд несколько карт на одной основе с
    /// общим префиксом id. В прогоне <c>20260916-synth</c> одиннадцать карт на Mercantile (<c>S135…</c>), из них две
    /// сессии <c>ai-q57</c> на трудной и очень трудной: окно ходов собирало снимки всех карт, <see cref="SideOf"/>
    /// видел разные стороны и отдавал null, и точность ИИ считалась без сдвига (С1: журнал 85, наше 80).</para>
    /// <para>Загрузка — смена логического потока <c>tid</c> (отличного от потока интерфейса из первой строки); файл
    /// снимка привязан к строке <c>save</c> (см. <see cref="BindToSaves"/>). Разметки нет (журнал без строк
    /// <c>save</c>, снимок не привязан) — ответ прежний, по всем снимкам.</para></summary>
    public SaveSnapshots At(long seq)
    {
        if (_sessionStarts.Count == 0) return this;
        int session = -1;
        for (int i = 0; i < _sessionStarts.Count && _sessionStarts[i] <= seq; i++) session = i;
        var own = _snapshots.Where(x => x.Session < 0 || x.Session == session).ToList();
        // Загрузка без своих сохранений (попытка, которую игрок бросил): цепочки взять неоткуда, а пустая была бы
        // выдумкой — ai-q14 до сохранения 107, лидер с «Критическим ударом» считался без навыка.
        var moves = _moves with { Casts = _moves.Casts.Where(c => c.Session == session).ToList() };
        return own.Count > 0 ? new SaveSnapshots(own) { _moves = moves } : new SaveSnapshots([]) { Orphan = true };
    }

    /// <summary>Вид <see cref="At"/> на загрузку карты, у которой в журнале нет ни одного своего снимка.</summary>
    public bool Orphan { get; private init; }

    /// <summary>Сколько снимков подхвачено. Ноль — журнал без сохранений.</summary>
    public int Count => _snapshots.Count;

    /// <summary>Снимки, привязанные к строке <c>save</c> журнала (<see cref="BindToSaves"/>), по порядку <c>seq</c>: файл, ход файла
    /// (<c>CUR_TURN</c>) и <c>seq</c> строки. Непривязанные не входят.</summary>
    public IReadOnlyList<(string Path, int Turn, long Seq)> BoundSaves() =>
        [.. _snapshots.Where(x => x.Seq >= 0).OrderBy(x => x.Seq).Select(x => (x.SavePath, x.Turn, x.Seq))];

    /// <summary>Снимки, объявленные манифестом прогона рядом с журналом. Разбор кешируется по пути:
    /// один и тот же журнал берут и С1, и С2, а 50 файлов <c>.sg</c> по 400 КБ разбирать дважды
    /// незачем. Замок — на путь, а не на весь кэш: прежний общий <c>lock</c> на время разбора выстраивал в очередь все харнессы
    /// процесса (16.10: первые ~45 с прогона D2.Rules.Tests ждали, пока один поток разберёт сохранения всего корпуса).</summary>
    public static SaveSnapshots Of(string journalPath)
    {
        var lazy = Cache.GetOrAdd(journalPath, p => new Lazy<SaveSnapshots>(() => Load(p)));
        try { return lazy.Value; }
        catch
        {
            Cache.TryRemove(new KeyValuePair<string, Lazy<SaveSnapshots>>(journalPath, lazy));   // как прежде: сбой разбора не запоминается
            throw;
        }
    }

    /// <summary>Есть ли у журнала снимки — то же, что <c>Of(journalPath).Count &gt; 0</c>, но без разбора сохранений: манифест объявляет
    /// хотя бы одно, и его файл на месте (приём журнала в корпус харнессов спрашивает только это).</summary>
    public static bool HasSaves(string journalPath)
    {
        var dir = Path.GetDirectoryName(journalPath) ?? ".";
        return DeclaredSaves(Path.Combine(dir, "manifest.json"), Path.GetFileName(journalPath), out _)
            .Any(name => File.Exists(Path.Combine(dir, name)));
    }

    /// <summary>Цепочка модификаторов юнита на момент боя хода <paramref name="turn"/>.
    ///
    /// <para>Порядок разбора:
    /// <list type="number">
    /// <item>снимков нет вовсе — пустая цепочка, <c>true</c> (прежнее поведение харнессов);</item>
    /// <item>в окне <c>turn−1…turn+1</c> юнита нет ни в одном снимке — пустая цепочка и
    /// <c>true</c>: юнит нанят или призван и убит внутри окна, своей цепочки у него взяться
    /// неоткуда, и это ровно то допущение, на котором держится весь прежний корпус. Чтобы это не
    /// пропало из виду, <see cref="LevelIn"/> в таком случае отвечает null, и харнесс печатает
    /// отдельную строку пропуска;</item>
    /// <item>все снимки окна, где юнит есть, дают одну цепочку — она и берётся;</item>
    /// <item>цепочки разные, но различаются они ТОЛЬКО способностями лидера
    /// (<paramref name="levelGranted"/> — идентификаторы <c>GleaUpg.MODIF_ID</c>) — берутся снимки,
    /// чей <c>LEVEL</c> равен уровню, который журнал пишет сам: способность выдаётся при повышении
    /// уровня, а журнал говорит, какой уровень был в бою. Сошлись — берём;</item>
    /// <item>иначе — <c>false</c> с причиной. Оговорка про «только способности лидера» не
    /// формальность: на ходу 13 партии <c>S073UN01fc</c> одновременно получил и уровень, и «Rune
    /// Blade» (<c>G000UM2301</c>, <c>+10 %</c> урона). Предмет надевают когда угодно, уровнем он не
    /// разводится, и без этой оговорки харнесс брал цепочку без клинка и давал урон 55 против
    /// журнальных 60.</item>
    /// </list></para>
    /// <para><b>Заклинания и зелья карты</b> (<paramref name="spells"/> и <paramref name="battleSeq"/> заданы): модификатор,
    /// наложенный заклинанием или выпитым на карте зельем из журнала (<see cref="TrackedCasts"/>), из цепочек окна сперва вычитается — он временный
    /// и окно по нему расходилось бы, — а поверх итоговой цепочки кладутся те, что действуют на момент боя.</para></summary>
    public bool TryChain(int turn, string unitId, long? journalLevel, IReadOnlySet<string>? levelGranted,
                         out IReadOnlyList<string> ids, out string reason,
                         long battleSeq = -1, MapEffectTables? spells = null)
    {
        ids = [];
        reason = "";
        if (_snapshots.Count == 0) return true;

        var casts = spells is null || battleSeq < 0 ? [] : TrackedCasts(spells).Where(c => c.Units.Contains(unitId)).ToList();
        var active = casts.Where(c => c.Live(battleSeq)).Select(c => c.ModifierId).ToList();
        var seen = new List<SavedUnit>(3);
        foreach (var (seq, unit) in InWindowAt(turn, unitId))
        {
            if (WithoutSpells(unit, seq, casts, out reason) is not { } own) return false;
            seen.Add(own);
        }
        if (!OwnChain(seen, journalLevel, levelGranted, out ids) && !BracketChain(unitId, battleSeq, casts, levelGranted, out ids))
        {
            reason = $"цепочка модификаторов юнита {unitId} менялась внутри окна ходов " +
                     $"{turn - 1}…{turn + 1} ({string.Join(" / ", seen.Select(u => u.Modifiers.Count))} шт.), " +
                     "и уровнем журнала это не разводится";
            return false;
        }
        ids = [.. ids, .. active];
        return true;
    }

    /// <summary>Цепочка по двум снимкам, обнимающим бой по оси <c>seq</c> (последний до боя и первый после — оба привязаны к
    /// строкам <c>save</c> этой загрузки), когда окно ходов не сошлось. Совпали — она и есть. Разошлись — смена ищется в журнале:
    /// выбор навыка лидера (<c>skill_pick</c> с <c>apply</c>, сам юнит) и надевание или снятие предмета (<c>item_equip</c>/
    /// <c>item_unequip</c> с <c>apply</c>, отряд юнита — знамя ложится на весь отряд). Все смены до боя — цепочка снимка после,
    /// все после — снимка до; юнита в снимке после нет (пал в бою) — снимок до, если смен до боя не было; и наоборот (нанят после
    /// снимка до). Смен нет или они по обе стороны боя — неизвестно, false.
    /// <para>Зачем: окно <c>T−1…T+1</c> отказывало, если цепочка сменилась где угодно в нём, хотя бы за ход до боя; С2 16.10 —
    /// 87 боёв (<c>20260918-probe65 [453832]</c>: навык <c>G000UM4002</c> выбран в seq 402072 после снимка хода 7 и до снимка
    /// хода 8, бой — seq 453832, то есть после выбора).</para></summary>
    private bool BracketChain(string unitId, long battleSeq, IReadOnlyList<TrackedCast> casts, IReadOnlySet<string>? levelGranted,
                              out IReadOnlyList<string> ids)
    {
        ids = [];
        if (battleSeq < 0) return false;
        var bound = _snapshots.Where(x => x.Seq >= 0).ToList();
        var before = bound.Where(x => x.Seq < battleSeq).MaxBy(x => x.Seq);
        var after = bound.Where(x => x.Seq > battleSeq).MinBy(x => x.Seq);
        SavedUnit? Own(Snapshot? x) => x is not null && x.Units.TryGetValue(unitId, out var u) ? WithoutSpells(u, x.Seq, casts, out _) : null;
        var (b, a) = (Own(before), Own(after));
        if (b is null && a is null) return false;
        // Те же модификаторы (в том числе в другом порядке — предмет сняли и надели снова: 20260918-probe65 [2944220], S143UN0247
        // [2301, 4003] → [4003, 2301]) — набор в бою известен; берётся порядок снимка до боя.
        if (b is not null && a is not null
            && b.Modifiers.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(a.Modifiers))
        {
            ids = b.Modifiers;
            return true;
        }
        var stacks = new[] { before, after }.OfType<Snapshot>()
            .SelectMany(x => x.Squads.Where(q => q.Units.Contains(unitId, StringComparer.OrdinalIgnoreCase)).Select(q => q.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        long from = before?.Seq ?? battleSeq, to = after?.Seq ?? battleSeq;
        // Какие смены могли дать разницу: только навыки лидера (GleaUpg) — только skill_pick юнита; только не навыки — только
        // предметы отряда (надетое после боя знамя не мешает навыку, выбранному до боя: 20260918-probe65 [7449324]).
        var diff = b is null || a is null ? null
            : b.Modifiers.Except(a.Modifiers, StringComparer.OrdinalIgnoreCase)
               .Concat(a.Modifiers.Except(b.Modifiers, StringComparer.OrdinalIgnoreCase)).ToList();
        bool skills = diff is null || levelGranted is null || diff.Any(levelGranted.Contains);
        bool items = diff is null || levelGranted is null || diff.Any(m => !levelGranted.Contains(m));
        var edits = _moves.Edits.Where(e => e.Seq > from && e.Seq < to
                                            && (skills && SameId(e.Unit, unitId) || items && e.Unit.Length == 0 && stacks.Contains(e.Stack)))
                                .ToList();
        bool early = edits.Any(e => e.Seq < battleSeq), late = edits.Any(e => e.Seq > battleSeq);
        if (b is not null && !early && (a is null || late)) { ids = b.Modifiers; return true; }
        if (a is not null && !late && (b is null || early)) { ids = a.Modifiers; return true; }
        return false;
    }

    /// <summary>Своя цепочка юнита по снимкам окна (без заклинаний карты) — правило <see cref="TryChain"/>.</summary>
    private static bool OwnChain(IReadOnlyList<SavedUnit> seen, long? journalLevel, IReadOnlySet<string>? levelGranted,
                                 out IReadOnlyList<string> ids)
    {
        ids = [];
        if (seen.Count == 0) return true;                 // юнита в окне нет — цепочка пуста, см. выше
        if (Same(seen, out ids)) return true;

        if (levelGranted is not null && journalLevel is not null && DifferOnlyBy(seen, levelGranted))
        {
            var atLevel = seen.Where(u => u.Level == journalLevel).ToList();
            if (atLevel.Count > 0 && Same(atLevel, out ids)) return true;
        }
        ids = [];
        return false;
    }

    /// <summary>Различаются ли цепочки окна только теми модификаторами, что выдаются при повышении
    /// уровня. Если да — уровень из журнала их разводит; если нет (надет предмет, выпито стойкое
    /// зелье) — момент внутри хода неизвестен, и гадать нельзя.</summary>
    private static bool DifferOnlyBy(IReadOnlyList<SavedUnit> seen, IReadOnlySet<string> levelGranted)
    {
        var common = seen.Select(u => (IEnumerable<string>)u.Modifiers)
                         .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase))
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var u in seen)
            foreach (var id in u.Modifiers)
                if (!common.Contains(id) && !levelGranted.Contains(id)) return false;
        return true;
    }

    /// <summary>Уровень юнита в снимках окна — для проверки самого правила сопоставления: журнал
    /// пишет уровень сам, и на принятой цепочке он обязан совпасть. null — юнита в окне нет.</summary>
    public int? LevelIn(int turn, string unitId)
    {
        var seen = InWindow(turn, unitId);
        return seen.Count == 0 ? null : seen[0].Level;
    }

    /// <summary>
    /// Укрепление, внутри которого юнит стоял на ходу <paramref name="turn"/>, — по тому же окну
    /// <c>turn−1…turn+1</c> и с тем же требованием согласия снимков, что у <see cref="TryChain"/>.
    /// <para><c>null</c> — сказать нечего: юнита в окне нет вовсе, он хоть в одном снимке окна стоит
    /// вне укрепления, или снимки называют разные укрепления. Сохранение снимается раз в ход, а
    /// отряд входит в город и выходит внутри хода, поэтому «вне укрепления в снимке» — это не
    /// «вне укрепления в бою», и выдавать одно за другое нельзя (ранг 3 на момент внутри хода).</para>
    /// </summary>
    public SavedFort? FortOf(int turn, string unitId)
    {
        SavedFort? found = null;
        foreach (var s in _snapshots)
        {
            if (s.Turn < turn - 1 || s.Turn > turn + 1) continue;
            if (!s.Forts.TryGetValue(unitId, out var fort))
            {
                if (s.Units.ContainsKey(unitId)) return null;   // в этом снимке юнит вне укрепления
                continue;                                       // юнита в снимке нет — снимок молчит
            }
            if (found is not null && found != fort) return null;
            found = fort;
        }
        return found;
    }

    /// <summary>Сторона юнита на ходу <paramref name="turn"/> — по тому же окну <c>turn−1…turn+1</c> и с тем же
    /// требованием согласия, что у <see cref="FortOf"/>. <c>null</c> — юнита с опознанным владельцем в окне нет или
    /// снимки расходятся (в манифесте бывают снимки разных карт с общим префиксом id — см. <see cref="InWindow"/>).
    /// Сложность — поле сохранения, а не догадка по журналу.</summary>
    public SavedSide? SideOf(int turn, string unitId)
    {
        SavedSide? found = null;
        foreach (var s in _snapshots)
        {
            if (s.Turn < turn - 1 || s.Turn > turn + 1 || !s.Sides.TryGetValue(unitId, out var side)) continue;
            if (found is not null && found != side) return null;
            found = side;
        }
        return found;
    }

    /// <summary>Все снимки ходов <c>turn−1…turn+1</c>, где этот юнит есть, — все, а не «последний
    /// на ход».
    /// <para>Это не педантизм. Манифест пассивного прогона может перечислять сохранения
    /// <b>нескольких карт подряд</b>: <c>20260909-passive/passive-001.jsonl</c> несёт двенадцать
    /// снимков трёх разных карт (m4-temple, m6-rods, m8-mage), у всех ходы 1…4, и сценарный префикс
    /// у них общий — <c>S143</c>, — то есть один и тот же <c>S143UN0005</c> в них разный юнит.
    /// «По одному снимку на ход» молча взяло бы последний и подставило цепочку чужой карты; список
    /// же сводит их в одно окно, цепочки не сходятся, и бой честно уходит в пропуск с причиной.
    /// Цена — два боя того прогона; выдуманная цепочка обошлась бы дороже.</para></summary>
    /// <summary>Отряд на ход <paramref name="turn"/>: снимок того же хода, иначе ближайший в окне
    /// <c>turn−1…turn+1</c> (из равноудалённых — более ранний). null — отряда в окне нет.</summary>
    public SavedStack? StackOf(int turn, string stackId) =>
        _snapshots.Where(s => Math.Abs(s.Turn - turn) <= 1 && s.Stacks.ContainsKey(stackId))
                  .OrderBy(s => Math.Abs(s.Turn - turn)).ThenBy(s => s.Turn)
                  .Select(s => s.Stacks[stackId]).FirstOrDefault();

    /// <summary>Сторона игрока <paramref name="playerId"/> на ходу <paramref name="turn"/> — по его записи (<c>MidPlayer</c>) в
    /// снимках окна <c>turn−1…turn+1</c> и сложности этих снимков; null — игрока в окне нет или снимки расходятся.
    /// Для отряда, которого в снимках ещё нет (см. <see cref="FreshStackOf"/>). Снимки, привязанные к строкам <c>save</c>
    /// своей загрузки (<see cref="At"/>), главнее непривязанных: id игроков у карт на одной основе общие, и в 20261014-replay
    /// к каждой загрузке ai-q57 подмешаны непривязанные снимки ходов 3–4 других карт S135 со сложностью 1 и 2.</summary>
    public SavedSide? PlayerSide(int turn, string playerId)
    {
        var window = _snapshots.Where(s => Math.Abs(s.Turn - turn) <= 1 && s.Players.ContainsKey(playerId)).ToList();
        var own = window.Where(s => s.Session >= 0).ToList();
        var sides = (own.Count > 0 ? own : window).Select(s => new SavedSide(s.Difficulty, s.Players[playerId].Human))
                                                   .Distinct().ToList();
        return sides.Count == 1 ? sides[0] : null;
    }

    /// <summary>Отряд, которого нет ни в одном снимке окна, но чей владелец <paramref name="playerId"/> в окне есть, —
    /// таким, каким его делает движок: отряд нанят после снимка. <c>CMidStack</c> при создании (<c>0x5ebfc7</c>) и при
    /// смене владельца (<c>0x5ecea4</c>) ставит приказ Stand нейтралу (<c>0x5e5358</c>) и Normal остальным, цель
    /// приказа пустую, приказ ИИ Normal (<c>0x5ec020</c>) — ранг 1. null — отряд в окне есть или владелец не опознан
    /// (нет в окне, снимки расходятся).
    /// <para>ponytail: приказ ИИ планировщик может сменить в тот же ход — у нападающего брать его отсюда нельзя.</para></summary>
    public SavedStack? FreshStackOf(int turn, string stackId, string playerId)
    {
        if (StackOf(turn, stackId) is not null) return null;
        var owners = _snapshots.Where(s => Math.Abs(s.Turn - turn) <= 1 && s.Players.ContainsKey(playerId))
                               .Select(s => s.Players[playerId]).Distinct().ToList();
        if (owners.Count != 1) return null;
        var (human, race) = owners[0];
        const int normal = 1, stand = 2;
        bool neutral = race.EndsWith("RR0004", StringComparison.OrdinalIgnoreCase);
        return new SavedStack(neutral ? stand : normal, "", normal, 0, human, race);
    }

    /// <summary>Остаток хода нападающего в бою — байт <c>CMidStack::movement</c> (<c>+0x98</c>), который читает
    /// <c>0x5d2a12</c>. Атака по карте списывает <c>ceil(MOVE/2)</c> полного запаса дня ДО боя (<c>CEffectChangeAP</c>
    /// <c>0x43588C</c> перед <c>CEffectBattle</c> <c>0x4338BA</c>, план Ф7 §3 п. 51), а последний <c>stack_move</c>
    /// показывает остаток ДО списания.
    /// <list type="number">
    /// <item><c>ap_apply</c> отряда после его последнего шага — <c>move_after</c> (ранг 1; схема 6: у 283 боёв из 291
    /// <c>delta</c> = <c>ceil(запас дня / 2)</c>, <c>move_before</c> = остаток последнего шага у всех);</item>
    /// <item>иначе <see cref="D2.Rules.Map.Movement.AfterAttack"/> от остатка последнего шага и запаса дня — остатка
    /// первого шага отряда после строки <c>income</c> его игрока (зонд пишет его до списания шага,
    /// <c>leader_move_max</c> 99/0).</item>
    /// </list>
    /// Шаги — только с потока боя <paramref name="tid"/>: планировщик ИИ пишет <c>stack_move</c> своим потоком
    /// (<c>20260915-passive</c>: <c>tid 12972</c> против 12804 у боя). null — шагов не было.
    /// <para>ponytail: отряд, не ходивший в день боя (последний шаг — вчерашний), получает остаток того шага без
    /// списания, как до правки; запаса дня для него журнал не даёт.</para></summary>
    public int? AttackerMovement(string stackId, string player, long tid, long battleSeq)
    {
        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        long dayStart = long.MaxValue;
        foreach (var d in _moves.Days)
        {
            if (d.Seq >= battleSeq) break;
            if (Is(d.Player, player)) dayStart = d.Seq;
        }
        if (dayStart == long.MaxValue) dayStart = -1;      // доходов игрока до боя нет — день с начала журнала
        long lastSeq = -1;
        int left = 0;
        int? full = null;
        foreach (var m in _moves.Moves)
        {
            if (m.Seq >= battleSeq) break;
            if (m.Tid != tid || !Is(m.Stack, stackId)) continue;
            (lastSeq, left) = (m.Seq, m.MoveLeft);
            if (m.Seq > dayStart) full ??= m.MoveLeft;
        }
        if (lastSeq < 0) return null;
        int? charged = null;
        foreach (var c in _moves.Charges)
        {
            if (c.Seq >= battleSeq) break;
            if (c.Seq > lastSeq && Is(c.Stack, stackId)) charged = c.MoveAfter;
        }
        if (charged is not null) return charged;
        return full is { } day ? D2.Rules.Map.Movement.AfterAttack(day, left) : left;
    }

    private List<SavedUnit> InWindow(int turn, string unitId) => InWindowAt(turn, unitId).Select(x => x.Unit).ToList();

    /// <summary>То же, что <see cref="InWindow"/>, с <c>seq</c> строки <c>save</c> каждого снимка (−1 — не привязан).</summary>
    private List<(long Seq, SavedUnit Unit)> InWindowAt(int turn, string unitId)
    {
        var seen = new List<(long, SavedUnit)>(3);
        foreach (var s in _snapshots)
            if (s.Turn >= turn - 1 && s.Turn <= turn + 1 && s.Units.TryGetValue(unitId, out var u))
                seen.Add((s.Seq, u));
        return seen;
    }

    private static bool Same(IReadOnlyList<SavedUnit> seen, out IReadOnlyList<string> ids)
    {
        ids = seen[0].Modifiers;
        for (int i = 1; i < seen.Count; i++)
            if (!ids.SequenceEqual(seen[i].Modifiers, StringComparer.OrdinalIgnoreCase))
            { ids = []; return false; }
        return true;
    }

    private static SaveSnapshots Load(string journalPath)
    {
        var dir = Path.GetDirectoryName(journalPath) ?? ".";
        var names = DeclaredSaves(Path.Combine(dir, "manifest.json"), Path.GetFileName(journalPath),
                                  out var startedAt);
        if (names.Count == 0) return None;

        // Манифест старше каталога — снимка просто нет. Сохранения разбираются параллельно (порядок — манифеста): у партии их
        // до 130, и по одному разбор шёл секунды, пока харнессы ждали список корпуса.
        var paths = names.Select(name => Path.Combine(dir, name)).Where(File.Exists).ToList();
        var snaps = new Snapshot[paths.Count];
        try { Parallel.For(0, paths.Count, i => snaps[i] = ReadSave(paths[i])); }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
        var files = paths.Zip(snaps, (path, snap) => (Path: path, Snap: snap)).ToList();
        if (files.Count == 0) return None;
        if (!File.Exists(journalPath)) return new SaveSnapshots(files.Select(f => f.Snap).ToList());
        if (startedAt is null)
            return new SaveSnapshots(files.Select(f => f.Snap).ToList()) { _moves = Moves(journalPath) };

        var (starts, saves) = Sessions(journalPath);
        var bound = BindToSaves(files, saves, startedAt.Value);
        return new SaveSnapshots(bound, bound.Any(x => x.Session >= 0) ? starts : null) { _moves = Moves(journalPath, starts) };
    }

    /// <summary>Строка <c>save</c> журнала: где она на осях и в какой загрузке карты.</summary>
    private sealed record SaveLine(long Seq, long T, long Turn, int Session);

    /// <summary>Загрузки карты журнала (первый <c>seq</c> каждой) и строки <c>save</c> (<c>ok</c>, не временные).
    /// Загрузка — смена логического потока: первая строка журнала (<c>start</c>) пишется потоком интерфейса, всё
    /// остальное с другим <c>tid</c> — поток партии, и он новый у каждой загрузки сценария (прогон
    /// <c>20260916-synth</c>: 45 загрузок, 279 из 279 снимков привязаны — <c>docs/synthetic-maps/journal_maps.py</c>
    /// в disciples-modding). Целиком разбирается только строка <c>save</c>; у остальных читаются <c>seq</c> и
    /// <c>tid</c> — журнал партии весит десятки мегабайт.</summary>
    private static (List<long> Starts, List<SaveLine> Saves) Sessions(string journalPath)
    {
        var starts = new List<long>();
        var saves = new List<SaveLine>();
        var loads = new JournalLoads();
        foreach (var line in File.ReadLines(journalPath))
        {
            if (Head(line) is not var (seq, tid)) continue;
            if (loads.Opens(tid, line)) starts.Add(seq);
            if (starts.Count == 0 || !line.Contains("\"ev\":\"save\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (!e.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) continue;
            if (e.TryGetProperty("temp", out var temp) && temp.GetBoolean()) continue;
            saves.Add(new SaveLine(seq, e.GetProperty("t").GetInt64(), e.GetProperty("turn").GetInt64(),
                                   starts.Count - 1));
        }
        return (starts, saves);
    }

    /// <summary>Строки <c>stack_move</c>, <c>ap_apply</c> и <c>income</c> журнала (см. <see cref="MoveLog"/>).</summary>
    private static MoveLog Moves(string journalPath, IReadOnlyList<long>? sessionStarts = null)
    {
        var log = new MoveLog([], [], []);
        foreach (var line in File.ReadLines(journalPath))
        {
            if (sessionStarts is not null && SpellLine(line, sessionStarts, log)) continue;
            bool move = line.Contains("\"ev\":\"stack_move\"", StringComparison.Ordinal);
            bool charge = !move && line.Contains("\"ev\":\"ap_apply\"", StringComparison.Ordinal);
            if (!move && !charge && !line.Contains("\"ev\":\"income\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            long seq = e.GetProperty("seq").GetInt64();
            if (move)
                log.Moves.Add((seq, e.GetProperty("tid").GetInt64(), e.GetProperty("stack").GetString() ?? "",
                               e.GetProperty("move_left").GetInt32()));
            else if (charge)
                log.Charges.Add((seq, e.GetProperty("stack").GetString() ?? "", e.GetProperty("move_after").GetInt32()));
            else
                log.Days.Add((seq, e.GetProperty("player").GetString() ?? ""));
        }
        log.Moves.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        log.Charges.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        log.Days.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        log.Casts.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        log.Expires.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        ReadEdits(journalPath, log);
        log.Edits.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return log;
    }

    /// <summary>Смены цепочки (<see cref="MoveLog.Edits"/>) — разобранные строки общего кэша журналов (<see cref="JournalCache"/>):
    /// действие, а не вопрос (<c>apply</c> и <c>ret</c> истинны); выбор навыка без лидера и строка без отряда — не смена.</summary>
    private static void ReadEdits(string journalPath, MoveLog log)
    {
        foreach (var e in JournalCache.Read(journalPath, EditKinds))
        {
            switch (e)
            {
                case SkillPickEvent { Apply: true, Ret: true } p when p.Stack.Length > 0 && p.Leader.Length > 0:
                    log.Edits.Add((p.Seq, p.Stack, p.Leader));
                    break;
                case ItemEquipEvent { Apply: true, Ret: true } q when q.Stack.Length > 0:
                    log.Edits.Add((q.Seq, q.Stack, ""));
                    break;
            }
        }
    }

    private static readonly string[] EditKinds = ["skill_pick", "item_equip", "item_unequip"];

    /// <summary><c>seq</c> и <c>tid</c> из начала строки зонда <c>{"seq":N,"tid":M,…</c>; иной вид — null.</summary>
    private static (long Seq, long Tid)? Head(string line)
    {
        const string SeqKey = "{\"seq\":", TidKey = ",\"tid\":";
        if (!line.StartsWith(SeqKey, StringComparison.Ordinal)) return null;
        int tidAt = line.IndexOf(TidKey, SeqKey.Length, StringComparison.Ordinal);
        if (tidAt < 0) return null;
        int tidEnd = line.IndexOf(',', tidAt + TidKey.Length);
        if (tidEnd < 0) return null;
        return long.TryParse(line.AsSpan(SeqKey.Length, tidAt - SeqKey.Length), out long seq)
               && long.TryParse(line.AsSpan(tidAt + TidKey.Length, tidEnd - tidAt - TidKey.Length), out long tid)
            ? (seq, tid) : null;
    }

    /// <summary>Каждому файлу по возрастанию <c>mtime</c> — последняя ещё не занятая строка <c>save</c> с
    /// <c>t ≤ mtime − started_at + 0,5 с</c> (файл пишется после строки, на 0,3–2,7 с). Ход строки обязан совпасть с
    /// <c>CUR_TURN</c> файла, иначе у файла загрузка не опознана — и тогда он виден из любой.</summary>
    private static List<Snapshot> BindToSaves(List<(string Path, Snapshot Snap)> files, List<SaveLine> saves,
                                              DateTimeOffset startedAt)
    {
        const double SlackMs = 500;
        var used = new HashSet<long>();
        var bound = new List<Snapshot>(files.Count);
        foreach (var (path, snap) in files.OrderBy(f => File.GetLastWriteTimeUtc(f.Path)))
        {
            double mt = (new DateTimeOffset(File.GetLastWriteTimeUtc(path)) - startedAt).TotalMilliseconds;
            var hit = saves.Where(x => x.T <= mt + SlackMs && !used.Contains(x.Seq)).MaxBy(x => x.Seq);
            if (hit is null || hit.Turn != snap.Turn) { bound.Add(snap); continue; }
            used.Add(hit.Seq);
            bound.Add(snap with { Session = hit.Session, Seq = hit.Seq });
        }
        return KeepOrder(bound);
    }

    /// <summary>Привязка сохраняет порядок: внутри загрузки и дня k-й по <c>mtime</c> снимок — k-я по <c>seq</c> из выбранных строк.
    /// «Последняя незанятая» его ломает, когда две быстрые записи легли раньше обеих копий (<c>20261016e-av</c>, карта 7: <c>t01-050</c>
    /// брал строку позже, чем <c>t01-051</c>).</summary>
    private static List<Snapshot> KeepOrder(List<Snapshot> bound)
    {
        var result = new List<Snapshot>(bound);
        foreach (var g in bound.Select((s, i) => (s, i)).Where(x => x.s.Seq >= 0).GroupBy(x => (x.s.Session, x.s.Turn)))
        {
            var seqs = g.Select(x => x.s.Seq).Order().ToList();
            int k = 0;
            foreach (var (s, i) in g) result[i] = s with { Seq = seqs[k++] };
        }
        return result;
    }

    /// <summary>Список сохранений этого журнала из манифеста прогона. Манифест — массив строк вида
    /// <c>{"file": "passive-004.jsonl", …, "saves": [...]}</c>; строка про другой журнал не берётся.
    /// Битый или отсутствующий манифест — пустой список, а не исключение: рядом с 69 прежними
    /// журналами манифеста с сохранениями нет и не будет.</summary>
    private static IReadOnlyList<string> DeclaredSaves(string manifest, string journalName,
                                                       out DateTimeOffset? startedAt)
    {
        startedAt = null;
        if (!File.Exists(manifest)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("file", out var file)) continue;
                if (!string.Equals(file.GetString(), journalName, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.TryGetProperty("started_at", out var at) && at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.None, out var parsed))
                    startedAt = parsed;
                if (!row.TryGetProperty("saves", out var saves) || saves.ValueKind != JsonValueKind.Array) return [];
                return saves.EnumerateArray().Select(NameOf).Where(s => s.Length > 0).ToList();
            }
        }
        catch (JsonException) { return []; }
        return [];
    }

    /// <summary>Имя файла снимка. У манифеста две формы, обе живые: пассивный прогон
    /// (<c>tools/gamedrv/passive_run.py</c>) пишет в <c>saves</c> просто имена, прогон Ф6
    /// (<c>tools/corpus_f6_run.py</c>) — записи <c>{turn, file, sha256, bytes}</c>. Ход из записи
    /// не берётся: <c>CUR_TURN</c> читается из самого файла, и это одна дорога вместо двух.</summary>
    private static string NameOf(JsonElement save) => save.ValueKind switch
    {
        JsonValueKind.String => save.GetString() ?? "",
        JsonValueKind.Object when save.TryGetProperty("file", out var f) => f.GetString() ?? "",
        _ => "",
    };

    private static Snapshot ReadSave(string path)
    {
        var data = SgFile.Open(path).Raw.Objects.Select(o => o.Data).ToList();
        var info = data.OfType<KSg.ScenarioInfo>().Single();
        int turn = (int)info.CurTurn.Value;
        var units = new Dictionary<string, SavedUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in data.OfType<KSg.MidUnit>())
            units[u.UnitId.String] = new SavedUnit(u.Type.String, (int)u.Level.Value,
                                                   u.Modifiers.Select(m => m.String).ToList());
        var players = data.OfType<KSg.MidPlayer>().ToDictionary(p => p.PlayerId.String,
            p => (p.IsHuman.Value != 0, p.RaceId.String), StringComparer.OrdinalIgnoreCase);
        var squads = data.OfType<KSg.MidStack>()
            .Select(s => new Squad(Ref(s.Owner.String), (int)s.Position.X.Value, (int)s.Position.Y.Value, Members(s.Group))
                         { Id = s.StackId.String })
            .ToList();
        return new Snapshot(turn, units, StacksOf(data), FortsOf(data), SidesOf(data, (int)info.Diffgame.Value), players)
        { Squads = squads, Difficulty = (int)info.Diffgame.Value, SavePath = path };
    }

    /// <summary>Юнит и группа -> сторона: члены отряда и сам отряд — по <c>MidStack.OWNER</c>, гарнизон и укрепление —
    /// по <c>City.OWNER</c>, стража руин — сторона ИИ без хозяина. Ключ группы — её id (<c>battle_start.attacker</c>/<c>defender</c>).
    /// Владелец без записи игрока (пустой id) — не попадает.</summary>
    private static Dictionary<string, SavedSide> SidesOf(IReadOnlyList<Kaitai.KaitaiStruct> data, int difficulty)
    {
        var human = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in data.OfType<KSg.MidPlayer>()) human[p.PlayerId.String] = p.IsHuman.Value != 0;
        var sides = new Dictionary<string, SavedSide>(StringComparer.OrdinalIgnoreCase);
        void Add(string groupId, KSg.Group group, string owner)
        {
            if (!human.TryGetValue(Ref(owner), out bool h)) return;
            var side = new SavedSide(difficulty, h);
            sides[groupId] = side;
            foreach (var id in Members(group)) sides[id] = side;
        }
        foreach (var s in data.OfType<KSg.MidStack>()) Add(s.StackId.String, s.Group, s.Owner.String);
        foreach (var c in data.OfType<KSg.Capital>()) Add(c.City.CityId.String, c.City.Group, c.City.Owner.String);
        foreach (var v in data.OfType<KSg.MidVillage>()) Add(v.City.CityId.String, v.City.Group, v.City.Owner.String);
        // Стража руин хозяина не имеет: проверка «хозяин группы — человек» (isGroupOwnerPlayerHuman 0x626ca5, из getAttackPower
        // 0x6278fa) отвечает «нет», и сдвиг точности по сложности у неё есть. Корпус 20261016e-av, карта 8 (трудная): страж руины
        // S143RU0000 S143UN001b — power 95 при POWER 90, бой [16005528].
        foreach (var r in data.OfType<KSg.MidRuin>())
        {
            var side = new SavedSide(difficulty, false);
            sides[r.RuinId.String] = side;
            foreach (var id in Members(r.Group)) sides[id] = side;
        }
        return sides;
    }

    /// <summary>Юнит -> укрепление, в котором он стоит: гарнизон (<c>City.GROUP</c>) и отряд-визитёр
    /// (<c>City.STACK</c>). Читается из того же разбора файла, что и юниты, — второго прохода по
    /// <c>.sg</c> здесь не заводится (задача 5 Ф7).</summary>
    private static Dictionary<string, SavedStack> StacksOf(IReadOnlyList<Kaitai.KaitaiStruct> data)
    {
        var players = new Dictionary<string, KSg.MidPlayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in data.OfType<KSg.MidPlayer>()) players[p.PlayerId.String] = p;
        var itemTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in data.OfType<KSg.MidItem>()) itemTypes[i.ItemId.String] = i.ItemType.String;
        string TypeOf(string itemId) => itemTypes.GetValueOrDefault(Ref(itemId), "");
        var stacks = new Dictionary<string, SavedStack>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in data.OfType<KSg.MidStack>())
        {
            var owner = players.GetValueOrDefault(Ref(s.Owner.String));
            stacks[s.StackId.String] = new SavedStack((int)s.Order.Value, Ref(s.OrderTarg.String), (int)s.Aiorder.Value,
                (int)s.Move.Value, owner is not null && owner.IsHuman.Value != 0, owner?.RaceId.String ?? "")
            {
                LeaderId = Ref(s.LeaderId.String),
                BattleItems = [TypeOf(s.Battle1.String), TypeOf(s.Battle2.String)],
            };
        }
        return stacks;
    }

    private static Dictionary<string, SavedFort> FortsOf(IReadOnlyList<Kaitai.KaitaiStruct> data)
    {
        var races = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in data.OfType<KSg.MidPlayer>()) races[p.PlayerId.String] = p.RaceId.String;
        var squads = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in data.OfType<KSg.MidStack>()) squads[s.StackId.String] = Members(s.Group);

        var forts = new Dictionary<string, SavedFort>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in data.OfType<KSg.Capital>())
            Fill(forts, c.City, squads, new SavedFort(true, -1, "", RaceOf(races, c.City)));
        foreach (var v in data.OfType<KSg.MidVillage>())
            Fill(forts, v.City, squads, new SavedFort(false, (int)v.Size.Value, Ref(v.ProtectB.String),
                                                      RaceOf(races, v.City)));
        return forts;
    }

    private static void Fill(Dictionary<string, SavedFort> forts, KSg.City city,
                             IReadOnlyDictionary<string, IReadOnlyList<string>> squads, SavedFort fort)
    {
        foreach (var id in Members(city.Group)) forts[id] = fort;
        if (Ref(city.Stack.String) is { Length: > 0 } stack && squads.TryGetValue(stack, out var visitors))
            foreach (var id in visitors) forts[id] = fort;
    }

    private static string RaceOf(IReadOnlyDictionary<string, string> races, KSg.City city) =>
        races.TryGetValue(Ref(city.Owner.String), out var race) ? race : "";

    private static IReadOnlyList<string> Members(KSg.Group group) =>
        group.Units.Select(u => u.String).Where(u => Ref(u).Length > 0).ToList();

    private static string Ref(string id) =>
        id.Equals(EmptyId, StringComparison.OrdinalIgnoreCase) ? "" : id;
}
