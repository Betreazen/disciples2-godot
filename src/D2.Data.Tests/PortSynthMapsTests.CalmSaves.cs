using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using D2.Rules.Units;

namespace D2.Data.Tests;

/// <summary>Карта 16 против партии оригинала 19.10 (<c>corpus/20261018b-port-synth</c>, сохранения t01-001 … t15-015, зонд 7.5).</summary>
public partial class PortSynthMapsTests
{
    private const string CalmRun = "20261018b-port-synth";

    /// <summary>Сохранений карты 16 в партии: начало каждого из 15 дней (человек только жмёт «Конец хода»).</summary>
    private const int CalmSaveCount = 15;

    /// <summary>
    /// Партия карты 16 портом с начала сценария (человек ничего не делает) против оригинала — выбор в выбор и день в день:
    /// каждый выбор тактики (<c>ai_tactic_pick</c> фазы 4 → 0: отряд, действие, цель, клетка, счёт) против выбора порта, каждый шаг
    /// стратегии (<c>ai_strategic_step</c> + <c>ai_exec</c>: игрок, день, проход, тип, цель — постройка, тип найма, повышение, заклинание,
    /// дипломатия), каждый жезл (<c>rod_drop</c>: хозяин и клетка; цена — в казне сохранений) и все 15 сохранений начала дня (<see cref="CalmRows"/>: клетки, ход,
    /// здоровье, опыт, типы юнитов, модификаторы, казна, гарнизоны, предметы, мешки, постройки, заклинания, жезлы, товары лавок).
    /// <para>Ответ движка 19.10: 96 выборов (TakeBag 21, HireUnit 21, AssistMove 16, PlantRod 14, VisitMerchant 14, VisitMage 5,
    /// VisitTrainer 5), 85 шагов стратегии (повышение 27, найм 30, постройки 15, изучение 4, дипломатия 9), 6 жезлов; боёв нет. Живая
    /// партия порта <c>port-20261019-144812</c> дала те же 96 выборов и 85 шагов (<c>docs/synthetic-maps-port/analysis/20261019-map16.md</c>).</para>
    /// </summary>
    [SkippableFact]
    public void Calm_PlayMatchesOriginalJournalAndSaves()
    {
        Skip.IfNot(MapIds().Contains(CalmMap));
        var (journal, saves) = CalmSaves();
        Assert.Equal(CalmSaveCount, saves.Count);
        long to = saves[^1].Seq;
        var events = JournalCache.Read(journal, ["ai_turn", "ai_tactic_pick", "ai_strategic_step", "ai_exec", "rod_drop", "battle_start",
                                                    "skill_pick"])
                                 .Where(e => e.Seq < to).OrderBy(e => e.Seq).ToList();
        Assert.Empty(events.OfType<BattleStartEvent>());
        var (enginePicks, engineSteps, engineRods) = CalmJournal(events);

        var session = Begin(CalmMap, 2);
        // Человек в начале дня 1 выбирает 9 навыков героя (SUGG_LVL 10) — их порт повторяет из журнала до сохранения t01.
        string human = Str(Facts(CalmMap).GetProperty("human"), "player");
        var skills = new Queue<SkillPickEvent>(events.OfType<SkillPickEvent>()
            .Where(k => k.Ret && session.Map.Stacks.TryGetValue(k.Stack, out var st) && Same(st.Owner, human)));
        var trace = new CalmTrace();
        session.AiTrace = trace;
        var seen = new Recorder();
        session.Watch(seen);
        var failed = new List<string>();
        var before = MapLooksLoader.Load(saves[0].Path);
        foreach (var save in saves)
        {
            while (session.Map.Turn < save.Turn && !session.Over) session.EndTurn();
            while (skills.Count > 0 && skills.Peek().Seq < save.Seq)
            {
                var k = skills.Dequeue();
                Assert.Equal(SkillRefusal.None, session.ChooseSkill(k.Stack, k.Modif));
            }
            var engine = MapLooksLoader.Load(save.Path);
            var rows = CalmRows(before, engine, session);
            Out.WriteLine($"t{save.Turn:D2}: {(rows.Count == 0 ? "совпало" : string.Join("; ", rows))}");
            failed.AddRange(rows.Select(r => $"t{save.Turn:D2}: {r}"));
            before = engine;
        }
        Assert.Empty(seen.Outcomes);
        Assert.Empty(skills);   // все навыки героя взяты в начале дня 1, до сохранения t01 (порядок «конец хода, затем навыки» не понадобился)

        int picksSame = Matched(enginePicks, trace.Picks, "выбор", failed);
        int stepsSame = Matched(engineSteps, trace.Steps, "шаг", failed);
        // Жезл движка (rod_drop) — тот, что стоит у порта в той же клетке того же хозяина; цена — в казне сохранений.
        var portRods = session.Map.Rods.Values.Select(r => (Up(r.Owner), r.X, r.Y)).ToHashSet();
        int rodsSame = engineRods.Count(r => portRods.Contains((Up(r.Player), r.X, r.Y)));
        if (rodsSame != engineRods.Count || portRods.Count != engineRods.Count)
            failed.Add($"жезлы: у движка {engineRods.Count} ({string.Join(" ", engineRods.Select(r => $"{r.Stack}@{r.X},{r.Y}"))}), у порта {portRods.Count}, совпало {rodsSame}");
        Out.WriteLine($"выборов {picksSame}/{enginePicks.Count} (порт {trace.Picks.Count}): " +
                      string.Join(", ", enginePicks.GroupBy(p => p.Action).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        Out.WriteLine($"шагов стратегии {stepsSame}/{engineSteps.Count} (порт {trace.Steps.Count}): " +
                      string.Join(", ", engineSteps.GroupBy(s => s.Type).Select(g => $"{g.Key} {g.Count()}")));
        Out.WriteLine($"жезлов {rodsSame}/{engineRods.Count}");
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed.Take(40)));
    }

