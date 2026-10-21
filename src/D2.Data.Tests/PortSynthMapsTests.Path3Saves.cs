using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 13 против журнала и сохранений оригинала (<c>corpus/20261018-port-synth</c>, сохранения t01-001 … t03-020): команды хода
/// человека (списание, нападение, засада, клетки и очки хода после), щелчки по союзнику (окно <c>X005TA0303</c>), охотники ИИ в трубках.</summary>
public partial class PortSynthMapsTests
{
    private const string Path3Run = "20261018-port-synth";

    private static string Path3Dir => Path.Combine(GameSaveTests.CorpusDir(), Path3Run);

    private static string Path3Journal => Path.Combine(Path3Dir, "passive-001.jsonl");

    private static readonly string[] Path3Kinds = ["save", "map_click", "path", "stack_move", "ap_restore", "ap_effect", "battle_start", "ui_msgbox"];

    /// <summary>События карты 13 по порядку и её сохранения, привязанные к строкам <c>save</c> (<see cref="AiStrategicOrdersHarnessTests.BoundSaves"/>).</summary>
    private static (List<CorpusEvent> Events, List<(string Path, SaveEvent Save)> Saves) Path3Original()
    {
        var events = JournalCache.Read(Path3Journal, Path3Kinds).OrderBy(e => e.Seq).ToList();
        var saves = AiStrategicOrdersHarnessTests.BoundSaves(Path3Dir, "passive-001.jsonl", events.OfType<SaveEvent>().ToList())
                                                 .Where(b => Path.GetFileName(b.Path).StartsWith(Path3Map, StringComparison.Ordinal))
                                                 .OrderBy(b => b.Save.Seq).ToList();
        long last = saves.Count > 0 ? saves[^1].Save.Seq : 0;
        return ([.. events.Where(e => e.Seq <= last)], saves);
    }

