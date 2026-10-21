using System.Text.Json;

namespace D2.Data.Corpus;

/// <summary>Разбор событий схемы 7.0 (записи — в <c>CorpusEventsV7.cs</c>). Правило то же: пропавшее обязательное
/// поле — <see cref="JsonException"/>; поля, которые зонд пишет только некоторым видам <c>ui_click</c>, — null.
/// Поля схемы 7.1 необязательны (журналы 7.0 их не несут); <c>ui_click.what</c> у <c>lbox</c> в 7.0 выводится из
/// адреса возврата.</summary>
public static partial class CorpusReader
{
    private static readonly HashSet<string> V7Events = new(StringComparer.Ordinal)
    {
        "ui_open", "ui_close", "ui_show", "ui_show_refused", "ui_hide", "ui_clear", "ui_click", "ui_text",
        "ui_msgbox", "game_phase", "screenshot",
        // 7.1, часть 3.
        "map_click", "rng_state",
    };

    /// <summary>Адреса возврата в <c>0x535f0c</c> (схема 7.1): сдвиг выбора и щелчок по строке.</summary>
    private const string ListScrollReturn = "0x535f8a", ListSelectReturn = "0x536168";

    private static string? LboxWhat(string kind, string retAddr) => kind != "lbox" ? null : retAddr switch
    {
        ListScrollReturn => "scroll",
        ListSelectReturn => "select",
        _ => "other",
    };

    private static CorpusEvent ParseV7(JsonElement root, string ev, long seq, long tid)
    {
        long L(string name) => GetLong(root, ev, seq, name);
        string S(string name) => GetString(root, ev, seq, name);
        bool B(string name) => GetBool(root, ev, seq, name);
        string? O(string name) => GetNullableString(root, name);
        long t = L("t");

        return ev switch
        {
            "ui_open" => new UiOpenEvent(seq, tid, t, S("name"), S("dialog"), S("parent_class"), S("kind"), S("top"),
                B("ok"), S("ret_addr"), O("parent"), O("top_class"), O("base")),
            "ui_close" => new UiCloseEvent(seq, tid, t, S("name"), S("dialog")),
            "ui_show" or "ui_show_refused" or "ui_hide" => new UiStackEvent(seq, tid, t, ev[3..], S("screen"),
                S("class"), S("kind"), L("depth"), S("top"), S("ret_addr"), O("iface"), O("top_class"), O("base")),
            "ui_clear" => new UiClearEvent(seq, tid, t, L("depth_before"), S("ret_addr")),
            "ui_click" => new UiClickEvent(seq, tid, t, S("kind"), S("screen"), S("element"),
                GetOptionalBool(root, "hotkey"), GetNullableLong(root, "key"), GetOptionalBool(root, "checked"),
                GetNullableLong(root, "index"), S("top"), S("ret_addr"),
                O("what") ?? LboxWhat(S("kind"), S("ret_addr")), GetNullableLong(root, "child"),
                root.TryGetProperty("area", out var area) ? area.EnumerateArray().Select(x => x.GetInt64()).ToList() : null,
                O("top_class"), O("base")),
            "ui_text" => new UiTextEvent(seq, tid, t, S("screen"), S("id"), B("opening")),
            "ui_msgbox" => new UiMsgBoxEvent(seq, tid, t, S("text"), GetStringOrNull(root, ev, seq, "last_text_id"),
                B("yes_no"), S("top"), S("ret_addr"), O("top_class")),
            "game_phase" => new GamePhaseEvent(seq, tid, t, S("what"), S("phase"), S("top"), O("top_class")),
            "screenshot" => new ScreenshotEvent(seq, tid, t, GetStringOrNull(root, ev, seq, "file"),
                ShotCameraOf(GetObject(root, ev, seq, "camera"), ev, seq), ShotDisplayOf(GetObject(root, ev, seq, "display"), ev, seq),
                GetStringOrNull(root, ev, seq, "ai_player"), S("top"), O("top_class"), O("via"),
                GetNullableLong(root, "time_ms")),
            "map_click" => new MapClickEvent(seq, tid, t, L("msg"), L("button"), S("action"), L("x"), L("y"),
                MapClickCellOf(GetObject(root, ev, seq, "cell"), ev, seq),
                GetObject(root, ev, seq, "objects") is { ValueKind: JsonValueKind.Array } objects
                    ? objects.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : null,
                GetStringOrNull(root, ev, seq, "task"), L("busy"), L("ret"), S("top"), O("top_class")),
            "rng_state" => new RngStateEvent(seq, tid, t, S("what"), L("n"), L("index"), L("wide"),
                RngSeedEvent.ParseTable(S("table"))
                    ?? throw new JsonException($"{ev} seq={seq}: table — не 250 слов по 4 шестнадцатеричных знака"),
                GetLongOrNull(root, ev, seq, "saved_index"), GetBoolOrNull(root, ev, seq, "saved_same"),
                GetBoolOrNull(root, ev, seq, "random"), S("ret_addr")),
            _ => throw new ArgumentOutOfRangeException(nameof(ev), ev, "не событие схемы 7.0"),
        };
    }

    private static ShotCamera? ShotCameraOf(JsonElement c, string ev, long seq) => c.ValueKind != JsonValueKind.Object
        ? null
        : new ShotCamera(GetLong(c, ev, seq, "x"), GetLong(c, ev, seq, "y"), GetLong(c, ev, seq, "dx"), GetLong(c, ev, seq, "dy"));

    private static MapClickCell? MapClickCellOf(JsonElement c, string ev, long seq) => c.ValueKind != JsonValueKind.Object
        ? null
        : new MapClickCell(GetLong(c, ev, seq, "x"), GetLong(c, ev, seq, "y"));

    private static ShotDisplay? ShotDisplayOf(JsonElement d, string ev, long seq) => d.ValueKind != JsonValueKind.Object
        ? null
        : new ShotDisplay(GetLong(d, ev, seq, "width"), GetLong(d, ev, seq, "height"), GetBool(d, ev, seq, "use_d3d"),
            GetBool(d, ev, seq, "fullscreen"), GetBool(d, ev, seq, "animated_iso"), GetBool(d, ev, seq, "iso_anim"),
            GetBool(d, ev, seq, "flipped_battle"), GetBool(d, ev, seq, "turn_summary"));
}
