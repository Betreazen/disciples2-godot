using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Задача 6 Ф7 — платное лечение, воскрешение и дневная регенерация (<see cref="Healing"/>,
/// <see cref="Regeneration"/>). Модульные тесты — на числах прогонов; харнесс — <c>regen_tick</c>,
/// <c>regen_dead_zero</c>, <c>heal_hp</c>, <c>revive_hp1</c> против следующего сохранения.</summary>
[Trait("Category", "Corpus")]
public class RegenHarnessTests(ITestOutputHelper output)
{
    private static readonly Bank Gold1 = Bank.Parse("g0001"), Gold3 = Bank.Parse("g0003");

    // ---- модульные ------------------------------------------------------------------------------------

    /// <summary><c>f7-m16-regen</c> (план Ф7 П§14): лидер HPmax 150 и Squire 100 (<c>REGEN</c> 5), лорд Маг, HP 8 и 6;
    /// столица 35 %, города 10…30 %. Три строки лидера различают «своё округление укрепления» (59/29/44).</summary>
    [Theory]
    [InlineData(150, 8, 35, 67)] [InlineData(100, 6, 35, 46)]
    [InlineData(150, 8, 10, 30)] [InlineData(100, 6, 10, 21)]
    [InlineData(150, 8, 15, 37)] [InlineData(100, 6, 15, 26)]
    [InlineData(150, 8, 20, 45)] [InlineData(100, 6, 20, 31)]
    [InlineData(150, 8, 25, 52)] [InlineData(100, 6, 25, 36)]
    [InlineData(150, 8, 30, 60)] [InlineData(100, 6, 30, 41)]
    public void Tick_FortTermRoundsSeparately_M16(int hpMax, int hp, int fort, int expected) =>
        Assert.Equal(expected, Regeneration.Tick(hp, hpMax, Regeneration.UnitPercent(5, 0, true, false, 15), fort));

    /// <summary>Прогон 2, M4: под Воителем Squire 50→70, Holy Avenger 1→51 (5 + 15 до округления: +50, а не 12 + 37),
    /// Imperial Knight 1→41; под Магом и Повелителем Воров 55 / 13 / 11.</summary>
    [Theory]
    [InlineData(100, 50, 1, 70)] [InlineData(250, 1, 1, 51)] [InlineData(200, 1, 1, 41)]
    [InlineData(100, 50, 0, 55)] [InlineData(250, 1, 0, 13)] [InlineData(200, 1, 2, 11)]
    public void Tick_WarlordBonusAddsBeforeRounding_Run2(int hpMax, int hp, int lordCategory, int expected) =>
        Assert.Equal(expected, Regeneration.Tick(hp, hpMax, Regeneration.UnitPercent(5, lordCategory, true, false, 15), 0));

    /// <summary>Первая большая партия: отряды ИИ под Воителем в поле на своей земле — 30 % (1 → 46 при HPmax 150,
    /// 57 → 84 при 90, 1 → 34 при 110); нейтралы на своей земле — 15 % (52 → 61 при 65).</summary>
    [Theory]
    [InlineData(150, 1, 1, true, 46)] [InlineData(90, 57, 1, true, 84)] [InlineData(110, 1, 1, true, 34)]
    [InlineData(65, 52, -1, false, 61)]
    public void Tick_OwnLandAddsTen_Big1(int hpMax, int hp, int lordCategory, bool playable, int expected) =>
        Assert.Equal(expected, Regeneration.Tick(hp, hpMax,
            Regeneration.UnitPercent(5, lordCategory, playable, false, 15, onOwnLand: true), 0));

    [Fact]
    public void Tick_DeadStaysDead_FullStaysFull_CapturedWarlordGivesNothing()
    {
        Assert.Equal(0, Regeneration.Tick(0, 225, 20, 35));
        Assert.Equal(100, Regeneration.Tick(99, 100, 5, 35));
        Assert.Equal(5, Regeneration.UnitPercent(5, Regeneration.WarlordCategory, playable: true, captured: true, 15));
        Assert.Equal(5, Regeneration.UnitPercent(5, Regeneration.WarlordCategory, playable: false, captured: false, 15));
    }

    [SkippableFact]
    public void FortPercents_FromTables()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var economy = EconomyLoader.Load(GamePaths.Globals);
        Assert.Equal(35, Regeneration.CapitalPercent(economy.Races["g000rr0000"]));
        Assert.Equal([10, 15, 20, 25, 30], Enumerable.Range(1, 5).Select(s => Regeneration.VillagePercent(economy, "g000rr0000", s, 0, false)));
        Assert.Equal(25, Regeneration.VillagePercent(economy, "g000rr0000", 3, 5, rioting: false));
        Assert.Equal(0, Regeneration.VillagePercent(economy, "g000rr0000", 3, 5, rioting: true));
        Assert.Equal((15, 25), (EconomyLoader.LoadVars(GamePaths.Globals).WarlordRegen, EconomyLoader.LoadVars(GamePaths.Globals).WeaponMaster));
        Assert.True(Healing.HasTemple(["G000BB0023"], economy.Buildings));
        Assert.False(Healing.HasTemple(["G000BB0024"], economy.Buildings));
    }

    /// <summary>Прогон 2: Ангел HPmax 225 после воскрешения (HP 1), <c>HEAL_C</c> 3, в казне 255 — 85 HP, 86/225;
    /// в казне 28 — 9 HP, остаётся 1. Squire 60/100 при <c>HEAL_C</c> 1 — ровно 40.</summary>
    [Fact]
    public void Heal_FullAndPartial_Run2()
    {
        Assert.Equal(new HealResult(86, Bank.Zero), Healing.Heal(1, 225, Gold3, Bank.Parse("g0255"), true, true));
        Assert.Equal(new HealResult(10, Gold1), Healing.Heal(1, 225, Gold3, Bank.Parse("g0028"), true, true));
        Assert.Equal(new HealResult(100, Bank.Parse("g1960")), Healing.Heal(60, 100, Gold1, Bank.Parse("g2000"), true, true));
        Assert.NotNull(Healing.Heal(0, 225, Gold3, Bank.Parse("g2000"), true, true).Refused);
        Assert.NotNull(Healing.Heal(100, 100, Gold1, Bank.Parse("g2000"), true, true).Refused);
        Assert.NotNull(Healing.Heal(60, 100, Gold1, Bank.Parse("g2000"), false, true).Refused);
        Assert.NotNull(Healing.Heal(60, 100, Gold1, Bank.Parse("g2000"), true, false).Refused);
    }

    /// <summary>Прогон 2: воскрешение Ангела — плоские 600, HP 1; коды отказа <c>0x5DE06E</c>.</summary>
    [Fact]
    public void Revive_FlatPriceHpOne_AndRefusalCodes()
    {
        var price = Bank.Parse("g0600");
        Assert.Equal(new HealResult(1, Bank.Parse("g1400")), Healing.Revive(0, price, Bank.Parse("g2000"), true, true));
        Assert.Equal(ReviveCheck.Alive, Healing.CheckRevive(5, Bank.Parse("g2000"), price, true, true));
        Assert.Equal(ReviveCheck.NotEnoughBank, Healing.CheckRevive(0, Bank.Parse("g0599"), price, true, true));
        Assert.Equal(ReviveCheck.NoTemple, Healing.CheckRevive(0, Bank.Parse("g2000"), price, false, true));
        Assert.Equal(ReviveCheck.NotInFortification, Healing.CheckRevive(0, Bank.Parse("g2000"), price, true, false));
    }

    // ---- харнесс ---------------------------------------------------------------------------------------

    /// <summary>Регенерация по парам сохранений (с журналом — окно между срезами <see cref="JournalSaves"/>, без
    /// журнала — <see cref="LooseSaves"/>) и цепочки лечения/воскрешения против следующего сохранения.</summary>
    [SkippableFact]
    public void Regen_HealAndRevive_MatchNextSave()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var audit = new RegenAudit(ProgressBook.Load(GamePaths.Globals), new Counters());
        var (unique, _) = JournalSet.Distinct(CorpusFiles.Journals());
        foreach (var path in unique.Where(TreasuryJournal.HasBank))
        {
            var j = TreasuryJournal.Load(path);
            if (j.Saves.Count == 0) { audit.C.Skip("heal_hp", $"у журнала нет сохранений в манифесте ({Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)})"); continue; }
            var points = JournalSaves.Of(j);
            foreach (var (a, b) in JournalSaves.Pairs(points)) audit.Pair(a.Save, b.Save, j, JournalSaves.Between(j, a.Cut, b.Cut));
            audit.Chains(j, points);
        }
        foreach (var (a, b) in LooseSaves.Pairs()) audit.Pair(a, b, null, null);
        audit.C.Print(output, "регенерация, лечение, воскрешение (задача 6 Ф7)");
        audit.C.Require("regen_tick", "regen_dead_zero", "heal_hp", "revive_hp1");
    }

    /// <summary>
    /// Конец мятежа по строкам журнала (а не по сохранениям — их заслоняет платное лечение ИИ): удар <c>hp_change</c> (флаг 1,
    /// урон &gt; 0) в начале хода владельца, между его <c>spell_expire</c> и <c>income</c>. <c>riot_hit_before_heal</c> — урон равен
    /// <see cref="Regeneration.RiotDamage"/> от HP до лечения (первая строка <c>regen</c> юнита в этом начале хода, без неё — HP перед
    /// ударом); <c>riot_end_heals</c> — раненый перед ударом вылечен строкой <c>regen</c> (деревня в день сброса лечит). Справочно —
    /// прежняя модель «20 % от HP после лечения». Ранг 1: <c>20261016b-port-synth</c>, карта 2, R1 (80 → 132 → 116, 40 → 62 → 54).
    /// </summary>
    [SkippableFact]
    public void RiotEnd_HealsFirst_HitFromHpBeforeHeal()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        int pct = ProgressBook.Load(GamePaths.Globals).ThiefRules.RiotDamage;
        var c = new Counters();
        var (unique, _) = JournalSet.Distinct(CorpusFiles.Journals());
        foreach (var path in unique.Where(TreasuryJournal.HasBank))
        {
            var j = TreasuryJournal.Load(path);
            string file = $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";
            SpellExpireEvent? start = null;
            var regen = new Dictionary<string, RegenEvent>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in j.TurnStarts.OrderBy(e => e.Seq))
                switch (e)
                {
                    case SpellExpireEvent x: start = x; regen.Clear(); break;
                    case IncomeEvent i when start is not null && Same(i.Player, start.Player): start = null; break;
                    case RegenEvent r when start is not null && r.Unit is { } u: regen.TryAdd(u, r); break;
                    case HpChangeEvent h when start is not null && h.Flag == 1 && h.Amount > 0:
                        var healed = regen.GetValueOrDefault(h.Unit);
                        int before = (int)(healed?.HpBefore ?? h.HpBefore);
                        string detail = $"{file} seq {h.Seq} {h.Unit}: HP до лечения {before}, перед ударом {h.HpBefore}, удар {h.Amount}";
                        c.Count("riot_hit_before_heal", h.Amount == Regeneration.RiotDamage(before, pct), detail);
                        c.Count("riot_hit · гипотеза «от HP после лечения»", h.Amount == Regeneration.RiotDamage((int)h.HpBefore, pct), detail);
                        // Зонд пишет regen только при лечении (213 строк корпуса, ни одной у здорового): без строки юнит был здоров.
                        if (healed is null) c.Skip("riot_end_heals", "строк regen нет — юнит был здоров");
                        else c.Count("riot_end_heals", healed.HpAfter > healed.HpBefore, detail);
                        c.Note("   " + detail);
                        break;
                }
        }
        c.Print(output, "конец мятежа: лечение и удар по строкам журнала");
        c.Require("riot_hit_before_heal", "riot_end_heals");
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Разбор харнесса задачи 6.</summary>
internal sealed class RegenAudit(ProgressBook book, Counters c)
{
    public Counters C => c;

