using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Окна дипломатии человека (<c>docs/ui-windows.md</c> §8): предложение ИИ продать предмет или заклинание (<c>DLG_OFFER</c>).</summary>
[Trait("Category", "Corpus")]
public class DiplomacyWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Human = "S073PL000a", Ai = "S073PL000b";

    /// <summary>Предложение предмета: текст с ценой и реальной стоимостью, значок, лицо; «Принять» без золота — X005TA0609 и отказ;
    /// с золотом — «да»; «Отказать» — «нет».</summary>
    [SkippableFact]
    public void TradeOffer_AcceptRefuseAndPoor()
    {
        var session = Game();
        var context = Context(session);
        var item = session.Book!.Items.Values.First(i => i.Value.Gold > 0);
        var answers = new List<bool>();
        var raceNames = RaceNames.Load(Path.Combine(Data, "rules"));
        var texts = StackTexts.Load(Path.Combine(Data, "rules"));
        string Name(string id) => context.Text(texts.Items.GetValueOrDefault(id, id));
        TradeOfferLogic Make(long price) => new(context, new TradeOffer(Ai, Human, item.Id, true, price), raceNames, Name, answers.Add);

        var logic = Make(1);
        var ui = Open(logic, TradeOfferLogic.Screen);
        output.WriteLine(ui.TextOf("TXT_INFO"));
        Assert.Contains($"{item.Value.Gold}", ui.TextOf("TXT_INFO"));
        Assert.Equal(Name(item.Id), ui.TextOf("TXT_ITEM_NAME"));
        Assert.StartsWith("IconItem:", ui.Frame("IMG_TRADED_ITEM"));
        Assert.StartsWith("Lords:", ui.Frame("IMG_FACE2"));
        Assert.False(logic.Allows("BTN_ACCEPT", ui));
        logic.Handle(new UiClicked("BTN_ACCEPT"), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
        Assert.Equal([true], answers);

        long gold = session.Map.Players[Human].Bank.Gold;
        var poor = Make(gold + 1);
        var poorUi = Open(poor, TradeOfferLogic.Screen);
        poor.Handle(new UiClicked("BTN_ACCEPT"), poorUi);
        Assert.IsType<UiClose>(poor.TakeRequest());
        Assert.Equal(context.Text("X005TA0609"), Assert.IsType<UiTell>(poor.TakeRequest()).Text);
        Assert.Equal([true, false], answers);

        var refuse = Make(1);
        refuse.Handle(new UiClicked("BTN_CANCEL"), Open(refuse, TradeOfferLogic.Screen));
        Assert.Equal([true, false, false], answers);
    }

    /// <summary>Сделка ИИ из модели партии (сессия А, <c>AllianceOffer.Trade</c>): экземпляр предмета — тип в окне; «Принять» —
    /// <see cref="GameSession.AnswerTradeOffer"/>: предмет в склад столицы человека, золото — продавцу, предложение снято.</summary>
    [SkippableFact]
    public void TradeOffer_FromGameStateItemDeal()
    {
        var game = Game();
        // Склад столицы ИИ — предмет из любого склада с предметами.
        var source = game.Map.Sites.First(s => s.Items.Count > 0 && s.Kind is SiteKind.Capital or SiteKind.Village);
        var fort = game.Map.Sites.First(s => s.Owner == Ai && s.Kind == SiteKind.Capital);
        string seller = Ai, instance = source.Items[0];
        Assert.Null(game.ExchangeItem(source.Id, fort.Id, instance));
        var map = game.Map;
        var deal = new AllianceOffer(seller, Human, map.Turn, new AiTradeOffer(AiGiftKind.Item, instance, fort.Id, 10));
        var session = new GameSession(game.State with { Offers = [deal] }, SharedContent.Load(Data).MapArt, game.Book!);
        var offer = TradeOffer.Of(session.State.Offers[0], session.Map)!;
        Assert.True(offer.IsItem);
        Assert.Equal(session.Map.ItemTypes[instance], offer.ThingId);
        Assert.Null(TradeOffer.Of(deal with { Trade = new AiTradeOffer(AiGiftKind.Gold, "", "", 100) }, session.Map));

        long gold = session.Map.Players[Human].Bank.Gold;
        var context = Context(session);
        var logic = new TradeOfferLogic(context, offer, RaceNames.Load(Path.Combine(Data, "rules")), id => id,
                                        yes => Assert.Null(session.AnswerTradeOffer(seller, Human, yes)));
        var ui = Open(logic, TradeOfferLogic.Screen);
        Assert.StartsWith("IconItem:", ui.Frame("IMG_TRADED_ITEM"));
        logic.Handle(new UiClicked("BTN_ACCEPT"), ui);
        Assert.Empty(session.State.Offers);
        Assert.Equal(gold - 10, session.Map.Players[Human].Bank.Gold);
        var capital = session.Map.Sites.First(s => s.Owner == Human && s.Kind == SiteKind.Capital);
        Assert.Contains(instance, capital.Items);
    }

    /// <summary>Продажа из дипломатии: склад своей крепости → окно цены (от цены, по 50, от 50 до 2 × цена) → сделка сессии А; итог —
    /// «думает над ответом» и принято/отвергнуто; при согласии предмет у покупателя, золото у продавца. Отказы продажи до отправки.</summary>
    [SkippableFact]
    public void TradeItem_SellFromStoreThroughPrice()
    {
        var session = Game();
        var source = session.Map.Sites.First(s => s.Items.Count > 0 && s.Kind is SiteKind.Capital or SiteKind.Village);
        var capital = session.Map.Sites.First(s => s.Owner == Human && s.Kind == SiteKind.Capital);
        string item = source.Items[0];
        if (!source.Id.Equals(capital.Id, StringComparison.OrdinalIgnoreCase)) Assert.Null(session.ExchangeItem(source.Id, capital.Id, item));
        var context = Context(session) with { ItemTexts = StackTexts.Load(Path.Combine(Data, "rules")) };

        var trade = new TradeItemLogic(context, Ai, context.ItemTexts!);
        var ui = Open(trade, TradeItemLogic.Screen);
        Assert.Equal(capital.Id, trade.Forts[0].Id);
        Assert.Contains(item, trade.Items);
        Assert.True(ui.IsEnabled("BTN_TRADE"));
        ui.Select("MLBOX_RINVENTORY", trade.Items.ToList().IndexOf(item));
        trade.Handle(new UiListSelected("MLBOX_RINVENTORY", trade.Items.ToList().IndexOf(item)), ui);
        trade.Handle(new UiClicked("BTN_TRADE"), ui);
        var price = Assert.IsType<TradePriceLogic>(Assert.IsType<UiOpen>(trade.TakeRequest()).Logic);
        var priceUi = Open(price, TradePriceLogic.Screen);
        output.WriteLine(priceUi.TextOf("TXT_INFO"));
        long start = price.Amount;
        Assert.True(start >= TradePriceLogic.MinGold);
        price.Handle(new UiClicked("BTN_UP"), priceUi);
        Assert.Equal(Math.Min(start + 50, 2 * Math.Max(start, 1)), price.Amount);
        for (int i = 0; i < 100; i++) price.Handle(new UiClicked("BTN_DOWN"), priceUi);
        Assert.Equal(TradePriceLogic.MinGold, price.Amount);
        Assert.Equal("50", priceUi.TextOf("TXT_GOLD"));

        long gold = session.Map.Players[Human].Bank.Gold;
        price.Handle(new UiClicked("BTN_OK"), priceUi);
        Assert.IsType<UiClose>(price.TakeRequest());
        string told = Assert.IsType<UiTell>(price.TakeRequest()).Text;
        output.WriteLine($"{price.Answer}: {told}");
        Assert.Contains(session.Map.Players[Ai].Name, told);
        var aiCapital = session.Map.Sites.First(s => s.Owner == Ai && s.Kind == SiteKind.Capital);
        if (price.Answer?.Accepted == true)
        {
            Assert.Contains(context.Text("X005TA0038"), told);
            Assert.Contains(item, aiCapital.Items);
            Assert.Equal(gold + 50, session.Map.Players[Human].Bank.Gold);
        }
        else
        {
            Assert.Equal(false, price.Answer?.Accepted);
            Assert.Contains(context.Text("X005TA0037"), told);
            Assert.Contains(item, session.Map.Sites.First(s => s.Id == capital.Id).Items);
        }

    }

    /// <summary>Поле дипломатии: сам игрок первым, строки X005TA0314, курсоры шкал; выбор другого игрока — панель включает кнопки;
    /// дань стрелками по D_GOLD — золото переходит, отношение растёт; союз — вопрос или «добейтесь расположения».</summary>
    [SkippableFact]
    public void Diplomacy_FieldPanelAndTribute()
    {
        var session = Game();
        var context = Context(session);
        var model = new DiplomacyModel(context, RaceNames.Load(Path.Combine(Data, "rules")));
        var field = new DiplomacyFieldLogic(model);
        var fieldUi = Open(field, DiplomacyFieldLogic.Screen);
        var players = model.Players;
        output.WriteLine(string.Join(", ", players.Select(p => p.Id)) + " | " + fieldUi.TextOf("TXT_PLAYER_CUR"));
        Assert.Equal(Human, players[0].Id);
        Assert.True(players.Count >= 2);
        Assert.Contains("Повелитель", fieldUi.TextOf("TXT_PLAYER_CUR"));
        Assert.Contains(fieldUi.Screen.Elements, e => e.Id == "IMG_CURSOR1");
        // Слот — круглый портрет 130×142 из Faces (%sLORD%d, 0x5c5424), кадр есть в описи графики.
        Assert.Equal($"Faces:{players[0].RaceId}LORD{players[0].Face + 1}", fieldUi.Frame("IMG_RACE1"));
        Assert.NotNull(SharedContent.Load(Data).Gfx.Image("Faces", $"{players[0].RaceId}LORD{players[0].Face + 1}"));
        // Шкала (0x48311f): окно полосы 30×360 — при войне (0) нижняя красная часть, при 100 — верх; курсор от низа вверх.
        var meterRect = fieldUi.Screen.Elements.First(e => e.Id == "IMG_METER1").Rect;
        Assert.Equal(new UiRect(4, 185, 22, 175), DiplomacyFieldLogic.MeterWindow(meterRect, 0));
        Assert.Equal(new UiRect(4, 0, 22, 175), DiplomacyFieldLogic.MeterWindow(meterRect, 100));
        Assert.Equal(meterRect.Y + 175 - 15, DiplomacyFieldLogic.CursorTop(meterRect, 0));
        Assert.Equal(meterRect.Y, DiplomacyFieldLogic.CursorTop(meterRect, 100));
        int rel = model.Current(players[0], players[1]);
        Assert.Equal(UiFrameRef.Cropped("DLG_ISO_DIPLO_METER", DiplomacyFieldLogic.MeterWindow(meterRect, rel)), fieldUi.Frame("IMG_METER1"));
        Assert.Equal(("DLG_ISO_DIPLO_METER", new UiRect(4, 185, 22, 175)), UiFrameRef.Crop("DLG_ISO_DIPLO_METER[4,185,22,175]"));
        Assert.Null(UiFrameRef.Crop("DLG_ISO_DIPLO_METER"));
        var panel = new DiplomacyPanelLogic(model);
        var panelUi = Open(panel, DiplomacyPanelLogic.Screen);
        Assert.False(panelUi.IsEnabled("BTN_GIVE_RESS"));   // выбран сам

        var slot2 = fieldUi.Screen.Elements.First(e => e.Id == "IMG_RACE2").Rect;
        Assert.Equal("+DRAGDROP_BIGFACE_RIGHT", fieldUi.Frame("IMG_RACE1" + DiplomacyFieldLogic.RingSuffix));   // выбран сам — кольцо своего слота
        field.Release(slot2.X + 5, slot2.Y + 5, fieldUi);
        panel.Refresh(panelUi);
        // Чужой слот: портрет отражён, кольцо LEFT по центру слота сложением (0x5c96ae, 0x484b53).
        Assert.EndsWith("LORD" + (model.SelectedPlayer.Face + 1) + "|", fieldUi.Frame("IMG_RACE2"));
        Assert.Equal("+DRAGDROP_BIGFACE_LEFT", fieldUi.Frame("IMG_RACE2" + DiplomacyFieldLogic.RingSuffix));
        Assert.Null(fieldUi.Frame("IMG_RACE1" + DiplomacyFieldLogic.RingSuffix));
        Assert.Equal(new UiRect(slot2.X - 25, slot2.Y - 19, 180, 180),
                     fieldUi.Screen.Elements.First(e => e.Id == "IMG_RACE2" + DiplomacyFieldLogic.RingSuffix).Rect);
        var other = model.SelectedPlayer;
        Assert.NotEqual(Human, other.Id);
        Assert.True(panelUi.IsEnabled("BTN_GIVE_RESS"));
        Assert.Contains(other.Name, panelUi.TextOf("TXT_LABEL1"));

        int before = model.Current(players[0], other);
        long gold = session.Map.Players[Human].Bank.Gold, theirs = session.Map.Players[other.Id].Bank.Gold;
        panel.Handle(new UiClicked("BTN_GIVE_RESS"), panelUi);
        var give = Assert.IsType<GiveResourceLogic>(Assert.IsType<UiOpen>(panel.TakeRequest()).Logic);
        var giveUi = Open(give, GiveResourceLogic.Screen);
        give.Handle(new UiClicked("BTN_UP"), giveUi);
        give.Handle(new UiClicked("BTN_UP"), giveUi);
        long step = session.Book!.Diplomacy.GoldPerPoint;
        Assert.Equal(2 * step, give.Amount);
        give.Handle(new UiClicked("BTN_OK"), giveUi);
        Assert.IsType<UiClose>(give.TakeRequest());
        Assert.Equal(gold - (2 * step), session.Map.Players[Human].Bank.Gold);
        Assert.Equal(Math.Min(9999, theirs + (2 * step)), session.Map.Players[other.Id].Bank.Gold);
        output.WriteLine($"отношение {before} → {model.Current(players[0], other)}");
        Assert.True(model.Current(players[0], other) >= before);

        panel.Handle(new UiClicked("BTN_PROPOSE_ALLIANCE"), panelUi);
        var answer = panel.TakeRequest();
        if (model.Peaceful(players[0], other)) Assert.IsType<UiAsk>(answer);
        else Assert.StartsWith("Для того, чтобы просить о перемирии", Assert.IsType<UiTell>(answer).Text);
        if (!model.Allies(players[0], other)) Assert.Equal("союза нет", session.BreakAllianceBy(Human, other.Id));
    }

    /// <summary>«Обменяться областями» — раз в день на цель (<c>0x48445e</c>: день из <c>MapPlayer.MapExchanges</c> ≥ текущего —
    /// <c>X160TA0023</c>). Память — поле игрока, а не окна: новое окно (как после загрузки) тоже отказывает; на следующий день — снова можно.</summary>
    [SkippableFact]
    public void ExchangeFog_OncePerDayPerTarget_FromPlayerState()
    {
        var session = Game();
        var map = session.Map;
        session = new GameSession(session.State with
        {
            Looks = GameSession.Retold(session.Looks, GameSession.With(map, relations: [.. map.Relations.Select(r => r with { Raw = 90u << 1 })]),
                                       session.Looks.Stacks),
        }, session.Art, session.Book);
        UiRequest? Click(GameSession s)
        {
            var model = new DiplomacyModel(Context(s), RaceNames.Load(Path.Combine(Data, "rules")));
            model.Selected = model.Players.ToList().FindIndex(p => !s.State.Info.Humans.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
            Assert.True(model.Selected > 0);
            var panel = new DiplomacyPanelLogic(model);
            panel.Handle(new UiClicked("BTN_EXCHANGE_FOG"), Open(panel, DiplomacyPanelLogic.Screen));
            return panel.TakeRequest();
        }
        string already = Context(session).Text("X160TA0023");

        Assert.IsType<UiAsk>(Click(session)).Yes();
        Assert.Equal(already, Assert.IsType<UiTell>(Click(session)).Text);

        var next = new GameSession(session.State with
        {
            Looks = GameSession.Retold(session.Looks, GameSession.With(session.Map, turn: session.Map.Turn + 1), session.Looks.Stacks),
        }, session.Art, session.Book);
        Assert.IsType<UiAsk>(Click(next));
    }

    /// <summary>Журнал (DLG_QUEST_LOG): строки X005TA0912 по записям игрока, выбрана последняя — текст её попапа; пусто —
    /// X005TA0916. «Задание»: длинная история — X005TA0832 с целью, метки %N% — переводы строк.</summary>
    [SkippableFact]
    public void QuestLogAndBriefing()
    {
        string path = D2.Data.Play.ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "Power to Corrupt");
        Skip.IfNot(File.Exists(path), "нет сценария");
        var file = D2.Data.Play.ScenarioFile.Read(path);
        var session = Game();
        var context = Context(session);
        var empty = new QuestLogLogic(context);
        var emptyUi = Open(empty, QuestLogLogic.Screen);
        Assert.Empty(empty.Entries);

        var ev = file.Events.Events.First(e => e.Effects.Any(x => x is PopupEffect));
        int n = ev.Effects.ToList().FindIndex(x => x is PopupEffect);
        var progress = D2.Rules.Map.EventProgress.Empty.Logged(Human, 3, ev.Id, n).Logged(Human, 5, ev.Id, n);
        var logged = new GameSession(session.State with { Events = file.Events, EventProgress = progress }, session.Art, session.Book);
        var log = new QuestLogLogic(Context(logged));
        var ui = Open(log, QuestLogLogic.Screen);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(1, log.Selected);
        Assert.Equal(((PopupEffect)ev.Effects[n]).Text, ui.TextOf("TXT_TEXT"));
        // Картинка — по центру места (флаги 9 setImage), справа — отражённая (снимки «Задания» 28.09: 0,00 % с обеих сторон).
        var shown = (PopupEffect)ev.Effects[n];
        if (shown.Image.Length > 0)
        {
            string side = shown.LeftSide ? "IMG_LEFTSIDE" : "IMG_RIGHTSIDE";
            string picture = EventPopupLogic.GfxFrame("Events", shown.Image);
            Assert.Equal(shown.LeftSide ? picture : UiFrameRef.Mirrored(picture), ui.Frame(side));
            Assert.Equal(UiImageAlign.Center, ui.ImageAlign(side));
        }

        Assert.Equal(@"a\n\n\nb", GameBriefingLogic.Breaks("a%3%b%12%"));
        var scenario = SharedContent.Load(Data).Catalog.Scenarios.First(s => s.BriefingLong.Trim().Length > 0);
        var brief = new GameBriefingLogic(context.Text, scenario, "Цель теста");
        var briefUi = Open(brief, GameBriefingLogic.Screen);
        Assert.Contains("Цель теста", briefUi.TextOf("TXT_TEXT_SCROLL"));
        Assert.Contains(scenario.Name, briefUi.TextOf("TXT_TEXT_SCROLL"));
        _ = emptyUi;

        // Автопрокрутка по точке: текст на 45 ниже верха рамки, 1500 мс ждёт, затем точка за 2 тика по 66 мс; кнопки ±10 и без задержки.
        Assert.Equal(GameBriefingLogic.StartOffset, briefUi.TextOffset("TXT_TEXT_SCROLL"));
        brief.Tick(1400, briefUi);
        Assert.Equal(0, brief.Pixels);
        brief.Tick(100 + (2 * 66 * 16), briefUi);
        Assert.Equal(16, brief.Pixels);
        Assert.Equal(GameBriefingLogic.StartOffset - 16, briefUi.TextOffset("TXT_TEXT_SCROLL"));
        brief.Handle(new UiClicked("BTN_SCROLL_UP"), briefUi);
        Assert.Equal(26, brief.Pixels);
        brief.Handle(new UiClicked("BTN_SCROLL_DOWN"), briefUi);
        brief.Handle(new UiClicked("BTN_SCROLL_DOWN"), briefUi);
        brief.Handle(new UiClicked("BTN_SCROLL_DOWN"), briefUi);
        Assert.Equal(0, brief.Pixels);
        // Сцена свёрстала текст высотой 300: конец — низ на 76 от верха рамки, сдвиг 45 + 300 − 76 = 269; щелчок вне рамки — сразу туда,
        // щелчок в рамке — ничего; дальше автопрокрутка стоит.
        briefUi.ReportTextHeight("TXT_TEXT_SCROLL", 300);
        var frame = briefUi.Screen.Elements.First(e => e.Id == "TXT_TEXT_SCROLL").Rect;
        brief.Press(frame.X + 5, frame.Y + 5, briefUi);
        Assert.Equal(0, brief.Pixels);
        brief.Press(frame.X + frame.Width + 40 < 800 ? frame.X + frame.Width + 40 : 5, 5, briefUi);
        Assert.Equal(269, brief.Pixels);
        Assert.Equal(GameBriefingLogic.StartOffset - 269, briefUi.TextOffset("TXT_TEXT_SCROLL"));
        brief.Tick(10_000, briefUi);
        Assert.Equal(269, brief.Pixels);
        Assert.Equal(2, brief.TicksPerPixel);

        // Клавиши (0x48201d): служебные — горячим клавишам кнопок, любая другая — сразу к концу и съедена; правая кнопка и двойной
        // щелчок — к концу и в рамке текста (0x481fac).
        foreach (var (make, ending) in new (Func<GameBriefingLogic, UiScreenState, bool>, bool)[]
                 {
                     ((b, u) => b.Key('Q', u), true), ((b, u) => b.Key(0, u), true), ((b, u) => b.Key(0x10, u), true),
                     ((b, u) => b.Key(0x0D, u), false), ((b, u) => b.Key(0x28, u), false), ((b, u) => b.Key(0x21, u), false),
                     ((b, u) => { b.RightPress(frame.X + 5, frame.Y + 5, u); return true; }, true),
                     ((b, u) => { b.DoubleClick(frame.X + 5, frame.Y + 5, u); return true; }, true),
                 })
        {
            var again = new GameBriefingLogic(context.Text, scenario, "Цель теста");
            var againUi = Open(again, GameBriefingLogic.Screen);
            againUi.ReportTextHeight("TXT_TEXT_SCROLL", 300);
            Assert.Equal(ending, make(again, againUi));
            Assert.Equal(ending ? 269 : 0, again.Pixels);
        }
        Assert.Equal(5, new GameBriefingLogic(context.Text, scenario with { BriefingLong = "%05%История" }, "").TicksPerPixel);
        Assert.Equal(2, new GameBriefingLogic(context.Text, scenario with { BriefingLong = "%00%История" }, "").TicksPerPixel);
    }

    /// <summary>«Задание» — тащение текста левой кнопкой (<c>0x481fac</c> → <c>0x482062</c>): нажатие в рамке ничего не прокручивает, текст идёт
    /// за мышью между стартом и концом, отпускание снимает тащение; защёлки конца нет — оттащенный от конца текст автопрокрутка везёт снова.</summary>
    [SkippableFact]
    public void Briefing_DragScrollsText_AutoScrollResumes()
    {
        var context = Context(Game());
        var scenario = SharedContent.Load(Data).Catalog.Scenarios.First(s => s.BriefingLong.Trim().Length > 0);
        var brief = new GameBriefingLogic(context.Text, scenario, "Цель теста");
        var ui = Open(brief, GameBriefingLogic.Screen);
        ui.ReportTextHeight("TXT_TEXT_SCROLL", 300);   // конец — 45 + 300 − 76 = 269
        var frame = ui.Screen.Elements.First(e => e.Id == "TXT_TEXT_SCROLL").Rect;
        int y = frame.Y + 50;

        brief.Press(frame.X + 5, y, ui);
        Assert.True(brief.Dragging);
        Assert.Equal(0, brief.Pixels);
        brief.Motion(frame.X + 90, y - 30, ui);   // мышь вверх — текст вверх; X не важен
        Assert.Equal(30, brief.Pixels);
        Assert.Equal(GameBriefingLogic.StartOffset - 30, ui.TextOffset("TXT_TEXT_SCROLL"));
        brief.Motion(frame.X + 5, y + 100, ui);   // вниз дальше старта — стоп на старте
        Assert.Equal(0, brief.Pixels);
        brief.Motion(5, y - 500, ui);             // за пределами рамки и экрана — тоже, но не дальше конца
        Assert.Equal(269, brief.Pixels);
        brief.Release(5, 5, ui);
        Assert.False(brief.Dragging);
        brief.Motion(5, 5, ui);
        Assert.Equal(269, brief.Pixels);

        brief.Press(frame.X + 5, y, ui);
        brief.Motion(frame.X + 5, y + 20, ui);
        brief.Release(frame.X + 5, y + 20, ui);
        Assert.Equal(249, brief.Pixels);
        brief.Tick(2 * 66 * 5, ui);               // задержка снята тащением — едет сразу
        Assert.Equal(254, brief.Pixels);
    }

    /// <summary>Автопрокрутка кадрами сцены по 16 мс (а не одним большим шагом): остаток меньше тика (66 мс) копится, текст едет плавно —
    /// точка за 2 тика, как при шаге в 132 мс. Раньше каждый кадр делился на 66 нацело, давал 0, и текст в игре стоял (замечание
    /// пользователя 11.10: вступление миссии не прокручивалось, кадр сменялся с названия на цели).</summary>
    [SkippableFact]
    public void Briefing_AutoScroll_AdvancesWithFrameSizedTicks()
    {
        var context = Context(Game());
        var scenario = SharedContent.Load(Data).Catalog.Scenarios.First(s => s.BriefingLong.Trim().Length > 0);
        var brief = new GameBriefingLogic(context.Text, scenario, "Цель теста");
        var ui = Open(brief, GameBriefingLogic.Screen);
        ui.ReportTextHeight("TXT_TEXT_SCROLL", 300);   // конец — 45 + 300 − 76 = 269
        const int frame = 16;
        int elapsed = 0;
        var seen = new List<int>();
        while (elapsed < GameBriefingLogic.StartDelayMs + (2 * GameBriefingLogic.TickMs * 10))
        {
            brief.Tick(frame, ui);
            elapsed += frame;
            seen.Add(brief.Pixels);
        }
        Assert.Equal(0, seen[(GameBriefingLogic.StartDelayMs / frame) - 1]);   // задержка старта 1500 мс
        Assert.InRange(brief.Pixels, 9, 10);                                    // 1320 мс по 16 — 10 точек (остаток кадров — меньше точки)
        Assert.All(seen.Zip(seen.Skip(1)), p => Assert.InRange(p.Second - p.First, 0, 1));   // плавно: не больше точки за кадр
        Assert.Equal(GameBriefingLogic.StartOffset - brief.Pixels, ui.TextOffset("TXT_TEXT_SCROLL"));
    }

    /// <summary>Карточка игрока (<c>DLG_R_C_PLAYER</c>, <c>0x577a0c</c>) по правой кнопке в дипломатии (<c>0x48522d</c>): над портретом слота
    /// поля — игрок слота, над лицом выбранного в панели — выбранный; отпускание закрывает. Сам — <c>X005TA0350</c>; другой —
    /// <c>X005TA0351</c>, два перевода строки и отношение (война <c>0352</c>, мир <c>0353</c> + союз <c>0355</c>/нет <c>0354</c>);
    /// <c>%PLAYER%</c> — имя, <c>%TYPE%</c> — вид лорда (<c>0x577d28</c>); портрет — крупный <c>LORD{раса}L0{лицо+1}</c>.</summary>
    [SkippableFact]
    public void PlayerCard_FromFieldSlotAndPanelFace()
    {
        var session = Game();
        var context = Context(session);
        var model = new DiplomacyModel(context, RaceNames.Load(Path.Combine(Data, "rules")));
        var field = new DiplomacyFieldLogic(model);
        var fieldUi = Open(field, DiplomacyFieldLogic.Screen);
        var players = model.Players;
        var slot2 = fieldUi.Screen.Elements.First(e => e.Id == "IMG_RACE2").Rect;
        field.RightPress(slot2.X + 5, slot2.Y + 5, fieldUi);
        var card = Assert.IsType<PlayerCardLogic>(Assert.IsType<UiOpen>(field.TakeRequest()).Logic);
        var cardUi = Open(card, PlayerCardLogic.Screen);
        var other = players[1];
        int me = model.RaceType(players[0]), them = model.RaceType(other);
        string relation = model.AtWar(players[0], other) ? context.Text("X005TA0352")
            : model.Peaceful(players[0], other) ? context.Text("X005TA0353") + context.Text(model.Allies(players[0], other) ? "X005TA0355" : "X005TA0354") : "";
        string text = cardUi.TextOf("TXT_PLAYER") ?? "";
        output.WriteLine($"{other.Id} {me}/{them}: {text}");
        Assert.Equal(UiTemplate.Fill(context.Text("X005TA0351") + "\n\n" + relation, ("PLAYER", other.Name),
                                     ("TYPE", context.Text(session.Book!.LordCategory(other.LordId) switch { 0 => "X005TA0066", 1 => "X005TA0065", _ => "X005TA0067" }))),
                     text);
        Assert.StartsWith(other.Name, text, StringComparison.Ordinal);
        Assert.Equal(LordFaces.Large(them, other.Face), cardUi.Frame("IMG_FACE"));
        Assert.NotNull(SharedContent.Load(Data).Gfx.Image("Lords", LordFaces.Large(them, other.Face)["Lords:".Length..]));
        card.RightRelease(0, 0, cardUi);
        Assert.IsType<UiClose>(card.TakeRequest());

        var panel = new DiplomacyPanelLogic(model);
        var panelUi = Open(panel, DiplomacyPanelLogic.Screen);
        panel.RightPress(0, 0, panelUi);   // мимо лица — ничего
        Assert.Null(panel.TakeRequest());
        var face = panelUi.Screen.Elements.First(e => e.Id == "IMG_LORD_FACE").Rect;
        panel.RightPress(face.X + 3, face.Y + 3, panelUi);
        var self = Assert.IsType<PlayerCardLogic>(Assert.IsType<UiOpen>(panel.TakeRequest()).Logic);
        var selfUi = Open(self, PlayerCardLogic.Screen);
        Assert.Contains(players[0].Name, selfUi.TextOf("TXT_PLAYER"));
        Assert.EndsWith(context.Text("X005TA0350").Split('%')[^1], selfUi.TextOf("TXT_PLAYER"), StringComparison.Ordinal);
        Assert.Equal("Lords:LORDNEL01", LordFaces.Large(4, 3));
    }

    // --- помощники ---

    private static WindowContext Context(GameSession session) =>
        new(session, Human, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));

    private static UiScreenState Open(IWindowLogic logic, string screen)
    {
        var ui = new UiScreenState(logic.Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == screen)));
        logic.Open(ui);
        return ui;
    }

    private static GameSession Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }
}
