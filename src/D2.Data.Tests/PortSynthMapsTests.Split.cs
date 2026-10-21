using Xunit.Abstractions;

namespace D2.Data.Tests;

// Самые долгие проверки PortSynthMapsTests — своими классами. xUnit гонит тесты одного класса подряд, а параллелит классы: 209 тестов
// синтетических карт одним классом шли ~86 с подряд и заканчивали прогон D2.Data.Tests последними (20.10, docs/perf-log.md, «Скорость
// проверок, 20.10»). Сами проверки — в частях PortSynthMapsTests, здесь только вход.

/// <summary>Партия каждой синтетической карты по памятке, два дня (<see cref="PortSynthMapsTests.NewGame_TwoDaysPass"/>).</summary>
[Trait("Category", "Synth")]
public sealed class PortSynthMapsTests_NewGame(ITestOutputHelper output)
{
    public static TheoryData<string> Maps => PortSynthMapsTests.Maps;

    [Theory]
    [MemberData(nameof(Maps))]
    public void NewGame_TwoDaysPass(string id) => new PortSynthMapsTests(output).NewGame_TwoDaysPass(id);
}

/// <summary>Карта 23 до последнего дня: прогноз порта и посуточная сверка с партией оригинала.</summary>
[Trait("Category", "Synth")]
public sealed class PortSynthMapsTests_Long(ITestOutputHelper output)
{
    /// <inheritdoc cref="PortSynthMapsTests.Long_ThreeAiAtWar_ToTheLastDay_PortForecast"/>
    [SkippableFact]
    public void Long_ThreeAiAtWar_ToTheLastDay_PortForecast() => new PortSynthMapsTests(output).Long_ThreeAiAtWar_ToTheLastDay_PortForecast();

    /// <inheritdoc cref="PortSynthMapsTests.Long_AiTurnsMatchTheOriginalJournal"/>
    [SkippableFact]
    public void Long_AiTurnsMatchTheOriginalJournal() => new PortSynthMapsTests(output).Long_AiTurnsMatchTheOriginalJournal();
}
