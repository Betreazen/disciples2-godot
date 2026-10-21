using D2.Data.Corpus;
using D2.Rules.Map;

/// <summary>Схема 6.5: события экономики и тактики ИИ разбираются в свои записи, ни одно не уходит в
/// <see cref="UnknownEvent"/>. Строки — по форматам <c>probeaieco.cpp</c> и <c>probeaitac.cpp</c>; живых
/// журналов 6.5 ещё нет.</summary>
public class CorpusReaderV65Tests
{
    private const string Gold = """{"gold":100,"infernal":0,"life":0,"death":0,"runic":0,"grove":0}""";

    private static CorpusEvent One(string line)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, line + "\n");
        try { return CorpusReader.Read(path).Single(); }
        finally { File.Delete(path); }
    }

    [Fact] public void AiEval_KnownObjectFieldsAndUnknownVftable()
    {
        var e = Assert.IsType<AiEvalEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_eval","player":"S143PL0002","pass":3,"list":"hire_leader","eval":{"vft":"0x6cf71c","class":"eval_hire_leader","type":0,"id4":"S143FT0001","id8":null,"d16":12.5,"cat32":2,"raw":"00ff"},"ret_addr":"0x44ac50"}"""));
        Assert.Equal(("S143PL0002", 3L, "hire_leader", "0x44ac50"), (e.Player, e.Pass, e.List, e.RetAddr));
        Assert.Equal(("0x6cf71c", "eval_hire_leader", (long?)0, "00ff"), (e.Eval!.Vft, e.Eval.Class, e.Eval.Type, e.Eval.Raw));
        Assert.Equal("S143FT0001", e.Eval.Fields["id4"].GetString());
        Assert.Equal(12.5, e.Eval.Fields["d16"].GetDouble());
        Assert.Equal(4, e.Eval.Fields.Count);

        var unknown = Assert.IsType<AiEvalEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_eval","player":null,"pass":1,"list":null,"eval":{"vft":"0x1","class":null,"type":null,"raw":null},"ret_addr":"0x1"}"""));
        Assert.Equal(((string?)null, (string?)null, (long?)null), (unknown.Player, unknown.Eval!.Class, unknown.Eval.Type));
        Assert.Empty(unknown.Eval.Fields);
    }

    [Fact] public void AiCandidate_ExecAndBudgetSpend()
    {
        var c = Assert.IsType<AiCandidateEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_candidate","player":"S143PL0002","pass":1,"idx":0,"chosen":true,"action":{"vft":"0x6cf7f4","class":"hire_unit","type":2,"id4":"S143FT0001","id8":"G000UU0001","id12":null,"raw":"aa"},"score":7400,"count_after":1,"ret_addr":"0x44c46e"}"""));
        Assert.Equal(((long?)0, (bool?)true, 7400.0, (long?)1), (c.Idx, c.Chosen, c.Score, c.CountAfter));
        Assert.Equal("hire_unit", c.Action!.Class);

        var x = Assert.IsType<AiExecEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_exec","player":"S143PL0002","pass":1,"fn":"0x44c6c6","action":null,"rng_before":4,"rng_after":4,"ret_addr":"0x44ea88","ret":true,"seq_end":9}"""));
        Assert.Equal(("0x44c6c6", (AiObject?)null, 4L, 4L, true, 9L), (x.Fn, x.Action, x.RngBefore, x.RngAfter, x.Ret, x.SeqEnd));

        var b = Assert.IsType<AiBudgetSpendEvent>(One($$"""{"seq":1,"tid":1,"t":2,"ev":"ai_budget_spend","player":"S143PL0002","pass":1,"type":2,"reserve":0,"budget_before":{{Gold}},"budget_after":null,"start_before":{{Gold}},"start_after":{{Gold}},"spell_start_before":null,"spell_start_after":null,"ret_addr":"0x44eaca"}"""));
        Assert.Equal((2L, (long?)0, 100L, (Bank?)null, (Bank?)null), (b.Type, b.Reserve, b.BudgetBefore!.Gold, b.BudgetAfter, b.SpellStartAfter));
    }

    [Fact] public void AiCityRank_LeaderRolePickUnitAndBuild()
    {
        var r = Assert.IsType<AiCityRankEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_city_rank","player":"S143PL0002","pass":1,"count":2,"cities":[{"id":"S143FT0003","prio":40},{"id":"S143FT0001","prio":0}],"ret_addr":"0x44a8e7"}"""));
        Assert.Equal([new AiCityRank("S143FT0003", 40), new AiCityRank("S143FT0001", 0)], r.Cities);

        var role = Assert.IsType<AiLeaderRoleEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_leader_role","player":null,"pass":1,"fn":"fighter","b":[1,0,0],"i":[2,3,4],"f":[0.25,null,1],"j":[0,5,2],"ret_addr":"0x44aa0b","ret":1}"""));
        Assert.Equal(("fighter", 1L), (role.Fn, role.Ret));
        Assert.Equal([1L, 0L, 0L], role.B);
        Assert.Equal([0.25, null, 1.0], role.F);
        Assert.Equal([0L, 5L, 2L], role.J);

        var lp = Assert.IsType<AiLeaderPickEvent>(One($$"""{"seq":1,"tid":1,"t":2,"ev":"ai_leader_pick","player":"S143PL0002","pass":1,"id":"S143FT0001","bank":{{Gold}},"role":0,"flag":false,"result":"G000UU5001","roll":2,"rng_before":10,"rng_after":11,"ret_addr":"0x44aaa1","seq_end":5}"""));
        Assert.Equal(("G000UU5001", (long?)2, 100L, false), (lp.Result, lp.Roll, lp.Bank!.Gold, lp.Flag));

        var up = Assert.IsType<AiUnitPickEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_unit_pick","player":"S143PL0002","pass":1,"id":"S143FT0001","filter":"melee_small","filter_vft":"0x6cf794","list_len":5,"result":"G000UU0001","rng_std_before":7,"rng_std_after":8,"ret_addr":"0x44bf40","seq_end":3}"""));
        Assert.Equal(("melee_small", "0x6cf794", (long?)5, "G000UU0001", 7L, 8L), (up.Filter, up.FilterVft, up.ListLen, up.Result, up.RngStdBefore, up.RngStdAfter));

        var s1 = Assert.IsType<AiBuildPickEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_build_pick","player":"S143PL0002","pass":1,"strategy":"s1","id":"S143PL0002","result":"G000BB0001","status":null,"ret_addr":"0x446d48"}"""));
        Assert.Equal(("s1", "G000BB0001", (long?)null), (s1.Strategy, s1.Result, s1.Status));
        var temple = Assert.IsType<AiBuildPickEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_build_pick","player":null,"pass":1,"strategy":"temple","id":"S143PL0002","result":null,"status":2,"ret_addr":"0x446d2c"}"""));
        Assert.Equal(("temple", (string?)null, (long?)2), (temple.Strategy, temple.Result, temple.Status));
    }

    [Fact] public void AiRng_AndCrtRand()
    {
        var r = Assert.IsType<AiRngEvent>(One("""{"seq":5,"tid":7,"t":2,"ev":"ai_rng","site":"unit_pick","kind":"crt","n":12,"max":5,"r":3,"raw":20763,"battle":false,"ret_addr":"0x44c13c"}"""));
        Assert.Equal(("unit_pick", "crt", 12L, 5L, 3L, (long?)20763, false, 7L), (r.Site, r.Kind, r.N, r.Max, r.R, r.Raw, r.Battle, r.Tid));
        var r250 = Assert.IsType<AiRngEvent>(One("""{"seq":5,"tid":7,"t":2,"ev":"ai_rng","site":"leader_pick","kind":"r250","n":40,"max":3,"r":1,"raw":null,"battle":false,"ret_addr":"0x44aee6"}"""));
        Assert.Null(r250.Raw);

        var c = Assert.IsType<CrtRandEvent>(One("""{"seq":6,"tid":7,"t":2,"ev":"crt_rand","n":1,"r":41,"ret_addr":"0x548587"}"""));
        Assert.Equal((1L, 41L, "0x548587"), (c.N, c.R, c.RetAddr));
    }

    private const string Action = """{"ptr":"0x1234","vft":"0x6d0000","type":3,"stack":"S143KC0001","target":"S143KC0002","point":{"x":5,"y":7}}""";

    [Fact] public void AiTacticCandidate_AdjustAndPick()
    {
        var c = Assert.IsType<AiTacticCandidateEvent>(One($$"""{"seq":1,"tid":1,"t":2,"ev":"ai_tactic_candidate","player":"S143PL0002","action":{{Action}},"score_in":100,"idx":0,"inserted":true,"score":125,"ret_addr":"0x452d93"}"""));
        Assert.Equal(((long?)3, "S143KC0002", new AiPoint(5, 7)), (c.Action!.Type, c.Action.Target, c.Action.Point));
        Assert.Equal((100.0, (long?)0, (bool?)true, 125.0), (c.ScoreIn, c.Idx, c.Inserted, c.Score));

        var dropped = Assert.IsType<AiTacticCandidateEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_tactic_candidate","player":null,"action":{"ptr":"0x1","vft":"0x1","type":null,"stack":null,"target":null,"point":null},"score_in":0,"idx":2,"inserted":false,"score":null,"ret_addr":"0x1"}"""));
        Assert.Equal(((bool?)false, (double?)null, (AiPoint?)null), (dropped.Inserted, dropped.Score, dropped.Action!.Point));

        var a = Assert.IsType<AiTacticAdjustEvent>(One($$"""{"seq":1,"tid":1,"t":2,"ev":"ai_tactic_adjust","player":"S143PL0002","kind":"priority","stack":"S143KC0001","action":{{Action}},"priority":5,"before":100,"after":166,"ret_addr":"0x466899"}"""));
        Assert.Equal(("priority", (long?)5, 100.0, 166.0), (a.Kind, a.Priority, a.Before, a.After));

        var p = Assert.IsType<AiTacticPickEvent>(One($$"""{"seq":1,"tid":1,"t":2,"ev":"ai_tactic_pick","player":"S143PL0002","phase_before":3,"phase_after":4,"stack":"S143KC0001","stack_after":"S143KC0001","count":2,"top":[{"action":{{Action}},"score":166},{"action":null,"score":null}],"chosen":null,"rng_before":1,"rng_after":1,"ret_addr":"0x4665bc","ret":true,"seq_end":4}"""));
        Assert.Equal((3L, 4L, (long?)2, true, 4L), (p.PhaseBefore, p.PhaseAfter, p.Count, p.Ret, p.SeqEnd));
        Assert.Equal(2, p.Top!.Count);
        Assert.Equal(166.0, p.Top[0].Score);
        Assert.Null(p.Top[1].Action);
        Assert.Null(p.Chosen);

        var s = Assert.IsType<AiTacticStackEvent>(One("""{"seq":2,"tid":1,"t":2,"ev":"ai_tactic_stack","player":"S143PL0002","stack":"S143KC0003","pick_seq":1}"""));
        Assert.Equal(("S143KC0003", 1L), (s.Stack, s.PickSeq));
        Assert.Null(s.Inside);
        Assert.Null(s.Pos);

        var s71 = Assert.IsType<AiTacticStackEvent>(One("""{"seq":2,"tid":1,"t":2,"ev":"ai_tactic_stack","player":"S143PL0002","stack":"S143KC0003","inside":"S143FT0001","pos":{"x":5,"y":7},"pick_seq":1}"""));
        Assert.Equal(("S143FT0001", new AiPoint(5, 7)), (s71.Inside, s71.Pos));
    }

    [Fact] public void AiHireScoreAndGrid()
    {
        var h = Assert.IsType<AiHireScoreEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_hire_score","player":"S143PL0002","stack":"S143KC0003","fort":"S143FT0001","score":12.5,"free":2,"removable":1,"can_hire":true,"ret_addr":"0x45a158"}"""));
        Assert.Equal(("S143KC0003", "S143FT0001", (double?)12.5, (long?)2, (long?)1, (bool?)true),
                     (h.Stack, h.Fort, h.Score, h.Free, h.Removable, h.CanHire));

        var g = Assert.IsType<AiGridEvent>(One("""{"seq":3,"tid":1,"t":2,"ev":"ai_grid","player":"S143PL0002","owner":"S143PL0002","grid":"0x100","ret_addr":"0x4658c1","seq_end":6}"""));
        Assert.Equal(("S143PL0002", 6L), (g.Owner, g.SeqEnd));
        var f = Assert.IsType<AiGridFortEvent>(One("""{"seq":4,"tid":1,"t":2,"ev":"ai_grid_fort","player":"S143PL0002","grid":"0x100","center":{"x":3,"y":4},"radius":15}"""));
        Assert.Equal((new AiPoint(3, 4), 15L), (f.Center, f.Radius));
    }

    [Fact] public void AiTacticResult_RodCellAndStrength()
    {
        var r = Assert.IsType<AiTacticResultEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_tactic_result","player":null,"stack":"S143KC0001","target":"S143KC0002","point":{"x":1,"y":2},"turn":4,"turn_ptr":"0x5000","status":5,"ret_addr":"0x466527"}"""));
        Assert.Equal((new AiPoint(1, 2), (long?)4, "0x5000", 5L), (r.Point, r.Turn, r.TurnPtr, r.Status));

        var rod = Assert.IsType<AiRodCellEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_rod_cell","player":"S143PL0002","stack":"S143KC0001","crystal":"S143CR0001","crystal_pos":{"x":10,"y":10},"result":{"x":11,"y":10},"cells":[{"x":9,"y":9,"ok_place":false,"ok_ally":null,"ok_block":null,"dist":null,"chosen":false},{"x":11,"y":10,"ok_place":true,"ok_ally":true,"ok_block":true,"dist":2,"chosen":true}],"ret_addr":"0x461415","ret":true}"""));
        Assert.Equal((new AiPoint(10, 10), new AiPoint(11, 10), true), (rod.CrystalPos, rod.Result, rod.Ret));
        Assert.Equal([new AiRodCell(9, 9, false, null, null, null, false), new AiRodCell(11, 10, true, true, true, 2, true)], rod.Cells);

        var st = Assert.IsType<AiStrengthEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_strength","player":null,"p1":"0x1","p2":"0x2","p4":"0x3","p2_n":null,"p4_n":6,"a":300,"b":400,"c":0.5,"ret_addr":"0x45375a","ret":0.6666666666666666}"""));
        Assert.Equal((300.0, 400.0, 0.5, (long?)6), (st.A, st.B, st.C, st.P4N));
        Assert.Equal(2.0 / 3.0, st.Ret!.Value, 12);
        Assert.Null(st.P2Units);

        var st71 = Assert.IsType<AiStrengthEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ai_strength","player":null,"p1":"0x1","p2":"0x2","p4":"0x3","p2_n":2,"p4_n":0,"p2_units":["S143UN0001","S143UN0002"],"p4_units":[],"a":1,"b":2,"c":0,"ret_addr":"0x45375a","ret":1}"""));
        Assert.Equal(["S143UN0001", "S143UN0002"], st71.P2Units!);
        Assert.Empty(st71.P4Units!);

        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":1,"tid":1,"t":2,"ev":"ai_strength","player":null}"""));
    }
}
