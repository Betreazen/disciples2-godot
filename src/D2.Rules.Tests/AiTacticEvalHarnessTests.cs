using System.Text;
using System.Text.Json;
using D2.Data;
using D2.Data.Map;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Счёт оценщиков тактики ИИ (<see cref="AiTacticEval"/>, <see cref="AiTacticScore"/>) против <c>score_in</c> каждой
/// строки <c>ai_tactic_candidate</c> всех сессий <see cref="AiCorpus"/> (зонд 6.5+: <c>20260918-probe65</c> — ai-q09 и
/// «Растленная власть»; <c>20260919-human</c> — партия человека, шесть загрузок). Оценщик — по типу действия и
/// <c>ret_addr</c>. Входы восстанавливаются из сохранения дня — последнего в сессии до первого кандидата дня (в партии
/// человека это автосохранение конца его хода) — и строк журнала до кандидата: позиция отряда — последняя <c>stack_move</c> дня, точка
/// цели — <c>action.point</c>, сетка влияния — крепости игрока из сохранения, веса — строка <c>GAI</c>, сила —
/// ближайшая предыдущая <c>ai_strength</c>, статусы — память из <c>ai_tactic_result</c>. Чего восстановить нельзя,
/// идёт в пропуск с причиной.
/// <para>Входы ранга 3 (что продаёт торговец, урон отряда, свободные места найма, множитель
/// поправок <c>0x453aee</c>) не восстановимы — они подбираются из своего конечного набора, и такая сверка считается
/// отдельно («с выведенным входом»): она проверяет форму счёта, а не значение входа.</para>
/// <para>Итог корпуса 18.09 (восемь сессий): ни одного расхождения; сошлось 11 548, с выведенным входом 2 656, пропусков
/// 192 — почти все «оценщик не сверяется» (взятие города 0x02/0x1a), три — объекта нет в сохранении.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class AiTacticEvalHarnessTests(ITestOutputHelper output)
{
    /// <summary>Кандидат; <see cref="Stack"/> — оцениваемый отряд (последняя <c>ai_tactic_stack</c>), а не слот 2
    /// действия: у выселения (<c>CAiKickOutStackAction</c>) слот 2 — выселяемый постоялец.</summary>
    private sealed record Cand(long Seq, string Player, int Type, string Stack, string Target, int X, int Y,
                               double ScoreIn, string Ret, long PrevSeq = 0, long StackSeq = 0);
    private sealed record Move(long Seq, string Stack, int X, int Y);
    private sealed record Str(long Seq, string Ret, double? Value);
    private sealed record Result(long Seq, string Player, string Stack, string Target, int X, int Y, int Turn,
                                 int Status);

    private sealed class Day
    {
        public required int Turn;
        public required long Seq;
        public MapState? Save;
        public readonly List<Cand> Cands = [];
        public readonly List<Move> Moves = [];
        public readonly List<Str> Strengths = [];
        public readonly List<Result> Results = [];
        public readonly List<(long Seq, string Stack)> TacticStacks = [];
        public readonly List<(long Seq, string Fort, string? Owner)> Forts = [];   // рост (Owner null) и захват
        public readonly Dictionary<string, byte[]> Grids = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Ctx
    {
        public required Day Day;
        public required MapState Save;
        public required IReadOnlyDictionary<int, AiWeights> Weights;
        public required List<Result> History;   // все результаты сеанса карты до кандидата
        public required HashSet<string> RodStacks;   // отряды с кандидатами PlantRod — класс CAiStackRod
        public required EconomyRules Econ;
        public required Dictionary<string, string> Owners;   // отряд карты -> хозяин, по всем сохранениям

        /// <summary>Хозяин — нейтрал (<c>0x5e5358</c>): раса игрока — нейтральная (<c>LRace</c> 4).</summary>
        public bool Neutral(MapPlayer p) => Race(p) == NeutralRace;

        /// <summary>«Раса не из играбельных» (<c>0x5e53a6</c>): порог силы 0,7 вместо 0,6.</summary>
        public bool RaceNeutral(MapPlayer p) => Race(p) is not (0 or 1 or 2 or 3 or 5);

        private int Race(MapPlayer p) => Econ.Races.TryGetValue(p.RaceId, out var r) ? r.RaceType : -1;
    }

    private const int NeutralRace = 4;

    /// <summary>Порог уровня сценария <c>SUGG_LVL</c> (<c>0x61eb31</c>: <c>cmp eax, 0xa; setge</c>).</summary>
    private const int HighSuggestedLevel = 10;

    /// <summary>Клеток в группе.</summary>
    private const int MaxGroupSlots = 6;

    [SkippableFact] public void Scores_MatchProbe65Candidates()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.If(AiCorpus.Sessions.Count == 0, $"нет журналов зонда 6.5+ в {CorpusFiles.Dir}");
        var weights = EconomyLoader.LoadAiWeights(GamePaths.Globals);
        var econ = EconomyLoader.Load(GamePaths.Globals);
        var report = new StringBuilder();
        int bad = 0, ok = 0;
        var history = new List<Result>();
        AiSession? prev = null;
        var lines = new JournalLines(Wanted);

        foreach (var session in AiCorpus.Sessions)
        {
            string name = session.Name;
            var days = Read(session, lines);
            var stats = new SortedDictionary<string, (int Ok, int Inferred, int Bad, int Skip)>(StringComparer.Ordinal);
            var notes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            // Память тактики переживает загрузку той же партии (откат человека) — до дня первого хода ИИ в сессии;
            // другая карта или журнал — пустая (см. AiTacticProbeHarnessTests.Insertion_MatchesProbe).
            if (prev?.Journal != session.Journal || prev.Map != session.Map) history.Clear();
            else if (days.Count > 0) history.RemoveAll(r => r.Turn >= days[0].Turn);
            prev = session;
            var rodStacks = days.SelectMany(d => d.Cands).Where(c => c.Type == (int)AiActionType.PlantRod)
                                .Select(c => c.Stack).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, MapState>(StringComparer.Ordinal);
            MapState Open(string f) => files.TryGetValue(f, out var m) ? m : files[f] = SaveMaps.Map(f);
            foreach (var day in days)
                if (day.Cands.Count > 0 && session.SaveBefore(day.Cands[0].Seq, day.Turn)?.File is { } f) day.Save = Open(f);
            // Отряд, нанятый за день, есть только в следующем сохранении — хозяин ищется по всем сохранениям сессии.
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in session.Saves.Select(x => x.File).OfType<string>())
                foreach (var st in Open(f).Stacks.Values) owners[st.Id] = st.Owner;
            foreach (var day in days)
            {
                var save = day.Save;
                int ri = 0;
                foreach (var c in day.Cands)
                {
                    while (ri < day.Results.Count && day.Results[ri].Seq < c.Seq) history.Add(day.Results[ri++]);
                    string key = $"{c.Type:x2}@{c.Ret}";
                    var v = save is null ? Skipped("нет сохранения дня")
                        : Check(new Ctx { Day = day, Save = save, Weights = weights, History = history, RodStacks = rodStacks, Econ = econ, Owners = owners }, c);
                    var st = stats.GetValueOrDefault(key);
                    stats[key] = v.Ok switch
                    {
                        true when v.Inferred is null => (st.Ok + 1, st.Inferred, st.Bad, st.Skip),
                        true => (st.Ok, st.Inferred + 1, st.Bad, st.Skip),
                        false => (st.Ok, st.Inferred, st.Bad + 1, st.Skip),
                        null => (st.Ok, st.Inferred, st.Bad, st.Skip + 1),
                    };
                    string? note = v.Ok is null ? $"пропуск: {v.Note}" : v.Inferred is { } inf ? $"выведено: {inf}" : null;
                    if (note is not null) notes[$"{key} {note}"] = notes.GetValueOrDefault($"{key} {note}") + 1;
                    if (v.Ok == false) bad++;
                    if (v.Ok == true) ok++;
                    if (v.Ok == false || Environment.GetEnvironmentVariable("H65_ALL") == "1")
                        report.AppendLine($"{name} t{day.Turn:D2} seq {c.Seq} {key} {c.Stack}->{c.Target} ({c.X},{c.Y}) in {c.ScoreIn}: " +
                                          $"{(v.Ok == false ? "BAD" : v.Ok == true ? "ok" : "skip")} {v.Inferred} {v.Note}");
                }
                history.AddRange(day.Results.Skip(ri));
            }
            foreach (var (k, v) in stats)
                output.WriteLine($"{name} {k}: сошлось {v.Ok}, с выведенным входом {v.Inferred}, разошлось {v.Bad}, пропуск {v.Skip}");
            foreach (var (k, v) in notes) output.WriteLine($"  {name} {k}: {v}");
            output.WriteLine($"{name} ИТОГО: сошлось {stats.Values.Sum(v => v.Ok)}, с выведенным входом {stats.Values.Sum(v => v.Inferred)}, " +
                             $"разошлось {stats.Values.Sum(v => v.Bad)}, пропуск {stats.Values.Sum(v => v.Skip)}");
        }
        if (Environment.GetEnvironmentVariable("H65_DUMP") is { Length: > 0 } dump) File.WriteAllText(dump, report.ToString());
        Assert.Equal(0, bad);
        Assert.True(ok > 0, "ни одной сверки");
    }

    // ------------------------------------------------------------------ оценщики

    /// <summary>Итог сверки: <see cref="Ok"/> null — пропуск (<see cref="Note"/> — причина);
    /// <see cref="Inferred"/> — какие невосстановимые входы (ранг 3 разбора) пришлось подобрать.</summary>
    private sealed record Verdict(bool? Ok, string Note, string? Inferred = null);

    private static Verdict Skipped(string why) => new(null, why);

    /// <summary>Первый вариант — входы, восстановленные из сохранения и журнала (метка пустая); прочие — перебор
    /// невосстановимых входов, метка называет подобранное значение.</summary>
    private static Verdict Match(double actual, string line, IEnumerable<(string Label, double Value)> options)
    {
        var tried = new List<double>();
        foreach (var (label, value) in options)
        {
            if (Near(value, actual)) return new(true, $"{line} port {value}", label.Length == 0 ? null : label);
            tried.Add(value);
        }
        return new(false, $"{line} port {string.Join("/", tried.Distinct().Take(8))}");
    }

    private static Verdict Exact(double actual, string line, double value) => Match(actual, line, [("", value)]);

    private static Verdict Check(Ctx x, Cand c)
    {
        if (!x.Save.Players.TryGetValue(c.Player, out var player)) return Skipped("игрока нет в сохранении");
        if (!x.Weights.TryGetValue(player.Attitude, out var w)) return Skipped($"нет строки GAI {player.Attitude}");
        if (c.Ret == "0x44ffe0") return Exact(c.ScoreIn, "смена приказа", AiTacticEval.ChangeOrderScore);
        if (!x.Save.Contains(c.X, c.Y)) return Skipped("нет точки действия");
        if (Position(x, c) is not { } pos) return Skipped("отряда нет в сохранении");
        double d = Cheb(pos, (c.X, c.Y));
        var grid = Grid(x, c.Player);
        int g = AiTacticScore.Influence(grid, c.X, c.Y);
        int today = Status(x, c, x.Day.Turn), yesterday = Status(x, c, x.Day.Turn - 1);
        bool captured = Captured(x, c);
        string line = $"pos {pos} d {d} 4g {g} st {today}/{yesterday} cap {captured}";
        // Построители сценарных приказов (MoveToLocation 0x45b2c1, DefendParty 0x455b8f, DefendLocation 0x45520b) вставляют действие
        // у цели со своим постоянным счётом 150, а тип действия может быть помехи — мешок (тип 0) или атака (20261014-replay, NM KC009b:
        // BG001a seq 246620 / 2051608 — прежде ветка TakeBag считала формулу мешка).
        if (AiTacticProbeHarnessTests.OrderBuilderType(c.Ret) is { } builder)
            return Exact(c.ScoreIn, line, AiTacticScore.Constant(builder)!.Value);
        var type = (AiActionType)c.Type;
        switch (type)
        {
            case AiActionType.DefendCity:
                return Exact(c.ScoreIn, line, AiTacticEval.DefendCityScore(
                    RawPosition(x, c) is { } raw && Same(Fort(x, raw.X, raw.Y)?.Id, c.Target)));
            case AiActionType.GiveItems when c.Ret == "0x45d637":
                return Exact(c.ScoreIn, line, AiTacticEval.NobleMerchantGive(w, g, d, today, yesterday));
            case AiActionType.Guard or AiActionType.AssistMove or AiActionType.SecureCity or AiActionType.Retreat
                or AiActionType.GiveItems or AiActionType.MoveToLocation or AiActionType.DefendParty
                or AiActionType.DefendLocation or AiActionType.MoveToStack:
                return Exact(c.ScoreIn, line, AiTacticScore.Constant(type)!.Value);
            case AiActionType.EnterCityRoam:
                return Exact(c.ScoreIn, line, AiTacticEval.EnterCityRoam(d));
            case AiActionType.PlantRod:
            {
                // 0x461978: сетка, расстояние и память — у кристалла (+0xc), не у клетки жезла.
                if (x.Save.Objects.GetValueOrDefault(c.Target) is not { } cr) return Skipped("кристалла нет в сохранении");
                return Exact(c.ScoreIn, line, AiTacticScore.PlantRod(w, AiTacticScore.Influence(grid, cr.X, cr.Y),
                    Cheb(pos, (cr.X, cr.Y)), cr.Resource == 0, captured, today));
            }
            case AiActionType.TakeBag:
                return Exact(c.ScoreIn, line, AiTacticEval.TakeBag(w, g, d, x.RodStacks.Contains(c.Stack), captured));
            case AiActionType.AttackRod:
                // Свой или союзный жезл (гейт) — «нет кандидата»; при вставленном счёте гейт пройден.
                return Exact(c.ScoreIn, line, AiTacticEval.AttackRod(w, false, false, g, d, captured, today));
            case AiActionType.NobleCity:
            {
                if (Forts(x).FirstOrDefault(f => Same(f.Id, c.Target)) is not { } fort) return Skipped("крепости нет в сохранении");
                var fp = FortPos(fort);
                // Гейты 0x45cea1 (союз, нейтрал, список действий 0x5d32b8) при вставленном счёте пройдены.
                return Exact(c.ScoreIn, line, AiTacticEval.NobleCity(w, false, false, true,
                    AiTacticScore.Influence(grid, fp.X, fp.Y), Cheb(pos, fp),
                    Status(x, c.Stack, c.Target, fp.X, fp.Y, x.Day.Turn), Status(x, c.Stack, c.Target, fp.X, fp.Y, x.Day.Turn - 1)));
            }
            case AiActionType.NobleMerchant:
                return Exact(c.ScoreIn, line, AiTacticEval.NobleMerchantTrade(w, true, g, d, today, yesterday));
            case AiActionType.NobleStack:
                // Гейты 0x45de91 (невидимый, в крепости, нейтрал, призыв, союз, список действий) при вставке пройдены.
                if (TargetOwner(x, c.Target) is not { } owner) return Skipped("цели нет в сохранении");
                return Exact(c.ScoreIn, line, AiTacticEval.NobleStack(w, new AiNobleStackFacts(false, false, false, true,
                    false, false, owner.IsHuman, true, false, g, d, today, yesterday)));
            case AiActionType.VisitRuin:
            {
                // 0x46c02a: сила 0x5dda98 против охраны — строка ai_strength (0x5ddb31) между кандидатами.
                if (StrengthBefore(x, c, "0x5ddb31") is not { } p) return Skipped("нет ai_strength перед кандидатом");
                return Exact(c.ScoreIn, $"{line} p {p}", AiTacticEval.VisitRuin(w, p, g, d, captured, yesterday));
            }
            case AiActionType.VisitMage:
                // Гейты 0x46a6c6 (среднее заклинаний) и 0x46a589 (есть что купить) при вставке пройдены.
                return Exact(c.ScoreIn, line, AiTacticEval.VisitMage(w, g, d, captured, today, yesterday, true, true));
            case AiActionType.VisitMerchant:
                // n = 0x46b0e0 (что можно продать, ранг 3) не восстановлено — подбирается.
                return Match(c.ScoreIn, line, Enumerable.Range(0, 40).Select(n =>
                    ($"продаваемое n = {n}", AiTacticEval.VisitMerchant(w, g, d, false, true, n, today, yesterday))));
            case AiActionType.VisitTrainer:
                // suggestedLevel (CScenarioInfo +0x70) в MapState не читается — подбирается.
                return Match(c.ScoreIn, line, [
                    ("уровень сценария < 10", AiTacticEval.VisitTrainer(w, g, d, captured, false, today)),
                    ("уровень сценария ≥ 10", AiTacticEval.VisitTrainer(w, g, d, captured, true, today))]);
            case AiActionType.VisitTemple:
                return Temple(c.ScoreIn, line, w, d, today);
            case AiActionType.HireUnit:
                return Hire(x, c, line, w, d, captured);
            case AiActionType.HireMercs:
            {
                // 0x4592cc: (I_H_MERCS + 4g − 3d)·свободных; уровень сценария — SUGG_LVL сохранения (≥ 10: место есть — нет кандидата,
                // нет — 0). Свободные места найма (0x458cf8) не восстановлены — подбираются. 20261014-replay r2a: KC0092 у лагеря SI0002
                // 333 / 243 / 189 = (120 − 3d)·3 при d = 3 / 13 / 19; r2b (SUGG_LVL 10, полный отряд) — 18 кандидатов со счётом 0.
                bool high = (x.Save.Limits?.SuggestedLevel ?? 0) >= HighSuggestedLevel;
                return Match(c.ScoreIn, line, Enumerable.Range(0, MaxGroupSlots + 1).Select(n =>
                    ($"свободных мест {n}", AiTacticEval.HireMercs(w, g, d, n, captured, high, today))));
            }
            case AiActionType.HealRevive:
                return Heal(x, c, line, w, captured, today);
            case AiActionType.StayInside:
                return Stay(x, c, line, w);
            case AiActionType.AttackStack or AiActionType.AttackStackBezerk or AiActionType.AttackParty
                or AiActionType.AttackStackRoam:
                return Attack(x, c, line, w, g, d, captured, today, yesterday);
            default:
                return Skipped("оценщик не сверяется");
        }
    }

    /// <summary><c>VisitTemple</c> (<c>0x46c5c5</c>): урон отряда (<c>0x468969</c>, нужен максимум ОЗ с модификаторами) и
    /// код <c>0x5de06e</c> не восстановлены — урон выводится из счёта и обязан лежать в [0,3; 0,9] (кандидат при
    /// уроне ≥ 0,3) или быть больше 0,9 (тогда счёт от <c>2·I_HEAL</c>).</summary>
    private static Verdict Temple(double actual, string line, AiWeights w, double d, int today)
    {
        foreach (bool code6 in new[] { false, true })
        {
            double k = code6 ? 1.1 : 1.0;
            double over = AiTacticEval.VisitTemple(w, 0.95, d, true, code6, today);
            if (Near(over, actual)) return new(true, $"{line} port {over}", $"урон > 0,9{(code6 ? ", код 6" : "")}");
            double damage = (actual / k + 2 * d) / w.Heal;
            double port = AiTacticEval.VisitTemple(w, damage, d, true, code6, today);
            if (damage is >= 0.3 and <= 0.9 && Near(port, actual))
                return new(true, $"{line} урон {damage:F3} port {port}", $"урон в [0,3; 0,9]{(code6 ? ", код 6" : "")}");
        }
        return new(false, $"{line} урон вне [0,3; 0,9] при любом коде");
    }

    /// <summary><c>HireUnit</c> (<c>0x45a34c</c>): занятость крепости — постоялец из сохранения; свободные места
    /// (<c>0x5f4485</c>: вместимость группы минус занятые клетки; вместимость — лидерство) не восстановлены и
    /// подбираются. «Нанять некого» (<c>0x45a498</c>) вставляет флаг <see cref="AiTactic.NoScore"/>.</summary>
    private static Verdict Hire(Ctx x, Cand c, string line, AiWeights w, double d, bool captured)
    {
        if (Forts(x).FirstOrDefault(f => Same(f.Id, c.Target)) is not { } fort) return Skipped("крепости нет в сохранении");
        var fp = FortPos(fort);
        double df = Cheb(Position(x, c)!.Value, fp);
        bool occupied = fort.StackId.Length > 0;
        var opts = new List<(string, double)> { ("нанять некого", AiTacticEval.HireUnit(w, occupied, 0, df, false, captured)) };
        foreach (bool occ in new[] { occupied, !occupied })
            for (int free = 0; free <= 6; free++)
                opts.Add(($"свободных мест {free}{(occ == occupied ? "" : ", занятость ≠ сохранению")}",
                          AiTacticEval.HireUnit(w, occ, free, df, true, captured)));
        return Match(c.ScoreIn, $"{line} dF {df} occ {occupied}", opts);
    }

    /// <summary><c>HealRevive</c> (<c>0x457afa</c>): расстояние и память — у позиции крепости; постоялец, мёртвые
    /// (ОЗ 0) и жизнь лидера — из сохранения; здоровье ниже <c>HP_HEALMIN</c> (<c>0x457d39</c>/<c>0x457e58</c>, ранг 1) — перебором.
    /// </summary>
    private static Verdict Heal(Ctx x, Cand c, string line, AiWeights w, bool captured, int today)
    {
        if (Forts(x).FirstOrDefault(f => Same(f.Id, c.Target)) is not { } fort) return Skipped("крепости нет в сохранении");
        if (!x.Save.Stacks.TryGetValue(c.Stack, out var stack)) return Skipped("отряда нет в сохранении");
        var fp = FortPos(fort);
        double df = Cheb(Position(x, c)!.Value, fp);
        int st = Status(x, c.Stack, c.Target, fp.X, fp.Y, x.Day.Turn);
        bool other = fort.StackId.Length > 0 && !Same(fort.StackId, c.Stack);
        bool dead = stack.UnitIds.Any(u => x.Save.Units.TryGetValue(u, out var unit) && unit.Hp <= 0);
        var facts = new AiHealFacts(true, st, other, true, df, dead, stack.LeaderAlive, captured);
        var opts = new List<(string, double)> { ("", AiTacticEval.HealRevive(w, facts)) };
        opts.Add(("ниже HP_HEALMIN", AiTacticEval.HealRevive(w, facts with { Healthy = false })));
        foreach (bool aff in new[] { true, false })
            foreach (bool dd in new[] { dead, !dead })
                foreach (bool alive in new[] { stack.LeaderAlive, !stack.LeaderAlive })
                    foreach (bool occ in new[] { other, !other })
                        if (dd != dead || alive != stack.LeaderAlive || occ != other)
                            opts.Add(($"{(aff ? "" : "ниже HP_HEALMIN, ")}мёртвые/лидер/постоялец ≠ сохранению",
                                      AiTacticEval.HealRevive(w, facts with { Healthy = aff, HasDead = dd, LeaderAlive = alive, OccupiedByOther = occ })));
        return Match(c.ScoreIn, $"{line} dF {df} stF {st} other {other} dead {dead} alive {stack.LeaderAlive}", opts);
    }

    /// <summary><c>StayInside</c> (<c>0x464b8f</c>): места, разрыв найма (<c>0x46886e</c>) и «неготовые»
    /// (<c>0x5f50fd</c>) — ранг 3, выводятся: при местах счёт кратен <c>I_H_UNITS</c>, без мест разрыв ≥ 1.</summary>
    private static Verdict Stay(Ctx x, Cand c, string line, AiWeights w)
    {
        bool alive = !x.Save.Stacks.TryGetValue(c.Stack, out var stack) || stack.LeaderAlive;
        var opts = new List<(string, double)>();
        foreach (int notReady in new[] { 0, 2 })
        {
            for (int free = 1; free <= 6; free++)
                opts.Add(($"мест {free}, неготовых {(notReady == 0 ? "0" : "≥ 2")}",
                          AiTacticEval.StayInside(w, free, 0, notReady, alive)!.Value));
            double s = notReady == 0 ? c.ScoreIn / 2 : c.ScoreIn + 1;
            double gap = (s - w.HireUnits) / 0.5;
            if (gap >= 1 && Math.Abs(gap - Math.Round(gap)) < 1e-6)
                opts.Add(($"мест 0, разрыв найма {gap}, неготовых {(notReady == 0 ? "0" : "≥ 2")}",
                          AiTacticEval.StayInside(w, 0, gap, notReady, alive)!.Value));
        }
        return Match(c.ScoreIn, line, opts);
    }

    /// <summary>
    /// Атака отряда (<c>0x453585</c>): вес, сетка, расстояние, статусы и сила (<c>ai_strength</c> с
    /// <c>ret_addr 0x45375a</c>) восстановлены; множитель поправок <c>0x453aee</c> (главный враг <c>+0x51c0</c>,
    /// план <c>+0x5158</c>, вид записи) не восстановим и подбирается из произведения ×1,5 / ×1,4 или ×1,15 / ×0,75 /
    /// ×0,1 / ×0,5 / ×0,5 / ×1,25, а у нейтральной цели — ещё хвост ×1,3 или ×0,75 (<c>0x4538a1</c>,
    /// <c>0x466f57</c>). Оценщик перед атакой (<c>0x454151</c>, <c>0x4541d6</c>) вычитает по 0,0001 на каждое
    /// вставленное заклинание или подмогу — это тоже подбирается (0…10).
    /// </summary>
    private static Verdict Attack(Ctx x, Cand c, string line, AiWeights w, int g, double d, bool captured,
                                  int today, int yesterday)
    {
        if (StrengthBefore(x, c, "0x45375a") is not { } p) return Skipped("нет ai_strength перед кандидатом");
        if (TargetOwner(x, c.Target) is not { } owner) return Skipped("цели нет в сохранении");
        bool neutral = x.Neutral(owner), attackerNeutral = x.Neutral(x.Save.Players[c.Player]);
        var kind = (AiActionType)c.Type switch
        {
            AiActionType.AttackStackBezerk => AttackEvalKind.Bezerk,
            AiActionType.AttackStackRoam => AttackEvalKind.Roam,
            AiActionType.AttackParty => AttackEvalKind.Party,
            _ => AttackEvalKind.Normal,
        };
        var facts = new AttackStackFacts(today, neutral, owner.IsHuman, x.RaceNeutral(owner), g, d, 1, p,
                                         false, 0, attackerNeutral, kind);
        var opts = new List<(string, double)>();
        foreach (double a in Adjusts(captured, today, yesterday))
            foreach (var (tailLabel, near, count) in new[] { ("", false, 0), (", сторожит мешок ×1,3", true, 0), (", ×0,75 за толпу", false, 2) })
            {
                double v = AiTacticScore.AttackStack(w, facts with { Adjust = a, NeutralNear = near, NeutralCount = count });
                for (int k = 0; k <= 10; k++)
                    opts.Add(($"поправка ×{a:0.####}{tailLabel}{(k == 0 ? "" : $", {k} действий перед атакой")}", v - k * 0.0001));
                if (!neutral) break;
            }
        return Match(c.ScoreIn, $"{line} p {p} нейтрал {neutral}", opts);
    }

    /// <summary>Возможные произведения множителей <c>0x453aee</c> (чужая функция, сюда входит как одно число).</summary>
    private static IEnumerable<double> Adjusts(bool captured, int today, int yesterday)
    {
        var seen = new HashSet<double>();
        foreach (double enemy in new[] { 1.0, 1.5 })
            foreach (double plan in new[] { 1.0, 1.4, 1.15 })
                foreach (double inside in new[] { 1.0, 0.75 })
                    foreach (double cap in captured ? [0.1] : new[] { 1.0, 0.1 })
                        foreach (double st in today == AiTactic.StatusHalf ? [0.5] : new[] { 1.0, 0.5 })
                            foreach (double yd in yesterday == AiTactic.StatusHalf ? [0.5]
                                     : yesterday == AiTactic.StatusProgress ? [1.25] : new[] { 1.0, 0.5, 1.25 })
                            {
                                double v = enemy * plan * inside * cap * st * yd;
                                if (seen.Add(v)) yield return v;
                            }
    }

    /// <summary>Сила из ближайшей <c>ai_strength</c> с нужным адресом возврата между предыдущим кандидатом и этим.</summary>
    private static double? StrengthBefore(Ctx x, Cand c, string ret) =>
        (x.Day.Strengths.LastOrDefault(s => s.Seq < c.Seq && s.Seq > c.PrevSeq && s.Ret == ret)
         // Вставки заклинания и подмоги перед атакой (0x454151, 0x4541d6) идут после единственного вызова силы
         // по этой цели: если между кандидатами её нет, берётся последняя с начала хода отряда.
         ?? x.Day.Strengths.LastOrDefault(s => s.Seq < c.Seq && s.Seq > c.StackSeq && s.Ret == ret))?.Value;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static double Cheb((int X, int Y) a, (int X, int Y) b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static IEnumerable<MapSite> Forts(Ctx x) => x.Save.Sites.Where(f => f.Kind is SiteKind.Capital or SiteKind.Village);

    private static MapPlayer? TargetOwner(Ctx x, string stack) =>
        x.Owners.TryGetValue(stack, out var owner) ? x.Save.Players.GetValueOrDefault(owner) : null;

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(b));

    // ------------------------------------------------------------------ входы

    /// <summary>Позиция обёртки отряда (слот 3 <c>0x46769f</c> → <c>0x5ed9b2</c>): в крепости (<c>+0x94</c>) —
    /// угол крепости <c>0x5e94eb</c>, иначе клетка отряда. «В крепости» — клетка в пятне крепости.</summary>
    private static (int X, int Y)? Position(Ctx x, Cand c) =>
        RawPosition(x, c) is { } p ? Fort(x, p.X, p.Y) is { } f ? FortPos(f) : p : null;

    private static (int X, int Y)? RawPosition(Ctx x, Cand c)
    {
        var m = x.Day.Moves.LastOrDefault(m => m.Seq < c.Seq && m.Stack.Equals(c.Stack, StringComparison.OrdinalIgnoreCase));
        if (m is not null) return (m.X, m.Y);
        return x.Save.Stacks.TryGetValue(c.Stack, out var s) ? (s.X, s.Y) : null;
    }

    private static MapSite? Fort(Ctx x, int px, int py) =>
        Forts(x).FirstOrDefault(f => px >= f.X && px < f.X + FortSize(f) && py >= f.Y && py < f.Y + FortSize(f));

    private static int FortSize(MapSite f) => f.Kind == SiteKind.Capital ? 5 : 4;

    private static (int X, int Y) FortPos(MapSite f) => AiTacticScore.AnchorCell(f.X, f.Y, FortSize(f));

    /// <summary>Сетка игрока (<c>0x461c85</c>) по крепостям сохранения с ростом и захватами дня до ПЕРВОГО кандидата
    /// игрока за день: сетка строится раз на ход тактики, захваченная посреди хода деревня в неё не попадает.</summary>
    private static byte[] Grid(Ctx x, string player)
    {
        long seq = x.Day.Cands.First(c => Same(c.Player, player)).Seq;
        var changes = x.Day.Forts.Where(f => f.Seq < seq).ToList();
        string key = $"{player}/{changes.Count}";
        if (x.Day.Grids.TryGetValue(key, out var g)) return g;
        var forts = Forts(x)
            .Select(s =>
            {
                int size = (s.Size ?? 0) + changes.Count(f => f.Owner is null && f.Fort.Equals(s.Id, StringComparison.OrdinalIgnoreCase));
                string owner = changes.LastOrDefault(f => f.Owner is not null && f.Fort.Equals(s.Id, StringComparison.OrdinalIgnoreCase)).Owner ?? s.Owner;
                return (Site: s, Size: size, Owner: owner);
            })
            .Where(f => f.Owner.Equals(player, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var (ax, ay) = FortPos(f.Site);
                return (ax, ay, AiTacticScore.FortRadius(f.Site.Kind == SiteKind.Capital, f.Size));
            });
        return x.Day.Grids[key] = AiTacticScore.InfluenceGrid(x.Save.Size, forts);
    }

    /// <summary>Память <c>0x462135</c>: последняя запись (отряд, цель, точка, день).</summary>
    private static int Status(Ctx x, Cand c, int turn) => Status(x, c.Stack, c.Target, c.X, c.Y, turn);

    /// <remarks>Запись <c>0x461fb2</c> ищет запись по (отряд, цель, точка) БЕЗ дня и перезаписывает в ней день и
    /// статус; чтение <c>0x4625ab</c> требует совпадения дня. Значит, в памяти одна запись на тройку: вчерашний
    /// статус пропадает, как только сегодня есть свой (ранг 1 по листингу; корпус — NobleCity seq 8671264).</remarks>
    private static int Status(Ctx x, string stack, string target, int px, int py, int turn) =>
        x.History.LastOrDefault(r => r.X == px && r.Y == py
                                     && r.Stack.Equals(stack, StringComparison.OrdinalIgnoreCase)
                                     && r.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) is { } last
        && last.Turn == turn ? last.Status : 0;

    /// <summary>Захват цели (<c>0x469a75</c>): карта цель → отряд <c>+0x513c</c> пополняется статусом 5
    /// (<c>0x469a37</c>, вставка без перезаписи); истина, если цель захвачена другим отрядом. Карта — на игрока и
    /// день (ранг 3: когда движок её чистит, не разобрано).</summary>
    private static bool Captured(Ctx x, Cand c)
    {
        var first = x.History.FirstOrDefault(r => r.Turn == x.Day.Turn && r.Status == AiTactic.StatusProgress
                                                  && r.Seq > x.Day.Seq
                                                  && r.Player.Equals(c.Player, StringComparison.OrdinalIgnoreCase)
                                                  && r.Target.Equals(c.Target, StringComparison.OrdinalIgnoreCase));
        return first is not null && !first.Stack.Equals(c.Stack, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ журнал

    private static readonly string[] Wanted =
        ["\"ev\":\"save\"", "\"ev\":\"stack_move\"", "\"ev\":\"ai_tactic_candidate\"", "\"ev\":\"ai_strength\"",
         "\"ev\":\"ai_tactic_result\"", "\"ev\":\"city_grow\"", "\"ev\":\"fort_capture\"", "\"ev\":\"ai_tactic_stack\""];

    /// <summary>Дни сессии; строки журнала — <paramref name="lines"/> (<see cref="JournalLines"/> по <see cref="Wanted"/>: журнал
    /// читается один раз на все свои сессии).</summary>
    private static List<Day> Read(AiSession session, JournalLines lines)
    {
        var days = new List<Day>();
        Day? day = null;
        foreach (var (k, seq, l) in lines.Of(session))
        {
            using var doc = JsonDocument.Parse(l);
            var e = doc.RootElement;
            string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double? D(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            switch (k)
            {
                case 0:
                    // День открывает первая строка save с новым днём — любая удачная постоянная (в партии человека
                    // бывают дни только с автосохранением).
                    if (!e.GetProperty("ok").GetBoolean() || e.GetProperty("temp").GetBoolean()) break;
                    int turn = e.GetProperty("turn").GetInt32();
                    if (day?.Turn != turn) days.Add(day = new Day { Turn = turn, Seq = seq });
                    break;
                case 1 when day is not null && e.GetProperty("ret").GetBoolean():
                    day.Moves.Add(new Move(seq, S("stack")!, e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32()));
                    break;
                case 2 when day is not null:
                    var a = e.GetProperty("action");
                    if (a.ValueKind != JsonValueKind.Object) break;
                    var p = a.GetProperty("point");
                    day.Cands.Add(new Cand(seq, S("player") ?? "", a.GetProperty("type").GetInt32(),
                        a.GetProperty("stack").GetString() ?? "", a.GetProperty("target").GetString() ?? "",
                        p.ValueKind == JsonValueKind.Object ? p.GetProperty("x").GetInt32() : -1,
                        p.ValueKind == JsonValueKind.Object ? p.GetProperty("y").GetInt32() : -1,
                        e.GetProperty("score_in").GetDouble(), S("ret_addr")!));
                    break;
                case 3 when day is not null:
                    day.Strengths.Add(new Str(seq, S("ret_addr")!, D("ret")));
                    break;
                case 4 when day is not null:
                    var rp = e.GetProperty("point");
                    day.Results.Add(new Result(seq, S("player") ?? "", S("stack") ?? "", S("target") ?? "",
                        rp.ValueKind == JsonValueKind.Object ? rp.GetProperty("x").GetInt32() : -1,
                        rp.ValueKind == JsonValueKind.Object ? rp.GetProperty("y").GetInt32() : -1,
                        e.TryGetProperty("turn", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1,
                        e.GetProperty("status").GetInt32()));
                    break;
                case 5 when day is not null && e.GetProperty("ret").GetBoolean():
                    day.Forts.Add((seq, S("fort")!, null));
                    break;
                case 6 when day is not null && e.GetProperty("ret").GetBoolean():
                    day.Forts.Add((seq, S("fort")!, S("owner_after")));
                    break;
                case 7 when day is not null:
                    day.TacticStacks.Add((seq, S("stack") ?? ""));
                    break;
            }
        }
        foreach (var d in days)
        {
            d.Results.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            d.Cands.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            d.Moves.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            d.Strengths.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            d.TacticStacks.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            for (int i = 0, j = -1; i < d.Cands.Count; i++)
            {
                while (j + 1 < d.TacticStacks.Count && d.TacticStacks[j + 1].Seq < d.Cands[i].Seq) j++;
                if (j >= 0) d.Cands[i] = d.Cands[i] with { Stack = d.TacticStacks[j].Stack };
                long stackSeq = j >= 0 ? d.TacticStacks[j].Seq : 0;
                d.Cands[i] = d.Cands[i] with { StackSeq = stackSeq, PrevSeq = Math.Max(i > 0 ? d.Cands[i - 1].Seq : 0, stackSeq) };
            }
        }
        return days;
    }
}
