namespace D2.Data.Diagnostics;

/// <summary>Портрет юнита в одном действии боя: надпись (шаблон — числа заменены на «#») и вспышка или стойкое состояние после неё
/// (<see cref="PortraitFlash"/>); у вспышки без надписи шаблон пуст, у надписи без вспышки нет состояния.</summary>
public sealed record FaceEvent(long Seq, string Unit, string Template, string? Src, int? State, string? Mode, bool Hold);

/// <summary>Сверки портретов боя и меток карты (схема 7.6, <c>docs/probe-av.md</c> §8; <see cref="AvJournal"/>).</summary>
public static partial class AvJournal
{
    /// <summary>Надпись и вспышка — одно действие, если вспышка того же юнита пришла не позже стольких мс после надписи (у движка — в той же
    /// мс: <c>0x64d09c</c>, затем <c>0x64cee8</c>; у порта — все надписи удара, затем все вспышки).</summary>
    public const long FaceJoinMs = 100;

    /// <summary>Портреты по действиям: надпись <c>bat_text</c> и следующая за ней вспышка <c>bat_flash</c> того же юнита (в пределах
    /// <see cref="FaceJoinMs"/>) — одно событие. Строки, где юнита в группе не нашлось (<c>found</c> = false: опыт конца боя зовётся на все
    /// четыре группы, <c>0x636f2d…0x636fb1</c>), не в счёт.</summary>
    public static IReadOnlyList<FaceEvent> FaceEvents(IReadOnlyList<AvEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = new List<FaceEvent>();
        var pending = new Dictionary<string, (AvEvent Text, string Template)>(StringComparer.Ordinal);
        void Flush(string unit)
        {
            if (!pending.Remove(unit, out var p)) return;
            list.Add(new FaceEvent(p.Text.Seq, unit, p.Template, p.Text.S("src"), null, null, false));
        }
        void FlushOld(long t)
        {
            foreach (var (unit, p) in pending.ToList())
                if (t - p.Text.T > FaceJoinMs) Flush(unit);
        }
        foreach (var e in events)
        {
            if (e.Ev == JournalBreak)
            {
                foreach (var unit in pending.Keys.ToList()) Flush(unit);
                continue;
            }
            if (e.Ev is not ("bat_text" or "bat_flash") || e.B("found") == false || e.S("unit") is not { } id) continue;
            FlushOld(e.T);
            if (e.Ev == "bat_text")
            {
                Flush(id);
                pending[id] = (e, Digits().Replace(e.S("text") ?? "", "#"));
                continue;
            }
            if (e.S("mode") == "none" || RefusedSteady(e)) continue;
            int state = (int)(e.L("state") ?? -1);
            if (pending.Remove(id, out var text))
                list.Add(new FaceEvent(text.Text.Seq, id, text.Template, text.Text.S("src"), state, e.S("mode"), e.B("hold") == true));
            else list.Add(new FaceEvent(e.Seq, id, "", e.S("src"), state, e.S("mode"), e.B("hold") == true));
        }
        foreach (var unit in pending.Keys.ToList()) Flush(unit);
        return [.. list.OrderBy(f => f.Seq)];
    }

    /// <summary>Паралич или окаменение удара (7, 8 «сразу», <c>0x64d040</c>) в журнале зонда до <c>bat_flash_allow</c>: строка писалась
    /// <c>mode</c> = <c>set</c>, хотя переход разрешает <c>0x64d05f</c>(текущее, новое) только 8 → 7/10, 7 → 10 и из 10 — а база живого (0, яды)
    /// ни 7, ни 8 не пропускает, база окаменевшего — не 8 (<c>0x64c2ab</c> опрашивает паралич второй раз). Портрет не менялся (Зс110, журнал
    /// 20261020-faces: «Окаменение» — 8 set у трёх живых, у порта вспышки нет — так и у движка). Такие строки (без <c>found</c> — запись не
    /// узнана) — как отказ; новый зонд пишет отказ сам (<c>mode</c> = <c>none</c>), а пущенный переход — с <c>found</c> = true. Цена: законный в
    /// старом журнале 8 → 7 (паралич у портрета с 8) тоже отброшен — у живого 8 не встаёт, случаев нет.</summary>
    private static bool RefusedSteady(AvEvent e) =>
        e.S("mode") == "set" && e.L("state") is 7 or 8 && e.S("src") == "target" && e.Fields.ContainsKey("found") && e.B("found") is null;

    /// <summary>Вид портрета события для сверки: состояние с именем, «стойко» и «держит»; «без вспышки» — надпись без смены состояния.</summary>
    public static string FaceLook(FaceEvent face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (face.State is not { } s) return "без вспышки";
        string look = $"{s} {PortraitFlash.Name(s)}";
        if (face.Mode == "set") look += " стойко";
        if (face.Hold) look += " держит";
        return look;
    }

