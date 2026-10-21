using D2.Data.Map;

namespace D2.Data.Tests;

/// <summary>Журнал заданий из сохранения движка (<c>MidQuestLog</c>, <see cref="EventLoader.LoadProgress"/>): окно «Задания» на
/// сохранении корпуса не пусто (пометка сессии Б, слияние восьмого круга 28.09).</summary>
[Trait("Category", "Corpus")]
public class QuestLogLoadTests
{
    [SkippableFact]
    public void LateHumanSave_HasQuestLog_WithScenarioEvents()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal).Last();
        var events = EventLoader.Load(sg);

        var progress = EventLoader.LoadProgress(sg, events);

        Assert.NotEmpty(progress.Log);
        var ids = events.Events.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(progress.Log, e =>
        {
            Assert.Contains(e.EventId, ids);
            Assert.StartsWith("S073PL", e.PlayerId, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(e.Turn, 0, 200);
        });
        Assert.Equal(EventProgressFrom(events).Disabled, progress.Disabled);
    }

    private static D2.Rules.Map.EventProgress EventProgressFrom(D2.Rules.Map.ScenarioEventSet events) =>
        D2.Rules.Map.EventProgress.From(events);
}
