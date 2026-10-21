using System.Text.Json;
using D2.Rules.Magic;

namespace D2.Data.Battle;

/// <summary>Заклинания карты, наложенные между сохранением и боем (журнал <c>spell_cast</c> / <c>spell_expire</c>).
///
/// <para><b>Как движок кладёт модификатор заклинания — ранг 1 по сохранениям.</b> Модификатор ложится НА ЮНИТОВ в
/// момент применения: каждому — строка <c>MidUnit.MODIF_ID</c> и запись <c>CMidSpellEffects</c>
/// (<c>UNIT_ID/ORIGIN_ID/MODIF_ID/CASTER_ID/TURN_STOP</c>, <see cref="SpellCast.TurnStop"/>), а общего «действующего
/// на карте» эффекта, который бой читал бы сам, нет. Замер — <c>20260919-human/passive-001</c>: «Зов предков»
/// (<c>G000SS0088</c>, <c>AREA</c> 999, <c>g000um0088</c> = броня +33) брошен в seq 15480877 на ходу 62; сохранение
/// 103 (seq 15522643) несёт <c>G000UM0088</c> ровно у 7 юнитов — всех членов трёх отрядов заклинателя
/// (<c>S073KC0001</c>, <c>0048</c>, <c>00ab</c>) из снимка 102 до заклинания, и ни у гарнизонов его столицы и
/// деревни, ни у отрядов <c>S073KC00b2</c>/<c>00b3</c>, нанятых после. Поэтому у их лидеров в боях 15504360 и
/// 15520354 журнал пишет броню 0, а у отряда <c>S073KC0001</c> в бою 15529111 — 33 (63 с навыком). Снимается запись
/// в начале следующего хода заклинателя (<c>spell_expire</c>, <c>0x43b9ef</c>; снимок хода 63 модификатора уже не
/// несёт).</para>
///
/// <para><b>Цели</b> — члены отрядов (<c>MidStack</c>) последнего снимка той же загрузки до заклинания:
/// <c>AREA</c> 1 — отряд на клетке заклинания; <c>AREA</c> 999 (квадрат, обрезанный краем, — вся карта,
/// <see cref="SpellCast.Tiles"/>) — отряды заклинателя. Правило сверяется с данными, а не принимается на веру:
/// снимок внутри срока действия, у юнита-цели которого модификатора нет, — отказ с причиной
/// (<see cref="WithoutSpells"/>).</para>
///
/// <para><b>Зелье, выпитое на карте</b> (<c>potion_use</c>, <c>in_battle = false</c>, категория
/// <see cref="ItemCategory.PotionBoost"/>), — тот же механизм: запись <c>CMidSpellEffects</c> с <c>ORIGIN_ID</c> = тип
/// предмета и тем же сроком (снимок 103: <c>G000IG0012/0011/0003/0015</c> у <c>S073UN01f8/01f1/01cc</c>, выпиты в
/// seq 15483131…15493737). Цель зелья журнал называет сам. Стойкое зелье (<see cref="ItemCategory.PotionPermanent"/>)
/// меняет свою цепочку навсегда и сюда не входит.</para>
///
/// <para>ponytail: области 3…9 с модификатором (в ванили 4 заклинания) не отслеживаются — чья сторона в них
/// попадает, корпус не показал; такой каст остаётся нарушением <c>SpellTests</c>, если доходит до боя раньше снимка.
/// Порядок в цепочке — заклинание в конце; в снимке движок ставит его перед модификаторами предметов, что для
/// сложения чисел безразлично, а для процентов поверх предмета не проверено.</para></summary>
/// <summary>Справочники наложений карты: заклинания (<c>Gspells</c>) и предметы (<c>GItem</c>, зелья).</summary>
public sealed record MapEffectTables(IReadOnlyDictionary<string, Spell> Spells, IReadOnlyDictionary<string, Item> Items)
{
    public static MapEffectTables Load(string globalsDir) =>
        new(D2.Data.Magic.SpellLoader.Load(globalsDir), D2.Data.Magic.ItemLoader.Load(globalsDir));
}

public sealed partial class SaveSnapshots
{
    /// <summary>Отряд снимка: владелец (пусто — нейтрал), клетка и члены.</summary>
    private sealed record Squad(string Owner, int X, int Y, IReadOnlyList<string> Units)
    {
        /// <summary>Id отряда (<c>MidStack.STACK_ID</c>) — для смен цепочки по отряду (<see cref="BracketChain"/>).</summary>
        public string Id { get; init; } = "";
    }

    /// <summary>Строка <c>spell_cast</c> (<see cref="Origin"/> — заклинание, цель — клетка) или <c>potion_use</c> на карте
    /// (<see cref="Origin"/> — тип предмета, <see cref="Target"/> — юнит), обе с <c>ret = true</c>, и номер загрузки карты.</summary>
    private sealed record CastLine(long Seq, string Player, string Origin, int X, int Y, int Session, string Target = "");

    /// <summary>Отслеженное заклинание: действует на <see cref="Units"/> в <c>(Seq, Expire)</c>.</summary>
    private sealed record TrackedCast(long Seq, long Expire, string ModifierId, IReadOnlySet<string> Units)
    {
        public bool Live(long seq) => Seq < seq && seq < Expire;
    }

