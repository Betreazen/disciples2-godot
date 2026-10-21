using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Смена хозяина деревни и отряда — визиторы рядом со сменой (аудит 13.10, «Осталось» п. 6; <c>GameSession.Capture.cs</c>):
/// прежние хозяева деревни (<c>CVisitorChangePreviousOwner</c>, единственный вызов — <c>CEffectCapture</c> <c>0x434017</c>) и сброс чужих
/// приказов на сменивший хозяина объект (<c>CVisitorChangeStackTemplateOrder</c>/<c>CVisitorChangeStackOrder</c> из
/// <c>CVisitorChangeVillageOwner</c> <c>0x60fb7b</c> и <c>CVisitorChangeStackOwner</c> <c>0x60d3ea</c>).</summary>
[Trait("Category", "Corpus")]
public class VillageOwnerChangeTests
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

    private sealed record Setup(GameSession Session, MapStack Stack, MapSite Village, MapStack Guard, string TemplateId, int HumanRace, int OldRace,
                                Sights Sights);

    /// <summary>Корпус 20260919-human, день 5: деревня отдана ИИ-игроку (Легионы), без гарнизона; отряд того же ИИ и шаблон отряда с
    /// приказом «охранять город» на неё; человек (Кланы) заходит.</summary>
    private static Setup Game(IReadOnlyList<int>? previous = null)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t05-011.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        int Race(MapPlayer p) => book.Economy.Races[p.RaceId].RaceType;
        var map = looks.Map;
        var human = map.Players.Values.First(p => p.IsHuman);
        var ai = map.Players.Values.First(p => !p.IsHuman && book.Economy.Races[p.RaceId].Playable && p.CapturedBy.Length == 0);
        var stack = map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && s.Inside.Length == 0)
                                     .OrderByDescending(s => s.UnitIds.Count).First();
        var village = map.Sites.First(s => s.Kind == SiteKind.Village && s.Owner != human.Id && s.StackId.Length == 0);
        string aiSubrace = map.Stacks.Values.First(s => s.Owner == ai.Id).Subrace;
        village = village with { Owner = ai.Id, Subrace = aiSubrace, Guards = [], GuardSlots = [], PreviousOwners = previous };
        var guard = map.Stacks.Values.First(s => s.Owner == ai.Id && s.Inside.Length == 0 && s.Id != stack.Id);
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[guard.Id] = guard with { Order = (int)StackOrder.SecureCity, OrderTarget = village.Id };
        map = GameSession.With(map, stacks: stacks, sites: [.. map.Sites.Select(s => s.Id == village.Id ? village : s)]);
        var state = new GameState(GameInfo.FromMap("PtC", looks), looks with { Map = map });
        var template = new StackTemplate("S073ST9999", ai.Id, aiSubrace, "", 1, "", (int)StackOrder.SecureCity, village.Id, [], [], false, 0, [], 0);
        var templates = new Dictionary<string, StackTemplate>(state.Events.Templates, StringComparer.OrdinalIgnoreCase) { [template.Id] = template };
        state = state with { Events = state.Events with { Templates = templates } };
        var session = new GameSession(state, SharedContent.Load(Data).MapArt, book);
        var sights = new Sights();
        session.Watch(sights);
        return new Setup(session, stack, village, guard, template.Id, Race(human), Race(ai), sights);
    }

    /// <summary>Захват деревни ИИ человеком: раса прежнего хозяина записана в прежние (<c>P_O_*</c>), «охранять город» на неё у отряда и
    /// шаблона ИИ сброшен в <c>L_NORMAL</c> без цели, и <c>OCCUPY</c> разослан — Кланов среди прежних хозяев нет.</summary>
    [SkippableFact]
    public void Capture_RecordsPreviousOwner_DropsForeignSecureCity_Occupies()
    {
        var g = Game();

        var report = g.Session.Enter(g.Stack.Id, g.Village.Id);

        Assert.True(report.Captured, report.Refused);
        var after = g.Session.Map.Sites.First(s => s.Id == g.Village.Id);
        Assert.Equal([g.OldRace], after.PreviousOwners);
        Assert.Equal(((int)StackOrder.Normal, ""), (g.Session.Map.Stacks[g.Guard.Id].Order, g.Session.Map.Stacks[g.Guard.Id].OrderTarget));
        var template = g.Session.State.Events.Templates[g.TemplateId];
        Assert.Equal(((int)StackOrder.Normal, ""), (template.Order, template.OrderTarget));
        var capture = Assert.Single(g.Sights.All, s => s.Kind == TurnSightKind.Capture);
        Assert.True(capture.Occupy);
        Assert.Equal("OCCUPY", D2.Data.Sound.WindowSounds.OfSight(capture, g.Session.Book));

        // Сохранение хранит прежних хозяев.
        var back = GameSave.FromJson(GameSave.ToJson(g.Session.Snapshot()));
        Assert.Equal([g.OldRace], back.Looks.Map.Sites.First(s => s.Id == g.Village.Id).PreviousOwners);
    }

    /// <summary>Деревня уже бывала у расы захватчика (<c>0x434080</c>): <c>CCmdOccupyCityMsg</c> с флагом 1 не рассылается — звука
    /// <c>OCCUPY</c> нет; прежний хозяин всё равно записан.</summary>
    [SkippableFact]
    public void Capture_ByRaceThatOwnedIt_NoOccupy()
    {
        var probe = Game();
        var g = Game([probe.HumanRace]);

        Assert.True(g.Session.Enter(g.Stack.Id, g.Village.Id).Captured);

        var capture = Assert.Single(g.Sights.All, s => s.Kind == TurnSightKind.Capture);
        Assert.False(capture.Occupy);
        Assert.Null(D2.Data.Sound.WindowSounds.OfSight(capture, g.Session.Book));
        Assert.Equal([probe.HumanRace, g.OldRace], g.Session.Map.Sites.First(s => s.Id == g.Village.Id).PreviousOwners);
    }

    /// <summary><c>L_CHANGE_OWNER</c> (<c>CVisitorChangeStackOwner</c> → <c>0x60d3ea</c>): чужой отряд переходит к человеку — у отряда ИИ
    /// (другой подрасы, чем новый хозяин) приказ «защищать отряд» на него сброшен в <c>L_NORMAL</c> без цели; у отряда подрасы нового
    /// хозяина — остаётся.</summary>
    [SkippableFact]
    public void ChangeOwner_DropsForeignDefendStack()
    {
        var g = Game();
        var map = g.Session.Map;
        var ward = map.Stacks.Values.First(s => s.Owner != g.Stack.Owner && s.Id != g.Guard.Id && s.Inside.Length == 0);
        var friend = map.Stacks.Values.First(s => s.Id != g.Stack.Id && s.Id != g.Guard.Id && s.Id != ward.Id && s.Inside.Length == 0);
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[g.Guard.Id] = g.Guard with { Order = (int)StackOrder.DefendStack, OrderTarget = ward.Id };
        stacks[friend.Id] = friend with { Subrace = g.Stack.Subrace, Order = (int)StackOrder.DefendStack, OrderTarget = ward.Id };   // подраса нового хозяина
        var state = g.Session.State with { Looks = g.Session.Looks with { Map = GameSession.With(map, stacks: stacks) } };
        var subraces = new Dictionary<string, string>(state.Events.SubraceOwners, StringComparer.OrdinalIgnoreCase)
            { [g.Stack.Subrace] = g.Stack.Owner };
        var toHuman = new ScenarioEvent("T0EV0001", true, true, 100, 0, [true, true, true, true, true, true], [],
                                        [new ChangeOwnerEffect(ward.Id, g.Stack.Subrace, true, false)]);
        var events = new ScenarioEventSet([toHuman], state.Events.Zones, state.Events.Variables)
            { Templates = state.Events.Templates, SubraceOwners = subraces };
        var session = new GameSession(state with { Events = events, EventProgress = EventProgress.From(events) }, g.Session.Art, g.Session.Book);

        session.RunEvents(g.Stack.Owner, "");

        Assert.Equal(g.Stack.Owner, session.Map.Stacks[ward.Id].Owner);
        Assert.Equal(((int)StackOrder.Normal, ""), (session.Map.Stacks[g.Guard.Id].Order, session.Map.Stacks[g.Guard.Id].OrderTarget));
        Assert.Equal(((int)StackOrder.DefendStack, ward.Id), (session.Map.Stacks[friend.Id].Order, session.Map.Stacks[friend.Id].OrderTarget));
    }
}
