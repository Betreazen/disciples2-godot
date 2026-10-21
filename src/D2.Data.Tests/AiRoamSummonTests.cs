using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Построители без кандидатов в журналах (перенос по листингу 28.09, <c>docs/ai-tactic-candidates.md</c> §22):
/// <c>EnterCityRoam</c> (<c>0x455cdb</c>) и <c>SummonAttack</c> (<c>0x464dcb</c>). Карта — сохранение ai-q09 дня 1, отряд и деревня
/// подставлены.</summary>
[Trait("Category", "Corpus")]
public class AiRoamSummonTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static (MapLooks Looks, GameInfo Info)? Save()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260928-shots");
        if (!Directory.Exists(dir)) return null;
        string sg = Directory.GetFiles(dir, "ai-q09-ai-ai-diplomacy-human-t01-*.sg").Order(StringComparer.Ordinal).First();
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        return (looks, new GameInfo("q09", "q09", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1));
    }

    private static GameSession Session(MapLooks looks, GameInfo info, MapState map) =>
        new(new GameState(info, looks with { Map = map }), SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules")));

    /// <summary>Деревня своей подрасы, пустая: издалека — шаг ко входу со счётом 250 − d, вплотную — вход; отряд в крепости или деревня
    /// занята — кандидата нет.</summary>
    [SkippableFact]
    public void EnterCityRoam_EmptyOwnVillage_StepThenEnter()
    {
        var save = Save();
        Skip.If(save is null, "нет корпуса");
        var (looks, info) = save!.Value;
        var map = looks.Map;
        var village = map.Sites.First(f => f.Kind == SiteKind.Village);
        var gate = AiTactics.Entrance(village);
        var stack = map.Stacks.Values.First(s => s.Inside.Length == 0) with { Inside = "" };
        var own = village with { Subrace = stack.Subrace, StackId = "", RiotTurn = null };
        MapState Put(MapStack s, MapSite v) => GameSession.With(map,
            stacks: map.Stacks.Values.Select(x => x.Id == s.Id ? s : x).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase),
            sites: [.. map.Sites.Select(f => f.Id == v.Id ? v : f)]);
        IEnumerable<(AiTacticOption Action, double Score)> Build(MapState m, MapStack s) =>
            AiTactics.EnterCityRoam(Session(looks, info, m), s).Where(c => c.Item1.Target == village.Id);

        var far = stack with { X = gate.X + 3, Y = gate.Y };
        var step = Assert.Single(Build(Put(far, own), far));
        Assert.Equal((AiFortStep.Move, gate.X, gate.Y, 247.0), (step.Action.Fort, step.Action.X, step.Action.Y, step.Score));

        var near = stack with { X = gate.X + 1, Y = gate.Y };
        var enter = Assert.Single(Build(Put(near, own), near));
        Assert.Equal((AiFortStep.Enter, 249.0), (enter.Action.Fort, enter.Score));

        var inside = far with { Inside = village.Id };
        Assert.Empty(Build(Put(inside, own), inside));
        var occupied = own with { StackId = "S999KC0001" };
        Assert.Empty(Build(Put(far, occupied), far));
    }

    /// <summary>Нейтральная цель — <see cref="AiTacticEval.SummonNeutral"/> (1 + (8 − d)·80, дальше 7 — без счёта); не нейтральная своей
    /// подрасы отряда — пропускается.</summary>
    [SkippableFact]
    public void SummonAttack_NeutralScoredByDistance_OwnSubraceSkipped()
    {
        var save = Save();
        Skip.If(save is null, "нет корпуса");
        var (looks, info) = save!.Value;
        var map = looks.Map;
        var session0 = Session(looks, info, map);
        var neutral = map.Stacks.Values.First(s => AiTactics.NeutralOwner(session0, s.Owner) && s.Inside.Length == 0);
        var self = map.Stacks.Values.First(s => !AiTactics.NeutralOwner(session0, s.Owner) && s.Inside.Length == 0);
        MapState Put(params MapStack[] moved) => GameSession.With(map,
            stacks: map.Stacks.Values.Select(x => moved.FirstOrDefault(m => m.Id == x.Id) ?? x).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase));
        double? ScoreOn(MapState m, string target)
        {
            var session = Session(looks, info, m);
            var turn = AiTactics.Begin(session, self.Owner, AiTacticMemo.Empty)!;
            var hit = AiTactics.SummonAttack(session, turn, session.Map.Stacks[self.Id]).Where(c => c.Item1.Target == target).ToList();
            return hit.Count == 0 ? null : hit[0].Item2;
        }

        var at5 = neutral with { X = self.X + 5, Y = self.Y };
        Assert.Equal(1 + (3 * 80.0), ScoreOn(Put(at5), neutral.Id));
        var at9 = neutral with { X = self.X + 9, Y = self.Y };
        Assert.Equal(AiTactic.NoScore, ScoreOn(Put(at9), neutral.Id));

        var sameSubrace = map.Stacks.Values.FirstOrDefault(s => s.Id != self.Id && s.Subrace == self.Subrace);
        if (sameSubrace is not null) Assert.Null(ScoreOn(map, sameSubrace.Id));
    }

    /// <summary><c>MoveToStack</c> у движка мёртв (план операции всегда пуст) — у порта кандидата нет ни у одного отряда.</summary>
    [SkippableFact]
    public void MoveToStack_NeverBuilt()
    {
        var save = Save();
        Skip.If(save is null, "нет корпуса");
        var (looks, info) = save!.Value;
        var session = Session(looks, info, looks.Map);
        foreach (var player in session.Map.Players.Values.Where(p => !p.IsHuman))
        {
            if (AiTactics.Begin(session, player.Id, AiTacticMemo.Empty) is not { } turn) continue;
            foreach (var stack in session.Map.Stacks.Values.Where(s => s.Owner == player.Id))
                Assert.DoesNotContain(AiTactics.Candidates(session, turn, stack), c => c.Action.Type == AiActionType.MoveToStack);
        }
    }
}