    /// <summary>Карты, чьи значения приёмка задачи 6 называет поимённо (<c>m4-temple</c> 5, <c>m5-regen</c> 3,
    /// <c>m16-regen</c> 12) — строки <c>regen_tick</c> по ним печатаются дословно.</summary>
    private static readonly string[] NotedMaps = ["m4-temple", "m5-regen", "m16-regen"];

    private sealed record Place(string Owner, MapStack? Stack, MapSite? Fort, bool Garrison);

    private sealed record Subject(MapUnit A, MapUnit B, Place Place, MapPlayer Player, UnitView View, UnitCosts Costs, bool OwnLand);

    /// <summary>Одна пара сохранений: каждый юнит первого, у которого между ними ровно один тик, нет боя и лечения.</summary>
    public void Pair(SaveAt a, SaveAt b, TreasuryJournal? j, List<CorpusEvent>? window)
    {
        if (b.Map.Turn != a.Map.Turn + 1) { SkipRegen(a, "сохранения не соседних дней"); return; }
        foreach (var ua in a.Map.Units.Values)
        {
            if (SubjectOf(a, b, ua.Id, out var why) is not { } s) { SkipRegen(a, why); continue; }
            var reason = PotionGone(a, b, s) ?? (window is null ? LooseExclusion(b, s) : JournalExclusion(window, s));
            if (reason is not null) { SkipRegen(a, reason); continue; }
            // Ход тика — из строки income владельца в окне; без журнала — CUR_TURN второго снимка.
            // ponytail: без журнала ход тика берётся у снимка «после»; тик в другой день окно и так отсекает.
            var (unit, unitIfStack, fort, where, riotEnds) = Percents(s, TickTurn(window, s) ?? b.Map.Turn);
            // Удар конца мятежа — после прохода лечения (прогон 13.10, ai-q11: hp_change после regen и land_grow, перед income), от HP до
            // лечения (20261016b, карта 2, R1: 80 → 132, удар 16).
            int ticked = Regeneration.Tick(s.A.Hp, s.View.HpMax, unit, fort);
            int expected = riotEnds ? Math.Max(ticked - Regeneration.RiotDamage(s.A.Hp, book.ThiefRules.RiotDamage), 0) : ticked;
            string name = s.A.Hp == 0 ? "regen_dead_zero" : s.A.Hp >= s.View.HpMax ? "regen_full"
                        : s.Place.Garrison ? "regen_garrison" : "regen_tick";
            string detail = $"{File(a)} → {File(b)} {ua.Id} {ua.TypeId} {where}{(window is null ? " (без журнала)" : "")}: " +
                            $"HP {s.A.Hp}{(riotEnds ? $" (лечение {ticked}, затем конец мятежа)" : "")} → {s.B.Hp}, модель {expected} " +
                            $"(HPmax {s.View.HpMax}, юнит {unit} %, укрепление {fort} %)";
            c.Count(name, s.B.Hp == expected, detail);
            if (name == "regen_tick") c.Count($"regen_tick · {where}", s.B.Hp == expected, detail);
            if (name == "regen_tick" && s.OwnLand)
                c.Count("regen_tick · своя земля · гипотеза «без +10»",
                        s.B.Hp == Regeneration.Tick(s.A.Hp, s.View.HpMax, unit - Regeneration.OwnLandBonus, fort), detail);
            if (name == "regen_garrison")
            {
                int fortOnly = Regeneration.Tick(s.A.Hp, s.View.HpMax, 0, fort);
                c.Count("regen_garrison · гипотеза «только укрепление»", s.B.Hp == fortOnly, $"{detail}; только укрепление {fortOnly}");
            }
            if (name == "regen_tick" && NotedMaps.Any(m => File(a).Contains(m, StringComparison.OrdinalIgnoreCase))) c.Note("   " + detail);
        }
    }

