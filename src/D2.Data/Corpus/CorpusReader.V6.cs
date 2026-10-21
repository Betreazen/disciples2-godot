using System.Text.Json;
using D2.Rules.Map;

namespace D2.Data.Corpus;

/// <summary>Разбор событий схемы 6.0, партия 1 (записи — в <c>CorpusEventsV6.cs</c>). Правило то же:
/// пропавшее поле — дрейф схемы, <see cref="JsonException"/>; JSON <c>null</c> допускается только там,
/// где зонд его пишет (дельта и величина эффекта, <c>human_*</c>, пары отношений).</summary>
public static partial class CorpusReader
{
    private static readonly HashSet<string> V6Events = new(StringComparer.Ordinal)
    {
        "save", "fort_capture", "land_grow", "land_reject", "site_visit", "spell_expire", "item_create",
        "ap_effect", "ap_apply", "hp_change", "garrison_max", "scout", "rod_check", "hire_check",
        "hire_leader_check", "merc_check", "sell_check", "grow_check", "noble_action", "thief_cancel",
        "noble_choose", "alliance_propose", "alliance_change", "trade_accept",
        // Партия 2.
        "skill_offer", "skill_pick", "upg_count", "equip_check", "wand_check", "potion_use", "item_consume",
        "talisman_charge", "xp_group",
        // 6.1.
        "attack_hit",
        // 6.2.
        "rng_seed", "ap_restore",
        // 6.3.
        "unit_transfer", "swap_check", "dismiss_check", "equip_drop", "ui_refusal", "wand_precheck",
        // 6.4.
        "ai_break_check", "ai_offer_check", "ai_gift_check", "ai_answer", "ai_strategic_step", "ai_turn",
        "ai_order", "ai_budget", "ai_chat",
    };

    /// <summary>Поля <c>item_create</c>, которые не ид-поля.</summary>
    private static readonly HashSet<string> ItemCreateFixed = new(StringComparer.Ordinal)
    {
        "seq", "tid", "t", "ev", "apply", "ret_addr", "cause", "ret", "seq_end",
    };

