using System.Text.Json;
using D2.Rules.Map;

namespace D2.Data.Corpus;

/// <summary>
/// Reads probe journals (<c>tools/d2probe</c>, one JSON object per line — see
/// <c>docs/corpus-schema.md</c>). Dispatches by the "ev" field with <see cref="JsonDocument"/>
/// rather than polymorphic deserialization, since the field set differs per event and an unknown
/// "ev" must still parse (into <see cref="UnknownEvent"/>), never throw.
/// </summary>
public static partial class CorpusReader
{
    /// <summary>Events in file order. File order is NOT sorted by <c>seq</c>: a nested call
    /// (e.g. "dmg" calling "dmg_buffs"/"armor"/"power") writes its line before the outer one,
    /// whose <c>seq</c> was taken first. Use <see cref="Battles"/> to get call order instead.
    /// Throws <see cref="JsonException"/> if a line is malformed or a field the probe always
    /// writes for that "ev" is missing — a schema drift is a bug to surface, not paper over
    /// with a 0 that looks like real data.</summary>
    public static IEnumerable<CorpusEvent> Read(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            yield return ParseLine(line);
        }
    }

    /// <summary>События одного типа <paramref name="ev"/> в порядке файла. Строки отбираются по подстроке
    /// <c>"ev":"…"</c> до разбора JSON — для редких событий (<c>rng_seed</c>) это дешевле полного
    /// <see cref="Read(string)"/> по многогигабайтному журналу.</summary>
    public static IEnumerable<CorpusEvent> Read(string path, string ev)
    {
        string marker = $"\"ev\":\"{ev}\"";
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || !line.Contains(marker, StringComparison.Ordinal)) continue;
            yield return ParseLine(line);
        }
    }

    /// <summary>События из набора <paramref name="evs"/> в порядке файла — то же отборочное правило, что у
    /// <see cref="Read(string, string)"/>, но за один проход по журналу. Имя события берётся из строки один раз (первое
    /// <c>"ev":"…"</c> — поле верхнего уровня, оно идёт сразу за <c>seq</c>/<c>tid</c>/<c>t</c>) и ищется во множестве: прежний поиск
    /// каждой метки по всей строке стоил харнессу с 18 видами событий 18 проходов по 80-мегабайтному журналу (28.09).</summary>
    public static IEnumerable<CorpusEvent> Read(string path, IReadOnlyCollection<string> evs)
    {
        ArgumentNullException.ThrowIfNull(evs);
        var wanted = new HashSet<string>(evs, StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || EvOf(line) is not { } ev || !wanted.Contains(ev)) continue;
            yield return ParseLine(line);
        }
    }

    private const string EvKey = "\"ev\":\"";

    /// <summary>Имя события строки журнала без разбора JSON; нет поля — null.</summary>
    internal static string? EvOf(string line)
    {
        int start = line.IndexOf(EvKey, StringComparison.Ordinal);
        if (start < 0) return null;
        start += EvKey.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? null : line[start..end];
    }

    /// <summary>Groups a journal's events into battles by pairing <c>battle_start</c> with the
    /// next <c>battle_end</c> of the same battle, in <c>seq</c> (call) order — not file order.
    /// A battle whose <c>battle_end</c> never arrives (truncated journal, or the last battle of a
    /// still-running probe) is returned with <c>End == null</c>. Events before the first
    /// <c>battle_start</c> belong to no battle and are dropped.
    /// Without the probe's forced autobattle the engine makes a fresh <c>BattleMsgData</c> copy
    /// every round and the probe (dedup by pointer) writes <c>battle_start</c> for each: a
    /// <c>battle_start</c> that arrives while a battle is open (no <c>battle_end</c> yet) with the
    /// same set of unit ids is that battle's next copy, not a new battle — it stays in the event
    /// stream and does not split it (docs/corpus-schema.md, «Журналы без принудительного автобоя»;
    /// corpus/20260906-manual: seq 52975 / 53047 / 53083, one <c>battle_end</c> 53088).</summary>
    public static IReadOnlyList<Battle> Battles(string path) => BattlesOf(Read(path));

    /// <summary><see cref="Battles(string)"/> по уже прочитанным событиям журнала в порядке файла (общий кэш тестов
    /// <see cref="JournalCache"/> строит бои из своих событий тем же правилом).</summary>
    internal static IReadOnlyList<Battle> BattlesOf(IEnumerable<CorpusEvent> events)
    {
        var battles = new List<Battle>();
        BattleStartEvent? start = null;
        List<CorpusEvent>? current = null;

        void Flush(BattleEndEvent? end)
        {
            if (start is null) return;
            battles.Add(new Battle(start, end, current!));
            start = null;
            current = null;
        }

        foreach (var e in events.OrderBy(e => e.Seq))
        {
            if (e is BattleStartEvent bs)
            {
                if (start is not null && SameBattle(start, bs)) { current!.Add(bs); continue; } // next BattleMsgData copy
                Flush(null); // previous battle never saw its battle_end: record it truncated.
                start = bs;
                current = [bs];
                continue;
            }
            if (start is null) continue; // outside any battle_start..battle_end span

            current!.Add(e);
            if (e is BattleEndEvent be) Flush(be);
        }
        Flush(null); // trailing battle truncated by end of file

        return battles;
    }

    /// <summary>Копия открытого боя: те же юниты, либо те же группы и раунд копии не нулевой — копия посреди боя, в которой
    /// появился призванный (<c>corpus/20260909-arena1/map-001</c>: бой 121432…125??? резался на шесть кусков без battle_end у
    /// пяти — в копиях раундов 1–5 добавлялся призванный <c>S143UN001a</c>…). Новый бой начинается с раунда 0.</summary>
    private static bool SameBattle(BattleStartEvent open, BattleStartEvent b) =>
        SameUnits(open, b)
        || b.Round > 0 && string.Equals(open.AttackerGroup, b.AttackerGroup, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(open.DefenderGroup, b.DefenderGroup, StringComparison.OrdinalIgnoreCase)
                       && Nested(open, b);

    /// <summary>Набор юнитов одной копии содержит набор другой (в копии добавился призванный или выпал павший призванный).</summary>
    private static bool Nested(BattleStartEvent a, BattleStartEvent b)
    {
        var x = a.Units.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var y = b.Units.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return x.IsSubsetOf(y) || y.IsSubsetOf(x);
    }

    private static bool SameUnits(BattleStartEvent a, BattleStartEvent b) =>
        a.Units.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
         .SetEquals(b.Units.Select(u => u.Id));

    /// <summary>Строка JSON; у строк портретов боя зонда 7.6 (<c>bat_text</c>, <c>bat_text_end</c>, <c>bat_flash</c> — <c>probeflash.cpp</c>
    /// пишет <c>"unit":{}</c> без кавычек) id юнита без кавычек берётся в кавычки: это тот же id, а не догадка. Журналы 20.10 с боями
    /// человека (<c>20261020-replay-human</c> — 116 строк, <c>20261020-replay10</c> — 300) иначе не читаются целиком (реестр Зс210).</summary>
    internal static JsonDocument ParseJson(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) when (line.Contains("\"ev\":\"bat_", StringComparison.Ordinal) && BareUnitId().IsMatch(line))
        {
            return JsonDocument.Parse(BareUnitId().Replace(line, "\"unit\":\"$1\""));
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"unit\":([A-Za-z][0-9A-Za-z]{9})(?=[,}])")]
    private static partial System.Text.RegularExpressions.Regex BareUnitId();

    internal static CorpusEvent ParseLine(string line)
    {
        using var doc = ParseJson(line);
        var root = doc.RootElement;
        var seq = root.GetProperty("seq").GetInt64();
        var tid = root.GetProperty("tid").GetInt64();
        var ev = root.GetProperty("ev").GetString() ?? "";

        long L(string name) => GetLong(root, ev, seq, name);
        string S(string name) => GetString(root, ev, seq, name);
        bool B(string name) => GetBool(root, ev, seq, name);

        return ev switch
        {
            "start" => new StartEvent(seq, tid, L("exe_size"), S("version"), S("probe"), B("autobattle"))
            {
                Schema = root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String ? schema.GetString() : null,
            },
            "rng" => new RngEvent(seq, tid, L("n"), L("max"), L("r"), false, GetNullableLong(root, "rng_index")),
            "rng_std" => new RngEvent(seq, tid, L("n"), L("max"), L("r"), true),
            "miss" => new MissEvent(seq, tid, L("power"), B("miss"), L("rng_before"), L("rng_after")),
            "dmg" => new DamageEvent(seq, tid, S("attacker"), S("target"), B("crit"), L("ret"), L("dmg"),
                L("crit_dmg"), L("rng_before"), L("rng_after")),
            "dmg_max" => new DamageMaxEvent(seq, tid, S("unit"), L("ret")),
            "dmg_buffs" => new DamageBuffsEvent(seq, tid, S("unit"), L("dmg_max"), B("random"), B("easy"),
                L("ret")),
            "armor" => new ArmorEvent(seq, tid, S("unit"), L("ret")),
            "power" => new PowerEvent(seq, tid, S("unit"), L("ret")),
            "income" => ParseIncome(root, ev, seq, tid),
            "round" => new RoundEvent(seq, tid, L("round"), ParseOrder(root, ev, seq), ParseUnits(root, ev, seq),
                GetNullableLong(root, "rng_index")),
            "turn_before" => new TurnEvent(seq, tid, false, S("unit"), null, L("round"), ParseOrder(root, ev, seq),
                ParseUnits(root, ev, seq)),
            "turn_after" => new TurnEvent(seq, tid, true, S("unit"), GetNullableString(root, "next"), L("round"),
                ParseOrder(root, ev, seq), ParseUnits(root, ev, seq)),
            "battle_start" => new BattleStartEvent(seq, tid, S("attacker_group"), S("defender_group"),
                S("attacker_player"), S("defender_player"), L("round"), ParseUnits(root, ev, seq),
                GetNullableLong(root, "rng_index")),
            "battle_units" => new BattleUnitsEvent(seq, tid, ParseOrder(root, ev, seq), ParseUnitIdentities(root, ev, seq)),
            "battle_turn" => new BattleTurnEvent(seq, tid, L("round")),
            "battle_end" => new BattleEndEvent(seq, tid, S("winner"), L("round"), ParseUnits(root, ev, seq),
                GetNullableLong(root, "rng_index"), GetNullableLong(root, "t")),
            "autobattle_forced" => new AutobattleForcedEvent(seq, tid),
            // Adventure map, schema v3.
            "move_cost" => new MoveCostEvent(seq, tid, L("x"), L("y"), S("stack"), B("leader_alive"),
                B("plains"), B("forest"), B("water"), B("water_only"), B("forbid_water"), L("ret")),
            "can_move" => new CanMoveEvent(seq, tid, L("x"), L("y"), S("stack"), B("ret")),
            "path" => new PathEvent(seq, tid, S("stack"), L("len"), L("last_x"), L("last_y"), L("end_x"),
                L("end_y")),
            "stack_move" => new StackMoveEvent(seq, tid, S("stack"), L("x"), L("y"), B("check_area"),
                L("move_left"), B("ret")),
            "xp" => new XpEvent(seq, tid, S("player"), S("unit"), L("amount"), B("ret"), GetNullableLong(root, "t"),
                GetNullableString(root, "ret_addr"), GetNullableLong(root, "xp_before"),
                GetNullableLong(root, "xp_after"), GetNullableLong(root, "hp"), GetNullableLong(root, "level"),
                GetNullableLong(root, "xp_next"), GetNullableString(root, "impl"), GetOptionalBool(root, "leader"),
                GetNullableLong(root, "max_leader")),
            "build_status" => new BuildStatusEvent(seq, tid, S("player"), S("building"), B("ignore_cost"),
                L("ret")),
            "hire_list" => new HireListEvent(seq, tid, S("player"), S("a3"), L("count"), B("ret")),
            "site" => new SiteEvent(seq, tid, S("site"), L("category")),
            "terrain_day" => new TerrainDayEvent(seq, tid, S("player"), L("turn"), L("race"), L("terrain"),
                L("size"), L("owned"), TerrainDayEvent.ParseCells(S("cells"))),
            // Бой, схема v4.
            "immune" => new ImmuneEvent(seq, tid, S("unit"), S("attack"), L("class"), L("source"),
                B("always_only"), B("ret")),
            "attack_new" => new AttackNewEvent(seq, tid, S("attacker"), S("impl"), L("number"), S("attack"),
                L("class"), L("source"), L("disp_class"), S("target_group")),
            "attack_get" => new AttackGetEvent(seq, tid, S("impl"), L("number"), B("alt"), S("attack"),
                L("class"), L("source")),
            "counts" => new CountsEvent(seq, tid, LongMap(GetObject(root, ev, seq, "calls")),
                GetNullableLong(root, "keys_dropped") ?? 0,
                root.TryGetProperty("keys_dropped_by", out var dropped) ? LongMap(dropped) : null,
                GetNullableLong(root, "keys_resets") ?? 0),
            // Казна и объекты, схема v5.
            "bank" => new BankEvent(seq, tid, L("t"), S("player"), L("mode"), ParseBank(GetObject(root, ev, seq, "before"), ev, seq),
                ParseBank(GetObject(root, ev, seq, "after"), ev, seq), B("ret")),
            "site_buy_item" => new SiteBuyItemEvent(seq, tid, L("t"), S("site"), S("stack"), S("item_type"), L("slot"), B("ret")),
            "site_sell_item" => new SiteSellItemEvent(seq, tid, L("t"), S("site"), S("stack"), S("item"), B("ret")),
            "site_buy_spell" => new SiteBuySpellEvent(seq, tid, L("t"), S("site"), S("player"), S("spell"), B("ret")),
            // Тип из генератора юнитов (G000UG…, наёмник выше базового уровня) зонд пишет как id3.
            "site_buy_unit" => new SiteBuyUnitEvent(seq, tid, L("t"), S("site"), S("stack"),
                                                    GetNullableString(root, "unit_type") ?? S("id3"), B("ret")),
            "site_train" => new SiteTrainEvent(seq, tid, L("t"), S("site"), S("unit"), B("ret")),
            "heal" => new HealEvent(seq, tid, L("t"), S("unit"), S("player"), B("ret")),
            "revive" => new ReviveEvent(seq, tid, L("t"), S("unit"), S("player"), B("ret"),
                GetNullableLong(root, "hp_before"), GetNullableLong(root, "hp_after"),
                GetNullableLong(root, "xp_before"), GetNullableLong(root, "xp_after")),
            "city_grow" => new CityGrowEvent(seq, tid, L("t"), S("player"), S("fort"), B("ret")),
            "build" => new BuildEvent(seq, tid, L("t"), S("player"), S("building"), S("id2"), L("flag"), B("ret")),
            "fort_day" => new FortDayEvent(seq, tid, L("t"), S("player"), L("turn"), ParseForts(root, ev, seq)),
            // Схемы 5–5.3 сверх задачи 1 — разбор в CorpusReader.V5.cs.
            "thief_roll" => ParseThiefRoll(root, ev, seq, tid),
            "relation" => ParseRelation(root, ev, seq, tid),
            "item_equip" or "item_unequip" => ParseItemEquip(root, ev, seq, tid),
            "input" => new InputEvent(seq, tid, L("t"), S("kind"), L("msg"), L("vk"), L("button"), L("x"), L("y"),
                L("sx"), L("sy")),
            "hire_unit" or "hire_leader" or "rod_drop" or "spell_cast" or "spell_research" or "wand_cast" =>
                ParseSpend(root, ev, seq, tid),
            // Схема 6.0, партия 1 — разбор в CorpusReader.V6.cs.
            _ when V6Events.Contains(ev) => ParseV6(root, ev, seq, tid),
            _ when V65Events.Contains(ev) => ParseV65(root, ev, seq, tid),
            _ when V7Events.Contains(ev) => ParseV7(root, ev, seq, tid),
            _ when V72Events.Contains(ev) => ParseV72(root, ev, seq, tid),
            // Схема 7.3: звук, музыка, ролики, анимации — общая запись (поля у оригинала и порта одни).
            _ when D2.Data.Diagnostics.AvJournal.Events.Contains(ev) =>
                new AvProbeEvent(seq, tid, D2.Data.Diagnostics.AvJournal.ParseLine(line, ev)),
            "coverage" => new CoverageEvent(seq, tid, L("t"), L("armed"), L("listed"), L("skipped"), S("file")),
            _ => new UnknownEvent(seq, tid, ev, line),
        };
    }

    private static IncomeEvent ParseIncome(JsonElement root, string ev, long seq, long tid)
    {
        var mana = GetObject(root, ev, seq, "mana");
        // "turn"/"bank" arrived with schema v3: read as optional so the 65 battle journals of Ф4
        // (v2.1, no such fields) keep parsing — there they stay null, not 0.
        var bank = root.TryGetProperty("bank", out var b) ? ParseBank(b, ev, seq) : null;
        return new IncomeEvent(seq, tid, GetString(root, ev, seq, "player"), GetLong(root, ev, seq, "gold"),
            GetLong(mana, ev, seq, "infernal"), GetLong(mana, ev, seq, "life"), GetLong(mana, ev, seq, "death"),
            GetLong(mana, ev, seq, "runic"), GetLong(mana, ev, seq, "grove"),
            GetNullableLong(root, "turn"), bank, GetNullableLong(root, "t"));
    }

    /// <summary>Казна-объект <c>{gold, infernal, life, death, runic, grove}</c> — один вид у <c>income</c>
    /// и у всех трёх казн <c>bank</c>.</summary>
    private static Bank ParseBank(JsonElement b, string ev, long seq) =>
        new(GetLong(b, ev, seq, "gold"), GetLong(b, ev, seq, "infernal"), GetLong(b, ev, seq, "life"),
            GetLong(b, ev, seq, "death"), GetLong(b, ev, seq, "runic"), GetLong(b, ev, seq, "grove"));

    private static List<FortState> ParseForts(JsonElement root, string ev, long seq)
    {
        var forts = new List<FortState>();
        foreach (var f in GetArray(root, ev, seq, "forts"))
            forts.Add(new FortState(GetString(f, ev, seq, "id"), GetString(f, ev, seq, "owner"),
                GetLong(f, ev, seq, "size"), GetString(f, ev, seq, "subrace"), GetString(f, ev, seq, "stack"),
                GetLong(f, ev, seq, "tier"), GetLong(f, ev, seq, "capacity"), GetLong(f, ev, seq, "growth_t"),
                GetLong(f, ev, seq, "riot_t"), GetLong(f, ev, seq, "regen"), GetNullableString(f, "protect_b")));
        return forts;
    }

    private static List<UnitState> ParseUnits(JsonElement root, string ev, long seq)
    {
        var units = new List<UnitState>();
        foreach (var u in GetArray(root, ev, seq, "units"))
        {
            units.Add(new UnitState(GetString(u, ev, seq, "id"), GetLong(u, ev, seq, "hp"),
                GetLong(u, ev, seq, "xp"), GetLong(u, ev, seq, "st"), GetLong(u, ev, seq, "st2"),
                GetLong(u, ev, seq, "flags"), GetLong(u, ev, seq, "shattered"), GetLong(u, ev, seq, "fort"),
                GetNullableString(u, "type"), GetNullableLong(u, "level"), GetNullableLong(u, "hp_max")));
        }
        return units;
    }

    private static List<UnitIdentity> ParseUnitIdentities(JsonElement root, string ev, long seq)
    {
        var units = new List<UnitIdentity>();
        foreach (var u in GetArray(root, ev, seq, "units"))
        {
            units.Add(new UnitIdentity(GetString(u, ev, seq, "id"), GetNullableString(u, "type"),
                GetNullableLong(u, "level"), GetNullableLong(u, "hp_max")));
        }
        return units;
    }

    private static List<OrderEntry> ParseOrder(JsonElement root, string ev, long seq)
    {
        var order = new List<OrderEntry>();
        foreach (var o in GetArray(root, ev, seq, "order"))
            order.Add(new OrderEntry(GetString(o, ev, seq, "id"), GetLong(o, ev, seq, "attacks")));
        return order;
    }

    // "next" (turn_after) and "type"/"level"/"hp_max" (battle_start units, non-soldier) are the
    // only fields the probe genuinely omits sometimes — everything else below is required: a
    // missing field means the probe's JSON shape drifted from this reader, not that the value is
    // 0/"" for real. See docs/corpus-schema.md.
    private static JsonException Missing(string ev, long seq, string name) =>
        new($"{ev}: missing field '{name}' (seq {seq})");

    private static long GetLong(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetInt64() : throw Missing(ev, seq, name);

    private static string GetString(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetString() ?? "" : throw Missing(ev, seq, name);

    private static bool GetBool(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetBoolean() : throw Missing(ev, seq, name);

    private static JsonElement GetObject(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p) ? p : throw Missing(ev, seq, name);

    private static JsonElement.ArrayEnumerator GetArray(JsonElement e, string ev, long seq, string name) =>
        e.TryGetProperty(name, out var p) ? p.EnumerateArray() : throw Missing(ev, seq, name);

    private static Dictionary<string, long> LongMap(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);

    private static long? GetNullableLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetInt64() : null;

    private static string? GetNullableString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetString() : null;

    /// <summary>Поле, которого нет в старых журналах и которое зонд пишет JSON <c>null</c>, когда не прочитал.</summary>
    private static bool? GetOptionalBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetBoolean() : null;
}
