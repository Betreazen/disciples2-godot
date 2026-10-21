using System.Text.Json;
using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Dbf;
using D2.Data.Magic;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Rng;
using D2.Rules.Units;
using Xunit.Abstractions;
using KSg = Kaitai.Sg;

namespace D2.Rules.Tests;

/// <summary>
/// Экономический шаг ИИ (<see cref="AiStrategic"/>, <see cref="AiStrategicRules"/>) против зонда 6.5+ — все сессии
/// прогонов, которые нашёл <see cref="AiCorpus"/> (сейчас <c>20260918-probe65</c>: ai-q09 и «Растленная власть»; и
/// <c>20260919-human</c>: «Растленная власть», партия человека на 71 день в шести загрузках). Сессии и сохранения — по
/// данным журнала, не по константам <c>seq</c>. Схема — <c>docs/corpus-schema.md</c> «Схема 6.5», разбор —
/// <c>O-re65-strategic.md</c>.
/// </summary>
public partial class AiStrategicHarnessTests(ITestOutputHelper output)
{
    private const string EmptyId = "G000000000";

    /// <summary>vftable действия дипломатии, которой нет в таблице зонда (ненависть/«ничего», <c>id4</c> — цель):
    /// тип 8, её исполнение зонд не перехватывает.</summary>
    private const string UnknownDiplomacyVft = "0x6cf5c4";

    private static readonly string[] Wanted =
    [
        "ai_eval", "ai_candidate", "ai_exec", "ai_budget_spend", "ai_budget", "ai_city_rank", "ai_leader_role",
        "ai_leader_pick", "ai_unit_pick", "ai_build_pick", "ai_rng", "crt_rand", "ai_strategic_step", "ai_turn",
        "save", "hire_unit", "unit_transfer", "fort_day", "build", "bank", "fort_capture", "spell_research", "city_grow", "xp",
        "upgrade_target",
    ];

    private static readonly Lazy<List<(AiSession Session, List<CorpusEvent> Events)>> Journal = new(() => AiCorpus.Split(Wanted));