    /// <summary>Пропуск <c>regen_tick</c>; у карт <see cref="NotedMaps"/> причина считается ещё и по карте.</summary>
    private void SkipRegen(SaveAt a, string reason)
    {
        c.Skip("regen_tick", reason);
        if (NotedMaps.FirstOrDefault(m => File(a).Contains(m, StringComparison.OrdinalIgnoreCase)) is { } map)
            c.Skip($"regen_tick {map}", reason);
    }

    /// <summary>Цепочки лечения и воскрешения: HP из сохранения до, тик, воскрешение (1 HP), лечение по казне
    /// <c>bank.before</c> — против сохранения после.</summary>
    public void Chains(TreasuryJournal j, IReadOnlyList<SavePoint> points)
    {
        var done = new HashSet<(string, long)>();
        foreach (var e in j.Events)
        {
            (string unitId, bool ret, long t, string name) = e switch
            {
                HealEvent h => (h.Unit, h.Ret, h.T, "heal_hp"),
                ReviveEvent r => (r.Unit, r.Ret, r.T, "revive_hp1"),
                PotionUseEvent { InBattle: false } p => (p.Target, p.Ret, p.T, "potion_heal_map"),
                _ => ("", false, 0L, ""),
            };
            if (name.Length == 0) continue;
            if (!ret) { c.Skip(name, "ret = false: движок отказал"); continue; }
            var a = JournalSaves.Before(points, e.Seq, j.EpochOf(t));
            var b = JournalSaves.After(points, e.Seq, j.EpochOf(t));
            if (a is null || b is null) { c.Skip(name, "нет сохранения до или после в той же загрузке карты"); continue; }
            if (done.Add((unitId, a.Cut))) Chain(j, a, b, unitId);
        }
    }

