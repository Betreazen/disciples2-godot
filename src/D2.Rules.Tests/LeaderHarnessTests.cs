using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Задача 11 Ф7 — уровень лидера, накопительный список навыков, выбор ИИ (argmax) и
/// картовые эффекты навыков (<see cref="LeaderUpgrades"/>, <see cref="SkillChoice"/>,
/// <see cref="Experience.LevelUpLeader"/>, <see cref="Movement.StartMovement"/>,
/// <see cref="Fog.Radius"/>).
/// <para>Счётчики: <c>skill_count</c>, <c>skill_available</c>, <c>ai_skill_argmax</c>,
/// <c>leader_move_max</c>.</para></summary>
[Trait("Category", "Corpus")]
public class LeaderHarnessTests(ITestOutputHelper output)
{
    // ---- модульные ------------------------------------------------------------------------------------

    /// <summary>Накопительный список: все строки лорда с <c>MIN_LEVEL ≤ уровень</c>, ещё не взятые.
    /// Ряд лорда <c>g000LR0001</c> (Воин) по таблице: на 2-м уровне четыре строки, на 3-м — семь,
    /// на 15-м — все 27.</summary>
    [SkippableFact]
    public void Available_IsCumulative_AndDropsTaken()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var all = ModifierLoader.LoadLeaderUpgrades(GamePaths.Globals);
        Assert.Equal(391, all.Count);

        // Порядок — табличный (в файле строки лорда лежат от старших уровней к младшим), поэтому
        // сверяется состав, а не последовательность: сортировки Available не делает нарочно.
        var lvl2 = LeaderUpgrades.Available(all, "g000LR0001", 2);
        Assert.Equal(["g000um4001", "g000um4002", "g000um4003", "g000um4004"],
                     lvl2.Select(u => u.ModifId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal([80, 85, 85, 90], lvl2.Select(u => u.Priority).Order());
        Assert.Equal(7, LeaderUpgrades.Available(all, "g000LR0001", 3).Count);
        Assert.Equal(27, LeaderUpgrades.Available(all, "g000LR0001", 15).Count);

        // Навык 2-го уровня, не взятый до 10-го, в списке 10-го уровня остаётся — это и есть
        // накопительность (29 поздних выборов корпуса, П§20).
        var late = LeaderUpgrades.Available(all, "g000LR0001", 10, ["G000UM4001", "g000um4005"]);
        Assert.Contains("g000um4002", late.Select(u => u.ModifId));
        Assert.DoesNotContain("g000um4001", late.Select(u => u.ModifId));   // взят, регистр не помеха
        Assert.DoesNotContain("g000um4022", late.Select(u => u.ModifId));   // MIN_LEVEL 13

        // Три «Leadership» — три разные строки с одной подписью: «ещё не взят» их не съедает.
        var leadership = all.Where(u => u.LordId == "g000LR0001" && u.ModifId is "g000um4006" or "g000um4101"
                                                                or "g000um4102").ToList();
        Assert.Equal([3, 8, 10], leadership.Select(u => u.MinLevel));
        Assert.Empty(LeaderUpgrades.Available(all, "g000LR0013", 15));      // нейтральный лорд — навыков нет
    }

    /// <summary>Выбор героя ИИ — argmax <c>PRIORITY</c>, при равенстве первый в порядке строк лорда
    /// (<c>ai_skill_argmax_filtered</c> 38/0, отчёт A §2, ai-q02). Вес 99 (<c>g000um4006</c>) берётся
    /// первым; вес 95 — <c>g000um4005</c> раньше <c>g000um4010</c> (у LR0007 и LR0016 он и в таблице
    /// выше); вес 85 у бойца (врождённая 8, <c>g000um4001</c> отфильтрован) — LR0007 и LR0016 берут
    /// <c>g000um4002</c>, LR0010 — <c>g000um4003</c> (у него 4003 первым).</summary>
    [SkippableFact]
    public void AiChoice_TakesHighestPriority_FirstInLordOrderOnTies()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var all = ModifierLoader.LoadLeaderUpgrades(GamePaths.Globals);
        var gmodif = ModifierLoader.Load(GamePaths.Globals).Modifiers;
        var fighter = LeaderAbilities.Innate("g000uu0070",
                                             D2.Data.Magic.EquipLoader.LoadInnateAbilities(GamePaths.Globals));
        string? Pick(string lord, int level, params string[] taken) =>
            SkillChoice.ByAi(LeaderUpgrades.Available(all, lord, level, taken, fighter, gmodif))?.ModifId;

        foreach (var lord in new[] { "g000LR0007", "g000LR0016" })
        {
            Assert.Equal("g000um4006", Pick(lord, 5));
            Assert.Equal("g000um4005", Pick(lord, 5, "g000um4006"));
            Assert.Equal("g000um4010", Pick(lord, 5, "g000um4006", "g000um4005"));
            Assert.Equal("g000um4002", Pick(lord, 2));
        }
        Assert.Equal("g000um4003", Pick("g000LR0010", 2));
        Assert.Null(SkillChoice.ByAi([]));
    }