    private static List<(AiSession Session, List<CorpusEvent> Events)> Load()
    {
        Skip.If(AiCorpus.Sessions.Count == 0, $"нет журналов зонда 6.5+ в {CorpusFiles.Dir}");
        return Journal.Value;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── Ход ИИ и проходы ────────────────────────────────────────────────────────────────────

    private sealed record Turn(string Player, int Day, long Seq, List<CorpusEvent> Events);

    private sealed class PassLog
    {
        public List<AiEvalEvent> Evals { get; } = [];
        public List<AiCandidateEvent> Cands { get; } = [];
        public AiExecEvent? Exec { get; set; }
        public AiStrategicStepEvent? Step { get; set; }
    }

    /// <summary>Законченные ходы ИИ. Ход без строки <c>end</c> оборван падением игры (20260919-f7, f7-m15-diplo:
    /// S143PL0002 день 1, журнал кончается на seq 674751 посреди первого прохода) — его проходы неполны, и проигрыш
    /// хода целиком сверял бы порт с обрывком.</summary>
    private static List<Turn> Turns(List<CorpusEvent> events)
    {
        var turns = new List<Turn>();
        Turn? cur = null;
        foreach (var e in events)
        {
            if (e is AiTurnEvent { Phase: "begin" } b) { cur = new Turn(b.Player!, (int)b.Turn, b.Seq, []); continue; }
            if (e is AiTurnEvent { Phase: "end" }) { if (cur is not null) turns.Add(cur); cur = null; continue; }
            cur?.Events.Add(e);
        }
        return turns;
    }

    private static SortedDictionary<long, PassLog> Passes(Turn t)
    {
        var passes = new SortedDictionary<long, PassLog>();
        PassLog Of(long pass) => passes.TryGetValue(pass, out var p) ? p : passes[pass] = new PassLog();
        foreach (var e in t.Events)
            switch (e)
            {
                case AiEvalEvent x when Same(x.Player, t.Player): Of(x.Pass).Evals.Add(x); break;
                case AiCandidateEvent x when Same(x.Player, t.Player): Of(x.Pass).Cands.Add(x); break;
                case AiExecEvent x when Same(x.Player, t.Player): Of(x.Pass).Exec = x; break;
                case AiStrategicStepEvent x when Same(x.Player, t.Player): Of(x.Pass).Step = x; break;
            }
        return passes;
    }

    private static string? Str(AiObject? o, string field) =>
        o is not null && o.Fields.TryGetValue(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static AiStepType? TypeOf(AiObject? o) =>
        o?.Type is { } t ? (AiStepType)t : o?.Vft == UnknownDiplomacyVft ? AiStepType.Diplomacy : null;

    /// <summary>Ключ памяти действия — <c>id4</c>; у неизвестной vftable — <c>id4</c> оценки с теми же четырьмя байтами.</summary>
    private static string? KeyOf(AiObject? action, PassLog p)
    {
        if (Str(action, "id4") is { } id) return id;
        if (action?.Raw is not { Length: >= 8 } raw) return null;
        var eval = p.Evals.FirstOrDefault(e => e.Eval?.Raw is { Length: >= 8 } r && r[..8] == raw[..8]);
        return Str(eval?.Eval, "id4");
    }

    /// <summary>
    /// 1. Выбор шага: номера <c>idx</c> подряд с 0, типы в порядке <see cref="AiStrategic.ListOrder"/>, исполнено первое;
    /// затем ход целиком проигрывается через <see cref="AiStrategic.RunTurn"/>: списки — оценки журнала (id памяти —
    /// <see cref="AiStrategic.MemoBlocks"/>), действия — кандидаты журнала, исход — <c>ai_exec.ret</c>. Шаги порта
    /// (тип, id, проход) обязаны совпасть с <c>ai_strategic_step</c>, а последний проход — оказаться пустым.
    /// </summary>
    [SkippableFact]
    public void StepChoice_ReplaysThroughRunTurn()
    {
        var all = new Dictionary<AiSession, Counters>();
        string[] required = [];
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            int failedExec = 0, noExec = 0;
            foreach (var turn in Turns(events))
            {
                var passes = Passes(turn);
                if (passes.Count == 0) continue;
                foreach (var (n, p) in passes)
                    CheckPass(c, turn, n, p, ref noExec, ref failedExec);
                Replay(c, turn, passes);
            }
            c.Note($"исполнений с ret=false: {failedExec} (правило «неудача — без памяти» корпусом не проверяется); " +
                   $"шагов без ai_exec (дипломатия {UnknownDiplomacyVft}): {noExec}");
            c.Print(output, $"── {session.Name}: выбор шага");
            required = ["step.idx_contiguous", "step.list_order", "step.exec_is_first", "step.step_is_first",
                      "step.replay", "memo.blocked_eval_has_no_candidate"];
        }
        AiCorpus.RequireEach(all, required);
    }

    private static void CheckPass(Counters c, Turn t, long n, PassLog p, ref int noExec, ref int failedExec)
    {
        string where = $"{t.Player} день {t.Day} проход {n}";
        var idx = p.Cands.Select(x => x.Idx ?? -1).ToList();
        c.Count("step.idx_contiguous", idx.SequenceEqual(Enumerable.Range(0, idx.Count).Select(i => (long)i)),
                $"{where}: idx {string.Join(",", idx)}");
        var ranks = p.Cands.Select(x => TypeOf(x.Action) is { } ty ? AiStrategic.Rank(ty) : -1).ToList();
        c.Count("step.list_order", ranks.All(r => r >= 0) && ranks.SequenceEqual(ranks.Order()),
                $"{where}: ранги {string.Join(",", ranks)}");
        if (p.Cands.Count == 0) return;
        var first = p.Cands[0];
        if (p.Exec is { } x)
        {
            c.Count("step.exec_is_first", x.Action?.Vft == first.Action?.Vft && x.Action?.Raw == first.Action?.Raw,
                    $"{where}: исполнено {x.Action?.Class}/{x.Action?.Raw}, первое {first.Action?.Class}/{first.Action?.Raw}");
            if (!x.Ret) failedExec++;
        }
        else noExec++;
        if (p.Step is { } s)
            c.Count("step.step_is_first", (AiStepType)s.Type == TypeOf(first.Action) && Same(s.Id, KeyOf(first.Action, p)),
                    $"{where}: шаг {s.Type}/{s.Id}, первое {TypeOf(first.Action)}/{KeyOf(first.Action, p)}");
    }

    private static void Replay(Counters c, Turn t, SortedDictionary<long, PassLog> passes)
    {
        long pass = 1;
        var remembered = new HashSet<(string, AiStepType)>();
        IEnumerable<AiEvaluation> Lists(AiStepType type, AiBudget _)
        {
            if (!passes.TryGetValue(pass, out var p)) yield break;
            var cands = p.Cands.Where(x => TypeOf(x.Action) == type).ToList();
            bool first = true;
            foreach (var e in p.Evals.Where(e => TypeOf(e.Eval) == type))
            {
                string id = Str(e.Eval, "id4") ?? "";
                List<AiStepAction> mine;
                if (AiStrategic.MemoBlocks(type))
                {
                    mine = [.. cands.Where(x => Same(KeyOf(x.Action, p), id)).Select(x => Act(x, p))];
                    if (remembered.Contains((id.ToUpperInvariant(), type)))
                        c.Count("memo.blocked_eval_has_no_candidate", mine.Count == 0,
                                $"{t.Player} день {t.Day} проход {pass}: оценка {type}/{id} запомнена, а кандидатов {mine.Count}");
                }
                else mine = first ? [.. cands.Select(x => Act(x, p))] : [];
                first = false;
                yield return new AiEvaluation(AiStrategic.MemoBlocks(type) ? id.ToUpperInvariant() : "", () => mine);
            }
        }
        bool Execute(AiStepAction a)
        {
            var p = passes[pass];
            bool ok = p.Exec?.Ret ?? p.Step is not null;
            if (ok) remembered.Add((a.Id, a.Type));
            pass++;
            return ok;
        }

        var expected = passes.Where(kv => kv.Value.Step is not null)
            .Select(kv => (kv.Key, (AiStepType)kv.Value.Step!.Type, kv.Value.Step.Id?.ToUpperInvariant())).ToList();
        string detail;
        bool same;
        try
        {
            var budget = new AiBudget(AiStrategic.ListOrder.ToDictionary(x => x, _ => Bank.Zero), Bank.Zero);
            var result = AiStrategic.RunTurn(t.Day, Lists, Execute, () => Bank.Zero, budget,
                new Dictionary<(string, AiStepType), int>(), maxPasses: passes.Count + 1);
            var ours = result.Steps.Where(s => s.Ok).Select(s => ((long)s.Pass, s.Action.Type, (string?)s.Action.Id)).ToList();
            same = ours.SequenceEqual(expected) && passes.Keys.Max() == result.Steps.Count + 1;
            detail = $"порт {string.Join(" ", ours.Select(o => $"{o.Item1}:{(int)o.Type}/{o.Item3}"))}; " +
                     $"журнал {string.Join(" ", expected.Select(o => $"{o.Key}:{(int)o.Item2}/{o.Item3}"))}, проходов {passes.Count}";
        }
        catch (InvalidOperationException ex) { same = false; detail = ex.Message; }
        c.Count("step.replay", same, $"{t.Player} день {t.Day}: {detail}");
    }

    private static AiStepAction Act(AiCandidateEvent x, PassLog p) =>
        new(TypeOf(x.Action)!.Value, (KeyOf(x.Action, p) ?? "").ToUpperInvariant(), x.Action?.Raw ?? "");

    // ── Роль и тип лидера ──────────────────────────────────────────────────────────────────

    /// <summary>4. Выбор по долям (<see cref="AiStrategicRules.PickByShare"/>) — все вызовы <c>ai_leader_role</c>.</summary>
    [SkippableFact]
    public void LeaderRole_ReproducesEveryLoggedCall()
    {
        var all = new Dictionary<AiSession, Counters>();
        string[] required = [];
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            foreach (var e in events.OfType<AiLeaderRoleEvent>())
            {
                int ours = AiStrategicRules.PickByShare([.. e.B.Select(b => b != 0)], [.. e.I.Select(i => (int)i)],
                    [.. e.F.Select(f => (float)(f ?? double.NaN))], [.. e.J.Select(j => (int)j)]);
                c.Count($"leader_role.{e.Fn}", ours == e.Ret,
                        $"seq {e.Seq}: b {string.Join(",", e.B)} i {string.Join(",", e.I)} f {string.Join(",", e.F)} " +
                        $"j {string.Join(",", e.J)} → журнал {e.Ret}, порт {ours}");
            }
            c.Print(output, $"── {session.Name}: роль лидера");
            required = ["leader_role.role", "leader_role.fighter", "leader_role.thief"];
        }
        AiCorpus.RequireEach(all, required);
    }

