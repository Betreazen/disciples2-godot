namespace D2.Data.Tests;

/// <summary>Числа сверок по видам строк — измерено 17.10.2026 на 20 каталогах корпуса, 19.10 — 21 (<see cref="Runs"/>; партия с покрытием <c>20261019c-cov</c>: строк 1592,
/// расхождений 0): 15 обычных (с
/// <c>20261016e-av</c>: greenskins и карты 5–8 под зондом 7.3) — в фактах ниже, 6 партий синтетических карт — в
/// <see cref="Synth_MatchesJournal"/>; построчно по партиям — печать теста. «Совпало после» — ответ по
/// сохранению «до» разошёлся, по сохранению «после» сошёлся (состояние сменилось между ними: надетый предмет, выросший уровень,
/// перестановка без строки зонда). <c>D2_CHECKS_SHOW=N</c> печатает до N расхождений вида.</summary>
[Trait("Category", "Corpus")]
public partial class HumanChecksHarnessTests
{
    private (int Same, int SameAfter, int Differ, int NoState) Kind(string kind, int rows, bool synth = false)
    {
        Skip.IfNot(Runs.All(r => Directory.Exists(Path.Combine(GameSaveTests.CorpusDir(), r))), "нет корпуса (числа — по всем 22 каталогам)");
        var r = Report(kind, synth);
        Assert.Equal(rows, r.Same + r.SameAfter + r.Differ + r.NoState);
        Assert.Equal(0, r.Differ);
        return r;
    }

    /// <summary>
    /// <c>equip_check</c> (<c>0x5df6b7</c>) — <see cref="Play.GameSession.EquipCheck"/>: подготовка отряда ИИ (<c>0x452126</c>) и сервер
    /// надевания (<c>0x5d6b80</c>), все коды 0, 2–9, 0xB. Найдено и исправлено 16.10: «ничего не даст» (0xB) у сапог для летуна — бонусы хода
    /// лидера берутся и из <c>GMabi</c> (<see cref="Play.GameSession.LeaderEffectsOf"/>; было 7 расхождений).
    /// </summary>
    [SkippableFact]
    public void Equip_MatchesJournal()
    {
        var r = Kind("equip_check", 6101);
        Assert.Equal(5944, r.Same);
    }

    /// <summary><c>swap_check</c> (<c>0x5df4fb</c>) — рамка перетаскивания порта (<see cref="Ui.Windows.DropFrames.Unit"/>, коды 0, 2, 3, 0xB)
    /// и перенос рукой человека (<c>unit_transfer</c> <c>swap_msg</c>) — <see cref="Play.GameSession.MoveInGroup"/> и
    /// <see cref="Play.GameSession.ExchangeUnits"/> с клетками после вызова как у движка.</summary>
    [SkippableFact]
    public void Swap_AndTransfers_MatchJournal()
    {
        var swap = Kind("swap_check", 462);
        var moves = Kind("unit_transfer", 42);
        Assert.Equal(156, swap.Same);
        Assert.Equal(16, moves.Same);
    }

    /// <summary><c>hire_check</c> (<c>0x5deea7</c>) и <c>hire_leader_check</c> (<c>0x5de921</c>) — <see cref="Play.GameSession.HireUnit"/> и
    /// <see cref="Play.GameSession.HireLeader"/> на копии партии: окно столицы, сервер и ИИ, коды 0 и 7. Состояние — с расстановкой
    /// гарнизона в начале стратегии ИИ, ростом деревни и наймами того же хода (<see cref="Arranged"/>, <see cref="Replayed"/>).</summary>
    [SkippableFact]
    public void Hire_MatchesJournal()
    {
        var units = Kind("hire_check", 903);
        var leaders = Kind("hire_leader_check", 210);
        Assert.Equal(891, units.Same);
        Assert.Equal(208, leaders.Same);
    }

    /// <summary><c>grow_check</c> (<c>0x5dc62a</c>) — <see cref="Play.GameSession.GrowCity"/>: ИИ (<c>0x44982a</c>, <c>0x4499ef</c>) и
    /// сервер (<c>0x5da03c</c>), коды 0, 3, 4, 7, 0xB; мятеж, дошедший до <c>RIOT_T</c>, к проверке хода ИИ уже снят.</summary>
    [SkippableFact]
    public void Grow_MatchesJournal()
    {
        var r = Kind("grow_check", 669);
        Assert.Equal(666, r.Same);
    }

    /// <summary><c>rod_check</c> (<c>0x5d9e59</c>) — <see cref="Play.GameSession.PlaceRod"/>: курсор постановки человека (<c>0x4cdbe7</c>,
    /// коды 0, 7, 8, 9), приказ и эффект постановки (<c>0x440d1f</c>, <c>0x43765c</c>); клетки отрядов — по <c>stack_move</c>, жезлы — по
    /// <c>rod_drop</c> между сохранением и проверкой.</summary>
    [SkippableFact]
    public void Rod_MatchesJournal()
    {
        var r = Kind("rod_check", 858);
        Assert.Equal(837, r.Same);
    }