    private void Chain(TreasuryJournal j, SavePoint a, SavePoint b, string unitId)
    {
        var events = JournalSaves.Between(j, a.Cut, b.Cut);
        var names = events.Select(e => e switch
        {
            HealEvent { Ret: true } h when Same(h.Unit, unitId) => "heal_hp",
            ReviveEvent { Ret: true } r when Same(r.Unit, unitId) => "revive_hp1",
            PotionUseEvent { Ret: true, InBattle: false } p when Same(p.Target, unitId) => "potion_heal_map",
            _ => null,
        }).OfType<string>().ToList();
        string where = $"{File(a.Save)} → {File(b.Save)} {unitId}";
        if (SubjectOf(a.Save, b.Save, unitId, out var why) is not { } s) { SkipChain(names, where, why); return; }
        // Зелье, выпитое этим юнитом, цепочка ведёт сама (potion_use схемы 6.0); исчезновение — признак для журналов без него.
        if (!names.Contains("potion_heal_map") && PotionGone(a.Save, b.Save, s) is { } potion) { SkipChain(names, where, potion); return; }
        var (hp, trail, skip) = Simulate(j, events, s, b.Save, a.Save.Map.Turn);
        if (skip is not null) { SkipChain(names, where, skip); return; }
        string detail = $"{File(a.Save)} → {File(b.Save)} {unitId} {s.A.TypeId}: {trail}; сохранение {s.B.Hp}";
        foreach (var n in names) c.Count(n, hp == s.B.Hp, detail);
        c.Note($"   {string.Join("+", names.Distinct())} {(hp == s.B.Hp ? "сошлось" : "разошлось")}: {detail}");
        if (names.Contains("heal_hp"))
            c.Count(hp == s.View.HpMax ? "heal_hp · до максимума" : "heal_hp · частичное", hp == s.B.Hp, detail);
    }

