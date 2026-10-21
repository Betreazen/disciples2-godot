using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>
/// Оперативный слой ИИ на сценариях уровня 10 и выше (<c>docs/ai-spells.md</c> §2): заклинания на всю карту — вторая часть лечения
/// (<c>0x46d97a</c>) — и «натиск» на людей (<c>0x46fe35</c>). В корпусе сценарии уровня 1, поэтому сверка — юнит-тестами по листингу
/// поверх сохранения human дня 62 (уровень сценария и казна подставлены).
/// </summary>
[Trait("Category", "Corpus")]
public class AiSpellsHighLevelTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Human = "S073PL000a";
    private const string HealAll = "G000SS0084", BoostAll = "G000SS0088";
    /// <summary>Ослабления — не брони: у цели (отряд человека в сохранении) брони нет, и «Адские муки» (<c>G000SS0045</c>, броня −50) движок
    /// по ней не бросает (<c>0x5db12a</c>, отказ 5) — проверка набега ей не про то.</summary>
    private const string Lower1 = "G000SS0050", Lower2 = "G000SS0049", Attack1 = "G000SS0044";

    private static GameSession Load(int level)
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t62-102.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var looks = MapLooksLoader.Load(sg);
        return new GameSession(new GameState(new GameInfo("", "", [Human], 1, level), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }

    /// <summary>Игрок ИИ знает только <paramref name="spells"/>, казна <paramref name="bank"/>, его туман снят.</summary>
    private static GameSession Prepare(GameSession game, string ai, Bank bank, params string[] spells)
    {
        var map = game.Map;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[ai] = players[ai] with { KnownSpells = [.. spells], Bank = bank, SpellBank = new Bank(0, 0, 0, 0, 0, 0) };
        var fog = map.Fog.ToDictionary(p => p.Key, p => p.Value);
        fog[players[ai].FogId] = new bool[map.Size, map.Size];
        return new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(map, players: players, fog: fog), game.Looks.Stacks) },
                               game.Art, game.Book);
    }

    private static readonly Bank Rich = new(9999, 9999, 9999, 9999, 9999, 9999);

    private static IEnumerable<string> AiPlayers(GameSession game) =>
        game.Map.Players.Values.Where(p => !p.IsHuman && !AiTactics.NeutralOwner(game, p.Id)).Select(p => p.Id).Order(StringComparer.Ordinal);

    private static List<string> Casts(List<string> report) =>
        [.. report.Where(r => r.StartsWith("заклинание ", StringComparison.Ordinal) && !r.Contains("отказ", StringComparison.Ordinal))
                  .Select(r => r.ToUpperInvariant())];

    private static string Cast(string ai, string spell, (int X, int Y) at) => $"заклинание {ai} {spell} ({at.X},{at.Y})".ToUpperInvariant();

    // ------------------------------------------------------------------ вся карта (0x46d97a)

    /// <summary>Годные заклинания на всю карту (известные по порядку: усиление, лечение) перемешиваются — один бросок
    /// <c>rng(1)</c> = 0 меняет их местами, — бросается первое: лечение всех своих, раненый юнит здоров.</summary>
    [SkippableFact]
    public void WholeMap_HighLevel_ShufflesAndCastsFirst()
    {
        var game = Load(level: 10);
        string ai = AiPlayers(game).First(p => game.Map.Stacks.Values.Count(s => Same(s.Owner, p) && s.Inside.Length == 0) >= 2);
        game = Prepare(game, ai, Rich, BoostAll, HealAll);
        var hurt = game.Map.Stacks.Values.First(s => Same(s.Owner, ai) && s.Inside.Length == 0);
        string unit = hurt.LeaderId;
        var units = game.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[unit] = units[unit] with { Hp = 1 };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, units: units), game.Looks.Stacks) },
                               game.Art, game.Book);
        var rng = new ScriptedRng([0]);
        var report = new List<string>();

        AiSpells.Heal(game, ai, new AiForces([], []), rng, report);

        Assert.Equal(1, rng.Used);
        Assert.Equal([Cast(ai, HealAll, (0, 0))], Casts(report));
        Assert.True(game.Map.Units[unit].Hp > 1);
    }

    /// <summary>Мана жизни ровно 1000 — «не больше 1000» (<c>cmp ax, 0x3e8; jle</c>): заклинаний на всю карту нет, бросков нет.</summary>
    [SkippableFact]
    public void WholeMap_ManaNotAbove1000_Nothing()
    {
        var game = Load(level: 10);
        string ai = AiPlayers(game).First();
        game = Prepare(game, ai, Rich with { Life = 1000 }, BoostAll, HealAll);
        var rng = new ScriptedRng([]);
        var report = new List<string>();

        AiSpells.Heal(game, ai, new AiForces([], []), rng, report);

        Assert.Equal(0, rng.Used);
        Assert.Empty(Casts(report));
    }

    /// <summary>Уровень сценария ниже 10 (<c>0x61eb31</c>) — вторая часть лечения не идёт.</summary>
    [SkippableFact]
    public void WholeMap_LowLevel_Nothing()
    {
        var game = Load(level: 9);
        string ai = AiPlayers(game).First();
        game = Prepare(game, ai, Rich, BoostAll, HealAll);
        var rng = new ScriptedRng([]);
        var report = new List<string>();

        AiSpells.Heal(game, ai, new AiForces([], []), rng, report);

        Assert.Empty(Casts(report));
    }

    // ------------------------------------------------------------------ «натиск» (0x46fe35)

    /// <summary>Пара «игрок ИИ — отряд человека вне крепости, не союзник»: <paramref name="near"/> — свой отряд ИИ не дальше 10.</summary>
    private static (string Ai, MapStack Target)? Pair(GameSession game, bool near)
    {
        var map = game.Map;
        foreach (string ai in AiPlayers(game))
            foreach (var target in map.Stacks.Values.Where(s => Same(s.Owner, Human) && s.Inside.Length == 0).OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                if (AiTactics.Allied(game, ai, Human)) continue;
                var at = AiTactics.Position(map, target);
                bool isNear = map.Stacks.Values.Any(s => Same(s.Owner, ai) && Cheb(AiTactics.Position(map, s), at) <= AiSpellRules.HarassNear);
                if (isNear == near) return (ai, target);
            }
        return null;
    }

    /// <summary>Далёкая цель: первый проход (<c>rng(100)</c> = 0 ≤ 49) — одно ослабление в клетку цели; второй (99) не идёт.</summary>
    [SkippableFact]
    public void Harass_FarTarget_OneLower()
    {
        var game = Load(level: 10);
        var found = Pair(game, near: false);
        Skip.If(found is null, "нет далёкой цели");
        var pair = found!.Value;
        game = Prepare(game, pair.Ai, Rich, Lower1, Attack1);
        var at = AiTactics.Position(game.Map, pair.Target);
        var rng = new ScriptedRng([0, 99]);
        var report = new List<string>();

        AiSpells.Harass(game, pair.Ai, new AiForces([], [pair.Target.Id]), rng, report);

        Assert.Equal(2, rng.Used);
        Assert.Equal([Cast(pair.Ai, Lower1, at)], Casts(report));
    }

    /// <summary>Близкая цель (свой отряд ИИ поставлен в 10 клетках от далёкой): первый проход не идёт (99), второй — да (0),
    /// число <c>rng(4) + 2</c> = 2; годные (ослабления и удар по порядку известных) перемешиваются двумя бросками 0 —
    /// [удар, ослабление 1, ослабление 2], брошены первые два.</summary>
    [SkippableFact]
    public void Harass_NearTarget_CastsCountAfterShuffle()
    {
        var game = Load(level: 10);
        var found = Pair(game, near: false);
        Skip.If(found is null, "нет цели");
        var pair = found!.Value;
        var at = AiTactics.Position(game.Map, pair.Target);
        var own = game.Map.Stacks.Values.First(s => Same(s.Owner, pair.Ai) && s.Inside.Length == 0);
        var stacks = game.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[own.Id] = own with { X = Math.Clamp(at.X + AiSpellRules.HarassNear, 0, game.Map.Size - 1), Y = at.Y };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, stacks: stacks), game.Looks.Stacks) },
                               game.Art, game.Book);
        Assert.Equal(pair, Pair(game, near: true));
        game = Prepare(game, pair.Ai, Rich, Lower1, Lower2, Attack1);
        var rng = new ScriptedRng([99, 0, 0, 0, 0]);
        var report = new List<string>();

        AiSpells.Harass(game, pair.Ai, new AiForces([], [pair.Target.Id]), rng, report);

        Assert.Equal(5, rng.Used);
        Assert.Equal([Cast(pair.Ai, Attack1, at), Cast(pair.Ai, Lower1, at)], Casts(report));
    }

    /// <summary>Оба броска прохода больше 49 — литья нет, бросков ровно два; ниже уровня 10 бросков нет вовсе.</summary>
    [SkippableFact]
    public void Harass_RollsAndLevelGate()
    {
        var game = Load(level: 10);
        string ai = AiPlayers(game).First();
        var target = game.Map.Stacks.Values.First(s => Same(s.Owner, Human));
        game = Prepare(game, ai, Rich, Lower1);
        var rng = new ScriptedRng([50, 50]);
        var report = new List<string>();
        AiSpells.Harass(game, ai, new AiForces([], [target.Id]), rng, report);
        Assert.Equal(2, rng.Used);
        Assert.Empty(Casts(report));

        var low = Prepare(Load(level: 9), ai, Rich, Lower1);
        var none = new ScriptedRng([]);
        AiSpells.Harass(low, ai, new AiForces([], [target.Id]), none, report);
        Assert.Equal(0, none.Used);
    }

    private static int Cheb((int X, int Y) a, (int X, int Y) b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
