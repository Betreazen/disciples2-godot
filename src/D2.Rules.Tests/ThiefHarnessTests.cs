using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Map;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Задача 12 Ф7 — воры: список действий, порог, треугольный бросок, гибель и цена в очках
/// хода (<see cref="Thieves"/>).
/// <para>Счётчики: <c>thief_chance</c>, <c>thief_roll_exact</c>, <c>thief_two_draws</c>,
/// <c>thief_death</c>, <c>thief_move_cost</c>, <c>thief_gate</c>.</para></summary>
[Trait("Category", "Corpus")]
public class ThiefHarnessTests(ITestOutputHelper output)
{
    // ---- модульные ------------------------------------------------------------------------------------

    /// <summary>
    /// Семь процентов из окна вора прогона <c>f7-m18-morale</c> (П§19, лорд Повелитель Воров,
    /// снимок пользователя) — единственное живое наблюдение морали укрепления. Столица соседа даёт
    /// <c>MORALE_6</c> = 60, города 1…5 — 10…50.
    /// </summary>
    [SkippableFact]
    public void CityMorale_ReproducesTheSevenPercentsOfM18()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var rules = EconomyLoader.LoadThieves(GamePaths.Globals);
        Assert.Equal([10, 20, 30, 40, 50, 60], rules.Morale);

        // (уровень деревни или null для столицы, действие, показанный процент)
        (int? Size, ThiefAction Action, int Window)[] window =
        [
            (null, ThiefAction.StealGold, 20),      // 80 − MORALE_6
            (null, ThiefAction.Misfit, 30),         // 90 − MORALE_6
            (1, ThiefAction.PoisonCity, 80),        // 90 − MORALE_1
            (2, ThiefAction.PoisonCity, 70),
            (3, ThiefAction.PoisonCity, 60),
            (4, ThiefAction.PoisonCity, 50),
            (5, ThiefAction.PoisonCity, 40),
        ];
        foreach (var (size, action, shown) in window)
        {
            int resistance = Thieves.CityResistance(rules, size);
            int chance = Thieves.Chance(rules, action, resistance);
            output.WriteLine($" {(size is null ? "столица" : $"город {size}"),-10} {action,-11} " +
                             $"CHANCE {rules.Chance(action)} − мораль {resistance} = {chance}, окно {shown}");
            Assert.Equal(shown, chance);
        }