    private void SkipChain(List<string> names, string where, string reason)
    {
        foreach (var n in names) c.Skip(n, reason);
        c.Note($"   {string.Join("+", names.Distinct())} пропуск: {where}: {reason}");
    }

    private (int Hp, string Trail, string? Skip) Simulate(TreasuryJournal j, List<CorpusEvent> events, Subject s, SaveAt after, int turn)
    {
        int hp = s.A.Hp, ticks = 0;
        long lastIncome = long.MinValue / 2;
        var trail = new List<string> { $"HP {hp}" };
        bool temple = Healing.HasTemple(PlayerIn(after, s.Player.Id)?.Buildings ?? s.Player.Buildings, book.Prices.Economy.Buildings);
        // Место в момент лечения из сохранений не восстановить: ИИ заходит в укрепление, лечит и выходит внутри своего
        // хода (обе строки heal ИИ первой большой партии — отряды в поле в обоих сохранениях). Код 7 держит ret = true
        // журнала; Храм сверяется по сохранению после (построенное не исчезает).
        const bool inside = true;
        foreach (var e in events)
        {
            switch (e)
            {
                case IncomeEvent i when Same(i.Player, s.Player.Id):
                    if (e.Seq - lastIncome > JournalSaves.AiIncomeRepeatSeq)
                    {
                        ticks++;
                        var (u, _, f, _, ends) = Percents(s, (int)(i.Turn ?? turn));
                        int beforeHeal = hp;
                        hp = Regeneration.Tick(hp, s.View.HpMax, u, f);
                        trail.Add($"тик {hp}");
                        if (ends)
                        {
                            hp = Math.Max(hp - Regeneration.RiotDamage(beforeHeal, book.ThiefRules.RiotDamage), 0);
                            trail.Add($"конец мятежа {hp}");
                        }
                    }
                    lastIncome = e.Seq;
                    break;
                case PotionUseEvent { Ret: true, InBattle: false } p when Same(p.Target, s.A.Id):
                    // Ранг 1 по журналу: ai-q49 S143UN0015 — тик 16 → 29 (xp seq 13920285), зелье G000IG0005 seq 13953914
                    // 29 → 45 (упор в HPmax, ItemUse.HealAmount).
                    if (p.ItemType is not { } type || !book.Prices.Items.TryGetValue(type, out var drunk))
                        return (hp, "", $"тип зелья {p.Item} не известен");
                    if (drunk.Category != Magic.ItemCategory.PotionHeal) return (hp, "", $"зелье {type} не лечебное");
                    hp += Magic.ItemUse.HealAmount(drunk, hp, s.View.HpMax);
                    trail.Add($"зелье {type} → {hp}");
                    break;
                case BattleEndEvent be when be.Units.Any(x => Same(x.Id, s.A.Id)):
                    return (hp, "", "бой с юнитом между сохранениями");
                case ItemEquipEvent or BankEvent or ThiefRollEvent when Hidden(e, s) is { } hidden:
                    return (hp, "", hidden);
                case ReviveEvent { Ret: true } r when Same(r.Unit, s.A.Id):
                    if (!TryBankBefore(j, r.Seq, r.Player, out var rb)) return (hp, "", "у воскрешения нет строки bank до следующей причины");
                    var rv = Healing.Revive(hp, s.Costs.Revive, rb, temple, inside);
                    hp = rv.Hp; trail.Add(rv.Refused ?? $"воскрешение {hp}");
                    break;
                case HealEvent { Ret: true } h when Same(h.Unit, s.A.Id):
                    if (!TryBankBefore(j, h.Seq, h.Player, out var hb)) return (hp, "", "у лечения нет строки bank до следующей причины");
                    var hr = Healing.Heal(hp, s.View.HpMax, s.Costs.Heal, hb, temple, inside);
                    hp = hr.Hp; trail.Add(hr.Refused ?? $"лечение за {TreasuryAudit.Show(hb)} → {hp}");
                    break;
            }
        }
        return ticks > 1 ? (hp, "", $"тиков дохода игрока в окне {ticks}") : (hp, string.Join(" → ", trail), null);
    }

