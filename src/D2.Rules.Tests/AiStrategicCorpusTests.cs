using D2.Data;
using D2.Data.Corpus;
using D2.Data.Dbf;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Map;
using Xunit.Abstractions;
using KSg = Kaitai.Sg;

namespace D2.Rules.Tests;

/// <summary>
/// Экономический шаг ИИ (<see cref="AiStrategic"/>) — следствия правил, которые видны и без счёта кандидатов (писались
/// под зонд 6.4, прогон 17.09 ai-q09 потерян 18.09): (a) исследование только в дни, кратные 5; (b) порядок типов шагов
/// в ходе; (c) выбор постройки столицы по сохранению перед ходом. Сессии — все сессии <see cref="AiCorpus"/> (зонд 6.5+).
/// </summary>
[Trait("Category", "Corpus")]
public class AiStrategicCorpusTests(ITestOutputHelper output)
{
    private static readonly string[] Wanted = ["ai_strategic_step", "spell_research", "build", "ai_budget", "save", "xp",
                                                "ai_unit_pick", "ai_eval", "ai_candidate", "ai_leader_role", "trade_accept"];

    /// <summary>Каталог построек (<c>Gbuild</c>) — для категории постройки шага столицы; грузится, только если нужен.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, BuildingType>> BuildingCatalog =
        new(() => EconomyLoader.Load(GamePaths.Globals).Buildings);

    private static readonly Lazy<List<(AiSession Session, List<CorpusEvent> Events)>> Journal = new(() => AiCorpus.Split(Wanted));

    private static List<(AiSession Session, List<CorpusEvent> Events)> Sessions()
    {
        Skip.If(AiCorpus.Sessions.Count == 0, $"нет журналов зонда 6.5+ в {CorpusFiles.Dir}");
        return Journal.Value;
    }

    private static List<AiStrategicStepEvent> Steps(List<CorpusEvent> events) =>
        [.. events.OfType<AiStrategicStepEvent>().Where(s => s.Player is not null)];

    /// <summary>День события — по ближайшему следующему шагу того же игрока и типа (шаг пишется после исполнения).</summary>
    private static long DayOf(List<AiStrategicStepEvent> steps, long seq, string player, AiStepType type) =>
        steps.First(s => s.Seq > seq && s.Player == player && s.Type == (long)type).Turn;

    [SkippableFact]
    public void SpellResearch_OnlyOnDaysDivisibleByFive()
    {
        int total = 0;
        foreach (var (session, events) in Sessions())
        {
            var steps = Steps(events);
            var ai = steps.Select(s => s.Player!).ToHashSet();
            // Исследования человека (в партии 20260919-human их пишет тот же хук) к шагу ИИ не относятся.
            var research = events.OfType<SpellResearchEvent>().Where(r => ai.Contains(r.Player)).ToList();
            var typeSix = steps.Where(s => s.Type == (long)AiStepType.SpellResearch).ToList();
            var days = research.Select(r => DayOf(steps, r.Seq, r.Player, AiStepType.SpellResearch)).ToList();
            output.WriteLine($"{session.Name}: spell_research ИИ {research.Count}, шагов типа 6 {typeSix.Count}, дни: {string.Join(", ", days)}");
            Assert.Equal(research.Count, typeSix.Count);
            Assert.All(days, d => Assert.Equal(0, d % 5));
            Assert.All(typeSix, s => Assert.Equal(0, s.Turn % 5));
            total += research.Count;
        }
        Assert.True(total > 0, "ни одного исследования ИИ");
    }

