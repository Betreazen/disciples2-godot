using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui.Windows;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 14 против сохранений оригинала (партия 18.10, <c>corpus/20261018-port-synth</c>, сохранения t01-021 … t04-024).</summary>
public partial class PortSynthMapsTests
{
    private const string Fog3Run = "20261018-port-synth";

    /// <summary>Сохранений карты 14 в партии 18.10: после Сокрытия и шага (день 1), после «Тумана Смерти» и «Сумерек» (день 2), конец дня 3
    /// (автосохранение) и начало дня 4.</summary>
    private const int Fog3SaveCount = 4;

    /// <summary>
    /// Партия оригинала карты 14, повторённая портом с начала сценария команда в команду, против каждого её сохранения: литья человека
    /// (<c>spell_cast</c> с <c>ret</c>) и его ходы (конец команды — <c>stack_move</c>), смена дня — по ближайшей строке <c>save</c> после
    /// команды (у движка автосохранение пишется в конце хода человека). Сверка области карты 14 (<see cref="Fog3Rows"/>): туман каждого
    /// игрока клетка в клетку, клетки и ход отрядов, невидимость, казна человека. Ходы ИИ — порта: отряды «идти к месту» у движка там же.
    /// Экономика ИИ (наймы, здоровье нанятых, опыт, гарнизоны, казна ИИ) сверяется тоже (с 18.10, <c>r18/aihire</c>): типы найма дня 1 —
    /// шаг CRT новой партии (<c>0x42af9b</c>, <see cref="NewGame"/>), гарнизон <c>S143FT0001</c> — подготовка отряда в повторе оценки
    /// (<c>0x466b68</c>, <see cref="AiTactics.Choose"/>); до них расходились у Империи наймы и казна (0/10, 50/70, 50/80).
    /// <para>Ответы движка: Сокрытие держится после шага (t01-021); «Сумерки» закрыли у Империи разведанный старт ушедших отрядов и всё
    /// невидимое, у прочих ИИ — тоже, заклинателю и нейтралу — ничего (t02-022). Форма «Тумана Смерти» здесь не видна: он брошен в (40,35) и
    /// перекрыт «Сумерками» того же окна сохранений.</para>
    /// </summary>
    [SkippableFact]
    public void Fog3_StateMatchesOriginalSaves()
    {
        Skip.IfNot(MapIds().Contains(Fog3Map));
        var (journal, saves) = Fog3Saves();
        Assert.Equal(Fog3SaveCount, saves.Count);
        var facts = Facts(Fog3Map);
        string human = Str(facts.GetProperty("human"), "player"), empire = facts.GetProperty("watched").GetString()!;
        var session = Begin(Fog3Map, 1);
        var humanStacks = session.Map.Stacks.Values.Where(s => Same(s.Owner, human)).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (commands, lines) = Fog3Commands(journal, saves, humanStacks, human);
        // Сокрытие, шаг Герцога, «Туман Смерти», «Сумерки»
        Assert.True(commands.Count == 4, "команды журнала: " + string.Join(", ", commands.Select(c => c switch
        {
            SpellCastEvent sc => $"{sc.Spell} ({sc.X},{sc.Y})",
            StackMoveEvent m => $"{m.Stack} → ({m.X},{m.Y})",
            _ => c.GetType().Name,
        })));
        var leavers = Zone(facts, "NF").GetProperty("leavers").EnumerateArray().Select(l => XY(l, "start")).ToList();
        bool[,] EmpireFog(MapState m) => m.Fog[m.Players[empire].FogId];

        var failed = new List<string>();
        var before = session.Looks;
        int next = 0, empireBeforeCasts = -1;
        foreach (var save in saves)
        {
            for (; next < commands.Count && commands[next].Seq < save.Seq; next++)
            {
                var c = commands[next];
                long day = lines.First(s => s.Seq > c.Seq).Turn;
                while (session.Map.Turn < day) session.EndTurn();
                if (c is SpellCastEvent && day == 2 && empireBeforeCasts < 0)
                {
                    // Старт ушедших отрядов Империи к литьям дня 2 разведан и не виден — «Сумеркам» есть что закрыть.
                    empireBeforeCasts = HiddenCount(EmpireFog(session.Map));
                    Assert.All(leavers, l => Assert.False(EmpireFog(session.Map)[l.X, l.Y], $"старт ({l.X},{l.Y}) у Империи не разведан"));
                }
                if (c is StackMoveEvent m) Walk(session, m.Stack, m.X, m.Y);
                else Assert.Empty(Replay(session, [(SpendEvent)c]));
            }
            while (session.Map.Turn < save.Turn) session.EndTurn();
            var engine = MapLooksLoader.Load(save.Path);
            var (mine, others) = Fog3Rows(before, engine, session, human);
            string name = Path.GetFileName(save.Path)[^10..^3];
            var em = engine.Map;
            Out.WriteLine($"{name} (день {save.Turn}): {(mine.Count == 0 ? "совпало" : string.Join("; ", mine))}; закрыто у " +
                          string.Join(", ", em.Players.Values.Select(p => $"{p.Id} {HiddenCount(em.Fog[p.FogId])}")));
            if (others.Count > 0) Out.WriteLine($"  экономика ИИ: {string.Join("; ", others)}");
            if (mine.Count > 0) failed.Add($"{name}: {string.Join("; ", mine)}");
            if (others.Count > 0) failed.Add($"{name} (экономика ИИ): {string.Join("; ", others)}");
            if (save.Turn == 2)
            {
                Assert.All(leavers, l => Assert.True(EmpireFog(em)[l.X, l.Y], $"старт ({l.X},{l.Y}) у Империи открыт после «Сумерек»"));
                Out.WriteLine($"  Империя: у порта перед литьями дня 2 закрыто {empireBeforeCasts}, после — {HiddenCount(EmpireFog(em))}");
            }
            before = engine;
        }
        var iv = Zone(facts, "IV");
        var duke = MapLooksLoader.Load(saves[0].Path).Stacks.Single(s => Same(s.Id, Str(iv, "stack")));
        Assert.True(duke.Invisible && (duke.X, duke.Y) == XY(iv, "step_to"), "Сокрытие у движка снято шагом");
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    /// <summary>
    /// Прицел «Тумана Смерти» (<c>AREA</c> 9) над стартом NF1 (40,30) в день 1: квадрат 9 × 9 целиком — 81 клетка (<c>0x4d2f58</c> →
    /// <c>0x4d32e0</c>; у порта так и было, пометка 29 партии 18.10), а в обзоре чужих (<c>SPLAREAFOGN</c>) — клетки прямоугольника
    /// «точка + размер 1» отряда Империи, расширенного на его обзор и перебранного включая границы (<c>0x4d31d1</c>): <c>[x − R, x + 1 + R] ×
    /// [y − R, y + 1 + R]</c>, без снятых углов (прежний порт брал настоящий обзор — квадрат ±R без углов). Отряд дальше 8 клеток от квадрата
    /// (<c>0x4d3333</c>) не учитывается.
    /// </summary>
    [SkippableFact]
    public void Fog3_FogAim_FullSquare_ForeignSightLikeEngine()
    {
        Skip.IfNot(MapIds().Contains(Fog3Map));
        var facts = Facts(Fog3Map);
        var session = Begin(Fog3Map, 1);
        string human = session.ActiveHuman;
        var cast = Zone(facts, "casts").GetProperty("casts").EnumerateArray().Single(c => Str(c, "key") == "FG");
        var (x, y) = XY(cast, "at");
        var leaver = session.Map.Stacks[Str(Zone(facts, "NF").GetProperty("leavers")[0], "stack")];
        Assert.Equal((x, y), (leaver.X, leaver.Y));
        int scout = session.ScoutRange(leaver), r = 4;
        Assert.True(scout < Fog.MaxScoutRange, $"обзор {scout}: прицел у края обзора уже не соберёт отряд (±8)");
        var view = SpellAimView.At(session, human, Str(cast, "spell"), x, y);
        var foreign = view.Area.Where(m => m.Frame == "SPLAREAFOGN").Select(m => (m.X, m.Y)).ToHashSet();
        var expected = (from cx in Enumerable.Range(x - r, 2 * r + 1) from cy in Enumerable.Range(y - r, 2 * r + 1)
                        where cx >= x - scout && cx <= x + 1 + scout && cy >= y - scout && cy <= y + 1 + scout select (cx, cy)).ToHashSet();
        Out.WriteLine($"прицел {view.Area.Count} клеток, в обзоре чужих {foreign.Count} (обзор отряда {scout})");
        Assert.Equal(81, view.Area.Count);
        Assert.True(expected.SetEquals(foreign), $"порт {foreign.Count}, движок {expected.Count}");
        // Прицел правее: в обзоре чужих только его левый столбец x + 1 + R — на клетку дальше настоящего обзора отряда.
        int edge = x + 1 + scout;
        var side = SpellAimView.At(session, human, Str(cast, "spell"), edge + r, y).Area.Where(m => m.Frame == "SPLAREAFOGN").ToList();
        Assert.Equal(2 * r + 1, side.Count);
        Assert.All(side, m => Assert.Equal(edge, m.X));
        Assert.True(session.ForeignSight(human, x + r + Fog.MaxScoutRange, y, r)[x + 1, y]);
        Assert.False(session.ForeignSight(human, x + r + Fog.MaxScoutRange + 1, y, r)[x + 1, y]);   // отряд за 8 клетками не собран
    }

    /// <summary>Клетки «в обзоре чужих» у укрепления (<c>0x4d31d1</c>): пятно «точка + размер», расширенное на обзор и перебранное включая
    /// границы, — <c>[minX − R, maxX + 1 + R] × [minY − R, maxY + 1 + R]</c>: справа и снизу на клетку дальше, чем слева и сверху. Столица
    /// Империи у верхнего края карты — обзор берётся по левой стороне, сверяется нижняя.</summary>
    [SkippableFact]
    public void Fog3_FogAim_ForeignFortRectangle()
    {
        Skip.IfNot(MapIds().Contains(Fog3Map));
        var facts = Facts(Fog3Map);
        var session = Begin(Fog3Map, 1);
        var map = session.Map;
        string empire = facts.GetProperty("watched").GetString()!;
        var capital = map.Sites.Single(s => s.Kind == SiteKind.Capital && Same(s.Owner, empire));
        var plan = LandModel.PlanCells(map, capital.Id);
        int minX = plan.Min(c => c.X), maxX = plan.Max(c => c.X), maxY = plan.Max(c => c.Y);
        var seen = session.ForeignSight(session.ActiveHuman, minX, maxY, 4);
        int column = maxX - 1, left = Enumerable.Range(0, map.Size).First(cx => seen[cx, maxY]);
        int bottom = Enumerable.Range(0, map.Size).Last(cy => seen[column, cy]);
        int radius = minX - left;
        Out.WriteLine($"столица {capital.Id} пятно x {minX}..{maxX}, y ..{maxY}; обзор {radius}; низ {bottom}");
        Assert.InRange(radius, 1, Fog.MaxScoutRange);
        Assert.Equal(Math.Min(map.Size - 1, maxY + 1 + radius), bottom);
        Assert.True(maxY + 1 + radius < map.Size, "низ упёрся в край карты — сверка не различает");
        Assert.True(seen[left, bottom], "угол прямоугольника снят");
    }

    /// <summary>Строки расхождений <see cref="Differences"/> и клеток отрядов, разделённые на область карты 14 (туман, клетка, ход,
    /// невидимость, казна человека) и прочее.</summary>
    private static (List<string> Mine, List<string> Others) Fog3Rows(MapLooks before, MapLooks engine, GameSession port, string human)
    {
        var rows = Differences(before, engine, port);
        Diff(rows, "клетка", engine.Map.Stacks.Values.Where(s => s.Inside.Length == 0), s => s.Id, s => (string?)null,
             s => $"({s.X},{s.Y})", s => port.Map.Stacks.TryGetValue(s.Id, out var p) ? $"({p.X},{p.Y})" : "нет");
        Diff(rows, "казна человека", [engine.Map.Players[human]], p => p.Id, p => before.Map.Players[p.Id].Bank, p => p.Bank,
             p => port.Map.Players[p.Id].Bank);
        string[] mine = ["туман", "клетка", "ход", "невидимость", "казна человека"];
        var split = rows.ToLookup(r => mine.Any(k => r.StartsWith(k + ":", StringComparison.Ordinal) || r.StartsWith(k + " ", StringComparison.Ordinal)));
        return ([.. split[true]], [.. split[false]]);
    }

    /// <summary>Журнал 18.10 и сохранения карты 14 на его оси (<see cref="CheckJournal"/>, по <c>seq</c>).</summary>
    private static (string Journal, List<SaveCopy> Saves) Fog3Saves()
    {
        string journal = Path.Combine(GameSaveTests.CorpusDir(), Fog3Run, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), $"нет {Fog3Run}");
        var saves = CheckJournal.Load(journal).Saves
                                .Where(s => Path.GetFileName(s.Path).StartsWith(Fog3Map + "-", StringComparison.OrdinalIgnoreCase)).ToList();
        Skip.If(saves.Count < 2, "нет сохранений карты 14");
        return (journal, saves);
    }

