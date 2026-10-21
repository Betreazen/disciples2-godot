using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>
/// Где невидимый отряд показывается снова (<see cref="GameSession.Unhide"/>, <c>CEffectShow</c> с 1): по листингу каждое из мест движка —
/// до самого действия (<c>docs/map-path.md</c> §9). Сохранения корпуса <c>20260919-human</c>; отряд прячется флагом облика
/// (<c>INVISIBLE</c>), как после литья. Обрыв пути событием — <c>PlayerPathEventsTests</c>, сверка с журналом — <c>RevealHarnessTests</c>.
/// </summary>
[Trait("Category", "Corpus")]
public class RevealRuleTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules"))));

    private static GameSession Game(string save)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", save);
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), Shared.Value.Content.MapArt, Shared.Value.Book);
    }

    /// <summary>Партия, где отряды <paramref name="ids"/> невидимы; <paramref name="map"/> — карта взамен текущей.</summary>
    private static GameSession Hidden(GameSession game, MapState? map, params string[] ids)
    {
        var looks = game.Looks with
        {
            Map = map ?? game.Map,
            Stacks = [.. game.Looks.Stacks.Select(s => ids.Contains(s.Id, StringComparer.OrdinalIgnoreCase) ? s with { Invisible = true } : s)],
        };
        var hidden = new GameSession(game.State with { Looks = looks }, game.Art, game.Book);
        Assert.All(ids, id => Assert.True(hidden.Invisible(id)));
        return hidden;
    }

    private static MapStack Outside(GameSession game, Func<MapStack, bool>? also = null) =>
        game.Map.Stacks.Values.OrderBy(s => s.Id, StringComparer.Ordinal)
            .First(s => s.Inside.Length == 0 && s.LeaderAlive && !AiTactics.NeutralOwner(game, s.Owner) && (also?.Invoke(s) ?? true));

    /// <summary>Нападение на отряд: показываются нападающий (<c>0x4406bf</c>) и цель (<c>0x43ff69</c>) — до <c>CEffectBattle</c>.</summary>
    [SkippableFact]
    public void Attack_RevealsAttackerAndTargetStack()
    {
        var game = Game("rastlennaya-vlast-dwarf-t05-011.sg");
        var attacker = Outside(game);
        var target = game.Map.Stacks.Values.OrderBy(s => s.Id, StringComparer.Ordinal)
            .First(s => s.Inside.Length == 0 && s.LeaderAlive && !Same(s.Owner, attacker.Owner) && !AiTactics.Allied(game, attacker.Owner, s.Owner));
        game = Hidden(game, null, attacker.Id, target.Id);

        game.StartBattle(attacker.Id, target.Id);

        Assert.False(game.Invisible(attacker.Id));
        Assert.False(game.Invisible(target.Id));
    }

    /// <summary>Вход: своя крепость (<c>0x44099e</c>; и когда в ней уже визитёр — показ идёт до проверки <c>0x4409b4</c>), место
    /// (<c>0x440b59</c>), мешок (<c>0x440c0b</c>), неохраняемая руина (<c>0x440a53</c>).</summary>
    [SkippableFact]
    public void Enter_FortSiteBagRuin_Reveal()
    {
        var game = Game("rastlennaya-vlast-dwarf-t24-045.sg");
        var map = game.Map;
        var cases = new List<(string What, MapSite Site, Func<MapState, MapState> Setup, bool Refused)>();
        var own = map.Sites.FirstOrDefault(f => f.Kind is SiteKind.Village or SiteKind.Capital && !Fortifications.Rioting(f.RiotTurn)
                                                && !AiTactics.NeutralOwner(game, f.Owner)
                                                && map.Stacks.Values.Any(s => Same(s.Owner, f.Owner) && s.Inside.Length == 0 && s.LeaderAlive));
        if (own is not null)
        {
            MapState Visitor(MapState m, string id) => GameSession.With(m, sites: [.. m.Sites.Select(f => f.Id == own.Id ? f with { StackId = id } : f)]);
            cases.Add(("своя крепость", own, m => Visitor(m, ""), false));
            cases.Add(("своя крепость с визитёром", own, m => Visitor(m, "S073KCffff"), true));
        }
        if (map.Sites.FirstOrDefault(s => s.Kind is SiteKind.Merchant or SiteKind.Mage or SiteKind.Trainer or SiteKind.Mercenaries) is { } shop)
            cases.Add(("место", shop, m => m, false));
        if (map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Bag && !s.Gone) is { } bag) cases.Add(("мешок", bag, m => m, false));
        if (map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Ruin && s.LooterId.Length == 0) is { } ruin)
            cases.Add(("руина без охраны", ruin,
                       m => GameSession.With(m, sites: [.. m.Sites.Select(r => r.Id == ruin.Id ? r with { Guards = [], GuardSlots = [] } : r)]), false));
        Assert.Equal(5, cases.Count);
        foreach (var (what, site, setup, refused) in cases)
        {
            var stack = site.Kind is SiteKind.Village or SiteKind.Capital ? Outside(game, s => Same(s.Owner, site.Owner)) : Outside(game);
            var hidden = Hidden(game, setup(map), stack.Id);
            var report = hidden.Enter(stack.Id, site.Id);
            Assert.True((report.Refused is not null) == refused, $"{what}: {report.Refused}");
            Assert.False(hidden.Invisible(stack.Id), what);
        }
    }

    /// <summary>Жезл: постановка (<c>0x440e3b</c>, до <c>CEffectDropRod</c>) и снос (<c>0x440c9f</c>, до <c>CEffectBreakRod</c>).
    /// Отказ ничего не показывает: у движка до эффекта дело не доходит.</summary>
    [SkippableFact]
    public void Rods_PlaceAndBreak_Reveal()
    {
        var game = Game("rastlennaya-vlast-dwarf-t24-045.sg");
        var book = game.Book!;
        var bearer = Outside(game, s => game.LeaderAbilitiesOf(s).Contains(Rods.LeaderAbilityRod)
                                        && book.Economy.Races[game.Map.Players[s.Owner].RaceId].Playable);
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[bearer.Owner] = players[bearer.Owner] with { Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        var stacks = game.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[bearer.Id] = bearer with { Move = game.DayAllowance(bearer) };
        var rich = GameSession.With(game.Map, players: players, stacks: stacks);

        var placing = Hidden(game, rich, bearer.Id);
        Assert.NotNull(placing.PlaceRod(bearer.Id, -5, -5));
        Assert.True(placing.Invisible(bearer.Id));
        var cell = (from dx in new[] { 1, -1, 0 } from dy in new[] { 1, -1, 0 } where dx != 0 || dy != 0
                    select (X: bearer.X + dx, Y: bearer.Y + dy)).FirstOrDefault(p => Hidden(game, rich, bearer.Id).PlaceRod(bearer.Id, p.X, p.Y) is null);
        Skip.If(cell == default, "рядом с жезлоносцем нет клетки для жезла");
        Assert.Null(placing.PlaceRod(bearer.Id, cell.X, cell.Y));
        Assert.False(placing.Invisible(bearer.Id));

        string foe = game.Map.Players.Keys.First(p => !Same(p, bearer.Owner) && book.Economy.Races[game.Map.Players[p].RaceId].Playable
                                                      && !AiTactics.Allied(game, bearer.Owner, p));
        var rods = rich.Rods.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        rods["S073RDTEST"] = new MapRod("S073RDTEST", foe, bearer.X + 1, bearer.Y);
        var breaking = Hidden(game, GameSession.WithRods(rich, rods), bearer.Id);
        Assert.Null(breaking.BreakRod(bearer.Id, "S073RDTEST"));
        Assert.False(breaking.Invisible(bearer.Id));
    }

    /// <summary>Выброс мешка (<c>0x4415c8</c>, до <c>CEffectDropBag</c>).</summary>
    [SkippableFact]
    public void DropBag_Reveals()
    {
        var game = Game("rastlennaya-vlast-dwarf-t24-045.sg");
        var stack = Outside(game, s => s.Items.Count > 0);
        var cell = (from dx in new[] { 1, -1, 0 } from dy in new[] { 1, -1, 0 } where dx != 0 || dy != 0
                    select (X: stack.X + dx, Y: stack.Y + dy)).FirstOrDefault(p => game.DropCheck(stack.Id, p.X, p.Y, out _) is null);
        Skip.If(cell == default, "рядом нет клетки для мешка");
        var hidden = Hidden(game, null, stack.Id);
        Assert.Null(hidden.DropBag(stack.Id, cell.X, cell.Y, out _));
        Assert.False(hidden.Invisible(stack.Id));
    }

    /// <summary>Вор у цели: невидимый вор показывается до выбора (<c>0x4406bf</c> → <c>0x442de5</c>). Против невидимого отряда выбора нет
    /// (<c>0x4407c2</c>: флаг невидимости цели на момент команды — бой вместо окна вора): в порядке хода ИИ и окна человека
    /// (<see cref="GameSession.ThiefIncorruptible"/>, затем <see cref="GameSession.ThiefActions"/>) цель остаётся невидимой и действий нет;
    /// бой (<see cref="GameSession.StartBattle"/>) показывает обоих.</summary>
    [SkippableFact]
    public void Thief_AtTarget_Reveals_InvisibleTargetHasNoActions()
    {
        var game = Game("rastlennaya-vlast-dwarf-t05-011.sg");
        var map = game.Map;
        var thief = map.Stacks.Values.OrderBy(s => s.Id, StringComparer.Ordinal).First(s => map.Players.TryGetValue(s.Owner, out var p) && !p.IsHuman
                                                                    && !AiTactics.NeutralOwner(game, s.Owner) && s.Inside.Length == 0);
        var target = map.Stacks.Values.OrderBy(s => s.Id, StringComparer.Ordinal).First(s => !Same(s.Owner, thief.Owner)
                                                                     && !AiTactics.NeutralOwner(game, s.Owner)
                                                                     && !AiTactics.Allied(game, thief.Owner, s.Owner)
                                                                     && s.Inside.Length == 0 && s.LeaderAlive);
        Assert.NotEmpty(game.ThiefActions(thief.Id, target.Id));

        var visible = Hidden(game, null, thief.Id);
        Assert.False(visible.ThiefIncorruptible(thief.Id, target.Id));
        Assert.False(visible.Invisible(thief.Id));
        Assert.NotEmpty(visible.ThiefActions(thief.Id, target.Id));

        var hidden = Hidden(game, null, thief.Id, target.Id);
        Assert.False(hidden.ThiefIncorruptible(thief.Id, target.Id));
        Assert.False(hidden.Invisible(thief.Id));
        Assert.True(hidden.Invisible(target.Id));
        Assert.Empty(hidden.ThiefActions(thief.Id, target.Id));
        hidden.StartBattle(thief.Id, target.Id);
        Assert.False(hidden.Invisible(target.Id));

        var cancelled = Hidden(game, null, thief.Id);
        cancelled.ThiefCancel(thief.Id, empty: false);
        Assert.False(cancelled.Invisible(thief.Id));
    }

    /// <summary>Простой ход (<see cref="GameSession.MoveStack"/> без событий пути) отряд не показывает: в корпусе невидимый <c>KC009f</c>
    /// ходил 58 шагов и остался невидимым (<c>RevealHarnessTests</c>).</summary>
    [SkippableFact]
    public void PlainMove_KeepsInvisible()
    {
        var game = Game("rastlennaya-vlast-dwarf-t05-011.sg");
        var stack = Outside(game);
        var hidden = Hidden(game, null, stack.Id);
        hidden.MoveStack(stack.Id, stack.X, stack.Y, 0, 0);
        Assert.True(hidden.Invisible(stack.Id));
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