    /// <summary>
    /// Инвариант порядка: списки пересобираются после каждого шага, поэтому тип следующего шага не раньше по
    /// <see cref="AiStrategic.ListOrder"/>, чем тип предыдущего, — кроме случаев, когда более ранний список на новом
    /// проходе стал непустым. В корпусе их три, и каждый проверяется по событиям обоих проходов:
    /// <list type="bullet">
    /// <item>рост деревни (3) → найм (2): у выросшей деревни появляются места гарнизона;</item>
    /// <item>любой шаг → найм (2), переброс выборки: найм берёт юнитов случайно (<c>ai_unit_pick</c>, CRT
    /// <c>rand</c>) при построении списка на каждом проходе, оценка появляется, только если цена юнита в бюджете,
    /// а действие — только если ещё и <c>hire_check 0x5deea7</c> == 0 (<see cref="AiStrategicRules.PickUnits"/>).
    /// На прошлом проходе выборки были, действий найма нет (все выбранные дороже бюджета — нет и оценок, или
    /// оценка есть, но <c>hire_check</c> отказал), на этом — оценка юнита, которого прошлый проход не выбирал.
    /// 20260919-f7b/20260920-gaps, Горные кланы, день 1: бюджет найма 50, проход 3 выбрал дважды G000UU0033 (60) —
    /// список пуст, исполнена столица; проход 4 выбрал G000UU0026 (40) и G000UU0036 (50) — найм. Постройка
    /// (G000BB0047) к этому отношения не имеет: бюджет найма тот же. 20261014-replay, эльфы S143PL0002, день 1
    /// (seq 288980…289059, и ещё раз после перезагрузки у 371421): бюджет найма 400, казна 50, проход 9 выбрал
    /// G000UU8025 — оценка есть, <c>hire_check</c> 7 (казна &lt; ENROLL_C), действий экономики нет — исполнена
    /// дипломатия (ненависть к S143PL0003); проход 10 выбрал G000UU8014 (50) — найм;</item>
    /// <item>столица (4) → найм лидера (0): постройка открыла роль — маска <c>b</c> вызова <c>ai_leader_role role</c>
    /// на этом проходе другая, чем на прошлом. 20260920-gaps ai-g2-thief-riot, день 1: гильдия G000BB0120 (категория
    /// 0) даёт <c>b</c> [1,1,0] → [1,1,1], роль 0 (выбор G000000000 — пусто) → 2 (вор G000UU8013).</item>
    /// <item>столица (4) → лечение (5): постройка — Храм (категория <see cref="Healing.TempleCategory"/>), без него
    /// раненых не лечат (<c>0x5de32f</c>, в порту <c>AiEconomy.CareUnit</c>), и список 5 на прошлом проходе пуст —
    /// оценок <c>heal_revive</c> нет, на этом — есть. 20261014-replay ai-q12 и ai-r1, эльфы S143PL0002, день 1
    /// (seq 104272…104321): проход 5 — <c>build</c> g000bb0118 (Temple, начальная постройка ИИ flag 1), проход 6 —
    /// оценки лечения S143UN008c/S143UN008d в S143FT0007 и лечение.</item>
    /// </list>
    /// Дипломатия (8) исполняется только на проходе без действий экономики, поэтому после первой 8 в ходе другой
    /// тип возможен только тем же перебросом выборки найма (ранг 1 по 20261014-replay, см. выше) или когда сделку ИИ
    /// приняли и казна выросла (<see cref="TradeFilledPurse"/>, карта 15); других — нет.
    /// Номера проходов идут подряд с 1.
    /// </summary>
    [SkippableFact]
    public void StepTypes_WithinTurn_FollowListOrder_ExceptGrowthRefillsHiring()
    {
        int pairs = 0, ascending = 0, growthToHire = 0, rerollToHire = 0, roleToLeader = 0, other = 0, turnCount = 0,
            diplomacyToHire = 0, templeToCare = 0, tradeRefill = 0;
        var unexplained = new List<string>();
        foreach (var (session, events) in Sessions())
        {
            var steps = Steps(events);
            foreach (var turn in steps.GroupBy(s => (s.Player, s.Turn)))
            {
                turnCount++;
                var list = turn.OrderBy(s => s.Seq).ToList();
                Assert.Equal(Enumerable.Range(1, list.Count).Select(i => (long)i), list.Select(s => s.Pass));
                for (int i = 1; i < list.Count; i++)
                {
                    var (a, b) = ((AiStepType)list[i - 1].Type, (AiStepType)list[i].Type);
                    pairs++;
                    if (AiStrategic.Rank(b) >= AiStrategic.Rank(a)) ascending++;
                    else if (a == AiStepType.GrowCities && b == AiStepType.HireUnit) growthToHire++;
                    else if (b == AiStepType.HireUnit && HireRerolled(events, steps, list[i - 1], list[i]))
                    {
                        rerollToHire++;
                        if (a == AiStepType.Diplomacy) diplomacyToHire++;
                    }
                    else if (a == AiStepType.Capital && b == AiStepType.HireLeader && RoleOpened(events, steps, list[i - 1], list[i])) roleToLeader++;
                    else if (a == AiStepType.Capital && b == AiStepType.HealRevive && TempleOpenedCare(events, steps, list[i - 1], list[i])) templeToCare++;
                    else if (a == AiStepType.Diplomacy && Spends(b) && TradeFilledPurse(events, list[i - 1], list[i])) tradeRefill++;
                    else
                    {
                        other++;
                        unexplained.Add($"{session.Name} {turn.Key.Player} день {turn.Key.Turn}: {(int)a}→{(int)b}");
                    }
                    if (a == AiStepType.Diplomacy && b != AiStepType.Diplomacy)
                        Assert.True(b == AiStepType.HireUnit && HireRerolled(events, steps, list[i - 1], list[i])
                                    || Spends(b) && TradeFilledPurse(events, list[i - 1], list[i]),
                            $"{session.Name} {turn.Key.Player} день {turn.Key.Turn}: после дипломатии {(int)b} без переброса найма");
                }
            }
        }
        output.WriteLine($"ходов {turnCount}, пар шагов {pairs}: по порядку {ascending}, рост→найм {growthToHire}, " +
                         $"переброс выборки→найм {rerollToHire} (из них после дипломатии {diplomacyToHire}), постройка открыла роль→найм лидера {roleToLeader}, " +
                         $"Храм открыл лечение {templeToCare}, сделка пополнила казну {tradeRefill}, " +
                         $"прочих спусков {other} {string.Join("; ", unexplained)}");
        Assert.True(turnCount > 0 && growthToHire > 0);
        Assert.Empty(unexplained);
    }

