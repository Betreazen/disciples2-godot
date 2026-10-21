using System.Text.Json;
using D2.Data.Content;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Окна вора на синтетической карте <c>port-synth-2-world</c> (прогон пользователя 15.10, журнал <c>port-20261015-211436</c>): данные — как
/// у игры при обычном запуске, база плюс моды корня <c>mods/</c>. Пометка 21: в списке действий печаталась разметка цвета; пометка 22:
/// окно итога шпионажа стало заливкой без кнопки (пример мода заменял <c>DLG_ACTION_RESULT</c>), итога в журнале нет. Здесь — строки
/// списка разбираются в имя и цвет движка, а у каждого из 13 действий окно итога открывается на окнах игры, итог уходит показу и зонду
/// (<c>sight ThiefResult</c>), «Закрыть» закрывает.
/// </summary>
[Trait("Category", "Synth")]
public class ThiefSynthWindowTests(ITestOutputHelper output)
{
    private const string Map = "port-synth-2-world";
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private static string Mods => Path.Combine(AppContext.BaseDirectory, "mods");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (GameContent.Load(Data, GameContent.Discover(Mods)), GameBook.Load(Path.Combine(Data, "rules"))));

    /// <summary>Доступное — чёрным (<c>X005TA0819</c>), недоступное — тёмно-красным (<c>X005TA0820</c>, <c>\c128;000;000;</c>); после
    /// разбора в строке одно имя действия (так рисует строку списка сцена, <c>UiScreenView.RefreshRows</c>).</summary>
    [SkippableFact]
    public void ActionRows_ParseToNameAndEngineColour()
    {
        var zone = Zones().Single(z => z.GetProperty("key").GetString() == "T-Spy");
        var (session, context, texts) = Begin();
        var logic = Assert.IsType<ThiefActionsLogic>(ThiefWindows.For(context, Str(zone, "stack"), Str(zone, "target"), texts)!.Logic);
        var ui = Open(logic, ThiefActionsLogic.Screen);
        var rows = ui.Items("TLBOX_ACTIONS").Select(i => UiTextStyle.Parse(i)).ToList();
        output.WriteLine(string.Join(" / ", rows.Select(r => $"{r.Text} {r.Color}")));

        Assert.Equal(logic.Shown.Count, rows.Count);
        Assert.Contains(logic.Shown, a => !logic.Available.Contains(a));   // у цели нет предметов — «Украсть предмет» недоступно
        foreach (var (action, row) in logic.Shown.Zip(rows))
        {
            Assert.DoesNotContain('\\', row.Text);
            Assert.Empty(row.Unknown);
            Assert.Equal(context.Text(texts.Actions[(int)action].Name), row.Text);
            Assert.Equal(logic.Available.Contains(action) ? new UiColor(0, 0, 0) : new UiColor(128, 0, 0), row.Color);
        }
        Assert.NotNull(session);
    }

    /// <summary>Каждый вор памятки делает своё действие окнами игры: выбор, кража (первая ячейка), подкуп («Да»), окно итога на разметке
    /// игры с модами по умолчанию, картинка — действия (успех) или общая провала <c>G000NA9999_BIG</c>, итог — показу и зонду.</summary>
    [SkippableFact]
    public void EveryAction_ResultWindowOpensAndCloses()
    {
        var seen = new List<string>();
        foreach (var zone in Zones())
        {
            var action = Enum.Parse<ThiefAction>(Str(zone, "action"));
            var (session, context, texts) = Begin();
            var sights = new SightRecorder();
            session.Observer = sights;
            var logic = Assert.IsType<ThiefActionsLogic>(ThiefWindows.For(context, Str(zone, "stack"), Str(zone, "target"), texts)!.Logic);
            var ui = Open(logic, ThiefActionsLogic.Screen);
            int index = logic.Shown.ToList().IndexOf(action);
            Assert.True(index >= 0, $"{action}: нет в списке");
            ui.Select("TLBOX_ACTIONS", index);
            logic.Handle(new UiListSelected("TLBOX_ACTIONS", index), ui);
            logic.Handle(new UiClicked("BTN_OK"), ui);
            var first = logic.TakeRequest();
            if (first is not UiClose)
            {
                Follow(first, action);
                Assert.IsType<UiClose>(logic.TakeRequest());
            }

            var report = logic.Report;
            Assert.NotNull(report);
            Assert.Null(report!.Refused);
            var result = Assert.IsType<ThiefResultLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
            Assert.Contains(sights.Sights, s => s.Kind == TurnSightKind.ThiefResult && s.Id == action.ToString());   // до окна итога

            var resultUi = Open(result, ThiefResultLogic.Screen);
            Assert.Equal(context.Text(report.Success ? "X005TA0032" : "X005TA0031"), resultUi.TextOf("TXT_ACTION_RESULT"));
            Assert.Equal(report.Success ? ThiefTexts.Picture(action, big: true) : ThiefTexts.FailurePicture, resultUi.Frame("IMG_ACTION"));
            Assert.NotEqual("", resultUi.TextOf("TXT_ACTION_RESULT_2"));
            result.Handle(new UiClicked("BTN_YES"), resultUi);
            Assert.IsType<UiClose>(result.TakeRequest());
            seen.Add($"{action}:{(report.Success ? "успех" : "провал")}");
        }
        output.WriteLine(string.Join(", ", seen));
        Assert.Equal(13, seen.Count);
    }

    /// <summary>Просьба окна действий после «ОК», если это не сразу закрытие: кража — окно выбора, его «Да» с первой ячейкой; подкуп —
    /// вопрос о цене, «Да».</summary>
    private static void Follow(UiRequest? request, ThiefAction action)
    {
        switch (request)
        {
            case UiOpen { Logic: StealLogic steal } open:
                var ui = Open(steal, open.Screen);
                Assert.True(steal.Choices.Count > 0, $"{action}: нечего красть");
                steal.Handle(new UiClicked("BTN_YES"), ui);
                Assert.IsType<UiClose>(steal.TakeRequest());
                break;
            case UiAsk ask:
                Assert.Equal(ThiefAction.Bribe, action);
                ask.Yes();
                break;
            default:
                Assert.Fail($"{action}: после «ОК» ждали окно кражи или вопрос, а пришло {request}");
                break;
        }
    }

    private static UiScreenState Open(IWindowLogic logic, string screen)
    {
        var ui = new UiScreenState(logic.Shape(Shared.Value.Content.Screens.First(s => s.Id == screen)));
        logic.Open(ui);
        return ui;
    }

    private static (GameSession Session, WindowContext Context, ThiefTexts Texts) Begin()
    {
        var facts = Facts();
        var human = facts.GetProperty("human");
        var content = Shared.Value.Content;
        var setup = new D2.Data.Menu.NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == Map), Race = human.GetProperty("race_type").GetInt32(),
            LordCategory = human.GetProperty("lord_category").GetInt32(), Difficulty = 1,
        };
        var session = NewGame.Begin(ScenarioFile.Read(content.ScenarioPath(Map)), setup, content.Catalog, Shared.Value.Book, content.MapArt,
                                    new Random(1));
        var context = new WindowContext(session, session.ActiveHuman, content.Strings("ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        return (session, context, ThiefTexts.Load(Path.Combine(Data, "rules")));
    }

    private static IEnumerable<JsonElement> Zones() =>
        Facts().GetProperty("zones").EnumerateArray().Where(z => z.TryGetProperty("action", out _)).ToList();

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static JsonElement Facts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Disciples.sln"))) dir = dir.Parent;
        string path = Path.Combine(dir?.FullName ?? "", "docs", "synthetic-maps-port", Map, "facts.json");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        Skip.IfNot(Shared.Value.Content.Catalog.Scenarios.Any(s => s.Id == Map), "нет слоя mods/port-synth");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }
}
