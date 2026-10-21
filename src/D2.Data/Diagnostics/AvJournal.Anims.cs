using System.Text.RegularExpressions;
using D2.Data.Sound;

namespace D2.Data.Diagnostics;

/// <summary>Сверки анимаций карты и боя, звуков и надписей боя, курсоров (<see cref="AvJournal"/>).</summary>
public static partial class AvJournal
{
    /// <summary>Классы, у которых сравнивается период кадра, а не длительность: задача движка короче показа порта.</summary>
    private static readonly IReadOnlySet<string> PerTick = new HashSet<string>(StringComparer.Ordinal) { "CMidAnim2Unit" };

    /// <summary>Группа часов задачи (байт <c>CMidAnim2Base</c>: 0 — медленная, 1 — быстрая); у старых строк порта поля нет — 0.</summary>
    private static long GroupOf(AvEvent e) => e.L("group") ?? 0;

    /// <summary>Мс на тик задачи. Зонд оригинала (поле <c>started</c>) считает тики после старта — старт задачи сам приходится на тик
    /// (<c>0x40ae5f</c>), поэтому делитель на один больше; порт считает все показанные кадры.</summary>
    private static double TickMs(AvEvent e)
    {
        long ticks = (e.L("ticks") ?? 0) + (e.Fields.ContainsKey("started") ? 1 : 0);
        return ticks > 0 ? (e.L("ms") ?? 0) / (double)ticks : 0;
    }

    /// <summary>Период часов анимаций движка без обёртки, мс: медленная группа — 66 (<c>0x51d871</c>: <c>mov [esi+0x34], 0x42</c>), быстрая — 33.
    /// Его же держит порт (<c>AnimClock</c>).</summary>
    public const double EngineSlowClockMs = 66, EngineFastClockMs = 33;

    /// <summary>Период медленной группы часов при ускорении обёртки DisciplesGL (<c>tools/GLWrapper/DisciplesGL-HD</c>, <c>Hooks.cpp</c>
    /// <c>SetAnimSpeed</c>): обёртка перехватывает установку периода <c>0x51d871</c> (<c>speed_anim</c> версии 3.01b) и пишет в поле
    /// <c>10000 / (v · 15)</c>, где <c>v</c> = <c>GameSpeed</c> + 10 на карте (при <c>SpeedEnabled</c> = 1) и <c>BattleSpeed</c> обёртки + 10 в бою
    /// (секция <c>[Wrapper]</c> <c>Disciple.ini</c>, не путать с <c>BattleSpeed</c> игры). Быстрая группа (<c>0x51d929</c>, <c>speed_map</c>) —
    /// 1000 / частота экрана. У <c>game-dev</c> 17.10: <c>GameSpeed</c> 4 → 47 мс (×1,4), <c>BattleSpeed</c> 10 → 33 мс (×2); у прогонов
    /// драйвера (<c>SpeedEnabled</c> 0) — 66.</summary>
    public static int WrapperClockMs(int speedIndex) => 10000 / ((speedIndex + 10) * 15);

    /// <summary>Период часов группы <paramref name="group"/> у журнала, мс: по строкам <c>frames</c> над картой (<c>slow_ticks</c>/<c>fast_ticks</c>
    /// за окно <c>ms</c>, медиана) — у журнала оригинала, где обёртка могла ускорить часы (<see cref="WrapperClockMs"/>); строк с тиками нет
    /// (порт) — период движка.</summary>
    public static double ClockMs(IReadOnlyList<AvEvent> events, long group)
    {
        ArgumentNullException.ThrowIfNull(events);
        string field = group == 0 ? "slow_ticks" : "fast_ticks";
        var periods = events.Where(e => e.Ev == "frames" && Screen(e.S("top")) == "DLG_ISO" && e.L(field) is > 0 && e.L("ms") is > 0)
            .Select(e => (long)Math.Round(e.L("ms")!.Value * 100.0 / e.L(field)!.Value)).ToList();
        return periods.Count > 0 ? Median(periods) / 100.0 : group == 0 ? EngineSlowClockMs : EngineFastClockMs;
    }

