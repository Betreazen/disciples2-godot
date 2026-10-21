using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Эффекты событий в партии (<c>docs/scenario-events.md</c> §3а): живые случаи корпуса и синтетические события на
/// сценариях порта.</summary>
[Trait("Category", "Corpus")]
public class EventEffectsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules"))));

    /// <summary>Партия из сохранения корпуса с событиями этого сохранения; люди — из файла.</summary>
    private static GameSession FromSave(string corpus, string save)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), corpus, save);
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        var events = EventLoader.Load(path);
        var state = new GameState(GameInfo.FromMap("x", looks), looks) { Events = events, EventProgress = EventProgress.From(events) };
        return new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book);
    }

    /// <summary>Партия на сценарии порта с одним событием «в начале хода <paramref name="player"/>» и заданными эффектами.</summary>
    private static GameSession WithEvent(string scenario, string player, params EventEffect[] effects) =>
        WithEvents(scenario, player, new ScenarioEvent("S999EV0000", true, true, 100, -1, All, [new FrequencyCondition(1)], effects, "проверка", All));

    private static readonly bool[] All = [true, true, true, true, true, true];

    /// <summary>Партия на сценарии порта только с событиями <paramref name="list"/>.</summary>
    private static GameSession WithEvents(string scenario, string player, params ScenarioEvent[] list)
    {
        string path = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), scenario);
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var data = ScenarioFile.Read(path);
        var events = data.Events with { Events = list };
        var looks = data.Looks with { Map = GameSession.With(data.Looks.Map, turn: 1) };
        var state = new GameState(new GameInfo(scenario, scenario, [player], 1), looks) { Events = events, EventProgress = EventProgress.From(events) };
        return new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book);
    }

    /// <summary>Ставит отряд в клетку и проверяет события его шага.</summary>
    private static IReadOnlyList<string> StepInto(GameSession session, string stackId, int x, int y)
    {
        session.MoveStack(stackId, x, y, 0, 0);
        return session.MoveCheckOf(session.Map.Stacks[stackId]) is { } done ? [.. done] : [];
    }

    /// <summary>Корпус 20260919-human, день 3: <c>EV0018</c> «Лок Эльфы» (область <c>LO0009</c>, отряд Легионов) — попап и
    /// <c>L_MOVE_STACK</c>: <c>KC000a</c> переставлен в центр <c>LO000d</c> (90,23), запас хода тот же (сохранение t04-010).</summary>
    [SkippableFact]
    public void MoveStack_Corpus_Elves()
    {
        var session = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var legion = session.Map.Stacks.Values.First(s => s.Owner.Equals("S073PL000b", StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        int move = session.Map.Stacks["S073KC000a"].Move;
        Assert.Contains("S073EV0018", StepInto(session, legion.Id, 75, 18));
        var elves = session.Map.Stacks["S073KC000a"];
        Assert.Equal((90, 23, move), (elves.X, elves.Y, elves.Move));
    }

    /// <summary>Корпус 20260919-human, день 47: <c>EV001b</c> «Лок Неверный» — <c>L_CHANGE_OWNER</c> с пустым игроком: нейтральный
    /// <c>KC0048</c> переходит к владельцу триггерера с его подрасой, приказ «обычный» (сохранение t47-080: PL000a, SR000a, 1).</summary>
    [SkippableFact]
    public void ChangeOwner_Corpus_Infidel()
    {
        var session = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t46-079.sg");
        var dwarf = session.Map.Stacks.Values.First(s => s.Owner.Equals("S073PL000a", StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        Assert.Contains("S073EV001B", StepInto(session, dwarf.Id, 67, 48).Select(e => e.ToUpperInvariant()));
        var infidel = session.Map.Stacks["S073KC0048"];
        Assert.Equal(("S073PL000A", dwarf.Subrace.ToUpperInvariant(), (int)StackOrder.Normal),
                     (infidel.Owner.ToUpperInvariant(), infidel.Subrace.ToUpperInvariant(), infidel.Order));
    }

    /// <summary>«A Return To Simpler Times», шаблон <c>S001TM0000</c> в области <c>LO0009</c>: отряд нейтрала с лидером
    /// «Shagherul» в клетке 3 (клетка шаблона с лидером), юнит клетки 4 и большой юнит клеток 0–1, модификатор клетки 3 — у
    /// лидера, приказ шаблона (берсерк, без цели), <c>SRCTMPL_ID</c>; клетка — центр локации, если свободен.</summary>
    [SkippableFact]
    public void CreateStack_FromTemplate()
    {
        var session = WithEvent("'A Return To Simpler Times'", "S001PL0001", new CreateStackEffect("S001TM0000", "S001LO0009"));
        int before = session.Map.Stacks.Count;
        var fired = session.RunEvents("S001PL0001", "");
        Assert.Contains(fired, f => f.StartsWith("S999EV0000", StringComparison.Ordinal));
        var created = Assert.Single(session.Map.Stacks.Values, s => s.TemplateId == "S001TM0000");
        Assert.Equal(before + 1, session.Map.Stacks.Count);
        Assert.Equal(("S001PL0000", (int)StackOrder.Bezerk), (created.Owner, created.Order));
        Assert.Equal(0, created.Slots[3]);
        var leader = session.Map.Units[created.LeaderId];
        Assert.Equal("G000UU0098", leader.TypeId);
        Assert.Contains("G000UM9031", leader.Modifiers);
        Assert.Equal(3, created.UnitIds.Count);
        Assert.True(Math.Max(Math.Abs(created.X - 40), Math.Abs(created.Y - 3)) <= 3);
        Assert.Equal("Shagherul", session.Looks.Stacks.Single(s => s.Id == created.Id).LeaderName);
    }

    /// <summary><c>L_CREATE_STACK</c> шаблона игрока: туман владельцу вокруг центра локации — <c>±scout</c> лидера без +1, квадрат без
    /// угловых клеток, как обычный обзор (<c>0x43bd74</c> → <c>CEffectFog</c> → <c>0x5d4dfc</c>).</summary>
    [SkippableFact]
    public void CreateStack_OwnerFog_SquareWithoutCorners()
    {
        const string human = "S001PL0001";
        var plain = WithEvent("'A Return To Simpler Times'", human, new CreateStackEffect("S001TM0000", "S001LO0009"));
        var events = plain.State.Events;
        var template = events.Templates["S001TM0000"] with { Owner = human };
        events = events with { Templates = new Dictionary<string, StackTemplate>(events.Templates) { ["S001TM0000"] = template } };
        var session = new GameSession(plain.State with { Events = events }, Shared.Value.Content.MapArt, Shared.Value.Book);
        var fog = session.Map.Fog[session.Map.Players[human].FogId];
        for (int x = 0; x < fog.GetLength(0); x++)
            for (int y = 0; y < fog.GetLength(1); y++) fog[x, y] = true;
        var zone = events.Zones["S001LO0009"];
        int r = session.Book!.ScoutByType[template.LeaderType];
        session.RunEvents(human, "");
        Assert.False(fog[zone.X, zone.Y]);
        Assert.False(fog[zone.X + r, zone.Y]);
        Assert.True(fog[zone.X + r, zone.Y + r]);   // угол квадрата не открывается
        Assert.True(fog[zone.X + r + 1, zone.Y]);
    }

    /// <summary>Предметы, заклинание, земля и дипломатия — по разбору.</summary>
    [SkippableFact]
    public void Items_Spell_Terrain_Diplomacy()
    {
        const string human = "S073PL000a", other = "S073PL000b";
        var session = WithEvent("Power to Corrupt", human,
            new GiveItemEffect(1, "G000IG0001"), new GiveSpellEffect("G000SS0001"),
            new ChangeTerrainEffect("S073LO0000", 2, 3), new AllyEffect(human, other, true));
        var zone = session.State.Events.Zones["S073LO0000"];
        session.RunEvents(human, "");
        var capital = session.Map.Sites.Single(s => s.Kind == SiteKind.Capital && s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capital.Items, i => session.Map.ItemTypes[i] == "G000IG0001");
        Assert.Contains("G000SS0001", session.Map.Players[human].KnownSpells);
        for (int x = zone.X - 1; x <= zone.X + 1; x++)
            for (int y = zone.Y - 1; y <= zone.Y + 1; y++)
                if (session.Map.Tiles[x, y].Ground is Ground.Plain or Ground.Forest) Assert.Equal(2, session.Map.Tiles[x, y].Terrain);
        var races = session.Book!.Economy.Races;
        var relation = session.Map.Relations.Single(r => r.Pairs(races[session.Map.Players[human].RaceId].RaceType,
                                                                  races[session.Map.Players[other].RaceId].RaceType));
        Assert.True(relation.Ally);
        Assert.True(relation.AiCannotBreak);   // PERMALLI
        Assert.Equal(session.Book.Diplomacy.MakeAlly, relation.Current);   // союз ставит D_MK_ALLY (0x5d95f4)
        Assert.Equal(1, relation.AllianceTurn);
    }

    /// <summary><c>L_DIPLOMACY</c> «война» после союза (<c>0x43cbbd</c>): союз разрывается (<c>D_BK_ALLY</c>, у первого игрока +1 разрыв),
    /// текущее — значение эффекта, «прежнее» не меняется (<c>CEffectDiploRelation</c> передаёт снимок прежнего); <c>L_WIN</c> снимает литьё
    /// всех игроков (<c>CEffectRemoveSpell(…, 9999)</c>).</summary>
    [SkippableFact]
    public void Diplomacy_BreaksAlliance_Win_RemovesCasts()
    {
        const string human = "S073PL000a", other = "S073PL000b";
        var session = WithEvent("Power to Corrupt", human,
            new AllyEffect(human, other, false), new DiplomacyEffect(human, other, 10, false), new WinEffect(true, human));
        var map = GameSession.With(session.Map, spellCasts: [new CastRecord(other, "G000SS0001", 9)], itemCasts: [new CastRecord(human, "G000IG0001", 9)]);
        session = new GameSession(session.State with { Looks = GameSession.Retold(session.Looks, map, session.Looks.Stacks) },
                                  session.Art, session.Book);
        int breaks = session.Map.Players[human].QtyBreaks;
        var races = session.Book!.Economy.Races;
        Relation Pair() => session.Map.Relations.Single(r => r.Pairs(races[session.Map.Players[human].RaceId].RaceType,
                                                                      races[session.Map.Players[other].RaceId].RaceType));
        int previous = Pair().Previous;
        session.RunEvents(human, "");
        var relation = Pair();
        Assert.False(relation.Ally);
        Assert.Equal((10, previous), (relation.Current, relation.Previous));
        Assert.Equal(breaks + 1, session.Map.Players[human].QtyBreaks);
        Assert.Empty(session.Map.SpellCasts);
        Assert.Empty(session.Map.ItemCasts);
    }

    /// <summary>Разрыв союза событием (<c>0x5d91fd</c>, ранг 1): мутатор <c>(P1, P2, D_BK_ALLY)</c> — третьему, враждующему с P2, минус
    /// половина к P1 (не наоборот, как у заключения союза); бит 31 «ИИ не разорвёт» снимается.</summary>
    [SkippableFact]
    public void Diplomacy_Break_ThirdPartyOfSecond_ClearsPermanent()
    {
        const string human = "S073PL000a", other = "S073PL000b", third = "S073PL000c";
        var session = WithEvent("Power to Corrupt", human, new AllyEffect(human, other, true), new DiplomacyEffect(human, other, 10, false));
        var races = session.Book!.Economy.Races;
        int Race(string p) => races[session.Map.Players[p].RaceId].RaceType;
        var relations = session.Map.Relations.Select(r =>
            r.Pairs(Race(other), Race(third)) ? r with { Raw = r.Raw & ~(0x7Fu << 1) & ~(1u << 30) }   // P2 и третий — война (0)
            : r.Pairs(Race(human), Race(third)) ? r with { Raw = (r.Raw & ~(0x7Fu << 1) & ~(1u << 30)) | (50u << 1) }
            : r).ToList();
        session = new GameSession(session.State with { Looks = GameSession.Retold(session.Looks, GameSession.With(session.Map, relations: relations), session.Looks.Stacks) },
                                  session.Art, session.Book);
        Relation Pair(string a, string b) => session.Map.Relations.Single(r => r.Pairs(Race(a), Race(b)));

        session.RunEvents(human, "");

        Assert.False(Pair(human, other).Ally);
        Assert.Equal(0u, Pair(human, other).Raw >> 31);
        Assert.Equal(50 - session.Book!.Diplomacy.BreakAlly / 2, Pair(human, third).Current);
        Assert.Equal(0, Pair(other, third).Current);
    }

    /// <summary>Мелочи по <c>canApply</c> движка: <c>L_GIVE_SPELL</c> — заклинания нет в таблице — ничего (<c>0x60c104</c>);
    /// <c>L_CHANGE_LANDMARK</c> — нового типа нет в таблице — ничего (<c>0x60f6b6</c>); <c>L_DESTROY_ITEM</c> снимает и запись зарядов
    /// уничтоженного экземпляра (<c>0x5eba93</c>).</summary>
    [SkippableFact]
    public void GiveSpell_ChangeLandmark_Unknown_DestroyItem_Charges()
    {
        const string human = "S073PL000a";
        var probe = WithEvent("Power to Corrupt", human);
        var landmark = probe.Looks.Landmarks.First();
        var holder = probe.Map.Stacks.Values.First(s => s.Items.Count > 0);
        string instance = holder.Items[0], type = probe.Map.ItemTypes[instance];
        var session = WithEvent("Power to Corrupt", human, new GiveSpellEffect("G000SS9999"), new ChangeLandmarkEffect(landmark.Id, "G000MG9999"),
                                new DestroyItemEffect(type, false));
        var charges = new Dictionary<string, int>(session.Map.TalismanCharges, StringComparer.OrdinalIgnoreCase) { [instance] = 3 };
        session = new GameSession(session.State with { Looks = GameSession.Retold(session.Looks, GameSession.With(session.Map, talismanCharges: charges), session.Looks.Stacks) },
                                  session.Art, session.Book);
        var known = session.Map.Players[human].KnownSpells;

        session.RunEvents(human, "");

        Assert.Equal(known, session.Map.Players[human].KnownSpells);
        Assert.Equal(landmark.Type, session.Looks.Landmarks.Single(l => l.Id == landmark.Id).Type);
        Assert.False(session.Map.TalismanCharges.ContainsKey(instance));
    }

    /// <summary>«Power to Corrupt», <c>EV0009</c> «КГ Статуя Лечения» (область <c>LO0005</c>, радиус 0, не одноразовое):
    /// <c>L_CAST_SPELL</c> на отряд-триггерер — раненые юниты отряда Кланов лечатся (заклинатель пуст, маны не тратится).</summary>
    [SkippableFact]
    public void CastSpell_Statue_HealsTriggerer()
    {
        var session = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var dwarf = session.Map.Stacks.Values.First(s => s.Owner.Equals("S073PL000a", StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var bank = session.Map.Players["S073PL000a"].Bank;
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string id in dwarf.UnitIds) units[id] = units[id] with { Hp = 1 };
        session = new GameSession(session.State with { Looks = GameSession.Retold(session.Looks, GameSession.With(session.Map, units: units), session.Looks.Stacks) },
                                  session.Art, session.Book);
        Assert.Contains("S073EV0009", StepInto(session, dwarf.Id, 66, 27).Select(e => e.ToUpperInvariant()));
        Assert.True(dwarf.UnitIds.Sum(id => session.Map.Units[id].Hp) > dwarf.UnitIds.Count, "статуя лечит");
        Assert.Equal(bank, session.Map.Players["S073PL000a"].Bank);
    }

    /// <summary>Туман (квадрат r+1 без углов вокруг центра локации; в маске <c>true</c> — скрыто), ландмарк, горы и уничтожение
    /// предмета.</summary>
    [SkippableFact]
    public void Fog_Landmark_Mountains_DestroyItem()
    {
        const string human = "S073PL000a";
        var probe = WithEvent("Power to Corrupt", human);
        var landmark = probe.Looks.Landmarks.First();
        var mountain = probe.State.Events.Zones.Values.FirstOrDefault(z => Around(probe.Map, z).Any(c => probe.Map.Tiles[c.X, c.Y].Ground == Ground.Mountain));
        var session = WithEvent("Power to Corrupt", human,
            new UnfogEffect("S073LO0000", true, 0, [new UnfogEntry("", human)]), new RemoveLandmarkEffect(landmark.Id, false),
            new GiveItemEffect(1, "G000IG0001"), new DestroyItemEffect("G000IG0001", false),
            new RemoveMountainsEffect(mountain?.Id ?? "S073LO0000"));
        var hidden = session.Map.Fog[session.Map.Players[human].FogId];
        for (int x = 0; x < session.Map.Size; x++)
            for (int y = 0; y < session.Map.Size; y++) hidden[x, y] = true;   // в сценарии тумана нет — всё скрыть
        session.RunEvents(human, "");
        var zone = session.State.Events.Zones["S073LO0000"];
        var fog = session.Map.Fog[session.Map.Players[human].FogId];
        for (int x = zone.X - 1; x <= zone.X + 1; x++)
            for (int y = zone.Y - 1; y <= zone.Y + 1; y++)
                Assert.Equal(Math.Abs(x - zone.X) == 1 && Math.Abs(y - zone.Y) == 1, fog[x, y]);   // открыто всё, кроме углов
        Assert.DoesNotContain(session.Looks.Landmarks, l => l.Id == landmark.Id);
        Assert.False(session.Map.Objects.ContainsKey(landmark.Id));
        Assert.DoesNotContain(session.Map.Sites.SelectMany(s => s.Items), i => session.Map.ItemTypes.GetValueOrDefault(i) == "G000IG0001");
        if (mountain is not null)
            Assert.DoesNotContain(Around(session.Map, mountain), c => session.Map.Tiles[c.X, c.Y].Ground == Ground.Mountain);
    }

    private sealed class GivenSights : ITurnObserver
    {
        public List<TurnSight> All { get; } = [];
        public void PlayerTurn(string playerId) { }
        public void StackMoving(StackMove move) { }
        public IReadOnlyList<D2.Rules.Battle.TurnRecord>? Battle(BattleState state) => null;
        public void Happened(TurnSight sight) { if (sight.Kind is TurnSightKind.ItemsGiven or TurnSightKind.SpellsGiven) All.Add(sight); }
    }

    /// <summary><c>L_GIVE_ITEM</c> и <c>L_GIVE_SPELL</c> шлют получателю сообщения 44/43 (<c>0x438616</c> → <c>0x47ac42</c>,
    /// <c>0x438762</c> → <c>0x47ab13</c>) — точка окна полученного: предмет в столицу кандидата (клетка столицы, объект — событие),
    /// новое заклинание; уже известное заклинание — ни записи, ни окна.</summary>
    [SkippableFact]
    public void GiveItem_GiveSpell_ReportWhatCame()
    {
        const string human = "S073PL000a";
        var probe = WithEvent("Power to Corrupt", human);
        string spell = FirstUnknownSpell(probe);
        var session = WithEvent("Power to Corrupt", human, new GiveItemEffect(1, "G000IG0001"), new GiveSpellEffect(spell),
                                new GiveSpellEffect(spell));
        var sights = new GivenSights();
        session.Watch(sights);
        session.RunEvents(human, "");
        var capital = session.Map.Sites.Single(s => s.Kind == SiteKind.Capital && s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, sights.All.Count);
        var item = sights.All[0];
        Assert.Equal((TurnSightKind.ItemsGiven, human, capital.X, capital.Y, "S999EV0000"), (item.Kind, item.PlayerId, item.X, item.Y, item.Id));
        Assert.Equal("G000IG0001", session.Map.ItemTypes[Assert.Single(item.Ids)]);
        Assert.Equal((TurnSightKind.SpellsGiven, spell.ToUpperInvariant()), (sights.All[1].Kind, Assert.Single(sights.All[1].Ids)));

        static string FirstUnknownSpell(GameSession s) =>
            s.Book!.Spells.Keys.Order(StringComparer.Ordinal).First(id => !s.Map.Players["S073PL000a"].KnownSpells.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Победа объявляется в конце команды (<c>0x421077</c>): событие после <c>L_WIN</c> в той же проверке ещё срабатывает (попап
    /// дошёл), вторая победа не засчитывается; новая проверка после победы — нет.</summary>
    [SkippableFact]
    public void Win_EventsOfSameAction_StillFire()
    {
        const string human = "S073PL000a", other = "S073PL000b";
        var win = new ScenarioEvent("S999EV0000", true, true, 100, 1, All, [new FrequencyCondition(1)], [new WinEffect(true, human)], "победа", All);
        var after = new ScenarioEvent("S999EV0001", true, true, 100, 2, All, [new FrequencyCondition(1)],
                                      [new PopupEffect("после победы"), new WinEffect(true, other)], "после", All);
        var session = WithEvents("Power to Corrupt", human, win, after);
        var fired = session.RunEvents(human, "");
        Assert.Contains(fired, f => f.StartsWith("S999EV0001", StringComparison.Ordinal));
        Assert.Equal(human, session.State.Info.Winner);
        Assert.Single(session.TakeMessages(human));
        Assert.Empty(session.RunEvents(human, ""));
    }

    /// <summary><c>L_REMOVE_MOUNTAIN</c> в углу большого объекта гор (Power to Corrupt, зона радиуса 0): угол — равнина, объект убран,
    /// остальные его горные клетки заново покрыты новыми объектами (<c>0x5efb81</c>): без наложений, каждая горная клетка охвата — ровно
    /// одним объектом; квадраты по убыванию размера, id — с наибольшего прежнего + 1 по порядку, картинка — меньше числа картинок
    /// (<c>GTileDBI</c>), раса — по местности квадрата.</summary>
    [SkippableFact]
    public void RemoveMountains_RelaysOutsideZone()
    {
        const string human = "S073PL000a";
        var probe = WithEvent("Power to Corrupt", human);
        var big = probe.Looks.Mountains.Where(g => g.Width >= 3).OrderBy(g => g.Id).First();
        var zone = new EventZone("S999LO0000", big.X, big.Y, 0);
        var plain = WithEvent("Power to Corrupt", human, new RemoveMountainsEffect(zone.Id));
        var events = plain.State.Events with { Zones = new Dictionary<string, EventZone>(plain.State.Events.Zones) { [zone.Id] = zone } };
        var session = new GameSession(plain.State with { Events = events }, plain.Art, plain.Book);
        int maxId = probe.Looks.Mountains.Max(g => g.Id);
        var sights = new SightRecorder();
        session.Observer = sights;

        session.RunEvents(human, "");

        // Точка показа сноса (RMVMOUNTAIN*, сессия Г): центр и локация, бывшая горная клетка.
        var shown = Assert.Single(sights.Sights, s => s.Kind == TurnSightKind.MountainsRemoved);
        Assert.Equal((human, big.X, big.Y, zone.Id), (shown.PlayerId, shown.X, shown.Y, shown.Id));
        Assert.Equal([(big.X, big.Y)], shown.Cells);
        Assert.Equal(Ground.Plain, session.Map.Tiles[big.X, big.Y].Ground);
        Assert.DoesNotContain(session.Looks.Mountains, g => g.Id == big.Id);
        var fresh = session.Looks.Mountains.Where(g => g.Id > maxId).ToList();
        Assert.NotEmpty(fresh);
        Assert.Equal(Enumerable.Range(maxId + 1, fresh.Count), fresh.Select(g => g.Id));
        Assert.Equal(fresh.Select(g => g.Width).OrderDescending(), fresh.Select(g => g.Width));
        Assert.All(fresh, g => Assert.InRange(g.Image, 0, session.Art.MountainImageCount("NE", g.Width) - 1));
        for (int x = big.X; x < big.X + big.Width; x++)
            for (int y = big.Y; y < big.Y + big.Height; y++)
            {
                int covering = session.Looks.Mountains.Count(g => x >= g.X && x < g.X + g.Width && y >= g.Y && y < g.Y + g.Height);
                Assert.Equal(session.Map.Tiles[x, y].Ground == Ground.Mountain && session.Map.Tiles[x, y].Terrain != 6 ? 1 : 0, covering);
            }
    }

    /// <summary><c>L_BATTLE</c> останавливает проверку (<c>CEffectBattle.stopProcessing</c>), даже когда проверка конца боя внутри
    /// эффекта нашла своё событие (корпус 20260919-human, день 3, отряд Кланов против чужого): <c>EV0001</c> («отряд существует») срабатывает у победителя во вложенной проверке, а внешняя
    /// после боя дальше не идёт.</summary>
    [SkippableFact]
    public void Battle_StopsCheck_EvenAfterNestedFiring()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var mine = probe.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var enemy = probe.Map.Stacks.Values.First(s => !s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var third = probe.Map.Stacks.Values.First(s => s.Id != mine.Id && s.Id != enemy.Id);
        var battle = new ScenarioEvent("S999EV0000", true, false, 100, -1, All, [new FrequencyCondition(1)], [new BattleEffect(enemy.Id, true)], "бой", All);
        var after = new ScenarioEvent("S999EV0001", true, false, 100, -1, All, [new StackExistsCondition(third.Id, 0)], [], "после", All);
        var events = probe.State.Events with { Events = [battle, after] };
        var session = new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book);

        var fired = session.RunEvents(human, mine.Id);

        Assert.Equal("S999EV0000 " + human.ToUpperInvariant(), Assert.Single(fired).ToUpperInvariant());
        Assert.Contains(mine.UnitIds.Concat(enemy.UnitIds), u => session.Map.Units.GetValueOrDefault(u)?.Hp != probe.Map.Units[u].Hp);   // бой был
    }

    /// <summary>Бой эффекта со стороной человека в его действие ждёт экрана (<see cref="GameSession.PendingEventBattle"/>): карта
    /// не тронута, проверка остановлена; журнал экрана (здесь — автобой на том же состоянии и генераторе) закрывает бой через
    /// <see cref="GameSession.FinishBattle"/>. Наблюдатель хода, если он играет бой, — раньше ожидания.</summary>
    [SkippableFact]
    public void Battle_WithHuman_WaitsForScreen_OrObserver()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var mine = probe.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var enemy = probe.Map.Stacks.Values.First(s => !s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var battle = new ScenarioEvent("S999EV0000", true, false, 100, -1, All, [new FrequencyCondition(1)], [new BattleEffect(enemy.Id, true)], "бой", All);
        var events = probe.State.Events with { Events = [battle] };
        GameSession Fresh() => new(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book);

        var session = Fresh();
        session.EventBattlesOnScreen = true;
        Assert.Single(session.RunEvents(human, mine.Id));
        var pending = Assert.IsType<BattleState>(session.PendingEventBattle);
        Assert.Equal((enemy.Id, mine.Id), (pending.AttackerGroup, pending.DefenderGroup));
        Assert.All(mine.UnitIds, u => Assert.Equal(probe.Map.Units[u].Hp, session.Map.Units[u].Hp));
        var rules = session.Book!.Rules;
        var played = new D2.Data.Battle.BattleSession(pending, new BattleSim(pending, session.Rng, rules.Constants), rules.Constants) { Auto = true };
        played.Resolve();
        session.FinishBattle(played.Log);
        Assert.Null(session.PendingEventBattle);
        Assert.Contains(mine.UnitIds.Concat(enemy.UnitIds), u => session.Map.Units.GetValueOrDefault(u)?.Hp != probe.Map.Units[u].Hp);

        var observed = Fresh();
        var observer = new PlayingObserver(observed);
        observed.Observer = observer;
        observed.EventBattlesOnScreen = true;
        observed.RunEvents(human, mine.Id);
        Assert.Equal(1, observer.Battles);
        Assert.Null(observed.PendingEventBattle);
    }

    /// <summary>Два <c>L_BATTLE</c> в одном действии человека: первый ждёт экрана, второй — в очереди и начинается, когда первый
    /// закрыт (<see cref="GameSession.NextEventBattle"/>), если триггерер жив; до того «бой ждёт» — чтобы вход и атака не шли.</summary>
    [SkippableFact]
    public void TwoBattles_SecondQueuedUntilFirstClosed()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var mine = probe.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var enemies = probe.Map.Stacks.Values.Where(s => !s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0)
                                            .Take(2).ToList();
        var two = new ScenarioEvent("S999EV0000", true, false, 100, -1, All, [new FrequencyCondition(1)],
                                    [new BattleEffect(enemies[0].Id, true), new BattleEffect(enemies[1].Id, true)], "два боя", All);
        var events = probe.State.Events with { Events = [two] };
        var session = new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book)
        {
            EventBattlesOnScreen = true,
        };
        session.RunEvents(human, mine.Id);

        var first = Assert.IsType<BattleState>(session.NextEventBattle());
        Assert.Equal(enemies[0].Id, first.AttackerGroup);
        Assert.Same(first, session.NextEventBattle());
        var rules = session.Book!.Rules;
        var played = new D2.Data.Battle.BattleSession(first, new BattleSim(first, session.Rng, rules.Constants), rules.Constants) { Auto = true };
        played.Resolve();
        session.FinishBattle(played.Log);
        Assert.True(session.EventBattleWaiting == session.Map.Stacks.ContainsKey(mine.Id));
        if (session.Map.Stacks.ContainsKey(mine.Id) && session.Map.Stacks.ContainsKey(enemies[1].Id))
            Assert.Equal((enemies[1].Id, mine.Id), (session.NextEventBattle()!.AttackerGroup, session.PendingEventBattle!.DefenderGroup));
    }

    /// <summary>Бой очереди без стороны человека экрану не отдаётся: пока ждёт бой человека, событие с другим триггерером (ИИ)
    /// начинает бой ИИ с ИИ — он в очереди, а когда экран закрыл первый, играется автобоем, и ждать больше нечего.</summary>
    [SkippableFact]
    public void QueuedBattle_WithoutHuman_PlaysAuto()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        bool Mine(MapStack s) => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase);
        var mine = probe.Map.Stacks.Values.First(s => Mine(s) && s.Inside.Length == 0);
        var others = probe.Map.Stacks.Values.Where(s => !Mine(s) && s.Inside.Length == 0).ToList();
        var first = others[0];
        var ai = others.First(s => s.Id != first.Id);
        var attacker = others.First(s => s.Id != first.Id && s.Id != ai.Id && !s.Owner.Equals(ai.Owner, StringComparison.OrdinalIgnoreCase));
        bool[] noDwarf = [true, false, true, true, true, true];   // флаг 1 — Кланы (RaceFlag): человек не кандидат
        var humanBattle = new ScenarioEvent("S999EV0000", true, false, 100, -1, All, [new FrequencyCondition(1)], [new BattleEffect(first.Id, true)], "человек", All);
        var aiBattle = new ScenarioEvent("S999EV0001", true, true, 100, -1, All, [new StackExistsCondition(ai.Id, 0)],
                                         [new BattleEffect(attacker.Id, true)], "ИИ", noDwarf);
        var events = probe.State.Events with { Events = [humanBattle, aiBattle] };
        var session = new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book)
        {
            EventBattlesOnScreen = true,
        };
        session.RunEvents(human, mine.Id);
        var pending = Assert.IsType<BattleState>(session.PendingEventBattle);
        Assert.Contains(session.RunEvents("", ai.Id), f => f.StartsWith("S999EV0001", StringComparison.Ordinal));
        Assert.True(session.EventBattleWaiting);

        var rules = session.Book!.Rules;
        var played = new D2.Data.Battle.BattleSession(pending, new BattleSim(pending, session.Rng, rules.Constants), rules.Constants) { Auto = true };
        played.Resolve();
        session.FinishBattle(played.Log);
        Assert.Null(session.NextEventBattle());
        Assert.False(session.EventBattleWaiting);
        int wins(string id, MapState map) => map.Stacks.GetValueOrDefault(id)?.BattlesWon ?? 0;
        Assert.Equal(1, wins(ai.Id, session.Map) + wins(attacker.Id, session.Map) - ai.BattlesWon - attacker.BattlesWon);   // бой ИИ был
    }

    /// <summary>Новая партия для сцены (<see cref="NewGame.Begin"/>): то же интро дня 1, что у <see cref="NewGame.Start(ScenarioData, D2.Data.Menu.NewGameSetup, D2.Data.Game.GameCatalog, GameBook?, Random?)"/>,
    /// но партия живая и с боем эффекта «экраном» уже в интро.</summary>
    [SkippableFact]
    public void NewGameBegin_SameIntro_BattlesOnScreen()
    {
        string path = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "Power to Corrupt");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var data = ScenarioFile.Read(path);
        var content = Shared.Value.Content;
        var setup = new D2.Data.Menu.NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == "Power to Corrupt"), Race = 3, LordCategory = 1, Difficulty = 1,
        };
        var state = NewGame.Start(data, setup, content.Catalog, Shared.Value.Book, new Random(1));
        var session = NewGame.Begin(data, setup, content.Catalog, Shared.Value.Book, content.MapArt, new Random(1));
        Assert.True(session.EventBattlesOnScreen);
        Assert.Equal(state.Messages.Select(m => m.EventId), session.State.Messages.Select(m => m.EventId));
        Assert.Equal(state.EventProgress.Log.Count, session.State.EventProgress.Log.Count);
        Assert.Equal(state.Looks.Map.Players["S073PL000a"].Bank, session.Map.Players["S073PL000a"].Bank);
    }

    /// <summary>Наблюдатель, который сам играет бой автобоем.</summary>
    private sealed class PlayingObserver(GameSession session) : ITurnObserver
    {
        public int Battles { get; private set; }

        public void PlayerTurn(string playerId) { }

        public void StackMoving(StackMove move) { }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state)
        {
            Battles++;
            var rules = session.Book!.Rules;
            var battle = new D2.Data.Battle.BattleSession(state, new BattleSim(state, session.Rng, rules.Constants), rules.Constants) { Auto = true };
            battle.Resolve();
            return battle.Log;
        }
    }

    /// <summary>Партия из сохранения корпуса только с событиями <paramref name="list"/>.</summary>
    private static GameSession SaveWith(GameSession probe, params ScenarioEvent[] list)
    {
        var events = probe.State.Events with { Events = list };
        return new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book);
    }

    private static ScenarioEvent Event(string id, EventCondition[] conditions, params EventEffect[] effects) =>
        new(id, true, false, 100, -1, All, conditions, effects, id, All);

    /// <summary><c>L_MOVE_BESIDE</c> идёт к клетке триггерера на момент срабатывания (<c>0x43d21b</c>): <c>L_MOVE_STACK</c> раньше по
    /// списку уводит триггерера в область, а отряд S встаёт рядом с прежней клеткой; сам в неё не входит.</summary>
    [SkippableFact]
    public void MoveBeside_GoesToTriggererCellAtFiring()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var mine = probe.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var far = probe.State.Events.Zones.Values.First(z => Math.Max(Math.Abs(z.X - mine.X), Math.Abs(z.Y - mine.Y)) > 10);
        var mover = probe.Map.Stacks.Values.Where(s => s.Inside.Length == 0 && !s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase))
                                           .OrderBy(s => Math.Max(Math.Abs(s.X - mine.X), Math.Abs(s.Y - mine.Y))).First();
        var session = SaveWith(probe, Event("S999EV0000", [new FrequencyCondition(1)],
                                            new MoveStackEffect("", far.Id, true), new MoveBesideEffect(mover.Id)));

        session.RunEvents(human, mine.Id);

        var moved = session.Map.Stacks[mover.Id];
        Assert.Equal(1, Math.Max(Math.Abs(moved.X - mine.X), Math.Abs(moved.Y - mine.Y)));
        Assert.NotEqual((mine.X, mine.Y), (session.Map.Stacks[mine.Id].X, session.Map.Stacks[mine.Id].Y));
    }

    /// <summary><c>L_POPUP</c> с <c>BOOLVALUE</c>: в сообщении — лидер отряда-триггерера (имя, тип); окно ставит имя перед текстом и
    /// его портрет на сторону <c>LEFT_SIDE</c>; без флага — картинка записи. Сохранение (v7) хранит лидера.</summary>
    [SkippableFact]
    public void Popup_BoolValue_ShowsTriggererLeader()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var mine = probe.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var session = SaveWith(probe, Event("S999EV0000", [new FrequencyCondition(1)],
                                            new PopupEffect("текст", Image: "G000UU0087", LeftSide: true, ShowTo: "TRI", Flag: true),
                                            new PopupEffect("второй", Image: "G000UU0087", ShowTo: "TRI")));
        session.RunEvents(human, mine.Id);
        var messages = session.State.Messages.Where(m => m.EventId == "S999EV0000").ToList();
        Assert.Equal(2, messages.Count);
        string type = session.Map.Units[mine.LeaderId].TypeId.ToUpperInvariant();
        Assert.Equal(type, messages[0].LeaderType);
        Assert.Equal("", messages[1].LeaderType);
        Assert.Equal(messages[0], GameSave.FromJson(GameSave.ToJson(session.State)).Messages.First(m => m.EventId == "S999EV0000"));

        var screen = Shared.Value.Content.Screens.Single(s => s.Id == "DLG_EVENT_POPUP");
        var ui = new D2.Data.Ui.UiScreenState(screen);
        new EventPopupLogic(messages[0], t => "Тип " + t).Open(ui);
        // Герой Кланов — собственное имя как есть (0x5e97dc), даже пустое: тогда перед текстом пустая строка.
        Assert.True(messages[0].OwnName);
        Assert.Equal($"{messages[0].LeaderName}\nтекст", ui.TextOf("TXT_RIGHTSIDE"));
        Assert.Equal(EventPopupLogic.GfxFrame("Events", type), ui.Frame("IMG_LEFTSIDE"));

        // Не герой во главе (воин, вор, призванный) — имя типа.
        var plain = new EventMessage(human, 1, "S999EV0000", new PopupEffect("текст", Flag: true), "", type);
        new EventPopupLogic(plain, t => "Тип " + t).Open(ui);
        Assert.Equal($"Тип {type}\nтекст", ui.TextOf("TXT_RIGHTSIDE"));
        // Справа — отражённая и по центру места (0x4bd55b, флаги 9 setImage; docs/ui-windows.md §13).
        Assert.Equal(UiFrameRef.Mirrored(EventPopupLogic.GfxFrame("Events", type)), ui.Frame("IMG_RIGHTSIDE"));
        Assert.Equal(UiImageAlign.Center, ui.ImageAlign("IMG_RIGHTSIDE"));
    }

    /// <summary><c>L_GIVE_ITEM</c> с <c>GIVETO</c> 1, а у кандидата нет столицы (нейтрал) — предмет остаётся триггереру
    /// (<c>0x43ce32</c>).</summary>
    [SkippableFact]
    public void GiveItem_ToCapital_WithoutCapital_GoesToTriggerer()
    {
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var neutral = probe.Map.Stacks.Values.First(s => s.Inside.Length == 0
            && !probe.Map.Sites.Any(c => c.Kind == SiteKind.Capital && c.Owner.Equals(s.Owner, StringComparison.OrdinalIgnoreCase)));
        var session = SaveWith(probe, Event("S999EV0000", [new StackExistsCondition(neutral.Id, 0)], new GiveItemEffect(1, "G000IG0001")));

        session.RunEvents("", neutral.Id);

        Assert.Contains(session.Map.Stacks[neutral.Id].Items, i => session.Map.ItemTypes[i] == "G000IG0001");
    }

    /// <summary>«Отряд в ходу» (<c>playingStack</c>, <c>StackMove</c>): эффект пути убил отряд — его проверка идёт без триггерера,
    /// и <c>L_GIVE_ITEM</c> кладёт предмет ходившему отряду — от каждого кандидата; проверка самого отряда — первой, у него один.</summary>
    [SkippableFact]
    public void PlayingStack_GetsItem_FromKilledStackCheck()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var mine = probe.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var victim = probe.Map.Stacks.Values.First(s => !s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var kill = Event("S999EV0000", [], new RemoveStackEffect(victim.Id, false));
        var session = SaveWith(probe, Event("S999EV0001", [new StackExistsCondition(victim.Id, 1)], new GiveItemEffect(0, "G000IG0001")));

        session.MoveStack(mine.Id, mine.X, mine.Y, 0, 0, [new EventFiring(kill, human, mine.Id)]);

        Assert.True(session.Map.Stacks[mine.Id].Items.Count(i => session.Map.ItemTypes[i] == "G000IG0001") > 1);
    }

    /// <summary><c>L_CHANGE_ORDER</c> отряду подрасы <c>L_NEUTRAL</c>: «защита отряда» → «стоять» без цели (<c>0x436576</c>);
    /// приказ, не прошедший проверку <c>0x60ed92</c> (атака самого себя), не ставится — прежний.</summary>
    [SkippableFact]
    public void ChangeOrder_NeutralSubrace_AndRefused()
    {
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var neutral = probe.Map.Stacks.Values.First(s => probe.Map.Subraces.GetValueOrDefault(s.Subrace) == 5);
        var other = probe.Map.Stacks.Values.First(s => s.Id != neutral.Id && s.Subrace == neutral.Subrace);
        var session = SaveWith(probe, Event("S999EV0000", [new StackExistsCondition(neutral.Id, 0)],
                                            new ChangeOrderEffect(neutral.Id, other.Id, false, (int)StackOrder.DefendStack)));
        session.RunEvents("", neutral.Id);
        Assert.Equal(((int)StackOrder.Stand, ""), (session.Map.Stacks[neutral.Id].Order, session.Map.Stacks[neutral.Id].OrderTarget));

        var refused = SaveWith(probe, Event("S999EV0000", [new StackExistsCondition(neutral.Id, 0)],
                                            new ChangeOrderEffect(neutral.Id, neutral.Id, false, (int)StackOrder.AttackStack)));
        refused.RunEvents("", neutral.Id);
        Assert.Equal((neutral.Order, neutral.OrderTarget), (refused.Map.Stacks[neutral.Id].Order, refused.Map.Stacks[neutral.Id].OrderTarget));
    }

    /// <summary>Эффекты упреждающей проверки пути (<c>MoveStack(…, pathEvents)</c>): убитый ими отряд — своя проверка без
    /// триггерера (у всех кандидатов) до проверки после хода (только хозяин отряда). <c>EV0001</c> «отряда нет» прибавляет 1 к
    /// переменной у каждого кандидата: больше одного — проверка очереди T была.</summary>
    [SkippableFact]
    public void PathEvents_KilledStack_ChecksBeforeMoveCheck()
    {
        const string human = "S073PL000a";
        var probe = FromSave("20260919-human", "rastlennaya-vlast-dwarf-t03-009.sg");
        var mine = probe.Map.Stacks.Values.First(s => s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var victim = probe.Map.Stacks.Values.First(s => !s.Owner.Equals(human, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0);
        var kill = new ScenarioEvent("S999EV0000", true, false, 100, -1, All, [], [new RemoveStackEffect(victim.Id, false)], "путь", All);
        var gone = new ScenarioEvent("S999EV0001", true, false, 100, -1, All, [new StackExistsCondition(victim.Id, 1)],
                                     [new ModifyVariableEffect(9001, 0, 1)], "отряда нет", All);
        var events = probe.State.Events with { Events = [gone] };
        var session = new GameSession(probe.State with { Events = events, EventProgress = EventProgress.From(events) }, probe.Art, probe.Book);

        session.MoveStack(mine.Id, mine.X, mine.Y, 0, 0, [new EventFiring(kill, human, mine.Id)]);

        Assert.False(session.Map.Stacks.ContainsKey(victim.Id));
        Assert.True(session.State.EventProgress.Variables.GetValueOrDefault(9001) > 1, "проверка убитого отряда была");
    }

    private static IEnumerable<(int X, int Y)> Around(MapState map, EventZone z)
    {
        for (int x = z.X - z.Radius; x <= z.X + z.Radius; x++)
            for (int y = z.Y - z.Radius; y <= z.Y + z.Radius; y++)
                if (map.Contains(x, y)) yield return (x, y);
    }
}