    /// <summary><c>Gspells.AREA</c> заклинания на всю карту.</summary>
    private const int WholeMapArea = 999;

    /// <summary>Отслеживает ли загрузка карты наложение в <paramref name="castSeq"/>: известны его модификатор и цели.
    /// Вызывать на виде <see cref="At"/> той же загрузки.</summary>
    public bool Tracks(long castSeq, MapEffectTables tables) => TrackedCasts(tables).Any(c => c.Seq == castSeq);

    private List<TrackedCast> TrackedCasts(MapEffectTables tables)
    {
        var tracked = new List<TrackedCast>();
        foreach (var cast in _moves.Casts)
        {
            if (Effect(cast, tables) is not var (modifier, units)) continue;
            long expire = _moves.Expires.Where(e => e.Seq > cast.Seq && SameId(e.Player, cast.Player))
                                        .Select(e => e.Seq).DefaultIfEmpty(long.MaxValue).First();
            tracked.Add(new TrackedCast(cast.Seq, expire, modifier, units));
        }
        return tracked;
    }

    /// <summary>Модификатор и цели наложения; null — без модификатора или не отслеживается.</summary>
    private (string Modifier, IReadOnlySet<string> Units)? Effect(CastLine cast, MapEffectTables tables)
    {
        if (cast.Target.Length > 0)
            return tables.Items.TryGetValue(cast.Origin, out var item) && item.Category == ItemCategory.PotionBoost
                   && item.PotionModifierId.Length > 0
                ? (item.PotionModifierId, new HashSet<string>([cast.Target], StringComparer.OrdinalIgnoreCase))
                : null;
        if (!tables.Spells.TryGetValue(cast.Origin, out var spell) || spell.ModifierId.Length == 0) return null;
        return Targets(cast, spell) is { } units ? (spell.ModifierId, units) : null;
    }

    /// <summary>Юниты-цели по последнему снимку загрузки до заклинания; null — не отслеживается.</summary>
    private HashSet<string>? Targets(CastLine cast, Spell spell)
    {
        var before = _snapshots.Where(s => s.Seq >= 0 && s.Seq < cast.Seq && s.Session == cast.Session).MaxBy(s => s.Seq);
        if (before is null) return null;
        IEnumerable<Squad>? hit = spell.Area switch
        {
            1 => before.Squads.Where(q => q.X == cast.X && q.Y == cast.Y),
            >= WholeMapArea => before.Squads.Where(q => SameId(q.Owner, cast.Player)),
            _ => null,
        };
        return hit?.SelectMany(q => q.Units).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Цепочка снимка без модификаторов заклинаний, действовавших на момент снимка. Нет ожидаемого
    /// модификатора — null и причина. Снимок не привязан к журналу — цепочка как есть.</summary>
    private static SavedUnit? WithoutSpells(SavedUnit unit, long seq, IReadOnlyList<TrackedCast> casts, out string reason)
    {
        reason = "";
        if (seq < 0 || casts.Count == 0) return unit;
        var chain = unit.Modifiers.ToList();
        foreach (var c in casts.Where(c => c.Live(seq)))
        {
            int at = chain.FindIndex(m => SameId(m, c.ModifierId));
            if (at < 0)
            {
                reason = $"в сохранении seq {seq} у цели заклинания seq {c.Seq} нет модификатора {c.ModifierId} — " +
                         "правило целей не сошлось с сохранением";
                return null;
            }
            chain.RemoveAt(at);
        }
        return unit with { Modifiers = chain };
    }

    /// <summary>Строка <c>spell_cast</c>/<c>spell_expire</c> в <paramref name="log"/>; false — строка не про это.</summary>
    private static bool SpellLine(string line, IReadOnlyList<long> sessionStarts, MoveLog log)
    {
        bool cast = line.Contains("\"ev\":\"spell_cast\"", StringComparison.Ordinal);
        bool potion = !cast && line.Contains("\"ev\":\"potion_use\"", StringComparison.Ordinal);
        if (!cast && !potion && !line.Contains("\"ev\":\"spell_expire\"", StringComparison.Ordinal)) return false;
        using var doc = JsonDocument.Parse(line);
        var e = doc.RootElement;
        long seq = e.GetProperty("seq").GetInt64();
        string player = e.GetProperty("player").GetString() ?? "";
        if (!cast && !potion) { log.Expires.Add((seq, player)); return true; }
        if (!e.TryGetProperty("ret", out var ret) || !ret.GetBoolean()) return true;
        int session = -1;
        for (int i = 0; i < sessionStarts.Count && sessionStarts[i] <= seq; i++) session = i;
        if (cast)
            log.Casts.Add(new CastLine(seq, player, e.GetProperty("spell").GetString() ?? "",
                                       e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(), session));
        else if (!e.GetProperty("in_battle").GetBoolean() && e.GetProperty("item_type").GetString() is { } type)
            log.Casts.Add(new CastLine(seq, player, type, 0, 0, session, e.GetProperty("target").GetString() ?? ""));
        return true;
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
