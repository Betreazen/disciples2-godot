using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Гонец «горячего стула» (<see cref="HotseatCourierLogic"/>, <c>DLG_HOTSEAT_POPUP</c> режима 1): предложение человека другому
/// человеку — сразу, в ход предложившего; «ОК» гонца — окно ответа адресата; итог — предложившему.</summary>
[Trait("Category", "Corpus")]
public class HotseatCourierTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static (GameSession Session, MapPlayer A, MapPlayer B, WindowContext Context) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var solo = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt, book);
        var a = solo.Map.Players.Values.First(p => p.IsHuman);
        var b = solo.Map.Players.Values.First(p => p.Id != a.Id && book.Economy.Races.TryGetValue(p.RaceId, out var r) && r.Playable
                                                   && p.CapturedBy.Length == 0);
        var session = new GameSession(solo.State with { Info = solo.State.Info with { Humans = [a.Id, b.Id] } }, solo.Art, book);
        var context = new WindowContext(session, a.Id, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        return (session, a, b, context);
    }

    private static UiScreenState Open(IWindowLogic logic, string screen)
    {
        var ui = new UiScreenState(logic.Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == screen)));
        logic.Open(ui);
        return ui;
    }

    [SkippableFact]
    public void Alliance_CourierThenAnswerThenResultToProposer()
    {
        var (session, a, b, context) = Game();
        Assert.True(session.Hotseat);
        Assert.Null(session.ProposeAllianceBy(a.Id, b.Id));   // человеку — ждёт ответа
        var offer = Assert.Single(session.State.Offers, o => o.From == a.Id && o.To == b.Id);

        var answer = new UiOpen(AllianceOfferLogic.Screen, new AllianceOfferLogic(context, offer, RaceNames.Load(Path.Combine(Data, "rules"))));
        var courier = new HotseatCourierLogic(new HotseatTurnLogic(b, SharedContent.Load(Data).Catalog, context.Text, HotseatCall.Message, sender: a), answer);
        var ui = Open(courier, HotseatCourierLogic.Screen);
        string text = ui.TextOf("TXT_TEXT")!;
        Assert.Contains(a.Name.Trim(), text);   // шлёт гонца — предложивший
        Assert.Contains(b.Name.Trim(), text);   // на конверте — адресат
        Assert.False(courier.Allows("BTN_OK", ui));
        courier.Handle(new UiClicked("BTN_OK"), ui);
        courier.Handle(new UiClicked("BTN_OK"), ui);   // второй щелчок — ничего
        Assert.IsType<UiClose>(courier.TakeRequest());
        Assert.Same(answer, courier.TakeRequest());
        Assert.Null(courier.TakeRequest());

        var reply = (AllianceOfferLogic)answer.Logic;
        var replyUi = Open(reply, AllianceOfferLogic.Screen);
        reply.Handle(new UiClicked("BTN_OK"), replyUi);
        Assert.IsType<UiClose>(reply.TakeRequest());
        string result = Assert.IsType<UiTell>(reply.TakeRequest()).Text;
        Assert.Contains(b.Name, result);
        Assert.Contains("союзники", result);
        Assert.Empty(session.State.Offers);
    }

    [SkippableFact]
    public void TradeToHuman_NoThinkingMessage_WaitsForCourier()
    {
        var (solo, a, b, _) = Game();
        // Продавцу — заклинание, которого у покупателя нет (у этого человека своих известных нет).
        string? spell = solo.Book!.Spells.Values.FirstOrDefault(sp => sp.Category != D2.Rules.Magic.SpellCategory.Summon
            && !solo.Map.Players[b.Id].KnownSpells.Contains(sp.Id, StringComparer.OrdinalIgnoreCase))?.Id;
        Skip.If(spell is null, "нечего продать");
        var players = solo.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[a.Id] = players[a.Id] with { KnownSpells = [.. players[a.Id].KnownSpells, spell!.ToUpperInvariant()] };
        var session = new GameSession(solo.State with { Looks = solo.Looks with { Map = GameSession.With(solo.Map, players: players) } }, solo.Art, solo.Book);
        var context = new WindowContext(session, a.Id, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
        var price = new TradePriceLogic(context, b.Id, new AiTradeOffer(AiGiftKind.Spell, spell!, "", 0), $"IconSpel:{spell}", spell!, 100);
        var ui = Open(price, TradePriceLogic.Screen);
        price.Handle(new UiClicked("BTN_OK"), ui);
        Assert.IsType<UiClose>(price.TakeRequest());
        Assert.Null(price.TakeRequest());   // итог — после ответа адресата
        Assert.NotNull(price.Answer);
        Assert.Null(price.Answer?.Accepted);
        Assert.Contains(session.State.Offers, o => o.Trade is not null && o.From == a.Id && o.To == b.Id);
        Assert.Contains(context.Text("X005TA0038"), TradePriceLogic.Result(context, b.Name, accepted: true));
    }
}