    /// <summary>События прохода шага <paramref name="step"/>: того же игрока, с его номером прохода, после предыдущего
    /// шага игрока и не позже этого шага.</summary>
    private static IEnumerable<T> OfPass<T>(List<CorpusEvent> events, List<AiStrategicStepEvent> steps, AiStrategicStepEvent step,
                                            Func<T, (string? Player, long Pass)> key) where T : CorpusEvent
    {
        long from = steps.LastOrDefault(s => s.Player == step.Player && s.Seq < step.Seq)?.Seq ?? long.MinValue;
        return events.OfType<T>().Where(e => e.Seq > from && e.Seq <= step.Seq && key(e) == (step.Player, step.Pass));
    }

    /// <summary>Найм снова непуст из-за новой случайной выборки: на проходе <paramref name="prev"/> выборки были, а
    /// действий найма нет (оценок нет — бюджет; оценки есть — отказ <c>hire_check</c>); на проходе
    /// <paramref name="next"/> оценка есть, и её юнит прошлым проходом не выбирался.</summary>
    private static bool HireRerolled(List<CorpusEvent> events, List<AiStrategicStepEvent> steps, AiStrategicStepEvent prev,
                                     AiStrategicStepEvent next)
    {
        var picked = OfPass<AiUnitPickEvent>(events, steps, prev, e => (e.Player, e.Pass)).Select(e => e.Result).ToHashSet();
        bool prevHadAction = OfPass<AiCandidateEvent>(events, steps, prev, e => (e.Player, e.Pass))
            .Any(e => e.Action?.Type == (long)AiStepType.HireUnit);
        var nowEval = OfPass<AiEvalEvent>(events, steps, next, e => (e.Player, e.Pass))
            .Where(e => e.List == "hire_unit").Select(e => Str(e.Eval, "id8")).ToList();
        return picked.Count > 0 && !prevHadAction && nowEval.Any(u => !picked.Contains(u));
    }

    /// <summary>Постройка открыла роль лидера: маска <c>b</c> вызова <c>ai_leader_role role</c> на двух проходах разная.</summary>
    private static bool RoleOpened(List<CorpusEvent> events, List<AiStrategicStepEvent> steps, AiStrategicStepEvent prev,
                                   AiStrategicStepEvent next)
    {
        IReadOnlyList<long>? Mask(AiStrategicStepEvent s) => OfPass<AiLeaderRoleEvent>(events, steps, s, e => (e.Player, e.Pass))
            .LastOrDefault(e => e.Fn == "role")?.B;
        return Mask(prev) is { } was && Mask(next) is { } now && !was.SequenceEqual(now);
    }

