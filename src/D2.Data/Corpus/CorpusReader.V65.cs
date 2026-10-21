using System.Text.Json;
using D2.Rules.Map;

namespace D2.Data.Corpus;

/// <summary>Разбор событий схемы 6.5 (записи — в <c>CorpusEventsV65.cs</c>). Правило то же, что у 6.0–6.4:
/// пропавшее поле — <see cref="JsonException"/>, JSON <c>null</c> — только там, где зонд его пишет.</summary>
public static partial class CorpusReader
{
    private static readonly HashSet<string> V65Events = new(StringComparer.Ordinal)
    {
        "ai_eval", "ai_candidate", "ai_exec", "ai_budget_spend", "ai_city_rank", "ai_leader_role",
        "ai_leader_pick", "ai_unit_pick", "ai_build_pick", "ai_rng", "crt_rand", "ai_tactic_candidate",
        "ai_tactic_adjust", "ai_tactic_pick", "ai_tactic_stack", "ai_tactic_result", "ai_rod_cell", "ai_strength",
        // Схема 7.1.
        "ai_hire_score", "ai_grid", "ai_grid_fort",
    };

    private static CorpusEvent ParseV65(JsonElement root, string ev, long seq, long tid)
    {
        long L(string name) => GetLong(root, ev, seq, name);
        string S(string name) => GetString(root, ev, seq, name);
        bool B(string name) => GetBool(root, ev, seq, name);
        long? NL(string name) => GetLongOrNull(root, ev, seq, name);
        // Поле, которого может не быть вовсе: журналы 6.5.0 писали мнимые p2_id/p4_id вместо длин векторов.
        long? OptL(string name) => root.TryGetProperty(name, out _) ? GetLongOrNull(root, ev, seq, name) : null;
        string? NS(string name) => GetStringOrNull(root, ev, seq, name);
        bool? NB(string name) => GetBoolOrNull(root, ev, seq, name);
        double? ND(string name) => Double(GetObject(root, ev, seq, name));
        JsonElement O(string name) => GetObject(root, ev, seq, name);
        Bank? Purse(string name) => O(name) is { ValueKind: not JsonValueKind.Null } o ? ParseBank(o, ev, seq) : null;
        AiObject? Obj(string name) => AiObj(O(name), ev, seq);
        AiTacticAction? Act(string name) => TacticAction(O(name), ev, seq);
        AiPoint? Pt(string name) => Point(O(name), ev, seq);
        // Поля 7.1: в старых журналах их нет вовсе.
        AiPoint? OptPt(string name) => root.TryGetProperty(name, out var p) ? Point(p, ev, seq) : null;
        IReadOnlyList<string?>? OptIds(string name) => root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? null : x.GetString()).ToList()
            : null;
        IReadOnlyList<AiTacticCandidate>? Cands(string name) => O(name) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(c => new AiTacticCandidate(TacticAction(GetObject(c, ev, seq, "action"), ev, seq),
                Double(GetObject(c, ev, seq, "score")))).ToList()
            : null;
        long t = L("t");