    internal static bool TryBankBefore(TreasuryJournal j, long causeSeq, string player, out Bank before)
    {
        before = Bank.Zero;
        if (j.NestedBank(causeSeq, player) is not { } b) return false;
        before = b.Before;
        return true;
    }

    private Subject? SubjectOf(SaveAt a, SaveAt b, string unitId, out string reason)
    {
        reason = "";
        if (!a.Map.Units.TryGetValue(unitId, out var ua)) { reason = "юнита нет в сохранении до (нанят или призван в окне)"; return null; }
        if (!b.Map.Units.TryGetValue(unitId, out var ub)) { reason = "юнита нет в следующем сохранении"; return null; }
        if (!Same(ua.TypeId, ub.TypeId) || ua.Level != ub.Level || !ua.Modifiers.SequenceEqual(ub.Modifiers, StringComparer.OrdinalIgnoreCase))
        { reason = "тип, уровень или цепочка модификаторов менялись"; return null; }
        if (PlaceOf(a, unitId) is not { } place) { reason = "юнит не в отряде и не в гарнизоне (охрана руины)"; return null; }
        if (PlayerIn(a, place.Owner) is not { } player) { reason = "владельца нет среди игроков сохранения"; return null; }
        if (book.View(ua, out reason) is not { } view) return null;
        if (book.CostsOf(ua.TypeId) is not { } costs) { reason = $"цен типа {ua.TypeId} нет в Gunits"; return null; }
        // Земля под отрядом в поле менялась в окне — «своя земля» на миг лечения из сохранений не видна: чужой жезл перекрашивает
        // клетки сразу, а land_grow пишет только новые клетки роста. Карта 23 (20261020-port-synth), t23 → t24: KC001b (74,83) и
        // KC0034 (76,117) на земле Нежити (4) в t23, в t24 там 2 и 3; в начале хода Нежити (seq 3988863) regen pct 5 без +10.
        if (place is { Fort: null, Stack: { } moved } && a.Map.Contains(moved.X, moved.Y) && b.Map.Contains(moved.X, moved.Y)
            && a.Map.At(moved.X, moved.Y).Terrain != b.Map.At(moved.X, moved.Y).Terrain)
        { reason = "земля под отрядом в поле менялась между сохранениями"; return null; }
        bool ownLand = place is { Fort: null, Stack: { } st } && a.Map.Contains(st.X, st.Y)
                       && a.Map.At(st.X, st.Y).Terrain == Economy.TerrainOfRace(book.Prices.Economy.Races[player.RaceId].RaceType);
        return new Subject(ua, ub, place, player, view, costs, ownLand);
    }

