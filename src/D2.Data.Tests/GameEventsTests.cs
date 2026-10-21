using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Menu;
using D2.Data.Play;
using D2.Data.Sg;
using D2.Rules.Map;
using KSg = Kaitai.Sg;

namespace D2.Data.Tests;

/// <summary>События сценария в партии (<see cref="GameSession.RunEvents"/>): старт, запись и чтение, конец хода.</summary>
[Trait("Category", "Corpus")]
public class GameEventsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Новая партия «Power to Corrupt» за Кланы: в день 1 у человека срабатывает интро Кланов (<c>EV001D</c>, частота 1,
    /// один раз) — два попапа, как в квест-логе автосохранения t01-001 корпуса 20260919-human, и событие выключено.</summary>
    [SkippableFact]
    public void NewGame_DayOneIntro_MatchesCorpusQuestLog()
    {
        string save = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t01-001.sg");
        string scenario = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "Power to Corrupt");
        Skip.IfNot(File.Exists(save) && File.Exists(scenario), "нет корпуса или сценария порта");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var setup = new NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == "Power to Corrupt"), Race = 3, LordCategory = 0, Difficulty = 1,
        };

        var state = NewGame.Start(ScenarioFile.Read(scenario), setup, content.Catalog, book, new Random(1));

        var want = SgFile.Open(save).Raw.Objects.Select(o => o.Data).OfType<KSg.MidQuestLog>().SelectMany(l => l.Sequences)
            .Select(q => (q.IdPlayer.String.ToUpperInvariant(), (int)q.SeqNum.Value, (int)q.CurTurn.Value, q.IdEvent.String.ToUpperInvariant(),
                          (int)q.EffNum.Value)).ToList();
        var got = state.EventProgress.Log.Select(l => (l.PlayerId.ToUpperInvariant(), l.Seq, l.Turn, l.EventId.ToUpperInvariant(), l.EffectNum));
        Assert.Equal(want, got);
        Assert.False(state.EventProgress.IsEnabled("S073EV001D"));
        Assert.Equal(2, state.Messages.Count);
        Assert.All(state.Messages, m => Assert.Equal(state.Info.Humans[0], m.PlayerId));
    }

    /// <summary>Состояние событий пишется в сохранение (формат 2) и читается обратно; сохранение версии 1 читается с пустыми
    /// событиями.</summary>
    [SkippableFact]
    public void Save_KeepsEvents()
    {
        string scenario = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "Power to Corrupt");
        Skip.IfNot(File.Exists(scenario), "нет сценария порта");
        var data = ScenarioFile.Read(scenario);
        var progress = EventProgress.From(data.Events).Enable("S073EV0000", false).Modify(new ModifyVariableEffect(1, 9, 5))
                                                      .Logged("S073PL000A", 3, "S073EV0008", 0);
        var state = new GameState(GameInfo.FromMap("PtC", data.Looks), data.Looks)
        {
            Events = data.Events, EventProgress = progress,
            Messages = [new EventMessage("S073PL000A", 3, "S073EV0008", new PopupEffect("текст", Image: "IMG"))],
        };
        var back = GameSave.FromJson(GameSave.ToJson(state));
        Assert.Equal(data.Events.Events.Count, back.Events.Events.Count);
        Assert.False(back.EventProgress.IsEnabled("S073EV0000"));
        Assert.Equal(5, back.EventProgress.Variables[1]);
        Assert.Equal(progress.Log, back.EventProgress.Log);
        Assert.Equal(state.Messages, back.Messages);

        string v1 = GameSave.ToJson(new GameState(state.Info, state.Looks)).Replace("\"Version\":2", "\"Version\":1");
        Assert.Empty(GameSave.FromJson(v1).Events.Events);
    }

    /// <summary>Конец хода с событиями «Power to Corrupt»: ходы ИИ и начало хода человека проходят, выключенные в сценарии
    /// события не срабатывают, отчёт дня не пишет непереносённых эффектов этого сценария, кроме известных.</summary>
    [SkippableFact]
    public void EndTurn_WithScenarioEvents_Runs()
    {
        string scenario = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "Power to Corrupt");
        Skip.IfNot(File.Exists(scenario), "нет сценария порта");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var setup = new NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == "Power to Corrupt"), Race = 3, LordCategory = 0, Difficulty = 1,
        };
        var session = new GameSession(NewGame.Start(ScenarioFile.Read(scenario), setup, content.Catalog, book, new Random(1)),
                                      content.MapArt, book);
        var report = session.EndTurn();
        Assert.Equal(2, session.Map.Turn);
        // Интро ИИ дня 1 уже отработали в их ход (EndTurn дня 1 — ходы ИИ дня 1).
        Assert.Contains(report.Events, e => e.StartsWith("событие S073EV001C", StringComparison.OrdinalIgnoreCase));
        Assert.False(session.State.EventProgress.IsEnabled("S073EV001C"));
    }

    /// <summary>Вход в свой город и посещение объекта — точка проверки событий (<c>StackMove 0x41e46b</c>, <c>docs/scenario-events.md</c>
    /// §1.2): <c>L_ENTER_CITY</c> и <c>L_VISIT_SITE</c> срабатывают сразу, а не на следующей проверке; посещение лавки вносит игрока в
    /// список посетивших (<c>0x5d7664</c>: по игроку, без повторов). Прежде вход в свой город возвращался до событий, а список посетивших
    /// в партии не пополнялся вовсе (аудит 13.10).</summary>
    [SkippableFact]
    public void EnterOwnCity_AndVisitSite_RunEventsAtOnce()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var map = looks.Map;
        var village = map.Sites.First(s => s.Kind == SiteKind.Village) with { Owner = human.Id, StackId = "", Guards = [], GuardSlots = [] };
        var merchant = map.Sites.First(s => s.Kind == SiteKind.Merchant) with { Visitors = [] };
        map = GameSession.With(map, sites: [.. map.Sites.Select(s => s.Id == village.Id ? village : s.Id == merchant.Id ? merchant : s)]);
        bool[] all = [true, true, true, true, true, true];
        ScenarioEvent Event(string id, EventCondition when, int variable) =>
            new(id, Enabled: true, OccurOnce: true, Chance: 100, Order: 0, all, [when], [new ModifyVariableEffect(variable, 4, 7)]);
        var events = new ScenarioEventSet([Event("T0EV0001", new EnterCityCondition(village.Id), 1),
                                           Event("T0EV0002", new VisitSiteCondition(merchant.Id), 2)],
                                          new Dictionary<string, EventZone>(StringComparer.OrdinalIgnoreCase),
                                          new Dictionary<int, int> { [1] = 0, [2] = 0 });
        var state = new GameState(GameInfo.FromMap("x", looks with { Map = map }), looks with { Map = map })
        {
            Events = events, EventProgress = EventProgress.From(events),
        };
        var session = new GameSession(state, SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));
        var mine = session.Map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);

        Assert.True(session.Enter(mine.Id, village.Id).Entered);
        Assert.Equal(7, session.State.EventProgress.Variables[1]);

        Assert.Null(session.Enter(mine.Id, merchant.Id).Refused);
        Assert.Equal([human.Id], session.Map.Sites.First(s => s.Id == merchant.Id).Visitors);
        Assert.Equal(7, session.State.EventProgress.Variables[2]);
        session.Enter(mine.Id, merchant.Id);
        Assert.Single(session.Map.Sites.First(s => s.Id == merchant.Id).Visitors);   // повторное посещение список не растит
    }

    /// <summary>«Бросить мешок» — своя точка проверки событий (<c>DropBag</c> <c>0x41f257</c> → <c>0x41f46e</c>, триггерер — отряд): событие
    /// «отряд в области» вокруг клетки отряда срабатывает сразу. Так же устроены действие вора и его отмена, постановка жезла
    /// (<c>RunActionEvents</c>). Прежде проверки не было (аудит 13.10).</summary>
    [SkippableFact]
    public void DropBag_RunsEventCheck()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        // Отряду — любой экземпляр предмета карты: без предметов пустую клетку мешок не принимает (DropCheck).
        var outside = looks.Map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);
        string item = looks.Map.ItemTypes.Keys.First();
        var map = GameSession.With(looks.Map, stacks: looks.Map.Stacks.ToDictionary(p => p.Key,
            p => p.Key == outside.Id ? p.Value with { Items = [item] } : p.Value, StringComparer.OrdinalIgnoreCase));
        looks = looks with { Map = map };
        var probe = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt,
                                    GameBook.Load(Path.Combine(Data, "rules")));
        var stack = probe.Map.Stacks[outside.Id];
        var cell = (from dx in new[] { -1, 0, 1 } from dy in new[] { -1, 0, 1 } select (X: stack.X + dx, Y: stack.Y + dy))
            .First(c => probe.DropCheck(stack.Id, c.X, c.Y, out _) is null);
        var zone = new EventZone("T0LO0001", stack.X, stack.Y, 0);
        var events = new ScenarioEventSet(
            [new ScenarioEvent("T0EV0001", true, true, 100, 0, [true, true, true, true, true, true], [new ZoneCondition(zone.Id)],
                               [new ModifyVariableEffect(1, 4, 7)])],
            new Dictionary<string, EventZone>(StringComparer.OrdinalIgnoreCase) { [zone.Id] = zone }, new Dictionary<int, int> { [1] = 0 });
        var session = new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book);

        Assert.Null(session.DropBag(stack.Id, cell.X, cell.Y, out _));

        Assert.Equal(7, session.State.EventProgress.Variables[1]);
    }

    /// <summary>«Меню героя» после боя — своя точка проверки событий (<c>RequestPaperdoll</c> <c>0x41f397</c> → <c>0x41f46e</c>, триггерер —
    /// отряд; единственный отправитель запроса — <c>CTaskBattle</c> <c>0x4d856d</c> по <c>BTN_PAPERDOLL</c>): событие «отряд в области» вокруг
    /// клетки отряда срабатывает сразу. Без отряда — ни проверки, ни окна. Прежде проверки не было (аудит 13.10, «Осталось» п. 3).</summary>
    [SkippableFact]
    public void RequestPaperdoll_RunsEventCheck()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var stack = looks.Map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);
        var probe = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt,
                                    GameBook.Load(Path.Combine(Data, "rules")));
        var zone = new EventZone("T0LO0001", stack.X, stack.Y, 0);
        var events = new ScenarioEventSet(
            [new ScenarioEvent("T0EV0001", true, true, 100, 0, [true, true, true, true, true, true], [new ZoneCondition(zone.Id)],
                               [new ModifyVariableEffect(1, 4, 7)])],
            new Dictionary<string, EventZone>(StringComparer.OrdinalIgnoreCase) { [zone.Id] = zone }, new Dictionary<int, int> { [1] = 0 });
        var session = new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book);

        Assert.False(session.RequestPaperdoll("S999KC9999"));
        Assert.Equal(0, session.State.EventProgress.Variables[1]);
        Assert.True(session.RequestPaperdoll(stack.Id));

        Assert.Equal(7, session.State.EventProgress.Variables[1]);
    }

    /// <summary><c>L_CAST_SPELL_MAP</c> с заклинанием «Туман» (категория 6): заклинатель — <c>ID_PLAYER1</c> (<c>0x5dc173</c>, как у
    /// развеивания), область уходит под туман у остальных игроков. Прежде эффект падал в «не перенесён» — 5 эффектов сценариев и саг
    /// (аудит 13.10).</summary>
    [SkippableFact]
    public void CastSpellMap_Fog_CoversZoneForOthers()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var outside = looks.Map.Stacks.Values.First(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0);
        string item = looks.Map.ItemTypes.Keys.First();
        looks = looks with { Map = GameSession.With(looks.Map, stacks: looks.Map.Stacks.ToDictionary(p => p.Key,
            p => p.Key == outside.Id ? p.Value with { Items = [item] } : p.Value, StringComparer.OrdinalIgnoreCase)) };
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var fogSpell = book.Spells.Values.First(s => s.Category == D2.Rules.Magic.SpellCategory.Fog && s.Area != 999);
        var probe = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), SharedContent.Load(Data).MapArt, book);
        var stack = probe.Map.Stacks[outside.Id];
        var cell = (from dx in new[] { -1, 0, 1 } from dy in new[] { -1, 0, 1 } select (X: stack.X + dx, Y: stack.Y + dy))
            .First(c => probe.DropCheck(stack.Id, c.X, c.Y, out _) is null);
        var other = probe.Map.Players.Values.First(p => p.Id != human.Id && probe.Map.Fog.ContainsKey(p.FogId) && book.Economy.Races[p.RaceId].Playable);
        probe.Map.Fog[other.FogId][stack.X, stack.Y] = false;
        var zone = new EventZone("T0LO0001", stack.X, stack.Y, 0);
        var events = new ScenarioEventSet(
            [new ScenarioEvent("T0EV0001", true, true, 100, 0, [true, true, true, true, true, true], [new ZoneCondition(zone.Id)],
                               [new CastSpellAtEffect(fogSpell.Id, zone.Id, human.Id)])],
            new Dictionary<string, EventZone>(StringComparer.OrdinalIgnoreCase) { [zone.Id] = zone }, new Dictionary<int, int>());
        var session = new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book);

        Assert.Null(session.DropBag(stack.Id, cell.X, cell.Y, out _));

        Assert.Contains("T0EV0001", session.State.EventProgress.Disabled);
        Assert.True(session.Map.Fog[other.FogId][stack.X, stack.Y], $"{other.Id} ({other.RaceId}) {fogSpell.Id} area {fogSpell.Area}");
    }

    /// <summary>Шаг и вход тем же ходом — одна проверка событий (<c>StackMove 0x41e46b</c>): вход продолжает проверку шага, и
    /// сработавшее на шаге событие (не одноразовое — статуя лечения Кланов <c>EV0009</c>, зона радиуса 0) второй раз не
    /// срабатывает; новая проверка — срабатывает снова.</summary>
    [SkippableFact]
    public void MoveThenEnter_IsOneCheck()
    {
        string scenario = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "Power to Corrupt");
        Skip.IfNot(File.Exists(scenario), "нет сценария порта");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var setup = new NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == "Power to Corrupt"), Race = 3, LordCategory = 0, Difficulty = 1,
        };
        var session = new GameSession(NewGame.Start(ScenarioFile.Read(scenario), setup, content.Catalog, book, new Random(1)),
                                      content.MapArt, book);
        string human = session.State.Info.Humans[0];
        var stack = session.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase));
        session.MoveStack(stack.Id, 66, 27, 0, 0);
        var check = session.MoveCheckOf(session.Map.Stacks[stack.Id]);
        Assert.NotNull(check);
        Assert.Contains("S073EV0009", check);
        Assert.Empty(session.RunEvents("", stack.Id, executed: check));
        Assert.Contains(session.RunEvents("", stack.Id), f => f.StartsWith("S073EV0009", StringComparison.OrdinalIgnoreCase));
    }
}
