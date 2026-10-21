namespace D2.Data.Diagnostics;

/// <summary>Смена доступности кнопки или переключателя (<c>ui_enable</c>) после свёртки дрожания: экран (<see cref="AvJournal.EnableScreen"/>),
/// элемент (имя из разметки <c>.dlg</c>), погасла (<see cref="On"/> = false) или зажглась; <see cref="Seq"/>/<see cref="T"/> — первой строки серии,
/// <see cref="LastSeq"/>/<see cref="LastT"/> — последней (итог наступил на ней).</summary>
public sealed record EnableChange(long Seq, long T, string Screen, string Element, bool On, long LastSeq, long LastT)
{
    public string Look => $"{Element}{(On ? "+" : "−")}";
}

/// <summary>Сверка погашенных кнопок (схема 7.6, <c>ui_enable</c>; <c>docs/probe-av.md</c> §8.3; <see cref="AvJournal"/>).</summary>
public static partial class AvJournal
{
    /// <summary>Смены одного элемента, между которыми не больше стольких мс, — одна смена (итог — от состояния до первой к состоянию после
    /// последней): движок в одном такте ставит флаг и снимает его (<c>BTN_REPLAY</c> брифинга, <c>BTN_GUARD</c> панели — «погасла» и
    /// «зажглась» в одну мс, партия <c>20261020-faces</c>), а порт пишет итог перерисовки.</summary>
    public const long EnableSettleMs = SettleMs;

    /// <summary>Смены после нажатия, не позже стольких мс, — его следствие (у движка — в ту же мс: щелчок «Защищаться» гасит четыре кнопки
    /// панели боя; у порта — перерисовка того же или следующего кадра).</summary>
    public const long EnableJoinMs = 100;

    /// <summary>Экран строки интерфейса в одних именах у двух игр: панель боя у движка — <c>DLG_BATTLE_B</c> (разметка широкого экрана), у порта
    /// — <c>DLG_BATTLE_A</c>; прочие — разметка как есть (панели карты не сливаются в <c>DLG_ISO</c>, как у звука: имена элементов у них свои).</summary>
    public static string EnableScreen(string? screen) => screen switch
    {
        null or "" => "-",
        "DLG_BATTLE_B" => "DLG_BATTLE_A",
        _ => screen,
    };

    /// <summary>Смены доступности журнала по порядку: строки <c>ui_enable</c> одного элемента в пределах <see cref="EnableSettleMs"/> друг от
    /// друга свёрнуты в одну, без итога (погасла и зажглась) — выброшены. Строка зонда — всегда смена флага (оригинал: <c>0x531cca</c> пишет
    /// только смену; порт — смену против прошлой перерисовки), так что состояние до первой строки — обратное её <c>on</c>. Безымянный
    /// элемент оригинала — <c>#child</c> с номером.</summary>
    public static IReadOnlyList<EnableChange> EnableChanges(IReadOnlyList<AvEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return [.. Journals(events).SelectMany(JournalEnables)];
    }

    /// <summary>Журналы <see cref="Join"/> по отдельности: у каждого свои <c>seq</c> и время.</summary>
    private static IEnumerable<List<AvEvent>> Journals(IReadOnlyList<AvEvent> events)
    {
        var current = new List<AvEvent>();
        foreach (var e in events)
        {
            if (e.Ev != JournalBreak)
            {
                current.Add(e);
                continue;
            }
            yield return current;
            current = [];
        }
        yield return current;
    }

    /// <summary>Смены одного журнала (<see cref="EnableChanges"/>) по <c>seq</c>.</summary>
    private static List<EnableChange> JournalEnables(List<AvEvent> events)
    {
        var list = new List<EnableChange>();
        var runs = new Dictionary<(string Screen, string Element), (AvEvent First, AvEvent Last)>();
        void Flush((string Screen, string Element) key)
        {
            // До первой строки — обратное её «on», после последней — её «on»: совпали — смена есть.
            if (runs.Remove(key, out var run) && run.First.B("on") is { } first && run.Last.B("on") == first)
                list.Add(new EnableChange(run.First.Seq, run.First.T, key.Screen, key.Element, first, run.Last.Seq, run.Last.T));
        }
        foreach (var e in events)
        {
            if (e.Ev != "ui_enable") continue;
            var key = (EnableScreen(e.S("screen")), e.S("element") is { Length: > 0 } name ? name : $"#child{e.L("child")}");
            if (runs.TryGetValue(key, out var run) && e.T - run.Last.T > EnableSettleMs)
            {
                Flush(key);
                run = default;
            }
            runs[key] = (run.First ?? e, e);
        }
        foreach (var key in runs.Keys.ToList()) Flush(key);
        list.Sort((x, y) => x.Seq.CompareTo(y.Seq));
        return list;
    }

