using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>«Поставить жезл» человеком (<see cref="DropRodLogic"/>, <c>0x48effd</c>, <c>CTaskDropRod</c>) на партии с жезлоносцем человека
/// (<c>20260919-f7/f7-m6-rods-human</c>): отказ кнопки у отряда без жезлоносца — <c>X005TA0506</c> с именами жезлоносцев расы; у жезлоносца —
/// режим; курсор по годности клетки; жезл рядом — окно <c>X005TA0356</c>; вопрос с ценой.</summary>
[Trait("Category", "Corpus")]
public class DropRodTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static WindowContext Context(string save)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-f7", save);
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("m6", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        string human = looks.Map.Players.Values.First(p => looks.Map.Stacks.Values.Any(s => s.Owner == p.Id
            && session.LeaderAbilitiesOf(s).Contains(Rods.LeaderAbilityRod) && !AiTactics.NeutralOwner(session, p.Id))).Id;
        return new WindowContext(session, human, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));
    }

    [SkippableFact]
    public void Button_Cursor_Messages_Question()
    {
        var c = Context("f7-m6-rods-human-t01-021.sg");
        var map = c.Map;
        var bearer = map.Stacks.Values.OrderBy(s => s.Id)
            .First(s => s.Owner == c.Viewer && s.Inside.Length == 0 && c.Session.LeaderAbilitiesOf(s).Contains(Rods.LeaderAbilityRod));
        Assert.Null(DropRodLogic.StartRefusal(c, bearer.Id));

        var near = (from dx in new[] { 0, 1, -1 } from dy in new[] { 0, 1, -1 } select (X: bearer.X + dx, Y: bearer.Y + dy)).ToList();
        var good = near.FirstOrDefault(p => DropRodLogic.Refusal(c, bearer.Id, p.X, p.Y) is null);
        output.WriteLine($"игрок {c.Viewer} жезлоносец {bearer.Id} ({bearer.X},{bearer.Y}), годная клетка {good}");
        Skip.If(good == default, "рядом с жезлоносцем нет годной клетки");
        Assert.Equal(DropRodLogic.CursorValid, DropRodLogic.Cursor(c, bearer.Id, good.X, good.Y));
        Assert.Equal(DropRodLogic.CursorInvalid, DropRodLogic.Cursor(c, bearer.Id, -1, -1));
        Assert.False(DropRodLogic.NeedsWalk(bearer, good.X, good.Y));
        Assert.Contains("150", DropRodLogic.Question(c));

        Assert.Null(c.Session.PlaceRod(bearer.Id, good.X, good.Y));
        Assert.Equal(DropRodLogic.RodNear, DropRodLogic.ClickMessage(c, bearer.Id, good.X + 1, good.Y));

        var other = map.Stacks.Values.OrderBy(s => s.Id)
            .FirstOrDefault(s => s.Owner == c.Viewer && !c.Session.LeaderAbilitiesOf(s).Contains(Rods.LeaderAbilityRod));
        if (other is not null)
        {
            string refusal = DropRodLogic.StartRefusal(c, other.Id)!;
            output.WriteLine(refusal);
            Assert.StartsWith("Чтобы устанавливать Жезлы", refusal);
            Assert.DoesNotContain("%LEADER%", refusal);
        }
    }

    /// <summary>Вопрос с ценой при втором щелчке (ответ пользователя 11.10): рядом — всегда; дальше — если сегодня доходим хотя бы до
    /// соседней с клеткой точки пути (жезл ставится с неё), а не только до самой клетки; не та клетка или не дойти и до соседней — нет.</summary>
    [Fact]
    public void AsksToday_WhenBesideReachable()
    {
        static PathPoint P(int x, int y, int day) => new(x, y, 0, day);
        var path = new[] { P(0, 0, 0), P(1, 0, 0), P(2, 0, 0), P(3, 0, 1) };
        Assert.True(DropRodLogic.AsksToday(null, 1, 1, near: true));
        Assert.True(DropRodLogic.AsksToday(new StackPath(path, 3), 3, 0, near: false));    // до самой клетки
        Assert.True(DropRodLogic.AsksToday(new StackPath(path, 2), 3, 0, near: false));    // только до соседней (2,0)
        Assert.False(DropRodLogic.AsksToday(new StackPath(path, 1), 3, 0, near: false));   // и до соседней не дойти
        Assert.False(DropRodLogic.AsksToday(new StackPath(path, 3), 2, 0, near: false));   // путь в другую клетку
        Assert.False(DropRodLogic.AsksToday(null, 3, 0, near: false));
    }

    /// <summary>На партии с жезлоносцем есть годные клетки, куда сегодня доходим только до соседней: прежде второй щелчок по ним вёл
    /// обычным ходом (условие «дойти до самой клетки»), теперь — вопрос с ценой (замечание пользователя 11.10).</summary>
    [SkippableFact]
    public void AsksToday_BesideOnlyCellsExistOnRodsMap()
    {
        var c = Context("f7-m6-rods-human-t01-021.sg");
        var bearer = c.Map.Stacks.Values.OrderBy(s => s.Id)
            .First(s => s.Owner == c.Viewer && s.Inside.Length == 0 && c.Session.LeaderAbilitiesOf(s).Contains(Rods.LeaderAbilityRod));
        int day = c.Session.DayAllowance(bearer);
        var beside = new List<(int X, int Y)>();
        for (int x = bearer.X - 12; x <= bearer.X + 12; x++)
            for (int y = bearer.Y - 12; y <= bearer.Y + 12; y++)
            {
                if (!DropRodLogic.NeedsWalk(bearer, x, y) || DropRodLogic.Refusal(c, bearer.Id, x, y) is not null) continue;
                var path = PathFinder.Find(c.Map, bearer, x, y, new MoveOptions(bearer.LeaderAlive), MovementRules.Vanilla, day);
                bool old = path.Points.Count > 1 && path.Points[^1].X == x && path.Points[^1].Y == y && path.Reachable >= path.Points.Count - 1;
                if (!old && DropRodLogic.AsksToday(path, x, y, near: false)) beside.Add((x, y));
            }
        output.WriteLine($"жезлоносец {bearer.Id} ({bearer.X},{bearer.Y}) ход {bearer.Move}/{day}: только до соседней — " +
                         string.Join(" ", beside.Take(12).Select(p => $"{p.X},{p.Y}")));
        Assert.NotEmpty(beside);
    }
    /// <summary>Снос чужого жезла отрядом — штраф отношений <c>D_ROD</c> игроку отряда с хозяином жезла (<c>CEffectBreakRod::apply</c>
    /// <c>0x433c65</c> → <c>0x5d9fab</c>: ход, штраф <c>0x5da006</c>, снос). Правило <see cref="Diplomacy.RodDestroyed"/> было, но сессия его не
    /// вызывала (аудит 13.10).</summary>
    [SkippableFact]
    public void BreakForeignRod_LowersRelations()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt, book);
        var map = session.Map;
        var races = Diplomacy.PlayerRaces(map, book.Economy);
        bool Playable(string player) => book.Economy.Races[map.Players[player].RaceId].Playable;
        int Race(string player) => book.Economy.Races[map.Players[player].RaceId].RaceType;
        var pair = (from s in map.Stacks.Values
                    where s.Inside.Length == 0 && Playable(s.Owner) && session.LeaderAbilitiesOf(s).Contains(Rods.LeaderAbilityRod)
                    from p in map.Players.Keys
                    where p != s.Owner && Playable(p) && !Diplomacy.AreAllies(map.Relations, Race(s.Owner), Race(p))
                    select (Breaker: s, Foe: p)).FirstOrDefault();
        Skip.If(pair.Breaker is null, "нет жезлоносца рядом с противником");
        var (breaker, foe) = pair;
        string viewer = breaker.Owner;
        var rods = map.Rods.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        rods["S000RDTEST"] = new MapRod("S000RDTEST", foe, breaker.X + 1, breaker.Y);
        // Отношения не на нуле — иначе штраф упрётся в нижний край и не будет виден.
        var warm = Diplomacy.Change(map.Relations, races, book.Diplomacy, Race(viewer), Race(foe), 50, false).Relations;
        map = GameSession.With(GameSession.WithRods(map, rods), relations: warm);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, book);
        int before = Diplomacy.Current(session.Map.Relations, Race(viewer), Race(foe));

        Assert.Null(session.BreakRod(breaker.Id, "S000RDTEST"));

        int after = Diplomacy.Current(session.Map.Relations, Race(viewer), Race(foe));
        output.WriteLine($"{viewer} → {foe}: {before} → {after} (D_ROD {book.Diplomacy.RodDestroyed})");
        Assert.Equal(Math.Clamp(before + book.Diplomacy.RodDestroyed, 0, Diplomacy.Max), after);
        Assert.NotEqual(before, after);
    }

    /// <summary>Снос чужого жезла человеком по журналу human (пометка 4 партии 15.10: щелчки жезлоносцем по соседнему чужому жезлу —
    /// без действия). Пары сохранений, между которыми у хозяина ИИ пропал жезл, а рядом стоял жезлоносец человека с полным ходом:
    /// у движка — щелчок, вопрос <c>X005TA0272</c>, «Да», <c>ap_restore</c> <c>rod_break</c> (весь ход) и <c>D_ROD</c>. Порт: жезл сносимый
    /// (<see cref="GameSession.BreakableRodAt"/>), путь к нему — одна клетка отряда, вопрос с первого щелчка
    /// (<see cref="GameSession.AsksRodBreak"/>), снос — ход 0 и жезла нет.</summary>
    [SkippableFact]
    public void HumanBreaksAdjacentForeignRod_ByJournal()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        // Пара дня 19 (снос S073RD0011 отрядом S073KC0075, seq 4173754…4173813); прочие пары партии таких сносов не дают — полный перебор
        // 122 сохранений находил только её (4 с), поэтому здесь только она.
        var saves = new[] { "rastlennaya-vlast-dwarf-t19-033.sg", "rastlennaya-vlast-dwarf-t19-034.sg" }.Select(f => Path.Combine(dir, f)).ToList();
        Skip.IfNot(saves.All(File.Exists), "нет сохранений дня 19");
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var art = SharedContent.Load(Data).MapArt;
        int found = 0;
        for (int i = 0; i + 1 < saves.Count; i++)
        {
            var before = MapLooksLoader.Load(saves[i]);
            var after = MapLooksLoader.Load(saves[i + 1]).Map;
            var map = before.Map;
            var human = map.Players.Values.FirstOrDefault(p => p.IsHuman);
            if (human is null) continue;
            foreach (var rod in map.Rods.Values.Where(r => r.Owner != human.Id && !after.Rods.ContainsKey(r.Id)))
            {
                var breaker = map.Stacks.Values.FirstOrDefault(s => s.Owner == human.Id && s.Inside.Length == 0
                    && Math.Max(Math.Abs(s.X - rod.X), Math.Abs(s.Y - rod.Y)) == 1
                    && after.Stacks.TryGetValue(s.Id, out var later) && later.Move == 0 && later.X == s.X && later.Y == s.Y && s.Move > 0);
                if (breaker is null) continue;
                var session = new GameSession(new GameState(GameInfo.FromMap("PtC", before), before), art, book);
                var path = PathFinder.Find(session.Map, breaker, rod.X, rod.Y, session.MoveOptionsOf(breaker), MovementRules.Vanilla,
                                           session.DayAllowance(breaker));
                output.WriteLine($"{Path.GetFileName(saves[i])}: {breaker.Id} ({breaker.X},{breaker.Y}) ход {breaker.Move} → жезл {rod.Id} {rod.Owner} " +
                                 $"({rod.X},{rod.Y}); путь {path.Points.Count} точ., достижимо {path.Reachable}");
                Assert.Equal(rod.Id, session.BreakableRodAt(breaker.Id, rod.X, rod.Y)?.Id);
                Assert.Single(path.Points);
                Assert.True(GameSession.AsksRodBreak(path));
                Assert.Null(session.BreakRod(breaker.Id, rod.Id));
                Assert.Equal(0, session.Map.Stacks[breaker.Id].Move);
                Assert.False(session.Map.Rods.ContainsKey(rod.Id));
                found++;
            }
        }
        output.WriteLine($"сносов рядом: {found}");
        Assert.True(found > 0, "нет сноса человеком соседнего жезла");
    }
}
