using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

public partial class PortSynthMapsTests
{
    /// <summary>
    /// Наблюдатель дня с сохранения (<see cref="SavedAiDays"/>): ставит R250 движка у боёв и сверяет состояние дня с журналом движка —
    /// расхождения копятся в <see cref="Notes"/> (печать «бой:», <see cref="AiDay.Checks"/>).
    /// <list type="bullet">
    /// <item>Бой порта, которому у движка есть пара (тот же нападающий и защитник, по порядку дня), идёт с генератором движка перед первым
    /// броском его боя, а после боя генератор ставится как у движка перед следующим броском журнала (все броски R250 схема 7.5 пишет —
    /// <c>rng</c> в бою, <c>ai_rng</c> вне его). Так расхождение исхода боя не сдвигает броски остатка дня (карта 23, день 22: бой KC001a с
    /// руиной RU0005 сдвигал кости дуэли S143KC0034 → S143KC001f). Боя без пары у движка генератор не трогает. Бой играет сам наблюдатель
    /// (основной, <see cref="GameSession.Observer"/>) — тем же автобоем, что <c>GameSession.AiBattle</c>, но с записью бросков.</item>
    /// <item>Бой: юниты и здоровье на входе (<c>battle_start</c>, порядок <c>unitsInfo</c>), броски (max, r) против строк <c>rng</c> боя,
    /// здоровье после — против <c>battle_end</c> (у движка он после опыта и повышений, у порта сверяется карта), победитель.</item>
    /// <item>R250 у каждого выбора тактики (<c>ai_tactic_pick</c> 4 → 0, <c>rng_before</c>) и в начале хода ИИ (<c>ai_turn</c> begin; броски
    /// начала хода — раскрытие шпиона <c>spy_discover</c>, шанс событий начала хода <c>event_chance</c> — движок делает раньше строки, а
    /// наблюдатель порта зовётся до них: их столько и впереди; replay-human, день 1: события EV001c, EV0002, EV0003 у трёх ИИ).</item>
    /// <item>Земля до выбора: клетки обходов <c>land_grow</c> и вся земля игрока из <c>terrain_day</c>.</item>
    /// </list>
    /// Карта 23 (20.10, ветка <c>r20/rest</c>): этой сверкой найдены Б80 (порядок юнитов боя), И151 (помощники павшего), И152 (земля захвата,
    /// цепочка квадратов жил), И153 (отход после дуэли) и харнессная ошибка <see cref="JournalRng.BeforeDraws"/>.
    /// </summary>
    private sealed class DayCheck(GameSession session, JournalRng rng, IEnumerable<CorpusEvent> slice) : ITurnObserver
    {
        /// <summary>Сколько шагов R250 вперёд искать состояние движка от состояния порта.</summary>
        private const int AheadLimit = 2000;

        private readonly List<CorpusEvent> _slice = [.. slice];
        private readonly List<(string Attacker, string Defender, long Seq, long End)> _battles = Battles(slice);
        private readonly HashSet<long> _seen = [];
        private (long Seq, long End)? _open;
        private BattleState? _state;
        private long _landFrom;

        /// <summary>Все броски R250 вне боя журнал пишет с игроком хода (<c>ai_rng</c> зонда 7.5): у журналов раньше 7.5 (карта 5,
        /// <c>20261016e-av</c>, схема 7.3) счётчик расходится с генератором порта на постоянные 28–32 шага при всех выборах совпавших —
        /// положение генератора у выборов по ним не сверяется.</summary>
        private readonly bool _drawsKnown = slice.OfType<AiRngEvent>().Any(r => r.Player is not null);

        public List<string> Notes { get; } = [];

        /// <summary>Бои движка этого дня, которых у порта не было.</summary>
        public IEnumerable<string> Remaining => _battles.Select(b => $"{b.Attacker}→{b.Defender} у порта нет (seq {b.Seq})");

        private static List<(string, string, long, long)> Battles(IEnumerable<CorpusEvent> slice)
        {
            var list = new List<(string, string, long, long)>();
            BattleStartEvent? open = null;
            foreach (var e in slice)
                if (e is BattleStartEvent { Round: 0 } bs) open = bs;
                else if (e is BattleEndEvent be && open is not null)
                {
                    list.Add((open.AttackerGroup, open.DefenderGroup, open.Seq, be.Seq));
                    open = null;
                }
            return list;
        }