    private static CorpusEvent ParseV6(JsonElement root, string ev, long seq, long tid)
    {
        long L(string name) => GetLong(root, ev, seq, name);
        string S(string name) => GetString(root, ev, seq, name);
        bool B(string name) => GetBool(root, ev, seq, name);
        long? NL(string name) => GetLongOrNull(root, ev, seq, name);
        string? NS(string name) => GetStringOrNull(root, ev, seq, name);
        bool? NB(string name) => GetBoolOrNull(root, ev, seq, name);
        RelationWord? W(string name) => Word(GetObject(root, ev, seq, name), ev, seq);
        Bank? Purse(string name) => GetObject(root, ev, seq, name) is { ValueKind: not JsonValueKind.Null } o
            ? ParseBank(o, ev, seq) : null;
        long t = L("t");

        return ev switch
        {
            "save" => new SaveEvent(seq, tid, t, S("name"), B("temp"), L("turn"), B("ok"), L("seq_end")),
            "fort_capture" => new FortCaptureEvent(seq, tid, t, S("stack"), S("fort"), S("owner"), S("subrace"),
                B("skip_leader_check"), S("owner_before"), S("subrace_before"), S("visitor_before"), S("owner_after"),
                B("ret"), L("seq_end")),
            "land_grow" => new LandGrowEvent(seq, tid, t, L("x"), L("y"), L("quota"), L("terrain"), L("count"),
                LandGrowEvent.ParseCells(S("cells")), B("ret"), L("seq_end")),
            "land_reject" => new LandRejectEvent(seq, tid, t, L("x"), L("y"), L("cx"), L("cy"), L("quota")),
            "site_visit" => new SiteVisitEvent(seq, tid, t, S("site"), S("stack"), S("owner"), L("move_left"), B("ret")),
            "spell_expire" => new SpellExpireEvent(seq, tid, t, S("player"), L("turn"), B("ret"), L("seq_end")),
            "item_create" => new ItemCreateEvent(seq, tid, t,
                root.EnumerateObject().Where(p => !ItemCreateFixed.Contains(p.Name))
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal),
                B("apply"), S("ret_addr"), S("cause"), B("ret"), L("seq_end")),
            "ap_effect" => new ApEffectEvent(seq, tid, t, S("effect"), S("stack"), NL("delta"), S("ret_addr"), S("cause")),
            "ap_apply" => new ApApplyEvent(seq, tid, t, S("effect"), S("stack"), NL("delta"), L("move_before"),
                L("move_after"), B("ret"), L("seq_end")),
            "hp_change" => new HpChangeEvent(seq, tid, t, S("unit"), NL("amount"), L("flag"), L("hp_before"),
                L("hp_after"), B("ret"), L("seq_end")),
            "garrison_max" => new GarrisonMaxEvent(seq, tid, t, S("group"), S("fort"), NL("allowed"), S("ret_addr"), L("ret")),
            "scout" => new ScoutEvent(seq, tid, t, L("bonus"), B("clamped"), S("ret_addr"), L("ret")),
            "rod_check" => new RodCheckEvent(seq, tid, t, S("stack"), L("x"), L("y"), B("flag"), S("ret_addr"), L("ret")),
            "hire_check" => new HireCheckEvent(seq, tid, t, S("player"), S("unit_type"), S("group"), L("position"),
                S("ret_addr"), L("ret")),
            "hire_leader_check" => new HireLeaderCheckEvent(seq, tid, t, S("player"), S("unit_type"), S("ret_addr"), L("ret")),
            "merc_check" => new MercCheckEvent(seq, tid, t, S("site"), S("stack"), S("unit_type"), S("ret_addr"), B("ret")),
            "sell_check" => new SellCheckEvent(seq, tid, t, S("site"), S("stack"), S("item"), NS("item_type"),
                S("ret_addr"), B("ret")),
            "grow_check" => new GrowCheckEvent(seq, tid, t, S("player"), S("fort"), L("turn"), S("ret_addr"), L("ret")),
            "noble_action" => new NobleActionEvent(seq, tid, t, S("player"), S("stack"), S("target"), L("action"),
                B("success"), L("value"), S("id"), B("ret"), L("seq_end")),
            "thief_cancel" => new ThiefCancelEvent(seq, tid, t, S("player"), S("stack"), B("ret"), L("seq_end")),
            "noble_choose" => new NobleChooseEvent(seq, tid, t, S("player"), S("stack"), S("target"), L("count"),
                GetArray(root, ev, seq, "actions").Select(a => a.GetInt64()).ToList(), B("ret")),
            "alliance_propose" => new AllianceProposeEvent(seq, tid, t, S("a"), S("b"), NB("human_a"), NB("human_b"),
                L("race_a"), L("race_b"), W("relation"), B("ret")),
            "alliance_change" => new AllianceChangeEvent(seq, tid, t, S("a"), S("b"), B("accept"), B("silent"),
                B("flag_e"), NB("human_a"), NB("human_b"), W("before"), W("after"), B("ret"), L("seq_end")),
            "trade_accept" => new TradeAcceptEvent(seq, tid, t, S("a"), S("b"), S("object"), S("id2"), L("gold"),
                B("accepted"), B("ret"), L("seq_end")),
            // Партия 2.
            "skill_offer" => new SkillOfferEvent(seq, tid, t, S("leader"), S("player"), S("lord"), L("level"),
                GetArray(root, ev, seq, "ids").Select(a => a.GetString() ?? "").ToList(), B("ret")),
            "skill_pick" => new SkillPickEvent(seq, tid, t, S("stack"), S("modif"), B("free"), B("apply"), S("leader"),
                L("upg_before"), L("upg_after"), L("rng_before"), L("rng_after"), S("ret_addr"), B("ret"), L("seq_end")),
            "upg_count" => new UpgCountEvent(seq, tid, t, S("stack"), L("count"), L("before"), L("after"), B("apply"),
                S("ret_addr"), B("ret"), L("seq_end")),
            "equip_check" => new EquipCheckEvent(seq, tid, t, S("item"), NS("item_type"), S("stack"), L("slot"),
                B("check_effect"), S("ret_addr"), L("ret")),
            "wand_check" => new WandCheckEvent(seq, tid, t, S("item"), S("target"), L("x"), L("y"), B("flag"),
                L("daily_uses"), S("ret_addr"), L("ret")),
            "potion_use" => new PotionUseEvent(seq, tid, t, S("player"), S("target"), S("group"), S("item"),
                NS("item_type"), B("destroy"), B("in_battle"), L("hp_before"), L("hp_after"), B("ret"), L("seq_end")),
            "item_consume" => new ItemConsumeEvent(seq, tid, t, S("group"), L("slot"), S("item_before"), NS("item_type"),
                S("item_after"), L("battle_round"), L("seq_end")),
            "talisman_charge" => new TalismanChargeEvent(seq, tid, t, S("item"), B("apply"), B("ret")),
            "xp_group" => new XpGroupEvent(seq, tid, t, S("group"), S("player"), B("attacker"), B("ai"),
                NB("weapon_master"),
                GetArray(root, ev, seq, "units").Select(u => new XpGroupUnit(GetString(u, ev, seq, "id"),
                    GetLong(u, ev, seq, "acc"), GetLong(u, ev, seq, "applied"))).ToList(), L("seq_end")),
            // 6.1.
            "attack_hit" => new AttackHitEvent(seq, tid, t, L("disp_class"), L("new_seq"), S("attacker"), S("attack"),
                S("target"), B("found"), L("hp_before"), L("hp_after"), L("st_before"), L("st_after"),
                Mods(root, ev, seq, "mods_before"), Mods(root, ev, seq, "mods_after"), L("seq_end")),
            // 6.2.
            "rng_seed" => new RngSeedEvent(seq, tid, t, L("n"), L("arg"), L("index_before"), L("index_after"),
                S("via"), S("cause"), S("ret_addr"), L("wide"),
                RngSeedEvent.ParseTable(S("table"))
                    ?? throw new JsonException($"{ev} seq={seq}: table — не 250 слов по 4 шестнадцатеричных знака")),
            "ap_restore" => new ApRestoreEvent(seq, tid, t, S("stack"), S("player"), L("amount"), L("move_before"),
                L("move_after"), L("max"), S("leader"), B("leader_found"), NB("leader_alive"), NS("zero"),
                S("cause"), S("ret_addr"), B("ret"), L("seq_end")),
            // 6.3.
            "unit_transfer" => new UnitTransferEvent(seq, tid, t, S("kind"), B("same_group"), S("from"), L("from_pos"),
                NS("to"), L("to_pos"), NS("unit"), NS("other"), NS("from_after"), NS("to_after"), S("cause"),
                S("ret_addr"), B("ret"), L("seq_end")),
            "swap_check" => new SwapCheckEvent(seq, tid, t, S("from"), L("from_pos"), S("to"), L("to_pos"), NS("unit"),
                NS("other"), S("ret_addr"), L("ret")),
            "dismiss_check" => new DismissCheckEvent(seq, tid, t, S("group"), L("position"), NS("unit"),
                S("ret_addr"), L("ret")),
            "equip_drop" => new EquipDropEvent(seq, tid, t, NS("item"), L("slot"), S("ret_addr"), L("ret")),
            "ui_refusal" => new UiRefusalEvent(seq, tid, t, S("text"), S("what"), S("ret_addr")),
            "wand_precheck" => new WandPrecheckEvent(seq, tid, t, S("item"), NS("item_type"), S("stack"),
                L("daily_uses"), S("ret_addr"), L("ret")),
            // 6.4.
            "ai_break_check" => new AiBreakCheckEvent(seq, tid, t, S("player"), S("other"), L("forts_player"),
                L("forts_other"), B("strong"), L("turn"), NL("ally_turn"), NB("nobreak"), NL("d"), L("t_base"),
                L("t_eff"), L("cap"), NL("chance"), NL("roll"), L("rng_before"), L("rng_after"), S("ret_addr"),
                B("ret"), L("seq_end")),
            "ai_offer_check" => new AiOfferCheckEvent(seq, tid, t, NS("turn_player"), NS("other"), L("rel"), L("turn"),
                NL("chance"), NL("roll"), L("rng_before"), L("rng_after"), S("ret_addr"), B("ret"), L("seq_end")),
            "ai_gift_check" => new AiGiftCheckEvent(seq, tid, t, NS("player"), NS("other"), L("rel"), L("threshold"),
                NL("roll"), NL("kind_roll"), NS("action"), L("rng_before"), L("rng_after"), S("ret_addr"), B("ret"),
                L("seq_end")),
            "ai_answer" => new AiAnswerEvent(seq, tid, t, S("kind"), NS("player"), NS("proposer"), NL("rel"),
                NL("gold"), NL("price"), NS("object"), NL("roll"), NB("accept"), L("rng_before"), L("rng_after"),
                S("ret_addr"), NL("ret"), L("seq_end")),
            "ai_strategic_step" => new AiStrategicStepEvent(seq, tid, t, NS("player"), NS("id"), L("type"), L("turn"),
                L("pass"), L("step"), L("rng_before"), L("rng_after"), S("ret_addr")),
            "ai_turn" => new AiTurnEvent(seq, tid, t, S("phase"), NS("player"), L("turn"), S("cause"), NL("arg1"),
                NL("arg2"), NL("arg3"), NL("pass_begins"), NL("passes"), NL("steps"), L("rng_before"),
                L("rng_after"), S("ret_addr"), NB("ret"), L("seq_end")),
            "ai_order" => new AiOrderEvent(seq, tid, t, S("stack"), L("order"), NS("order_name"), S("target"),
                B("flag"), B("apply"), L("order_before"), L("order_after"), L("ai_order_before"),
                L("ai_order_after"), S("via"), S("cause"), L("rng_before"), L("rng_after"), S("ret_addr"), B("ret"),
                L("seq_end")),
            "ai_budget" => new AiBudgetEvent(seq, tid, t, NS("player"), L("type"), B("flag"), Purse("budget"),
                Purse("bank"), Purse("spell_bank"), L("rng_before"), L("rng_after"), S("ret_addr")),
            "ai_chat" => new AiChatEvent(seq, tid, t, NS("from"), NS("to"), L("category"), NS("category_name"),
                NL("roll"), L("rng_before"), L("rng_after"), S("cause"), S("ret_addr"), B("ret")),
            _ => throw new ArgumentOutOfRangeException(nameof(ev), ev, "не событие схемы 6.0–6.4"),
        };
    }

    /// <summary>Модификаторы цели <c>attack_hit</c>: JSON <c>null</c> — цели нет в слотах боя.</summary>
    private static IReadOnlyList<string>? Mods(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.Null ? null : p.EnumerateArray().Select(m => m.GetString() ?? "").ToList()
            : throw Missing(ev, seq, name);

    /// <summary>Обязательное поле, у которого зонд законно пишет <c>null</c>.</summary>
    private static long? GetLongOrNull(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.Null ? null : p.GetInt64()
            : throw Missing(ev, seq, name);

    /// <summary><c>item_type</c>: JSON <c>null</c>; строка <c>"null"</c> — так писал зонд <c>856bf05</c>
    /// (дымовой прогон 14.09.2026), читается так же.</summary>
    private static string? GetStringOrNull(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.Null || p.GetString() == "null" ? null : p.GetString()
            : throw Missing(ev, seq, name);

    private static bool? GetBoolOrNull(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.Null ? null : p.GetBoolean()
            : throw Missing(ev, seq, name);
}
