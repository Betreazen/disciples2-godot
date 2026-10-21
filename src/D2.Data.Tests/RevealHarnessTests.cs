using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Раскрытие невидимого отряда (<see cref="GameSession.Unhide"/>, <c>CEffectShow</c> с 1) по журналу и сохранениям: корпус
/// <c>20261014-replay</c> passive-002 — два литья Illudere terra (<c>g000ss0092</c>, вся карта) у <c>S143PL0002</c>. Окно — от сохранения
/// перед литьём до следующего: в порте литьё на сохранении перед ним (<see cref="GameSession.CastSpell"/>), затем бои отрядов заклинателя
/// из журнала (<see cref="GameSession.StartBattle"/>, здоровье — из <c>battle_end</c>, итог — <see cref="GameSession.CommitBattle"/>,
/// у руины — и добыча); сверка — флаг <c>INVISIBLE</c> каждого отряда заклинателя в следующем сохранении. Ходы порт не повторяет: простой
/// ход у движка отряд не раскрывает (в окнах ходили и невидимые — <c>KC009f</c> 58 шагов, помощник к своему <c>KC0001</c>).
/// </summary>
[Trait("Category", "Corpus")]
public class RevealHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private const string Corpus = "20261014-replay", Journal = "passive-002.jsonl", Illudere = "G000SS0092";

    /// <summary>
    /// Литьё прячет все отряды заклинателя, а видимыми к следующему сохранению становятся ровно напавшие: окно 1 (сохранение
    /// <c>t01-146</c> → <c>t02-147</c>) — <c>KC0001</c>, <c>KC008b</c>, <c>KC008c</c> бьют отряды, <c>KC0089</c>, <c>KC008e</c>,
    /// <c>KC009d</c> не действуют и невидимы; окно 2 (<c>t02-147</c> → <c>t03-148</c>) — <c>KC0001</c> (отряд, затем охрана руины
    /// <c>RU0003</c>) и <c>KC008c</c>. Отрядов сверено 13, сошлось 13, раскрыто боем 5.
    /// </summary>
    [SkippableFact]
    public void IlludereTerra_RevealedAreExactlyTheAttackers()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), Corpus);
        Skip.IfNot(File.Exists(Path.Combine(dir, Journal)), $"нет журнала {Corpus}/{Journal}");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var events = JournalCache.Read(Path.Combine(dir, Journal), ["save", "spell_cast", "battle_start", "battle_end"]).OrderBy(e => e.Seq).ToList();
        var bound = AiStrategicOrdersHarnessTests.BoundSaves(dir, Journal, events.OfType<SaveEvent>().ToList());
        int windows = 0, stacks = 0, same = 0, revealed = 0;
        foreach (var cast in events.OfType<SpellCastEvent>().Where(c => Same(c.Spell, Illudere)))
        {
            var before = bound.Where(b => b.Save.Tid == cast.Tid && b.Save.Seq < cast.Seq).OrderBy(b => b.Save.Seq).Last();
            var after = bound.Where(b => b.Save.Tid == cast.Tid && b.Save.Seq > cast.Seq).OrderBy(b => b.Save.Seq).First();
            var game = Session(before.Path, content, book);
            Assert.Null(game.CastSpell(cast.Player, cast.Spell, (int)cast.X, (int)cast.Y, cast.UseSpellBank));
            var own = game.Map.Stacks.Values.Where(s => Same(s.Owner, cast.Player)).Select(s => s.Id).ToList();
            Assert.All(own, id => Assert.True(game.Invisible(id), $"{id} после литья виден"));
            foreach (var start in events.OfType<BattleStartEvent>().Where(b => b.Tid == cast.Tid && b.Seq > cast.Seq && b.Seq < after.Save.Seq
                                                                               && Same(b.AttackerPlayer, cast.Player)))
            {
                if (!game.Map.Stacks.ContainsKey(start.AttackerGroup) || MapBattle.DefenderOf(game.Map, start.DefenderGroup) is null) continue;
                var end = events.OfType<BattleEndEvent>().First(e => e.Tid == start.Tid && e.Seq > start.Seq);
                var state = game.StartBattle(start.AttackerGroup, start.DefenderGroup);
                foreach (var u in end.Units)
                    if (state.Unit(u.Id) is { } unit) { unit.Hp = (int)Math.Max(0, u.Hp); unit.Retreated = u.Retreating; }
                game.EndBattle([]);
                game.CommitBattle();
            }
            var saved = MapLooksLoader.Load(after.Path).Stacks.ToDictionary(s => s.Id, s => s.Invisible, StringComparer.OrdinalIgnoreCase);
            windows++;
            foreach (string id in own.Where(game.Map.Stacks.ContainsKey).Where(saved.ContainsKey))
            {
                stacks++;
                bool port = game.Invisible(id);
                if (port == saved[id]) same++;
                else output.WriteLine($"{Path.GetFileName(after.Path)} {id}: порт {(port ? "невидим" : "виден")}, сохранение {(saved[id] ? "невидим" : "виден")}");
                if (!saved[id]) revealed++;
            }
        }
        output.WriteLine($"литий {windows}, отрядов заклинателя сверено {stacks}: сошлось {same}, раскрыто к сохранению {revealed}");
        Assert.Equal(2, windows);
        Assert.Equal(13, stacks);
        Assert.Equal(stacks, same);
        Assert.Equal(5, revealed);
    }

    /// <summary>
    /// Ход человека к невидимому чужому отряду (<c>20261016g-port-synth</c>, карта 8, полигон R10). Интерфейс строит путь сквозь невидимку
    /// (<c>0x4cca54</c> → <c>0x5cc6da</c> с <c>a5</c> = 0: отряд — «можно», <c>0x5ccc68</c>) — строка <c>path</c>: длина, последняя
    /// достижимая и цель как у <see cref="GameSession.PathTo"/>; команда видит невидимых (<c>0x5cc120(…, 1, 1)</c>) — отряд встаёт перед ним
    /// (последний <c>stack_move</c> до боя — конец <see cref="PlayerMove.Today"/>), и невидимый нападает сам (<c>battle_start</c>: нападающий —
    /// невидимка, <c>CEffectBattle</c> <c>0x433a57</c>), очки хода за нападение — у пришедшего (<c>ap_apply</c>). Случаев в корпусе — 1 (R10).
    /// </summary>
    [SkippableFact]
    public void HiddenTarget_PathThroughIt_AndItAttacks()
    {
        const string corpus = "20261016g-port-synth", journal = "passive-001.jsonl";
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        Skip.IfNot(File.Exists(Path.Combine(dir, journal)), $"нет журнала {corpus}/{journal}");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var events = JournalCache.Read(Path.Combine(dir, journal), ["save", "map_click", "path", "stack_move", "battle_start", "ap_apply"])
                                 .OrderBy(e => e.Seq).ToList();
        var bound = AiStrategicOrdersHarnessTests.BoundSaves(dir, journal, events.OfType<SaveEvent>().ToList());
        int cases = 0;
        foreach (var start in events.OfType<BattleStartEvent>().Where(b => b.Round == 0))
        {
            var before = bound.Where(b => b.Save.Tid == start.Tid && b.Save.Seq < start.Seq).OrderBy(b => b.Save.Seq).LastOrDefault();
            if (before.Path is null) continue;
            var game = Session(before.Path, content, book);
            string hidden = start.AttackerGroup, mover = start.DefenderGroup;
            // Засада: нападает невидимый чужой отряд, защищается отряд человека (свой невидимый нападающий — обычное нападение).
            if (!game.IsInvisible(hidden) || !game.Map.Stacks.TryGetValue(mover, out var stack)
                || !game.Map.Players.TryGetValue(stack.Owner, out var owner) || !owner.IsHuman || Same(game.Map.Stacks[hidden].Owner, stack.Owner)) continue;
            // Команда: последний щелчок по клетке невидимки до боя; путь интерфейса — первая строка path этого отряда после первого щелчка.
            var clicks = events.OfType<MapClickEvent>().Where(c => c.Seq < start.Seq && c.Seq > before.Save.Seq && c.Action == "down"
                                                                    && c.Objects?.Contains(hidden, StringComparer.OrdinalIgnoreCase) == true).ToList();
            Assert.NotEmpty(clicks);
            var cell = clicks[^1].Cell!;
            var shown = events.OfType<PathEvent>().First(p => p.Seq > clicks[0].Seq && Same(p.Stack, mover));
            var path = game.PathTo(stack, (int)cell.X, (int)cell.Y);
            Assert.Equal(shown.Len, path.Points.Count);
            Assert.Equal((shown.LastX, shown.LastY), ((long)path.Points[path.Reachable].X, (long)path.Points[path.Reachable].Y));
            Assert.Equal((shown.EndX, shown.EndY), ((long)path.Points[^1].X, (long)path.Points[^1].Y));
            var move = PlayerMove.Plan(game, mover, path, ((int)cell.X, (int)cell.Y));
            Assert.Equal(hidden, move.Attack, StringComparer.OrdinalIgnoreCase);
            var walked = events.OfType<StackMoveEvent>().Last(m => m.Seq < start.Seq && Same(m.Stack, mover));
            var end = move.Today[^1];
            Assert.Equal((walked.X, walked.Y), ((long)end.X, (long)end.Y));
            game.MoveStack(mover, end.X, end.Y, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
            Assert.Equal(walked.MoveLeft, game.Map.Stacks[mover].Move);
            var state = game.StartBattle(mover, hidden);
            Assert.Equal(hidden, state.AttackerGroup, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(mover, state.DefenderGroup, StringComparer.OrdinalIgnoreCase);
            var paid = events.OfType<ApApplyEvent>().Last(a => a.Seq < start.Seq && Same(a.Stack, mover));
            Assert.Equal(paid.MoveAfter, game.Map.Stacks[mover].Move);
            Assert.False(game.IsInvisible(hidden) || game.IsInvisible(mover), "нападение не раскрыло отряды");
            output.WriteLine($"{Path.GetFileName(before.Path)}: {mover} → ({cell.X},{cell.Y}), путь {path.Points.Count} точки, встал на ({end.X},{end.Y}), " +
                             $"нападает {hidden}, очки {paid.MoveBefore} → {paid.MoveAfter}");
            cases++;
        }
        Assert.True(cases >= 1, "засад в журнале нет");   // 20261016g: одна (R10)
    }

    private static GameSession Session(string sg, GameContent content, GameBook book)
    {
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = new GameInfo("", "", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        return new GameSession(new GameState(info, looks), content.MapArt, book);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