        public void PlayerTurn(string playerId)
        {
            var begin = _slice.OfType<AiTurnEvent>().FirstOrDefault(t => t is { Phase: "begin" } && Same(t.Player ?? "", playerId) && !_seen.Contains(t.Seq));
            if (begin is null) return;
            _seen.Add(begin.Seq);
            _turnFrom = begin.Seq;
            if (!_drawsKnown) return;
            if ((rng.AtAhead(begin.Seq, begin.RngBefore + 1) ?? rng.At(begin.Seq, begin.RngBefore + 1)) is not { } g)
            {
                Notes.Add($"ход {playerId}: R250 движка не подобран");
                return;
            }
            long prev = _slice.OfType<AiTurnEvent>().Where(t => t.Phase == "end" && t.Seq < begin.Seq).Select(t => t.Seq).DefaultIfEmpty(0).Max();
            int early = _slice.OfType<AiRngEvent>().Count(r => r.Kind == "r250" && r.Site is ("spy_discover" or "event_chance")
                                                              && r.Seq > prev && r.Seq < begin.Seq);
            int ahead = Ahead(g);
            if (ahead != early) Notes.Add($"ход {playerId}: R250 движка впереди на {Shift(ahead)} (n {begin.RngBefore}), бросков начала хода {early}");
        }

        /// <summary>Выбор тактики порта: R250 и земля — как у движка в его выбор того же отряда.</summary>
        public void Picked(string playerId, AiTacticOption action)
        {
            var pick = _slice.OfType<AiTacticPickEvent>().FirstOrDefault(p => p is { PhaseBefore: 4, Chosen: [{ Action: { } a }, ..] } && !_seen.Contains(p.Seq)
                                                                             && Same(p.Player ?? "", playerId) && Same(a.Stack ?? "", action.KeyStack));
            if (pick is null) return;
            _seen.Add(pick.Seq);
            CheckLand(pick.Seq, $"до выбора {action.KeyStack}");
            if (_drawsKnown && (rng.AtAhead(pick.Seq, pick.RngBefore + 1) ?? rng.At(pick.Seq, pick.RngBefore + 1)) is { } g
                && Ahead(g) is var ahead and not 0)
                Notes.Add($"выбор {action.KeyStack} {action.Type}: R250 движка впереди на {Shift(ahead)} (n {pick.RngBefore}, seq {pick.Seq})");
        }

        /// <summary>На сколько шагов генератор движка <paramref name="engine"/> впереди генератора порта (отрицательное — позади); не
        /// нашёлся в пределах <see cref="AheadLimit"/> — <see cref="int.MinValue"/>.</summary>
        private int Ahead(EngineRng engine)
        {
            if (Steps(session.Rng, engine) is { } k) return k;
            return Steps(engine, session.Rng) is { } back ? -back : int.MinValue;
        }

        private static int? Steps(EngineRng from, EngineRng to)
        {
            var probe = from.Clone();
            var table = to.TableSnapshot();
            for (int k = 0; k < AheadLimit; k++, probe.Step())
                if (probe.Index == to.Index && probe.TableSnapshot().AsSpan().SequenceEqual(table)) return k;
            return null;
        }

        private static string Shift(int ahead) => ahead == int.MinValue ? "?" : ahead.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Земля порта против строк движка с прошлой проверки: вся земля игрока (<c>terrain_day</c>) и клетки обходов (<c>land_grow</c>).</summary>
        private void CheckLand(long upTo, string where)
        {
            var map = session.Map;
            foreach (var td in _slice.OfType<TerrainDayEvent>().Where(g => g.Seq > _landFrom && g.Seq < upTo))
            {
                var port = new HashSet<(int X, int Y)>();
                for (int x = 0; x < map.Size; x++)
                    for (int y = 0; y < map.Size; y++)
                        if (map.Tiles[x, y].Terrain == td.Terrain) port.Add((x, y));
                var onlyEngine = td.Cells.Where(c => !port.Contains(c)).ToList();
                var onlyPort = port.Where(c => !td.Cells.Contains(c)).ToList();
                if (onlyEngine.Count + onlyPort.Count > 0)
                    Notes.Add($"{where}: земля {td.Player} (террейн {td.Terrain}) — только у движка {onlyEngine.Count}: {Cells(onlyEngine)}; " +
                              $"только у порта {onlyPort.Count}: {Cells(onlyPort)}");
            }
            foreach (var g in _slice.OfType<LandGrowEvent>().Where(g => g.Seq > _landFrom && g.Seq < upTo))
            {
                var bad = g.Cells.Where(c => map.Tiles[c.X, c.Y].Terrain != g.Terrain).ToList();
                if (bad.Count > 0)
                    Notes.Add($"{where}: рост земли ({g.X},{g.Y}) квота {g.Quota} террейн {g.Terrain} — у порта иная земля в {bad.Count} из {g.Cells.Count}: {Cells(bad)}");
            }
            _landFrom = upTo;
        }