    /// <summary>
    /// Выбор навыка лидером (<c>0x5d6e21</c>, и у человека, и у ИИ — <c>skill_pick</c> с <c>ret_addr</c> <c>0x5d6e6e</c>): после
    /// <c>visitorLeaderUpgrade</c> сервер смотрит элементы модификатора — обзор открывает туман вокруг отряда (<c>0x5d52fe</c>), запас хода
    /// встаёт на новый наибольший (<c>0x5e780d</c> с «текущий − наибольший»), здоровье лидера — на новое наибольшее (<c>0x5e75f3</c>).
    /// Журнал карты 16: герой человека взял G000UM4004 (+20 % хода) — <c>ap_restore</c> <c>cause skill_pick</c> 34 → 40 (у порта было 34).
    /// </summary>
    [SkippableFact]
    public void Calm_SkillPick_RefreshesMoveHpAndSight()
    {
        Skip.IfNot(MapIds().Contains(CalmMap));
        var session = Begin(CalmMap, 2);
        string human = Str(Facts(CalmMap).GetProperty("human"), "player");
        var hero = session.Map.Stacks.Values.Single(s => Same(s.Owner, human));
        int move = hero.Move, hp = session.Map.Units[hero.LeaderId].Hp;
        int scout = session.ScoutRange(hero);
        Assert.Equal(SkillRefusal.None, session.ChooseSkill(hero.Id, "G000UM4004"));   // MoveAllowance +20 %
        Assert.True(session.Map.Stacks[hero.Id].Move > move, $"ход {move} → {session.Map.Stacks[hero.Id].Move}");
        Assert.Equal(40, session.Map.Stacks[hero.Id].Move);
        Assert.Equal(SkillRefusal.None, session.ChooseSkill(hero.Id, "G000UM4017"));   // Hp +20 %
        Assert.True(session.Map.Units[hero.LeaderId].Hp > hp, $"здоровье {hp} → {session.Map.Units[hero.LeaderId].Hp}");
        Assert.Equal(SkillRefusal.None, session.ChooseSkill(hero.Id, "G000UM4015"));   // ScoutingRange +3
        var now = session.Map.Stacks[hero.Id];
        int wider = session.ScoutRange(now);
        Out.WriteLine($"ход {move} → {now.Move}, здоровье {hp} → {session.Map.Units[hero.LeaderId].Hp}, обзор {scout} → {wider}");
        Assert.Equal(scout + 3, wider);
        // Туман открыт по новому обзору (0x5d52fe): крайние клетки креста радиуса — открыты (у столицы человека они и так открыты её
        // обзором, поэтому сверяется итог, а не прирост).
        var fog = session.Map.Fog[session.Map.Players[human].FogId];
        foreach (var (dx, dy) in new[] { (wider, 0), (-wider, 0), (0, wider), (0, -wider) })
            if (session.Map.Contains(now.X + dx, now.Y + dy)) Assert.False(fog[now.X + dx, now.Y + dy], $"({now.X + dx},{now.Y + dy}) закрыта");
    }

