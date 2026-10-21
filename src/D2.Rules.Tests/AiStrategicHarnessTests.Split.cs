using Xunit.Abstractions;

namespace D2.Rules.Tests;

// Две самые долгие проверки AiStrategicHarnessTests — своими классами: xUnit гонит тесты одного класса подряд, и девять тестов харнесса
// (постройка столицы ~68 с, бюджет ~46 с) заканчивали прогон D2.Rules.Tests последними (20.10, docs/perf-log.md, «Скорость проверок,
// 20.10»). Сами проверки — в AiStrategicHarnessTests.Economy.cs, здесь только вход.

/// <inheritdoc cref="AiStrategicHarnessTests.BuildPick_OrderAndChoiceFromSaves"/>
[Trait("Category", "Corpus")]
public sealed class AiStrategicHarnessTests_BuildPick(ITestOutputHelper output)
{
    [SkippableFact]
    public void BuildPick_OrderAndChoiceFromSaves() => new AiStrategicHarnessTests(output).BuildPick_OrderAndChoiceFromSaves();
}

/// <inheritdoc cref="AiStrategicHarnessTests.BudgetSpend_MatchesModelAndChains"/>
[Trait("Category", "Corpus")]
public sealed class AiStrategicHarnessTests_Budget(ITestOutputHelper output)
{
    [SkippableFact]
    public void BudgetSpend_MatchesModelAndChains() => new AiStrategicHarnessTests(output).BudgetSpend_MatchesModelAndChains();
}