    /// <summary>Ключ события: шаблон надписи; вспышка без надписи — по источнику.</summary>
    private static string FaceKey(FaceEvent face) => face.Template.Length > 0 ? face.Template : $"(без надписи: {face.Src ?? "?"})";

    /// <summary>Вспышки портретов по надписям (<c>battle_face</c>): у надписи одного шаблона — какие состояния портрета ей сопутствуют
    /// (<see cref="FaceLook"/>). Порт не должен показывать того, чего у движка с этой надписью не бывает, — в том числе надписи без вспышки
    /// там, где движок вспыхивает (лечение без синего, опыт без жёлтого); набор оригинала шире — партия порта показала не всё (не
    /// расхождение). Только журналы, где вспышки пишутся (схема 7.6: есть строки <c>bat_flash</c>, у порта — и строки <c>bat_text</c> с
    /// местом), иначе сверять нечего.</summary>
    public static IEnumerable<AvDiff> BattleFaces(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        if (!Has76(original) || !Has76(port)) yield break;
        static Dictionary<string, SortedSet<string>> Looks(IReadOnlyList<AvEvent> events) => FaceEvents(events)
            .GroupBy(FaceKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new SortedSet<string>(g.Select(FaceLook), StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var d in SubsetDiffs("battle_face", Looks(original), Looks(port))) yield return d;
    }

    /// <summary>Журнал схемы 7.6: надписи портретов с местом (<c>w</c>) — у порта и оригинала одинаково; у журналов 7.3–7.5 их нет.</summary>
    private static bool Has76(IReadOnlyList<AvEvent> events) => events.Any(e => e.Ev == "bat_text" && e.Fields.ContainsKey("w"));

    /// <summary>Сверка «набор порта — часть набора оригинала» по ключам обеих сторон; ключ одной стороны — <c>Ok</c> (не сыграно).</summary>
    private static IEnumerable<AvDiff> SubsetDiffs(string check, Dictionary<string, SortedSet<string>> a, Dictionary<string, SortedSet<string>> b)
    {
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(key, out var x)) { yield return new AvDiff(check, key, "нет у оригинала", Join(b[key]), true); continue; }
            if (!b.TryGetValue(key, out var y)) { yield return new AvDiff(check, key, Join(x), "нет у порта", true); continue; }
            var extra = y.Except(x, StringComparer.Ordinal).ToList();
            yield return new AvDiff(check, key, Join(x), extra.Count == 0 ? Join(y) : "только у порта: " + string.Join(",", extra),
                extra.Count == 0);
        }
    }

    /// <summary>Место надписи (<c>battle_text_place</c>): у шаблона — размер места «ш×в» (у движка 70×85, у большого 150×85,
    /// <c>0x64d160</c>), стойкая ли (<c>battle_text_persist</c>: опыт конца боя у движка без срока, <c>0x64d23e</c>), и тиков вспышки
    /// (<c>battle_flash_ticks</c>, <c>+0x44</c> группы). Порт ⊆ оригинала.</summary>
    public static IEnumerable<AvDiff> BattleTextPlaces(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        if (!Has76(original) || !Has76(port)) yield break;
        static Dictionary<string, SortedSet<string>> By(IEnumerable<AvEvent> events, Func<AvEvent, string?> value) => events
            .Where(e => e.Ev == "bat_text" && e.B("found") != false && e.L("w") is > 0 && value(e) is not null)
            .GroupBy(e => Digits().Replace(e.S("text") ?? "", "#"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new SortedSet<string>(g.Select(e => value(e)!), StringComparer.Ordinal), StringComparer.Ordinal);
        static string Place(AvEvent e) => $"{e.L("w")}×{e.L("h")}";
        static string? Persist(AvEvent e) => e.B("persist") is { } p ? (p ? "без срока" : "со сроком") : null;
        foreach (var d in SubsetDiffs("battle_text_place", By(original, Place), By(port, Place))) yield return d;
        foreach (var d in SubsetDiffs("battle_text_persist", By(original, Persist), By(port, Persist))) yield return d;
        static SortedSet<string> Ticks(IEnumerable<AvEvent> events) =>
            new(events.Where(e => e.Ev == "bat_flash" && e.S("mode") == "flash" && e.L("ticks") is >= 0).Select(e => e.L("ticks")!.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)), StringComparer.Ordinal);
        var ta = Ticks(original);
        var tb = Ticks(port);
        if (ta.Count > 0 && tb.Count > 0)
            yield return new AvDiff("battle_flash_ticks", "тиков вспышки", Join(ta), Join(tb), tb.IsSubsetOf(ta));
    }

    /// <summary>Перенос надписей портретов у порта (<c>battle_text_wrap</c>): строк показано столько же, сколько по правилу переноса движка
    /// в ширину места (<c>lines_rule</c>, <c>UiTextWrap</c> шрифтом надписи; Ин37), иначе длинная надпись выходит за портрет (слова
    /// пользователя 20.10, п. 3). Только порт: у движка картинка текста переносит сама (<c>CImage2Text</c>), строки не пишутся. Ключ —
    /// шаблон надписи, у оригинала — место надписи того же шаблона.</summary>
    public static IEnumerable<AvDiff> BattleTextWraps(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        if (!Has76(port)) yield break;
        var places = original.Where(e => e.Ev == "bat_text" && e.L("w") is > 0)
            .GroupBy(e => Digits().Replace(e.S("text") ?? "", "#"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Join(new SortedSet<string>(g.Select(e => $"{e.L("w")}×{e.L("h")}"), StringComparer.Ordinal)),
                StringComparer.Ordinal);
        foreach (var g in port.Where(e => e.Ev == "bat_text" && e.L("lines") is > 0 && e.L("lines_rule") is > 0)
                     .GroupBy(e => Digits().Replace(e.S("text") ?? "", "#"), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var bad = g.Where(e => e.L("lines") != e.L("lines_rule")).ToList();
            var shown = bad.Count > 0 ? bad[0] : g.First();
            string port76 = $"строк {shown.L("lines")}, по правилу {shown.L("lines_rule")}, ширина {shown.L("text_w")} из {shown.L("w")}";
            yield return new AvDiff("battle_text_wrap", g.Key, places.GetValueOrDefault(g.Key, "надписи нет"), port76, bad.Count == 0);
        }
    }

    /// <summary>
    /// Метки карты в начале хода человека (<c>turn_marks</c>): выбран ли отряд (эллипс <c>map_sel</c>) и показан ли путь (<c>map_path</c>) к
    /// первому вводу нового хода — по тому, остался ли к концу прошлого хода показанный путь (слова пользователя 20.10, п. 1: путь, не
    /// дойденный до конца, — у движка отряд снова выбран и путь виден). Конец хода у оригинала — нажатие <c>BTN_END_TURN</c>
    /// (<c>ui_click</c>), у порта — смена строки <c>turn</c> с человека на ИИ (состояние — на последнем вводе перед ней); первый ввод нового
    /// хода — <c>ui_click</c>/<c>map_click</c> оригинала после хода ИИ, у порта — <c>click</c>/<c>key</c> после <c>turn</c> человека.
    /// Порт ⊆ оригинала.
    /// </summary>
    public static IEnumerable<AvDiff> TurnMarks(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        var a = TurnStarts(original);
        var b = TurnStarts(port);
        if (a.Count == 0 || b.Count == 0) yield break;
        foreach (var d in SubsetDiffs("turn_marks", a, b)) yield return d;
    }

    /// <summary>Начала ходов человека: «путь к концу хода» → набор «выбран / путь» к первому вводу. У оригинала ввод посреди хода ИИ
    /// (между <c>ai_turn</c> <c>begin</c> и <c>end</c>: «Закрыть» окна речи ИИ) — ещё не новый ход, и новый ход — только после хода
    /// хотя бы одного ИИ.</summary>
    private static Dictionary<string, SortedSet<string>> TurnStarts(IReadOnlyList<AvEvent> events)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        if (!events.Any(e => e.Ev is "map_sel" or "map_path")) return map;
        bool sel = false, path = false, pathAtInput = false;
        bool? ended = null;   // путь к концу хода; null — ход не кончался
        bool humanTurn = false, waiting = false, aiOpen = false;
        foreach (var e in events)
        {
            switch (e.Ev)
            {
                case JournalBreak:
                    (sel, path, ended, humanTurn, waiting, aiOpen) = (false, false, null, false, false, false);
                    break;
                case "map_sel":
                    sel = e.S("action") == "show";
                    break;
                case "map_path":
                    path = e.S("action") == "show";
                    break;
                case "ai_turn" when e.S("cause") == "ai":
                    aiOpen = e.S("phase") == "begin";
                    if (!aiOpen && ended is not null) waiting = true;
                    break;
                case "ui_click" when e.S("element") == "BTN_END_TURN":
                    (ended, waiting) = (path, false);
                    break;
                case "ui_click" or "map_click" when waiting && !aiOpen && ended is { } before:
                    Add(before);
                    break;
                case "turn" when e.B("human") == true:
                    if (ended is not null) waiting = true;
                    humanTurn = true;
                    break;
                case "turn":
                    if (humanTurn) (ended, waiting) = (pathAtInput, false);
                    humanTurn = false;
                    break;
                case "click" or "key" when waiting && ended is { } before:
                    Add(before);
                    break;
                case "click" or "key":
                    pathAtInput = path;
                    break;
            }
        }
        return map;

        void Add(bool before)
        {
            string key = before ? "к концу хода путь был" : "к концу хода пути не было";
            if (!map.TryGetValue(key, out var set)) map[key] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add($"выбран {(sel ? "да" : "нет")}, путь {(path ? "да" : "нет")}");
            (ended, waiting, pathAtInput) = (null, false, path);
        }
    }
}