        private static string Cells(IEnumerable<(int X, int Y)> cells) => string.Join(" ", cells.Take(10).Select(c => $"{c.X}:{c.Y}"));

        public void StackMoving(StackMove move) { }

        private readonly HashSet<long> _answered = [];

        /// <summary>Строка начала хода ИИ, который сейчас играет порт: ответ человека ищется после неё.</summary>
        private long _turnFrom;

        /// <summary>Предложение ИИ человеку — ответ человека из журнала сейчас, в ходе ИИ, как у движка (<see cref="ITurnObserver.Offered"/>):
        /// союз — первая неотвеченная <c>alliance_change</c> человека с предлагавшим, сделка — <c>trade_accept</c> той же пары. Прежде
        /// наблюдатель не отвечал, предложение ждало в <see cref="GameState.Offers"/>, и у порта не было ни отказа (<c>D_REF_ALLY</c>), ни
        /// реплики ИИ «отказался» с её броском R250 (<c>ai_chat</c> <c>PLAYER_REFUSED_ALL</c>; replay-human, дни 31 и 41: R250 на шаг позади).</summary>
        public void Offered(AllianceOffer offer)
        {
            var report = new List<string>();
            if (offer.IsAlliance)
            {
                var answer = _slice.OfType<AllianceChangeEvent>().FirstOrDefault(e => e.Seq > _turnFrom && !_answered.Contains(e.Seq)
                                                                                     && e.HumanA == true && Same(e.A, offer.To) && Same(e.B, offer.From));
                if (answer is null)
                {
                    Notes.Add($"союз {offer.From} → {offer.To}: ответа человека в журнале нет");
                    return;
                }
                _answered.Add(answer.Seq);
                session.AnswerAllianceOffer(offer.From, offer.To, answer.Accept, report);
            }
            else if (offer.Trade is not null)
            {
                var answer = _slice.OfType<TradeAcceptEvent>().FirstOrDefault(e => e.Seq > _turnFrom && !_answered.Contains(e.Seq)
                    && ((Same(e.A, offer.To) && Same(e.B, offer.From)) || (Same(e.A, offer.From) && Same(e.B, offer.To))));
                if (answer is null)
                {
                    Notes.Add($"сделка {offer.From} → {offer.To}: ответа человека в журнале нет");
                    return;
                }
                _answered.Add(answer.Seq);
                session.AnswerTradeOffer(offer.From, offer.To, answer.Accepted, report);
            }
        }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state)
        {
            int i = _battles.FindIndex(b => Same(b.Attacker, state.AttackerGroup) && Same(b.Defender, state.DefenderGroup));
            if (i < 0)
            {
                (_open, _state) = (null, null);
                Notes.Add($"{state.AttackerGroup}→{state.DefenderGroup} у движка нет");
                return null;
            }
            var b = _battles[i];
            _battles.RemoveAt(i);
            (_open, _state) = ((b.Seq, b.End), state);
            var start = _slice.OfType<BattleStartEvent>().FirstOrDefault(e => e.Seq == b.Seq);
            if (start is not null) CompareStart(start.Units, state);
            if (rng.BeforeDraws(b.Seq, b.End) is { } g) session.Rng.SetState(g.TableSnapshot(), g.Index);
            // Броски чужого потока внутри боя (показ боя с человеком берёт rng(16) из того же генератора, C2HarnessTests) — пропускаются
            // генератором в том же месте потока: модель о них не знает (окружение оригинала, не правило боя).
            var foreign = start is null ? []
                : _slice.OfType<RngEvent>().Where(e => e.Seq > b.Seq && e.Seq < b.End && !e.Std).OrderBy(e => e.N)
                        .Select(e => e.Tid == start.Tid ? 0 : (int)e.Max).ToList();
            var draws = new RecordingRng(session.Rng, foreign);
            var rules = session.Book!.Rules;
            var battle = new BattleSession(state, new BattleSim(state, draws, rules.Constants), rules.Constants) { Auto = true };
            battle.PlayOut();
            // Броски потока боя (tid battle_start): поток показа боя с человеком берёт rng(16) из того же генератора (C2HarnessTests).
            var journal = _slice.OfType<RngEvent>().Where(e => e.Seq > b.Seq && e.Seq < b.End && !e.Std && (start is null || e.Tid == start.Tid))
                                .Select(e => ((int)e.Max, (int)e.R)).ToList();
            int n = 0;
            while (n < journal.Count && n < draws.Draws.Count && journal[n] == draws.Draws[n]) n++;
            if (n < journal.Count || n < draws.Draws.Count)
                Notes.Add($"{state.AttackerGroup}→{state.DefenderGroup} броски: движок {journal.Count}, порт {draws.Draws.Count}, первое расхождение №{n}: " +
                          $"движок …{Draws(journal, n)}; порт …{Draws(draws.Draws, n)}");
            return battle.Log;
        }