    private sealed class LoggedRng(IReadOnlyList<AiRngEvent> rolls) : IRng
    {
        public int Used { get; private set; }
        public int? LastMax { get; private set; }

        public int Next(int maxValue)
        {
            LastMax = maxValue;
            if (maxValue == 0) return 0;
            if (Used >= rolls.Count) throw new InvalidOperationException($"бросков R250 в журнале {rolls.Count}");
            return (int)rolls[Used++].R;
        }
    }

    /// <summary>
    /// 3. Тип лидера (<see cref="AiStrategicRules.PickLeader"/>): список расы <c>Grace</c>, роль (<see cref="AiStrategicRules.RoleOf"/>),
    /// казна <c>bank</c>, помощь <c>flag</c>; бросок — <c>ai_rng leader_pick</c> из окна вызова. Сверяется результат, длина
    /// отфильтрованного списка (<c>max</c> броска), отсутствие броска при помощи и пустом списке, роль — ответ
    /// предшествующего <c>ai_leader_role role</c>, помощь — подприказ (<c>0x44b6f3</c> = 1 или <c>0x44b7d0</c> = 2).
    /// </summary>
    [SkippableFact]
    public void LeaderPick_MatchesRaceListRoleTreasuryAndRoll()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        var econ = EconomyLoader.Load(GamePaths.Globals);
        var extras = RulesLoader.LoadExtras(GamePaths.Globals);
        var all = new Dictionary<AiSession, Counters>();
        string[] required = [];
        foreach (var (session, events) in Load())
        {
            var c = all[session] = new Counters();
            var races = RacesOf(session);
            var rolls = events.OfType<AiRngEvent>().Where(r => r.Site == "leader_pick").ToList();
            AiLeaderRoleEvent? role = null, sub = null;
            foreach (var e in events)
            {
                if (e is AiLeaderRoleEvent r) { if (r.Fn == "role") (role, sub) = (r, null); else sub = r; continue; }
                if (e is not AiLeaderPickEvent pick) continue;
                string where = $"{pick.Player} проход {pick.Pass} seq {pick.Seq}";
                c.Count("leader_pick.role_from_role_call", role is not null && role.Ret == pick.Role, $"{where}: роль {pick.Role}, вызов {role?.Ret}");
                bool assist = sub is { Fn: "fighter", Ret: 1 } or { Fn: "thief", Ret: 2 };
                c.Count("leader_pick.flag_from_suborder", assist == pick.Flag, $"{where}: flag {pick.Flag}, подприказ {sub?.Fn}={sub?.Ret}");
                if (pick.Bank is null || !races.TryGetValue(pick.Player ?? "", out var raceId)) { c.Skip("leader_pick", "нет казны или расы"); continue; }
                var options = econ.Races[raceId].Leaders.Select(id => new AiLeaderOption(id,
                    AiStrategicRules.RoleOf(extras.Units[id].LeaderCategory), econ.Hire[id].Cost, extras.Units[id].Move)).ToList();
                var window = rolls.Where(x => x.Seq > pick.Seq && x.Seq <= pick.SeqEnd).ToList();
                var rng = new LoggedRng(window);
                var ours = AiStrategicRules.PickLeader(options, (AiLeaderRole)pick.Role, pick.Bank, pick.Flag, rng);
                c.Count("leader_pick.result", Same(ours?.UnitId ?? EmptyId, pick.Result), $"{where}: журнал {pick.Result}, порт {ours?.UnitId}");
                c.Count("leader_pick.rolls", rng.Used == window.Count, $"{where}: бросков журнал {window.Count}, порт {rng.Used}");
                if (window.Count == 1)
                {
                    c.Count("leader_pick.roll_max", rng.LastMax == window[0].Max, $"{where}: max журнал {window[0].Max}, порт {rng.LastMax}");
                    c.Count("leader_pick.roll_field", pick.Roll == window[0].R, $"{where}: roll {pick.Roll}, ai_rng {window[0].R}");
                }
            }
            c.Print(output, $"── {session.Name}: тип лидера");
            required = ["leader_pick.role_from_role_call", "leader_pick.flag_from_suborder", "leader_pick.result",
                      "leader_pick.rolls", "leader_pick.roll_max", "leader_pick.roll_field"];
        }
        AiCorpus.RequireEach(all, required);
    }

    // ── Сохранения ─────────────────────────────────────────────────────────────────────────

    /// <summary>Сохранение дня <paramref name="day"/> — последнее в сессии раньше события <paramref name="seq"/> (в
    /// партии человека это автосохранение конца его хода, прямо перед ходами ИИ), и <c>seq</c> его строки <c>save</c>.</summary>
    private static (string Path, long Cut)? SaveOf(AiSession s, int day, long seq) =>
        s.SaveBefore(seq, day) is { File: { } file } save ? (file, save.Seq) : null;

    private static Dictionary<string, string> RacesOf(AiSession s)
    {
        var file = s.Saves.First(x => x.File is not null).File!;
        return SaveMaps.Map(file).Players.Values.ToDictionary(p => p.Id, p => p.RaceId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary><c>Capital</c> и <c>Size</c> нужны броне укрепления в силе города (<c>0x5dcf60</c>,
    /// <see cref="AiStrength.Unit"/>): у столицы <c>PROT_CAP</c>, у деревни <c>PROT_&lt;Size&gt;</c>.</summary>
    private sealed record Fort(string Id, string Owner, string Guest, List<string> Units, List<int> Positions,
                               bool Capital, int Size);

    private static readonly Dictionary<string, (MapState Map, Dictionary<string, Fort> Forts)> SaveCache = new();

    private static (MapState Map, Dictionary<string, Fort> Forts) Open(string path)
    {
        lock (SaveCache)
        {
            if (SaveCache.TryGetValue(path, out var hit)) return hit;
            var forts = new Dictionary<string, Fort>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in SgFile.Open(path).Raw.Objects)
            {
                var city = o.Data switch { KSg.Capital x => x.City, KSg.MidVillage x => x.City, _ => null };
                if (city is null) continue;
                forts[city.CityId.String] = new Fort(city.CityId.String, city.Owner.String, city.Stack.String,
                    [.. city.Group.Units.Select(u => u.String)], [.. city.Group.Positions.Select(x => (int)x.Value)],
                    o.Data is KSg.Capital, o.Data is KSg.MidVillage v ? (int)v.Size.Value : 0);
            }
            return SaveCache[path] = (SaveMaps.Map(path), forts);
        }
    }

    /// <summary>Снимок с досыпанными постройками игрока (и, если задана, его казной) — состояние на момент события
    /// журнала, позже сохранения. Общий с харнессом экономики (<see cref="EconomyHarnessTests"/>).</summary>
    internal static MapState WithBuildings(MapState map, string playerId, IEnumerable<string> extra, Bank? bank = null)
    {
        var p = map.Players[playerId];
        var player = new MapPlayer
        {
            Id = p.Id, RaceId = p.RaceId, LordId = p.LordId, Bank = bank ?? p.Bank, FogId = p.FogId, BuildingsId = p.BuildingsId,
            Buildings = [.. p.Buildings, .. extra], KnownSpells = p.KnownSpells,
        };
        return new MapState
        {
            Size = map.Size, Tiles = map.Tiles, Stacks = map.Stacks, Units = map.Units, Objects = map.Objects,
            Plan = map.Plan, Fog = map.Fog, Turn = map.Turn,
            Players = map.Players.Values.Where(x => x.Id != playerId).Append(player)
                .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase),
        };
    }
}
