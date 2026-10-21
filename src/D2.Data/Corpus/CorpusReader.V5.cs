using System.Text.Json;
using D2.Rules.Map;

namespace D2.Data.Corpus;

/// <summary>Разбор событий схем 5–5.3, которых не разбирала задача 1 Ф7 (записи — в
/// <c>CorpusEventsV5.cs</c>). Обязательные поля — как во всём читателе: пропавшее поле — дрейф схемы,
/// <see cref="JsonException"/>, а не ноль.</summary>
public static partial class CorpusReader
{
    /// <summary><c>thief_roll</c>: форма схемы 5.1 опознаётся по полю <c>action</c>; в схеме 5 цель лежит
    /// в <c>stack</c>, а кубиков и порога нет (corpus-schema.md «Бросок вора»).</summary>
    private static ThiefRollEvent ParseThiefRoll(JsonElement root, string ev, long seq, long tid)
    {
        long L(string name) => GetLong(root, ev, seq, name);
        bool v51 = root.TryGetProperty("action", out _);
        return new ThiefRollEvent(seq, tid, L("t"), v51 ? L("action") : null,
            GetString(root, ev, seq, v51 ? "target" : "stack"), v51 ? L("dice1") : null, v51 ? L("dice2") : null,
            v51 ? L("chance") : null, L("rng_before"), L("rng_after"), GetBool(root, ev, seq, "ret"));
    }

    private static RelationEvent ParseRelation(JsonElement root, string ev, long seq, long tid)
    {
        long L(string name) => GetLong(root, ev, seq, name);
        string S(string name) => GetString(root, ev, seq, name);
        var changed = new List<RelationChange>();
        foreach (var c in GetArray(root, ev, seq, "changed"))
            changed.Add(new RelationChange(GetLong(c, ev, seq, "race1"), GetLong(c, ev, seq, "race2"),
                GetLong(c, ev, seq, "before"), GetLong(c, ev, seq, "after")));
        return new RelationEvent(seq, tid, L("t"), S("a"), S("b"), L("race_a"), L("race_b"), L("value"),
            GetBool(root, ev, seq, "absolute"), S("ret_addr"), S("cause"),
            Word(GetObject(root, ev, seq, "before"), ev, seq), Word(GetObject(root, ev, seq, "after"), ev, seq),
            changed, GetBool(root, ev, seq, "ret"));
    }

    /// <summary>Пара рас <c>{cur, prev, ally, eternal_war, raw}</c>; JSON <c>null</c> — пары нет или
    /// дипломатию не прочитать (поле само обязательно).</summary>
    private static RelationWord? Word(JsonElement w, string ev, long seq) =>
        w.ValueKind == JsonValueKind.Null
            ? null
            : new RelationWord(GetLong(w, ev, seq, "cur"), GetLong(w, ev, seq, "prev"), GetBool(w, ev, seq, "ally"),
                GetBool(w, ev, seq, "eternal_war"), GetLong(w, ev, seq, "raw"));

    /// <summary><c>item_equip</c> несёт <c>item</c>, <c>item_unequip</c> — нет (probeecon.cpp).</summary>
    private static ItemEquipEvent ParseItemEquip(JsonElement root, string ev, long seq, long tid)
    {
        bool equip = ev == "item_equip";
        return new ItemEquipEvent(seq, tid, GetLong(root, ev, seq, "t"), equip, GetString(root, ev, seq, "stack"),
            equip ? GetString(root, ev, seq, "item") : null, GetLong(root, ev, seq, "slot"),
            GetLong(root, ev, seq, "apply") != 0, GetBool(root, ev, seq, "ret"));
    }

    /// <summary>Шесть причин трат схемы 5.3: общие поля <c>player</c>, <c>before</c>, <c>after</c>,
    /// <c>seq_end</c>, <c>ret</c> (<c>emitSpend</c>, probespend.cpp:150) плюс поля вызова.</summary>
    private static SpendEvent ParseSpend(JsonElement root, string ev, long seq, long tid)
    {
        long L(string name) => GetLong(root, ev, seq, name);
        string S(string name) => GetString(root, ev, seq, name);
        bool B(string name) => GetBool(root, ev, seq, name);
        Bank Purse(string name) => ParseBank(GetObject(root, ev, seq, name), ev, seq);
        long t = L("t"), end = L("seq_end");
        string player = S("player");
        Bank before = Purse("before"), after = Purse("after");
        bool ret = B("ret");
        return ev switch
        {
            "hire_unit" => new HireUnitEvent(seq, tid, t, player, S("unit_type"), S("group"), L("position"),
                before, after, end, ret),
            "hire_leader" => new HireLeaderEvent(seq, tid, t, player, S("fort"), S("unit_type"), before, after, end, ret),
            "rod_drop" => new RodDropEvent(seq, tid, t, player, S("stack"), L("x"), L("y"), before, after, end, ret),
            "spell_cast" => new SpellCastEvent(seq, tid, t, player, S("spell"), L("x"), L("y"), B("use_spell_bank"),
                B("free"), Purse("spell_bank_before"), Purse("spell_bank_after"), before, after, end, ret),
            "spell_research" => new SpellResearchEvent(seq, tid, t, player, S("spell"), B("free"), before, after, end, ret),
            "wand_cast" => new WandCastEvent(seq, tid, t, player, S("target"), S("owner"), S("item"), L("x"), L("y"),
                before, after, end, ret),
            _ => throw new ArgumentOutOfRangeException(nameof(ev), ev, "не событие траты казны"),
        };
    }
}
