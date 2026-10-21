using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Battle;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Итоги хода (<c>DLG_TURNSUMMARY</c>, <c>docs/ui-windows.md</c> §11–§12): часть карты, записи сборщика хода и подписка на ход
/// партии (<see cref="GameSession.Watch"/>).</summary>
[Trait("Category", "Corpus")]
public class TurnSummaryTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Human = "S073PL000a", Other = "S073PL000b";

    [Fact]
    public void MapPart_CenterAndQuarters()
    {
        Assert.Equal("X160TA0037", TurnSummaryLogic.MapPart(96, 48, 48));
        Assert.Equal("X160TA0033", TurnSummaryLogic.MapPart(96, 5, 5));
        Assert.Equal("X160TA0034", TurnSummaryLogic.MapPart(96, 90, 90));
        Assert.Equal("X160TA0035", TurnSummaryLogic.MapPart(96, 5, 90));
        Assert.Equal("X160TA0036", TurnSummaryLogic.MapPart(96, 90, 5));
    }

    /// <summary>Сборщик по событиям хода: нападение на отряд (1), заклинания другого игрока — на всю карту, по цели и по области (0),
    /// гибель (2), захват столицы (3), созданный отряд (5), вход в армию и уход из неё (6), снесённый жезл — разницей снимков (4).
    /// Свои действия игрока и чужие заклинания мимо его отрядов записей не дают.</summary>
    [SkippableFact]
    public void Watch_RecordsEveryKindInOrder()
    {
        var (session, context) = Game();
        var map = session.Map;
        var book = session.Book!;
        var mine = map.Stacks.Values.Where(s => s.Owner == Human && s.Inside.Length == 0).OrderBy(s => s.Id).ToList();
        var enemy = map.Stacks.Values.First(s => s.Owner != Human && s.Inside.Length == 0 && map.Players.ContainsKey(s.Owner));
        Skip.If(mine.Count < 2, "мало отрядов человека");
        var capital = map.Sites.First(s => s.Owner == Human && s.Kind == SiteKind.Capital);
        var watch = new TurnSummaryWatch(context, Human, session.Looks, null, RaceNames.Load(Path.Combine(Data, "rules")));

        watch.Attacked(enemy.Id, mine[0].Id);
        watch.Attacked(mine[1].Id, enemy.Id);   // нападает сам — не запись
        var whole = book.Spells.Values.First(s => s.Area == 999);
        var target = book.Spells.Values.First(s => s.Area == 1);
        var area = book.Spells.Values.First(s => s.Area is > 1 and < 999);
        watch.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, 0, 0, whole.Id));
        watch.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, mine[0].X, mine[0].Y, target.Id));
        watch.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, mine[1].X, mine[1].Y, area.Id));
        watch.Happened(new TurnSight(TurnSightKind.Spell, Human, mine[1].X, mine[1].Y, area.Id));   // своё — не запись
        watch.Happened(new TurnSight(TurnSightKind.Death, Human, mine[1].X, mine[1].Y, mine[1].Id));
        watch.Happened(new TurnSight(TurnSightKind.Capture, Other, capital.X, capital.Y, capital.Id));
        watch.Happened(new TurnSight(TurnSightKind.Created, Human, mine[1].X, mine[1].Y, mine[1].Id));
        watch.Happened(new TurnSight(TurnSightKind.OwnerChanged, Human, enemy.X, enemy.Y, enemy.Id));
        watch.Happened(new TurnSight(TurnSightKind.OwnerChanged, Other, mine[0].X, mine[0].Y, mine[0].Id));

        // Жезлы — разницей снимков: все на месте — записи нет.
        var entries = watch.Finish(session.Looks);
        foreach (var e in entries) output.WriteLine($"{e.Kind} {e.Text}");
        Assert.Equal([1, 0, 0, 0, 2, 3, 5, 6, 6], entries.Select(e => e.Kind));
        Assert.Contains("был атакован", entries[0].Text);
        Assert.Contains("было наложено", entries[1].Text);
        Assert.Contains(" для ", entries[2].Text);
        Assert.DoesNotContain(" для ", entries[3].Text);
        Assert.Contains("был уничтожен", entries[4].Text);
        Assert.Contains("был захвачен", entries[5].Text);
        Assert.Contains("присоединился", entries[7].Text);
        Assert.Contains("покинул", entries[8].Text);

        var logic = new TurnSummaryLogic(context, entries);
        var ui = new UiScreenState(((IWindowLogic)logic).Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == TurnSummaryLogic.Screen)));
        logic.Open(ui);
        Assert.Equal(entries.Count, ui.Cards("LBOX_EVENTS").Count);
        Assert.Equal(context.Text("X005TA0917"), ui.TextOf("TXT_TITLE"));
        logic.Handle(new UiClicked("BTN_OK"), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
    }

    /// <summary>Заклинания другого игрока (<c>CEffectSpell::apply</c> <c>0x43e4a7</c>, ранг 1): по области — запись без условий, и вдали от
    /// отрядов; по цели — по отряду в клетке после действия: чужой — записи нет, никого — запись с пустой целью, свой погиб от
    /// заклинания — сначала гибель, потом запись с пустой целью.</summary>
    [SkippableFact]
    public void Watch_SpellRecordsFollowEngineRecipients()
    {
        var (session, context) = Game();
        var map = session.Map;
        var book = session.Book!;
        var mine = map.Stacks.Values.First(s => s.Owner == Human && s.Inside.Length == 0);
        var enemy = map.Stacks.Values.First(s => s.Owner != Human && s.Inside.Length == 0 && map.Players.ContainsKey(s.Owner));
        var target = book.Spells.Values.First(s => s.Area == 1);
        var area = book.Spells.Values.First(s => s.Area is > 1 and < 999);
        var empty = Enumerable.Range(0, map.Size).SelectMany(x => Enumerable.Range(0, map.Size).Select(y => (x, y)))
            .First(c => !map.Plan.ContainsKey(c) && Math.Abs(c.x - mine.X) > 10 && Math.Abs(c.y - mine.Y) > 10);
        var watch = new TurnSummaryWatch(context, Human, session.Looks, null, RaceNames.Load(Path.Combine(Data, "rules")));

        watch.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, empty.x, empty.y, area.Id));    // вдали — всё равно запись
        watch.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, enemy.X, enemy.Y, target.Id));  // по чужому — нет
        watch.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, empty.x, empty.y, target.Id));  // никого — пустая цель
        watch.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, mine.X, mine.Y, target.Id));    // свой, выжил — с именем
        watch.StackMoving(new StackMove(enemy.Id, enemy.Owner, [(enemy.X, enemy.Y)]));

        var entries = watch.Finish(session.Looks);
        foreach (var e in entries) output.WriteLine($"{e.Kind} {e.Text}");
        const string NoTarget = @"для \fMedBold;\fNormal;";
        Assert.Equal([0, 0, 0], entries.Select(e => e.Kind));
        Assert.DoesNotContain(" для ", entries[0].Text);
        Assert.Contains(NoTarget, entries[1].Text);
        Assert.Contains(" для ", entries[2].Text);
        Assert.DoesNotContain(NoTarget, entries[2].Text);

        // Свой отряд погиб от заклинания по цели: гибель, затем запись с пустой целью (отряда в клетке уже нет — здесь распускают гостя
        // города: сначала воинов, потом лидера).
        var guest = map.Stacks.Values.OrderBy(s => s.Owner == Human ? 0 : 1).FirstOrDefault(s => s.Inside.Length > 0);
        Skip.If(guest is null, "нет гостя города");
        bool own = guest.Owner == Human;
        var killed = new TurnSummaryWatch(context, Human, session.Looks, null, RaceNames.Load(Path.Combine(Data, "rules")));
        killed.Happened(new TurnSight(TurnSightKind.Spell, enemy.Owner, guest.X, guest.Y, target.Id));
        killed.Happened(new TurnSight(TurnSightKind.Death, guest.Owner, guest.X, guest.Y, guest.Id));
        foreach (string unit in guest.UnitIds.Where(u => u != guest.LeaderId)) session.DismissUnit(guest.Id, unit);
        Assert.Equal(0, session.DismissUnit(guest.Id, guest.LeaderId));
        Assert.False(session.Map.Stacks.ContainsKey(guest.Id));
        var after = killed.Finish(session.Looks);
        Assert.Equal(own ? [2, 0] : [0], after.Select(e => e.Kind));   // чужой погиб — гибели в итогах нет, запись заклинания — есть
        Assert.Contains(NoTarget, after[^1].Text);
    }

    /// <summary>Подписка на ход партии: основной наблюдатель остаётся, подписчик слышит события и бой (без права на журнал), отписка
    /// возвращает прежнее; снятие основного подписчика не снимает.</summary>
    [SkippableFact]
    public void Watch_FansOutEventsButBattleStaysWithMain()
    {
        var (session, _) = Game();
        var main = new Recorder([]);
        var watcher = new Recorder(null);
        session.Observer = main;
        Assert.Same(main, session.Observer);
        session.Watch(watcher);
        var sight = new TurnSight(TurnSightKind.Hire, Human, 1, 1, "x");
        session.Observer!.Happened(sight);
        session.Observer.PlayerTurn(Other);
        var state = new BattleState { Units = [], AttackerGroup = "a", DefenderGroup = "d" };
        Assert.Same(main.Log, session.Observer.Battle(state));
        Assert.Equal([sight], main.Sights);
        Assert.Equal([sight], watcher.Sights);
        Assert.Equal([Other], watcher.Turns);
        Assert.Equal(1, watcher.Battles);

        session.Observer = null;
        Assert.NotNull(session.Observer);   // подписчик слышит и без основного
        Assert.Null(session.Observer!.Battle(state));
        session.Unwatch(watcher);
        Assert.Null(session.Observer);
    }

    private sealed class Recorder(IReadOnlyList<TurnRecord>? log) : ITurnObserver
    {
        public IReadOnlyList<TurnRecord>? Log => log;
        public List<TurnSight> Sights { get; } = [];
        public List<string> Turns { get; } = [];
        public int Battles { get; private set; }

        public void PlayerTurn(string playerId) => Turns.Add(playerId);

        public void StackMoving(StackMove move)
        {
        }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state)
        {
            Battles++;
            return log;
        }

        public void Happened(TurnSight sight) => Sights.Add(sight);
    }

    /// <summary>Итоги в сохранении v9: записи сборщика уходят в снимок партии (поставщик сцены), после загрузки сборщик продолжает с ними и
    /// с прежним днём, новые записи — в конец.</summary>
    [SkippableFact]
    public void Watch_SavedAndResumed_KeepsEntriesAndDay()
    {
        var (session, context) = Game();
        var map = session.Map;
        var mine = map.Stacks.Values.Where(s => s.Owner == Human && s.Inside.Length == 0).OrderBy(s => s.Id).First();
        var enemy = map.Stacks.Values.First(s => s.Owner != Human && s.Inside.Length == 0 && map.Players.ContainsKey(s.Owner));
        var races = RaceNames.Load(Path.Combine(Data, "rules"));
        var watch = new TurnSummaryWatch(context, Human, session.Looks, null, races);
        watch.Attacked(enemy.Id, mine.Id);
        session.SummariesForSave = () => new Dictionary<string, PendingSummary> { [Human] = new(watch.Day, watch.Finish(session.Looks)) };

        var loaded = GameSave.FromJson(GameSave.ToJson(session.Snapshot()));
        var again = new GameSession(loaded, session.Art, session.Book);
        var pending = Assert.Single(again.TakeSummaries());
        Assert.Empty(again.State.Summaries);
        var resumed = new TurnSummaryWatch(context, pending.Key, again.Looks, null, races, pending.Value);
        resumed.Happened(new TurnSight(TurnSightKind.Death, Human, mine.X, mine.Y, mine.Id));

        Assert.Equal(watch.Day, resumed.Day);
        Assert.Equal([1, 2], resumed.Finish(again.Looks).Select(e => e.Kind));
    }

    private static (GameSession Session, WindowContext Context) Game()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        return (session, new WindowContext(session, Human, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules"))));
    }
}