    /// <summary>Без журнала: бой виден по опыту и убыли HP, платное лечение возможно только при Храме у игрока и
    /// отряде в укреплении (коды 6 и 7 <c>0x5DE06E</c>).</summary>
    private string? LooseExclusion(SaveAt b, Subject s)
    {
        if (s.A.Xp != s.B.Xp) return "опыт изменился — бой или тренер (журнала нет)";
        if (s.B.Hp < s.A.Hp) return "HP убыло — бой (журнала нет)";
        bool inside = s.Place.Fort is not null || PlaceOf(b, s.A.Id)?.Fort is not null;
        var buildings = PlayerIn(b, s.Player.Id)?.Buildings ?? s.Player.Buildings;
        return inside && Healing.HasTemple(buildings, book.Prices.Economy.Buildings)
            ? "у игрока Храм и отряд в укреплении — возможно платное лечение (журнала нет)" : null;
    }

    private static string? JournalExclusion(List<CorpusEvent> window, Subject s)
    {
        int ticks = 0;
        long last = long.MinValue / 2;
        foreach (var e in window)
        {
            if (e is IncomeEvent i && Same(i.Player, s.Player.Id)) { if (e.Seq - last > JournalSaves.AiIncomeRepeatSeq) ticks++; last = e.Seq; }
            if (e is BattleEndEvent be && be.Units.Any(x => Same(x.Id, s.A.Id))) return "бой с юнитом между сохранениями";
            if (e is HealEvent h && Same(h.Unit, s.A.Id) || e is ReviveEvent r && Same(r.Unit, s.A.Id))
                return "лечение или воскрешение между сохранениями — сверяет цепочка heal_hp/revive_hp1";
            if (e is PotionUseEvent { InBattle: false } p && Same(p.Target, s.A.Id))
                return "зелье на карте между сохранениями — сверяет цепочка potion_heal_map";
            if (Hidden(e, s) is { } hidden) return hidden;
        }
        return ticks == 1 ? null : $"тиков дохода владельца между сохранениями {ticks}";
    }

    /// <summary>
    /// Что меняет HP мимо событий схемы 5.2, и поэтому исключает окно:
    /// <list type="bullet">
    /// <item>отряд юнита надевал или снимал предметы — зелье лечения выпивается снятием (прогон 2, M8: «лечили и
    /// золотом, и зельями»);</item>
    /// <item>в окне кто-то тратил ману строкой <c>bank</c> — это каст заклинания на карте (урон, лечение, воскрешение
    /// любого отряда) или изучение в столице; события <c>spell_cast</c> появились только в схеме 5.3, а в корпусе их нет
    /// (харнесс казны задачи 1: «каст заклинания, ИИ — 36, человек — 13»).</item>
    /// </list>
    /// </summary>
    private static string? Hidden(CorpusEvent e, Subject s)
    {
        if (e is ItemEquipEvent { Apply: true } item && s.Place.Stack is { } stack && Same(item.Stack, stack.Id))
            return "отряд надевал или снимал предметы между сохранениями (зелье?)";
        if (e is BankEvent { Mode: 0 } bank && bank.Change is var ch
            && (ch.Infernal < 0 || ch.Life < 0 || ch.Death < 0 || ch.Runic < 0 || ch.Grove < 0))
            return "в окне тратили ману без события-причины — возможен каст заклинания на карте (spell_cast только в схеме 5.3)";
        if (e is BankEvent { Mode: 0 } zeroed && zeroed.After == Bank.Zero && zeroed.Before != Bank.Zero)
            return "казна игрока обнулена — каст ИИ со второго кошелька (задача 28), события-причины нет";
        if (e is ThiefRollEvent roll && (s.Place.Stack is { } target && Same(roll.Target, target.Id)
                                         || s.Place.Fort is { } fort && Same(roll.Target, fort.Id)))
            return "вор действовал против отряда или укрепления юнита (отравление)";
        return null;
    }

    /// <summary>Из сумки или слотов отряда юнита между сохранениями исчезло зелье (<see cref="Magic.Item.IsPotion"/>) —
    /// выпитое на карте зелье лечения или воскрешения событий <c>heal</c>/<c>revive</c> не пишет и снятием предмета не
    /// всегда проходит, поэтому окно исключается по самому исчезновению.</summary>
    private string? PotionGone(SaveAt a, SaveAt b, Subject s)
    {
        if (s.Place.Stack is not { } stack) return null;
        var after = b.Map.Stacks.TryGetValue(stack.Id, out var sb) ? sb.Items.Concat(sb.Equipment).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        bool gone = stack.Items.Concat(stack.Equipment).Where(i => i.Length > 0 && !after.Contains(i))
                         .Any(i => a.ItemTypes.TryGetValue(i, out var type) && book.Prices.Items.TryGetValue(type, out var item) && item.IsPotion);
        return gone ? "из сумки или слотов отряда исчезло зелье — выпито на карте" : null;
    }

