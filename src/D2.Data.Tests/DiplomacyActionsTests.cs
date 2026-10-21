using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Дипломатия в партии (<c>GameSession.Diplomacy.cs</c>) на живом сохранении: предложение союза ИИ другому ИИ и
/// человеку, ответ человека, разрыв союза, отношения за нападение, ждущие предложения в сохранении.</summary>
[Trait("Category", "Corpus")]
public class DiplomacyActionsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    // Партия human: человек — S073PL000a, ИИ — S073PL0009, S073PL000b, S073PL000c.
    private const string Human = "S073PL000a", Ai1 = "S073PL0009", Ai2 = "S073PL000b";

    private static GameSession Game(Func<MapState, int, int, uint>? relation = null)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        if (relation is not null)
        {
            var map = looks.Map;
            var relations = map.Relations.Select(r => r with { Raw = relation(map, r.Race1, r.Race2) }).ToList();
            looks = GameSession.Retold(looks, GameSession.With(map, relations: relations), looks.Stacks);
        }
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt, book);
    }

    private static int Race(GameSession s, string player) => s.Book!.Economy.Races[s.Map.Players[player].RaceId].RaceType;

    private static Relation Pair(GameSession s, string a, string b) =>
        s.Map.Relations.First(r => r.Pairs(Race(s, a), Race(s, b)));

    /// <summary>Отношение <paramref name="value"/> без союза у всех пар.</summary>
    private static uint Current(int value) => (uint)value << 1;

    /// <summary>ИИ предлагает союз ИИ: сообщение предлагающего, ответ цели броском (<see cref="AiDiplomacy.AnswerAlliance"/>)
    /// и сообщение-ответ — три броска общего R250 по порядку; исход — союз (<c>D_MK_ALLY</c>, ход союза — сегодня) или
    /// отказ (<c>D_REF_ALLY</c>).</summary>
    [SkippableFact]
    public void Offer_AiToAi_TargetAnswersAtOnce()
    {
        var game = Game((_, _, _) => Current(80));
        var book = game.Book!;
        var expected = game.Rng.Clone();
        int chat = book.AiMessages(game.Map.Players[Ai1].RaceId, AiMessage.AiOffersAlliance).Count;
        Assert.True(chat > 0);
        expected.Next(chat);
        bool accept = AiDiplomacy.AnswerAlliance(expected, 80, AiDiplomacy.PlainLandShare(game.Map, Race(game, Ai1)));

        var report = new List<string>();
        Assert.True(AiEconomy.Execute(game, Ai1, new AiStepAction(AiStepType.Diplomacy, Ai2, nameof(AiDiploAction.Offer)), report));

        var pair = Pair(game, Ai1, Ai2);
        Assert.Equal(accept, pair.Ally);
        Assert.Equal(accept ? book.Diplomacy.MakeAlly : book.Diplomacy.RefuseAlly, pair.Current);
        if (accept) Assert.Equal(game.Map.Turn, pair.AllianceTurn);
        Assert.Equal(expected.Steps + 1, game.Rng.Steps);   // + сообщение-ответ предлагающего
        Assert.Contains(report, r => r.Contains("предлагает союз", StringComparison.Ordinal));
        Assert.Empty(game.State.Offers);
    }

    /// <summary>Предложение человеку ждёт ответа в состоянии партии; согласие — союз, повторный ответ — отказ «нет
    /// предложения»; отказ ставит <c>D_REF_ALLY</c>.</summary>
    [SkippableFact]
    public void Offer_ToHuman_WaitsAndIsAnswered()
    {
        foreach (bool accept in new[] { true, false })
        {
            var game = Game((_, _, _) => Current(80));
            Assert.True(AiEconomy.Execute(game, Ai1, new AiStepAction(AiStepType.Diplomacy, Human, nameof(AiDiploAction.Offer))));
            var offer = Assert.Single(game.State.Offers);
            Assert.Equal(new AllianceOffer(Ai1, Human, game.Map.Turn), offer);
            Assert.False(Pair(game, Ai1, Human).Ally);

            Assert.Null(game.AnswerAllianceOffer(Ai1, Human, accept));
            Assert.Empty(game.State.Offers);
            Assert.Equal(accept, Pair(game, Ai1, Human).Ally);
            Assert.Equal(accept ? 100 : 50, Pair(game, Ai1, Human).Current);
            Assert.NotNull(game.AnswerAllianceOffer(Ai1, Human, accept));
        }
    }

    /// <summary>У движка предложение ИИ приходит человеку сразу, в ход ИИ (<c>CEffectProposeAlliance</c> <c>0x43b5c8</c>): наблюдатель
    /// хода (сцена) получает его уже в <see cref="GameState.Offers"/> и отвечает тем же <see cref="GameSession.AnswerAllianceOffer"/> —
    /// союз заключён до конца хода ИИ. Без ответа — ждёт хода человека, как прежде.</summary>
    [SkippableFact]
    public void Offer_ToHuman_ObserverAnswersDuringAiTurn()
    {
        var game = Game((_, _, _) => Current(80));
        var asked = new List<AllianceOffer>();
        game.Observer = new OfferAnswerer(offer =>
        {
            asked.Add(offer);
            Assert.Contains(offer, game.State.Offers);
            Assert.Null(game.AnswerAllianceOffer(offer.From, offer.To, accept: true));
        });

        Assert.True(AiEconomy.Execute(game, Ai1, new AiStepAction(AiStepType.Diplomacy, Human, nameof(AiDiploAction.Offer))));

        Assert.Equal([new AllianceOffer(Ai1, Human, game.Map.Turn)], asked);
        Assert.Empty(game.State.Offers);
        Assert.True(Pair(game, Ai1, Human).Ally);
    }

    private sealed class OfferAnswerer(Action<AllianceOffer> answer) : ITurnObserver
    {
        public void PlayerTurn(string playerId)
        {
        }

        public void StackMoving(StackMove move)
        {
        }

        public IReadOnlyList<D2.Rules.Battle.TurnRecord>? Battle(D2.Rules.Battle.BattleState state) => null;

        public void Offered(AllianceOffer offer) => answer(offer);
    }

    /// <summary>Предложение от игрока, чья столица взята, снимается при ответе и отношений не меняет.</summary>
    [SkippableFact]
    public void Offer_FromCapturedPlayer_IsDropped()
    {
        var game = Game((_, _, _) => Current(80));
        Assert.True(AiEconomy.Execute(game, Ai1, new AiStepAction(AiStepType.Diplomacy, Human, nameof(AiDiploAction.Offer))));
        var map = game.Map;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Ai1] = players[Ai1] with { CapturedBy = Ai2 };
        var looks = GameSession.Retold(game.Looks, GameSession.With(map, players: players), game.Looks.Stacks);
        game = new GameSession(game.State with { Looks = looks }, game.Art, game.Book);

        Assert.NotNull(game.AnswerAllianceOffer(Ai1, Human, accept: true));
        Assert.Empty(game.State.Offers);
        Assert.False(Pair(game, Ai1, Human).Ally);
        Assert.Equal(80, Pair(game, Ai1, Human).Current);
    }

    /// <summary>Разрыв союза: бит союза гаснет, отношение — <c>D_BK_ALLY</c>, ход союза — день разрыва, счётчик разрывов +1.</summary>
    [SkippableFact]
    public void Break_EndsAllianceAndCountsIt()
    {
        var game = Game((_, _, _) => Current(100) | 1u);
        int breaks = game.Map.Players[Ai1].QtyBreaks;
        Assert.True(AiEconomy.Execute(game, Ai1, new AiStepAction(AiStepType.Diplomacy, Ai2, nameof(AiDiploAction.Break))));
        var pair = Pair(game, Ai1, Ai2);
        Assert.False(pair.Ally);
        Assert.Equal(game.Book!.Diplomacy.BreakAlly, pair.Current);
        Assert.Equal(game.Map.Turn, pair.AllianceTurn);
        Assert.Equal(breaks + 1, game.Map.Players[Ai1].QtyBreaks);
        // Без союза разрывать нечего — действие не исполнено.
        Assert.False(AiEconomy.Execute(game, Ai1, new AiStepAction(AiStepType.Diplomacy, Ai2, nameof(AiDiploAction.Break))));
    }

    /// <summary>Нападение в начале боя двигает отношения (<see cref="Diplomacy.Attack"/>): вне мира отряд — <c>D_ATTAK_SC</c>,
    /// при мире — присваивание <c>D_ATTAK_PC</c>.</summary>
    [SkippableFact]
    public void Attack_ChangesRelationsAtBattleStart()
    {
        foreach (int before in new[] { 50, 90 })
        {
            var game = Game((_, _, _) => Current(before));
            var attacker = game.Map.Stacks.Values.First(s => s.Owner == Human && s.UnitIds.Count > 0);
            var target = game.Map.Stacks.Values.First(s => s.Owner == Ai1 && s.Inside.Length == 0 && s.UnitIds.Count > 0);
            game.StartBattle(attacker.Id, target.Id);
            var rules = game.Book!.Diplomacy;
            Assert.Equal(before > rules.Neutral ? rules.AttackAtPeace : before + rules.AttackStack, Pair(game, Human, Ai1).Current);
        }
    }

    /// <summary>Ждущие предложения союза переживают запись и загрузку.</summary>
    [SkippableFact]
    public void Offers_SurviveSave()
    {
        var game = Game();
        var state = game.State with { Offers = [new AllianceOffer(Ai1, Human, 7)] };
        var back = GameSave.FromJson(GameSave.ToJson(state));
        Assert.Equal(state.Offers, back.Offers);
    }
}