    /// <summary>Сколько строк двух списков совпало по порядку; расхождения (и лишние/недостающие) — в <paramref name="failed"/>.</summary>
    private static int Matched<T>(IReadOnlyList<T> engine, IReadOnlyList<T> port, string what, List<string> failed)
    {
        int same = 0;
        for (int k = 0; k < Math.Max(engine.Count, port.Count); k++)
        {
            var e = k < engine.Count ? engine[k] : default;
            var p = k < port.Count ? port[k] : default;
            if (Equals(e, p)) same++;
            else failed.Add($"{what} #{k}: движок {e?.ToString() ?? "нет"}, порт {p?.ToString() ?? "нет"}");
        }
        return same;
    }

    /// <summary>Выборы тактики, шаги стратегии и жезлы движка по журналу карты 16 (день — <c>ai_turn begin</c> игрока).</summary>
    private static (List<CalmPick> Picks, List<CalmStep> Steps, List<CalmRod> Rods) CalmJournal(IReadOnlyList<CorpusEvent> events)
    {
        var picks = new List<CalmPick>();
        var steps = new List<CalmStep>();
        var rods = new List<CalmRod>();
        var day = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AiExecEvent? exec = null;
        foreach (var e in events)
            switch (e)
            {
                case AiTurnEvent { Phase: "begin", Player: { } p } t:
                    day[p] = (int)t.Turn;
                    break;
                case AiTacticPickEvent { PhaseBefore: 4, Chosen: [{ Action: { } a, Score: { } score }, ..], Player: { } p }:
                    // Смена приказа (CAiChangeOrderAction) — зонд читает слот типа как 0x1b; порт держит её отдельным номером.
                    var type = a.Vft == "0x6cfbec" ? AiActionType.ChangeOrder : (AiActionType)(a.Type ?? -1);
                    picks.Add(new(p, day[p], Up(a.Stack), type, Up(a.Target), (int)(a.Point?.X ?? -1), (int)(a.Point?.Y ?? -1), Math.Round(score, 2)));
                    break;
                case AiExecEvent { Ret: true } x:
                    exec = x;
                    break;
                case AiStrategicStepEvent { Player: { } p } s when exec is { Action: { } act }:
                    string Field(string k) => act.Fields.TryGetValue(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? Up(v.GetString()) : "";
                    int Number(string k) => act.Fields.TryGetValue(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : -1;
                    var stepType = (AiStepType)s.Type;
                    string target = stepType switch
                    {
                        AiStepType.HireUnit => Field("id12"),
                        // Найм лидера (0x44b09f): тип — id12, подприказ ИИ — cat24 (у порта цель «тип|приказ»).
                        AiStepType.HireLeader => $"{Field("id12")}|{Number("cat24")}",
                        AiStepType.Diplomacy => Field("id4"),
                        _ => Field("id8"),
                    };
                    steps.Add(new(p, (int)s.Turn, (int)exec.Pass, stepType, target));
                    exec = null;
                    break;
                case RodDropEvent { Ret: true } r:
                    rods.Add(new(r.Player, Up(r.Stack), (int)r.X, (int)r.Y));
                    break;
            }
        return (picks, steps, rods);
    }

    private static string Up(string? id) => Id(id).ToUpperInvariant();

    /// <summary>Строки расхождений карты 16 на сохранении начала дня: общие признаки <see cref="Differences"/> (туман
    /// всех игроков, ход, здоровье, опыт, модификаторы, казна, гарнизоны, предметы, мешки), клетки и «внутри», типы юнитов, постройки, заклинания, жезлы, товары площадок.</summary>
    private static List<string> CalmRows(MapLooks beforeLooks, MapLooks engineLooks, GameSession port)
    {
        var rows = Differences(beforeLooks, engineLooks, port);
        var before = beforeLooks.Map;
        var engine = engineLooks.Map;
        var map = port.Map;
        static string Ids(IEnumerable<string> ids) => string.Join("+", ids.Select(i => i.ToUpperInvariant()).Order(StringComparer.Ordinal));
        static string Cell(MapState m, string id) => m.Stacks.TryGetValue(id, out var s)
            ? s.Inside.Length > 0 ? $"в {s.Inside.ToUpperInvariant()}" : $"({s.X},{s.Y})" : "нет";
        Diff(rows, "клетка", engine.Stacks.Values, s => s.Id, s => Cell(before, s.Id), s => Cell(engine, s.Id), s => Cell(map, s.Id));
        Diff(rows, "тип юнита", engine.Units.Values, u => u.Id, u => before.Units.GetValueOrDefault(u.Id)?.TypeId.ToUpperInvariant(),
             u => u.TypeId.ToUpperInvariant(), u => map.Units.GetValueOrDefault(u.Id)?.TypeId.ToUpperInvariant());
        static string Slots(MapState m, string id) => m.Stacks.TryGetValue(id, out var s) ? Ids(s.UnitIds) : "нет";
        Diff(rows, "состав", engine.Stacks.Values, s => s.Id, s => Slots(before, s.Id), s => Slots(engine, s.Id), s => Slots(map, s.Id));
        Diff(rows, "постройки", engine.Players.Values, p => p.Id, p => Ids(before.Players[p.Id].Buildings), p => Ids(p.Buildings),
             p => map.Players.TryGetValue(p.Id, out var q) ? Ids(q.Buildings) : "нет");
        Diff(rows, "заклинания", engine.Players.Values, p => p.Id, p => Ids(before.Players[p.Id].KnownSpells), p => Ids(p.KnownSpells),
             p => map.Players.TryGetValue(p.Id, out var q) ? Ids(q.KnownSpells) : "нет");
        Diff(rows, "казна заклинаний", engine.Players.Values, p => p.Id, p => before.Players[p.Id].SpellBank, p => p.SpellBank,
             p => map.Players.GetValueOrDefault(p.Id)?.SpellBank);
        static string Rods(MapState m) => Ids(m.Rods.Values.Select(r => $"{r.Owner}@{r.X},{r.Y}"));
        Diff(rows, "жезлы", [engine], _ => "карта", _ => Rods(before), Rods, _ => Rods(map));
        static string Stock(MapState m, string site) => m.Sites.FirstOrDefault(x => Same(x.Id, site)) is { } f ? Ids(f.Items) : "нет";
        Diff(rows, "товары", engine.Sites.Where(x => x.Kind is not (SiteKind.Bag or SiteKind.Rod)), x => x.Id, x => Stock(before, x.Id), x => Stock(engine, x.Id),
             x => Stock(map, x.Id));
        static string Owner(MapState m, string site) => m.Sites.FirstOrDefault(x => Same(x.Id, site))?.Owner.ToUpperInvariant() ?? "нет";
        Diff(rows, "хозяин", engine.Sites.Where(x => x.Kind is not (SiteKind.Bag or SiteKind.Rod)), x => x.Id, x => Owner(before, x.Id), x => Owner(engine, x.Id),
             x => Owner(map, x.Id));
        return rows;
    }

    /// <summary>Журнал 19.10 и сохранения карты 16 на его оси (<see cref="CheckJournal"/>, по <c>seq</c>).</summary>
    private static (string Journal, List<SaveCopy> Saves) CalmSaves()
    {
        string journal = Path.Combine(GameSaveTests.CorpusDir(), CalmRun, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), $"нет {CalmRun}");
        var saves = CheckJournal.Load(journal).Saves
                                .Where(s => Path.GetFileName(s.Path).StartsWith(CalmMap + "-", StringComparison.OrdinalIgnoreCase)).ToList();
        Skip.If(saves.Count < 2, "нет сохранений карты 16");
        return (journal, saves);
    }

    private sealed record CalmPick(string Player, int Day, string Stack, AiActionType Action, string Target, int X, int Y, double Score);

    private sealed record CalmStep(string Player, int Day, int Pass, AiStepType Type, string Target);

    private sealed record CalmRod(string Player, string Stack, int X, int Y);

    /// <summary>Выборы тактики и шаги стратегии порта в форме журнала движка (<see cref="CalmJournal"/>).</summary>
    private sealed class CalmTrace : IAiTrace
    {
        public List<CalmPick> Picks { get; } = [];

        public List<CalmStep> Steps { get; } = [];

        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates) =>
            Picks.Add(new(playerId, day, Up(action.KeyStack), action.Type, Up(action.Target), action.X, action.Y, Math.Round(score, 2)));

        public void Stepped(string playerId, int day, AiExecuted step, int number)
        {
            var a = step.Action;
            // Дипломатия — цель игрок (Id); лечение — место (Id: отряд в укреплении или само укрепление, как id8 движка).
            string target = a.Type is AiStepType.Diplomacy or AiStepType.HealRevive ? a.Id : a.Target;
            Steps.Add(new(playerId, day, step.Pass, a.Type, Up(a.Type == AiStepType.HireLeader ? target : target.Split('|')[0])));
        }

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) { }
    }
}