    /// <summary>Ход дневного тика владельца — из первой его строки <c>income</c> в окне; null — журнала нет.</summary>
    private static int? TickTurn(List<CorpusEvent>? window, Subject s) =>
        window?.OfType<IncomeEvent>().FirstOrDefault(i => Same(i.Player, s.Player.Id) && i.Turn is not null)?.Turn is { } t
            ? (int)t : null;

    /// <summary>Проценты юнита и укрепления (<see cref="Regeneration"/>: у гарнизона оба слагаемых) в тик хода
    /// <paramref name="turn"/>. <c>RiotEnds</c> — мятеж укрепления кончается в этот тик
    /// (<see cref="Fortifications.RiotEndsAt"/>): тогда тик уже без мятежа, а после него — разовый <see cref="Regeneration.RiotDamage"/>
    /// от HP до тика. Было <c>riotEnd &gt; turn</c> — противоречило модели (мятеж идёт, пока <c>RIOT_T</c> не
    /// сброшен, <see cref="Fortifications.Rioting"/>).</summary>
    private (int Unit, int UnitIfStack, int Fort, string Where, bool RiotEnds) Percents(Subject s, int turn)
    {
        var race = book.Prices.Economy.Races[s.Player.RaceId];
        int lord = book.Prices.Treasury.LordCategory.GetValueOrDefault(s.Player.LordId, -1);
        int stackPercent = Regeneration.UnitPercent(s.View.Regen, lord, race.Playable, s.Player.CapturedBy.Length > 0,
                                                    book.Vars.WarlordRegen, s.OwnLand);
        int unit = stackPercent;
        string who = lord == Regeneration.WarlordCategory ? ", Воитель" : "";
        if (s.Place.Fort is not { } fort) return (unit, stackPercent, 0, (s.OwnLand ? "поле, своя земля" : "поле") + who, false);
        bool ends = fort.RiotTurn is { } riotT && Fortifications.RiotEndsAt(riotT, turn);
        string riotNote = ends ? ", конец мятежа" : "";
        if (fort.Kind == SiteKind.Capital)
            return (unit, stackPercent, Regeneration.CapitalPercent(race),
                    (s.Place.Garrison ? "гарнизон столицы" : "столица") + who + riotNote, ends);
        int size = fort.Size ?? 1;
        // Бунт — до сброса RIOT_T; сброс идёт до прохода лечения, так что в день конца мятежа деревня уже лечит (20261016b, карта 2,
        // R1: regen 15 % и 20 % в ход Кланов 3 при RIOT_T 3), а в дни бунта не лечит никого — ни слагаемого укрепления, ни юнита.
        if (Fortifications.Rioting(fort.RiotTurn) && !ends)
            return (0, 0, 0, $"{(s.Place.Garrison ? "гарнизон деревни" : "деревня")} {size}{who}, мятеж{riotNote}", ends);
        return (unit, stackPercent, Regeneration.VillagePercent(book.Prices.Economy, s.Player.RaceId, size, fort.RegenBonus ?? 0, false),
                $"{(s.Place.Garrison ? "гарнизон деревни" : "деревня")} {size}" + who + riotNote, ends);
    }

    private static Place? PlaceOf(SaveAt s, string unitId)
    {
        var stack = s.Map.Stacks.Values.FirstOrDefault(x => x.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase));
        if (stack is not null)
            return new Place(stack.Owner, stack, s.Sites.FirstOrDefault(x => IsFort(x) && (Same(x.Id, stack.Inside) || Same(x.StackId, stack.Id))), false);
        var garrison = s.Sites.FirstOrDefault(x => IsFort(x) && x.Guards.Contains(unitId, StringComparer.OrdinalIgnoreCase));
        return garrison is null ? null : new Place(garrison.Owner, null, garrison, true);
    }

    private static bool IsFort(MapSite s) => s.Kind is SiteKind.Capital or SiteKind.Village;

    private static MapPlayer? PlayerIn(SaveAt s, string id) => s.Map.Players.TryGetValue(id, out var p) ? p : null;

    private static string File(SaveAt s) => Path.GetFileName(s.Path);

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
