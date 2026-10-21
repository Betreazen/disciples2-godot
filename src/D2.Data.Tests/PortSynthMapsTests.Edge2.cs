using System.Text.Json;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 10 <c>port-synth-10-edge2</c> (<c>tools/synth-maps/maps_edge2.py</c>): ситуации памятки складываются в порте так, как
/// задумано, — ответ движка (нападёт ли команда на врага у конца пути, куда пойдёт ИИ мимо невидимки, кого усилит Травница) дадут журнал
/// и сохранения оригинала. Здесь проверяется только устройство полигонов, не спорное правило: его порт меняет по журналу.</summary>
public partial class PortSynthMapsTests
{
    private const string Edge2Map = "port-synth-10-edge2";

    /// <summary>Лидеры с <c>L_ROD</c> у Кланов, Легионов и Эльфов: Старейшина, Баронесса, Мудрец.</summary>
    private static readonly string[] RodBearers = ["g000uu0047", "g000uu0073", "g000uu8012"];

    /// <summary>VA1–VA3: путь к щелчку встаёт на клетке рядом с видимым врагом (<c>0x5cd38a</c>), враг виден человеку, объект вокруг клетки
    /// остановки (<c>0x5cd11f</c>, <see cref="PathFinder.HostileBeside"/>) — первый по таблице соседей: у VA2 — Гоблин (−1 по y), а не Гном.</summary>
    [SkippableFact]
    public void Edge2_VisibleEnemyBesidePath_PathStopsBesideIt()
    {
        Skip.IfNot(MapIds().Contains(Edge2Map));
        var facts = Facts(Edge2Map);
        var session = Begin(Edge2Map, 2);
        var fog = session.Map.Fog[session.Map.Players[session.ActiveHuman].FogId];
        foreach (string key in new[] { "VA1", "VA2", "VA3" })
        {
            var z = Zone(facts, key);
            var stack = session.Map.Stacks[Str(z, "stack")];
            var (cx, cy) = XY(z, "click_at");
            var path = session.PathTo(stack, cx, cy);
            var stop = path.Points[path.Reachable];
            var beside = PathFinder.HostileBeside(session.Map, stack, stop.X, stop.Y, (path.Points[^1].X, path.Points[^1].Y),
                (s, _) => !Same(s.Owner, stack.Owner));
            Out.WriteLine($"{key}: путь до ({cx},{cy}) встаёт на ({stop.X},{stop.Y}), рядом {beside}");
            Assert.Equal(XY(z, "stop_at"), (stop.X, stop.Y));
            Assert.Equal(z.GetProperty("beside")[0].GetString(), beside, StringComparer.OrdinalIgnoreCase);
            foreach (var at in z.GetProperty("beside_at").EnumerateArray())
                Assert.False(fog[at[0].GetInt32(), at[1].GetInt32()], $"{key}: враг у ({at[0]},{at[1]}) в тумане");
            // Команда, кончившаяся вплотную к врагу, нападает на него (0x5cd11f; журнал — PathObjectHarnessTests).
            Assert.Equal(beside, PlayerMove.Plan(session, stack.Id, path, (cx, cy)).Attack, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Партия оригинала с картой 10 (<c>passive-001.jsonl</c>: карты 11, 12, затем 10) и seq, с которого идёт карта 10 (первый
    /// щелчок AV — 3099730; карта 12 кончилась раньше).</summary>
    private const string Edge2Run = "20261017-port-synth";

    private const long Edge2FromSeq = 3_099_000;

    private static string Edge2Journal => Path.Combine(GameSaveTests.CorpusDir(), Edge2Run, "passive-001.jsonl");

    /// <summary>VA1–VA3 против журнала оригинала (<c>docs/map-path.md</c> §4, «Прицел щелчка»): пользователь щёлкал пустые клетки над врагом —
    /// (10,20) над Гоблином (11,21), (10,26) над Гномом (11,27), (11,32) над Гоблином (12,33). Движок целит в отряд под клеткой (<c>0x4d04c3</c>):
    /// путь к нему (<c>path</c> — длина, последняя достижимая, цель), повторный щелчок по той же клетке путь не подтверждает (<c>0x4cc8c2</c>),
    /// а щелчок по концу пути идёт и нападает (VA3, seq 3215905: встал на (11,33), бой с Гоблином). Порт до 17.10 вставал на щёлкнутую клетку
    /// и нападал на соседа (журнал порта <c>port-20261017-215431</c>: VA1 — встал на (10,20), бой).</summary>
    [SkippableFact]
    public void Edge2_ClickAboveEnemy_AimsAtIt_LikeTheOriginal()
    {
        Skip.IfNot(MapIds().Contains(Edge2Map));
        Skip.IfNot(File.Exists(Edge2Journal), $"нет {Edge2Run}");
        var facts = Facts(Edge2Map);
        var session = Begin(Edge2Map, 2);
        var events = D2.Data.Corpus.JournalCache.Read(Edge2Journal, ["map_click", "path"]).Where(e => e.Seq > Edge2FromSeq).OrderBy(e => e.Seq).ToList();
        foreach (string key in new[] { "VA1", "VA2", "VA3" })
        {
            var z = Zone(facts, key);
            var stack = session.Map.Stacks[Str(z, "stack")];
            // Первый показанный путь отряда в оригинале и щелчок, который его показал.
            var shown = events.OfType<D2.Data.Corpus.PathEvent>().First(p => Same(p.Stack, stack.Id));
            var click = events.OfType<D2.Data.Corpus.MapClickEvent>().Last(c => c.Seq < shown.Seq);
            (int X, int Y) at = ((int)click.Cell!.X, (int)click.Cell.Y);
            var aim = session.ClickAim(stack, at.X, at.Y);
            var path = session.PathTo(stack, aim.X, aim.Y);
            var last = path.Points[path.Reachable];
            Out.WriteLine($"{key}: щелчок {at}; движок — цель ({shown.EndX},{shown.EndY}), длина {shown.Len}, встанет ({shown.LastX},{shown.LastY}); " +
                          $"порт — цель {aim}, длина {path.Points.Count}, встанет ({last.X},{last.Y})");
            Assert.Equal(((int)shown.EndX, (int)shown.EndY), aim);
            Assert.Equal(shown.Len, path.Points.Count);
            Assert.Equal(((int)shown.LastX, (int)shown.LastY), (last.X, last.Y));
            Assert.False(session.ClickConfirms(stack, path, aim, at.X, at.Y), $"{key}: повторный щелчок по {at} подтвердил путь");
            Assert.True(session.ClickConfirms(stack, path, aim, last.X, last.Y), $"{key}: щелчок по концу пути не подтвердил");
            var foe = session.Map.Plan[aim].Single(id => session.Map.Stacks.ContainsKey(id));
            Assert.Equal(foe, PlayerMove.Plan(session, stack.Id, path, aim).Attack, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>AI1/AI2 — списание хода сквозь невидимку (<see cref="StackPath.Charge"/>, <c>0x5cd0e9</c>, <c>0x5cc3d2</c>): охотники Кланов встают перед
    /// невидимкой (AI1 — (12,45), AI2 — (12,50)), и на них нападает невидимый, но списано не за две пройденные клетки, а до последней достижимой
    /// пути ИИ без клетки невидимки — у движка 10 и 12 (<c>ap_restore</c> <c>stack_move</c> seq 3361321, 3366873), затем 10 за нападение, и запаса
    /// нет: оба после боя в день 1 стоят (следующий выбор — другой отряд), к приманке идут в день 2. Порт до 17.10 списывал 4 и после засады
    /// шёл дальше — на (15,45) и (15,50). Сверка: клетки охотников после хода ИИ дня 1 — сохранение оригинала дня 2.</summary>
    [SkippableFact]
    public void Edge2_AiHuntersPastHidden_ChargeAndStopLikeTheOriginal()
    {
        Skip.IfNot(MapIds().Contains(Edge2Map));
        string day2 = Path.Combine(GameSaveTests.CorpusDir(), Edge2Run, "port-synth-10-edge2-undead-t02-050.sg");
        Skip.IfNot(File.Exists(day2) && File.Exists(Edge2Journal), $"нет {Edge2Run}");
        var facts = Facts(Edge2Map);
        var session = Begin(Edge2Map, 2);
        var engine = D2.Data.Map.MapLooksLoader.Load(day2).Map;
        var paid = D2.Data.Corpus.JournalCache.Read(Edge2Journal, "ap_restore").OfType<D2.Data.Corpus.ApRestoreEvent>()
                     .Where(r => r.Seq > Edge2FromSeq && r.Cause == "stack_move").ToList();
        foreach (string key in new[] { "AI1", "AI2" })
        {
            var z = Zone(facts, key);
            var hunter = session.Map.Stacks[Str(z, "stack")];
            var (bx, by) = XY(z, "target_at");
            var (path, attack) = session.CommandPath(hunter, session.PathTo(hunter, bx, by), (bx, by), other => !Same(other.Owner, hunter.Owner));
            long engineCharge = paid.First(r => Same(r.Stack, hunter.Id)).Amount;
            Out.WriteLine($"{key}: команда встанет на ({path.Points[path.Reachable].X},{path.Points[path.Reachable].Y}), нападёт {attack}, " +
                          $"списание порта {path.ChargeFor(path.Reachable)}, движка {engineCharge}");
            Assert.Equal(Str(z, "hidden"), attack, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(engineCharge, path.ChargeFor(path.Reachable));
        }
        session.EndTurn();
        foreach (string key in new[] { "AI1", "AI2" })
        {
            string id = Str(Zone(facts, key), "stack");
            var e = engine.Stacks[id];
            Assert.True(session.Map.Stacks.TryGetValue(id, out var p), $"{key}: охотника нет в порте");
            Out.WriteLine($"{key}: охотник после дня 1 — движок ({e.X},{e.Y}), порт ({p!.X},{p.Y})");
            Assert.Equal((e.X, e.Y), (p.X, p.Y));
            Assert.False(engine.Stacks.ContainsKey(Str(Zone(facts, key), "hidden")));
        }
    }

    /// <summary>BI2: в отряде только Баньши со Сферой грома и Травница — Баньши единственный кандидат усиления, и усилить её нельзя ни в порте
    /// (<see cref="Attacks.HasDamageAttack"/> — своя первая и вторая атаки), ни в оригинале: журнал 20261017-port-synth, ход Травницы в первом
    /// раунде боя с Гномом — курсор над полем всё время <c>CUDEFAULTA</c> (усиление дало бы <c>CUHEALA</c>), щелчки по Баньши ничего не дали,
    /// ход кончился «Защитой» (<c>BTN_DEFEND</c>, seq 3119217); слова пользователя — «нельзя улучшить атаку Баньши». Прежняя запись разбора
    /// (вторая строка списка атак <c>0x644815</c> берёт и атаку предмета, ранг 2) журналом не подтвердилась.</summary>
    [SkippableFact]
    public void Edge2_Boost_BansheeIsTheOnlyCandidate()
    {
        Skip.IfNot(MapIds().Contains(Edge2Map));
        var facts = Facts(Edge2Map);
        var session = Begin(Edge2Map, 2);
        var state = EdgeBattle(session, Zone(facts, "BI2"), 1);
        var fight = Fight(state, 1, session.Book!);
        var ours = state.Units.Where(u => u.Attacker).ToList();
        Assert.Equal(2, ours.Count);
        var herbalist = Assert.Single(ours, u => u.Attack.Class == AttackClass.BoostDamage);
        var banshee = Assert.Single(ours, u => u.Type.Id.Equals("g000uu0099", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(banshee.BattleItems);
        for (int step = 0; step < 50 && !ReferenceEquals(fight.Current, herbalist); step++) fight.AiStep();
        Assert.Same(herbalist, fight.Current);
        var targets = fight.Targets();
        Out.WriteLine("цели Травницы: " + (targets.Count == 0 ? "нет" : string.Join(", ", targets.Select(t => t.Type.Id))));
        Assert.Empty(targets);
        if (!File.Exists(Edge2Journal)) return;
        // Оригинал: от начала боя BI2 до конца хода Травницы — ни одного курсора усиления, ход — «Защита».
        var journal = D2.Data.Corpus.JournalCache.Read(Edge2Journal, ["battle_start", "turn_after", "cursor", "ui_click"])
                        .Where(e => e.Seq > Edge2FromSeq).OrderBy(e => e.Seq).ToList();
        var start = journal.OfType<D2.Data.Corpus.BattleStartEvent>().First(b => b.Round == 0 && Same(b.AttackerGroup, Str(Zone(facts, "BI2"), "stack")));
        var done = journal.OfType<D2.Data.Corpus.TurnEvent>().First(t => t.Seq > start.Seq && t.After && Same(t.Unit, herbalist.Id));
        var during = journal.Where(e => e.Seq > start.Seq && e.Seq < done.Seq).ToList();
        var cursors = during.OfType<D2.Data.Corpus.AvProbeEvent>().Where(e => e.Av.Ev == "cursor").Select(e => e.Av.S("name")).ToList();
        Out.WriteLine($"оригинал, ход Травницы: курсоров {cursors.Count} ({string.Join(", ", cursors.Distinct())}), кнопки " +
                      string.Join(", ", during.OfType<D2.Data.Corpus.UiClickEvent>().Select(u => u.Element)));
        Assert.NotEmpty(cursors);
        Assert.DoesNotContain(cursors, n => n is not null && n.StartsWith("CUHEAL", StringComparison.Ordinal));
        Assert.Contains(during.OfType<D2.Data.Corpus.UiClickEvent>(), u => u.Element == "BTN_DEFEND");
    }

    /// <summary>R8b: невидимый идёт сквозь зону события радиуса 1 — путь обрывается на первой клетке зоны, отряд раскрыт; R11b: вор у невидимого
    /// нейтрала — действий нет, бой, нападает невидимка (<c>0x4407c2</c>, <c>0x433a57</c>).</summary>
    [SkippableFact]
    public void Edge2_InvisibleCutInZone_AndThiefAtInvisibleFights()
    {
        Skip.IfNot(MapIds().Contains(Edge2Map));
        var facts = Facts(Edge2Map);
        var session = Begin(Edge2Map, 2);
        var r8 = Zone(facts, "R8b");
        string id = Str(r8, "stack");
        Assert.True(session.IsInvisible(id));
        var (cx, cy) = XY(r8, "click_at");
        var path = session.PathTo(session.Map.Stacks[id], cx, cy);
        var move = PlayerMove.Plan(session, id, path, (cx, cy));
        Assert.True(move.Cut);
        Assert.Contains(move.Fired, f => Same(f.Event.Id, Str(r8, "event")));
        var end = move.Today[^1];
        Assert.Equal(XY(r8, "stop_at"), (end.X, end.Y));
        session.MoveStack(id, end.X, end.Y, 0, path.Points[move.Today.Count].Cost, move.Fired, move.Today);
        Assert.False(session.IsInvisible(id));
        // Оригинал (сохранения дня 1 карты 10): первое, где R8b уже на (10,38), — он там видим, как у порта (окно [PS-R8b] — DLG_EVENT_POPUP).
        string runDir = Path.Combine(GameSaveTests.CorpusDir(), Edge2Run);
        if (Directory.Exists(runDir))
        {
            var after = Directory.GetFiles(runDir, "port-synth-10-edge2-*-t01-*.sg").Order(StringComparer.Ordinal)
                                 .Select(D2.Data.Map.MapLooksLoader.Load)
                                 .FirstOrDefault(l => l.Map.Stacks.TryGetValue(id, out var s) && (s.X, s.Y) == (end.X, end.Y));
            Assert.NotNull(after);
            Assert.False(after.Stacks.Single(s => Same(s.Id, id)).Invisible, "R8b у движка остался невидимым");
            Out.WriteLine($"R8b: движок — ({end.X},{end.Y}), видим; порт — так же");
        }

        var r11 = Zone(facts, "R11b");
        string thief = Str(r11, "stack"), guard = Str(r11, "target");
        Assert.True(session.IsInvisible(guard));
        // Правило — как у карты 8 (Edge_HiddenStack_PathRunsThrough_ItAmbushes, 0x4407c2; засада — журнал 20261016g R10).
        Assert.Empty(session.ThiefActions(thief, guard));
        var fight = session.StartBattle(thief, guard);
        Assert.Equal(guard, fight.AttackerGroup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>AI1: охотник Кланов (война с Нежитью, приказ «атаковать отряд» на раненого Рыцаря) стоит в трубке гор — к цели ведёт только
    /// трубка, и в ней невидимый отряд человека: путь без учёта невидимки идёт через его клетку. AI2: путь охотника по полю проходит рядом с
    /// невидимкой, не через неё. Что сделает ИИ — ответ движка (здесь только печать хода ИИ порта).</summary>
    [SkippableFact]
    public void Edge2_AiHunters_PathOnlyThroughTheHiddenStack()
    {
        Skip.IfNot(MapIds().Contains(Edge2Map));
        var facts = Facts(Edge2Map);
        var session = Begin(Edge2Map, 2);
        var map = session.Map;
        var war = facts.GetProperty("war");
        string clans = Str(war, "player");
        Assert.True(map.Relations.Single(r => r.Pairs(3, 1)).Current <= 33, "Кланы и Нежить не в войне");
        var ai1 = Zone(facts, "AI1");
        var tube = ai1.GetProperty("tube");
        int ty = tube.GetProperty("y").GetInt32(), x0 = tube.GetProperty("x0").GetInt32(), x1 = tube.GetProperty("x1").GetInt32();
        for (int x = x0 - 1; x <= x1 + 1; x++)
            foreach (int y in new[] { ty - 1, ty + 1 })
                Assert.Equal(Ground.Mountain, map.Tiles[x, y].Ground);
        Assert.Equal(Ground.Mountain, map.Tiles[x0 - 1, ty].Ground);
        Assert.Equal(Ground.Mountain, map.Tiles[x1 + 1, ty].Ground);
        foreach (string key in new[] { "AI1", "AI2" })
        {
            var z = Zone(facts, key);
            var hunter = map.Stacks[Str(z, "stack")];
            Assert.Equal(clans, hunter.Owner, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(StackOrder.AttackStack, (StackOrder)hunter.Order);
            Assert.Equal(Str(z, "target"), hunter.OrderTarget, StringComparer.OrdinalIgnoreCase);
            Assert.True(session.IsInvisible(Str(z, "hidden")));
            var (hx, hy) = XY(z, "hidden_at");
            var (bx, by) = XY(z, "target_at");
            // Устройство: AI1 — невидимка в трубке между охотником и приманкой (другого пути нет — стены выше), AI2 — в поле, рядом с прямой
            // охотник → приманка. Путь, который строит порт, только печатается: путь ИИ сквозь невидимку — правило, его сверят с журналом.
            if (key == "AI1") Assert.True(hy == ty && hy == hunter.Y && hy == by && hunter.X < hx && hx < bx, $"{key}: невидимка не в трубке между ними");
            else Assert.True(hunter.Y == by && Math.Abs(hy - by) == 1 && hunter.X < hx && hx < bx, $"{key}: невидимка не рядом с прямой");
            Out.WriteLine($"{key}: путь интерфейса порта {string.Join(" ", session.PathTo(hunter, bx, by).Points.Select(p => (p.X, p.Y)))}");
        }
        session.EndTurn();
        foreach (string key in new[] { "AI1", "AI2" })
        {
            var z = Zone(facts, key);
            string where = session.Map.Stacks.TryGetValue(Str(z, "stack"), out var h) ? $"({h.X},{h.Y})" : "нет";
            Out.WriteLine($"{key} после хода ИИ порта: охотник {where}, приманка {(session.Map.Stacks.ContainsKey(Str(z, "target")) ? "жива" : "нет")}, " +
                          $"невидимка {(session.Map.Stacks.ContainsKey(Str(z, "hidden")) ? "есть" : "нет")}");
        }
    }

    /// <summary>У столиц ИИ (<c>ATTITUDE</c> 4/5/6 — веса жезла 0): жезлоносец с <c>L_ROD</c> в трёх клетках от двух жил на ничьей земле,
    /// берсерк с двумя целями, обычный отряд у нейтрала-берсерка; за три дня у каждого из них есть выбор тактики (печатаются виды).</summary>
    [SkippableFact]
    public void Edge2_AiCorners_RodBezerkAndAttackOnBezerk()
    {
        Skip.IfNot(MapIds().Contains(Edge2Map));
        var facts = Facts(Edge2Map);
        var session = Begin(Edge2Map, 2);
        var trace = new PickTrace();
        session.AiTrace = trace;
        var map = session.Map;
        var corners = facts.GetProperty("zones").EnumerateArray().Where(z => Str(z, "key").StartsWith("AI-", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, corners.Count);
        foreach (var z in corners)
        {
            Assert.Equal(z.GetProperty("attitude").GetInt32(), map.Players[Str(z, "player")].Attitude);
            var planter = map.Stacks[Str(z, "planter")];
            Assert.Contains(map.Units[planter.LeaderId].TypeId, RodBearers, StringComparer.OrdinalIgnoreCase);   // L_ROD (GLabi 13)
            foreach (var at in z.GetProperty("crystals_at").EnumerateArray())
            {
                int x = at[0].GetInt32(), y = at[1].GetInt32();
                Assert.Equal(Economy.TerrainOfRace(4), map.Tiles[x, y].Terrain);   // ничья земля
                Assert.True(Math.Max(Math.Abs(x - planter.X), Math.Abs(y - planter.Y)) <= 3);
            }
            Assert.Equal(StackOrder.Bezerk, (StackOrder)map.Stacks[Str(z, "bezerk")].Order);
            Assert.Equal(StackOrder.Bezerk, (StackOrder)map.Stacks[Str(z, "neutral_bezerk")].Order);
        }
        for (int k = 0; k < 3; k++) session.EndTurn();
        foreach (var z in corners)
            foreach (string role in new[] { "planter", "bezerk", "normal" })
            {
                var picks = trace.Picks.Where(p => Same(p.Stack, Str(z, role))).Select(p => $"д{p.Day} {p.Action}").ToList();
                Out.WriteLine($"{Str(z, "key")} {role}: {string.Join(", ", picks)}");
                Assert.NotEmpty(picks);
            }
    }
}