    /// <summary>Шаг столицы <paramref name="prev"/> построил Храм, и список лечения стал непуст: на его проходе оценок
    /// <c>heal_revive</c> нет, на проходе <paramref name="next"/> — есть.</summary>
    private static bool TempleOpenedCare(List<CorpusEvent> events, List<AiStrategicStepEvent> steps, AiStrategicStepEvent prev,
                                         AiStrategicStepEvent next)
    {
        long from = steps.LastOrDefault(s => s.Player == prev.Player && s.Seq < prev.Seq)?.Seq ?? long.MinValue;
        var built = events.OfType<BuildEvent>().Where(b => b.Ret && b.Player == prev.Player && b.Seq > from && b.Seq < prev.Seq)
                          .Select(b => b.Building).ToList();
        if (built.Count == 0) return false;
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        bool Care(AiStrategicStepEvent s) => OfPass<AiEvalEvent>(events, steps, s, e => (e.Player, e.Pass)).Any(e => e.List == "heal_revive");
        return Healing.HasTemple(built, BuildingCatalog.Value) && !Care(prev) && Care(next);
    }

    /// <summary>Шаг дипломатии <paramref name="step"/> — сделка ИИ, и цель её приняла (<c>trade_accept</c> с <c>accepted</c>,
    /// предлагавший — <c>b</c>): золото пришло в казну после траты типа 8 (<c>CEffectAcceptTrade 0x433805</c>), и на новом
    /// проходе экономика снова непуста. Шаг пишется сразу за исполнением, сделка — после траты: окно — между шагами. Карта 15
    /// (<c>20261018-port-synth</c>), Легионы, день 2: проход 8 — заклинание Нежити за 500 (seq 3656131, казна 90 → 590), проход 9 —
    /// найм лидера (200; бюджет 418 был и раньше, не хватало казны).</summary>
    private static bool TradeFilledPurse(List<CorpusEvent> events, AiStrategicStepEvent step, AiStrategicStepEvent next) =>
        events.OfType<TradeAcceptEvent>().Any(t => t.Accepted && t.Gold > 0 && JournalSaves.Same(t.B, step.Player ?? "")
                                                   && t.Seq > step.Seq && t.Seq < next.Seq);

    /// <summary>Тип, которому нужна казна (найм, постройка, рост): только его могла открыть выросшая казна.</summary>
    private static bool Spends(AiStepType type) =>
        type is AiStepType.HireLeader or AiStepType.HireUnit or AiStepType.Capital or AiStepType.GrowCities;