    /// <summary>Партия порта с сохранения оригинала: карта, события и их ход, правила.</summary>
    private static GameSession Path3Session(string sg, string human)
    {
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        var state = new GameState(new GameInfo(Path3Map, Path3Map, [human], 1, 1), looks) { Events = events, EventProgress = EventLoader.LoadProgress(sg, events) };
        return new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book) { EventBattlesOnScreen = false };
    }

    /// <summary>
    /// AL (<see cref="GameSession.AllyRefuses"/>, <c>0x4d1590</c> → <c>0x4d0ca4</c>): каждый щелчок оригинала, после которого движок показал
    /// «Перед тем, как атаковать союзника, расторгните альянс с ним.» (<c>ui_msgbox</c> <c>X005TA0303</c>; день 1 — пустая (13,67) над Эльфами и сами
    /// Эльфы (14,68), день 2 — снова (13,67)), — у порта отказ союзнику и пути к нему нет; каждый щелчок, показавший путь (<c>path</c>), — у порта
    /// отказа нет. Порт до 18.10 прокладывал путь к союзнику и окна не показывал (слова пользователя).
    /// </summary>
    [SkippableFact]
    public void Path3_ClickOnAlly_RefusedLikeTheOriginal()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        Skip.IfNot(File.Exists(Path3Journal), $"нет {Path3Run}");
        var facts = Facts(Path3Map);
        var al = Zone(facts, "AL");
        string human = Str(facts.GetProperty("human"), "player");
        var (events, saves) = Path3Original();
        var clicks = events.OfType<MapClickEvent>().Where(c => c.Button == 1 && c.Task == "CTaskSelectUnit" && c.Cell is not null).ToList();
        int refused = 0, shown = 0;
        var diffs = new List<string>();
        foreach (var c in clicks)
        {
            long next = clicks.FirstOrDefault(k => k.Seq > c.Seq)?.Seq ?? long.MaxValue;
            bool engineRefuses = events.OfType<UiMsgBoxEvent>().Any(m => m.Seq > c.Seq && m.Seq < next && m.LastTextId == GameSession.AllyRefusalText);
            var path = events.OfType<PathEvent>().FirstOrDefault(p => p.Seq > c.Seq && p.Seq < Math.Min(next, c.Seq + 5000));
            if (!engineRefuses && path is null) continue;
            var before = saves.LastOrDefault(s => s.Save.Seq < c.Seq);
            if (before.Path is null) continue;
            var session = Path3Session(before.Path, human);
            string id = engineRefuses ? Str(al, "stack") : path!.Stack;
            if (!session.Map.Stacks.TryGetValue(id, out var stack)) continue;
            (int X, int Y) at = ((int)c.Cell!.X, (int)c.Cell.Y);
            var aim = session.ClickAim(stack, at.X, at.Y);
            bool port = session.AllyRefuses(stack, aim);
            if (engineRefuses) refused++;
            else shown++;
            Out.WriteLine($"seq {c.Seq} {id} щёлкнул {at}: движок {(engineRefuses ? "окно X005TA0303, пути нет" : $"путь к ({path!.EndX},{path.EndY})")}, " +
                          $"порт — прицел {aim}, {(port ? "отказ союзнику" : "путь")}");
            if (port != engineRefuses) diffs.Add($"seq {c.Seq} {id} {at}: движок {(engineRefuses ? "отказ" : "путь")}, порт {(port ? "отказ" : "путь")}");
        }
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
        Assert.Equal(3, refused);   // день 1: (13,67) и (14,68); день 2: (13,67)
        Assert.True(shown >= 15, $"щелчков с путём сверено {shown}");
    }

    /// <summary>
    /// Команды хода человека дня 1 (SU, MS, C5t-ворота, HM1, HM2, CH1, CH2, E07p, CUT-S3/B3/V3 — оба шага), повторённые портом с сохранения перед
    /// каждой: щелчок, показавший путь (<c>path</c> после <c>map_click</c>), — прицел и путь порта; щелчок, после которого сервер пошёл
    /// (<c>stack_move</c> раньше нового пути), — подтверждение (<see cref="GameSession.ClickConfirms"/>) и команда (<see cref="PlayerMove.Plan"/>).
    /// Сверка с движком: списание хода (<c>ap_restore</c> <c>stack_move</c>, нет — 0), нападение (<c>ap_effect</c> <c>attack</c>), стороны боя
    /// (<c>battle_start</c>: нападает невидимка — засада), а после каждого окна — клетка, очки хода и невидимость отрядов полосы и отряды,
    /// которых нет (сохранение после); путь после хода — показан ли снова (<see cref="GameSession.ShownPathKept"/>). Ответы движка: обрыв у CH1 — 4 (цена клетки обрыва), второй ход CH1 — 6, CH2 — 6 и без шага
    /// (конец присланного списка без клетки невидимки, <c>0x5cd0e9</c>), HM1 — 10, HM2 — 8.
    /// </summary>
    [SkippableFact]
    public void Path3_HumanCommandsLikeTheOriginal()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        Skip.IfNot(File.Exists(Path3Journal), $"нет {Path3Run}");
        var facts = Facts(Path3Map);
        string human = Str(facts.GetProperty("human"), "player");
        var (events, saves) = Path3Original();
        var days1 = saves.Where(s => s.Save.Turn == 1).ToList();
        Assert.True(days1.Count >= 17, $"сохранений дня 1 {days1.Count}");
        var diffs = new List<string>();
        int commands = 0, charged = 0, ambushes = 0, kept = 0;
        (string Stack, int X, int Y)? shown = null;   // щелчок, показавший путь (интерфейс помнит его и через F9)
        for (int w = 0; w + 1 < days1.Count; w++)
        {
            var session = Path3Session(days1[w].Path, human);
            long from = days1[w].Save.Seq, to = days1[w + 1].Save.Seq;
            var window = events.Where(e => e.Seq > from && e.Seq < to).ToList();
            var clicks = window.OfType<MapClickEvent>().Where(c => c.Button == 1 && c.Cell is not null).ToList();
            foreach (var c in clicks)
            {
                long next = clicks.FirstOrDefault(k => k.Seq > c.Seq)?.Seq ?? to;
                var after = window.Where(e => e.Seq > c.Seq && e.Seq < next).ToList();
                var path = after.OfType<PathEvent>().FirstOrDefault();
                var move = after.OfType<StackMoveEvent>().FirstOrDefault();
                (int X, int Y) at = ((int)c.Cell!.X, (int)c.Cell.Y);
                if (move is not null && (path is null || move.Seq < path.Seq) && shown is { } s && Same(move.Stack, s.Stack))
                {
                    commands++;
                    var stack = session.Map.Stacks[s.Stack];
                    var aim = session.ClickAim(stack, s.X, s.Y);
                    var preview = session.PathTo(stack, aim.X, aim.Y);
                    if (!session.ClickConfirms(stack, preview, aim, at.X, at.Y))
                        diffs.Add($"seq {c.Seq} {s.Stack}: движок пошёл по щелчку {at}, порт путь к {aim} не подтвердил");
                    var (charge, attack, ambush, walked) = Path3Command(session, s.Stack, preview, aim);
                    long engineCharge = after.OfType<ApRestoreEvent>().Where(r => Same(r.Stack, s.Stack) && r.Cause == "stack_move").Sum(r => r.Amount);
                    bool engineAttack = after.OfType<ApEffectEvent>().Any(a => Same(a.Stack, s.Stack) && a.Cause == "attack");
                    var start = after.OfType<BattleStartEvent>().FirstOrDefault(b => b.Round == 0);
                    bool engineAmbush = start is not null && Same(start.DefenderGroup, s.Stack);
                    charged += engineCharge > 0 ? 1 : 0;
                    ambushes += engineAmbush ? 1 : 0;
                    Out.WriteLine($"seq {c.Seq} {s.Stack} к {aim}: движок — списано {engineCharge}, нападение {(engineAttack ? "да" : "нет")}" +
                                  $"{(engineAmbush ? ", засада" : "")}; порт — {charge}, {(attack ? "да" : "нет")}{(ambush ? ", засада" : "")}");
                    if (charge != engineCharge || attack != engineAttack || ambush != engineAmbush)
                        diffs.Add($"seq {c.Seq} {s.Stack} к {aim}: движок {engineCharge}/{engineAttack}/{engineAmbush}, порт {charge}/{attack}/{ambush}");
                    // Путь после команды (0x4cfead): движок показал его снова (path отряда после хода) — один щелчок по цели ведёт дальше.
                    bool engineKept = after.OfType<PathEvent>().Any(p => p.Seq > move.Seq && Same(p.Stack, s.Stack));
                    bool portKept = session.Map.Stacks.TryGetValue(s.Stack, out var moved) && session.ShownPathKept(moved, preview, walked, aim);
                    if (moved is not { Inside.Length: > 0 })
                    {
                        kept += engineKept ? 1 : 0;
                        if (engineKept != portKept)
                            diffs.Add($"seq {c.Seq} {s.Stack} к {aim}: путь после хода у движка {(engineKept ? "показан" : "сброшен")}, у порта {(portKept ? "показан" : "сброшен")}");
                        // Показанный снова — остаток прежнего списка с новой клетки (последний path отряда до следующего щелчка).
                        if (engineKept && portKept && after.OfType<PathEvent>().Last(p => Same(p.Stack, s.Stack)) is var again)
                        {
                            var rest = session.KeptPath(moved!, preview, walked);
                            var last = rest.Points[rest.Reachable];
                            if (again.Len != rest.Points.Count || (again.LastX, again.LastY) != (last.X, last.Y))
                                diffs.Add($"seq {c.Seq} {s.Stack}: путь после хода у движка длина {again.Len}, встанет ({again.LastX},{again.LastY}); " +
                                          $"у порта {rest.Points.Count}, ({last.X},{last.Y})");
                        }
                    }
                    shown = engineKept ? s : null;
                    continue;
                }
                if (path is not null) shown = (path.Stack, at.X, at.Y);
            }
            diffs.AddRange(Path3StripDiffs(session, days1[w + 1].Path));
        }
        Out.WriteLine($"команд {commands}, со списанием {charged}, засад {ambushes}, путь показан снова {kept}");
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
        Assert.True(commands >= 14, $"команд сверено {commands}");
        Assert.True(charged >= 10 && ambushes == 4, $"со списанием {charged}, засад {ambushes}");
        Assert.True(kept >= 5, $"путь после хода показан снова {kept} раз");
    }

    /// <summary>Команда человека, как <c>MapView.Move</c>: шаги (с обрывом событием), списание (<see cref="PlayerMove.Charge"/> или цена клетки,
    /// где встал), затем нападение (бой автобоем), вход в крепость или мешок у конца пути. Возвращает списание, было ли нападение и засада.</summary>
    private static (long Charge, bool Attack, bool Ambush, int Walked) Path3Command(GameSession session, string id, StackPath path, (int X, int Y) aim)
    {
        var stack = session.Map.Stacks[id];
        int before = stack.Move;
        var move = PlayerMove.Plan(session, id, path, aim);
        if (move.Today.Count > 0)
        {
            var end = move.Today[^1];
            session.MoveStack(id, end.X, end.Y, 0, move.Charge ?? path.Points[move.Today.Count].Cost, move.Fired, move.Today, move.Attack is not null);
        }
        else session.ActInPlace(id, move.Attack is not null, move.Charge ?? 0);
        long charge = before - session.Map.Stacks[id].Move;
        if (move.Attack is { } foe && !session.EventBattleWaiting)
        {
            var battle = session.StartBattle(id, foe);
            bool ambush = Same(battle.DefenderGroup, id);
            var fight = new D2.Data.Battle.BattleSession(battle, new D2.Rules.Battle.BattleSim(battle, session.Rng, session.Book!.Rules.Constants),
                                                         session.Book!.Rules.Constants) { Auto = true };
            fight.Resolve();
            session.FinishBattle(fight.Log);
            return (charge, true, ambush, move.Today.Count);
        }
        // Мешок или своя крепость у конца пути, дошёл сегодня и без обрыва — вход (MapView.Move: _attackTarget у цели).
        if (!move.Cut && move.Today.Count == path.Points.Count - 1 && session.Map.Plan.TryGetValue(aim, out var ids)
            && ids.FirstOrDefault(i => session.Map.Sites.Any(f => Same(f.Id, i) && f.Kind is SiteKind.Bag or SiteKind.Village or SiteKind.Capital)) is { } site)
            session.Enter(id, site);
        return (charge, false, false, move.Today.Count);
    }

    /// <summary>Полоса полигонов карты 13 (x &lt; 40, y &lt; 72 — без трубок ИИ): у каждого отряда клетка, очки хода и невидимость порта против
    /// сохранения оригинала; отряд только у одного из них — расхождение.</summary>
    private static List<string> Path3StripDiffs(GameSession session, string sg)
    {
        var engine = MapLooksLoader.Load(sg);
        string name = Path.GetFileName(sg);
        static bool InStrip(MapStack s) => s.X < 40 && s.Y < 72 && s.Inside.Length == 0;
        string Row(MapStack s, MapLooks looks) =>
            $"({s.X},{s.Y}) ход {s.Move}{(looks.Stacks.FirstOrDefault(l => Same(l.Id, s.Id)) is { Invisible: true } ? " невидим" : "")}";
        var diffs = new List<string>();
        foreach (var e in engine.Map.Stacks.Values.Where(InStrip))
        {
            string want = Row(e, engine);
            string got = session.Map.Stacks.TryGetValue(e.Id, out var p) ? Row(p, session.Looks) : "нет";
            if (want != got) diffs.Add($"{name} {e.Id}: движок {want}, порт {got}");
        }
        foreach (var p in session.Map.Stacks.Values.Where(InStrip).Where(p => !engine.Map.Stacks.ContainsKey(p.Id)))
            diffs.Add($"{name} {p.Id}: у движка нет, порт {Row(p, session.Looks)}");
        return diffs;
    }

    /// <summary>
    /// CA1/CA2 — охотники Кланов в трубках гор после хода ИИ дня 1, порт с сохранения оригинала t01-017 против t02-018. Движок: CA1 — обрыв
    /// событием на (11,76) (списано 2), второй выбор <c>AttackStack</c> — встал на (12,76) перед невидимкой (13,76), списано 8 (конец списка без
    /// клетки невидимки), засада, невидимка погибла; CA2 — обрыв на (12,82) вплотную к невидимке (13,82) (списано 4), второй выбор — команда без
    /// шага: списано 6, засада (seq 855759–855776). Оба после дня — с нулём очков хода. Порт до 18.10 вторую команду CA2 не исполнял — охотник
    /// стоял с 16 очками, невидимка жила.
    /// </summary>
    [SkippableFact]
    public void Path3_AiHunters_LikeTheOriginalSave()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        Skip.IfNot(File.Exists(Path3Journal), $"нет {Path3Run}");
        var facts = Facts(Path3Map);
        string human = Str(facts.GetProperty("human"), "player");
        var (events, saves) = Path3Original();
        var last1 = saves.Last(s => s.Save.Turn == 1);
        var first2 = saves.First(s => s.Save.Turn == 2);
        var session = Path3Session(last1.Path, human);
        session.EndTurn();
        var engine = MapLooksLoader.Load(first2.Path).Map;
        var paid = events.OfType<ApRestoreEvent>().Where(r => r.Seq > last1.Save.Seq && r.Seq < first2.Save.Seq && r.Cause == "stack_move").ToList();
        foreach (string key in new[] { "CA1", "CA2" })
        {
            var z = Zone(facts, key);
            string hunter = Str(z, "stack"), hidden = Str(z, "hidden");
            var e = engine.Stacks[hunter];
            Assert.True(session.Map.Stacks.TryGetValue(hunter, out var p), $"{key}: охотника нет в порте");
            Out.WriteLine($"{key}: движок — ({e.X},{e.Y}) ход {e.Move}, списания {string.Join("+", paid.Where(r => Same(r.Stack, hunter)).Select(r => r.Amount))}, " +
                          $"невидимка {(engine.Stacks.ContainsKey(hidden) ? "жива" : "погибла")}; порт — ({p!.X},{p.Y}) ход {p.Move}, " +
                          $"невидимка {(session.Map.Stacks.ContainsKey(hidden) ? "жива" : "погибла")}");
            Assert.Equal((e.X, e.Y, e.Move), (p.X, p.Y, p.Move));
            Assert.Equal(engine.Stacks.ContainsKey(hidden), session.Map.Stacks.ContainsKey(hidden));
        }
    }
    /// <summary>
    /// MS — промахи в бою с анимацией (<c>BattleAnim</c> 1) по журналу оригинала: каждый бросок точности боя Рыцаря с Толстыми бесами (<c>miss</c>,
    /// два броска R250 <c>rng</c> после <c>rng_before</c>) решается формулой порта (<see cref="D2.Rules.Battle.Formulas.ShouldMiss"/>, среднее двух
    /// бросков больше точности — промах), и на каждый промах движок вывел надпись «Промах» над целью (<c>bat_text</c>). Партия 18.10: 45 бросков,
    /// 17 промахов (по точности: 50 — 13 из 23, 65 — 3 из 8, 70 — 0 из 7, 80 — 1 из 7), 17 надписей.
    /// </summary>
    [SkippableFact]
    public void Path3_MissBattle_MissesLikeTheOriginal()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        Skip.IfNot(File.Exists(Path3Journal), $"нет {Path3Run}");
        var z = Zone(Facts(Path3Map), "MS");
        var events = JournalCache.Read(Path3Journal, ["battle_start", "battle_end", "miss", "rng", "bat_text"]).OrderBy(e => e.Seq).ToList();
        var start = events.OfType<BattleStartEvent>().First(b => b.Round == 0 && Same(b.AttackerGroup, Str(z, "stack")));
        var end = events.OfType<BattleEndEvent>().First(b => b.Seq > start.Seq);
        var battle = events.Where(e => e.Seq > start.Seq && e.Seq < end.Seq).ToList();
        var rolls = battle.OfType<RngEvent>().Where(r => !r.Std).GroupBy(r => (r.Tid, r.N)).ToDictionary(g => g.Key, g => g.First());
        int checkedRolls = 0, misses = 0;
        var diffs = new List<string>();
        foreach (var m in battle.OfType<MissEvent>())
        {
            var r1 = rolls.GetValueOrDefault((m.Tid, m.RngBefore + 1));
            var r2 = rolls.GetValueOrDefault((m.Tid, m.RngBefore + 2));
            Assert.True(r1 is not null && r2 is not null, $"seq {m.Seq}: бросков точности нет в журнале");
            checkedRolls++;
            misses += m.Miss ? 1 : 0;
            if (D2.Rules.Battle.Formulas.ShouldMiss((int)m.Power, (int)r1!.R, (int)r2!.R) != m.Miss)
                diffs.Add($"seq {m.Seq}: точность {m.Power}, броски {r1.R} и {r2.R} — движок {(m.Miss ? "промах" : "попал")}");
        }
        int texts = battle.OfType<AvProbeEvent>().Count(t => t.Av.Ev == "bat_text" && (t.Av.S("text") ?? "").Contains("Промах", StringComparison.Ordinal));
        Out.WriteLine($"MS: бросков точности {checkedRolls}, промахов {misses}, надписей «Промах» {texts}, расхождений формулы {diffs.Count}");
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
        Assert.True(misses >= 10, $"промахов {misses}");
        Assert.Equal(misses, texts);
    }

    /// <summary>
    /// PRE1/PRE2 — стоящий в зоне события все три дня: у движка ни в одном сохранении после включения PRE1 воротами E07p (t01-011 и дальше) в
    /// журнале заданий нет ни PRE1, ни PRE2, окон <c>[PS-PRE1]</c>/<c>[PS-PRE2]</c> в днях 2–3 не было; у порта условие «в зоне» — по входу, у
    /// стоящего не срабатывает (<see cref="Path3_StandingInZone_AndAlly_ThreeDays"/>).
    /// </summary>
    [SkippableFact]
    public void Path3_StandingInZone_NeverFires_LikeTheOriginal()
    {
        Skip.IfNot(MapIds().Contains(Path3Map));
        Skip.IfNot(File.Exists(Path3Journal), $"нет {Path3Run}");
        var facts = Facts(Path3Map);
        string gate = Str(Zone(facts, "E07p"), "event"), pre1 = Str(Zone(facts, "PRE1"), "event"), pre2 = Str(Zone(facts, "PRE2"), "event");
        var (_, saves) = Path3Original();
        bool enabled = false;
        foreach (var (sg, save) in saves)
        {
            var log = EventLoader.LoadProgress(sg, EventLoader.Load(sg)).Log;
            enabled |= log.Any(l => Same(l.EventId, gate));
            Assert.DoesNotContain(log, l => Same(l.EventId, pre1) || Same(l.EventId, pre2));
            if (enabled) Out.WriteLine($"{Path.GetFileName(sg)} (день {save.Turn}): PRE1, PRE2 — нет");
        }
        Assert.True(enabled, "E07p в сохранениях не сработало");
        Assert.Contains(saves, s => s.Save.Turn == 3);
    }
}
