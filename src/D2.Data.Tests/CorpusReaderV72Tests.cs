using D2.Data.Corpus;

/// <summary>Схема 7.2: данные ИИ (<c>probeai72.cpp</c>) разбираются в свои записи, ни одно событие не уходит в
/// <see cref="UnknownEvent"/>. Строки — по форматам зонда; живых журналов 7.2 ещё нет.</summary>
public class CorpusReaderV72Tests
{
    private static CorpusEvent One(string line)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, line + "\n");
        try { return CorpusReader.Read(path).Single(); }
        finally { File.Delete(path); }
    }

    private const string Bank70 = """{"gold":70,"infernal":0,"life":0,"death":0,"runic":0,"grove":7}""";

    [Fact] public void PlaceCache_EnterCarriesPurseAndBank()
    {
        var e = Assert.IsType<AiPlaceCacheEvent>(One($$"""{"seq":1,"tid":7,"t":5,"ev":"ai_place_cache","at":"enter","player":"S143PL0001","owner":"S143PL0001","purse":{{Bank70}},"bank":{"gold":100,"infernal":0,"life":0,"death":0,"runic":0,"grove":10},"stack":null,"pos":null,"places":null,"picked":null,"excluded":null,"ret_addr":"0x471b3c"}"""));
        Assert.Equal(("enter", "S143PL0001", 70L, 100L), (e.At, e.Owner, e.Purse!.Gold, e.Bank!.Gold));
        Assert.Equal(7L, e.Purse.Grove);
        Assert.Null(e.Places);
        Assert.Null(e.Picked);
        Assert.Null(e.Stack);
    }

    [Fact] public void PlaceCache_CacheListsPlacesWithReason()
    {
        var e = Assert.IsType<AiPlaceCacheEvent>(One($$"""{"seq":2,"tid":7,"t":5,"ev":"ai_place_cache","at":"cache","player":"S143PL0001","owner":null,"purse":{{Bank70}},"bank":null,"stack":"S143KC0003","pos":{"x":10,"y":12},"places":[{"id":"S143SI0001","cls":"CMidSiteMerchant","d":5.5,"pos":{"x":14,"y":15},"why":"picked"},{"id":"S143SI0002","cls":"CMidSiteMage","d":40.2,"pos":{"x":44,"y":30},"why":"radius"}],"picked":["S143SI0001"],"excluded":[],"ret_addr":"0x466b3b"}"""));
        Assert.Equal(("cache", "S143KC0003", new AiPoint(10, 12)), (e.At, e.Stack, e.Pos));
        Assert.Equal(2, e.Places!.Count);
        Assert.Equal(new AiCachePlace("S143SI0001", "CMidSiteMerchant", 5.5, new AiPoint(14, 15), "picked"), e.Places[0]);
        Assert.Equal("radius", e.Places[1].Why);
        Assert.Equal(["S143SI0001"], e.Picked!);
        Assert.Empty(e.Excluded!);
        Assert.Equal(70L, e.Purse!.Gold);

        // Кэш чужой тактики — кошелька нет; отряд не найден — сбора не было.
        var bare = Assert.IsType<AiPlaceCacheEvent>(One("""{"seq":3,"tid":7,"t":5,"ev":"ai_place_cache","at":"cache","player":null,"owner":null,"purse":null,"bank":null,"stack":"S143KC0003","pos":null,"places":null,"picked":[],"excluded":[null],"ret_addr":"0x466b3b"}"""));
        Assert.Null(bare.Purse);
        Assert.Null(bare.Places);
        Assert.Equal([null], bare.Excluded!);
    }

    [Fact] public void FortCache_FilterGuardAndReason()
    {
        var e = Assert.IsType<AiFortCacheEvent>(One("""{"seq":4,"tid":7,"t":5,"ev":"ai_fort_cache","player":"S143PL0002","stack":"S143KC0086","pos":{"x":3,"y":4},"forts":[{"id":"S143FT0006","filter":true,"guard":false,"owner":"S143PL0003","visitor":"S143KC0060","d":9.1,"pos":{"x":8,"y":9},"why":"excluded"},{"id":"S143FT0001","filter":false,"guard":null,"owner":"S143PL0002","visitor":null,"d":null,"pos":null,"why":"filter"},{"id":"S143FT000c","filter":false,"guard":true,"owner":"S143PL0003","visitor":null,"d":null,"pos":null,"why":"guard"}],"picked":["S143FT000c"],"excluded":["S143FT0006"],"place_seq":3}"""));
        Assert.Equal(3, e.Forts!.Count);
        Assert.Equal(new AiCacheFort("S143FT0006", true, false, "S143PL0003", "S143KC0060", 9.1, new AiPoint(8, 9), "excluded"),
            e.Forts[0]);
        Assert.Equal(("filter", (bool?)null, (double?)null), (e.Forts[1].Why, e.Forts[1].Guard, e.Forts[1].D));
        Assert.Equal(("guard", (bool?)true), (e.Forts[2].Why, e.Forts[2].Guard));
        Assert.Equal((3L, "S143FT0006"), (e.PlaceSeq, e.Excluded[0]));

        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":5,"tid":7,"t":5,"ev":"ai_fort_cache","player":null,"stack":null,"pos":null,"forts":null,"picked":null,"excluded":[],"place_seq":3}"""));
    }

    [Fact] public void StrengthUnits_PerUnitContribution()
    {
        var e = Assert.IsType<AiStrengthUnitsEvent>(One("""{"seq":6,"tid":7,"t":5,"ev":"ai_strength_units","player":"S073PL0001","stack":"S073KC0003","a3":1,"enemies":2,"a6":0,"city":true,"full":false,"armor":0,"units":[{"id":"S073UN0010","type":"G000UU0010","hp":1,"max":120,"s":8.25,"mods":["G000UM0120","G000UM2302"]},{"id":"S073UN0011","type":"G000UU0011","hp":65,"max":65,"s":30.5,"mods":[]}],"total":38.75,"ret_addr":"0x453511"}"""));
        Assert.Equal(("S073KC0003", 2L, true, false, (long?)0), (e.Stack, e.Enemies, e.City, e.Full, e.Armor));
        Assert.Equal(2, e.Units.Count);
        Assert.Equal((1L, 120L, (double?)8.25), (e.Units[0].Hp, e.Units[0].Max, e.Units[0].S));
        Assert.Equal(["G000UM0120", "G000UM2302"], e.Units[0].Mods);
        Assert.Empty(e.Units[1].Mods);
        Assert.Equal(((double?)38.75, "0x453511"), (e.Total, e.RetAddr));

        var none = Assert.IsType<AiStrengthUnitsEvent>(One("""{"seq":7,"tid":7,"t":5,"ev":"ai_strength_units","player":null,"stack":null,"a3":0,"enemies":1,"a6":0,"city":false,"full":true,"armor":null,"units":[],"total":0,"ret_addr":"0x1"}"""));
        Assert.Null(none.Armor);
        Assert.Empty(none.Units);
    }

    [Fact] public void Regen_TermOwnerAndHp()
    {
        var e = Assert.IsType<RegenEvent>(One("""{"seq":8,"tid":7,"t":5,"ev":"regen","term":"unit","unit":"S143UN001a","type":"G000UU0001","owner":"S143FT000c","inside":null,"pct":15,"bonus":10,"hp_before":80,"hp_after":98,"hp_max":120,"ret_addr":"0x5d461b"}"""));
        Assert.Equal(("unit", "S143UN001a", "S143FT000c", (string?)null), (e.Term, e.Unit, e.Owner, e.Inside));
        Assert.Equal(((long?)15, (long?)10, 80L, 98L, 120L), (e.Pct, e.Bonus, e.HpBefore, e.HpAfter, e.HpMax));

        var ruin = Assert.IsType<RegenEvent>(One("""{"seq":9,"tid":7,"t":5,"ev":"regen","term":"ruin","unit":"S143UN0001","type":null,"owner":null,"inside":null,"pct":null,"bonus":null,"hp_before":0,"hp_after":0,"hp_max":50,"ret_addr":"0x5d4706"}"""));
        Assert.Equal(("ruin", (long?)null), (ruin.Term, ruin.Pct));
    }

    [Fact] public void UpgradeTarget_NoTargetAndLeader()
    {
        var e = Assert.IsType<UpgradeTargetEvent>(One("""{"seq":10,"tid":7,"t":5,"ev":"upgrade_target","leader":false,"player":"S143PL0001","unit":"S143UN0019","type":"G000UU0003","xp":275,"dyn":false,"grow":true,"container":"S143FT000c","owner":"S143PL0001","race_player":"S143PL0001","race_ok":true,"target":null,"flag":1,"ret_addr":"0x5d7499"}"""));
        Assert.Equal((false, "S143UN0019", (long?)275, (bool?)true, "S143FT000c"), (e.Leader, e.Unit, e.Xp, e.Grow, e.Container));
        Assert.Equal(((string?)null, (long?)1, (bool?)true), (e.Target, e.Flag, e.RaceOk));

        var leader = Assert.IsType<UpgradeTargetEvent>(One("""{"seq":11,"tid":7,"t":5,"ev":"upgrade_target","leader":true,"player":null,"unit":"S143UN0002","type":"G000UU5001","xp":8999,"dyn":true,"grow":false,"container":null,"owner":null,"race_player":null,"race_ok":null,"target":"G000UU5001","flag":null,"ret_addr":"0x5d7488"}"""));
        Assert.Equal((true, "G000UU5001", (bool?)false, (long?)null), (leader.Leader, leader.Target, leader.Grow, leader.Flag));
    }

    [Fact] public void PathExpand_NodesAndPath()
    {
        var e = Assert.IsType<PathExpandEvent>(One("""{"seq":12,"tid":7,"t":5,"ev":"path_expand","player":"S073PL0001","stack":"S073KC0085","owner":"S073PL0001","target":{"x":20,"y":30},"resolved":{"x":20,"y":30},"forbid_water":1,"a5":0,"mode":1,"a8":1,"a10":0,"n":3,"cut":false,"nodes":[[20,30,0,11.18034],[20,29,2,12.0],[19,29,2.4142137,12.4142137]],"path":[[10,25,0],[11,26,2],[12,27,4]],"end":{"x":20,"y":30},"reach":{"x":12,"y":27},"ret_addr":"0x469070"}"""));
        Assert.Equal(("S073KC0085", 1L, 3L, false), (e.Stack, e.Mode, e.N, e.Cut));
        Assert.Equal(3, e.Nodes.Count);
        Assert.Equal(new PathExpandNode(19, 29, 2.4142137, 12.4142137), e.Nodes[2]);
        Assert.Equal(new PathExpandPoint(12, 27, 4), e.Path[2]);
        Assert.Equal((new AiPoint(20, 30), new AiPoint(12, 27)), (e.End, e.Reach));

        Assert.Null(Assert.IsType<PathExpandEvent>(One("""{"seq":13,"tid":7,"t":5,"ev":"path_expand","player":null,"stack":"S073KC0001","owner":"S073PL0002","target":{"x":1,"y":1},"resolved":null,"forbid_water":0,"a5":0,"mode":2,"a8":0,"a10":0,"n":1,"cut":false,"nodes":[[1,1,null,null]],"path":[],"end":null,"reach":null,"ret_addr":"0x1"}""")).Nodes[0].G);

        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":14,"tid":7,"t":5,"ev":"path_expand","player":null,"stack":null,"owner":null,"target":null,"resolved":null,"forbid_water":0,"a5":0,"mode":0,"a8":0,"a10":0,"n":1,"cut":false,"nodes":[[1,1,0]],"path":[],"end":null,"reach":null,"ret_addr":"0x1"}"""));
    }

    [Fact] public void IdAlloc_TypeIndexAndCallers()
    {
        var e = Assert.IsType<IdAllocEvent>(One("""{"seq":15,"tid":7,"t":5,"ev":"id_alloc","player":"S143PL0001","type":27,"id":"S143KC00a9","index":169,"ret_addr":"0x5d6a10","callers":["0x5d6a10","0x5d6957","0x41f2a1"]}"""));
        Assert.Equal((27L, "S143KC00a9", (long?)169, "0x5d6a10"), (e.Type, e.Id, e.Index, e.RetAddr));
        Assert.Equal(["0x5d6a10", "0x5d6957", "0x41f2a1"], e.Callers);
    }

    [Fact] public void JournalMixingV71AndV72_ReadsAll()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, [
            """{"seq":1,"tid":1,"ev":"start","exe_size":1,"version":"Gog","probe":"dev","autobattle":false}""",
            """{"seq":2,"tid":1,"t":2,"ev":"ai_grid","player":null,"owner":"S143PL0001","grid":"0x10","ret_addr":"0x1","seq_end":3}""",
            """{"seq":4,"tid":1,"t":2,"ev":"id_alloc","player":null,"type":1,"id":null,"index":null,"ret_addr":"0x1","callers":[]}""",
        ]);
        try
        {
            var events = CorpusReader.Read(path).ToList();
            Assert.Equal(3, events.Count);
            Assert.DoesNotContain(events, e => e is UnknownEvent);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Зс210: портреты боя зонда 7.6 пишут id юнита без кавычек (<c>probeflash.cpp</c> <c>"unit":{}</c>) — строка читается, id тот же;
    /// прочая порча строки по-прежнему падает.</summary>
    [Fact] public void BattleFace_BareUnitId_IsQuoted_OtherDamageStillThrows()
    {
        using (var doc = CorpusReader.ParseJson("""{"seq":1,"tid":7,"t":5,"ev":"bat_text_end","unit":S073UN01f8,"state":-1,"how":"end","ms":359}"""))
            Assert.Equal("S073UN01f8", doc.RootElement.GetProperty("unit").GetString());
        using (var none = CorpusReader.ParseJson("""{"seq":2,"tid":7,"t":5,"ev":"bat_flash","unit":null,"state":2}"""))
            Assert.Equal(System.Text.Json.JsonValueKind.Null, none.RootElement.GetProperty("unit").ValueKind);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => CorpusReader.ParseJson("""{"seq":3,"tid":7,"t":5,"ev":"ai_order","stack":S073KC0001}"""));
    }
}