        private static string Draws(List<(int Max, int R)> d, int i) => string.Join(" ", d.Skip(Math.Max(0, i - 2)).Take(8).Select(x => $"{x.Max}:{x.R}"));

        /// <summary>Вход боя: юниты, здоровье и порядок <c>unitsInfo</c> (<c>battle_start</c>).</summary>
        private void CompareStart(IReadOnlyList<UnitState> engine, BattleState state)
        {
            var diffs = new List<string>();
            foreach (var u in engine)
                if (state.Unit(u.Id) is not { } m) diffs.Add($"{u.Id} нет у порта (у движка hp {u.Hp})");
                else if (m.Hp != u.Hp) diffs.Add($"{u.Id} hp {u.Hp}/{m.Hp}");
            foreach (var m in state.Units.Where(m => !engine.Any(u => Same(u.Id, m.Id)))) diffs.Add($"{m.Id} нет у движка (у порта hp {m.Hp})");
            string order = string.Join(" ", engine.Select(u => u.Id)), mine = string.Join(" ", state.Units.Select(u => u.Id));
            if (!Same(order, mine)) diffs.Add($"порядок движка [{order}], порта [{mine}]");
            if (diffs.Count > 0) Notes.Add($"{state.AttackerGroup}→{state.DefenderGroup} вход: " + string.Join("; ", diffs));
        }

        public void BattleEnded(BattleOutcome outcome)
        {
            if (_open is not { } b) return;
            _open = null;
            if (_slice.OfType<BattleEndEvent>().FirstOrDefault(e => e.Seq == b.End) is { } end && _state is not null)
            {
                // Конец боя у движка — после опыта и повышений (battle_end за xp_group): сверка — с картой порта после боя.
                var diffs = end.Units.Select(u => (u, m: session.Map.Units.GetValueOrDefault(u.Id)))
                                     .Where(p => (p.m?.Hp ?? 0) != p.u.Hp).Select(p => $"{p.u.Id} hp {p.u.Hp}/{p.m?.Hp.ToString() ?? "-"}").ToList();
                if (diffs.Count > 0) Notes.Add($"{_state.AttackerGroup}→{_state.DefenderGroup} конец: " + string.Join("; ", diffs));
                if (!Same(end.Winner, outcome.Winner)) Notes.Add($"{outcome.Attacker}→{outcome.Defender} победитель {end.Winner}/{outcome.Winner}");
            }
            // Последний бой журнала: бросков после него нет — генератор остаётся портовым (дальше в дне бросков у движка тоже нет).
            if (rng.BeforeDraws(b.End, long.MaxValue) is { } g) session.Rng.SetState(g.TableSnapshot(), g.Index);
        }

        private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Генератор боя с записью бросков (max, r); <c>rng(0)</c> броска не тратит и не пишется — как у движка. <paramref name="journal"/> —
    /// броски журнала боя по порядку: 0 — бросок потока боя, иначе max броска чужого потока; чужой генератор тратит перед следующим
    /// броском боя.</summary>
    private sealed class RecordingRng(IRng inner, IReadOnlyList<int>? journal = null) : IRng
    {
        private int _at;

        public List<(int Max, int R)> Draws { get; } = [];

        public int Next(int maxValue)
        {
            if (maxValue != 0 && journal is not null)
            {
                while (_at < journal.Count && journal[_at] != 0) inner.Next(journal[_at++]);
                _at++;
            }
            int r = inner.Next(maxValue);
            if (maxValue != 0) Draws.Add((maxValue, r));
            return r;
        }
    }

    /// <summary>Трасса тактики порта, которая на каждом выборе зовёт сверку дня (<see cref="DayCheck.Picked"/>).</summary>
    private sealed class CheckedTrace(IAiTrace inner, DayCheck check) : IAiTrace
    {
        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates)
        {
            check.Picked(playerId, action);
            inner.Picked(playerId, day, action, score, candidates);
        }

        public void Stepped(string playerId, int day, AiExecuted step, int number) => inner.Stepped(playerId, day, step, number);

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) => inner.TurnEnded(playerId, day, ms, steps, picks);
    }
}
