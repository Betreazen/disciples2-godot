using D2.Data.Corpus;
using D2.Rules.Map;

/// <summary>Задача 2 Ф7: разбор событий схем 5–5.3 на строках того вида, что печатает зонд. Строки
/// вора, дипломатии, предметов, ввода, наёмника — дословно из корпуса 11.09; шесть причин трат схемы
/// 5.3 — по формату <c>probespend.cpp</c> (<c>emitSpend</c>), живых журналов с ними ещё нет.</summary>
public class CorpusReaderV5Tests
{
    private const string Before = """{"gold":500,"infernal":0,"life":100,"death":0,"runic":0,"grove":0}""";
    private const string After = """{"gold":350,"infernal":0,"life":60,"death":0,"runic":0,"grove":0}""";

    private static CorpusEvent One(string line)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, line + "\n");
        try { return CorpusReader.Read(path).Single(); }
        finally { File.Delete(path); }
    }

    [Fact] public void ThiefRoll_Schema5_TargetInStack_NoDice()
    {
        var e = Assert.IsType<ThiefRollEvent>(One("""{"seq":278093,"tid":22428,"t":640469,"ev":"thief_roll","id1":"G001CRedb4","stack":"S143KC0001","rng_before":152138,"rng_after":152140,"ret":true}"""));
        Assert.Null(e.Action);
        Assert.Equal("S143KC0001", e.Target);
        Assert.Null(e.Dice1);
        Assert.Null(e.Chance);
        Assert.Equal(152138, e.RngBefore);
        Assert.True(e.Ret);
    }

    [Fact] public void ThiefRoll_Schema51_ActionTargetDiceAndChance()
    {
        var e = Assert.IsType<ThiefRollEvent>(One("""{"seq":1,"tid":2,"t":3,"ev":"thief_roll","action":1,"target":"S073FT0002","dice1":40,"dice2":20,"chance":55,"rng_before":100,"rng_after":102,"ret":true}"""));
        Assert.Equal(1, e.Action);
        Assert.Equal("S073FT0002", e.Target);
        Assert.Equal((40L, 20L, 55L), (e.Dice1!.Value, e.Dice2!.Value, e.Chance!.Value));
    }

    [Fact] public void Relation_MainPairAndThirdParty_NullWord()
    {
        var e = Assert.IsType<RelationEvent>(One("""{"seq":363956,"tid":26300,"t":534219,"ev":"relation","a":"S143PL0001","b":"S143PL0002","race_a":0,"race_b":3,"value":-15,"absolute":false,"ret_addr":"0x5d98a9","cause":"rod_destroyed","before":{"cur":66,"prev":90,"ally":false,"eternal_war":false,"raw":23172},"after":{"cur":51,"prev":90,"ally":false,"eternal_war":false,"raw":23142},"changed":[{"race1":4,"race2":0,"before":0,"after":14},{"race1":0,"race2":3,"before":23172,"after":23142}],"ret":true}"""));
        Assert.Equal("rod_destroyed", e.Cause);
        Assert.Equal(-15, e.Value);
        Assert.Equal(51, e.After!.Cur);
        Assert.Equal(23142, e.After.Raw);
        Assert.Equal(new RelationChange(4, 0, 0, 14), e.Changed[0]);
        // Слово 23142 той же раскладкой, что у сохранения: биты 1…7 — текущее, 8…14 — прошлое.
        Assert.Equal((51, 90), (new Relation(0, 3, 23142).Current, new Relation(0, 3, 23142).Previous));
        Assert.Equal(7, new Relation(4, 0, 14).Current);

        var none = Assert.IsType<RelationEvent>(One("""{"seq":1,"tid":1,"t":1,"ev":"relation","a":"null","b":"null","race_a":-1,"race_b":-1,"value":5,"absolute":false,"ret_addr":"0x5d935e","cause":"tribute","before":null,"after":null,"changed":[],"ret":true}"""));
        Assert.Null(none.Before);
        Assert.Empty(none.Changed);
    }

    [Fact] public void ItemEquip_EquipCarriesItem_UnequipDoesNot()
    {
        var on = Assert.IsType<ItemEquipEvent>(One("""{"seq":509986,"tid":14176,"t":934204,"ev":"item_equip","stack":"S143KC0003","item":"S143IM0003","slot":3,"apply":1,"ret":true}"""));
        Assert.True(on.Equip && on.Apply);
        Assert.Equal(("S143IM0003", 3L), (on.Item, on.Slot));

        var ask = Assert.IsType<ItemEquipEvent>(One("""{"seq":509989,"tid":22468,"t":934204,"ev":"item_equip","stack":"S143KC0003","item":"S143IM0003","slot":3,"apply":0,"ret":true}"""));
        Assert.False(ask.Apply);

        var off = Assert.IsType<ItemEquipEvent>(One("""{"seq":509988,"tid":22468,"t":934204,"ev":"item_unequip","stack":"S143KC0003","slot":3,"apply":1,"ret":true}"""));
        Assert.False(off.Equip);
        Assert.Null(off.Item);
    }

    [Fact] public void Input_MouseDown()
    {
        var e = Assert.IsType<InputEvent>(One("""{"seq":778785,"tid":11032,"t":1517453,"ev":"input","kind":"down","msg":513,"vk":-1,"button":1,"x":746,"y":822,"sx":906,"sy":933}"""));
        Assert.Equal(("down", 513L, -1L, 1L, 746L, 933L), (e.Kind, e.Msg, e.Vk, e.Button, e.X, e.Sy));
    }

    [Fact] public void SiteEvents_CarrySite_FortDayCarriesVillageFields()
    {
        var hire = Assert.IsType<SiteBuyUnitEvent>(One("""{"seq":2640671,"tid":37284,"t":2811656,"ev":"site_buy_unit","site":"S073SI0004","stack":"S073KC0072","unit_type":"G000UU5003","id4":"null","id5":"null","ret":true}"""));
        Assert.Equal(("S073SI0004", "G000UU5003"), (hire.Site, hire.UnitType));

        // Наёмник выше базового уровня — тип из генератора юнитов (G000UG…, в Gunits его нет), и зонд,
        // называющий поле по двум буквам типа, пишет его как id3 (20260916-synth, ai-q53, seq 874244).
        var generated = Assert.IsType<SiteBuyUnitEvent>(One("""{"seq":874244,"tid":37492,"t":823938,"ev":"site_buy_unit","site":"S001SI0002","stack":"S001KC0001","id3":"G000UG8165","id4":"null","id5":"null","ret":true}"""));
        Assert.Equal("G000UG8165", generated.UnitType);

        var day = Assert.IsType<FortDayEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"fort_day","player":"S143PL0001","turn":5,"forts":[{"id":"S143FT0001","owner":"S143PL0001","subrace":"S143SR0001","stack":"S143KC0000","tier":3,"capacity":3,"size":3,"growth_t":5,"riot_t":0,"regen":0}]}"""));
        Assert.Equal(new FortState("S143FT0001", "S143PL0001", 3, "S143SR0001", "S143KC0000", 3, 3, 5, 0, 0),
                     Assert.Single(day.Forts));
    }

    [Fact] public void SpendEvents_Schema53_AllSixWithPriceAndSeqEnd()
    {
        string Tail(string fields) => $$"""{{fields}},"before":{{Before}},"after":{{After}},"seq_end":9,"ret":true}""";

        var unit = Assert.IsType<HireUnitEvent>(One(Tail("""{"seq":1,"tid":1,"t":5,"ev":"hire_unit","player":"S143PL0001","unit_type":"G000UU0001","group":"S143FT0001","position":2""")));
        Assert.Equal(("G000UU0001", "S143FT0001", 2L, 9L), (unit.UnitType, unit.Group, unit.Position, unit.SeqEnd));
        Assert.Equal(new Bank(150, 0, 40, 0, 0, 0), unit.Price);

        // Поля лидера кончаются строкой, а однострочный сырой литерал не может кончаться кавычкой — обычная строка.
        var leader = Assert.IsType<HireLeaderEvent>(One(Tail(
            "{\"seq\":1,\"tid\":1,\"t\":5,\"ev\":\"hire_leader\",\"player\":\"S143PL0001\",\"fort\":\"S143FT0001\",\"unit_type\":\"G000UU0002\"")));
        Assert.Equal(("S143FT0001", "G000UU0002"), (leader.Fort, leader.UnitType));

        var rod = Assert.IsType<RodDropEvent>(One(Tail("""{"seq":1,"tid":1,"t":5,"ev":"rod_drop","player":"S143PL0001","stack":"S143KC0001","x":10,"y":-1""")));
        Assert.Equal(("S143KC0001", 10L, -1L), (rod.Stack, rod.X, rod.Y));

        var cast = Assert.IsType<SpellCastEvent>(One(Tail($$"""{"seq":1,"tid":1,"t":5,"ev":"spell_cast","player":"S143PL0002","spell":"G000SS0001","x":4,"y":5,"use_spell_bank":true,"free":false,"spell_bank_before":{{Before}},"spell_bank_after":{{After}}""")));
        Assert.True(cast.UseSpellBank && !cast.Free);
        Assert.Equal(new Bank(350, 0, 60, 0, 0, 0), cast.SpellBankAfter);

        var research = Assert.IsType<SpellResearchEvent>(One(Tail("""{"seq":1,"tid":1,"t":5,"ev":"spell_research","player":"S143PL0001","spell":"G000SS0002","free":true""")));
        Assert.True(research.Free);

        var wand = Assert.IsType<WandCastEvent>(One(Tail("""{"seq":1,"tid":1,"t":5,"ev":"wand_cast","player":"S143PL0001","target":"S143KC0002","owner":"S143KC0001","item":"S143IM0004","x":3,"y":4""")));
        Assert.Equal(("S143KC0002", "S143KC0001", "S143IM0004"), (wand.Target, wand.Owner, wand.Item));
    }
}
