using D2.Data.Corpus;
using D2.Rules.Map;

/// <summary>Схема 6.0, партия 1: каждое новое событие разбирается в свою запись, ни одно не уходит в
/// <see cref="UnknownEvent"/>. Строки — по форматам <c>probemap.cpp</c>, <c>probecheck.cpp</c>,
/// <c>probenoble.cpp</c>; живых журналов 6.0 ещё нет.</summary>
public class CorpusReaderV6Tests
{
    private const string Word = """{"cur":66,"prev":90,"ally":false,"eternal_war":false,"raw":23172}""";

    private static CorpusEvent One(string line)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, line + "\n");
        try { return CorpusReader.Read(path).Single(); }
        finally { File.Delete(path); }
    }

    [Fact] public void Save_NameBytesDecodeAsCp1251()
    {
        var e = Assert.IsType<SaveEvent>(One("""{"seq":10,"tid":1,"t":5,"ev":"save","name":"Ñîõð 1","temp":false,"turn":7,"ok":true,"seq_end":12}"""));
        Assert.Equal("Сохр 1", e.NameText);
        Assert.Equal((false, 7L, true, 12L), (e.Temp, e.Turn, e.Ok, e.SeqEnd));
    }

    [Fact] public void Save_Utf8NameAsIs_SmokeRunEscapesStillDecode()
    {
        var utf8 = Assert.IsType<SaveEvent>(One("""{"seq":10,"tid":1,"t":5,"ev":"save","name":"Автосохранение","temp":false,"turn":1,"ok":true,"seq_end":11}"""));
        Assert.Equal("Автосохранение", utf8.NameText);

        // Ровно как в corpus/20260914-smoke (зонд 856bf05).
        var smoke = Assert.IsType<SaveEvent>(One("""{"seq":52803,"tid":12284,"t":20704,"ev":"save","name":"Àâòîñîõðàíåíèå","temp":false,"turn":1,"ok":true,"seq_end":52804}"""));
        Assert.Equal("Автосохранение", smoke.NameText);

        var ascii = Assert.IsType<SaveEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"save","name":"QuickSave","temp":false,"turn":6,"ok":true,"seq_end":2}"""));
        Assert.Equal("QuickSave", ascii.NameText);
        Assert.Equal("«Сохр» 2°", new SaveEvent(1, 1, 2, "«Сохр» 2°", false, 1, true, 2).NameText);
    }

    [Fact] public void EmptySlotItemType_JsonNull_AndSmokeRunStringNull()
    {
        var empty = Assert.IsType<ItemConsumeEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"item_consume","group":"S143KC0001","slot":2,"item_before":"G000000000","item_type":null,"item_after":"G000000000","battle_round":5,"seq_end":2}"""));
        Assert.Null(empty.ItemType);
        Assert.False(empty.Consumed);

        var smoke = Assert.IsType<ItemConsumeEvent>(One("""{"seq":76430,"tid":12284,"t":36000,"ev":"item_consume","group":"S143KC0001","slot":2,"item_before":"G000000000","item_type":"null","item_after":"G000000000","battle_round":5,"seq_end":76431}"""));
        Assert.Null(smoke.ItemType);

        var equip = Assert.IsType<EquipCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"equip_check","item":"G000000000","item_type":null,"stack":"S143KC0001","slot":2,"check_effect":false,"ret_addr":"0x5041b1","ret":9}"""));
        Assert.Null(equip.ItemType);

        var garrison = Assert.IsType<GarrisonMaxEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"garrison_max","group":"0x2f00a0","fort":"null","allowed":null,"ret_addr":"0x5f4495","ret":6}"""));
        Assert.Null(garrison.Allowed);
    }

    [Fact] public void FortCapture_CapturedWhenOwnerChanged()
    {
        var e = Assert.IsType<FortCaptureEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"fort_capture","stack":"S143KC0001","fort":"S143FT0003","owner":"S143PL0001","subrace":"S143SR0001","skip_leader_check":false,"owner_before":"S143PL0004","subrace_before":"S143SR0004","visitor_before":"G000000000","owner_after":"S143PL0001","ret":true,"seq_end":9}"""));
        Assert.True(e.Captured);
        Assert.Equal(("S143FT0003", "S143PL0004", 9L), (e.Fort, e.OwnerBefore, e.SeqEnd));
    }

    [Fact] public void LandGrow_CellsInTakeOrder()
    {
        var e = Assert.IsType<LandGrowEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"land_grow","x":12,"y":7,"quota":3,"terrain":1,"count":2,"cells":"13:7;12:8","ret":true,"seq_end":4}"""));
        Assert.Equal(new[] { (13, 7), (12, 8) }, e.Cells);
        Assert.Equal((3L, 1L, 2L), (e.Quota, e.Terrain, e.Count));
        Assert.Throws<FormatException>(() => LandGrowEvent.ParseCells("13-7"));
    }

    [Fact] public void LandReject()
    {
        var e = Assert.IsType<LandRejectEvent>(One("""{"seq":3,"tid":1,"t":2,"ev":"land_reject","x":14,"y":7,"cx":12,"cy":7,"quota":3}"""));
        Assert.Equal((14L, 7L, 12L, 7L, 3L), (e.X, e.Y, e.Cx, e.Cy, e.Quota));
    }

    [Fact] public void SiteVisit()
    {
        var e = Assert.IsType<SiteVisitEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"site_visit","site":"S143SI0002","stack":"S143KC0001","owner":"S143PL0001","move_left":14,"ret":true}"""));
        Assert.Equal(("S143SI0002", "S143PL0001", 14L), (e.Site, e.Owner, e.MoveLeft));
    }

    [Fact] public void SpellExpire()
    {
        var e = Assert.IsType<SpellExpireEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"spell_expire","player":"S143PL0002","turn":8,"ret":true,"seq_end":3}"""));
        Assert.Equal(("S143PL0002", 8L), (e.Player, e.Turn));
    }

    [Fact] public void ItemCreate_IdsByType()
    {
        var e = Assert.IsType<ItemCreateEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"item_create","item_type":"G000IG0001","stack":"S143KC0001","apply":true,"ret_addr":"0x43915f","cause":"loot_ruin","ret":true,"seq_end":5}"""));
        Assert.Equal(2, e.Ids.Count);
        Assert.Equal("G000IG0001", e.Ids["item_type"]);
        Assert.Equal(("loot_ruin", true), (e.Cause, e.Apply));
    }

    [Fact] public void ApEffectAndApply_PairByEffect_NullDelta()
    {
        var made = Assert.IsType<ApEffectEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ap_effect","effect":"0x1a2b3c","stack":"S143KC0001","delta":10,"ret_addr":"0x41d8ca","cause":"duel"}"""));
        Assert.Equal(("duel", 10L), (made.Cause, made.Delta!.Value));

        var applied = Assert.IsType<ApApplyEvent>(One("""{"seq":2,"tid":1,"t":2,"ev":"ap_apply","effect":"0x1a2b3c","stack":"S143KC0001","delta":10,"move_before":20,"move_after":10,"ret":true,"seq_end":3}"""));
        Assert.Equal(made.Effect, applied.Effect);
        Assert.Equal((20L, 10L), (applied.MoveBefore, applied.MoveAfter));

        var unread = Assert.IsType<ApEffectEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ap_effect","effect":"0x0","stack":"null","delta":null,"ret_addr":"0x0","cause":"unknown"}"""));
        Assert.Null(unread.Delta);
    }

    [Fact] public void HpChange()
    {
        var e = Assert.IsType<HpChangeEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"hp_change","unit":"S143UN0005","amount":42,"flag":1,"hp_before":140,"hp_after":98,"ret":true,"seq_end":2}"""));
        Assert.Equal((42L, 1L, 140L, 98L), (e.Amount!.Value, e.Flag, e.HpBefore, e.HpAfter));
    }

    [Fact] public void KeyedChecks_AllEight()
    {
        var garrison = Assert.IsType<GarrisonMaxEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"garrison_max","group":"0x2f00a0","fort":"S143FT0002","allowed":-1,"ret_addr":"0x4fe000","ret":1}"""));
        Assert.Equal(("S143FT0002", (long?)-1L, 1L), (garrison.Fort, garrison.Allowed, garrison.Ret));

        var scout = Assert.IsType<ScoutEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"scout","bonus":2,"clamped":false,"ret_addr":"0x5d4000","ret":5}"""));
        Assert.Equal((2L, false, 5L), (scout.Bonus, scout.Clamped, scout.Ret));

        var rod = Assert.IsType<RodCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"rod_check","stack":"S143KC0001","x":4,"y":9,"flag":false,"ret_addr":"0x4cdbe7","ret":9}"""));
        Assert.Equal(("0x4cdbe7", 9L, 4L), (rod.RetAddr, rod.Ret, rod.X));

        var hire = Assert.IsType<HireCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"hire_check","player":"S143PL0001","unit_type":"G000UU0001","group":"S143FT0001","position":2,"ret_addr":"0x502a42","ret":10}"""));
        Assert.Equal(("G000UU0001", 2L, 10L), (hire.UnitType, hire.Position, hire.Ret));

        var leader = Assert.IsType<HireLeaderCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"hire_leader_check","player":"S143PL0001","unit_type":"G000UU0002","ret_addr":"0x505270","ret":2}"""));
        Assert.Equal(2L, leader.Ret);

        var merc = Assert.IsType<MercCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"merc_check","site":"S143SI0004","stack":"S143KC0001","unit_type":"G000UU5003","ret_addr":"0x5d7ac2","ret":false}"""));
        Assert.False(merc.Ret);

        var sell = Assert.IsType<SellCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"sell_check","site":"S143SI0001","stack":"S143KC0001","item":"S143IM0003","item_type":"G000IG0007","ret_addr":"0x4fe8c8","ret":true}"""));
        Assert.Equal(("G000IG0007", true), (sell.ItemType, sell.Ret));

        var grow = Assert.IsType<GrowCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"grow_check","player":"S143PL0002","fort":"S143FT0005","turn":6,"ret_addr":"0x44982a","ret":7}"""));
        Assert.Equal((6L, 7L), (grow.Turn, grow.Ret));
    }

    [Fact] public void NobleActionCancelAndChoose()
    {
        var act = Assert.IsType<NobleActionEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"noble_action","player":"S143PL0002","stack":"S143KC0007","target":"S143FT0001","action":9,"success":true,"value":137,"id":"S143PL0001","ret":true,"seq_end":6}"""));
        Assert.Equal(("S143KC0007", 9L, 137L, true), (act.Stack, act.Action, act.Value, act.Success));

        var cancel = Assert.IsType<ThiefCancelEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"thief_cancel","player":"S143PL0001","stack":"S143KC0003","ret":true,"seq_end":4}"""));
        Assert.Equal("S143KC0003", cancel.Stack);

        var choose = Assert.IsType<NobleChooseEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"noble_choose","player":"S143PL0001","stack":"S143KC0003","target":"S143KC0009","count":2,"actions":[1,9],"ret":true}"""));
        Assert.Equal(new long[] { 1, 9 }, choose.Actions);
        Assert.Equal(choose.Count, choose.Actions.Count);
    }

    [Fact] public void AllianceProposeChangeAndTrade()
    {
        var propose = Assert.IsType<AllianceProposeEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"alliance_propose","a":"S143PL0001","b":"S143PL0002","human_a":true,"human_b":null,"race_a":0,"race_b":3,"relation":null,"ret":true}"""));
        Assert.Equal((true, (bool?)null), (propose.HumanA!.Value, propose.HumanB));
        Assert.Null(propose.Relation);

        var change = Assert.IsType<AllianceChangeEvent>(One($$"""{"seq":1,"tid":1,"t":2,"ev":"alliance_change","a":"S143PL0002","b":"S143PL0001","accept":false,"silent":false,"flag_e":false,"human_a":false,"human_b":true,"before":{{Word}},"after":{{Word}},"ret":true,"seq_end":5}"""));
        Assert.Equal(66, change.Before!.Cur);
        Assert.False(change.Accept);

        var trade = Assert.IsType<TradeAcceptEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"trade_accept","a":"S143PL0001","b":"S143PL0002","object":"S143IM0004","id2":"G000000000","gold":50,"accepted":true,"ret":true,"seq_end":7}"""));
        Assert.Equal(("S143IM0004", 50L, true), (trade.Object, trade.Gold, trade.Accepted));
    }

    [Fact] public void Xp_RetAddr_And_FortDay_ProtectB()
    {
        var xp = Assert.IsType<XpEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"xp","player":"S143PL0002","unit":"S143UN0001","amount":30,"ret_addr":"0x431d07","ret":false}"""));
        Assert.Equal("0x431d07", xp.RetAddr);

        var day = Assert.IsType<FortDayEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"fort_day","player":"S143PL0001","turn":5,"forts":[{"id":"S143FT0001","owner":"S143PL0001","subrace":"S143SR0001","stack":"G000000000","tier":6,"capacity":6,"size":-1,"growth_t":-1,"riot_t":-1,"regen":-1,"protect_b":null},{"id":"S143FT0002","owner":"S143PL0001","subrace":"S143SR0001","stack":"G000000000","tier":2,"capacity":5,"size":2,"growth_t":3,"riot_t":0,"regen":10,"protect_b":"G000000000"}]}"""));
        Assert.Null(day.Forts[0].ProtectB);
        Assert.Equal("G000000000", day.Forts[1].ProtectB);
        Assert.Null(xp.XpBefore); // журнал партии 1: полей партии 2 нет
        Assert.Null(xp.Leader);
    }

    // Партия 2: лидеры, предметы, бой — форматы probeleader.cpp, probecheck.cpp, probehooks.cpp, probeecon.cpp.

    [Fact] public void Xp_Batch2Fields_NullLeaderWhenUnread()
    {
        var xp = Assert.IsType<XpEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"xp","player":"S143PL0001","unit":"S143UN000d","amount":62,"xp_before":94,"xp_after":100,"hp":110,"level":1,"xp_next":100,"impl":"G000UU0022","leader":true,"max_leader":99,"ret_addr":"0x665788","ret":true}"""));
        Assert.Equal((94L, 100L, 110L, 1L, 100L), (xp.XpBefore!.Value, xp.XpAfter!.Value, xp.Hp!.Value, xp.Level!.Value, xp.XpNext!.Value));
        Assert.Equal(("G000UU0022", true, 99L), (xp.Impl, xp.Leader!.Value, xp.MaxLeader!.Value));

        var unread = Assert.IsType<XpEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"xp","player":"S143PL0001","unit":"S143UN000d","amount":6,"xp_before":-1,"xp_after":-1,"hp":-1,"level":-1,"xp_next":-1,"impl":"null","leader":null,"max_leader":-1,"ret_addr":"0x431d07","ret":false}"""));
        Assert.Null(unread.Leader);
        Assert.Equal(-1L, unread.XpBefore);
    }

    [Fact] public void Revive_HpAndXpBeforeAfter_OldLineStillParses()
    {
        var e = Assert.IsType<ReviveEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"revive","unit":"S143UN0004","player":"S143PL0001","hp_before":0,"hp_after":1,"xp_before":37,"xp_after":37,"ret":true}"""));
        Assert.Equal((0L, 1L, 37L, 37L), (e.HpBefore!.Value, e.HpAfter!.Value, e.XpBefore!.Value, e.XpAfter!.Value));

        var old = Assert.IsType<ReviveEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"revive","unit":"S143UN0004","player":"S143PL0001","ret":true}"""));
        Assert.Null(old.HpAfter);
    }

    [Fact] public void SkillOfferPickAndUpgCount()
    {
        var offer = Assert.IsType<SkillOfferEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"skill_offer","leader":"S143UN0001","player":"S143PL0002","lord":"G000LR0004","level":3,"ids":["G000UM4101","G000UM4102"],"ret":true}"""));
        Assert.Equal(new[] { "G000UM4101", "G000UM4102" }, offer.Ids);
        Assert.Equal(("G000LR0004", 3L), (offer.Lord, offer.Level));

        var pick = Assert.IsType<SkillPickEvent>(One("""{"seq":5,"tid":1,"t":2,"ev":"skill_pick","stack":"S143KC0002","modif":"G000UM4102","free":false,"apply":true,"leader":"S143UN0001","upg_before":1,"upg_after":0,"rng_before":40,"rng_after":40,"ret_addr":"0x5d6e6e","ret":true,"seq_end":7}"""));
        Assert.Equal((1L, 0L, "0x5d6e6e"), (pick.UpgBefore, pick.UpgAfter, pick.RetAddr));
        Assert.Equal(pick.RngBefore, pick.RngAfter);

        var count = Assert.IsType<UpgCountEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"upg_count","stack":"S143KC0002","count":1,"before":0,"after":1,"apply":true,"ret_addr":"0x42246c","ret":true,"seq_end":3}"""));
        Assert.Equal((1L, 0L, 1L), (count.Count, count.Before, count.After));
    }

    [Fact] public void EquipAndWandChecks_CodeInRet()
    {
        var equip = Assert.IsType<EquipCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"equip_check","item":"S143IM0003","item_type":"G000IG5001","stack":"S143KC0001","slot":4,"check_effect":true,"ret_addr":"0x5041b1","ret":5}"""));
        Assert.Equal(("G000IG5001", 4L, true, 5L), (equip.ItemType, equip.Slot, equip.CheckEffect, equip.Ret));

        var wand = Assert.IsType<WandCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"wand_check","item":"S143IM0007","target":"S143KC0001","x":10,"y":4,"flag":false,"daily_uses":2,"ret_addr":"0x4cd5ff","ret":18}"""));
        Assert.Equal((2L, 0x12L, 10L), (wand.DailyUses, wand.Ret, wand.X));
    }

    [Fact] public void PotionConsumeTalismanAndXpGroup()
    {
        var potion = Assert.IsType<PotionUseEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"potion_use","player":"S143PL0001","target":"S143UN0003","group":"S143KC0001","item":"S143IM0009","item_type":"G000IG1001","destroy":false,"in_battle":true,"hp_before":40,"hp_after":90,"ret":true,"seq_end":4}"""));
        Assert.Equal((true, false, 40L, 90L), (potion.InBattle, potion.Destroy, potion.HpBefore, potion.HpAfter));

        var consume = Assert.IsType<ItemConsumeEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"item_consume","group":"S143KC0001","slot":2,"item_before":"S143IM0009","item_type":"G000IG1001","item_after":"G000000000","battle_round":3,"seq_end":6}"""));
        Assert.True(consume.Consumed);
        Assert.Equal((2L, 3L), (consume.Slot, consume.BattleRound));

        var charge = Assert.IsType<TalismanChargeEvent>(One("""{"seq":2,"tid":1,"t":2,"ev":"talisman_charge","item":"S143IM000a","apply":true,"ret":true}"""));
        Assert.True(charge.Apply);

        var group = Assert.IsType<XpGroupEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"xp_group","group":"S143KC0001","player":"S143PL0001","attacker":true,"ai":false,"weapon_master":true,"units":[{"id":"S143UN000d","acc":50,"applied":62},{"id":"S143UN000c","acc":0,"applied":0}],"seq_end":9}"""));
        Assert.Equal(2, group.Units.Count);
        Assert.Equal(new XpGroupUnit("S143UN000d", 50, 62), group.Units[0]);
        Assert.True(group.WeaponMaster);

        var noHook = Assert.IsType<XpGroupEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"xp_group","group":"S143KC0001","player":"S143PL0001","attacker":false,"ai":true,"weapon_master":null,"units":[],"seq_end":2}"""));
        Assert.Null(noHook.WeaponMaster);
    }

    [Fact] public void AttackHit_BestowWards_TargetAndAddedWards()
    {
        var e = Assert.IsType<AttackHitEvent>(One("""{"seq":100,"tid":1,"t":2,"ev":"attack_hit","disp_class":24,"new_seq":97,"attacker":"S143UN0040","attack":"G000AA8133","target":"S143UN008a","found":true,"hp_before":45,"hp_after":45,"st_before":0,"st_after":0,"mods_before":["G000UM0099"],"mods_after":["G000UM0099","g000um0001","g000um0016"],"seq_end":104}"""));
        Assert.Equal((24L, 97L, "S143UN0040", "G000AA8133", "S143UN008a"), (e.DispClass, e.NewSeq, e.Attacker, e.Attack, e.Target));
        Assert.True(e.Found);
        Assert.Equal(new[] { "g000um0001", "g000um0016" }, e.ModsAdded);
        Assert.Equal(104L, e.SeqEnd);
    }

    [Fact] public void AttackHit_TargetNotInSlots_ModsNull_CreatorUnknown()
    {
        var e = Assert.IsType<AttackHitEvent>(One("""{"seq":5,"tid":1,"t":2,"ev":"attack_hit","disp_class":17,"new_seq":-1,"attacker":"null","attack":"null","target":"S143UN0100","found":false,"hp_before":-1,"hp_after":-1,"st_before":0,"st_after":0,"mods_before":null,"mods_after":null,"seq_end":9}"""));
        Assert.False(e.Found);
        Assert.Null(e.ModsBefore);
        Assert.Empty(e.ModsAdded);
        Assert.Equal((-1L, "null"), (e.NewSeq, e.Attacker));
        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":5,"tid":1,"t":2,"ev":"attack_hit","disp_class":17}"""));
    }

    // 6.2.
    private static string Table(Func<int, int> word) =>
        string.Concat(Enumerable.Range(0, 250).Select(i => word(i).ToString("x4")));

    [Fact] public void RngSeed_ReseedFromTimer_TableSnapshotParsed()
    {
        var table = Table(i => (i * 0x101 + 7) & 0xffff);
        var e = Assert.IsType<RngSeedEvent>(One($$"""{"seq":40,"tid":1,"t":9,"ev":"rng_seed","n":50688,"arg":123456789,"index_before":112,"index_after":39,"via":"reseed","cause":"new_game_time","ret_addr":"0x429dcd","wide":0,"table":"{{table}}"}"""));
        Assert.Equal((50688L, 123456789L, 112L, 39L), (e.N, e.Arg, e.IndexBefore, e.IndexAfter));
        Assert.Equal(("reseed", "new_game_time", "0x429dcd", 0L), (e.Via, e.Cause, e.RetAddr, e.Wide));
        Assert.Equal(250, e.Table.Count);
        Assert.Equal((ushort)7, e.Table[0]);
        Assert.Equal((ushort)((249 * 0x101 + 7) & 0xffff), e.Table[249]);
    }

    [Fact] public void RngSeed_TableOfWrongLength_IsSchemaDrift()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":1,"tid":1,"t":2,"ev":"rng_seed","n":0,"arg":47,"index_before":47,"index_after":47,"via":"set_state","cause":"load_save","ret_addr":"0x429dec","wide":0,"table":"00ff"}"""));
        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":1,"tid":1,"t":2,"ev":"rng_seed","n":0}"""));
    }

    [Fact] public void ApRestore_TurnStartZeroedByMissingLeader()
    {
        var e = Assert.IsType<ApRestoreEvent>(One("""{"seq":70,"tid":1,"t":2,"ev":"ap_restore","stack":"S143KC0091","player":"S143PL0002","amount":20,"move_before":20,"move_after":0,"max":0,"leader":"S143UN0100","leader_found":false,"leader_alive":true,"zero":"max0_no_leader","cause":"turn_start","ret_addr":"0x5d4470","ret":true,"seq_end":71}"""));
        Assert.Equal(("S143KC0091", "S143PL0002", 20L, 20L, 0L, 0L), (e.Stack, e.Player, e.Amount, e.MoveBefore, e.MoveAfter, e.Max));
        Assert.Equal(("S143UN0100", false, (bool?)true), (e.Leader, e.LeaderFound, e.LeaderAlive));
        Assert.Equal(("max0_no_leader", "turn_start", "0x5d4470", true, 71L), (e.Zero, e.Cause, e.RetAddr, e.Ret, e.SeqEnd));
    }

    [Fact] public void ApRestore_StackUnreadable_NullsAndNoZeroReason()
    {
        var e = Assert.IsType<ApRestoreEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ap_restore","stack":"S143KC0001","player":"null","amount":-3,"move_before":-1,"move_after":-1,"max":-1,"leader":"null","leader_found":false,"leader_alive":null,"zero":null,"cause":"unknown","ret_addr":"0x401000","ret":false,"seq_end":8}"""));
        Assert.Null(e.LeaderAlive);
        Assert.Null(e.Zero);
        Assert.Equal(-3L, e.Amount);
        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":7,"tid":1,"t":2,"ev":"ap_restore","stack":"S143KC0001"}"""));
    }

    [Fact] public void ReadByEvent_OnlyMatchingLinesAreParsed()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, [
            """{"seq":1,"tid":1,"t":2,"ev":"ui_refusal","text":"X005TA0247","what":"no_space","ret_addr":"0x498477"}""",
            "это не JSON: строка без маркера не разбирается",
            """{"seq":3,"tid":1,"t":2,"ev":"ui_refusal_x","text":"X005TA0247"}""",
            """{"seq":4,"tid":1,"t":2,"ev":"ui_refusal","text":"X005TA0211","what":"dead_leader_transfer","ret_addr":"0x4b19c0"}""",
        ]);
        try
        {
            var texts = CorpusReader.Read(path, "ui_refusal").Cast<UiRefusalEvent>().Select(e => e.Text).ToList();
            Assert.Equal(["X005TA0247", "X005TA0211"], texts);
        }
        finally { File.Delete(path); }
    }

    // 6.3.
    [Fact] public void UnitTransfer_SwapBetweenGarrisonAndStack()
    {
        var e = Assert.IsType<UnitTransferEvent>(One("""{"seq":90,"tid":1,"t":3,"ev":"unit_transfer","kind":"swap","same_group":false,"from":"S135FT0000","from_pos":1,"to":"S135KC0000","to_pos":3,"unit":"S135UN014c","other":"S135UN014f","from_after":"S135UN014f","to_after":"S135UN014c","cause":"swap_msg","ret_addr":"0x42e45a","ret":true,"seq_end":95}"""));
        Assert.Equal(("swap", false, "S135FT0000", 1L, "S135KC0000", 3L), (e.Kind, e.SameGroup, e.From, e.FromPos, e.To, e.ToPos));
        Assert.Equal(("S135UN014c", "S135UN014f", "S135UN014f", "S135UN014c"), (e.Unit, e.Other, e.FromAfter, e.ToAfter));
        Assert.Equal(("swap_msg", "0x42e45a", true, 95L), (e.Cause, e.RetAddr, e.Ret, e.SeqEnd));
    }

    [Fact] public void UnitTransfer_MoveIntoEmptyCell_AndDismissHasNoTarget()
    {
        var move = Assert.IsType<UnitTransferEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"unit_transfer","kind":"move","same_group":true,"from":"S135KC002f","from_pos":0,"to":"S135KC002f","to_pos":4,"unit":"S135UN0144","other":null,"from_after":null,"to_after":"S135UN0144","cause":"swap_msg","ret_addr":"0x42e45a","ret":true,"seq_end":3}"""));
        Assert.Null(move.Other);
        Assert.Null(move.FromAfter);
        Assert.True(move.SameGroup);

        var dismiss = Assert.IsType<UnitTransferEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"unit_transfer","kind":"dismiss","same_group":false,"from":"S135KC0000","from_pos":2,"to":null,"to_pos":-1,"unit":"S135UN0143","other":null,"from_after":null,"to_after":null,"cause":"dismiss_msg","ret_addr":"0x42e726","ret":false,"seq_end":4}"""));
        Assert.Equal(("dismiss", (string?)null, -1L, "S135UN0143", false), (dismiss.Kind, dismiss.To, dismiss.ToPos, dismiss.Unit, dismiss.Ret));
        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":1,"tid":1,"t":2,"ev":"unit_transfer","kind":"move"}"""));
    }

    [Fact] public void SwapAndDismissChecks_CodesAndCells()
    {
        var swap = Assert.IsType<SwapCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"swap_check","from":"S135FT0000","from_pos":0,"to":"S135KC0000","to_pos":5,"unit":"S135UN014c","other":null,"ret_addr":"0x500d86","ret":11}"""));
        Assert.Equal(("S135FT0000", 0L, "S135KC0000", 5L, "S135UN014c", (string?)null), (swap.From, swap.FromPos, swap.To, swap.ToPos, swap.Unit, swap.Other));
        Assert.Equal(("0x500d86", 11L), (swap.RetAddr, swap.Ret));

        var dismiss = Assert.IsType<DismissCheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"dismiss_check","group":"S135KC002f","position":2,"unit":null,"ret_addr":"0x500c78","ret":4}"""));
        Assert.Equal(("S135KC002f", 2L, (string?)null, "0x500c78", 4L), (dismiss.Group, dismiss.Position, dismiss.Unit, dismiss.RetAddr, dismiss.Ret));
    }

    [Fact] public void EquipDrop_SilentRefusalOfScroll()
    {
        var e = Assert.IsType<EquipDropEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"equip_drop","item":"S135IM0031","slot":4,"ret_addr":"0x50428a","ret":2}"""));
        Assert.Equal(("S135IM0031", 4L, "0x50428a", 2L), (e.Item, e.Slot, e.RetAddr, e.Ret));
        Assert.Null(Assert.IsType<EquipDropEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"equip_drop","item":null,"slot":-1,"ret_addr":"0x50428a","ret":2}""")).Item);
    }

    [Fact] public void UiRefusal_TextAndSite()
    {
        var e = Assert.IsType<UiRefusalEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ui_refusal","text":"X005TA0247","what":"no_space","ret_addr":"0x498477"}"""));
        Assert.Equal(("X005TA0247", "no_space", "0x498477"), (e.Text, e.What, e.RetAddr));
        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":1,"tid":1,"t":2,"ev":"ui_refusal","text":"X005TA0247"}"""));
    }

    [Fact] public void WandPrecheck_ManaRefusalFromInterface()
    {
        var e = Assert.IsType<WandPrecheckEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"wand_precheck","item":"S135IM0041","item_type":"G000IG6001","stack":"S135KC0000","daily_uses":1,"ret_addr":"0x4ff970","ret":9}"""));
        Assert.Equal(("S135IM0041", "G000IG6001", "S135KC0000", 1L, "0x4ff970", 9L), (e.Item, e.ItemType, e.Stack, e.DailyUses, e.RetAddr, e.Ret));
    }

    // 6.4.
    [Fact] public void AiBreakCheck_StrongSideHalvesTermAndDoublesChance()
    {
        var e = Assert.IsType<AiBreakCheckEvent>(One("""{"seq":5,"tid":1,"t":2,"ev":"ai_break_check","player":"S143PL0002","other":"S143PL0001","forts_player":2,"forts_other":1,"strong":true,"turn":10,"ally_turn":1,"nobreak":false,"d":9,"t_base":10,"t_eff":5,"cap":5,"chance":8,"roll":3,"rng_before":40,"rng_after":41,"ret_addr":"0x4487ac","ret":true,"seq_end":6}"""));
        Assert.Equal(("S143PL0002", "S143PL0001", 2L, 1L, true), (e.Player, e.Other, e.FortsPlayer, e.FortsOther, e.Strong));
        Assert.Equal((10L, (long?)1, (bool?)false, (long?)9, 10L, 5L, 5L), (e.Turn, e.AllyTurn, e.NoBreak, e.D, e.TBase, e.TEff, e.Cap));
        Assert.Equal(((long?)8, (long?)3, 40L, 41L, "0x4487ac", true, 6L), (e.Chance, e.Roll, e.RngBefore, e.RngAfter, e.RetAddr, e.Ret, e.SeqEnd));
        Assert.Equal(2L, e.T);
    }

    [Fact] public void AiBreakCheck_TooEarly_NoChanceNoRoll()
    {
        var e = Assert.IsType<AiBreakCheckEvent>(One("""{"seq":5,"tid":1,"t":2,"ev":"ai_break_check","player":"S143PL0002","other":"S143PL0001","forts_player":1,"forts_other":1,"strong":false,"turn":4,"ally_turn":0,"nobreak":false,"d":4,"t_base":10,"t_eff":10,"cap":5,"chance":null,"roll":null,"rng_before":40,"rng_after":40,"ret_addr":"0x4487ac","ret":false,"seq_end":6}"""));
        Assert.Null(e.Chance);
        Assert.Null(e.Roll);
        Assert.False(e.Ret);
        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":5,"tid":1,"t":2,"ev":"ai_break_check","player":"S143PL0002"}"""));
    }

    [Fact] public void AiOfferCheck_ChanceAndRoll()
    {
        var e = Assert.IsType<AiOfferCheckEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_offer_check","turn_player":"S143PL0002","other":"S143PL0001","rel":95,"turn":1,"chance":100,"roll":57,"rng_before":10,"rng_after":11,"ret_addr":"0x4487dc","ret":true,"seq_end":9}"""));
        Assert.Equal(("S143PL0002", "S143PL0001", 95L, 1L, (long?)100, (long?)57), (e.TurnPlayer, e.Other, e.Rel, e.Turn, e.Chance, e.Roll));
        Assert.Equal((10L, 11L, "0x4487dc", true, 9L), (e.RngBefore, e.RngAfter, e.RetAddr, e.Ret, e.SeqEnd));

        var cold = Assert.IsType<AiOfferCheckEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_offer_check","turn_player":null,"other":"S143PL0001","rel":50,"turn":3,"chance":null,"roll":null,"rng_before":10,"rng_after":10,"ret_addr":"0x4487dc","ret":false,"seq_end":8}"""));
        Assert.Equal(((string?)null, (long?)null, (long?)null), (cold.TurnPlayer, cold.Chance, cold.Roll));
    }

    [Fact] public void AiGiftCheck_KindFromActionClass()
    {
        var e = Assert.IsType<AiGiftCheckEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_gift_check","player":"S143PL0002","other":"S143PL0001","rel":80,"threshold":16,"roll":12,"kind_roll":2,"action":"gold","rng_before":10,"rng_after":13,"ret_addr":"0x448862","ret":true,"seq_end":9}"""));
        Assert.Equal(("S143PL0002", "S143PL0001", 80L, 16L, (long?)12, (long?)2, "gold"), (e.Player, e.Other, e.Rel, e.Threshold, e.Roll, e.KindRoll, e.Action));
        Assert.Equal((10L, 13L, "0x448862", true, 9L), (e.RngBefore, e.RngAfter, e.RetAddr, e.Ret, e.SeqEnd));

        var none = Assert.IsType<AiGiftCheckEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_gift_check","player":"S143PL0002","other":"S143PL0001","rel":70,"threshold":14,"roll":60,"kind_roll":null,"action":null,"rng_before":10,"rng_after":11,"ret_addr":"0x448862","ret":false,"seq_end":8}"""));
        Assert.Equal(((long?)null, (string?)null, false), (none.KindRoll, none.Action, none.Ret));

        var unread = Assert.IsType<AiGiftCheckEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_gift_check","player":null,"other":null,"rel":-1,"threshold":0,"roll":null,"kind_roll":null,"action":null,"rng_before":10,"rng_after":10,"ret_addr":"0x448862","ret":false,"seq_end":8}"""));
        Assert.Equal(((string?)null, (string?)null), (unread.Player, unread.Other));
    }

    [Fact] public void AiAnswer_AllianceAndTrade()
    {
        var a = Assert.IsType<AiAnswerEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_answer","kind":"alliance","player":"S143PL0003","proposer":"S143PL0001","rel":70,"gold":null,"price":null,"object":null,"roll":25,"accept":true,"rng_before":204,"rng_after":205,"ret_addr":"0x475813","ret":56,"seq_end":12}"""));
        Assert.Equal(("alliance", "S143PL0003", "S143PL0001", (long?)70, (long?)25, (bool?)true), (a.Kind, a.Player, a.Proposer, a.Rel, a.Roll, a.Accept));
        Assert.Equal(((long?)null, (long?)null, (string?)null, (long?)56, 204L, 205L, "0x475813", 12L), (a.Gold, a.Price, a.Object, a.Ret, a.RngBefore, a.RngAfter, a.RetAddr, a.SeqEnd));

        var t = Assert.IsType<AiAnswerEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_answer","kind":"trade","player":"S143PL0003","proposer":"S143PL0001","rel":null,"gold":50,"price":null,"object":"G000SS0043","roll":null,"accept":null,"rng_before":204,"rng_after":204,"ret_addr":"0x47571f","ret":null,"seq_end":8}"""));
        Assert.Equal(("trade", (long?)50, (long?)null, "G000SS0043", (long?)null, (bool?)null), (t.Kind, t.Gold, t.Price, t.Object, t.Ret, t.Accept));
    }

    [Fact] public void AiStrategicStep_AndTurnBoundaries()
    {
        var s = Assert.IsType<AiStrategicStepEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"ai_strategic_step","player":"S143PL0002","id":"S143PL0001","type":8,"turn":3,"pass":2,"step":1,"rng_before":10,"rng_after":10,"ret_addr":"0x44eab8"}"""));
        Assert.Equal(("S143PL0002", "S143PL0001", 8L, 3L, 2L, 1L, 10L, 10L, "0x44eab8"), (s.Player, s.Id, s.Type, s.Turn, s.Pass, s.Step, s.RngBefore, s.RngAfter, s.RetAddr));

        var begin = Assert.IsType<AiTurnEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_turn","phase":"begin","player":"S143PL0002","turn":3,"cause":"ai","arg1":3,"arg2":0,"arg3":1,"pass_begins":null,"passes":null,"steps":null,"rng_before":5,"rng_after":9,"ret_addr":"0x471654","ret":null,"seq_end":2}"""));
        Assert.Equal(("begin", "S143PL0002", 3L, "ai", (long?)3, (long?)0, (long?)1), (begin.Phase, begin.Player, begin.Turn, begin.Cause, begin.Arg1, begin.Arg2, begin.Arg3));
        Assert.Equal(((long?)null, (bool?)null), (begin.Passes, begin.Ret));

        var end = Assert.IsType<AiTurnEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_turn","phase":"end","player":"S143PL0002","turn":3,"cause":"ai","arg1":null,"arg2":null,"arg3":null,"pass_begins":1,"passes":6,"steps":6,"rng_before":50,"rng_after":50,"ret_addr":"0x471bd5","ret":true,"seq_end":2}"""));
        Assert.Equal(((long?)1, (long?)6, (long?)6, (bool?)true, (long?)null), (end.PassBegins, end.Passes, end.Steps, end.Ret, end.Arg1));
    }

    [Fact] public void AiOrder_BudgetAndChat()
    {
        var o = Assert.IsType<AiOrderEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_order","stack":"S143KC0005","order":14,"order_name":"defend_city","target":"G000000000","flag":false,"apply":true,"order_before":1,"order_after":14,"ai_order_before":1,"ai_order_after":1,"via":"direct","cause":"strategic_orders","rng_before":3,"rng_after":3,"ret_addr":"0x44e6cc","ret":true,"seq_end":2}"""));
        Assert.Equal(("S143KC0005", 14L, "defend_city", "G000000000", false, true), (o.Stack, o.Order, o.OrderName, o.Target, o.Flag, o.Apply));
        Assert.Equal((1L, 14L, 1L, 1L, "direct", "strategic_orders", "0x44e6cc", true, 2L), (o.OrderBefore, o.OrderAfter, o.AiOrderBefore, o.AiOrderAfter, o.Via, o.Cause, o.RetAddr, o.Ret, o.SeqEnd));

        var b = Assert.IsType<AiBudgetEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_budget","player":"S143PL0002","type":2,"flag":false,"budget":{"gold":375,"infernal":0,"life":0,"death":0,"runic":0,"grove":0},"bank":{"gold":500,"infernal":0,"life":10,"death":0,"runic":0,"grove":0},"spell_bank":{"gold":0,"infernal":0,"life":0,"death":0,"runic":0,"grove":0},"rng_before":3,"rng_after":3,"ret_addr":"0x446457"}"""));
        Assert.Equal(("S143PL0002", 2L, false, "0x446457"), (b.Player, b.Type, b.Flag, b.RetAddr));
        Assert.Equal(375L, b.Budget!.Gold);
        Assert.Equal((500L, 10L), (b.Purse!.Gold, b.Purse.Life));
        Assert.Equal(0L, b.SpellBank!.Gold);
        var unread = Assert.IsType<AiBudgetEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_budget","player":null,"type":2,"flag":false,"budget":null,"bank":null,"spell_bank":null,"rng_before":3,"rng_after":3,"ret_addr":"0x446457"}"""));
        Assert.Equal(((string?)null, (Bank?)null, (Bank?)null), (unread.Player, unread.Budget, unread.SpellBank));

        var c = Assert.IsType<AiChatEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_chat","from":"S143PL0002","to":"S143PL0001","category":4,"category_name":"AI_OFFERS_ALLIANCE","roll":1,"rng_before":3,"rng_after":4,"cause":"strategic","ret_addr":"0x44eba7","ret":true}"""));
        Assert.Equal(("S143PL0002", "S143PL0001", 4L, "AI_OFFERS_ALLIANCE", (long?)1, 3L, 4L, "strategic", "0x44eba7", true),
            (c.From, c.To, c.Category, c.CategoryName, c.Roll, c.RngBefore, c.RngAfter, c.Cause, c.RetAddr, c.Ret));
    }
}