    private static string? Str(AiObject? o, string field) =>
        o is not null && o.Fields.TryGetValue(field, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// (c) Постройка столицы: для каждого события <c>build</c> выбор <see cref="AiStrategicRules.PickBuilding"/> по
    /// сохранению перед ходом (tNN — ход человека того же дня; ИИ ходят после него) плюс постройки этого же хода
    /// раньше. Обратная проверка — для лечения/магии/гильдии (без казны и бюджета): если на начало хода выбор такой,
    /// постройка обязана случиться в этот ход.
    /// </summary>
    [SkippableFact]
    public void Build_MatchesCapitalChoice_FromSaveBeforeTurn()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var rules = EconomyLoader.Load(GamePaths.Globals);
        var world = Gunits.Load(GamePaths.Globals);
        var cheats = EconomyLoader.LoadAiCheats(GamePaths.Globals);
        int XpPercent(string sg) => cheats.UnitXp(DifficultyOf(sg));
        var lordLists = DbfTable.Read(Find(GamePaths.Globals, "GbuiList.dbf")).Rows
            .GroupBy(r => r.GetString("LORD_ID"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.GetString("BUILD_ID")).ToList(), StringComparer.OrdinalIgnoreCase);

        int total = 0, match = 0, mismatch = 0, negChecked = 0, negOk = 0, unitChoices = 0, unitFitButIdle = 0;
        var lines = new List<string>();
        foreach (var (session, events) in Sessions())
        {
            var steps = Steps(events);
            var ai = steps.Select(s => s.Player!).ToHashSet();
            // Опыт на момент выбора — строки xp журнала после сохранения (см. Choice, xpNow).
            Dictionary<string, int> XpAt(long from, long to) => events.OfType<XpEvent>()
                .Where(x => x.Ret && x.XpAfter is not null && x.Seq > from && x.Seq < to)
                .GroupBy(x => x.Unit, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (int)g.Last().XpAfter!.Value, StringComparer.OrdinalIgnoreCase);
            var builds = events.OfType<BuildEvent>().Where(b => b.Ret && ai.Contains(b.Player)
                                                               && steps.Any(s => s.Seq > b.Seq && s.Player == b.Player && s.Type == (long)AiStepType.Capital))
                .Select(b => (Event: b, Day: (int)DayOf(steps, b.Seq, b.Player, AiStepType.Capital))).ToList();
            total += builds.Count;
            foreach (var (b, day) in builds)
            {
                if (session.SaveBefore(b.Seq, day) is not { File: { } sg } save) { lines.Add($"{session.Name} день {day}: нет сохранения"); continue; }
                var earlier = builds.Where(x => x.Day == day && x.Event.Player == b.Player && x.Event.Seq < b.Seq)
                                    .Select(x => x.Event.Building).ToList();
                var pick = Choice(sg, b.Player, earlier, rules, world, lordLists, XpPercent(sg), out string how, XpAt(save.Seq, b.Seq));
                bool ok = string.Equals(pick?.Id, b.Building, StringComparison.OrdinalIgnoreCase);
                if (ok) match++; else mismatch++;
                lines.Add($"{session.Name} {b.Player} день {day}: build {b.Building}, выбор {pick?.Id ?? "—"} ({how}) {(ok ? "ok" : "РАСХОЖДЕНИЕ")}");
            }

            // Обратная проверка: выбор без казны на начало хода обязан исполниться.
            foreach (var turn in steps.GroupBy(s => (Player: s.Player!, Day: (int)s.Turn)))
            {
                long first = turn.Min(s => s.Seq);
                if (session.SaveBefore(first, turn.Key.Day) is not { File: { } sg } save) continue;
                var pick = Choice(sg, turn.Key.Player, [], rules, world, lordLists, XpPercent(sg), out _, XpAt(save.Seq, first));
                if (pick is null) continue;
                if (!AiStrategicRules.IgnoresCost(pick))
                {
                    // Справочно: постройка ветки юнитов ждёт бюджета типа 4 (0x446e60) и казны — их вне сохранения нет.
                    var start = events.OfType<AiBudgetEvent>().LastOrDefault(e => e.Player == turn.Key.Player
                        && e.Type == (long)AiStepType.Capital && e.Seq < first);
                    bool built = builds.Any(x => x.Day == turn.Key.Day && x.Event.Player == turn.Key.Player);
                    bool fits = start?.Budget is { } bud && !Economy.NotEnough(bud, pick.Cost);
                    unitChoices++;
                    if (!built && fits) unitFitButIdle++;
                    continue;
                }
                negChecked++;
                if (builds.Any(x => x.Day == turn.Key.Day && x.Event.Player == turn.Key.Player
                                    && x.Event.Building.Equals(pick.Id, StringComparison.OrdinalIgnoreCase)))
                    negOk++;
                else lines.Add($"{session.Name} {turn.Key.Player} день {turn.Key.Day}: выбор {pick.Id} без казны, а постройки нет");
            }
        }

        foreach (var l in lines) output.WriteLine(l);
        output.WriteLine($"build {total}: совпало {match}, расхождений {mismatch}; " +
                         $"выбор без казны на начало хода {negChecked}, исполнен {negOk}; выбор — здание юнита {unitChoices}, " +
                         $"из них в бюджете типа 4 на начало хода, но без постройки {unitFitButIdle}");
        Assert.True(match > 0);
        Assert.Equal(0, mismatch);
        Assert.Equal(negChecked, negOk);
    }

    /// <summary>Пометка в описании выбора <see cref="Choice"/>: у двух зданий S2 поровну юнитов у порога.</summary>
    internal const string TieMark = "равенство S2";

    /// <param name="xpNow">Опыт юнитов на момент выбора по строкам <c>xp</c> журнала между сохранением и выбором
    /// (дневной чит и бои): задан — чит не предсказывается, юнит без строки остаётся с опытом сохранения. Предсказание
    /// чита не знает его фильтра тумана (<c>0x47387a</c>: группа, которую видит человек, опыта не получает — в партии
    /// человека 20260919-human юнит S073UN019c дни 4–15 стоит на 266/275 без чита, и порт брал g000bb0064 на три дня
    /// раньше движка).</param>
    /// <param name="removed">Юниты, снятые с карты между сохранением и выбором (отряд пал в бою — <c>CEffectKillStack</c> убирает его юнитов,
    /// и <c>0x44773c</c> их больше не видит; павший юнит выжившей группы остаётся и считается).</param>