        return ev switch
        {
            "ai_eval" => new AiEvalEvent(seq, tid, t, NS("player"), L("pass"), NS("list"), Obj("eval"), S("ret_addr")),
            "ai_candidate" => new AiCandidateEvent(seq, tid, t, NS("player"), L("pass"), NL("idx"), NB("chosen"),
                Obj("action"), ND("score"), NL("count_after"), S("ret_addr")),
            "ai_exec" => new AiExecEvent(seq, tid, t, NS("player"), L("pass"), S("fn"), Obj("action"), L("rng_before"),
                L("rng_after"), S("ret_addr"), B("ret"), L("seq_end")),
            "ai_budget_spend" => new AiBudgetSpendEvent(seq, tid, t, NS("player"), L("pass"), L("type"), NL("reserve"),
                Purse("budget_before"), Purse("budget_after"), Purse("start_before"), Purse("start_after"),
                Purse("spell_start_before"), Purse("spell_start_after"), S("ret_addr")),
            "ai_city_rank" => new AiCityRankEvent(seq, tid, t, NS("player"), L("pass"), NL("count"),
                GetArray(root, ev, seq, "cities").Select(c => new AiCityRank(GetStringOrNull(c, ev, seq, "id"),
                    GetLongOrNull(c, ev, seq, "prio"))).ToList(), S("ret_addr")),
            "ai_leader_role" => new AiLeaderRoleEvent(seq, tid, t, NS("player"), L("pass"), S("fn"), Longs(root, ev, seq, "b"),
                Longs(root, ev, seq, "i"), GetArray(root, ev, seq, "f").Select(Double).ToList(), Longs(root, ev, seq, "j"),
                S("ret_addr"), L("ret")),
            "ai_leader_pick" => new AiLeaderPickEvent(seq, tid, t, NS("player"), L("pass"), NS("id"), Purse("bank"),
                L("role"), B("flag"), NS("result"), NL("roll"), L("rng_before"), L("rng_after"), S("ret_addr"),
                L("seq_end")),
            "ai_unit_pick" => new AiUnitPickEvent(seq, tid, t, NS("player"), L("pass"), NS("id"), NS("filter"),
                S("filter_vft"), NL("list_len"), NS("result"), L("rng_std_before"), L("rng_std_after"), S("ret_addr"),
                L("seq_end")),
            "ai_build_pick" => new AiBuildPickEvent(seq, tid, t, NS("player"), L("pass"), S("strategy"), NS("id"),
                NS("result"), NL("status"), S("ret_addr")),
            "ai_rng" => new AiRngEvent(seq, tid, t, S("site"), S("kind"), L("n"), L("max"), L("r"), NL("raw"),
                B("battle"), S("ret_addr"))
            {
                // Схема 7.5; у старых журналов полей нет.
                Player = root.TryGetProperty("player", out _) ? NS("player") : null,
                Outer = root.TryGetProperty("outer", out _) ? Mods(root, ev, seq, "outer") : null,
                Origin = root.TryGetProperty("origin", out _) ? NS("origin") : null,
            },
            "crt_rand" => new CrtRandEvent(seq, tid, t, L("n"), L("r"), S("ret_addr")),
            "ai_tactic_candidate" => new AiTacticCandidateEvent(seq, tid, t, NS("player"), Act("action"),
                ND("score_in"), NL("idx"), NB("inserted"), ND("score"), S("ret_addr")),
            "ai_tactic_adjust" => new AiTacticAdjustEvent(seq, tid, t, NS("player"), S("kind"), NS("stack"),
                Act("action"), NL("priority"), ND("before"), ND("after"), S("ret_addr")),
            "ai_tactic_pick" => new AiTacticPickEvent(seq, tid, t, NS("player"), L("phase_before"), L("phase_after"),
                NS("stack"), NS("stack_after"), NL("count"), Cands("top"), Cands("chosen"), L("rng_before"),
                L("rng_after"), S("ret_addr"), B("ret"), L("seq_end")),
            "ai_tactic_stack" => new AiTacticStackEvent(seq, tid, t, NS("player"), NS("stack"), L("pick_seq"),
                GetNullableString(root, "inside") is { } inside && !inside.Equals(EmptyId, StringComparison.OrdinalIgnoreCase) ? inside : null, OptPt("pos")),
            "ai_tactic_result" => new AiTacticResultEvent(seq, tid, t, NS("player"), NS("stack"), NS("target"),
                Pt("point"), NL("turn"), S("turn_ptr"), L("status"), S("ret_addr")),
            "ai_rod_cell" => new AiRodCellEvent(seq, tid, t, NS("player"), NS("stack"), NS("crystal"),
                Pt("crystal_pos"), Pt("result"),
                GetArray(root, ev, seq, "cells").Select(c => new AiRodCell(GetLong(c, ev, seq, "x"),
                    GetLong(c, ev, seq, "y"), GetBoolOrNull(c, ev, seq, "ok_place"),
                    GetBoolOrNull(c, ev, seq, "ok_ally"), GetBoolOrNull(c, ev, seq, "ok_block"),
                    GetLongOrNull(c, ev, seq, "dist"), GetBool(c, ev, seq, "chosen"))).ToList(),
                S("ret_addr"), B("ret")),
            "ai_strength" => new AiStrengthEvent(seq, tid, t, NS("player"), S("p1"), S("p2"), S("p4"), OptL("p2_n"),
                OptL("p4_n"), ND("a"), ND("b"), ND("c"), S("ret_addr"), ND("ret"), OptIds("p2_units"), OptIds("p4_units")),
            "ai_hire_score" => new AiHireScoreEvent(seq, tid, t, NS("player"), NS("stack"), NS("fort"), ND("score"),
                NL("free"), NL("removable"), NB("can_hire"), S("ret_addr")),
            "ai_grid" => new AiGridEvent(seq, tid, t, NS("player"), NS("owner"), S("grid"), S("ret_addr"), L("seq_end")),
            "ai_grid_fort" => new AiGridFortEvent(seq, tid, t, NS("player"), S("grid"), Pt("center"), L("radius")),
            _ => throw new ArgumentOutOfRangeException(nameof(ev), ev, "не событие схемы 6.5"),
        };
    }

    /// <summary>Пустой id движка (значение 0): зонд пишет его как есть.</summary>
    private const string EmptyId = "G000000000";

    private static double? Double(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();

    private static IReadOnlyList<long> Longs(JsonElement root, string ev, long seq, string name) =>
        GetArray(root, ev, seq, name).Select(x => x.GetInt64()).ToList();

    private static readonly HashSet<string> AiObjectFixed = new(StringComparer.Ordinal) { "vft", "class", "type", "raw" };

    private static AiObject? AiObj(JsonElement o, string ev, long seq) => o.ValueKind == JsonValueKind.Null
        ? null
        : new AiObject(GetString(o, ev, seq, "vft"), GetStringOrNull(o, ev, seq, "class"),
            GetLongOrNull(o, ev, seq, "type"),
            o.EnumerateObject().Where(p => !AiObjectFixed.Contains(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
            GetStringOrNull(o, ev, seq, "raw"));

    private static AiPoint? Point(JsonElement o, string ev, long seq) => o.ValueKind == JsonValueKind.Null
        ? null
        : new AiPoint(GetLong(o, ev, seq, "x"), GetLong(o, ev, seq, "y"));

    private static AiTacticAction? TacticAction(JsonElement o, string ev, long seq) => o.ValueKind == JsonValueKind.Null
        ? null
        : new AiTacticAction(GetString(o, ev, seq, "ptr"), GetString(o, ev, seq, "vft"),
            GetLongOrNull(o, ev, seq, "type"), GetStringOrNull(o, ev, seq, "stack"),
            GetStringOrNull(o, ev, seq, "target"), Point(GetObject(o, ev, seq, "point"), ev, seq));
}
