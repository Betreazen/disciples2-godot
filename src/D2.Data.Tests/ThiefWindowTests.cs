using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Окна вора (<c>DLG_THIEF_ACTIONS</c>, <c>DLG_STEAL_*</c>, <c>DLG_ACTION_RESULT</c>, <c>DLG_THIEF_NOACTIONS</c>; <c>docs/ui-windows.md</c> §11).</summary>
[Trait("Category", "Corpus")]
public class ThiefWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Вор у чужого отряда: окно действий — весь список окна, недоступные тёмно-красным, «ОК» только у доступного; действие —
    /// окно итога «Успех!»/«Неудача!». Цель не для вора — окна нет.</summary>
    [SkippableFact]
    public void Thief_ActionsListAndResult()
    {
        var session = Game();
        var book = session.RequireBook();
        var map = session.Map;
        var thief = map.Stacks.Values.FirstOrDefault(s => s.Inside.Length == 0 && map.Players.ContainsKey(s.Owner)
            && map.Units.TryGetValue(s.LeaderId, out var l) && l.Hp > 0 && book.CategoryOf(l.TypeId) == D2.Rules.Battle.UnitCategory.Noble);
        Skip.If(thief is null, "в сохранении нет вора");
        var target = map.Stacks.Values.Where(s => !s.Owner.Equals(thief!.Owner, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0
                                                  && session.ThiefActions(thief.Id, s.Id).Count > 0)
                                      .OrderBy(s => Math.Abs(s.X - thief.X) + Math.Abs(s.Y - thief.Y)).FirstOrDefault();
        Skip.If(target is null, "нет цели для вора");
        var context = new WindowContext(session, thief!.Owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var texts = ThiefTexts.Load(Path.Combine(Data, "rules"));
        Assert.True(ThiefWindows.IsThief(context, thief.Id));
        Assert.Null(ThiefWindows.For(context, thief.Id, "нет-цели", texts));

        var open = ThiefWindows.For(context, thief.Id, target!.Id, texts)!;
        var logic = Assert.IsType<ThiefActionsLogic>(open.Logic);
        var ui = Open(logic, ThiefActionsLogic.Screen);
        output.WriteLine(thief.Id + " " + thief.Owner + " " + logic.Summary + " | " + string.Join(" / ", ui.Items("TLBOX_ACTIONS")));
        Assert.Equal(logic.Shown.Count, ui.Items("TLBOX_ACTIONS").Count);
        Assert.All(logic.Available, a => Assert.Contains(a, logic.Shown));
        foreach (var (a, i) in logic.Shown.Select((a, i) => (a, i)))
            Assert.StartsWith(logic.Available.Contains(a) ? @"\c000;000;000;" : @"\c128;000;000;", ui.Items("TLBOX_ACTIONS")[i]);
        Assert.True(ui.IsEnabled("BTN_OK"));
        Assert.Contains("Вероятность", ui.TextOf("TXT_DESC"));

        // Простое действие без выбора — сразу итог.
        var simple = logic.Available.First(a => a is not (ThiefAction.StealItem or ThiefAction.StealMerchant or ThiefAction.StealSpell
                                                          or ThiefAction.StealMage or ThiefAction.Bribe));
        int index = logic.Shown.ToList().IndexOf(simple);
        ui.Select("TLBOX_ACTIONS", index);
        logic.Handle(new UiListSelected("TLBOX_ACTIONS", index), ui);
        var sights = new SightRecorder();
        session.Observer = sights;
        logic.Handle(new UiClicked("BTN_OK"), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
        var result = Assert.IsType<ThiefResultLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        // Итог вора — уведомление показу (звук BKPOPUP ставит сессия Г) и зонду — по действию, ещё до окна итога.
        Assert.Contains(sights.Sights, s => s.Kind == TurnSightKind.ThiefResult && s.PlayerId == thief.Owner && s.Id == simple.ToString());
        var resultUi = Open(result, ThiefResultLogic.Screen);
        output.WriteLine($"{simple}: {logic.Report} | {resultUi.TextOf("TXT_ACTION_RESULT")} | {resultUi.TextOf("TXT_ACTION_RESULT_2")}");
        Assert.Equal(context.Text(logic.Report!.Success ? "X005TA0032" : "X005TA0031"), resultUi.TextOf("TXT_ACTION_RESULT"));
        Assert.StartsWith("gfx:Thief/", resultUi.Frame("IMG_ACTION"));
        result.Handle(new UiClicked("BTN_YES"), resultUi);
        Assert.IsType<UiClose>(result.TakeRequest());
    }

    /// <summary>Вор у бунтующей деревни: окно «нечего делать» с текстом движка «Пока город бунтует…» (<c>X005TA0689</c>, снимок 28 прогона
    /// 13.10), а не общим «Вор не может ничего предпринять против этого города» (<c>X005TA0526</c>).</summary>
    [SkippableFact]
    public void Thief_RiotingVillage_SaysCityRiots()
    {
        var session = Game();
        var map = session.Map;
        var book = session.RequireBook();
        var thief = map.Stacks.Values.FirstOrDefault(s => s.Inside.Length == 0 && map.Units.TryGetValue(s.LeaderId, out var l) && l.Hp > 0
                                                          && book.CategoryOf(l.TypeId) == D2.Rules.Battle.UnitCategory.Noble);
        Skip.If(thief is null, "в сохранении нет вора");
        var village = map.Sites.First(s => s.Kind == SiteKind.Village && !s.Owner.Equals(thief!.Owner, StringComparison.OrdinalIgnoreCase))
                      with { RiotTurn = map.Turn + 2 };
        map = GameSession.With(map, sites: [.. map.Sites.Select(s => s.Id == village.Id ? village : s)]);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, book);
        var context = new WindowContext(session, thief!.Owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));

        var open = ThiefWindows.For(context, thief.Id, village.Id, ThiefTexts.Load(Path.Combine(Data, "rules")))!;

        var logic = Assert.IsType<ThiefNoActionsLogic>(open.Logic);
        var ui = Open(logic, ThiefNoActionsLogic.Screen);
        Assert.Equal(context.Text("X005TA0689"), ui.TextOf("TXT_MESSAGE"));
    }

    private static UiScreenState Open(IWindowLogic logic, string screen)
    {
        var ui = new UiScreenState(logic.Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == screen)));
        logic.Open(ui);
        return ui;
    }

    /// <summary>Окно кражи: рамка выбранной ячейки — жёлтая <c>FFFF00</c> (у прочих списков — чёрная), первая ячейка выбрана.</summary>
    [SkippableFact]
    public void Steal_SelectionFrameIsYellow()
    {
        var session = Game();
        var map = session.Map;
        var target = map.Stacks.Values.FirstOrDefault(s => s.Items.Count > 0 && map.Players.ContainsKey(s.Owner));
        Skip.If(target is null, "нет отряда с предметами");
        var thief = map.Stacks.Values.First(s => s.Owner != target!.Owner && map.Players.ContainsKey(s.Owner));
        var context = new WindowContext(session, thief.Owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var logic = new StealLogic(context, thief.Id, target!.Id, ThiefAction.StealItem, _ => { });
        var ui = Open(logic, StealLogic.Screen(ThiefAction.StealItem));
        Assert.Equal(new UiColor(255, 255, 0), ui.MarkColor("MLBOX_LIST"));
        Assert.Equal(0, ui.ListSelected("MLBOX_LIST"));
    }

    /// <summary>Партия 15.10 (карта 2, stderr): «Да» в окне кражи сделало действие, вора повесили, и окно, спросив после этого свой выбор,
    /// падало <c>KeyNotFoundException</c> (<c>DLG_STEAL_ITEM</c>, <c>DLG_STEAL_SPELL</c>). Без вора выбор пуст, сводка — без исключения.</summary>
    [SkippableFact]
    public void Steal_ThiefGone_ChoicesEmpty()
    {
        var session = Game();
        var map = session.Map;
        var target = map.Stacks.Values.FirstOrDefault(s => s.Items.Count > 0 && map.Players.ContainsKey(s.Owner));
        Skip.If(target is null, "нет отряда с предметами");
        var viewer = map.Players.Keys.First(p => p != target!.Owner);
        var context = new WindowContext(session, viewer, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        const string hanged = "S073KC0fff";
        Assert.False(map.Stacks.ContainsKey(hanged));
        foreach (var action in new[] { ThiefAction.StealItem, ThiefAction.StealSpell, ThiefAction.StealMage, ThiefAction.StealMerchant })
        {
            var logic = new StealLogic(context, hanged, target!.Id, action, _ => { });
            Assert.Empty(logic.Choices);
            Assert.Equal("кража выбор=0", logic.Summary);
        }
    }

    /// <summary>Партия 16.10, пометка 5: «Да» окна кражи — ещё и подтверждение списка (<c>MLBOX_LIST</c> → <c>BTN_YES</c>), щелчок даёт
    /// <c>UiClicked</c> и <c>UiListConfirmed</c>, и действие шло дважды: успех, второй бросок — провал, вор повешен с украденным (мешок на клетке
    /// гибели), два окна итога. Теперь щелчок настоящим путём (<see cref="UiScreenState.Press"/>/<see cref="UiScreenState.Release"/>) — ровно
    /// одно действие и одно окно итога; провал — у торговца и мага ничего не убыло, успех — убыло ровно одно.</summary>
    [SkippableTheory]
    [InlineData(ThiefAction.StealMerchant)]
    [InlineData(ThiefAction.StealMage)]
    public void Steal_YesClick_ActsOnce(ThiefAction action)
    {
        // Бросок — генератор партии; сдвигаем его пустыми вытяжками, пока не увидим и успех (тут и шло второе действие), и провал.
        var seen = new HashSet<bool>();
        for (int skip = 0; skip < 40 && seen.Count < 2; skip++) seen.Add(StealOnce(action, skip));
        Assert.True(seen.Count == 2, $"за 40 сдвигов генератора нет и успеха, и провала: {string.Join(",", seen)}");
    }

    /// <summary>Одна кража щелчком по «Да»; true — удалась.</summary>
    private bool StealOnce(ThiefAction action, int skip)
    {
        var session = Game("rastlennaya-vlast-dwarf-t05-011.sg");
        var map = session.Map;
        var kind = action == ThiefAction.StealMerchant ? SiteKind.Merchant : SiteKind.Mage;
        var thief = map.Stacks.Values.OrderBy(s => s.Id).FirstOrDefault(s => s.Inside.Length == 0 && map.Players.ContainsKey(s.Owner)
            && map.Sites.Any(f => f.Kind == kind && session.ThiefActions(s.Id, f.Id).Contains(action)));
        Skip.If(thief is null, $"нет вора для {action}");
        var shop = map.Sites.First(f => f.Kind == kind && session.ThiefActions(thief!.Id, f.Id).Contains(action));
        int Wares() => action == ThiefAction.StealMerchant
            ? session.Map.Sites.First(f => f.Id == shop.Id).Stock.Sum(r => r.Count) : session.Map.Players[thief!.Owner].KnownSpells.Count;
        int before = Wares();
        var context = new WindowContext(session, thief!.Owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var logic = new ThiefActionsLogic(context, thief.Id, shop.Id, ThiefTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, ThiefActionsLogic.Screen);
        int index = logic.Shown.ToList().IndexOf(action);
        ui.Select("TLBOX_ACTIONS", index);
        logic.Handle(new UiListSelected("TLBOX_ACTIONS", index), ui);
        logic.Handle(new UiClicked("BTN_OK"), ui);
        var steal = Assert.IsType<StealLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        var stealUi = Open(steal, StealLogic.Screen(action));
        var sights = new SightRecorder();
        session.Observer = sights;
        for (int i = 0; i < skip; i++) session.Rng.Next(100);

        var yes = stealUi.Screen.Elements.First(e => e.Id == "BTN_YES").Rect;
        (int x, int y) = (yes.X + yes.Width / 2, yes.Y + yes.Height / 2);
        stealUi.Press(x, y);
        var events = stealUi.Release(x, y);
        Assert.Equal([new UiClicked("BTN_YES")], events);   // одно нажатие — одно событие (UiScreenState.LinkedList)
        foreach (var e in events) steal.Handle(e, stealUi);

        Assert.Single(sights.Sights, s => s.Kind == TurnSightKind.ThiefResult);
        var report = logic.Report!;
        var opened = new List<UiRequest>();
        while (logic.TakeRequest() is { } r) opened.Add(r);
        Assert.Collection(opened, r => Assert.IsType<UiClose>(r), r => Assert.IsType<ThiefResultLogic>(Assert.IsType<UiOpen>(r).Logic));
        output.WriteLine($"{action} сдвиг={skip}: успех={report.Success} казнён={report.ThiefDied} было={before} стало={Wares()}");
        Assert.Equal(report.Success ? before + (action == ThiefAction.StealMerchant ? -1 : 1) : before, Wares());
        Assert.Equal(!report.Success, report.ThiefDied);
        Assert.Equal(report.ThiefDied, !session.Map.Stacks.ContainsKey(thief.Id));
        return report.Success;
    }

    private static GameSession Game()
        => Game("rastlennaya-vlast-dwarf-t24-045.sg");

    private static GameSession Game(string save)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", save);
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }
}