    internal static BuildingType? Choice(string sg, string playerId, IReadOnlyList<string> builtToday, EconomyRules rules,
        Gunits world, IReadOnlyDictionary<string, List<string>> lordLists, int xpPercent, out string how,
        IReadOnlyDictionary<string, int>? xpNow = null, IReadOnlySet<string>? removed = null)
    {
        var map = SaveMaps.Map(sg);
        var p = map.Players[playerId];
        var player = new MapPlayer
        {
            Id = p.Id, RaceId = p.RaceId, LordId = p.LordId, Bank = p.Bank, FogId = p.FogId,
            BuildingsId = p.BuildingsId, Buildings = [.. p.Buildings, .. builtToday],
        };
        var state = new MapState
        {
            Size = map.Size, Tiles = map.Tiles, Stacks = map.Stacks, Units = map.Units, Objects = map.Objects,
            Plan = map.Plan, Fog = map.Fog, Turn = map.Turn,
            Players = map.Players.Values.Where(x => x.Id != playerId).Append(player)
                .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase),
        };
        // Порядок списка лорда — порядок id: ничьи S2 корпуса (build_pick.tie) — первое по id, 491 из 491 (И113).
        var lordList = lordLists.GetValueOrDefault(p.LordId, []).Order(StringComparer.OrdinalIgnoreCase);
        var available = AiStrategicRules.Available(state, playerId, lordList, rules);
        bool temple = player.Buildings.Any(id => rules.Buildings.TryGetValue(id, out var t) && t.Category == 1);