    /// <summary><c>wand_check</c> (<c>0x5dbdd2</c>) — <see cref="Play.GameSession.ItemCastRefusal"/>: наведение посоха и свитка человека
    /// (<c>0x4cd5ff</c>: 0, 2 — своя крепость или свой отряд, 6 — нет цели, 8 — туман) и сервер <c>CEffectWand</c> (<c>0x5dc4fa</c>).</summary>
    [SkippableFact]
    public void Wand_MatchesJournal()
    {
        var r = Kind("wand_check", 154);
        Assert.Equal(154, r.Same);
    }

    /// <summary><c>merc_check</c> (<c>0x5e00c6</c>) и <c>sell_check</c> (<c>0x5dfe1a</c>) — <see cref="Play.GameSession.HireMercenary"/> и
    /// <see cref="Play.GameSession.SellItem"/>; в корпусе только согласия.</summary>
    [SkippableFact]
    public void MercAndSell_MatchJournal()
    {
        Assert.Equal(1, Kind("merc_check", 1).Same);
        Assert.Equal(12, Kind("sell_check", 28).Same);
    }

    /// <summary><c>ai_answer</c> — ответ ИИ на предложение союза (<c>0x474b37</c>) и сделки (<c>0x474841</c>) от человека и ИИ: шанс порта
    /// (отношение строки, доля равнин и цена — из сохранения) и согласие. R250 движка в миг ответа (<see cref="EngineRngAt"/>: вызов броска —
    /// <c>rng_before + 1</c>, опоры — строки <c>rng</c> и броски дипломатии ИИ в полях строк) восстанавливается у 24 строк из 30 — там согласие
    /// даёт сам <see cref="Play.GameSession.ProposeTradeBy"/>/<see cref="Play.GameSession.ProposeAllianceBy"/>; у прочих 6 (до первой опоры
    /// после сева: legions 2, replay 4) — бросок журнала против шанса порта. По <c>rng_before</c> без <c>+ 1</c> генератор сходился у 1
    /// строки (реестр Зс12).</summary>
    [SkippableFact]
    public void AiAnswers_MatchJournal()
    {
        var r = Kind("ai_answer", 32);
        Assert.Equal(31, r.Same);
        int restored = 0;
        foreach (string path in Runs.Where(x => !CorpusSets.IsSynth(x)).Select(x => Path.Combine(GameSaveTests.CorpusDir(), x)).Where(Directory.Exists)
                                    .SelectMany(d => Directory.GetFiles(d, "passive-*.jsonl")))
        {
            var j = CheckJournal.Load(path);
            foreach (var q in j.Events.OfType<Corpus.AiAnswerEvent>().Where(q => q.Ret is not null))
            {
                if (EngineRngAt(j, q) is not null) restored++;
                else output.WriteLine($"  без R250 движка: {Path.GetFileName(Path.GetDirectoryName(path))} seq {q.Seq} {q.Kind} rng_before {q.RngBefore}");
            }
        }
        output.WriteLine($"генератор движка восстановлен у {restored} строк");
        Assert.Equal(26, restored);
    }

    /// <summary>Партии синтетических карт (<c>*-port-synth</c>, <see cref="CorpusSets.IsSynth"/>): все виды разом, строк и «совпало» на вид. <c>20261017-port-synth</c>
    /// — первый журнал зонда 7.4 (ключи по видам, сброс на входе в партию): строки проверок на всех трёх картах (10–12), в том числе первые
    /// в корпусе <c>dismiss_check</c> — отпуск в столице карты 11 (<c>0x502314</c> окно армии: коды 0, 2 страж, 4 пустая клетка;
    /// <c>0x5025eb</c>, сервер <c>0x5d5797</c>) — 8 из 8. У <c>rod_check</c> и <c>wand_check</c> остаток до числа строк — «совпало после» (2 и 1).
    /// 20.10 (r20/batB): карта 23 (<c>20261020-port-synth</c>, 60 дней) — строк 2230, расхождений 0: <c>equip_check</c> 654 (совпало 607,
    /// после 31, нет состояния 16 — предметы, созданные и переданные посреди дня), <c>grow_check</c> 1098, <c>hire_check</c> 318,
    /// <c>hire_leader_check</c> 18, <c>rod_check</c> 142 (совпало 122, после 20).</summary>
    [SkippableTheory, Trait("Category", "Synth")]
    [InlineData("equip_check", 1270, 1223)]
    [InlineData("swap_check", 36, 36)]
    [InlineData("dismiss_check", 8, 8)]
    [InlineData("unit_transfer", 2, 2)]
    [InlineData("hire_check", 914, 914)]
    [InlineData("hire_leader_check", 58, 58)]
    [InlineData("grow_check", 1218, 1218)]
    [InlineData("rod_check", 173, 151)]
    [InlineData("wand_check", 336, 335)]
    [InlineData("merc_check", 8, 8)]
    [InlineData("sell_check", 0, 0)]
    [InlineData("ai_answer", 0, 0)]
    public void Synth_MatchesJournal(string kind, int rows, int same) => Assert.Equal(same, Kind(kind, rows, synth: true).Same);
}