    /// <summary>Команды человека карты 14 по порядку — литья человека с <c>ret</c> и конец каждой команды хода (<c>stack_move</c> сервера, где у
    /// отряда человека новый остаток очков: до него идут клетки пути и старт, как у <see cref="Events3Journal"/>) — и все строки <c>save</c>
    /// (по ним — день команды). Карта — от последнего сохранения прошлой карты журнала до последнего своего.</summary>
    private static (List<CorpusEvent> Commands, List<SaveEvent> Lines) Fog3Commands(string journal, List<SaveCopy> saves,
                                                                                    IReadOnlySet<string> humanStacks, string humanPlayer)
    {
        long from = CheckJournal.Load(journal).Saves.Where(s => s.Seq < saves[0].Seq).Select(s => s.Seq).DefaultIfEmpty(0).Max(), to = saves[^1].Seq;
        var events = JournalCache.Read(journal, ["save", "spell_cast", "stack_move"]).Where(e => e.Seq > from && e.Seq <= to).ToList();
        var lines = events.OfType<SaveEvent>().Where(s => s.Ok && !s.Temp).ToList();
        var commands = new List<CorpusEvent>();
        var left = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
            switch (e)
            {
                case SpellCastEvent { Ret: true } c when Same(c.Player, humanPlayer):
                    commands.Add(c);
                    break;
                case StackMoveEvent m when humanStacks.Contains(m.Stack):
                    if (left.TryGetValue(m.Stack, out long was) && was > m.MoveLeft) commands.Add(m);   // рост — восстановление хода в начале дня
                    left[m.Stack] = m.MoveLeft;
                    break;
            }
        return (commands, lines);
    }
}