        // Все юниты карты; своим — дневной чит опыта (отряды p, гарнизоны 2p), он идёт до стратегии.
        // ponytail: условие тумана чита (0x47387a) и чит ИИ, ходивших раньше в этот день, не учитываются.
        var own = OwnUnits(sg, map, playerId);
        var ids = map.Units.Values.Where(u => removed is null || !removed.Contains(u.Id)).ToList();
        var units = ids.Select(u =>
        {
            int next = world.XpNext(u.TypeId);
            int xp = xpNow is not null ? xpNow.GetValueOrDefault(u.Id, u.Xp)
                : own.TryGetValue(u.Id, out bool garrison)
                    ? Math.Min(u.Xp + Units.Experience.AiDailyXp(next, xpPercent, garrison), next)
                    : u.Xp;
            return new AiCapitalUnit(world.UpgradeBuildingsOf(u.TypeId, u.Level), xp, next);
        }).ToList();
        var pick = AiStrategicRules.PickBuilding(available, temple, units, world.SideshowBuilding(p.RaceId));
        var ready = ids.Zip(units).Where(x => x.Second.UpgradeBuildings.Count > 0 && x.Second.Xp > x.Second.XpNext - 5)
            .Select(x => $"{x.First.Id}:{x.First.TypeId} {x.First.Xp}→{x.Second.Xp}/{x.Second.XpNext}{(own.ContainsKey(x.First.Id) ? " свой" : "")}").ToList();
        // Счёт S2 по зданиям (как AiStrategicRules.Upgrades): при равенстве наибольшего — первое в порядке id (строгий максимум 0x4476fc),
        // 491 проход с ничьей по корпусу из 491 (build_pick.tie; И113, 20.10 r20/last). Единственная «ничья не в пользу первого» (карта 23, день 56 Кланов, BB0028 и BB0039
        // по 8) была счётом харнесса: UU0038 S143UN0088 пал с отрядом S143KC0058 в том же дне до выбора (removed).
        var counts = available.Select(b => units.Count(u => u.Xp > u.XpNext - 5 && u.UpgradeBuildings.Contains(b.Id, StringComparer.OrdinalIgnoreCase)))
                              .ToList();
        int top = counts.DefaultIfEmpty(0).Max();
        string tie = temple && top > 0 && counts.Count(n => n == top) > 1
            ? $", {TieMark} {top}: {string.Join(" ", available.Zip(counts).Where(x => x.Second == top).Select(x => x.First.Id))}" : "";
        how = $"храм {(temple ? "есть" : "нет")}, доступно {available.Count}, у порога {ready.Count}{tie} [{string.Join("; ", ready)}]";
        return pick;
    }

    /// <summary>Юниты игрока: id → стоит ли в гарнизоне его столицы или деревни (иначе — в отряде).</summary>
    private static Dictionary<string, bool> OwnUnits(string sg, MapState map, string playerId)
    {
        var own = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in map.Stacks.Values.Where(s => s.Owner.Equals(playerId, StringComparison.OrdinalIgnoreCase)))
            foreach (var u in s.UnitIds) own[u] = false;
        foreach (var o in SgFile.Open(sg).Raw.Objects)
        {
            var city = o.Data switch { KSg.Capital c => c.City, KSg.MidVillage v => v.City, _ => null };
            if (city is null || !city.Owner.String.Equals(playerId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var u in city.Group.Units) own[u.String] = true;
        }
        return own;
    }

    private static Difficulty DifficultyOf(string sg) =>
        (Difficulty)(int)SgFile.Open(sg).Raw.Objects.Select(o => o.Data)
            .OfType<KSg.ScenarioInfo>().Single().Diffgame.Value;

    internal static string Find(string dir, string name) =>
        Directory.EnumerateFiles(dir).First(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Нужные столбцы <c>Gunits</c>: <c>PREV_ID</c>, <c>UPGRADE_B</c>, <c>ENROLL_B</c>, <c>XP_NEXT</c>, ветка.</summary>
    internal sealed class Gunits(IReadOnlyList<DbfRow> rows)
    {
        public IReadOnlyList<DbfRow> Rows { get; } = rows;

        private const string Empty = "g000000000";
        private const int Sideshow = 4;   // LunitB: L_SIDESHOW

        public static Gunits Load(string globals) => new(DbfTable.Read(Find(globals, "Gunits.dbf")).Rows);

        private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        private static bool Set(string id) => id.Length > 0 && !Same(id, Empty);

        /// <summary>Здания, для которых тип — исходный (<c>0x44786e</c>): первый по файлу тип с этим <c>UPGRADE_B</c>
        /// и его <c>PREV_ID</c>.</summary>
        public IReadOnlyList<string> UpgradeBuildingsOf(string typeId, int level) =>
            RowOf(typeId) is { } own && level > own.GetInt("LEVEL")
                ? []   // вырос на месте: реализация порождённая, PREV_ID на неё не ссылается (0x5d7e07; UnitUpgrade.IsGrown)
                : UpgradeBuildingsOf(typeId);

        /// <summary>Здания по <c>PREV_ID</c> первого типа каждого <c>UPGRADE_B</c> — один раз на таблицу: проход по всем строкам с
        /// группировкой на каждого юнита был 32 с из 43 у харнесса стратегии (замер 16.10).</summary>
        private IReadOnlyList<string> UpgradeBuildingsOf(string typeId) =>
            (_upgrades ??= Rows.Where(r => Set(r.GetString("UPGRADE_B")))
                .GroupBy(r => r.GetString("UPGRADE_B"), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Prev: g.First().GetString("PREV_ID"), Building: g.Key))
                .ToList())
            .Where(u => Same(u.Prev, typeId)).Select(u => u.Building).ToList();

        private List<(string Prev, string Building)>? _upgrades;
        private Dictionary<string, DbfRow>? _byId;

        /// <summary>Первая по файлу строка типа (<c>UNIT_ID</c> без учёта регистра).</summary>
        private DbfRow? RowOf(string typeId)
        {
            if (_byId is null)
            {
                var byId = new Dictionary<string, DbfRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in Rows) byId.TryAdd(r.GetString("UNIT_ID"), r);
                _byId = byId;
            }
            return _byId.GetValueOrDefault(typeId);
        }

        public int XpNext(string typeId) => RowOf(typeId)?.GetInt("XP_NEXT") ?? int.MaxValue;

        /// <summary><c>ENROLL_B</c> базового юнита ветки L_SIDESHOW расы (<c>0x837f28</c>, ранг 2).</summary>
        public string SideshowBuilding(string raceId) =>
            Rows.Where(r => Same(r.GetString("RACE_ID"), raceId) && r.GetInt("UNIT_CAT") == 0
                            && r.GetInt("BRANCH") == Sideshow && !Set(r.GetString("PREV_ID")))
                .Select(r => r.GetString("ENROLL_B")).FirstOrDefault(Set) ?? "";
    }
}
