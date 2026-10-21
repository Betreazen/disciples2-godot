using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Magic;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Предложения человека из окна дипломатии и реакции ИИ на них (<c>GameSession.Trade.cs</c>, <c>docs/ai-diplomacy.md</c> §5):
/// дань (<c>0x474dd2</c>), сделка предметом и заклинанием (<c>0x474841</c>), обмен картами (<c>0x474d40</c> → <c>0x5d927b</c>).
/// Сохранение партии human, как в <see cref="AiGiftSessionTests"/>; <see cref="Ai2"/> в части тестов — второй человек.</summary>
[Trait("Category", "Corpus")]
public class HumanDiplomacyTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private const string Human = "S073PL000a", Ai1 = "S073PL0009", Ai2 = "S073PL000b";
    private const string Instance = "S073IMffff";

    /// <summary>Партия с отношением 90 без союзов; в складе столицы <see cref="Human"/> — экземпляр <see cref="Instance"/> дешёвого
    /// предмета; у <see cref="Ai1"/> 5000 золота. <paramref name="secondHuman"/> — <see cref="Ai2"/> тоже человек.</summary>
    private static (GameSession Game, Item Item) Game(bool secondHuman = false)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var item = book.Items.Values.First(i => i.Category != ItemCategory.Talisman && i.Value.Gold is > 0 and < 1000);
        var map = looks.Map;
        var sites = map.Sites.Select(s => s.Kind == SiteKind.Capital && s.Owner == Human ? s with { Items = [.. s.Items, Instance] } : s).ToList();
        var types = map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[Instance] = item.Id;
        var relations = map.Relations.Select(r => r with { Raw = 90u << 1 }).ToList();
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Ai1] = GameSession.WithBank(players[Ai1], players[Ai1].Bank with { Gold = 5000 });
        players[Human] = GameSession.WithBank(players[Human], players[Human].Bank with { Gold = 5000 });
        looks = GameSession.Retold(looks, GameSession.With(map, players: players, sites: sites, itemTypes: types, relations: relations),
                                   looks.Stacks);
        var info = GameInfo.FromMap("PtC", looks);
        if (secondHuman) info = info with { Humans = [Human, Ai2] };
        return (new GameSession(new GameState(info, looks), SharedContent.Load(Data).MapArt, book), item);
    }

    private static MapSite Capital(GameSession g, string player) => g.Map.Sites.First(s => s.Kind == SiteKind.Capital && s.Owner == player);

    /// <summary>Дань ИИ: получатель сразу пишет дарителю <c>PLAYER_GIVE_GOLD</c> (один бросок номера сообщения; корпус
    /// <c>react_474dd2</c> — 10 из 10 после дани человека); дань человеку — без сообщения.</summary>
    [SkippableFact]
    public void Tribute_ToAi_RecipientChats()
    {
        var (game, _) = Game(secondHuman: true);
        int msgs = game.Book!.AiMessages(game.Map.Players[Ai1].RaceId, AiMessage.PlayerGiveGold).Count;
        long steps = game.Rng.Steps;
        var report = new List<string>();

        Assert.Null(game.GiveGold(Human, Ai1, 100, report));
        Assert.Equal(steps + (msgs > 0 ? 1 : 0), game.Rng.Steps);
        if (msgs > 0) Assert.Contains(report, r => r.StartsWith($"{Ai1} → {Human}: ", StringComparison.Ordinal));

        steps = game.Rng.Steps;
        Assert.Null(game.GiveGold(Human, Ai2, 100, report));
        Assert.Equal(steps, game.Rng.Steps);
    }

    /// <summary>Сделка предметом от человека ИИ: ответ броском <see cref="AiDiplomacy.AnswerTrade"/> без реплики; согласие —
    /// предмет в склад столицы ИИ, золото человеку.</summary>
    [SkippableFact]
    public void ItemTrade_HumanToAi_AnsweredAtOnce()
    {
        var (game, item) = Game();
        int price = (int)item.Value.Gold;
        var expected = game.Rng.Clone();
        bool accept = AiDiplomacy.AnswerTrade(expected, price, price, 5000, known: false);
        long gold = game.Map.Players[Human].Bank.Gold;

        var result = game.ProposeTradeBy(Human, Ai1, AiGiftKind.Item, Instance, price, Capital(game, Human).Id);

        Assert.Equal(new ProposalResult(accept, null), result);
        Assert.Equal(expected.Steps, game.Rng.Steps);
        Assert.Equal(accept, Capital(game, Ai1).Items.Contains(Instance));
        Assert.Equal(accept ? gold + price : gold, game.Map.Players[Human].Bank.Gold);
        Assert.Empty(game.State.Offers);
    }

    /// <summary>Сделка заклинанием от человека человеку ждёт ответа и проходит через <see cref="GameSession.AnswerTradeOffer"/>;
    /// повторное предложение той же паре, пока первое ждёт, не уходит.</summary>
    [SkippableFact]
    public void SpellTrade_HumanToHuman_Waits()
    {
        var (game, _) = Game(secondHuman: true);
        var book = game.Book!;
        string spell = book.Spells.Values.First(s => s.Category != SpellCategory.Summon
                                                     && !game.Map.Players[Ai2].KnownSpells.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).Id;
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = players[Human] with { KnownSpells = [.. players[Human].KnownSpells, spell.ToUpperInvariant()] };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);
        long steps = game.Rng.Steps;
        Assert.Equal(new ProposalResult(null, null), game.ProposeTradeBy(Human, Ai2, AiGiftKind.Spell, spell, 0));
        Assert.Equal(steps, game.Rng.Steps);
        Assert.NotNull(game.ProposeTradeBy(Human, Ai2, AiGiftKind.Spell, spell, 0).Refusal);
        Assert.Single(game.State.Offers);

        Assert.Null(game.AnswerTradeOffer(Human, Ai2, accept: true));
        Assert.Contains(spell, game.Map.Players[Ai2].KnownSpells, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(game.State.Offers);
    }

    /// <summary>Что не уходит: золото (это дань), чужой или отсутствующий предмет, неизвестное заклинание, призыв.</summary>
    [SkippableFact]
    public void Trade_InvalidProposals_Refused()
    {
        var (game, _) = Game();
        var book = game.Book!;
        string fort = Capital(game, Human).Id;
        string unknown = book.Spells.Keys.First(id => !game.Map.Players[Human].KnownSpells.Contains(id, StringComparer.OrdinalIgnoreCase));
        Assert.NotNull(game.ProposeTradeBy(Human, Ai1, AiGiftKind.Gold, "", 100).Refusal);
        Assert.NotNull(game.ProposeTradeBy(Human, Ai1, AiGiftKind.Item, Instance, 100, Capital(game, Ai1).Id).Refusal);
        Assert.NotNull(game.ProposeTradeBy(Human, Ai1, AiGiftKind.Item, "S073IMfffe", 100, fort).Refusal);
        Assert.NotNull(game.ProposeTradeBy(Human, Ai1, AiGiftKind.Spell, unknown, 100).Refusal);
        if (game.Map.Players[Human].KnownSpells.FirstOrDefault(id => book.Spells.TryGetValue(id, out var s) && s.Category == SpellCategory.Summon) is { } summon)
            Assert.NotNull(game.ProposeTradeBy(Human, Ai1, AiGiftKind.Spell, summon, 100).Refusal);
        Assert.Empty(game.State.Offers);
    }

    /// <summary>Обмен картами с ИИ: ответ броском <c>rng(100) &lt; 30</c>; согласие открывает обоим клетки, открытые у одного.</summary>
    [SkippableFact]
    public void ExchangeMap_HumanToAi_AnsweredAtOnce()
    {
        var (game, _) = Game();
        var expected = game.Rng.Clone();
        bool accept = AiDiplomacy.AnswerExchangeMap(expected);
        var (a, b) = Fogs(game, Human, Ai1);

        Assert.Equal(new ProposalResult(accept, null), game.ProposeExchangeMapBy(Human, Ai1));
        Assert.Equal(expected.Steps, game.Rng.Steps);
        var (a2, b2) = Fogs(game, Human, Ai1);
        AssertFog(accept ? Merged(a, b) : a, a2);
        AssertFog(accept ? Merged(a, b) : b, b2);
        // Память обмена (EXMAPID/EXMAPTURN, CEffectExchangeMap 0x43803f — до проверки согласия): у предлагавшего, по цели.
        Assert.Equal(game.Map.Turn, game.Map.Players[Human].LastMapExchange(Ai1));
        Assert.Null(game.Map.Players[Ai1].LastMapExchange(Human));
        Assert.Null(game.Map.Players[Human].LastMapExchange(Ai2));
    }

    /// <summary>Отказ тоже запоминается: эффект пишет день до проверки согласия (<c>0x43803f</c>, согласие — <c>0x438044</c>).
    /// Ответ человека — в его ход; память — у предлагавшего.</summary>
    [SkippableFact]
    public void ExchangeMap_Refused_StillRememberedByProposer()
    {
        var (game, _) = Game(secondHuman: true);
        var (a, b) = Fogs(game, Human, Ai2);
        Assert.Equal(new ProposalResult(null, null), game.ProposeExchangeMapBy(Human, Ai2));
        Assert.Null(game.Map.Players[Human].LastMapExchange(Ai2));   // предложение ждёт — эффекта ещё нет

        Assert.Null(game.AnswerExchangeOffer(Human, Ai2, accept: false));

        Assert.Equal(game.Map.Turn, game.Map.Players[Human].LastMapExchange(Ai2));
        Assert.Null(game.Map.Players[Ai2].LastMapExchange(Human));
        var (a2, b2) = Fogs(game, Human, Ai2);
        AssertFog(a, a2);
        AssertFog(b, b2);
    }

    /// <summary>Поле читается из сохранения движка: legions, человек <c>S073PL000b</c> предлагал обмен <c>S073PL0009</c> в день 9 и 21
    /// (<c>EXMAPID1</c>/<c>EXMAPTURN1</c>), и запись переживает сохранение порта.</summary>
    [SkippableFact]
    public void ExchangeMap_MemoryLoadedFromSg_AndKeptBySave()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260920-legions", "rastlennaya-vlast-heretic-t21-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        Assert.Equal(21, looks.Map.Players["S073PL000b"].LastMapExchange("S073PL0009"));
        Assert.All(looks.Map.Players.Values.Where(p => p.Id != "S073PL000b"), p => Assert.Null(p.MapExchanges));

        var back = GameSave.FromJson(GameSave.ToJson(new GameState(GameInfo.FromMap("legions", looks), looks)));

        Assert.Equal(21, back.Looks.Map.Players["S073PL000b"].LastMapExchange("s073pl0009"));
    }

    /// <summary>Обмен картами между людьми ждёт ответа; предложение обмена — не союз; согласие сливает туман.</summary>
    [SkippableFact]
    public void ExchangeMap_HumanToHuman_WaitsAndMerges()
    {
        var (game, _) = Game(secondHuman: true);
        var (a, b) = Fogs(game, Human, Ai2);
        Assert.Equal(new ProposalResult(null, null), game.ProposeExchangeMapBy(Human, Ai2));
        Assert.True(Assert.Single(game.State.Offers).Exchange);
        Assert.NotNull(game.AnswerAllianceOffer(Human, Ai2, accept: true));
        Assert.NotNull(game.AnswerTradeOffer(Human, Ai2, accept: true));

        Assert.Null(game.AnswerExchangeOffer(Human, Ai2, accept: true));
        Assert.Empty(game.State.Offers);
        var (a2, b2) = Fogs(game, Human, Ai2);
        AssertFog(Merged(a, b), a2);
        AssertFog(Merged(a, b), b2);
    }

    private static (bool[,] A, bool[,] B) Fogs(GameSession g, string p1, string p2) =>
        ((bool[,])g.Map.Fog[g.Map.Players[p1].FogId].Clone(), (bool[,])g.Map.Fog[g.Map.Players[p2].FogId].Clone());

    private static bool[,] Merged(bool[,] a, bool[,] b)
    {
        var m = new bool[a.GetLength(0), a.GetLength(1)];
        for (int x = 0; x < m.GetLength(0); x++)
            for (int y = 0; y < m.GetLength(1); y++)
                m[x, y] = a[x, y] && b[x, y];
        return m;
    }

    private static void AssertFog(bool[,] want, bool[,] got) =>
        Assert.Equal(want.Cast<bool>(), got.Cast<bool>());
}
