using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 23 <c>port-synth-23-ai-long</c> (<c>tools/synth-maps/maps_long.py</c>): длинная партия трёх ИИ на очень трудной — войны
/// ИИ между собой, наймы весь поход, цели событий победы с дня 30 (множество <c>+0x51dc</c>). Прогноз порта: партия доживает до конца без
/// победителя, человек за стеной гор не воюет, а ситуаций под строки реестра (И21 … И93) за партию набирается достаточно — таблица
/// печатается. Сверка с оригиналом — каждый день с сохранения (<see cref="Long_AiTurnsMatchTheOriginalJournal"/>).</summary>
public partial class PortSynthMapsTests
{
    private const string LongMap = "port-synth-23-ai-long";

    /// <summary>
    /// Партия порта с начала сценария на очень трудной до последнего дня памятки (сиды — <c>D2_LONG_SEEDS</c>, по умолчанию 1; дней —
    /// <c>D2_LONG_DAYS</c>, по умолчанию все): все три ИИ живы (столица своя) и ходят до конца, ни одного боя с человеком, победителя нет, бои
    /// ИИ ↔ ИИ идут, и под каждую строку реестра, ради которой карта сделана, ситуации есть (по сумме сидов: бегство защитника и дальняя
    /// цель на отдельном сиде могут не выпасть).
    /// <para>Прогноз 19.10 (сиды 1–6, по 7–16 с): все ИИ живы до дня 60, боёв 68–120 (ИИ ↔ ИИ 52–102), бегств защитника 2–12, найм лидера
    /// с дня 30 — 32–59, AttackCity по целям событий победы с дня 30 — 32–82, подмога 401–469; таблица — разбор
    /// <c>docs/synthetic-maps-port/analysis/20261019c-map23.md</c>.</para>
    /// </summary>
    /// <remarks>Тест — в <see cref="PortSynthMapsTests_Long"/> (своим классом, параллельно остальным).</remarks>
    internal void Long_ThreeAiAtWar_ToTheLastDay_PortForecast()
    {
        Skip.IfNot(MapIds().Contains(LongMap));
        var facts = Facts(LongMap);
        int days = int.Parse(Environment.GetEnvironmentVariable("D2_LONG_DAYS") ?? Str(facts, "days"), CultureInfo.InvariantCulture);
        var seeds = (Environment.GetEnvironmentVariable("D2_LONG_SEEDS") ?? "1").Split(',').Select(x => uint.Parse(x, CultureInfo.InvariantCulture));
        var failures = new List<string>();
        var sums = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (uint seed in seeds)
        {
            var run = LongPlay(facts, days, seed);
            Out.WriteLine($"== сид {seed}: дней {run.Session.Map.Turn}, {run.Ms / 1000} с");
            foreach (var line in run.Daily) Out.WriteLine("  " + line);
            Out.WriteLine("  строка реестра | ситуаций | что считается");
            foreach (var (row, count, what) in run.Rows) Out.WriteLine($"  {row} | {count} | {what}");
            if (run.Session.Over) failures.Add($"сид {seed}: победитель {run.Session.State.Info.Winner}");
            if (run.Session.Map.Turn != days) failures.Add($"сид {seed}: дней {run.Session.Map.Turn}");
            if (run.HumanBattles > 0) failures.Add($"сид {seed}: боёв с человеком {run.HumanBattles}");
            foreach (string ai in facts.GetProperty("ai").EnumerateArray().Select(a => a.GetString()!))
            {
                var capital = run.Session.Map.Sites.Single(x => x.Kind == SiteKind.Capital && Same(run.Start[ai], x.Id));
                if (!Same(capital.Owner, ai)) failures.Add($"сид {seed}: столица {ai} у {capital.Owner}");
                if (run.LastPick[ai] < days - 1) failures.Add($"сид {seed}: {ai} последний выбор — день {run.LastPick[ai]}");
            }
            foreach (var (row, count, _) in run.Rows) sums[row] = sums.GetValueOrDefault(row) + count;
        }
        // Редкие ситуации (бегство защитника И93, дальняя цель И25) на отдельном сиде могут и не выпасть — их не требуем: партия оригинала
        // (20261020-port-synth) их дала (AttackStack с отрицательным счётом 10, бегств защитника 2), а с 20.10 (уход по юниту, отступление
        // бежавшего) сид 1 порта даёт И25 ноль. Прочие строки — есть на любом сиде.
        failures.AddRange(sums.Where(p => p.Value == 0 && p.Key is not ("И25" or "И93")).Select(p => $"{p.Key} — ситуаций нет ни на одном сиде"));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Итог прогона порта: сессия, строки «реестр → ситуаций», дневник, бои с человеком, последний день выбора каждого ИИ.</summary>
    private sealed record LongRun(GameSession Session, List<(string Row, int Count, string What)> Rows, List<string> Daily, int HumanBattles,
                                  Dictionary<string, int> LastPick, Dictionary<string, string> Start, long Ms);

    /// <summary>Партия порта карты 23 с начала сценария на очень трудной: предложения человеку — «Нет», как в памятке.</summary>
    private LongRun LongPlay(JsonElement facts, int days, uint seed)
    {
        var session = Begin(LongMap, 3, seed);
        string human = session.ActiveHuman;
        var ai = facts.GetProperty("ai").EnumerateArray().Select(a => a.GetString()!).ToList();
        var targets = facts.GetProperty("zones").EnumerateArray().Single(z => Str(z, "key") == "win").GetProperty("targets")
                           .EnumerateArray().Select(t => t.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capitals = ai.ToDictionary(p => p, p => session.Map.Sites.Single(x => x.Kind == SiteKind.Capital && Same(x.Owner, p)).Id);
        var trace = new LongTrace(() => session.Map, targets);
        session.AiTrace = trace;
        var seen = new Recorder();
        session.Watch(seen);
        var daily = new List<string>();
        var tally = new LongTally();
        var watch = Stopwatch.StartNew();
        for (int day = 1; day < days && !session.Over; day++)
        {
            var before = session.Map.Stacks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            tally.ItemStackDays += session.Map.Stacks.Values.Count(s => ai.Contains(s.Owner, StringComparer.OrdinalIgnoreCase) && s.Items.Count > 0);
            int picksBefore = trace.Picks.Count;
            long ms = watch.ElapsedMilliseconds;
            var report = session.EndTurn();
            tally.Potions += report.Events.Count(l => l.StartsWith("зелье ", StringComparison.Ordinal) && !l.Contains("отказ", StringComparison.Ordinal));
            tally.NewStackPicks += trace.Picks.Skip(picksBefore).Count(p => !before.Contains(p.Stack));
            foreach (var offer in session.State.Offers.Where(o => Same(o.To, human)).ToList())
            {
                tally.Offers++;
                if (offer.Trade is not null) session.AnswerTradeOffer(offer.From, human, accept: false);
                else if (!offer.Exchange) session.AnswerAllianceOffer(offer.From, human, accept: false);
            }
            if (day % 5 == 0 || day == days - 1) daily.Add($"день {session.Map.Turn} ({(watch.ElapsedMilliseconds - ms) / 1000.0:0.0} с): " +
                                                           LongStanding(session.Map, ai) + $"; боёв {seen.Outcomes.Count}");
        }
        var rows = LongRows(trace, seen.Outcomes, ai, tally, daily);
        var last = ai.ToDictionary(p => p, p => trace.Picks.Where(x => Same(x.Player, p)).Select(x => x.Day).DefaultIfEmpty(0).Max());
        int humanBattles = seen.Outcomes.Count(o => Same(o.AttackerOwner, human) || Same(o.DefenderOwner, human));
        return new LongRun(session, rows, daily, humanBattles, last, capitals, watch.ElapsedMilliseconds);
    }

    /// <summary>Счётчики дня, которых нет в трассе: зелья вне боя, выборы отрядов того же дня, отрядо-дни с предметами, предложения.</summary>
    private sealed class LongTally
    {
        public int Potions { get; set; }

        public int NewStackPicks { get; set; }

        public int ItemStackDays { get; set; }

        public int Offers { get; set; }
    }

    /// <summary>Отрядов, крепостей и золота у каждого ИИ.</summary>
    private static string LongStanding(MapState map, IEnumerable<string> ai) => string.Join("; ", ai.Select(p =>
        $"{p} отрядов {map.Stacks.Values.Count(s => Same(s.Owner, p))}, " +
        $"крепостей {map.Sites.Count(x => Same(x.Owner, p) && x.Kind is SiteKind.Village or SiteKind.Capital)}, золото {map.Players[p].Bank.Gold}"));

    /// <summary>Строки реестра → ситуаций за партию порта (и сводка выборов, шагов и боёв в дневник).</summary>
    private static List<(string Row, int Count, string What)> LongRows(LongTrace trace, IReadOnlyList<BattleOutcome> outcomes, IReadOnlyList<string> ai,
                                                                      LongTally tally, List<string> daily)
    {
        bool IsAi(string p) => ai.Contains(p, StringComparer.OrdinalIgnoreCase);
        var aiAi = outcomes.Where(o => IsAi(o.AttackerOwner) && IsAi(o.DefenderOwner)).ToList();
        var withAi = outcomes.Where(o => IsAi(o.AttackerOwner) || IsAi(o.DefenderOwner)).ToList();
        var picks = trace.Picks;
        var steps = trace.Steps;
        int Of(params AiActionType[] types) => picks.Count(p => types.Contains(p.Type));
        string defendCity = $"|{(int)StackOrder.DefendCity}";
        daily.Add("выборы: " + string.Join(", ", picks.GroupBy(p => p.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        daily.Add("шаги: " + string.Join(", ", steps.GroupBy(x => x.Type).Select(g => $"{g.Key} {g.Count()}")) +
                  $"; найм лидера по дням: {string.Join(" ", steps.Where(x => x.Type == AiStepType.HireLeader).Select(x => x.Day))}");
        daily.Add($"предложений человеку (ответ «Нет») {tally.Offers}");
        daily.Add($"бои: всего {outcomes.Count}, ИИ ↔ ИИ {aiAi.Count}, с нейтралами {withAi.Count - aiAi.Count}, по дням ИИ ↔ ИИ: " +
                  string.Join(" ", aiAi.Select(o => o.Day)));
        return
        [
            ("И21", Of(AiActionType.HireUnit, AiActionType.TakeBag, AiActionType.AttackStack, AiActionType.AttackStackBezerk),
             "выборы HireUnit, TakeBag, AttackStack, берсерк на очень трудной"),
            ("И25", picks.Count(p => p.Type == AiActionType.AttackStack && p.Score < 0), "AttackStack с отрицательным счётом (дальняя цель)"),
            ("И26", tally.Potions + withAi.Count(o => o.ItemsUsed.Count > 0), "зелья ИИ вне боя и бои ИИ с предметами"),
            ("И31", tally.NewStackPicks, "выборы отрядов, созданных в тот же день (id нового отряда в окне)"),
            ("И32", picks.Count(p => p.Prep is not null), "выборы производных кандидатов (подготовка атаки)"),
            ("И33", tally.ItemStackDays + Of(AiActionType.VisitMerchant, AiActionType.HireUnit), "отрядо-дни ИИ с предметами, лавка, найм в отряд"),
            ("И36", Of(AiActionType.DefendCity) + steps.Count(x => x.Type == AiStepType.HireLeader && x.Target.EndsWith(defendCity, StringComparison.Ordinal)),
             "DefendCity и найм лидера с подприказом «защита города»"),
            ("И37", withAi.Count, "бои с участием ИИ (политика боя)"),
            ("И38", aiAi.Count, "бои ИИ ↔ ИИ в свободном дне"),
            ("И40", trace.Supported, "атаки, у цели которых в ±10 клеток есть свои отряды хозяина цели или гость крепости"),
            ("И41", trace.LateTargets, "выборы AttackCity по целям событий победы с дня 30"),
            ("И42", outcomes.Count(o => IsAi(o.AttackerOwner) && !o.AttackerWon || IsAi(o.DefenderOwner) && o.AttackerWon),
             "проигранные бои ИИ (силы пересобираются, «главный» отряд)"),
            ("И43", steps.Count(x => x.Type == AiStepType.HireLeader && x.Day >= 30), "найм лидера с дня 30"),
            ("И70", picks.Count(p => p.Prep is { Kind: AiPrepKind.Assist }) + Of(AiActionType.AssistMove), "подмога перед атакой и AssistMove"),
            ("И93", withAi.Count(o => IsAi(o.DefenderOwner) && o.DefendersRetreated > 0), "бои, где защитник ИИ бежал"),
        ];
    }

    /// <summary>Трасса порта для прогноза карты 23: выборы и шаги, атаки с поддержкой у цели (И40) и выборы целей множества с дня 30 (И41).</summary>
    private sealed class LongTrace(Func<MapState> map, IReadOnlySet<string> winTargets) : IAiTrace
    {
        /// <summary>Квадрат поддержки вокруг цели (<c>0x5dda98</c>: ±10 клеток).</summary>
        private const int SupportRange = 10;

        public List<(string Player, int Day, string Stack, AiActionType Type, string Target, double Score, AiPrep? Prep)> Picks { get; } = [];

        public List<(string Player, int Day, AiStepType Type, string Id, string Target)> Steps { get; } = [];

        public int Supported { get; private set; }

        public int LateTargets { get; private set; }

        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates)
        {
            Picks.Add((playerId, day, action.KeyStack, action.Type, action.Target, score, action.Prep));
            if (action.Type is AiActionType.AttackCity && day >= 30 && winTargets.Contains(action.Target)) LateTargets++;
            if (action.Type is not (AiActionType.AttackStack or AiActionType.AttackCity)) return;
            var m = map();
            string owner = m.Stacks.TryGetValue(action.Target, out var t) ? t.Owner
                           : m.Sites.FirstOrDefault(x => x.Id.Equals(action.Target, StringComparison.OrdinalIgnoreCase))?.Owner ?? "";
            if (owner.Length == 0) return;
            bool near(int x, int y) => Math.Abs(x - action.X) <= SupportRange && Math.Abs(y - action.Y) <= SupportRange;
            if (m.Stacks.Values.Any(s => s.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)
                                         && !s.Id.Equals(action.Target, StringComparison.OrdinalIgnoreCase) && near(s.X, s.Y)))
                Supported++;
        }

        public void Stepped(string playerId, int day, AiExecuted step, int number) =>
            Steps.Add((playerId, day, step.Action.Type, step.Action.Id, step.Action.Target));

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) { }
    }
}