    /// <summary>Анимации карты: класс → медиана длительности у законченных в тиках часов своей группы (мс / <see cref="ClockMs"/>): у движка
    /// длительность задачи — число тиков, а мс зависят от периода часов, который обёртка оригинала меняет (<see cref="WrapperClockMs"/>: 750 мс
    /// начала боя при 47 мс — те же 16 тиков, что 1042 мс у порта при 66). Ход отряда — тиков на ключевой кадр в своей группе часов (у хода
    /// группа — флажок <c>StackSmoothScroll</c>, при разных флажках двух партий сравнивать нечего).</summary>
    public static IEnumerable<AvDiff> MapAnims(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        static Dictionary<string, List<AvEvent>> Ended(IEnumerable<AvEvent> events) => events
            .Where(e => e.Ev == "map_anim" && e.S("action") == "end")
            .GroupBy(e => PerTick.Contains(e.S("class") ?? "") ? $"{e.S("class")} (мс на кадр, группа {GroupOf(e)})" : e.S("class") ?? "?",
                StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var a = Ended(original);
        var b = Ended(port);
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(key, out var x)) { yield return new AvDiff("map_anim", key, "анимации нет", Anim(b[key], port), true); continue; }
            if (!b.TryGetValue(key, out var y)) { yield return new AvDiff("map_anim", key, Anim(x, original), "анимации нет", true); continue; }
            if (key.Contains("мс на кадр", StringComparison.Ordinal))
            {
                // Ход отряда: у движка задача — прямой отрезок пути, у порта — весь ход; сравнивается период ключевого кадра в тиках.
                double cx = ClockMs(original, GroupOf(x[0])), cy = ClockMs(port, GroupOf(y[0]));
                double px = Median(x.Select(TickMs).Where(v => v > 0).Select(v => (long)Math.Round(v * 100 / cx))) / 100;
                double py = Median(y.Select(TickMs).Where(v => v > 0).Select(v => (long)Math.Round(v * 100 / cy))) / 100;
                yield return new AvDiff("map_anim", key, $"{px:0.0} тика по {cx:0} мс", $"{py:0.0} тика по {cy:0} мс",
                    Math.Abs(px - py) <= Math.Max(0.15, px * AnimTolerance));
                continue;
            }
            double tx = Ticks(x, original), ty = Ticks(y, port);
            yield return new AvDiff("map_anim", key, Anim(x, original), Anim(y, port), Math.Abs(tx - ty) <= Math.Max(1, tx * AnimTolerance));
        }
    }

    /// <summary>Медиана длительности задач в тиках часов их группы.</summary>
    private static double Ticks(List<AvEvent> list, IReadOnlyList<AvEvent> journal)
    {
        double clock = ClockMs(journal, GroupOf(list[0]));
        return Median(list.Select(e => (long)Math.Round((e.L("ms") ?? 0) * 10 / clock))) / 10;
    }

    private static string Anim(List<AvEvent> list, IReadOnlyList<AvEvent> journal) =>
        $"{Median(list.Select(e => e.L("ms") ?? 0)):0} мс = {Ticks(list, journal):0.0} тика по {ClockMs(journal, GroupOf(list[0])):0} мс (n={list.Count})";

    /// <summary>Кадры картинки анимации карты по классу (<c>frames</c>: начало боя, гибель, создание, смена владельца — одна картинка на
    /// класс; литьё — у каждого заклинания своя): кадры порта — из тех, что показал движок. Классы без картинки не сверяются.</summary>
    public static IEnumerable<AvDiff> MapAnimFrames(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        static Dictionary<string, SortedSet<long>> Frames(IEnumerable<AvEvent> events) => events
            .Where(e => e.Ev == "map_anim" && e.S("action") == "end" && e.L("frames") is > 0)
            .GroupBy(e => e.S("class") ?? "?", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new SortedSet<long>(g.Select(e => e.L("frames")!.Value)), StringComparer.Ordinal);
        var a = Frames(original);
        var b = Frames(port);
        foreach (var key in a.Keys.Intersect(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            yield return new AvDiff("map_anim_frames", key, string.Join(",", a[key]), string.Join(",", b[key]), b[key].IsSubsetOf(a[key]));
    }

    /// <summary>Анимации боя: «тип юнита/вид» → кадры (<c>bat_anim</c>: покой, замах, удар, труп); эффекты ходящего и на целях:
    /// «вид@тип ходящего» → кадры (<c>bat_fx</c>: <c>HEFF</c>, <c>TUCH</c>, <c>TICK</c>, <c>HEAL</c>; тип — по строкам <c>bat_anim</c> того же
    /// юнита в журнале: у зонда оригинала в <c>bat_fx</c> только id). Данные одни — кадры обязаны совпасть точно. Эффект <c>Battle.ff</c> на
    /// юните (<c>EFFECT</c>: смерть 26/29, уход призванного 16, пауза призыва 23, превращения) — картинка по эффекту, а не по типу: его кадры
    /// сверяются одним набором (<c>EFFECT</c>, кадры порта — из тех, что показал движок).</summary>
    public static IEnumerable<AvDiff> BattleFrames(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        static Dictionary<string, SortedSet<long>> Frames(IReadOnlyList<AvEvent> events)
        {
            // Тип юнита — по строкам bat_anim этого журнала (id юнитов в разных партиях повторяются: Join).
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<string, SortedSet<long>>(StringComparer.OrdinalIgnoreCase);
            void Add(string key, long frames)
            {
                if (!map.TryGetValue(key, out var set)) map[key] = set = [];
                set.Add(frames);
            }
            foreach (var e in events)
            {
                if (e.Ev == JournalBreak) types.Clear();
                if (e.Ev == "bat_anim" && e.S("unit") is { } who && e.S("type") is { } whoType) types[who] = whoType;
                if (e.L("frames") is not { } frames || frames < 0) continue;
                if (e.Ev == "bat_anim" && e.S("kind") == "EFFECT") Add("EFFECT", frames);
                else if (e.Ev == "bat_anim" && e.S("type") is { } type) Add($"{type}/{e.S("kind")}", frames);
                else if (e.Ev == "bat_fx" && e.S("unit") is { } unit && types.TryGetValue(unit, out var actor))
                    Add($"{e.S("kind")}@{actor}", frames);
            }
            return map;
        }
        var a = Frames(original);
        var b = Frames(port);
        foreach (var key in a.Keys.Intersect(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal))
            yield return new AvDiff("battle_frames", key, string.Join(",", a[key]), string.Join(",", b[key]),
                key == "EFFECT" ? b[key].IsSubsetOf(a[key]) : a[key].SetEquals(b[key]));
    }

    /// <summary>
    /// Звуки боя по ходящему (<paramref name="sounds"/> — звуки юнитов, <c>data/sound/UnitSounds.json</c>; без них сверки нет). Звук
    /// проигрывателя боя относится к последнему замаху (<c>bat_anim HMOV</c>) в журнале; если имя — из списков ходящего, это его роль:
    /// <c>attack</c> (замах), <c>effect</c> (эффект ходящего), <c>hit</c> (удар по цели). У типа, который ходил в обеих партиях, роли
    /// порта — из ролей движка, и замах звучит у обоих или ни у кого (варианты внутри списка случайны — сверяется роль, а не имя). Звуки
    /// боя не из списков юнитов (<c>DEATH</c>, классы атак, уход призванного) — одним набором <c>battle_other</c>: лишнее у порта —
    /// расхождение. «Больно» целей (списки целей) не сверяются: цели у двух партий разные.
    /// </summary>
    public static IEnumerable<AvDiff> BattleSounds(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port, UnitSounds? sounds = null)
    {
        if (sounds is null) yield break;
        var (rolesA, otherA) = BattleRoles(original, sounds);
        var (rolesB, otherB) = BattleRoles(port, sounds);
        foreach (var type in rolesA.Keys.Intersect(rolesB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal))
        {
            var x = rolesA[type];
            var y = rolesB[type];
            bool ok = y.IsSubsetOf(x) && x.Contains("attack") == y.Contains("attack");
            yield return new AvDiff("battle_sounds", type, Join(x), Join(y), ok);
        }
        if (otherA.Count == 0 && otherB.Count == 0) yield break;
        var extra = otherB.Except(otherA, StringComparer.OrdinalIgnoreCase).ToList();
        yield return new AvDiff("battle_sounds", "battle_other", Join(otherA),
            extra.Count == 0 ? Join(otherB) : "только у порта: " + string.Join(",", extra), extra.Count == 0);
    }

    /// <summary>Роли звуков у каждого ходившего типа (только те, кто замахивался) и звуки боя не из списков юнитов.</summary>
    private static (Dictionary<string, SortedSet<string>> Roles, SortedSet<string> Other) BattleRoles(IReadOnlyList<AvEvent> events,
                                                                                                      UnitSounds sounds)
    {
        var roles = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var other = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string? actor = null;
        foreach (var e in events)
        {
            if (e.Ev == JournalBreak) actor = null;
            if (e.Ev == "bat_anim" && e.S("kind") == "HMOV" && e.S("type") is { } type)
            {
                actor = type.ToUpperInvariant();
                if (!roles.ContainsKey(actor)) roles[actor] = new SortedSet<string>(StringComparer.Ordinal);
                continue;
            }
            if (e.Ev != "snd" || e.S("via") != "battle" || e.B("ok") == false || e.S("name") is not { } name) continue;
            if (!sounds.IsUnitSound(name))
            {
                other.Add(name.ToUpperInvariant());
                continue;
            }
            if (actor is not null) roles[actor].UnionWith(sounds.RolesOf(actor, name));
        }
        return (roles, other);
    }

    /// <summary>Надписи боя (<c>bat_text</c>: урон, лечение, промах, статусы на портрете, <c>0x64d09c</c>) — шаблоны с числами, заменёнными
    /// на «#»: шаблон порта, которого движок не писал, — расхождение; шаблоны одной стороны движка — что партия не показала.</summary>
    public static IEnumerable<AvDiff> BattleTexts(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        static SortedSet<string> Templates(IEnumerable<AvEvent> events) =>
            new(events.Where(e => e.Ev == "bat_text" && e.S("text") is not null).Select(e => Digits().Replace(e.S("text")!, "#")),
                StringComparer.Ordinal);
        var a = Templates(original);
        var b = Templates(port);
        if (a.Count == 0 || b.Count == 0) yield break;
        var extra = b.Except(a, StringComparer.Ordinal).ToList();
        yield return new AvDiff("battle_text", "шаблоны", Join(a), extra.Count == 0 ? Join(b) : "только у порта: " + string.Join(",", extra),
            extra.Count == 0);
    }

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    /// <summary>Курсоры по экрану: имя картинки (<c>cursor</c>). Имена порта приводятся к именам движка: без архива (<c>IsoCursr:</c>,
    /// <c>Battle:</c>), у курсора с картинкой тащимого (<c>…+IconItem:…</c>) — только курсор. Хвост «A» у курсоров боя — не буква кадра, а
    /// анимированный вариант (<c>CUATTACKA</c>, <c>0x5b343c</c>) при «дополнительной анимации боя»: журнал оригинала с <c>BattleAnim</c> 1
    /// (<c>20261017-port-synth</c>) пишет <c>CUATTACKA</c>/<c>CUDEFAULTA</c>, с 0 — <c>CUATTACK</c>; имя сверяется как есть. Курсор порта, которого
    /// движок на этом экране не ставил, — расхождение; только экраны обеих партий.</summary>
    public static IEnumerable<AvDiff> Cursors(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        static Dictionary<string, SortedSet<string>> ByScreen(IEnumerable<AvEvent> events)
        {
            var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var e in events.Where(e => e.Ev == "cursor" && e.S("name") is { Length: > 0 }))
            {
                string name = CursorName(e.S("name")!);
                if (name is "-" or "?") continue;
                string screen = Screen(e.S("top"));
                if (!map.TryGetValue(screen, out var set)) map[screen] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(name);
            }
            return map;
        }
        var a = ByScreen(original);
        var b = ByScreen(port);
        var seen = new HashSet<string>(a.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
        foreach (var screen in a.Keys.Intersect(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            // Курсор порта, которого движок здесь не ставил, но ставил на другом экране, — что партия не показала (навёл не туда), а
            // курсор, которого движок не ставил нигде, — расхождение.
            var extra = b[screen].Except(a[screen], StringComparer.OrdinalIgnoreCase).Where(n => !seen.Contains(n)).ToList();
            yield return new AvDiff("cursor", screen, Join(a[screen]),
                extra.Count == 0 ? Join(b[screen]) : "только у порта: " + string.Join(",", extra), extra.Count == 0);
        }
    }

    /// <summary>Имя курсора порта в именах движка (см. <see cref="Cursors"/>). Курсор окна из разметки (<c>ui:_CUDEFAUL</c>, кинжал
    /// <c>Interf.ff</c>) — «?»: зонд оригинала знает имена только курсоров карты и боя (<c>0x5b27ee</c>, <c>0x5b345e</c>), прочие у него «?».</summary>
    public static string CursorName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.StartsWith("ui:", StringComparison.Ordinal)) return "?";
        string n = name.Split('+')[0];
        int colon = n.IndexOf(':', StringComparison.Ordinal);
        n = colon >= 0 ? n[(colon + 1)..] : n;
        return n.ToUpperInvariant();
    }
}
