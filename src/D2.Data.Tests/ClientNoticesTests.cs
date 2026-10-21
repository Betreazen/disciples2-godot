using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Battle;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Команды сервера клиенту без своего окна в ядре (<see cref="NoticeWatch"/>, аудит 13.10 «Осталось» п. 4): раскрытый шпион
/// (<c>CCmdDiscoverSpyMsg</c>), гибель иллюзии (<c>CCmdStackIllusionMsg</c>), взятые ресурсы столицы (<c>CCmdCaptureResourcesMsg</c>),
/// выделение отряда (<c>CCmdStackActionMsg</c> 1/2).</summary>
[Trait("Category", "Corpus")]
public class ClientNoticesTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private sealed class Sights : ITurnObserver
    {
        public List<TurnSight> All { get; } = [];

        public void PlayerTurn(string playerId)
        {
        }

        public void StackMoving(StackMove move)
        {
        }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;

        public void Happened(TurnSight sight) => All.Add(sight);
    }

    private static MapLooks Looks()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        return MapLooksLoader.Load(path);
    }

    private static string Text(string id) => id switch
    {
        NoticeWatch.SpyCaughtText => "к Вам заслал %PLAYER%, повелитель %RACE%",
        NoticeWatch.SpyLostText => "%PLAYER% Повелитель %RACE%, шлет Вам голову",
        NoticeWatch.IllusionText => "%KILLER% побеждает, %ILLUSION% гибнет.",
        _ => $"[{id}]",
    };

    /// <summary>Шпион человека у чужого игрока раскрыт в начале хода человека (<c>0x44430f</c>, бросок R250(100) &lt; <c>SPY_DISCOV</c>):
    /// <c>CEffectSpy</c> с флагом 0 шлёт всем <c>CCmdDiscoverSpyMsg</c> (<c>0x43e8c0</c>), клиент <c>0x489f16</c> показывает разведанному
    /// <c>X005TA0536</c> с хозяином шпиона, хозяину — <c>X005TA0605</c> с разведанным. Прежде запись снималась молча.</summary>
    [SkippableFact]
    public void SpyDiscovered_BothSidesGetTheirMessage()
    {
        var looks = Looks();
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var target = looks.Map.Players.Values.First(p => !p.IsHuman && p.CapturedBy.Length == 0 && book.Economy.Races.TryGetValue(p.RaceId, out var r)
                                                          && r.Playable);
        var players = looks.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[human.Id] = human with { Spied = [target.Id] };
        looks = looks with { Map = GameSession.With(looks.Map, players: players) };
        var info = GameInfo.FromMap("PtC", looks) with { Humans = [human.Id] };
        List<TurnSight>? found = null;
        for (int index = 0; index < EngineRng.Size && found is null; index++)
        {
            var rng = new EngineRng(index);
            var session = new GameSession(new GameState(info, looks) { Random = new RandomStreams(rng.Index, rng.TableSnapshot(), 0) },
                                          content.MapArt, book);
            var sights = new Sights();
            session.Watch(sights);
            session.NextDay();
            var spy = sights.All.Where(s => s.Kind is TurnSightKind.SpyCaught or TurnSightKind.SpyLost).ToList();
            bool gone = !session.Map.Players[human.Id].Spied.Contains(target.Id, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(gone, spy.Count > 0);   // сообщение — ровно тогда, когда запись снята
            if (gone) found = spy;
        }
        Assert.NotNull(found);
        Assert.Equal([(TurnSightKind.SpyCaught, target.Id, human.Id), (TurnSightKind.SpyLost, human.Id, target.Id)],
                     found!.Select(s => (s.Kind, s.PlayerId, s.Id)));

        var lost = NoticeWatch.Text(found[1], looks.Map, content.Catalog, Text, t => t);
        string race = content.Catalog.Races.First(r => r.Id.Equals(target.RaceId, StringComparison.OrdinalIgnoreCase)).NameText;
        Assert.Equal($"{target.Name.Trim()} Повелитель [{race}], шлет Вам голову", lost);

        // Сборщик: окно только человеку; разведанный — ИИ, его сообщение не копится.
        var watch = new NoticeWatch(id => id.Equals(human.Id, StringComparison.OrdinalIgnoreCase));
        foreach (var s in found) watch.Happened(s);
        Assert.True(watch.HasMessages(human.Id));
        Assert.Equal([TurnSightKind.SpyLost], watch.Take(human.Id, messagesOnly: true).Select(s => s.Kind));
        Assert.False(watch.HasMessages(human.Id));
    }

    /// <summary>Отряд с лидером-иллюзией побеждён отрядом не нейтрала (<c>CEffectKillStack</c> <c>0x43895c</c>: лидер — <c>IUsIllusion</c>
    /// <c>0x438ff3</c>) — <c>CCmdStackIllusionMsg</c>(убийца, иллюзия) и у хозяина иллюзии <c>X005TA0408</c> «%KILLER% побеждает, %ILLUSION%
    /// гибнет.» (<c>0x4ce7bd</c>); подписи — собственное имя лидера или имя типа (<c>0x5e97dc</c>). Обычный отряд — без сообщения.</summary>
    [SkippableFact]
    public void IllusionKilled_TellsItsOwner()
    {
        var looks = Looks();
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var mine = looks.Map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0)
                                         .OrderByDescending(s => s.UnitIds.Count).First();
        var enemy = looks.Map.Stacks.Values.Where(s => s.Owner != human.Id && s.Inside.Length == 0 && s.LeaderAlive && s.UnitIds.Count > 0)
                                          .OrderBy(s => s.UnitIds.Count).ThenBy(s => s.Id, StringComparer.Ordinal).First();
        string illusionType = book.Rules.Units.Keys.Order(StringComparer.Ordinal).First(t => book.CategoryOf(t) == UnitCategory.Illusion);

        var plain = Battle(looks, content, book, mine.Id, enemy.Id);
        Assert.DoesNotContain(plain, s => s.Kind == TurnSightKind.IllusionKilled);

        var units = looks.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[enemy.LeaderId] = units[enemy.LeaderId] with { TypeId = illusionType };
        var withIllusion = looks with { Map = GameSession.With(looks.Map, units: units) };
        var sights = Battle(withIllusion, content, book, mine.Id, enemy.Id);
        Skip.IfNot(sights.Any(s => s.Kind == TurnSightKind.Death && s.Id == enemy.Id), "иллюзия пережила бой");
        var sight = Assert.Single(sights, s => s.Kind == TurnSightKind.IllusionKilled);
        Assert.Equal((enemy.Owner, enemy.Id), (sight.PlayerId, sight.Id));
        string killerType = looks.Map.Units[mine.LeaderId].TypeId;
        Assert.Equal(killerType, sight.Ids[1]);
        Assert.Equal(["", illusionType], sight.Ids.Skip(2));
        var text = NoticeWatch.Text(sight, withIllusion.Map, content.Catalog, Text, t => $"<{t}>");
        string killer = sight.Ids[0].Length > 0 ? sight.Ids[0] : $"<{killerType}>";
        Assert.Equal($"{killer} побеждает, <{illusionType}> гибнет.", text);
    }

    private static List<TurnSight> Battle(MapLooks looks, GameContent content, GameBook book, string attacker, string defender)
    {
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), content.MapArt, book);
        var sights = new Sights();
        session.Watch(sights);
        var state = session.StartBattle(attacker, defender, duel: true);
        var battle = new D2.Data.Battle.BattleSession(state, new BattleSim(state, session.Rng, book.Rules.Constants), book.Rules.Constants)
            { Auto = true };
        battle.Resolve();
        session.FinishBattle(battle.Log);
        return sights.All;
    }

    /// <summary>Окно взятых ресурсов (<c>CCaptureResourcesInterf</c> <c>0x4929ae</c>) на разметке <c>DLG_BEGIN_TURN</c>: заголовок
    /// <c>X005TA0776</c>, «Доход:» <c>X005TA0051</c> (<c>0x492a6e</c>), числа по валютам (<c>0x492ae8</c>: 0 → GOLD, 3 → ORANGE, 1 → ROUGE,
    /// 2 → JAUNE, 4 → BLANC, 5 → BLUE).</summary>
    [Fact]
    public void CaptureResourcesWindow_ShowsLoserBankByCurrency()
    {
        var screens = SharedContent.Load(Data).Screens;
        var screen = screens.Single(s => s.Id == CaptureResourcesLogic.Screen);
        var ui = new UiScreenState(screen);
        new CaptureResourcesLogic(new Bank(1234, 11, 22, 33, 44, 55), Text).Open(ui);

        Assert.Equal("[X005TA0776]", ui.TextOf("TXT_TURN_NUMBER"));
        Assert.Equal("[X005TA0051]", ui.TextOf("TXT_INCOME"));
        Assert.Equal(["1234", "33", "11", "22", "44", "55"],
                     new[] { "GOLD", "ORANGE", "ROUGE", "JAUNE", "BLANC", "BLUE" }.Select(c => ui.TextOf($"TXT_INCOME_{c}")));
    }

    /// <summary>Окно начала хода (<c>CBeginTurnInterf</c> <c>0x4b9b9d</c>): «Начинается N день» (<c>X005TA0047</c>, <c>%NUMBER%</c>), «Доход:»
    /// и доход хода по валютам — та же разметка и раскладка чисел, что у взятых ресурсов (<c>0x4b9d96</c>).</summary>
    [Fact]
    public void BeginTurnWindow_ShowsDayAndIncome()
    {
        var screens = SharedContent.Load(Data).Screens;
        var ui = new UiScreenState(screens.Single(s => s.Id == BeginTurnLogic.Screen));
        new BeginTurnLogic(7, new Bank(150, 0, 5, 0, 0, 0), id => id == BeginTurnLogic.TitleText ? "Начинается %NUMBER% день" : $"[{id}]").Open(ui);

        Assert.Equal("Начинается 7 день", ui.TextOf("TXT_TURN_NUMBER"));
        Assert.Equal("[X005TA0051]", ui.TextOf("TXT_INCOME"));
        Assert.Equal(["150", "0", "0", "5", "0", "0"],
                     new[] { "GOLD", "ORANGE", "ROUGE", "JAUNE", "BLANC", "BLUE" }.Select(c => ui.TextOf($"TXT_INCOME_{c}")));
    }

    /// <summary>Руина разграблена (<c>CCmdLootRuinMsg</c>, задача <c>0x4cea43</c>): «Вы победили!», при золоте — перевод строки и «Вы
    /// нашли N золота» (журнал 20261016g seq 564453); без золота — одна строка. Сборщик держит уведомление для грабителя-человека.</summary>
    [Fact]
    public void RuinLooted_MessageWithFoundGold()
    {
        static string T(string id) => id switch
        {
            NoticeWatch.RuinWonText => "Вы победили!",
            NoticeWatch.RuinGoldText => @"\hC;Вы нашли %CURRENCY%",
            "X005TA0055" => "%QTY% золота",
            _ => $"[{id}]",
        };
        Assert.Equal("Вы победили!\n" + @"\hC;Вы нашли 300 золота", NoticeWatch.RuinLoot(new Bank(300, 0, 0, 0, 0, 0), T));
        Assert.Equal("Вы победили!", NoticeWatch.RuinLoot(Bank.Zero, T));

        var watch = new NoticeWatch(id => id == "H");
        watch.Happened(new TurnSight(TurnSightKind.Looted, "AI", 1, 1, "R1") { Bank = new Bank(100, 0, 0, 0, 0, 0) });
        watch.Happened(new TurnSight(TurnSightKind.Looted, "H", 1, 1, "R2") { Bank = new Bank(300, 0, 0, 0, 0, 0) });
        Assert.Empty(watch.Take("H", messagesOnly: true));
        var looted = Assert.Single(watch.Take("H"));
        Assert.Equal(("R2", 300L), (looted.Id, (long)looted.Bank.Gold));
    }

    /// <summary>Сборщик выделений (<c>CCmdStackActionMsg</c>): создание и смена хозяина копятся для людей; смена хозяина — для любого
    /// зрителя (прежний хозяин снимает выделение, <c>0x48d8f3</c>); в ход ИИ (<c>messagesOnly</c>) выделения не забираются.</summary>
    [Fact]
    public void Watch_KeepsSelectionsForTheirTurn()
    {
        var watch = new NoticeWatch(id => id == "H");
        watch.Happened(new TurnSight(TurnSightKind.Created, "H", 1, 1, "S1"));
        watch.Happened(new TurnSight(TurnSightKind.Created, "AI", 1, 1, "S2"));
        watch.Happened(new TurnSight(TurnSightKind.OwnerChanged, "AI", 1, 1, "S3"));
        watch.Happened(new TurnSight(TurnSightKind.Capture, "H", 1, 1, "F1"));

        Assert.Empty(watch.Take("H", messagesOnly: true));
        Assert.Equal(["S1", "S3"], watch.Take("H").Select(s => s.Id));
        Assert.Empty(watch.Take("H"));

        // Ход ИИ: выделения не копятся и прежние снимаются; окна — да.
        watch.Happened(new TurnSight(TurnSightKind.Created, "H", 1, 1, "S4"));
        watch.PlayerTurn("AI");
        watch.Happened(new TurnSight(TurnSightKind.OwnerChanged, "H", 1, 1, "S5"));
        watch.Happened(new TurnSight(TurnSightKind.SpyLost, "H", -1, -1, "AI"));
        watch.HumanTurn("H");
        watch.Happened(new TurnSight(TurnSightKind.Created, "H", 1, 1, "S6"));
        Assert.Equal(["AI", "S6"], watch.Take("H").Select(s => s.Id));
    }
}