    /// <summary>
    /// Погашенные кнопки (<c>ui_enable</c>), по экрану и элементу (имена — разметки <c>.dlg</c>, у порта те же):
    /// <list type="bullet">
    /// <item><b><c>ui_enable</c></b> — элемент → в какую сторону он менялся («гаснет», «зажигается»): порт не гасит того, что движок не гасит,
    /// и не зажигает того, что движок не зажигает;</item>
    /// <item><b><c>ui_enable_click</c></b> — нажатие (<c>ui_click</c>: экран и элемент) → какие элементы того же экрана погасли или зажглись
    /// в пределах <see cref="EnableJoinMs"/> после него («без смен» — ни одного): последовательность смен вокруг одинаковых действий, без
    /// неё «порт кнопки не гасит вовсе» не видно (щелчок «Защищаться» у движка гасит «Защищаться», «Ждать», «Отступить» и
    /// <c>TOG_RIGHTUNITS</c> до хода человека).</item>
    /// </list>
    /// Порт ⊆ оригинала; ключ одной стороны — <c>Ok</c> (не сыграно). Только когда у оригинала есть строки <c>ui_enable</c>, а у порта —
    /// <c>ui_enable</c> или <c>ui_click</c> (зонд порта до Зс54 их не писал — журналы 20.10 и раньше сверка пропускает).
    /// </summary>
    public static IEnumerable<AvDiff> UiEnables(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(port);
        if (!original.Any(e => e.Ev == "ui_enable") || !port.Any(e => e.Ev is "ui_enable" or "ui_click")) yield break;
        static Dictionary<string, SortedSet<string>> Directions(IEnumerable<EnableChange> changes) => changes
            .GroupBy(c => $"{c.Screen}/{c.Element}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new SortedSet<string>(g.Select(c => c.On ? "зажигается" : "гаснет"), StringComparer.Ordinal),
                StringComparer.Ordinal);
        foreach (var d in SubsetDiffs("ui_enable", Directions(EnableChanges(original)), Directions(EnableChanges(port)))) yield return d;
        foreach (var d in SubsetDiffs("ui_enable_click", ClickEnables(original), ClickEnables(port))) yield return d;
    }

    /// <summary>Нажатия → наборы смен того же экрана после них (<see cref="UiEnables"/>): «элемент−» погас, «элемент+» зажёгся, по имени;
    /// журналы <see cref="Join"/> — по отдельности. Смена — следствие нажатия, если её итог (последняя строка серии) пришёл после нажатия по
    /// <c>seq</c> и не позже <see cref="EnableJoinMs"/> по времени: нажатие посреди дрожания получает итог серии. Время и <c>seq</c> у зонда
    /// оригинала с нескольких потоков идут не строго вместе — окно проверяется у каждой смены, а не обрывает просмотр.</summary>
    private static Dictionary<string, SortedSet<string>> ClickEnables(IReadOnlyList<AvEvent> events)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var journal in Journals(events))
        {
            var changes = JournalEnables(journal);
            foreach (var e in journal)
            {
                if (e.Ev != "ui_click" || e.S("element") is not { Length: > 0 } element) continue;
                string screen = EnableScreen(e.S("screen"));
                var after = new SortedSet<string>(changes
                    .Where(c => c.Screen == screen && c.LastSeq > e.Seq && c.LastT >= e.T && c.LastT <= e.T + EnableJoinMs)
                    .Select(c => c.Look), StringComparer.Ordinal);
                string key = $"{screen}/{element}";
                if (!map.TryGetValue(key, out var set)) map[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(after.Count == 0 ? "без смен" : string.Join(" ", after));
            }
        }
        return map;
    }
}
