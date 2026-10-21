using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 12 против сохранений оригинала (<c>corpus/20261017-port-synth</c>, 13 сохранений дня 1, дни 2 и 3).</summary>
public partial class PortSynthMapsTests
{
    private const string Events3Run = "20261017-port-synth";

    /// <summary>
    /// Партия оригинала карты 12, повторённая портом команда в команду, против каждого её сохранения F9 (t01-028 … t03-042): состояние событий
    /// (выключенные, переменные PS_ON/PS_OFF, журнал заданий — событие, день, номер, эффект окна), отряды западной полосы (клетка, очки хода,
    /// хозяин, приказ, шаблон, юниты с модификаторами), эффекты заклинаний с днём снятия, туман человека (всего и 9×9 у (30,47)), мешки.
    /// Команды — из журнала (<see cref="JournalCache"/>): конец каждой команды хода человека у сервера (<c>stack_move</c> с новым остатком
    /// очков), смена дня — по дню сохранения.
    /// </summary>
    [SkippableFact]
    public void Events3_StateMatchesOriginalSaves()
    {
        Skip.IfNot(MapIds().Contains(Events3Map));
        string dir = Path.Combine(GameSaveTests.CorpusDir(), Events3Run), journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), $"нет {Events3Run}");
        var files = Directory.GetFiles(dir, Events3Map + "-*.sg").Order(StringComparer.Ordinal).ToList();
        var facts = Facts(Events3Map);
        var zones = facts.GetProperty("zones").EnumerateArray().ToDictionary(z => Str(z, "key"));
        var first = MapLoader.Load(files[0]);
        string human = Str(facts.GetProperty("human"), "player");
        var humanStacks = first.Stacks.Values.Where(s => Same(s.Owner, human)).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (saves, commands) = Events3Journal(journal, zones, humanStacks);
        Assert.Equal(files.Count, saves.Count);
        Assert.Equal(15, commands.Count);   // 11 ворот и E07-pre (выход и возврат), CUT-S2 двумя командами, CUT-V2, CUT-B2
        string pre = Str(zones["E07-pre"], "event").ToUpperInvariant();
        var fired = new List<bool>();

        var session = Begin(Events3Map, 3);
        session.EventBattlesOnScreen = false;
        var mismatches = new List<string>();
        int next = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var save = saves[i];
            bool newDay = session.Map.Turn < save.Turn;
            for (; next < commands.Count && commands[next].Seq < save.Seq; next++)
            {
                // Смена дня ставится перед сохранением: команд между сохранением прошлого дня и сохранением нового в этой партии нет.
                Assert.False(newDay, $"команда {commands[next].Stack} (seq {commands[next].Seq}) между сменой дня и {Path.GetFileName(files[i])}");
                Walk(session, commands[next].Stack, commands[next].X, commands[next].Y);
            }
            while (session.Map.Turn < save.Turn) session.EndTurn();
            var engine = MapLooksLoader.Load(files[i]);
            var progress = EventLoader.LoadProgress(files[i], EventLoader.Load(files[i]));
            var e1 = Events3Rows(engine.Map, engine.Bags, progress, human);
            fired.Add(progress.Log.Any(l => Same(l.EventId, pre)));
            var p1 = Events3Rows(session.Map, session.Looks.Bags, session.State.EventProgress, human).ToDictionary(r => r.Key, r => r.Value);
            var rows = e1.Select(r => (r.Key, Engine: r.Value, Port: p1.GetValueOrDefault(r.Key, "нет")))
                         .Concat(p1.Where(r => e1.All(x => x.Key != r.Key)).Select(r => (r.Key, Engine: "нет", Port: r.Value))).ToList();
            Out.WriteLine($"== {Path.GetFileName(files[i])} (день {save.Turn})");
            var cells = FogDiff(engine.Map, session.Map, human);
            if (cells.Count > 0) mismatches.Add($"{Path.GetFileName(files[i])} туман по клеткам (Д — открыта только у движка, П — только у порта): {string.Join(" ", cells.Take(20))}");
            foreach (var (key, e, p) in rows)
            {
                bool same = e == p;
                Out.WriteLine(same ? $"  {key}: {e}" : $"  {key}: движок {e}; ПОРТ {p}");
                if (!same) mismatches.Add($"{Path.GetFileName(files[i])} {key}: движок {e}; порт {p}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
        // E07-pre у движка: включено в t01-029, отряд стоит в зоне — не сработало (029–031), сработало на возврате в зону (032).
        Assert.Equal([false, false, false, false, true], fired.Take(5));
    }

    /// <summary>Сохранения F9 сессии карты 12 и конец каждой команды хода человека между ними. Сессия — поток сервера, у которого ходок E07
    /// пришёл в свои ворота; конец команды — <c>stack_move</c> сервера, где у отряда новый остаток очков (до него идут клетки пути и старт;
    /// команда без траты очков так не видна — на этой карте таких нет).</summary>
    private static (List<SaveEvent> Saves, List<StackMoveEvent> Commands) Events3Journal(string journal,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> zones, IReadOnlySet<string> humanStacks)
    {
        var events = JournalCache.Read(journal, ["save", "stack_move"]).ToList();
        var (gx, gy) = XY(zones["E07"], "gate_at");
        string walker = Str(zones["E07"], "stack");
        var savers = events.OfType<SaveEvent>().Select(s => s.Tid).ToHashSet();   // поток сервера пишет и сохранения; главный — нет
        var moves = events.OfType<StackMoveEvent>().Where(m => humanStacks.Contains(m.Stack) && savers.Contains(m.Tid)).ToList();
        var tids = moves.Where((m, k) => Same(m.Stack, walker) && m.X == gx && m.Y == gy && k > 0 && moves[k - 1].MoveLeft != m.MoveLeft
                                         && Same(moves[k - 1].Stack, walker))
                        .Select(m => m.Tid).Distinct().ToList();
        Assert.True(tids.Count == 1, $"сессий карты 12 (ходок {walker} пришёл в ворота ({gx},{gy})) в журнале {tids.Count}, нужна одна");
        long tid = tids[0];
        var saves = events.OfType<SaveEvent>().Where(s => s.Tid == tid && s.Name == "QuickSave" && s.Ok).ToList();
        Assert.True(saves.Count >= 2, $"у сессии {tid} сохранений F9 {saves.Count}");
        var mine = moves.Where(m => m.Tid == tid && m.Seq > saves[0].Seq && m.Seq < saves[^1].Seq).ToList();
        var commands = new List<StackMoveEvent>();
        var left = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mine)
        {
            if (left.TryGetValue(m.Stack, out long was) && was != m.MoveLeft) commands.Add(m);
            left[m.Stack] = m.MoveLeft;
        }
        return (saves, commands);
    }

    /// <summary>Ход человека к клетке, как команда интерфейса: путь, упреждающая проверка событиями, перестановка с ценой пути.</summary>
    private static void Walk(GameSession session, string stack, long x, long y)
    {
        var path = session.PathTo(session.Map.Stacks[stack], (int)x, (int)y);
        var move = PlayerMove.Plan(session, stack, path);
        Assert.True(move.Today.Count > 0, $"{stack}: нет пути к ({x},{y})");
        var end = move.Today[^1];
        session.MoveStack(stack, end.X, end.Y, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
    }

    private static List<string> FogDiff(MapState engine, MapState port, string human)
    {
        var (e, p) = (engine.Fog[engine.Players[human].FogId], port.Fog[port.Players[human].FogId]);
        var cells = new List<string>();
        for (int y = 0; y < engine.Size; y++)
            for (int x = 0; x < engine.Size; x++)
                if (e[x, y] != p[x, y]) cells.Add($"({x},{y}){(e[x, y] ? "П" : "Д")}");
        return cells;
    }

    private static List<KeyValuePair<string, string>> Events3Rows(MapState m, IEnumerable<BagLook> bags, EventProgress progress, string human)
    {
        var fog = m.Fog[m.Players[human].FogId];
        int fogged = 0;
        for (int y = 0; y < m.Size; y++)
            for (int x = 0; x < m.Size; x++)
                if (fog[x, y]) fogged++;
        string Fog9(int fx, int fy) => string.Concat(from y in Enumerable.Range(fy - 4, 9) from x in Enumerable.Range(fx - 4, 9) select fog[x, y] ? '#' : '.');
        var rows = new List<KeyValuePair<string, string>>
        {
            new("выключены", string.Join(",", progress.Disabled.Select(d => d.ToUpperInvariant()).Order(StringComparer.Ordinal))),
            new("переменные", string.Join(",", progress.Variables.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}"))),
            new("задания", string.Join(" ", progress.Log.Select(l => $"{l.EventId.ToUpperInvariant()}@{l.Turn}#{l.Seq}/{l.EffectNum}"))),
        };
        foreach (var s in m.Stacks.Values.Where(s => s.X < 40 && s.Y is > 15 and < 60).OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase))
        {
            var units = s.UnitIds.Select(u => m.Units[u]).OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
                         .Select(u => $"{u.TypeId.ToUpperInvariant()}:{u.Hp}[{string.Join("+", u.Modifiers.Select(x => x.ToUpperInvariant()))}]");
            rows.Add(new(s.Id.ToUpperInvariant(), $"{s.Owner.ToUpperInvariant()} ({s.X},{s.Y}) ход {s.Move} приказ {s.Order} шаблон '{s.TemplateId.ToUpperInvariant()}' {string.Join(" ", units)}"));
        }
        rows.Add(new("туман", $"{fogged}; 9×9 у (30,47) {Fog9(30, 47)}"));
        rows.Add(new("эффекты", string.Join(" ", m.SpellEffects.Select(e => $"{e.UnitId.ToUpperInvariant()}:{e.ModifierId.ToUpperInvariant()}:{e.TurnStop}").Order(StringComparer.Ordinal))));
        rows.Add(new("мешки", string.Join(" ", bags.Select(b => $"{b.Id.ToUpperInvariant()}@{b.X},{b.Y}").Order(StringComparer.Ordinal))));
        return rows;
    }
}
