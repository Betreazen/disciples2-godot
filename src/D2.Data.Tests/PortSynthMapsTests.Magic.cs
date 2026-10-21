using D2.Data.Play;

namespace D2.Data.Tests;

/// <summary>Карта 7 <c>port-synth-7-magic</c> (<c>tools/synth-maps/maps_magic.py</c>): у каждого класса заклинаний на карте — заклинание из
/// книги, свиток и посох со своей целью на полигоне; заклинания на всю карту — последними (невидимость всей карты — самым последним: по
/// своему невидимому отряду заклинания не бросаются).</summary>
public partial class PortSynthMapsTests
{
    private const string MagicMap = "port-synth-7-magic";

    [SkippableFact]
    public void Magic_EveryClass_FromBookScrollAndWand_HitsItsTarget()
    {
        Skip.IfNot(MapIds().Contains(MagicMap));
        var facts = Facts(MagicMap);
        var session = Begin(MagicMap, 1);
        var seen = new Recorder();
        session.Watch(seen);
        string human = session.ActiveHuman;
        string scrolls = Str(Zone(facts, "MG"), "stack"), wands = Str(Zone(facts, "MW"), "stack");
        var casts = Zone(facts, "casts").GetProperty("casts").EnumerateArray().ToList();
        // Ход восстанавливает только неполный запас: по памятке отряды RS шагают на клетку вниз (запас дня восстановлен началом хода).
        foreach (var rs in Zone(facts, "casts").GetProperty("rested").EnumerateArray())
        {
            var stack = session.Map.Stacks[rs.GetString()!];
            session.MoveStack(stack.Id, stack.X, stack.Y + 1, 0, 2);
        }
        // на всю карту — после всех точечных: развеивание всей карты сняло бы туман с целей развеивания, невидимость — с целей своих
        foreach (var cast in casts.OrderBy(c => c.GetProperty("source").GetString()!.Contains("вся карта", StringComparison.Ordinal) ? 1 : 0))
        {
            var at = cast.GetProperty("at");
            int x = at[0].GetInt32(), y = at[1].GetInt32();
            string spell = Str(cast, "spell");
            string caster = Str(cast, "source") == "посох" ? wands : scrolls;
            string? refused = cast.GetProperty("item").ValueKind == System.Text.Json.JsonValueKind.Null
                ? session.CastSpell(human, spell, x, y, useSpellBank: false)
                : session.CastItem(caster, session.Map.Stacks[caster].Items.First(i => Same(session.Map.ItemTypes[i], Str(cast, "item"))), x, y);
            Out.WriteLine($"{Str(cast, "key")} {Str(cast, "source")} {spell} ({x},{y}): {refused ?? "да"}");
            // «Адские муки» (броня −50) по Гному с бронёй 0 — без эффекта, как у движка (20261016e-av seq 13976861, X005TA0671).
            if (spell == PainSpell) Assert.Equal("брони нет", refused);
            else Assert.True(refused is null, $"{Str(cast, "key")} {spell}: {refused}");
        }
        int spellSights = seen.Sights.Count(s => s.Kind == TurnSightKind.Spell);
        Assert.True(casts.Count - 1 == spellSights, $"литий {casts.Count}, строк Spell {spellSights} — у каждого литья одна");
        Assert.All(Zone(facts, "casts").GetProperty("rods").EnumerateArray(), r => Assert.False(session.Map.Rods.ContainsKey(r.GetString()!)));
        Assert.All(Zone(facts, "casts").GetProperty("hidden").EnumerateArray(), h => Assert.True(session.Invisible(h.GetString()!)));
    }

    /// <summary>«Адские муки» — ослабление брони (<c>G000SS0045</c>, модификатор <c>G000UM0045</c>: броня −50).</summary>
    private const string PainSpell = "G000SS0045";

    /// <summary>Ослабление брони «без эффекта» (<c>0x5dac42</c> → <c>0x5db12a</c>: у модификатора элемент брони, флаг <c>0x400</c>, и сумма брони
    /// юнитов отряда <c>0x5db216</c> — 0 → отказ 5, <c>X005TA0671</c>): по Гному у (24, 12) с бронёй 0 — отказ, маны не берёт; по отряду с
    /// бронёй — можно. Ослабление урона («Слабость», свиток) и паралич (посох) брони не смотрят — по соседним Гномам проходят
    /// (<see cref="Magic_EveryClass_FromBookScrollAndWand_HitsItsTarget"/>).</summary>
    [SkippableFact]
    public void Magic_ArmorLower_OnArmorlessStack_HasNoEffect()
    {
        Skip.IfNot(MapIds().Contains(MagicMap));
        var session = Begin(MagicMap, 1);
        string human = session.ActiveHuman;
        var bank = session.Map.Players[human].Bank;
        Assert.Equal("брони нет", session.SpellRefusal(human, PainSpell, 24, 12, useSpellBank: false));
        Assert.Equal("брони нет", session.CastSpell(human, PainSpell, 24, 12, useSpellBank: false));
        Assert.Equal(bank, session.Map.Players[human].Bank);
        // Тот же Гном с бронёй (модификатор «+20 брони» G000UM0012 на лидере): ослабление брони можно.
        var gnome = session.Map.Stacks.Values.Single(st => st.X == 24 && st.Y == 12);
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[gnome.LeaderId] = units[gnome.LeaderId] with { Modifiers = [.. units[gnome.LeaderId].Modifiers, "G000UM0012"] };
        var armored = new GameSession(session.State with
        {
            Looks = GameSession.Retold(session.Looks, GameSession.With(session.Map, units: units), session.Looks.Stacks),
        }, session.Art, session.Book);
        Assert.Null(armored.SpellRefusal(human, PainSpell, 24, 12, useSpellBank: false));
    }

    /// <summary>Паралич (<c>G000SS0053</c>) по Гному у (28, 12): после модификатора запас хода не выше нового дневного (<c>CEffectModifier</c>
    /// <c>0x439886</c>–<c>0x4398af</c>) — 20 → 0, как у движка (<c>20261016e-av</c>, карта 7, <c>ap_restore modifier_clamp</c> seq 14001016).</summary>
    [SkippableFact]
    public void Magic_Paralysis_ClampsMoveLeft()
    {
        Skip.IfNot(MapIds().Contains(MagicMap));
        var facts = Facts(MagicMap);
        var session = Begin(MagicMap, 1);
        var gnome = session.Map.Stacks.Values.Single(st => st.X == 28 && st.Y == 12);
        Assert.True(gnome.Move > 0);
        string wands = Str(Zone(facts, "MW"), "stack");
        var cast = Zone(facts, "casts").GetProperty("casts").EnumerateArray().Single(c => Str(c, "spell") == "G000SS0053");
        string staff = session.Map.Stacks[wands].Items.First(i => Same(session.Map.ItemTypes[i], Str(cast, "item")));
        Assert.Null(session.CastItem(wands, staff, 28, 12));
        Assert.Equal(0, session.Map.Stacks[gnome.Id].Move);
        Assert.Equal(0, session.DayAllowance(session.Map.Stacks[gnome.Id]));
    }
}