        // Действие с CHANCE 100 мораль не вычитает вовсе (jge в 0x5D31D0): Riot и Spy по столице — 100.
        Assert.Equal(100, Thieves.Chance(rules, ThiefAction.RiotCity, Thieves.CityResistance(rules, null)));
        Assert.Equal(100, Thieves.Chance(rules, ThiefAction.Spy, Thieves.CityResistance(rules, null)));
        // Пол нуля: Assassinate (70) против лидера с NEGOTIATE 70 и выше — ноль, а не минус.
        Assert.Equal(0, Thieves.Chance(rules, ThiefAction.Assassinate, Thieves.StackResistance(70)));
        Assert.Equal(0, Thieves.Chance(rules, ThiefAction.Assassinate, Thieves.StackResistance(95)));
    }

    /// <summary>Список действий: гейтит лорд игрока, а не вор (<c>0x5D3A51</c> + <c>0x5D3BEB</c>).
    /// Против нейтрального отряда обычному лорду остаются ровно два — Poison Party и Duel.</summary>
    [Fact]
    public void Actions_AreGatedByThePlayersLord()
    {
        var warrior = Thieves.Actions(LordCategory.Warrior, ThiefTargetKind.Stack, targetPlayable: true);
        // Порядок — по номеру действия, как в окне движка на снимках прогона 13.10 (Poison Party, Infiltrate Spy, …, Duel).
        Assert.Equal([ThiefAction.PoisonStack, ThiefAction.Spy, ThiefAction.Duel], warrior);
        Assert.Equal([ThiefAction.PoisonStack, ThiefAction.Duel],
                     Thieves.Actions(LordCategory.Mage, ThiefTargetKind.Stack, targetPlayable: false));
        Assert.Equal([ThiefAction.Spy, ThiefAction.PoisonCity],
                     Thieves.Actions(LordCategory.Warrior, ThiefTargetKind.Fortification, true));

        // Глава гильдии: по отряду +3 действия, по укреплению +6; играбельность расы его добавку не
        // трогает — Counterfeit Orders у него есть и против нейтрала (П§16.1, Misfit по нейтралам).
        var guild = Thieves.Actions(LordCategory.Diplomat, ThiefTargetKind.Stack, true);
        Assert.Equal(6, guild.Count);
        Assert.Contains(ThiefAction.Misfit, guild);
        Assert.Contains(ThiefAction.StealItem, guild);
        Assert.Contains(ThiefAction.Assassinate, guild);
        Assert.Contains(ThiefAction.Misfit, Thieves.Actions(LordCategory.Diplomat, ThiefTargetKind.Stack, false));
        Assert.Equal([ThiefAction.PoisonStack, ThiefAction.Spy, ThiefAction.StealItem, ThiefAction.Assassinate, ThiefAction.Misfit,
                      ThiefAction.Duel], guild);   // снимок 27 прогона 13.10
        var city = Thieves.Actions(LordCategory.Diplomat, ThiefTargetKind.Fortification, true);
        Assert.Equal([ThiefAction.Spy, ThiefAction.Assassinate, ThiefAction.Misfit, ThiefAction.PoisonCity, ThiefAction.StealSpell,
                      ThiefAction.Bribe, ThiefAction.StealGold, ThiefAction.RiotCity], city);   // снимки 1, 12, 26
        Assert.Contains(ThiefAction.StealGold, city);
        Assert.Contains(ThiefAction.RiotCity, city);
        Assert.Contains(ThiefAction.Bribe, city);

        // Руина и лавки: одно действие, и добавки главы гильдии там нет (0x5D3CA0).
        foreach (var lord in new[] { LordCategory.Mage, LordCategory.Warrior, LordCategory.Diplomat })
        {
            Assert.Equal([ThiefAction.SpyRuin], Thieves.Actions(lord, ThiefTargetKind.Ruin, false));
            Assert.Equal([ThiefAction.StealMerchant], Thieves.Actions(lord, ThiefTargetKind.Merchant, true));
            Assert.Equal([ThiefAction.StealMage], Thieves.Actions(lord, ThiefTargetKind.MageTower, true));
        }
        // Доступность (Allowed) — уже не по списку окна, а по фильтру 0x5D32B8: ThievesSynthTests.
    }

    /// <summary>Бросок: два вызова генератора ВСЕГДА (и при пороге 100), сравнение по
    /// <c>&lt;=</c> со сдвинутой суммой, гибель при провале и цена <c>ceil(MOVE / 2)</c>.</summary>
    [Fact]
    public void Roll_AlwaysDrawsTwice_AndChargesHalfTheMovement()
    {
        var rng = new ScriptedRng([79, 4, 0, 96]);
        var miss = Thieves.Roll(rng, 40);                     // (79 + 4) >> 1 = 41 > 40
        Assert.Equal((41, false), (miss.Average, miss.Success));
        var atHundred = Thieves.Roll(rng, 100);               // порог 100 — броски всё равно сделаны
        Assert.Equal((48, true), (atHundred.Average, atHundred.Success));
        Assert.Equal(4, rng.Used);

        // Ровно на пороге — успех (setle, а не setl).
        Assert.True(Thieves.Roll(new ScriptedRng([40, 40]), 40).Success);
        Assert.False(Thieves.Roll(new ScriptedRng([41, 41]), 40).Success);
        // Сдвиг, а не округление вверх: (0 + 1) >> 1 = 0.
        Assert.Equal(0, Thieves.Roll(new ScriptedRng([0, 1]), 0).Average);

        Assert.Equal(13, Thieves.MoveCost(25));               // ceil(25 / 2)
        Assert.Equal(10, Thieves.MoveCost(20));
        Assert.Equal(15, Thieves.MoveCost(30));
        Assert.Equal(0, Thieves.MoveCost(0));
        Assert.Equal(0, Thieves.MoveCost(-4));
        Assert.Equal(0, Thieves.Spend(10, 13));               // пол нуля, а не минус три
        Assert.Equal(12, Thieves.Spend(25, 13));
        Assert.True(Thieves.Charged(success: true));          // и Дуэль — через свой результат 0x41D892
        Assert.False(Thieves.Charged(success: false));        // провал: 0x44314B → 0x4431B8, без ChangeAP
        Assert.True(Thieves.DiesBeforeDialog([Thieves.IncorruptibleAbility]));
        Assert.False(Thieves.DiesBeforeDialog([13, 14]));
    }

    /// <summary>Исход действия целиком: провал убивает вора и хода не списывает, успешная Дуэль
    /// списывает ceil(MOVE/2) своим результатом (журнал 20260915 seq 5309788: delta 13 при MOVE 25,
    /// seq 5309792: 7 → 0), списание считается от ПОЛНОГО запаса, а не от остатка.</summary>
    [Fact]
    public void Perform_KillsOnFailure_AndChargesDuelLikeTheRest()
    {
        var rules = ThiefRules.Vanilla;
        var fail = Thieves.Perform(rules, new ScriptedRng([79, 4]), ThiefAction.StealGold,
                                   chance: 40, moveAllowance: 25, moveLeft: 23);
        Assert.True(fail.ThiefDies);
        Assert.Equal((23, 0), (fail.MoveLeft, fail.Cost));    // провал не списывает: вор гибнет

        var duel = Thieves.Perform(rules, new ScriptedRng([50, 50]), ThiefAction.Duel,
                                   chance: 100, moveAllowance: 25, moveLeft: 7);
        Assert.Equal((0, 13, false), (duel.MoveLeft, duel.Cost, duel.ThiefDies)); // seq 5309788/5309792

        var spy = Thieves.Perform(rules, new ScriptedRng([10, 10]), ThiefAction.Spy,
                                  chance: 100, moveAllowance: 25, moveLeft: 23);
        Assert.Equal((10, 13), (spy.MoveLeft, spy.Cost));     // 23 − ceil(25/2), а не 23 − ceil(23/2)

        var floored = Thieves.Perform(rules, new ScriptedRng([0, 0]), ThiefAction.PoisonCity,
                                      chance: 80, moveAllowance: 25, moveLeft: 10);
        Assert.Equal((0, 13), (floored.MoveLeft, floored.Cost));

        // Ванильная таблица — та же, что в Globals: пороги действий и мораль.
        Assert.Equal(90, rules.Chance(ThiefAction.PoisonStack));
        Assert.Equal(60, rules.Chance(ThiefAction.StealMage));
        Assert.Equal(60, Thieves.CityResistance(rules, null));
        Assert.Throws<ArgumentException>(() => Thieves.CityResistance(rules, 9));
    }

    // ---- харнесс ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void Thieves_MatchCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Available, "корпус недоступен");
        var book = ProgressBook.Load(GamePaths.Globals);
        var c = new Counters();
        var audit = new ThiefAudit(book, c);

        var (unique, _) = JournalSet.Distinct(CorpusFiles.Journals());
        foreach (var path in unique)
        {
            // Броски читаются у ЛЮБОГО журнала: сохранений они не требуют. Цена хода до схемы 6.0 — по
            // цепочке stack_move и сохранениям каталога; в 6.0 — по ap_apply и снимку той же загрузки карты.
            // Порог, гейт и гибель — только у журналов с манифестом.
            var raw = ThiefJournal.Load(path);
            audit.Draws(raw);
            bool needSaves = raw.Rolls.Count > 0 || raw.Charges.Count > 0;
            var j = needSaves && TreasuryJournal.HasBank(path) ? TreasuryJournal.Load(path) : null;
            audit.MoveCosts(raw, j);
            if (raw.Rolls.Count == 0 || j is null) continue;
            audit.Thresholds(j);
            audit.Deaths(j, raw);
            audit.PoisonProbe(j, raw);
        }
        audit.RiotProbe();
        audit.Print();

        c.Print(output, "воры: список действий, порог, бросок и цена хода (задача 12 Ф7)");
        // Строка-гипотеза «успех вора не убивает» в приёмку не входит нарочно: в окне между снимками
        // бывают бои, и исчезновение вора там объясняется не броском.
        c.Require("thief_two_draws", "thief_roll_exact", "thief_chance", "thief_gate", "thief_death",
                  "thief_move_cost");
        // В корпусе после потери 18.09 нет ни одного ap_effect с причиной thief_no_actions/thief_cancel.
        c.RequireOrSkip(("thief_no_actions_twice",
            "вор подходит к цели с пустым списком действий и закрывает свиток — например, к городу, который уже " +
            "бунтует после «Мятежа» (свиток «Пока город бунтует…», C-thieves.md §2)"));
    }
}