    /// <summary>Фильтр врождённых (<c>canApply</c> <c>0x44f35d</c>, отчёт A §2): навык, чей
    /// <c>GmodifL TYPE 11 ABILITY</c> уже есть у типа героя в <c>GLabi</c>, в список не входит.
    /// Боец <c>g000uu0070</c> (8) не получает <c>g000um4001</c>, маг <c>g000uu0072</c> (7, 14) —
    /// <c>g000um4005</c>; без врождённых список прежний. Человеку тот же список (ранг 3).</summary>
    [SkippableFact]
    public void Available_DropsSkillsGrantingAnInnateAbility()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var all = ModifierLoader.LoadLeaderUpgrades(GamePaths.Globals);
        var gmodif = ModifierLoader.Load(GamePaths.Globals).Modifiers;
        var innate = D2.Data.Magic.EquipLoader.LoadInnateAbilities(GamePaths.Globals);
        IEnumerable<string> Ids(string type, int level) => LeaderUpgrades.Available(
            all, "g000LR0001", level, null, LeaderAbilities.Innate(type, innate), gmodif).Select(u => u.ModifId);

        Assert.DoesNotContain("g000um4001", Ids("g000uu0070", 2));
        Assert.Equal(3, Ids("g000uu0070", 2).Count());
        Assert.DoesNotContain("g000um4005", Ids("g000uu0072", 3));
        Assert.Contains("g000um4001", Ids("g000uu0072", 3));
        Assert.Contains("g000um4005", Ids("g000uu0070", 3));
        Assert.Equal(4, LeaderUpgrades.Available(all, "g000LR0001", 2).Count);   // без фильтра — как было
        Assert.Equal(SkillRefusal.NotAvailable,
                     SkillChoice.ByPlayer(all, "g000LR0001", 2, 1, [], "g000um4001",
                                          innate: LeaderAbilities.Innate("g000uu0070", innate),
                                          modifiers: gmodif).Refusal);
    }

    /// <summary>Выбор человека: один выбор снимает единицу с очереди, чужой навык не проходит,
    /// отказ ничего не меняет.</summary>
    [SkippableFact]
    public void PlayerChoice_TakesOneFromTheQueue_AndRefusesOutsideTheList()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var all = ModifierLoader.LoadLeaderUpgrades(GamePaths.Globals);

        var ok = SkillChoice.ByPlayer(all, "g000LR0001", 2, pending: 2, taken: [], "g000um4002");
        Assert.Equal(SkillRefusal.None, ok.Refusal);
        Assert.Equal(1, ok.Pending);
        Assert.Equal(["g000um4002"], ok.Taken);

        Assert.Equal(SkillRefusal.NothingPending,
                     SkillChoice.ByPlayer(all, "g000LR0001", 2, 0, [], "g000um4002").Refusal);
        Assert.Equal(SkillRefusal.NotAvailable,
                     SkillChoice.ByPlayer(all, "g000LR0001", 2, 1, [], "g000um4022").Refusal);   // MIN_LEVEL 13
        Assert.Equal(SkillRefusal.NotAvailable,
                     SkillChoice.ByPlayer(all, "g000LR0001", 2, 1, ["g000um4002"], "g000um4002").Refusal);
        var limited = Enumerable.Range(0, SkillChoice.DefaultLimit).Select(i => $"g000um4{i:000}").ToList();
        Assert.Equal(SkillRefusal.LimitReached,
                     SkillChoice.ByPlayer(all, "g000LR0001", 15, 1, limited, "g000um4024").Refusal);
        var refused = SkillChoice.ByPlayer(all, "g000LR0001", 2, 1, ["g000um4002"], "g000um4002");
        Assert.Equal((1, 1), (refused.Pending, refused.Taken.Count));
    }

    /// <summary>Переход уровня лидера: циклом, опыт после повышения обнуляется, очередь выбора
    /// растёт на каждый уровень, потолок сценария держит.</summary>
    [Fact]
    public void LeaderLevelUp_CyclesAndQueuesOneChoicePerLevel()
    {
        int Next(int level) => 100 * level;      // 100, 200, 300 — растущий порог, как GDynUpgr

        var one = Experience.LevelUpLeader(new LeaderProgress(1, 150, 0), Next, maxLevel: 99);
        Assert.Equal(new LeaderProgress(2, 0, 1), one);

        // Излишек не переносится: 1000 опыта при пороге 100 — всё равно ОДИН уровень, остальное
        // пропадает. Цикл в LevelUpLeader — сторож той же формы, что в UnitUpgrade.Apply: пока опыт
        // обнуляется, второй виток недостижим, и увидеть его можно только на правиле «излишек
        // переносится», от которого корпус отказался (задача 4).
        var many = Experience.LevelUpLeader(new LeaderProgress(1, 1000, 0), Next, maxLevel: 99);
        Assert.Equal(new LeaderProgress(2, 0, 1), many);

        // Очередь копится: два повышения без выбора между ними дают UPGCOUNT 2 (план §1.5).
        var twice = Experience.LevelUpLeader(
            Experience.LevelUpLeader(new LeaderProgress(1, 150, 0), Next, 99) with { Xp = 300 }, Next, 99);
        Assert.Equal(new LeaderProgress(3, 0, 2), twice);

        // Потолок сценария: уровень не растёт, опыт остаётся как есть (прибавку в бою потолок обнуляет раньше —
        // Experience.Amount atMaxLevel; живьём 20260917-synth q40, см. тест ниже).
        var capped = Experience.LevelUpLeader(new LeaderProgress(4, 900, 1), Next, maxLevel: 4);
        Assert.Equal(new LeaderProgress(4, 900, 1), capped);
        Assert.Equal(new LeaderProgress(3, 99, 0), Experience.LevelUpLeader(new LeaderProgress(3, 99, 0), Next, 99));
    }

    /// <summary>Живой прогон <c>20260917-synth</c> q40 (<c>MAX_LEADER</c> = 2), ранг 1: лидер 148/150 с уже созданным
    /// типом следующего уровня получает +2 (излишек урезан), берёт уровень 2 с опытом 0 и одним выбором навыка;
    /// во втором бою он на потолке и получает 0. Лидер, стоявший на потолке с начала (CAP, накоплено 5), — 0 дважды.
    /// Накопленное LOW журнал не пишет — взято 5, как у CAP; урезанию до порога оно безразлично.</summary>
    [Fact]
    public void LeaderAtCap_Q40_TopUpToThresholdThenZero()
    {
        const int maxLeader = 2, xpNext = 150;

        int low = Experience.Amount(5, 148, xpNext, Experience.UpgradePath.Leader, nextTierCreated: true);
        Assert.Equal(2, low);
        Assert.Equal(xpNext, Experience.LeaderXpAfter(148, low, xpNext, nextTierCreated: true));
        var up = Experience.LevelUpLeader(new LeaderProgress(1, xpNext, 0), _ => xpNext, maxLeader);
        Assert.Equal(new LeaderProgress(2, 0, 1), up);

        Assert.Equal(0, Experience.Amount(5, up.Xp, xpNext, Experience.UpgradePath.Leader, atMaxLevel: true));
        Assert.Equal(0, Experience.BattleXp(5, 648, xpNext, Experience.UpgradePath.Leader, atMaxLevel: true));
        Assert.Equal(up, Experience.LevelUpLeader(up, _ => xpNext, maxLeader));
    }

    // ---- харнесс ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void LeaderSkillsAndMovement_MatchCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var book = ProgressBook.Load(GamePaths.Globals);
        var c = new Counters();
        var audit = new LeaderAudit(book, c);

        foreach (var (group, saves) in LeaderAudit.AllSaves()) audit.Skills(group, saves);
        audit.NewSkills();
        var (unique, _) = JournalSet.Distinct(CorpusFiles.Journals());
        foreach (var path in unique.Where(TreasuryJournal.HasBank))
        {
            var j = TreasuryJournal.Load(path);
            audit.DayStartMovement(path, j);
            audit.SkillPicks(path, j);
        }
        audit.Print();

        c.Print(output, "уровень лидера, навыки и картовые эффекты (задача 11 Ф7)");
        // leader_move_max в приёмке наравне с остальными: наблюдений 99, поэтому оговорка критерия
        // «если ноль — пункт уходит в §3» не понадобилась. Строка-гипотеза «плюс цена шага» в
        // приёмку не входит нарочно — она красная целиком и печатается ради видимого выбора.
        c.Require("skill_count", "skill_available", "ai_skill_argmax", "leader_move_max");
    }
}
