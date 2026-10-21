using System.Text.Json;
using D2.Rules.Map;

namespace D2.Data.Corpus;

/// <summary>Разбор событий схемы 7.2 (записи — в <c>CorpusEventsV72.cs</c>). Правило то же: пропавшее поле —
/// <see cref="JsonException"/>, JSON <c>null</c> — только там, где зонд его пишет.</summary>
public static partial class CorpusReader
{
    private static readonly HashSet<string> V72Events = new(StringComparer.Ordinal)
    {
        "ai_place_cache", "ai_fort_cache", "ai_strength_units", "regen", "upgrade_target", "path_expand", "id_alloc",
    };

    private static CorpusEvent ParseV72(JsonElement root, string ev, long seq, long tid)
    {
        long L(string name) => GetLong(root, ev, seq, name);
        string S(string name) => GetString(root, ev, seq, name);
        bool B(string name) => GetBool(root, ev, seq, name);
        long? NL(string name) => GetLongOrNull(root, ev, seq, name);
        string? NS(string name) => GetStringOrNull(root, ev, seq, name);
        bool? NB(string name) => GetBoolOrNull(root, ev, seq, name);
        double? ND(string name) => Double(GetObject(root, ev, seq, name));
        JsonElement O(string name) => GetObject(root, ev, seq, name);
        Bank? Purse(string name) => O(name) is { ValueKind: not JsonValueKind.Null } o ? ParseBank(o, ev, seq) : null;
        AiPoint? Pt(string name) => Point(O(name), ev, seq);
        IReadOnlyList<string?>? Ids(string name) => IdList(O(name));
        long t = L("t");

        return ev switch
        {
            "ai_place_cache" => new AiPlaceCacheEvent(seq, tid, t, S("at"), NS("player"), NS("owner"), Purse("purse"),
                Purse("bank"), NS("stack"), Pt("pos"),
                O("places") is { ValueKind: JsonValueKind.Array } places
                    ? places.EnumerateArray().Select(p => new AiCachePlace(GetStringOrNull(p, ev, seq, "id"),
                        GetStringOrNull(p, ev, seq, "cls"), Double(GetObject(p, ev, seq, "d")),
                        Point(GetObject(p, ev, seq, "pos"), ev, seq), GetString(p, ev, seq, "why"))).ToList()
                    : null,
                Ids("picked"), Ids("excluded"), S("ret_addr")),
            "ai_fort_cache" => new AiFortCacheEvent(seq, tid, t, NS("player"), NS("stack"), Pt("pos"),
                O("forts") is { ValueKind: JsonValueKind.Array } forts
                    ? forts.EnumerateArray().Select(f => new AiCacheFort(GetStringOrNull(f, ev, seq, "id"),
                        GetBool(f, ev, seq, "filter"), GetBoolOrNull(f, ev, seq, "guard"),
                        GetStringOrNull(f, ev, seq, "owner"), GetStringOrNull(f, ev, seq, "visitor"),
                        Double(GetObject(f, ev, seq, "d")), Point(GetObject(f, ev, seq, "pos"), ev, seq),
                        GetString(f, ev, seq, "why"))).ToList()
                    : null,
                Ids("picked") ?? throw new JsonException($"{ev} seq={seq}: picked — не массив"),
                Ids("excluded") ?? throw new JsonException($"{ev} seq={seq}: excluded — не массив"), L("place_seq")),
            "ai_strength_units" => new AiStrengthUnitsEvent(seq, tid, t, NS("player"), NS("stack"), L("a3"), L("enemies"),
                L("a6"), B("city"), B("full"), NL("armor"),
                GetArray(root, ev, seq, "units").Select(u => new AiStrengthUnit(GetStringOrNull(u, ev, seq, "id"),
                    GetStringOrNull(u, ev, seq, "type"), GetLong(u, ev, seq, "hp"), GetLong(u, ev, seq, "max"),
                    Double(GetObject(u, ev, seq, "s")),
                    IdList(GetObject(u, ev, seq, "mods")) ?? throw new JsonException($"{ev} seq={seq}: mods — не массив")))
                    .ToList(),
                ND("total"), S("ret_addr")),
            "regen" => new RegenEvent(seq, tid, t, S("term"), NS("unit"), NS("type"), NS("owner"), NS("inside"), NL("pct"),
                NL("bonus"), L("hp_before"), L("hp_after"), L("hp_max"), S("ret_addr")),
            "upgrade_target" => new UpgradeTargetEvent(seq, tid, t, B("leader"), NS("player"), NS("unit"), NS("type"),
                NL("xp"), NB("dyn"), NB("grow"), NS("container"), NS("owner"), NS("race_player"), NB("race_ok"),
                NS("target"), NL("flag"), S("ret_addr")),
            "path_expand" => new PathExpandEvent(seq, tid, t, NS("player"), NS("stack"), NS("owner"), Pt("target"),
                Pt("resolved"), L("forbid_water"), L("a5"), L("mode"), L("a8"), L("a10"), L("n"), B("cut"),
                GetArray(root, ev, seq, "nodes").Select(n => PathNode(n, ev, seq)).ToList(),
                GetArray(root, ev, seq, "path").Select(p => PathPointOf(p, ev, seq)).ToList(),
                Pt("end"), Pt("reach"), S("ret_addr")),
            "id_alloc" => new IdAllocEvent(seq, tid, t, NS("player"), L("type"), NS("id"), NL("index"), S("ret_addr"),
                GetArray(root, ev, seq, "callers").Select(c => c.GetString() ?? "").ToList()),
            _ => throw new ArgumentOutOfRangeException(nameof(ev), ev, "не событие схемы 7.2"),
        };
    }

    /// <summary>Массив id, где пустой id — JSON <c>null</c>; сам массив null — null.</summary>
    private static IReadOnlyList<string?>? IdList(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? null : x.GetString()).ToList()
        : null;

    /// <summary>Узел A* <c>[x, y, g, f]</c>.</summary>
    private static PathExpandNode PathNode(JsonElement n, string ev, long seq)
    {
        var a = n.EnumerateArray().ToList();
        return a.Count == 4
            ? new PathExpandNode(a[0].GetInt64(), a[1].GetInt64(), Double(a[2]), Double(a[3]))
            : throw new JsonException($"{ev} seq={seq}: узел A* — не [x, y, g, f]");
    }

    /// <summary>Точка пути <c>[x, y, цена]</c>.</summary>
    private static PathExpandPoint PathPointOf(JsonElement p, string ev, long seq)
    {
        var a = p.EnumerateArray().ToList();
        return a.Count == 3
            ? new PathExpandPoint(a[0].GetInt64(), a[1].GetInt64(), a[2].GetInt64())
            : throw new JsonException($"{ev} seq={seq}: точка пути — не [x, y, цена]");
    }
}
