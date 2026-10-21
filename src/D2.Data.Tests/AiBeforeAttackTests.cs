using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>
/// Подготовка ИИ к атаке (<c>docs/ai-spells.md</c> §5) на сохранении живой партии: журнал <c>20260919-human</c>, день 44 —
/// PL000c (отряд <c>S073KC0003</c> в своей крепости) бьёт отряд человека <c>S073KC0075</c> на (80,74); движок бросил перед
/// атакой удар со вторым кошельком.
/// </summary>
[Trait("Category", "Corpus")]
public class AiBeforeAttackTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private const string Player = "S073PL000c";
    private const string Attacker = "S073KC0003";
    private const string Target = "S073KC0075";

    /// <summary>Сила ниже 0,6 при взятом пороге (у человека порог 0,5) — только удар; ярус цели 4 даёт уровни 3…5, таких
    /// ударов у игрока нет — запасной заход 1…2; из годных — старшие (уровень 2), второй бросок выбирает среди них. Точка
    /// литья — позиция цели, второй кошелёк — как у действия. Два броска.</summary>
    [SkippableFact]
    public void Spell_WeakAgainstHuman_StrikeOnly_FallbackToHighestLevel()
    {
        var (game, book) = Load();
        var attacker = game.Map.Stacks[Attacker];
        var target = game.Map.Stacks[Target];
        var rng = new ScriptedRng([0, 0]);

        var prep = AiSpells.BeforeAttack(game, attacker, Target, target.UnitIds, targetStackOutside: true, (80, 74), p: 0.55,
                                         passed: true, spellBank: true, rng);

        Assert.NotNull(prep);
        Assert.Equal(AiPrepKind.Spell, prep.Kind);
        var spell = book.Spells[prep.Id];
        Assert.Equal(SpellCategory.Attack, spell.Category);
        Assert.Equal(2, spell.Level);
        Assert.Equal((Target, 80, 74, true), (prep.CastTarget, prep.X, prep.Y, prep.SpellBank));
        Assert.Equal(2, rng.Used);
        Assert.Null(game.SpellRefusal(Player, prep.Id, 80, 74, useSpellBank: true));
    }

    /// <summary>Атака готова (флаг <c>+0x50</c>): порог силы взят, «напрямую» нет, но путь сегодня приводит отряд вплотную к
    /// цели — производное «заклинание» бросает его в точку литья, не сходя с места; статус 2, ключ памяти — (атакующий, в кого
    /// бросали, точка литья). Как в журнале: из крепости (81,81) до цели 7 клеток (путь 12 при запасе 21), удар брошен без хода.</summary>
    [SkippableFact]
    public void Execute_ReadyAttack_CastsSpell()
    {
        var (game, _) = Load();
        var prep = new AiPrep(AiPrepKind.Spell, "G000SS0067", Target, 80, 74, SpellBank: true);
        var option = new AiTacticOption(AiActionType.AttackStack, Attacker, Target, 80, 74, Prep: prep);
        game.BeginAiTurn(Player);   // сохранение снято до хода ИИ: запас хода — вчерашний остаток (5 из 21)
        var start = (game.Map.Stacks[Attacker].X, game.Map.Stacks[Attacker].Y);
        Assert.True(AiTactics.AttackReady(game, game.Map.Stacks[Attacker], option));
        int casts = game.Map.SpellCasts.Count;
        var hp = game.Map.Stacks[Target].UnitIds.Sum(u => game.Map.Units[u].Hp);

        var (status, key) = game.ExecuteTactic(option);

        Assert.Equal(2, status);
        Assert.Equal((Attacker, Target, 80, 74), (key.Stack, key.Target, key.X, key.Y));
        Assert.Equal(casts + 1, game.Map.SpellCasts.Count);
        Assert.True(game.Map.Stacks[Target].UnitIds.Sum(u => game.Map.Units[u].Hp) < hp);
        Assert.Equal(start, (game.Map.Stacks[Attacker].X, game.Map.Stacks[Attacker].Y));
    }

    /// <summary>Слабая атака «напрямую» (второй проход, цель в пределах обзора) — <c>StackEmptyMoveAllowance</c>: отряд стоит,
    /// очки хода 0, статус 1.</summary>
    [SkippableFact]
    public void Execute_DirectWeakAttack_WaitsInPlace()
    {
        var (game, _) = Load();
        var option = new AiTacticOption(AiActionType.AttackStack, Attacker, Target, 80, 74, Direct: true);
        var before = game.Map.Stacks[Attacker];
        Assert.True(before.Move > 0);

        var (status, key) = game.ExecuteTactic(option);

        var after = game.Map.Stacks[Attacker];
        Assert.Equal(AiTactic.StatusStuck, status);
        Assert.Equal(option, key);
        Assert.Equal((before.X, before.Y, 0), (after.X, after.Y, after.Move));
    }

    /// <summary>Цель далеко (атака не готова) — производное ведёт себя как атака: подход, без литья, ключ — атаки.</summary>
    [SkippableFact]
    public void Execute_FarTarget_ApproachesWithoutCast()
    {
        var (game, _) = Load();
        var attacker = game.Map.Stacks[Attacker];
        var self = AiTactics.Position(game.Map, attacker);
        var far = game.Map.Stacks.Values
            .Where(s => !s.Owner.Equals(Player, StringComparison.OrdinalIgnoreCase) && s.Inside.Length == 0)
            .First(s => Math.Max(Math.Abs(s.X - self.X), Math.Abs(s.Y - self.Y)) > 20);
        var option = new AiTacticOption(AiActionType.AttackStack, Attacker, far.Id, far.X, far.Y,
                                        Prep: new AiPrep(AiPrepKind.Spell, "G000SS0067", far.Id, far.X, far.Y, SpellBank: true));
        Assert.False(AiTactics.AttackReady(game, attacker, option));
        int casts = game.Map.SpellCasts.Count;

        var (_, key) = game.ExecuteTactic(option);

        Assert.Equal(casts, game.Map.SpellCasts.Count);
        Assert.Equal((Attacker, far.Id, far.X, far.Y), (key.Stack, key.Target, key.X, key.Y));
    }

    private static (GameSession Game, GameBook Book) Load()
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t44-077.sg");
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        return (new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks), content.MapArt, book), book);
    }
}
