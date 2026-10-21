using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Magic;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>«Подарок» ИИ в партии (<c>GameSession.Trade.cs</c>): кандидаты, сделка ИИ с ИИ, сделка с человеком через ожидание
/// ответа, подарок золотом. Сохранение партии human, как в <see cref="DiplomacyActionsTests"/>.</summary>
[Trait("Category", "Corpus")]
public class AiGiftSessionTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private const string Human = "S073PL000a", Ai1 = "S073PL0009", Ai2 = "S073PL000b";
    private const string Instance = "S073IMffff";

    /// <summary>Партия с отношением 90 без союзов; в складе столицы <see cref="Ai1"/> — экземпляр <see cref="Instance"/>
    /// дешёвого предмета.</summary>
    private static (GameSession Game, Item Item) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var item = book.Items.Values.First(i => i.Category != ItemCategory.Talisman && i.Value.Gold is > 0 and < 1000);
        var map = looks.Map;
        var sites = map.Sites.Select(s => s.Kind == SiteKind.Capital && s.Owner == Ai1 ? s with { Items = [.. s.Items, Instance] } : s).ToList();
        var types = map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[Instance] = item.Id;
        var relations = map.Relations.Select(r => r with { Raw = 90u << 1 }).ToList();
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = GameSession.WithBank(players[Human], players[Human].Bank with { Gold = 5000 });
        looks = GameSession.Retold(looks, GameSession.With(map, players: players, sites: sites, itemTypes: types, relations: relations),
                                   looks.Stacks);
        return (new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt, book), item);
    }

    private static MapSite Capital(GameSession g, string player) => g.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == player);

    /// <summary>Кандидаты-предметы — склады своих крепостей с ценностью; кандидаты-заклинания — без тех, что цель знает.</summary>
    [SkippableFact]
    public void Candidates_FromForts_AndUnknownSpells()
    {
        var (game, item) = Game();
        Assert.Contains(new AiGiftItem(Capital(game, Ai1).Id, Instance, (int)item.Value.Gold), game.GiftItems(Ai1));
        var known = game.Map.Players[Ai2].KnownSpells;
        Assert.All(game.GiftSpells(Ai1, Ai2), s => Assert.DoesNotContain(s.Spell, known, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(game.GiftSpells(Ai1, Ai2).Select(s => s.Spell).Order(StringComparer.Ordinal), game.GiftSpells(Ai1, Ai2).Select(s => s.Spell));
    }

    /// <summary>Сделка ИИ с ИИ: цель отвечает броском (<see cref="AiDiplomacy.AnswerTrade"/>), затем предлагавший пишет
    /// сообщение. Согласие — золото предлагавшему, предмет — в склад столицы цели, отношения за обмен.</summary>
    [SkippableFact]
    public void ItemTrade_AiToAi_SettlesAtOnce()
    {
        var (game, item) = Game();
        var offer = new AiTradeOffer(AiGiftKind.Item, Instance, Capital(game, Ai1).Id, (int)item.Value.Gold + 90);
        var expected = game.Rng.Clone();
        long gold1 = game.Map.Players[Ai1].Bank.Gold, gold2 = game.Map.Players[Ai2].Bank.Gold;
        bool accept = AiDiplomacy.AnswerTrade(expected, offer.Gold, (int)item.Value.Gold, (int)gold2, known: false);

        var report = new List<string>();
        Assert.True(game.ExecuteGift(Ai1, Ai2, offer, report));

        Assert.Equal(expected.Steps + 1, game.Rng.Steps);   // + сообщение TRADE_ACCEPTED/TRADE_REFUSED
        Assert.Equal(accept, Capital(game, Ai2).Items.Contains(Instance));
        Assert.Equal(!accept, Capital(game, Ai1).Items.Contains(Instance));
        Assert.Equal(accept ? gold1 + offer.Gold : gold1, game.Map.Players[Ai1].Bank.Gold);
        Assert.Equal(accept ? gold2 - offer.Gold : gold2, game.Map.Players[Ai2].Bank.Gold);
        Assert.Empty(game.State.Offers);
    }

    /// <summary>Сделка с человеком ждёт его ответа; согласие переносит предмет и золото, повторный ответ — «нет предложения»;
    /// ответ на союз сделку не трогает; согласие без золота — отказ, предложение остаётся.</summary>
    [SkippableFact]
    public void ItemTrade_ToHuman_WaitsAndIsAnswered()
    {
        var (game, item) = Game();
        var offer = new AiTradeOffer(AiGiftKind.Item, Instance, Capital(game, Ai1).Id, (int)item.Value.Gold + 90);
        long steps = game.Rng.Steps, gold = game.Map.Players[Human].Bank.Gold;
        Assert.True(game.ExecuteGift(Ai1, Human, offer, null));
        Assert.Equal(steps, game.Rng.Steps);
        Assert.Equal(new AllianceOffer(Ai1, Human, game.Map.Turn, offer), Assert.Single(game.State.Offers));
        Assert.NotNull(game.AnswerAllianceOffer(Ai1, Human, accept: true));
        Assert.Single(game.State.Offers);
        var poor = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map,
            players: game.Map.Players.ToDictionary(p => p.Key, p => p.Key == Human ? GameSession.WithBank(p.Value, p.Value.Bank with { Gold = 0 }) : p.Value)),
            game.Looks.Stacks) }, game.Art, game.Book);
        Assert.NotNull(poor.AnswerTradeOffer(Ai1, Human, accept: true));
        Assert.Single(poor.State.Offers);

        Assert.Null(game.AnswerTradeOffer(Ai1, Human, accept: true));
        Assert.Empty(game.State.Offers);
        Assert.Contains(Instance, Capital(game, Human).Items);
        Assert.DoesNotContain(Instance, Capital(game, Ai1).Items);
        Assert.Equal(gold - offer.Gold, game.Map.Players[Human].Bank.Gold);
        Assert.NotNull(game.AnswerTradeOffer(Ai1, Human, accept: true));
    }

    /// <summary>Подарок золотом не союзнику: дань (казна и отношения), реакция получателя <c>PLAYER_GIVE_GOLD</c>, затем сообщение
    /// <c>AI_GIVE_GOLD</c>.</summary>
    [SkippableFact]
    public void GoldGift_PaysTributeAndChats()
    {
        var (game, _) = Game();
        long gold1 = game.Map.Players[Ai1].Bank.Gold, gold2 = game.Map.Players[Ai2].Bank.Gold;
        var offer = AiGift.Gold(allies: false, 90, (int)gold1);
        Assert.NotNull(offer);
        long steps = game.Rng.Steps;
        Assert.True(game.ExecuteGift(Ai1, Ai2, offer, null));
        Assert.Equal(gold1 - 180, game.Map.Players[Ai1].Bank.Gold);
        Assert.Equal(gold2 + 180, game.Map.Players[Ai2].Bank.Gold);
        int msgs = game.Book!.AiMessages(game.Map.Players[Ai1].RaceId, AiMessage.AiGiveGold).Count;
        int reply = game.Book!.AiMessages(game.Map.Players[Ai2].RaceId, AiMessage.PlayerGiveGold).Count;
        Assert.Equal(steps + (msgs > 0 ? 1 : 0) + (reply > 0 ? 1 : 0), game.Rng.Steps);
    }

    /// <summary>Сделка заклинанием устарела, пока ждала ответа (человек уже узнал заклинание): согласие — отказ, дубля в
    /// известных нет, предложение остаётся.</summary>
    [SkippableFact]
    public void SpellTrade_Stale_RefusedWithoutDuplicate()
    {
        var (game, _) = Game();
        string spell = game.Book!.Spells.Keys.First(id => !game.Map.Players[Human].KnownSpells.Contains(id, StringComparer.OrdinalIgnoreCase));
        Assert.True(game.ExecuteGift(Ai1, Human, new AiTradeOffer(AiGiftKind.Spell, spell, "", 100), null));
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = players[Human] with { KnownSpells = [.. players[Human].KnownSpells, spell.ToUpperInvariant()] };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);

        Assert.Equal("заклинание уже известно", game.AnswerTradeOffer(Ai1, Human, accept: true));
        Assert.Single(game.State.Offers);
        Assert.Equal(1, game.Map.Players[Human].KnownSpells.Count(k => k.Equals(spell, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Ждущая сделка переживает сохранение.</summary>
    [SkippableFact]
    public void TradeOffer_RoundTripsThroughSave()
    {
        var (game, _) = Game();
        var state = game.State with { Offers = [new AllianceOffer(Ai1, Human, 7, new AiTradeOffer(AiGiftKind.Spell, "G000SS0097", "", 300))] };
        Assert.Equal(state.Offers, GameSave.FromJson(GameSave.ToJson(state)).Offers);
    }
}
