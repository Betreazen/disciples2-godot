using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Объект действия команды хода (<c>docs/map-path.md</c> §9, «Нападение на врага у конца пути»): <c>0x5cd38a</c> → <c>0x5cd11f</c> — враждебный
/// отряд вокруг последней достижимой, <b>любой</b>, видимый или нет; генератор <c>0x43fc3c</c> нападает на него (<c>0x43ff9d</c> → <c>0x440667</c>),
/// если путь не оборван событием, а у вора — свиток вора. Сверка по всему корпусу: каждый ход человека (последняя <c>stack_move</c> команды —
/// сразу за <c>ap_restore</c> списания), кончившийся вплотную к видимому чужому отряду, — и что за ним шло. Позиции — сохранение окна и
/// <c>stack_move</c> после него; проигравший бой снимается.
/// </summary>
[Trait("Category", "Corpus")]
public class PathObjectHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Окно журнала вокруг последней <c>stack_move</c> команды, где ищется бой, свиток вора или окно события (seq).</summary>
    private const long Around = 400;

    /// <summary>Боёв с нещёлкнутым соседом в корпусе 17.10 — 4; меньше трёх — корпус не тот (или правило сломано).</summary>
    private const int MinUnclicked = 3;

    private static readonly string[] Kinds =
        ["save", "stack_move", "battle_start", "battle_end", "ui_open", "ap_restore", "path", "noble_choose"];

    /// <summary>Ход человека, кончившийся вплотную к врагу: бой, вор или обрыв событием — и ни разу «встал и стоит». Журнал 17.10: ходов
    /// вплотную 72 — бой 64, окно события (обрыв, <c>0x43ff9d</c>) 6, свиток вора 2, без действия 0; бой с отрядом, на который не щёлкали
    /// (курсор не на нём), — 4 (например, 20260919-human seq 2376476: <c>S073KC0070</c> шёл к (57,23), встал на (58,25) и напал на
    /// <c>S073KC0053</c> (57,26); 20260920-gaps seq 393452: <c>S135KC0000</c> щёлкнул пустую (12,19), встал на неё и напал на <c>S135KC0032</c>
    /// (12,20)).</summary>
    [SkippableFact]
    public void HumanMove_EndingBesideEnemy_AlwaysActs()
    {
        var (content, book) = (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules")));
        var tally = Survey(content, book);
        output.WriteLine($"ходов человека {tally.Moves}, вплотную к врагу {tally.Beside}: бой {tally.Battle} (с нещёлкнутым {tally.Unclicked}), " +
                         $"вор {tally.Thief}, событие {tally.Event}, без действия {tally.Idle}");
        Skip.If(tally.Moves == 0, "в корпусе нет ходов человека");
        Assert.Equal(0, tally.Idle);
        Assert.True(tally.Unclicked >= MinUnclicked, $"боёв с нещёлкнутым соседом {tally.Unclicked}");
    }

    /// <summary>Бой с нещёлкнутым соседом — в порте: из сохранения окна (отряд ещё на старте команды, с тем же запасом, враг на месте) путь
    /// интерфейса к клетке курсора (<see cref="GameSession.PathTo"/>) и команда (<see cref="PlayerMove.Plan"/>): <see cref="PlayerMove.Attack"/> —
    /// тот же враг, конец сегодняшней части — клетка, где встал отряд. До 17.10 порт нападал только на щёлкнутую цель.</summary>
    [SkippableFact]
    public void BesideAttack_PortPicksTheSameFoe()
    {
        var (content, book) = (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules")));
        var tally = Survey(content, book);
        Skip.If(tally.Cases.Count == 0, "боёв с нещёлкнутым соседом нет");
        int replayed = 0;
        foreach (var c in tally.Cases)
        {
            var game = Session(c.Save, content, book)!;
            var stack = game.Map.Stacks[c.Stack];
            if ((stack.X, stack.Y) != c.Start || stack.Move != c.MoveBefore || !game.Map.Stacks.TryGetValue(c.Foe, out var foe)
                || (foe.X, foe.Y) != c.FoeAt) continue;
            var path = game.PathTo(stack, c.Cursor.X, c.Cursor.Y);
            var move = PlayerMove.Plan(game, c.Stack, path, c.Cursor);
            output.WriteLine($"{Path.GetFileName(c.Save)}: {c.Stack} → курсор {c.Cursor}, порт встал {(move.Today.Count > 0 ? (move.Today[^1].X, move.Today[^1].Y) : c.Start)}, нападает {move.Attack}");
            Assert.Equal(c.Foe, move.Attack, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(c.End, (move.Today[^1].X, move.Today[^1].Y));
            replayed++;
        }
        output.WriteLine($"случаев {tally.Cases.Count}, повторено из сохранения {replayed}");
        Assert.True(replayed >= 1, "ни один случай не повторён из сохранения");
    }

    private sealed class Tally
    {
        public int Moves, Beside, Battle, Unclicked, Thief, Event, Idle;
        public List<BesideCase> Cases { get; } = [];
        public string Save { get; set; } = "";
    }

    private sealed record BesideCase(string Save, string Stack, (int X, int Y) Start, long MoveBefore, (int X, int Y) Cursor, (int X, int Y) End,
                                     string Foe, (int X, int Y) FoeAt);

    private Tally Survey(GameContent content, GameBook book)
    {
        var tally = new Tally();
        foreach (var (dir, journal) in Journals())
            foreach (var (save, window) in Windows(dir, journal))
                if (Session(save, content, book) is { } game)
                {
                    tally.Save = save;
                    Scan(game, window, tally, Path.GetFileName(dir));
                }
        return tally;
    }

    private void Scan(GameSession game, List<CorpusEvent> window, Tally tally, string dir)
    {
        var pos = game.Map.Stacks.Values.Where(s => s.Inside.Length == 0)
                      .ToDictionary(s => s.Id, s => (s.X, s.Y), StringComparer.OrdinalIgnoreCase);
        string? attacker = null, defender = null;
        for (int i = 0; i < window.Count; i++)
        {
            switch (window[i])
            {
                case BattleStartEvent b:
                    (attacker, defender) = (b.AttackerGroup, b.DefenderGroup);
                    continue;
                case BattleEndEvent e when attacker is not null:
                    pos.Remove(Same(e.Winner, attacker) ? defender! : attacker);
                    continue;
                case StackMoveEvent m when m.Ret:
                    pos[m.Stack] = ((int)m.X, (int)m.Y);
                    // Последняя stack_move команды — сразу за списанием очков (ap_restore того же отряда).
                    if (i > 0 && window[i - 1] is ApRestoreEvent r && Same(r.Stack, m.Stack) && r.MoveBefore != r.MoveAfter)
                        Judge(game, window, i, m, pos, tally, dir);
                    continue;
            }
        }
    }

    private void Judge(GameSession game, List<CorpusEvent> window, int i, StackMoveEvent m, Dictionary<string, (int X, int Y)> pos,
                       Tally tally, string dir)
    {
        if (!game.Map.Stacks.TryGetValue(m.Stack, out var stack) || !game.Map.Players.TryGetValue(stack.Owner, out var owner) || !owner.IsHuman)
            return;
        tally.Moves++;
        var hostile = AiTactics.ObjectHostile(game, stack);
        bool near = pos.Any(p => !Same(p.Key, m.Stack) && Math.Max(Math.Abs(p.Value.X - m.X), Math.Abs(p.Value.Y - m.Y)) == 1
                                 && game.Map.Stacks.TryGetValue(p.Key, out var o) && hostile(o, false) && !game.IsInvisible(o.Id));
        if (!near) return;
        tally.Beside++;
        var around = window.Where(e => e.Seq > m.Seq - Around && e.Seq < m.Seq + Around).ToList();
        if (around.OfType<BattleStartEvent>().FirstOrDefault(b => Same(b.AttackerGroup, m.Stack) || Same(b.DefenderGroup, m.Stack)) is { } fight)
        {
            tally.Battle++;
            string foe = Same(fight.AttackerGroup, m.Stack) ? fight.DefenderGroup : fight.AttackerGroup;
            var shown = window.Take(i).OfType<PathEvent>().LastOrDefault(p => Same(p.Stack, m.Stack));
            if (shown is not null && pos.TryGetValue(foe, out var at) && Math.Max(Math.Abs(at.X - m.X), Math.Abs(at.Y - m.Y)) == 1
                && (at.X != shown.EndX || at.Y != shown.EndY))
            {
                tally.Unclicked++;
                if (i > 1 && window[i - 2] is StackMoveEvent back && Same(back.Stack, m.Stack) && window[i - 1] is ApRestoreEvent paid)
                    tally.Cases.Add(new BesideCase(tally.Save, m.Stack, ((int)back.X, (int)back.Y), paid.MoveBefore,
                                                   ((int)shown.EndX, (int)shown.EndY), ((int)m.X, (int)m.Y), foe, at));
                output.WriteLine($"{dir} seq {m.Seq}: {m.Stack} → ({m.X},{m.Y}), курсор ({shown.EndX},{shown.EndY}), бой с {foe} ({at.X},{at.Y})");
            }
            return;
        }
        var after = around.Where(e => e.Seq > m.Seq).ToList();
        if (after.OfType<NobleChooseEvent>().Any(n => Same(n.Stack, m.Stack))) tally.Thief++;
        else if (after.OfType<UiOpenEvent>().Any(u => u.Name == "DLG_EVENT_POPUP")) tally.Event++;
        else
        {
            tally.Idle++;
            output.WriteLine($"БЕЗ ДЕЙСТВИЯ {dir} seq {m.Seq}: {m.Stack} → ({m.X},{m.Y})");
        }
    }

    private static IEnumerable<(string Dir, string Journal)> Journals()
    {
        foreach (string dir in Directory.GetDirectories(GameSaveTests.CorpusDir()).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
            foreach (string journal in Directory.GetFiles(dir, "*.jsonl").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
                yield return (dir, journal);
        }
    }

    /// <summary>Окна журнала между соседними привязанными сохранениями: (сохранение, события окна).</summary>
    private static IEnumerable<(string Save, List<CorpusEvent> Window)> Windows(string dir, string journal)
    {
        var events = JournalCache.Read(Path.Combine(dir, journal), Kinds).OrderBy(e => e.Seq).ToList();
        List<(string Path, SaveEvent Save)> bound;
        try
        {
            bound = [.. AiStrategicOrdersHarnessTests.BoundSaves(dir, journal, events.OfType<SaveEvent>().ToList()).OrderBy(b => b.Save.Seq)];
        }
        catch (InvalidOperationException)
        {
            yield break;   // старые описи (до схемы 6): сохранения — объекты, а не имена; привязки к журналу нет
        }
        for (int w = 0; w < bound.Count; w++)
        {
            long from = bound[w].Save.Seq, to = w + 1 < bound.Count ? bound[w + 1].Save.Seq : long.MaxValue;
            yield return (bound[w].Path, events.Where(e => e.Seq > from && e.Seq < to).ToList());
        }
    }

    private static GameSession? Session(string sg, GameContent content, GameBook book)
    {
        var looks = MapLooksLoader.Load(sg);
        if (looks.Map.Players.Values.FirstOrDefault(p => p.IsHuman) is not { } human) return null;
        var info = new GameInfo("", "", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1);
        return new GameSession(new GameState(info, looks), content.MapArt, book);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
