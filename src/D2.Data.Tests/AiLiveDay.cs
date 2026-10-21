using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Партия порта с сохранения дня: начало хода и стратегия каждого игрока ИИ — самим портом
/// (<see cref="GameSession.BeginAiTurn"/>: регенерация, чит опыта с повышениями, лечение в городах), поверх —
/// позиции и приказы из журнала.</summary>
internal sealed class AiLiveDay
{
    private readonly MapArtData _art;
    private readonly GameBook _book;
    private GameState _state;

    /// <param name="spends">Память трат стратегии ИИ из журнала (<see cref="AiSpendMemory"/>); null — пустая, как в <c>.sg</c>.</param>
    /// <param name="events">События сценария из сохранения (<see cref="EventLoader"/>); null — без событий.</param>
    public AiLiveDay(MapLooks looks, int day, MapArtData art, GameBook book, AiSpendMemory? spends = null, ScenarioEventSet? events = null)
    {
        Day = day;
        _art = art;
        _book = book;
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        // Уровень сценария — из сохранения (SUGG_LVL): r2b 20261014-replay идёт с 10, прежде харнесс держал 1.
        _state = new GameState(new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1,
                                            looks.Map.Limits?.SuggestedLevel ?? 1), looks)
        {
            Ai = spends?.Before(looks.Map, day) ?? new Dictionary<string, AiMemory>(),
            Events = events ?? ScenarioEventSet.Empty,
            EventProgress = events is null ? EventProgress.Empty : EventProgress.From(events),
        };
        _loaded = looks.Map.Units.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Guests();
    }

    /// <summary>Юниты сохранения дня: их id — движка. Юнит, созданный в живом дне (найм), получил id порта.</summary>
    private readonly HashSet<string> _loaded;

    /// <summary>Гость крепости из сохранения (<c>City.STACK</c>) — отряд в ней, даже если его <c>INSIDE</c> пуст.</summary>
    private void Guests()
    {
        foreach (var fort in AiTactics.Forts(_state.Looks.Map).Where(f => f.StackId.Length > 0))
            if (_state.Looks.Map.Stacks.TryGetValue(fort.StackId, out var s) && s.Inside.Length == 0) Put(s with { Inside = fort.Id });
    }

    /// <summary>Адрес возврата строки <c>xp</c> дневного чита опыта ИИ (<c>0x431d07</c>) — это не опыт за бой.</summary>
    public const string DailyXpCheat = "0x431d07";

    public int Day { get; }
    public Dictionary<(string Player, string Target), string> Captured { get; } = [];

    public GameSession Session()
    {
        FlushArrange();
        return new(_state, _art, _book);
    }

    /// <summary>Группы, которые ждут расстановки после серии перестановок ИИ (<see cref="ArrangeAfter"/>).</summary>
    private readonly List<string> _toArrange = [];

    /// <summary>Конец серии перестановок (<c>0x5d5d73</c> — одна подготовка отряда): расстановка групп серии. Серии одной пары групп
    /// идут подряд без других строк — в начале хода тактики (подготовка всех отрядов) и при выборе отряда (строка <c>ai_tactic_pick</c>
    /// 0 → 1), поэтому граница — строка выбора.</summary>
    public void EndSeries() => FlushArrange();

    private void FlushArrange()
    {
        if (_toArrange.Count == 0) return;
        var session = new GameSession(_state, _art, _book);
        // Группа, чей юнит успел исчезнуть с карты (погибший вор и т. п.), не расставляется.
        foreach (string group in _toArrange)
            if (session.GroupCells(group) is { } cells && cells.Units.All(session.Map.Units.ContainsKey))
                AiStackUnits.Arrange(session, group, center: true);
        _toArrange.Clear();
        _state = session.State;
    }

    /// <summary>Начало хода и стратегия игрока ИИ портом; сессия, в которой они прошли (её отчёт — в
    /// <paramref name="report"/>), или null — игрока на карте нет.</summary>
    /// <param name="hireUnits">Найм юнитов в гарнизоны стратегией порта; false — наймы берутся из журнала
    /// (<see cref="HireUnit"/>).</param>
    /// <param name="hires">Наймы этого хода из журнала (<c>hire_leader</c>; при <paramref name="hireUnits"/> = false — и <c>hire_unit</c>, в
    /// порядке журнала: id юнитов раздаются по очереди найма): идут после стратегии, до оперативного слоя, как у движка
    /// (<see cref="GameSession.BeginAiTurn"/>, <c>journalHires</c>); те же строки потом в <see cref="HireLeader"/>/<see cref="HireUnit"/> не
    /// повторяются. null — наймы только по строкам журнала после хода.</param>
    /// <param name="purseIncome">Прибавлять ли доход дня в казну (<see cref="GameSession.BeginAiTurn"/>): false — казна уже со строки
    /// <c>bank</c> журнала.</param>
    /// <param name="operationalRolls">Броски оперативного слоя (<see cref="GameSession.OperationalRolls"/>: строки журнала 7.5); null —
    /// генератор партии.</param>
    public GameSession? Begin(string player, List<string>? report = null, bool hireUnits = true, IReadOnlyList<CorpusEvent>? hires = null,
                              bool purseIncome = true, D2.Rules.Rng.IRng? operationalRolls = null, uint? strategyCrt = null)
    {
        LoseSpied(player);
        var session = Session();
        if (!session.Map.Players.ContainsKey(player)) return null;
        session.OperationalRolls = operationalRolls;
        // Наймы лидеров — из журнала (HireLeader), чтобы отряды и id были те же, что у движка. Земля игрока уже взята из
        // его terrain_day (Land) — второй раз её не растить.
        // Наймы лидеров журнала — шагами стратегии (список 0, GameSession.BeginAiTurn journalLeaders): казна и бюджет тратятся на них в том
        // же проходе, что у движка (при найме юнитов стратегией порта, hireUnits = true); не исполненные стратегией (прервана, не дошла) —
        // после неё, как прежде. С наймом юнитов из журнала (hireUnits = false) — все наймы после стратегии в порядке журнала: id раздаются
        // по очереди найма.
        var leaders = hires is null || !hireUnits || Environment.GetEnvironmentVariable("D2_LEADERS_IN_STRATEGY") == "false" ? null
            : hires.OfType<HireLeaderEvent>().Where(h => h.Ret)
                   .Select(h => new JournalLeaderHire(h.Fort, h.UnitType, st =>
                   {
                       var next = Hired(st, h);
                       if (ReferenceEquals(next, st)) return null;
                       _hiredEarly.Add(h.Seq);
                       NameRoll(session, st, h);
                       return next;
                   })).ToList();
        session.BeginAiTurn(player, report, hireLeaders: false, growLand: !_landOf.Contains(player), hireUnits: hireUnits,
                            spies: false, purseIncome: purseIncome, journalLeaders: leaders, strategyCrt: strategyCrt,
                            journalHires: hires is null ? null : state => hires.Aggregate(state, (st, e) =>
                            {
                                switch (e)
                                {
                                    case HireLeaderEvent h when !_hiredEarly.Contains(h.Seq):
                                        _hiredEarly.Add(h.Seq);
                                        var next = Hired(st, h);
                                        if (!ReferenceEquals(next, st)) NameRoll(session, st, h);
                                        return next;
                                    case HireUnitEvent u when !hireUnits:
                                        _hiredEarly.Add(u.Seq);
                                        return UnitHired(st, u);
                                    default:
                                        return st;
                                }
                            }));
        _state = session.State;
        return session;
    }

    /// <summary>Начало хода игрока ИИ (<see cref="Begin"/>): юнитов в гарнизоны нанимает стратегия порта на потоке CRT движка
    /// (<see cref="JournalCrt.BeforeHire"/> — перед первым <c>ai_unit_pick</c> хода), так что тип выбирает правило порта
    /// (<c>0x44c137</c>, <see cref="D2.Rules.Map.AiStrategicRules.PickUnits"/>), а не свой поток порта (probe72 день 15: у порта UU0080 вместо
    /// UU0086 в FT000e — своя сила B ниже на 14,28). В <paramref name="tally"/> — сошёлся ли найм порта (тип и укрепление) с
    /// <c>hire_unit</c> журнала этого хода.</summary>
    public GameSession? BeginTurn(AiTurnEvent turn, IReadOnlyList<CorpusEvent>? hires, JournalCrt? crt, IDictionary<string, int>? tally = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.Player is not { } player) return null;
        // Поток движка перед первым выбором юнита найма — после начала хода порта (strategyCrt): броски варианта грунта у перекрашенного
        // леса (И210) при росте земли порта его не сдвигают.
        uint? strategyCrt = crt?.BeforeHire(turn, player);
        if (strategyCrt is { } c) SetCrt(c);
        _crtFrom = crt is null ? long.MaxValue : crt.FirstHirePick(turn, player) ?? turn.Seq;
        var report = new List<string>();
        // Казна со строки bank после прошлого начала хода — уже с доходом этого дня.
        bool paid = _bankRows.Contains(player);
        _bankRows.Clear();
        var begun = Begin(player, report, hires: hires, purseIncome: !paid, strategyCrt: strategyCrt);
        if (tally is not null)
        {
            var got = report.Select(e => e.Split(' ')).Where(q => q.Length == 4 && Same(q[0], player) && q[1] == "HireUnit")
                            .Select(q => $"{q[2]}@{q[3]}".ToUpperInvariant()).Order().ToList();
            var want = (hires ?? []).OfType<HireUnitEvent>().Where(h => h.Ret)
                                    .Select(h => $"{h.UnitType}@{h.Group}".ToUpperInvariant()).Order().ToList();
            if (got.Count + want.Count > 0)
            {
                string key = got.SequenceEqual(want) ? "найм юнитов: сошёлся" : "найм юнитов: разошёлся";
                tally[key] = tally.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        return begun;
    }

    /// <summary>Поток CRT <c>rand()</c> партии (<see cref="GameSession.Crt"/>) — состояние движка (<see cref="JournalCrt"/>): тип юнита найма в
    /// гарнизон стратегия порта выбирает на нём (<c>0x44c137</c>). Генератор R250 не трогается.</summary>
    public void SetCrt(uint crt)
    {
        var rng = new GameSession(_state, _art, _book).Rng;
        _state = _state with { Random = new RandomStreams(rng.Index, rng.TableSnapshot(), crt) };
    }

    private readonly HashSet<string> _landOf = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Игроки, чья казна пришла строкой <c>bank</c> журнала после последнего <see cref="BeginTurn"/> (<see cref="SetBank"/>).</summary>
    private readonly HashSet<string> _bankRows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Разведанные игроки по сохранению следующего дня (<see cref="MapPlayer.Spied"/>): снятое раскрытием шпиона за
    /// день (бросок R250 в начале хода разведчика, значения которого журнал не пишет) харнесс берёт отсюда.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? NextSpied { get; init; }

    /// <summary>Кого игрок разведывает в этот день (шпионаж <c>noble_action</c> журнала, <c>id</c> — игрок цели): шпионаж
    /// предлагается только неразведанным, так что к его началу хода запись уже была раскрыта, даже если сохранение следующего дня
    /// её снова показывает.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>>? SpiedToday { get; init; }

    /// <summary>Начало хода разведчика: раскрытые записи (<c>0x44430f</c>) — которых нет в сохранении следующего дня или по
    /// которым игрок в этот день снова шпионит (<see cref="SpiedToday"/>).</summary>
    public void LoseSpied(string player)
    {
        var map = _state.Looks.Map;
        if (!map.Players.TryGetValue(player, out var p)) return;
        var next = NextSpied?.GetValueOrDefault(player);
        var again = SpiedToday?.GetValueOrDefault(player);
        var kept = p.Spied.Where(x => (next is null || next.Contains(x, StringComparer.OrdinalIgnoreCase))
                                      && (again is null || !again.Contains(x))).ToList();
        if (kept.Count == p.Spied.Count) return;
        var players = map.Players.ToDictionary(q => q.Key, q => q.Value, StringComparer.OrdinalIgnoreCase);
        players[player] = p with { Spied = kept };
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, players: players), _state.Looks.Stacks) };
    }

    /// <summary>
    /// Земля игрока из <c>terrain_day</c> (пишется в начале его хода, после роста земли): клетки списка — его, а его
    /// клетки не из списка становятся нейтральными (ранг 3: чья земля там на самом деле, журнал не говорит — чаще всего
    /// это снесённый чужим отрядом жезл, и землю перекрасит новый хозяин позже).
    /// </summary>
    public void Land(TerrainDayEvent t)
    {
        if (t.Terrain < 0 || !_state.Looks.Map.Players.ContainsKey(t.Player)) return;
        var map = _state.Looks.Map;
        var tiles = (Tile[,])map.Tiles.Clone();
        for (int x = 0; x < map.Size; x++)
            for (int y = 0; y < map.Size; y++)
            {
                var tile = tiles[x, y];
                bool mine = t.Cells.Contains((x, y));
                if (mine && tile.Terrain != t.Terrain) tiles[x, y] = new Tile((byte)((tile.Raw & ~0b111) | (int)t.Terrain));
                else if (!mine && tile.Terrain == t.Terrain) tiles[x, y] = new Tile((byte)((tile.Raw & ~0b111) | NeutralTerrain));
            }
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.WithTiles(map, tiles), _state.Looks.Stacks) };
        _landOf.Add(t.Player);
    }

    private const int NeutralTerrain = 5;

    public void Move(string id, int x, int y, int moveLeft)
    {
        var map = _state.Looks.Map;
        if (!map.Stacks.TryGetValue(id, out var s))
        {
            if ((Hired(map, id, x, y) ?? Summoned(map, id, x, y)) is not { } renamed) return;
            s = renamed;
            map = _state.Looks.Map;
        }
        // Строка stack_move самой постановки нанятого лидера пишет запас 0 или чужой (human, день 45: 35 у лидера с MOVE 20 —
        // дальше у движка 20), а запас выставляется после неё (HireLeader).
        if (_justHired.Remove(id) && s.X == x && s.Y == y) return;
        var fort = AiTactics.Forts(map).FirstOrDefault(f => x >= f.X && y >= f.Y && x < f.X + Size(f) && y < f.Y + Size(f));
        // Встал на клетку мешка — мешок подобран, его предметы — в сумку отряда.
        if (map.Sites.FirstOrDefault(b => b.Kind == SiteKind.Bag && !b.Gone && b.X == x && b.Y == y) is { } bag)
        {
            TakeBag(bag.Id);
            s = s with { Items = [.. s.Items, .. bag.Items] };
            map = _state.Looks.Map;
        }
        var moved = s with { X = x, Y = y, Move = moveLeft, Inside = fort?.Id ?? "" };
        Put(moved);
        // Ход открывает туман хозяина вокруг новой клетки (как ход движка): иначе игрок ИИ не видит целей, к которым
        // подошёл за день, — проверка литья (туман) и «атака напрямую» (обзор) расходятся с движком.
        if (map.Players.TryGetValue(s.Owner, out var owner) && map.Fog.TryGetValue(owner.FogId, out var fog))
            Fog.Reveal(fog, x, y, Session().ScoutRange(moved), Fog.DefaultShape);
        // Настоящий ход (запас изменился; пробные шаги показа пути его не меняют) — события после хода (0x41e46b): иначе событие
        // OCCUR_ONCE, сработавшее у движка утром, в живом дне ещё включено и ложно обрывает пути (упреждающая проверка 0x440284).
        if (moveLeft != s.Move && _state.Events.Events.Count > 0)
        {
            var session = Session();
            session.RunEvents("", id);
            _state = session.State;
        }
    }

    /// <summary>Подготовка отряда ИИ портом (<see cref="AiStackItems.Prepare"/>: склад крепости, зелья, снаряжение) —
    /// в состоянии дня; строки действий — в <paramref name="report"/>.</summary>
    public void Prepare(string stackId, List<string> report)
    {
        var session = Session();
        AiStackItems.Prepare(session, stackId, report);
        _state = session.State;
    }

    /// <summary>Начало хода тактики игрока портом: подготовка всех отрядов очереди (<see cref="GameSession.PrepareTactic"/>).</summary>
    public void PrepareTactic(string player, List<string> report)
    {
        var session = Session();
        session.PrepareTactic(player, report);
        _state = session.State;
    }

    /// <summary>Новый предмет в сумке отряда (<c>item_create</c>: руина, торговец, подарок): id журнал не
    /// пишет — движок раздаёт их подряд, берётся следующий после наибольшего номера <c>IM</c> карты.</summary>
    public void GainItem(string stackId, string itemType)
    {
        var map = _state.Looks.Map;
        if (!map.Stacks.TryGetValue(stackId, out var s) || itemType.Length == 0) return;
        string prefix = stackId[..4] + "IM";
        int next = map.ItemTypes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                      .Select(k => int.TryParse(k[6..], System.Globalization.NumberStyles.HexNumber, null, out int n) ? n : -1)
                      .DefaultIfEmpty(-1).Max() + 1;
        string id = $"{prefix}{next:x4}";
        var types = map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[id] = itemType.ToUpperInvariant();
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, itemTypes: types), _state.Looks.Stacks) };
        Put(s with { Items = [.. s.Items, id] });
    }

    /// <summary>Предмет израсходован (<c>potion_use</c> с уничтожением, <c>item_consume</c>): уходит из сумки отряда.</summary>
    public void LoseItem(string itemId)
    {
        var holder = _state.Looks.Map.Stacks.Values.FirstOrDefault(s => s.Items.Contains(itemId, StringComparer.OrdinalIgnoreCase));
        if (holder is null) return;
        Put(holder with
        {
            Items = [.. holder.Items.Where(i => !Same(i, itemId))],
            Equipment = [.. holder.Equipment.Select(e => Same(e, itemId) ? "" : e)],
        });
    }

    /// <summary>Счётчики id движка на этот момент (<c>id_alloc</c>: номер последней выдачи + 1): день живёт с сохранения, где счётчик —
    /// наибольший из записанных + 1, а движок за сессию уже выдавал номера, которых в файле нет (ai-q09 день 5 — <c>UN0274</c>…<c>UN0278</c>,
    /// призванные в бою хода нейтрала, <see cref="BattleSummon"/>; в сохранении дня 6 их нет). Счётчик только растёт.</summary>
    public void SyncIds(IReadOnlyDictionary<string, int> engine)
    {
        var map = _state.Looks.Map;
        var next = map.IdNext.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, n) in engine)
            if (next.GetValueOrDefault(kind) < n) next[kind] = n;
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, idNext: next), _state.Looks.Stacks) };
    }

    /// <summary>Призыв в бою: бой живой день не моделирует (исход — <c>battle_end</c>), но призванный берёт номер из счётчика карты, как
    /// у порта <c>GameSession.StartBattle</c> → <see cref="D2.Rules.Battle.BattleState.NewUnitId"/> (<c>CBatAttackSummon::onHit</c>
    /// <c>0x6631eb</c> → <c>0x5ee8bd</c>; строка <c>id_alloc</c> с адресом <c>0x66350c</c> в цепочке — только признак, что призыв был).
    /// Выданные — <see cref="BattleSummoned"/>.</summary>
    public void BattleSummon()
    {
        var map = _state.Looks.Map;
        var ids = map.IdNext;
        string id = GameSession.NewId(ref ids, map.Units.Keys.Concat(_battleSummoned), map.Units.Keys.FirstOrDefault() ?? "S000", "UN");
        _battleSummoned.Add(id);
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, idNext: ids), _state.Looks.Stacks) };
    }

    /// <summary>Id призванных в боях живого дня (<see cref="BattleSummon"/>) по порядку.</summary>
    public IReadOnlyList<string> BattleSummoned => _battleSummoned;

    private readonly List<string> _battleSummoned = [];

    /// <summary>Адрес возврата в <c>CBatAttackSummon::onHit</c> (<c>0x663507</c> → addUnitToGroup <c>0x5e78f7</c>) в цепочке
    /// <c>id_alloc</c>.</summary>
    public const string SummonCaller = "0x66350c";

    public void MoveLeft(string id, int left)
    {
        if (_state.Looks.Map.Stacks.TryGetValue(id, out var s)) Put(s with { Move = left });
    }

    /// <summary>Жезл журнала (<c>rod_drop</c>): ставится правилом порта (<see cref="GameSession.PlaceRod"/>) — иначе у живого дня нет
    /// жезлов, поставленных в этот же день, и путь к ним (<c>AttackRod</c>) ищется как в пустую клетку. Отказ правила — жезла нет.</summary>
    public void Rod(RodDropEvent e)
    {
        if (!e.Ret || e.X < 0) return;
        var session = Session();
        if (!session.Map.Stacks.ContainsKey(e.Stack)) return;
        if (session.PlaceRod(e.Stack, (int)e.X, (int)e.Y) is null) _state = session.State;
    }

    /// <summary>Мешок перед отрядом на пути (правило порта <c>GameSession.AiMove</c>/<c>TakeBagAtFirstStep</c>: мешок в клетке шага
    /// обрывает путь, <c>0x5cda67</c>, и его берут эффекты хода с соседней клетки, <c>0x43fbac</c> → <c>0x4408cb</c>). Путь — порта на
    /// момент выбора (<paramref name="path"/>), клетка отряда — после ходов журнала: следующая точка пути за ней — мешок → взят. Строк
    /// журнал не пишет (предмет виден только в <c>equip_check</c> следующей подготовки): human день 9 <c>KC0079</c>/<c>BG000b</c>, день 10
    /// <c>KC0076</c>/<c>BG0027</c>.</summary>
    public bool BagAhead(string stackId, StackPath path)
    {
        var map = _state.Looks.Map;
        if (!map.Stacks.TryGetValue(stackId, out var s) || path.Points.Count < 2) return false;
        int at = -1;
        for (int i = path.Points.Count - 1; i >= 0; i--)
            if (path.Points[i].X == s.X && path.Points[i].Y == s.Y) { at = i; break; }
        if (at < 0 && s.Inside.Length > 0) at = 0;   // в крепости: клетка пути — вход
        if (at < 0 || at + 1 >= path.Points.Count) return false;
        var next = path.Points[at + 1];
        if (map.Sites.FirstOrDefault(b => b.Kind == SiteKind.Bag && !b.Gone && b.X == next.X && b.Y == next.Y) is not { } bag) return false;
        TakeBag(bag.Id, stackId);
        return true;
    }

    /// <summary>Предмет <paramref name="itemId"/> уже у отряда (строка <c>equip_check</c> подготовки), а живой день держит его в мешке рядом
    /// с отрядом (Чебышёв ≤ 1): мешок взят с соседней клетки эффектами хода (обрыв пути мешком, <c>0x5cda67</c>, подбор по клетке обрыва
    /// <c>0x4408cb</c>) — строк подбора журнал не пишет. Мешок уходит, предметы — в сумку отряда. Иначе ничего.</summary>
    public bool BagItemSeen(string stackId, string itemId)
    {
        var map = _state.Looks.Map;
        if (!map.Stacks.TryGetValue(stackId, out var s) || s.Items.Contains(itemId, StringComparer.OrdinalIgnoreCase)) return false;
        if (map.Sites.FirstOrDefault(b => b.Kind == SiteKind.Bag && !b.Gone && b.Items.Contains(itemId, StringComparer.OrdinalIgnoreCase)) is not { } bag
            || Math.Max(Math.Abs(s.X - bag.X), Math.Abs(s.Y - bag.Y)) > 1)
            return false;
        TakeBag(bag.Id, stackId);
        return true;
    }

    /// <summary>Жезл снесён отрядом ИИ: строка <c>ap_restore</c> с причиной <c>rod_break</c> (весь остаток хода, <c>0x5d9ffb</c>) и
    /// следом <c>ai_tactic_result</c> по жезлу. Правилом порта (<see cref="GameSession.BreakRod"/>) — иначе жезл, снесённый раньше в
    /// тот же день, у живого дня остаётся: лишний <c>AttackRod</c> и нет <c>PlantRod</c> в его точке. Отказ правила — жезл остаётся.</summary>
    public bool BreakRod(string rodId, string stackId)
    {
        var session = Session();
        if (!session.Map.Rods.ContainsKey(rodId) || !session.Map.Stacks.ContainsKey(stackId)) return false;
        if (session.BreakRod(stackId, rodId) is not null) return false;
        _state = session.State;
        return true;
    }

    /// <summary>Отряд дошёл до мешка (стоит на его клетке или рядом): статус 5 у перемещения к мешку ставится и на полпути, а
    /// мешок уходит только у дошедшего (<see cref="TakeBag"/>; прежде — при любом статусе 5, и путь порта к мешку, до которого
    /// отряд шёл несколько дней, со второго дня искался в пустую клетку). Не мешок — истина.</summary>
    public bool Reached(string bagId, string? stackId)
    {
        var map = _state.Looks.Map;
        if (map.Sites.FirstOrDefault(b => b.Kind == SiteKind.Bag && Same(b.Id, bagId)) is not { } bag
            || stackId is null || !map.Stacks.TryGetValue(stackId, out var s)) return true;
        return Math.Max(Math.Abs(s.X - bag.X), Math.Abs(s.Y - bag.Y)) <= 1;
    }

    /// <summary>Мешок подобран: <c>ai_tactic_result</c> по нему со статусом 5 или 2 (отчёт разбора, ранг 2). Предметы мешка —
    /// в сумку <paramref name="by"/>, если он ещё не забрал их шагом на клетку мешка (<see cref="Move"/>).</summary>
    public void TakeBag(string id, string? by = null)
    {
        var map = _state.Looks.Map;
        if (map.Sites.FirstOrDefault(b => b.Kind == SiteKind.Bag && Same(b.Id, id)) is not { } bag) return;
        if (!bag.Gone && by is not null && map.Stacks.TryGetValue(by, out var taker) && bag.Items.Count > 0)
        {
            Put(taker with { Items = [.. taker.Items, .. bag.Items] });
            map = _state.Looks.Map;
        }
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map,
            sites: [.. map.Sites.Select(b => Same(b.Id, id) ? b with { Gone = true } : b)],
            plan: GameSession.PlanWithout(map, id)), _state.Looks.Stacks) };
    }

    /// <summary>Смена приказа из журнала (<c>ai_order</c>, <c>0x5e811a</c>). Вызов с <c>apply</c> = false приказа не меняет —
    /// ни категории, ни цели: так <c>CVisitorChangeVillageOwner</c> (<c>0x60fb7b</c>, возврат <c>0x60fc85</c>) спрашивает о каждом
    /// отряде с <c>L_SECURE_CITY</c> на захваченную деревню, а у отрядов подрасы нового хозяина приказ остаётся (20261016c
    /// seq 1351779: <c>KC003b</c> после захвата <c>FT000c</c> союзным <c>KC0034</c> — приказ 6 и цель <c>FT000c</c>, у движка дальше вход
    /// и «остаться»). Во всём корпусе у строк с <c>apply</c> = false приказ до и после один (0x60fc85, 0x436e9b).</summary>
    public void Order(AiOrderEvent o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!o.Apply && o.OrderAfter == o.OrderBefore && o.AiOrderAfter == o.AiOrderBefore) return;
        if (!_state.Looks.Map.Stacks.TryGetValue(o.Stack, out var s)) return;
        string target = o.Target.Equals("G000000000", StringComparison.OrdinalIgnoreCase) ? "" : o.Target;
        s = s with { Order = (int)o.OrderAfter, AiOrder = (int)o.AiOrderAfter };
        if (o.Flag) s = s with { AiOrderTarget = target };
        else
        {
            s = s with { OrderTarget = target };
            if (o.AiOrderAfter != o.AiOrderBefore) s = s with { AiOrderTarget = target };
        }
        Put(s);
    }

    /// <summary>Исход боя: здоровье юнитов из <c>battle_end</c>; отряд, у которого не осталось живых, снимается; у выживших
    /// отрядов флаг «лидер жив» (<c>LEADR_ALIV</c>) — по здоровью лидера: без него голова списка тактики (атаки, места, храм)
    /// у отряда с павшим лидером не строится (<see cref="AiTacticLists.Build"/>). Гарнизон без живых пустеет, павшие удаляются — как
    /// у порта (<see cref="MapBattle.Apply"/>): иначе павший гарнизон взятой деревни — «есть кого забрать» у <c>HireUnit</c>
    /// (port-synth-4-ai, день 1: Гоблин <c>S143UN0009</c> в <c>S143FT0005</c> после боя seq 551073, у движка <c>ai_hire_score</c>
    /// <c>removable</c> 0).</summary>
    public void Battle(BattleEndEvent b)
    {
        var map = _state.Looks.Map;
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var u in b.Units)
            if (units.TryGetValue(u.Id, out var unit)) units[u.Id] = unit with { Hp = (int)u.Hp };
        var dead = map.Stacks.Values.Where(s => s.UnitIds.Count > 0 && s.UnitIds.All(u => units.TryGetValue(u, out var x) && x.Hp <= 0))
                      .Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fought = b.Units.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sites = map.Sites.Select(f =>
        {
            if (f.Guards.Count == 0 || !f.Guards.Any(fought.Contains)
                || f.Guards.Any(id => units.TryGetValue(id, out var g) && g.Hp > 0)) return f;
            foreach (string id in f.Guards) units.Remove(id);
            return f with { Guards = [], GuardSlots = [] };
        }).ToList();
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => LeaderFlag(p.Value, units), StringComparer.OrdinalIgnoreCase);
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, units: units, stacks: stacks, sites: sites), _state.Looks.Stacks) };
        Put(null, dead);
        DestroyedStacks += dead.Count;
    }

    /// <summary>
    /// Склад крепости в подготовке отряда (<see cref="AiStackItems.Prepare"/>, шаг выбора <c>0x466b68</c>): боец и нейтрал в
    /// крепости забирают весь склад (<c>0x45193d</c>), вор и жезлоносец сдают на него сумку (<c>0x451a43</c>). Обмен журнал не пишет
    /// (видны только <c>equip_check</c>/<c>item_equip</c> предметов склада), и без этого шага сумка живого дня пуста: human, день 1 —
    /// <c>S073KC0003</c> в столице <c>S073FT0003</c> с 15 предметами склада, у движка торговец не строится (<c>0x46b0e0</c>: юнитов 3 −
    /// предметов 15 &lt; 1), у порта строился.
    /// </summary>
    public void FortItems(string stackId)
    {
        var session = Session();
        if (!session.Map.Stacks.TryGetValue(stackId, out var s) || s.Inside.Length == 0) return;
        var kind = AiTactics.KindOf(session, s);
        if (kind is AiStackKind.Fighter or AiStackKind.Neutral) session.TakeFortItems(stackId);
        else if (kind is AiStackKind.Thief or AiStackKind.Rod) session.GiveFortItems(stackId);
        else return;
        _state = session.State;
    }

    /// <summary>Сколько отрядов снято боями живого дня — харнессы по нему пересобирают списки сил тактики
    /// (<see cref="AiTactics.StackDestroyed"/>, <c>CCmdStackDestroyedMsg</c>).</summary>
    public int DestroyedStacks { get; private set; }

    /// <summary>Руина разграблена: бой, где отряд напал на руину (<c>battle_start</c>: группа защиты — руина) и победил (<c>battle_end</c>).
    /// Грабитель — хозяин отряда, как у порта (<c>Sites</c>: <c>LooterId = stack.Owner</c>); трофей и золото — строками <c>item_create</c> и
    /// <c>bank</c>. Иначе руина, разграбленная раньше в тот же день, у живого дня остаётся в кэше руин (<c>0x45f1cb</c>: грабитель пуст) —
    /// лишний <c>VisitRuin</c> и другая третья руина кэша (legions день 51: <c>KC0000</c> грабит <c>RU000d</c>).</summary>
    public void LootRuin(string ruinId, string stackId)
    {
        var map = _state.Looks.Map;
        if (!map.Stacks.TryGetValue(stackId, out var s)
            || map.Sites.FirstOrDefault(r => r.Kind == SiteKind.Ruin && Same(r.Id, ruinId)) is not { LooterId.Length: 0 }) return;
        var sites = map.Sites.Select(r => Same(r.Id, ruinId) ? r with { LooterId = s.Owner, Cash = Bank.Zero, ItemId = "" } : r).ToList();
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, sites: sites), _state.Looks.Stacks) with
        {
            Ruins = [.. _state.Looks.Ruins.Select(r => Same(r.Id, ruinId) ? r with { Looted = true } : r)],
        } };
    }

    /// <summary>Захват деревни из журнала (<c>fort_capture</c>, <c>CEffectCapture::apply</c> <c>0x433e2e</c>): владелец и подраса — как у
    /// движка. Живой день боёв и входов порта не играет, поэтому без этой строки захваченная крепость оставалась прежнему хозяину и сетка
    /// влияния следующих окон расходилась (ai-q09: <c>FT000c</c>, день 5, <c>docs/ai-tactic-candidates.md</c> §21). Место отряда — строкой
    /// <c>stack_move</c>.</summary>
    public void Capture(FortCaptureEvent c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!c.Captured || Environment.GetEnvironmentVariable("D2_CAPTURE") == "false") return;
        var map = _state.Looks.Map;
        var sites = map.Sites.Select(f => Same(f.Id, c.Fort) ? f with { Owner = c.OwnerAfter, Subrace = c.Subrace } : f).ToList();
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, sites: sites), _state.Looks.Stacks) };
    }

    /// <summary>Проверка событий после хода отряда <paramref name="stackId"/> правилом порта (<see cref="GameSession.RunEvents"/>, триггерер —
    /// отряд, <c>StackMove 0x41e46b</c> → <c>0x41f762</c>): строк срабатывания журнал до зонда 7.x не пишет, а разовое событие, выполненное
    /// движком на этом ходу, у живого дня оставалось бы ждать — и упреждающая проверка пути (<c>0x440284</c>) следующего отряда его
    /// «находила». legions день 42: KC0000 захватил FT000b (seq 9176647) — L_OWN_CITY S073EV000e (разовое) у движка выполнено, у живого
    /// дня помощник KC0075 вставал после первого шага.</summary>
    public void MoveEvents(string stackId)
    {
        var session = Session();
        if (!session.Map.Stacks.ContainsKey(stackId)) return;
        session.RunEvents("", stackId);
        _state = session.State;
    }

    /// <summary>Флаг «лидер жив» отряда по здоровью его лидера.</summary>
    private static MapStack LeaderFlag(MapStack stack, IReadOnlyDictionary<string, MapUnit> units) =>
        units.TryGetValue(stack.LeaderId, out var leader) && (leader.Hp > 0) != stack.LeaderAlive ? stack with { LeaderAlive = leader.Hp > 0 } : stack;

    private void Put(MapStack? stack, IReadOnlySet<string>? remove = null)
    {
        var map = _state.Looks.Map;
        var stacks = map.Stacks.Where(p => remove is null || !remove.Contains(p.Key))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (stack is not null) stacks[stack.Id] = stack;
        // План — прежний без отрядов, затем отряды вне городов в порядке словаря. Клетки без отрядов (почти весь план) остаются
        // прежними списками: пересборка всего плана на каждый шаг отряда была 18 % процессорного времени D2.Data.Tests (замер 16.10).
        // Клетки с отрядами — те, куда их положил прошлый вызов, если план с тех пор не менялся; иначе — поиском по всему плану.
        var plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>(map.Plan);
        var withStacks = ReferenceEquals(map.Plan, _plan) ? _stackCells
            : [.. map.Plan.Where(p => p.Value.Any(IsStack)).Select(p => p.Key)];
        foreach (var cell in withStacks) plan[cell] = [.. plan[cell].Where(id => !IsStack(id))];
        var grown = new Dictionary<(int X, int Y), List<string>>();
        foreach (var s in stacks.Values.Where(s => s.Inside.Length == 0))
        {
            if (!grown.TryGetValue((s.X, s.Y), out var cell))
                plan[(s.X, s.Y)] = grown[(s.X, s.Y)] = cell = plan.TryGetValue((s.X, s.Y), out var was) ? [.. was] : [];
            cell.Add(s.Id);
        }
        _plan = plan;
        _stackCells = [.. grown.Keys];
        var sites = map.Sites.Select(f => f.Kind is SiteKind.Capital or SiteKind.Village
            ? f with { StackId = stacks.Values.FirstOrDefault(s => Same(s.Inside, f.Id))?.Id ?? "" } : f).ToList();
        var live = GameSession.With(map, stacks: stacks, plan: plan, sites: sites);
        _state = _state with { Looks = GameSession.Retold(_state.Looks, live, _state.Looks.Stacks) };
    }

    /// <summary>План, собранный прошлым <see cref="Put"/>, и его клетки с отрядами.</summary>
    private IReadOnlyDictionary<(int X, int Y), IReadOnlyList<string>>? _plan;
    private List<(int X, int Y)> _stackCells = [];

    private static int Size(MapSite f) => f.Kind == SiteKind.Capital ? 5 : 4;

    private static bool IsStack(string id) => id.Length >= 6 && id.AsSpan(4, 2).Equals("KC", StringComparison.OrdinalIgnoreCase);

    /// <summary>Опыт юнита из строки <c>xp</c> журнала: <c>xp_after</c>, и если порог пройден — повышение по правилам
    /// порта (<see cref="BattleAftermath.Advance"/>, лечит до полного).</summary>
    public void Xp(XpEvent x)
    {
        var map = _state.Looks.Map;
        if (x.XpAfter is not { } xp || !map.Units.TryGetValue(x.Unit, out var unit) || unit.Hp <= 0) return;
        var stack = map.Stacks.Values.FirstOrDefault(s => s.UnitIds.Contains(x.Unit, StringComparer.OrdinalIgnoreCase));
        var (grown, pending) = stack is null ? (unit with { Xp = (int)xp }, 0) : BattleAftermath.Advance(map, unit, stack, (int)xp, _book, amount: (int)x.Amount);
        SetUnit(grown, stack is null || pending == stack.UpgradeCount ? null : stack with { UpgradeCount = pending });
    }

    /// <summary>Лечение в городе (<c>heal</c>): строка здоровья не пишет, поэтому — правилом порта (<see cref="Healing.Heal"/>:
    /// <c>HEAL_C</c> за очко, пока хватает казны хозяина; казна — журнала, её строка <c>bank</c> идёт сразу перед <c>heal</c>).
    /// Правило отказало (нет цены, нет Храма в модели) — до полного, как было.</summary>
    public void Heal(string unitId)
    {
        var session = Session();
        var map = session.Map;
        if (!map.Units.TryGetValue(unitId, out var unit) || unit.Hp <= 0) return;
        int max = session.HpMax(unit);
        string owner = map.Stacks.Values.FirstOrDefault(s => s.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner
                       ?? map.Sites.FirstOrDefault(f => f.Guards.Contains(unitId, StringComparer.OrdinalIgnoreCase))?.Owner ?? "";
        int hp = max;
        if (map.Players.TryGetValue(owner, out var player) && _book.TryGlobal(unit.TypeId, out var id, out _)
            && _book.Treasury.Units.TryGetValue(id, out var costs)
            && Healing.Heal(unit.Hp, max, costs.Heal, player.Bank, hasTemple: true, insideFort: true) is { Refused: null } healed)
            hp = healed.Hp;
        SetUnit(unit with { Hp = hp }, null);
    }

    /// <summary>Покупка у торговца из журнала (<c>site_buy_item</c>): общим действием порта (<see cref="GameSession.BuyItem"/>);
    /// казна и склад порта могли разойтись с движком, а движок купил — поэтому покупка проходит всегда (не нашлось на складе
    /// порта — предмет всё равно в сумку), цена — с обрезкой в ноль. Возвращает хозяина и цену (её движок вычитает и из
    /// кошелька тактики).</summary>
    public (string Owner, Bank Price) BuyItem(SiteBuyItemEvent e)
    {
        var session = Session();
        if (!e.Ret || !session.Map.Stacks.TryGetValue(e.Stack, out var stack) || !_book.Items.TryGetValue(e.ItemType, out var item))
            return ("", Bank.Zero);
        var price = Treasury.ItemBuyPrice(item);
        var bank = session.Map.Players[stack.Owner].Bank;
        SetBank(stack.Owner, new Bank(9999, 9999, 9999, 9999, 9999, 9999));
        session = Session();
        if (session.BuyItem(e.Stack, e.Site, e.ItemType) is null) _state = session.State;
        else GainItem(e.Stack, e.ItemType);
        SetBank(stack.Owner, Treasury.Pay(bank, price));
        return (stack.Owner, price);
    }

    /// <summary>Покупка заклинания из журнала (<c>site_buy_spell</c>): игроку — известное заклинание, из башни — уходит, цена
    /// <c>BUY_C</c> — с обрезкой в ноль. Возвращает цену (её движок вычитает и из кошелька тактики).</summary>
    public Bank BuySpell(SiteBuySpellEvent e)
    {
        var map = _state.Looks.Map;
        if (!e.Ret || !map.Players.TryGetValue(e.Player, out var player) || !_book.Spells.TryGetValue(e.Spell, out var spell)) return Bank.Zero;
        var price = Treasury.SpellBuyPrice(spell);
        var players = WithBank(map, player, Treasury.Pay(player.Bank, price));
        if (!player.KnownSpells.Contains(e.Spell, StringComparer.OrdinalIgnoreCase))
            players[player.Id] = players[player.Id] with { KnownSpells = [.. player.KnownSpells, e.Spell.ToUpperInvariant()] };
        var sites = map.Sites.Select(s => Same(s.Id, e.Site) ? s with { Spells = [.. s.Spells.Where(x => !Same(x, e.Spell))] } : s).ToList();
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, players: players, sites: sites), _state.Looks.Stacks) };
        return price;
    }

    /// <summary>Передача предметов ИИ (<c>CAiGiveItemsAction</c>, статус 1 в <c>ai_tactic_result</c>): строки обмена журнал не
    /// пишет — вся сумка уходит цели общим действием порта (<see cref="GameSession.GiveItems"/>).</summary>
    public void GiveItems(string fromStack, string toStack)
    {
        var session = Session();
        session.GiveItems(fromStack, toStack);
        _state = session.State;
    }

    /// <summary>Союз заключён или разорван (<c>alliance_change</c>): слово отношений пары рас — из строки журнала
    /// (<c>after.raw</c>); разведка друг друга — как у движка: союз — оба разведаны (<c>0x5d7dbc</c>), разрыв — записи сняты
    /// (<c>CVisitorPlayerDelSpy</c>, <c>0x5d91fd</c>).</summary>
    public void Alliance(AllianceChangeEvent e)
    {
        var map = _state.Looks.Map;
        if (e.After is not { } after || !map.Players.TryGetValue(e.A, out var a) || !map.Players.TryGetValue(e.B, out var b)
            || !_book.Economy.Races.TryGetValue(a.RaceId, out var ra) || !_book.Economy.Races.TryGetValue(b.RaceId, out var rb)) return;
        var relations = map.Relations.Select(r => r.Pairs(ra.RaceType, rb.RaceType) ? r with { Raw = (uint)after.Raw } : r).ToList();
        bool wasAlly = e.Before?.Ally == true;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        static MapPlayer Spy(MapPlayer p, string other, bool on) =>
            on ? p.Spied.Contains(other, StringComparer.OrdinalIgnoreCase) ? p : p with { Spied = [.. p.Spied, other] }
               : p with { Spied = [.. p.Spied.Where(x => !x.Equals(other, StringComparison.OrdinalIgnoreCase))] };
        if (after.Ally || wasAlly)
        {
            players[a.Id] = Spy(a, b.Id, after.Ally);
            players[b.Id] = Spy(b, a.Id, after.Ally);
        }
        _state = _state with
        {
            Looks = GameSession.Retold(_state.Looks, GameSession.With(map, players: players, relations: relations), _state.Looks.Stacks),
        };
    }

    /// <summary>Действие вора из журнала (<c>noble_action</c>): провал — вор гибнет; успешный шпионаж — игрок из <c>id</c> в
    /// разведанных (<see cref="MapPlayer.Spied"/>). Прочие исходы (урон, кражи) приходят своими строками или не нужны.</summary>
    public void Noble(NobleActionEvent n)
    {
        var map = _state.Looks.Map;
        if (!n.Success)
        {
            if (map.Stacks.TryGetValue(n.Stack, out var thief))
            {
                var units = map.Units.Where(p => !thief.UnitIds.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                                     .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, units: units), _state.Looks.Stacks) };
                Put(null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { thief.Id });
                DestroyedStacks++;   // CEffectKillStack → CCmdStackDestroyedMsg, как у боя (20261016d seq 732750 → 732752)
            }
            return;
        }
        if (n.Action != (int)ThiefAction.Spy || !map.Players.TryGetValue(n.Player, out var player)
            || player.Spied.Contains(n.Id, StringComparer.OrdinalIgnoreCase)) return;
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[player.Id] = player with { Spied = [.. player.Spied, n.Id] };
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, players: players), _state.Looks.Stacks) };
    }

    /// <summary>Удачная кража у торговца (<c>noble_action</c> действия <see cref="ThiefAction.StealMerchant"/>): экземпляр типа <c>id</c>
    /// уходит со склада (как <see cref="GameSession"/> <c>StealFromMerchant</c>, <c>CVisitorMerchantDelItem</c>); предмет вору приходит
    /// своей строкой <c>item_create</c> (<c>give_item</c>).</summary>
    public void StealFromMerchant(NobleActionEvent n)
    {
        if (!n.Success || n.Action != (int)ThiefAction.StealMerchant) return;
        var map = _state.Looks.Map;
        if (map.Sites.FirstOrDefault(x => x.Id.Equals(n.Target, StringComparison.OrdinalIgnoreCase)) is not { } site
            || site.Stock.FirstOrDefault(r => r.Item.Equals(n.Id, StringComparison.OrdinalIgnoreCase) && r.Count > 0) is not { } row) return;
        var sites = map.Sites.Select(x => ReferenceEquals(x, site)
            ? x with { Stock = [.. x.Stock.Select(r => ReferenceEquals(r, row) ? r with { Count = r.Count - 1 } : r).Where(r => r.Count > 0)] }
            : x).ToList();
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, sites: sites), _state.Looks.Stacks) };
    }

    /// <summary>Казна игрока из журнала (<c>bank.after</c>). <paramref name="journalRow"/> — это строка <c>bank</c> журнала: казна, пришедшая
    /// так между началами ходов, уже с доходом дня (движок пишет его до <c>ai_turn begin</c> игрока), и <see cref="BeginTurn"/> второй раз
    /// его не прибавит. Флаг живёт до ближайшего <see cref="BeginTurn"/> любого игрока; списания посреди хода тоже его ставят — безвредно, пока
    /// движок пишет строку дохода перед каждым началом хода с доходом (так во всех журналах корпуса).</summary>
    /// <param name="playerId">Игрок.</param>
    /// <param name="bank">Казна после строки.</param>
    /// <param name="journalRow">Строка <c>bank</c> журнала (а не служебная постановка казны харнессом).</param>
    public void SetBank(string playerId, Bank bank, bool journalRow = false)
    {
        if (journalRow) _bankRows.Add(playerId);
        var map = _state.Looks.Map;
        if (!map.Players.TryGetValue(playerId, out var player)) return;
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, players: WithBank(map, player, bank)), _state.Looks.Stacks) };
    }

    /// <summary>Обучение у тренера из журнала (<c>site_train</c>) — общим действием порта (<see cref="GameSession.Train"/>,
    /// по команде ИИ). Возвращает хозяина и списанное: движок вычитает его и из кошелька тактики (<c>0x46d322</c>).</summary>
    public (string Owner, Bank Paid) Train(string siteId, string unitId)
    {
        var session = Session();
        var stack = session.Map.Stacks.Values.FirstOrDefault(s => s.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase));
        if (stack is null) return ("", Bank.Zero);
        session.Train(stack.Id, siteId, unitId, out var paid, ai: true);
        _state = session.State;
        return (stack.Owner, paid);
    }

    /// <summary>Найм у наёмника из журнала (<c>site_buy_unit</c>, ИИ — тактика <c>HireMercs</c>) общим действием порта
    /// (<see cref="GameSession.HireMercenary"/>, по команде ИИ): без него места в отряде после найма у порта не убывают, и в следующем
    /// окне того же отряда строится лишний <c>HireUnit</c> (port-synth-4-ai, день 1: <c>S143KC001a</c> у <c>S143SI0004</c>, seq 579356 и
    /// 579361). Казну затем ставит строка <c>bank</c>. Возвращает причину отказа порта; null — нанят.</summary>
    public string? BuyMercenary(SiteBuyUnitEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var session = Session();
        if (session.HireMercenary(e.Stack, e.Site, e.UnitType, ai: true) is { } why) return why;
        _state = session.State;
        return null;
    }

    /// <summary>Перенос юнита между группами из журнала (<c>unit_transfer</c> вида <c>move</c>/<c>swap</c>; у ИИ — перенос из гарнизона в
    /// отряд при подготовке, <c>0x5d5d73</c>): общим действием порта <see cref="GameSession.SwapUnits"/>. Перестановки внутри одной группы
    /// не повторяются.
    /// <para>28.09: юнит, нанятый в живом дне, берёт id движка (<see cref="Rename"/>); если в клетках порта не те юниты, что называет
    /// строка, перенос сразу по id (<see cref="ById"/>) — по клеткам правило его пропускало и уводило других (probe65, день 8:
    /// <c>FT0010#4</c> ↔ <c>KC009f#0</c> увёл <c>UN0289</c> вместо <c>UN0288</c>); перестановка ИИ — с расстановкой отряда в начале
    /// серии и после каждого обмена (<see cref="ArrangeAfter"/>).</para></summary>
    public void Transfer(UnitTransferEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!e.Ret || e.SameGroup || e.To is null || e.Kind is not ("move" or "swap")) return;
        // Серия перестановок — переносы одной пары групп подряд; перенос другой пары сначала завершает прежнюю серию.
        if (_toArrange.Count > 0 && !(_toArrange.Contains(e.From, StringComparer.OrdinalIgnoreCase)
                                      && _toArrange.Contains(e.To, StringComparer.OrdinalIgnoreCase))) FlushArrange();
        if (Rename(e.From, e.FromPos, e.Unit) | Rename(e.To, e.ToPos, e.Other)) Count("переименован нанятый в живом дне");
        // Начало серии (0x5d5d73): отряд расставляется без центровки до первого обмена.
        if (ArrangeReorder && e.Cause == "ai_reorder" && _toArrange.Count == 0) ArrangeNow(e.To, center: false);
        var session = new GameSession(_state, _art, _book);   // без расстановки: серия перестановок ещё идёт
        bool atCells = AtCells(session, e);
        if (ForceById && !atCells && ById(session, e))
        {
            Done(session, e);
            Count("по id: в клетках порта другие юниты");
            return;
        }
        if (session.SwapUnits(e.From, (int)e.FromPos, e.To, (int)e.ToPos) is not { } refusal)
        {
            Done(session, e);
            return;
        }
        if (ForceById && atCells && ById(session, e))
        {
            Done(session, e);
            Count("проведён по id");
            return;
        }
        Count($"{e.Kind}: {refusal}");
        if (TransferExamples.Count < 12)
        {
            string Cell(string g, long pos) => session.GroupCells(g) is { } c && pos >= 0 && pos < c.Cells.Count ? c.Cells[(int)pos] : "?";
            string Fill(string g) => session.GroupCells(g) is { } c ? $"{c.Occupied}/{session.GroupCapacity(g)} [{string.Join(",", c.Cells.Select(x => x.Length == 0 ? "-" : x[^4..]))}]" : "?";
            TransferExamples.Add($"seq {e.Seq} {e.Kind} {e.From}#{e.FromPos} → {e.To}#{e.ToPos} {e.Unit}/{e.Other}: {refusal}; порт: там {Cell(e.From, e.FromPos)}/{Cell(e.To, e.ToPos)}, {e.From} {Fill(e.From)}, {e.To} {Fill(e.To)}");
        }
    }

    private void Done(GameSession session, UnitTransferEvent e)
    {
        _state = session.State;
        ArrangeAfter(e);
    }

    private void Count(string key) => TransferRefusals[key] = TransferRefusals.GetValueOrDefault(key) + 1;

    /// <summary>В клетке <c>from_pos</c> у порта — уходящий юнит журнала, в клетке <c>to_pos</c> — встречный (или пусто у <c>move</c>).
    /// Строка без id (старые журналы) — считается, что те.</summary>
    private static bool AtCells(GameSession session, UnitTransferEvent e)
    {
        if (e.Unit is null || e.To is null || session.GroupCells(e.From) is not { } from || session.GroupCells(e.To) is not { } to) return true;
        if (e.FromPos is < 0 or >= UnitCells.CellCount || e.ToPos is < 0 or >= UnitCells.CellCount) return true;
        return Same(from.Cells[(int)e.FromPos], e.Unit) && (e.Other is null ? to.Cells[(int)e.ToPos].Length == 0 : Same(to.Cells[(int)e.ToPos], e.Other));
    }

    /// <summary>Перенос, который правило порта отвергло, проводится по id юнитов (<see cref="ById"/>).</summary>
    public bool ForceById { get; init; } = true;

    /// <summary>
    /// Перенос журнала по id юнитов, без правил: движок его уже провёл. Клетки порта могут разойтись с движком — расстановку
    /// группы ИИ (<c>0x5e7991</c>) зонд переносами не пишет, и номер клетки события указывает у порта на другого юнита (27.09:
    /// human 230, probe65 ~156, ai-q09 168 отказов «нет места»/«клетки не годятся»/«не покидает группу»). Уходящий <c>unit</c>
    /// из <c>from</c>, встречный <c>other</c> из <c>to</c>; каждый встаёт в клетку движка, если она годна
    /// (<see cref="GroupArrange.Fits"/>), иначе в первую годную. Юнита нет там, где его ждёт журнал, — перенос не проводится.
    /// </summary>
    private static bool ById(GameSession session, UnitTransferEvent e)
    {
        if (e.Unit is null || e.To is null || session.GroupCells(e.From) is not { } from || session.GroupCells(e.To) is not { } to) return false;
        if (!from.Cells.Any(c => Same(c, e.Unit)) || (e.Other is not null && !to.Cells.Any(c => Same(c, e.Other)))) return false;
        bool unitBig = from.Cells.Count(c => Same(c, e.Unit)) == 2;
        bool otherBig = e.Other is not null && to.Cells.Count(c => Same(c, e.Other)) == 2;
        var fromLeft = Without(from, e.Unit);
        var toLeft = e.Other is null ? to : Without(to, e.Other);
        if (Put(toLeft, e.Unit, (int)e.ToPos, unitBig) is not { } toAfter) return false;
        var fromAfter = fromLeft;
        if (e.Other is not null)
        {
            if (Put(fromLeft, e.Other, (int)e.FromPos, otherBig) is not { } f) return false;
            fromAfter = f;
        }
        session.SetCells(e.From, fromAfter);
        session.SetCells(e.To, toAfter);
        return true;
    }

    /// <summary>
    /// Юнит, созданный в живом дне, получает id движка по первой строке переноса, которая его называет: движок раздаёт номера по
    /// типу счётчиком, который номера погибших за сессию не переиспользует и растёт от всех наймов всех игроков
    /// (<c>CMidgardScenarioMap::freeIdTypeIndices</c>, как у отрядов — <see cref="Hired"/>); порт тем же счётчиком
    /// (<see cref="GameSession.NewId"/>), но порядок наймов живого дня свой. Строки найма (<c>hire_unit</c>, <c>hire_leader</c>) id не пишут. Журнал называет в клетке
    /// <paramref name="pos"/> группы <paramref name="group"/> юнита <paramref name="id"/>, которого там нет, а в этой клетке у порта —
    /// новый юнит: он и есть этот. Если id у порта уже занят другим новым юнитом (лидер, нанятый у движка раньше), имена меняются
    /// местами. Юниты сохранения не переименовываются.
    /// </summary>
    private bool Rename(string group, long pos, string? id)
    {
        var session = new GameSession(_state, _art, _book);
        if (id is null || session.GroupCells(group) is not { } cells || cells.Units.Contains(id, StringComparer.OrdinalIgnoreCase)
            || pos < 0 || pos >= cells.Cells.Count || cells.Cells[(int)pos] is not { Length: > 0 } port || _loaded.Contains(port)
            || _loaded.Contains(id)) return false;
        var map = session.Map;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [port] = id };
        if (map.Units.ContainsKey(id)) names[id] = port;
        string Name(string u) => names.GetValueOrDefault(u, u);
        var units = map.Units.Values.Select(u => names.ContainsKey(u.Id) ? u with { Id = Name(u.Id) } : u)
                       .ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value.UnitIds.Any(names.ContainsKey)
            ? p.Value with { UnitIds = [.. p.Value.UnitIds.Select(Name)], LeaderId = Name(p.Value.LeaderId) } : p.Value,
            StringComparer.OrdinalIgnoreCase);
        var sites = map.Sites.Select(f => f.Guards.Any(names.ContainsKey) ? f with { Guards = [.. f.Guards.Select(Name)] } : f).ToList();
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, units: units, stacks: stacks, sites: sites), _state.Looks.Stacks) };
        return true;
    }

    /// <summary>Перестановка ИИ (<c>0x5d5d73</c>, как <see cref="AiStackUnits.TakeFromGarrison"/>): отряд расставляется без центровки в
    /// начале серии (<c>0x5d5eab</c>) и после каждого обмена (<c>0x5d6152</c>), в конце — гарнизон и отряд с центровкой
    /// (<c>0x5d5ee8</c>, <c>0x5d5ef3</c>). Строк у расстановки в журнале нет; ряд юнита меняет его силу, а клетки — номера клеток
    /// следующих строк серии. 28.09: с расстановкой после каждого обмена клетки порта не те, что в строке, у 8/11/3/9 переносов,
    /// без неё — у 164/144/228/47 (ai-q09/probe65/human/legions). 27.09 расстановка с центровкой после каждого обмена была хуже
    /// (human выбор 1445).</summary>
    private void ArrangeAfter(UnitTransferEvent e)
    {
        if (!ArrangeReorder || e.Cause != "ai_reorder" || e.To is null) return;
        // 0x5d6152: после каждого обмена отряд расставляется без центровки.
        ArrangeNow(e.To, center: false);
        // Порядок как у движка (0x5d5d73): гарнизон, затем отряд — при первом обращении к дню после серии.
        foreach (string group in new[] { e.From, e.To })
            if (!_toArrange.Contains(group, StringComparer.OrdinalIgnoreCase)) _toArrange.Add(group);
    }

    private void ArrangeNow(string group, bool center)
    {
        var session = new GameSession(_state, _art, _book);
        if (session.GroupCells(group) is { } cells && cells.Units.All(session.Map.Units.ContainsKey))
            AiStackUnits.Arrange(session, group, center);
        _state = session.State;
    }

    /// <summary>Расставлять ли группы после перестановки ИИ (<see cref="ArrangeAfter"/>).</summary>
    public bool ArrangeReorder { get; init; } = true;

    private static UnitCells Without(UnitCells cells, string id) =>
        new([.. cells.Units.Where(u => !Same(u, id))], [.. cells.Cells.Select(c => Same(c, id) ? "" : c)]);

    private static UnitCells? Put(UnitCells cells, string id, int cell, bool big)
    {
        var list = cells.Cells.ToArray();
        if (cell < 0 || cell >= list.Length || !GroupArrange.Fits(list, big, cell))
            cell = Enumerable.Range(0, list.Length).Where(c => GroupArrange.Fits(list, big, c)).DefaultIfEmpty(-1).First();
        if (cell < 0) return null;
        list[cell] = id;
        if (big) list[GroupArrange.Pair(cell)] = id;
        return new([.. cells.Units, id], list);
    }

    /// <summary>Переносы журнала, которые порт не провёл: отказ → число (диагностика живого дня).</summary>
    public SortedDictionary<string, int> TransferRefusals { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Первые отвергнутые переносы — для разбора.</summary>
    public List<string> TransferExamples { get; init; } = [];

    /// <summary>Здоровье из <c>hp_change</c>/<c>revive</c>/<c>potion_use</c>; у лидера — и флаг «лидер жив».</summary>
    public void Hp(string unitId, int hp)
    {
        var map = _state.Looks.Map;
        if (!map.Units.TryGetValue(unitId, out var unit)) return;
        var led = map.Stacks.Values.FirstOrDefault(s => Same(s.LeaderId, unitId) && s.LeaderAlive != hp > 0);
        SetUnit(unit with { Hp = hp }, led is null ? null : led with { LeaderAlive = hp > 0 });
    }

    private void SetUnit(MapUnit unit, MapStack? stack)
    {
        var map = _state.Looks.Map;
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[unit.Id] = unit;
        var stacks = stack is null ? null : map.Stacks.ToDictionary(p => p.Key, p => p.Key.Equals(stack.Id, StringComparison.OrdinalIgnoreCase) ? stack : p.Value,
                                                                     StringComparer.OrdinalIgnoreCase);
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, units: units, stacks: stacks), _state.Looks.Stacks) };
    }

    /// <summary>Найм лидера из журнала (<c>hire_leader</c>): отряд-гость в укреплении, как у движка. Казна порта могла
    /// разойтись с движком (свои траты стратегии, например рост деревни), а движок нанял — поэтому найм проходит всегда,
    /// цена списывается с обрезкой в ноль.</summary>
    public void HireLeader(HireLeaderEvent h)
    {
        if (_hiredEarly.Remove(h.Seq)) return;
        FlushArrange();
        _state = Hired(_state, h);
    }

    /// <summary>Найм <see cref="HireLeader"/> поверх состояния <paramref name="state"/>.</summary>
    private GameState Hired(GameState state, HireLeaderEvent h)
    {
        if (!h.Ret) return state;
        var map = state.Looks.Map;
        if (map.Sites.FirstOrDefault(s => Same(s.Id, h.Fort)) is not { } fort || !map.Players.TryGetValue(fort.Owner, out var player)) return state;
        var cost = Economy.HireCost(h.UnitType, _book.Economy);
        var session2 = new GameSession(state with
        {
            Looks = GameSession.Retold(state.Looks, GameSession.With(map, players: WithBank(map, player, player.Bank + cost)), state.Looks.Stacks),
        }, _art, _book);
        if (session2.HireLeader(h.Fort, h.UnitType) is not null) return state;
        var after = session2.Map;
        _justHired.UnionWith(after.Stacks.Keys.Where(k => !map.Stacks.ContainsKey(k)));
        _lastHire = (h.Seq, after.Stacks.Keys.FirstOrDefault(k => !map.Stacks.ContainsKey(k)));
        return session2.State with
        {
            Looks = GameSession.Retold(session2.State.Looks, GameSession.With(after, players: WithBank(after, player, Treasury.Pay(player.Bank, cost))),
                                       session2.State.Looks.Stacks),
        };
    }

    /// <summary>С какой строки журнала найм лидера сдвигает поток CRT харнесса (<see cref="BeginTurn"/>: первый выбор юнита хода, перед
    /// которым поток поставлен по журналу; выборов нет — начало хода). <see cref="long.MaxValue"/> — поток по журналу не ставился.</summary>
    private long _crtFrom = long.MaxValue;

    /// <summary>Имя нового лидера движок берёт CRT <c>rand()</c> (<c>0x44b126</c>, <c>ai_rng</c> <c>leader_name</c>; порт —
    /// <c>AiEconomy.ExecuteHireLeader</c>), а найм лидера журнала харнесс проводит без него. Поток харнесса поставлен по журналу перед
    /// первым выбором юнита хода (<see cref="BeginTurn"/>), и найм лидера после этого выбора сдвигает тип следующих наймов в гарнизон —
    /// шаг делается в потоке сессии стратегии <paramref name="session"/>. Карта 4b/4c/4d, день 2, <c>S143PL0004</c>: без шага порт брал
    /// <c>G000UU8025</c> вместо <c>G000UU8018</c> в <c>FT000c</c>, на рост <c>FT000d</c> не хватало золота, и радиус <c>FT000d</c> в сетке влияния
    /// выходил 14 вместо 15 (счёт TakeBag и GiveItems ниже на 3 и 4; реестр И20, И44).</summary>
    private void NameRoll(GameSession session, GameState state, HireLeaderEvent h)
    {
        var map = state.Looks.Map;
        if (h.Seq <= _crtFrom || map.Sites.FirstOrDefault(s => Same(s.Id, h.Fort)) is not { } fort
            || !map.Players.TryGetValue(fort.Owner, out var player)) return;
        if (_book.LeaderNames.GetValueOrDefault(player.RaceId) is { Count: > 0 }) session.Crt.Next();
    }

    /// <summary>Строки <c>hire_leader</c> и <c>hire_unit</c>, применённые до оперативного слоя (<see cref="Begin"/>).</summary>
    private readonly HashSet<long> _hiredEarly = [];

    /// <summary>Найм юнита в гарнизон из журнала (<c>hire_unit</c>): тип и клетка — движка, id — по счётчику карты
    /// (<see cref="GameSession.NewId"/>, как у движка), базовый уровень с полным здоровьем, цена — с обрезкой в ноль.
    /// Проверки найма не повторяются: движок уже нанял.</summary>
    public void HireUnit(HireUnitEvent h)
    {
        if (_hiredEarly.Remove(h.Seq)) return;
        _state = UnitHired(_state, h);
    }

    /// <summary>Найм <see cref="HireUnit"/> поверх состояния <paramref name="state"/>.</summary>
    private GameState UnitHired(GameState state, HireUnitEvent h)
    {
        if (!h.Ret) return state;
        var map = state.Looks.Map;
        var fort = map.Sites.FirstOrDefault(s => Same(s.Id, h.Group));
        if (fort is null || !map.Players.TryGetValue(fort.Owner, out var player)
            || !_book.Economy.Hire.TryGetValue(h.UnitType, out var hire) || _book.Grown(h.UnitType, hire.Level) is not { } grown) return state;
        var ids = map.IdNext;
        string id = GameSession.NewId(ref ids, map.Units.Keys, fort.Id, "UN");
        var cells = fort.GuardSlots.Count == 6 ? fort.GuardSlots.ToArray() : [-1, -1, -1, -1, -1, -1];
        int at = (int)h.Position;
        if (at is < 0 or > 5) return state;
        cells[at] = fort.Guards.Count;
        if (!grown.Type.SizeSmall && at % 2 == 0) cells[at + 1] = fort.Guards.Count;
        var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[id] = new MapUnit { Id = id, TypeId = grown.Type.Id.ToUpperInvariant(), Level = grown.Level, Hp = grown.Type.HitPoints };
        var sites = map.Sites.Select(s => ReferenceEquals(s, fort) ? s with { Guards = [.. s.Guards, id], GuardSlots = cells } : s).ToList();
        var players = WithBank(map, player, Treasury.Pay(player.Bank, Economy.HireCost(h.UnitType, _book.Economy)));
        return state with { Looks = GameSession.Retold(state.Looks, GameSession.With(map, units: units, players: players, sites: sites, idNext: ids),
                                                       state.Looks.Stacks) };
    }

    private readonly HashSet<string> _justHired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Последний найм лидера живого дня: строка журнала и id отряда у порта.</summary>
    private (long Seq, string? Stack) _lastHire;

    /// <summary>Строк журнала между <c>hire_leader</c> и выдачей id его отряда (<c>id_alloc</c> юнита, затем отряда — seq +4 в карте 23).</summary>
    private const int HireAllocWindow = 20;

    /// <summary>
    /// Выдача id (<c>id_alloc</c>) сразу за наймом лидера (<see cref="Hired"/>): отряд у порта получает номер движка. Счётчик живого дня может
    /// уйти вперёд (отряд, созданный живым днём иначе, чем у движка), и тогда найм получает следующий номер — карта 23, день 20: найм
    /// <c>S143PL0002</c> в <c>S143FT0005</c> (seq 3543119) у движка — <c>S143KC0037</c> (seq 3543123), у живого дня — <c>KC0038</c>, и выборы
    /// <c>KC0037</c> (AssistMove к <c>KC0004</c>) окно не находило. Номер занят другим отрядом живого дня — имена меняются местами.
    /// </summary>
    public void IdAlloc(IdAllocEvent e)
    {
        if (_lastHire.Stack is not { } mine || e.Id is not { Length: 10 } id || !id[4..6].Equals("KC", StringComparison.OrdinalIgnoreCase)
            || e.Seq <= _lastHire.Seq || e.Seq > _lastHire.Seq + HireAllocWindow) return;
        _lastHire = default;
        if (Same(mine, id)) return;
        var map = _state.Looks.Map;
        if (!map.Stacks.ContainsKey(mine) || _loaded.Contains(id)) return;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [mine] = id };
        if (map.Stacks.ContainsKey(id)) names[id] = mine;
        string Name(string x) => names.GetValueOrDefault(x, x);
        var stacks = map.Stacks.Values.Select(st => names.ContainsKey(st.Id) ? st with { Id = Name(st.Id) } : st)
                        .ToDictionary(st => st.Id, StringComparer.OrdinalIgnoreCase);
        var sites = map.Sites.Select(f => names.ContainsKey(f.StackId) ? f with { StackId = Name(f.StackId) } : f).ToList();
        var plan = map.Plan.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[.. p.Value.Select(Name)]);
        _justHired.Remove(mine);
        _justHired.Add(id);
        var looks = _state.Looks.Stacks.Select(l => names.ContainsKey(l.Id) ? l with { Id = Name(l.Id) } : l).ToList();
        _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, stacks: stacks, sites: sites, plan: plan), looks) };
    }

    /// <summary>
    /// Литьё из журнала (<c>spell_cast</c>) — только призыв: отряд заклинателя ставится общим действием порта (<c>GameSession.Summon</c>,
    /// клетка — <see cref="GameSession.SummonCell"/>), без проверок и оплаты (движок уже заплатил — казну ведут строки <c>bank</c>). Урон и
    /// лечение заклинаний живой день берёт из строк здоровья, здесь их не повторить. Приказ призванного приходит строкой <c>ai_order</c>.
    /// Без этого призванного (human, день 46: <c>KC00A5</c> на <c>KC0009</c>) в живом дне нет, его окна отсеиваются — и кэш исключений не
    /// видит смены отряда.
    /// </summary>
    /// <summary>Надевание и снятие предмета журнала (<c>item_equip</c>/<c>item_unequip</c>, <c>apply</c> = true, <c>0x41f0fe</c>/<c>0x5e746d</c>):
    /// <c>MOD_EQUIP</c> носителям — правилом порта (<see cref="GameSession.EquipItem"/>). Снаряжение ИИ (<c>0x451ff3</c>) — оперативный слой,
    /// живой день его не строит: без этого у лидера нет модификатора оружия (probe72: <c>S143UN0246</c> день 8 — <c>UM2302</c>).</summary>
    public void Equip(ItemEquipEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!e.Apply || !e.Ret) return;
        // Предмет надевается из сумки своего отряда; нет в ней, но есть у другого отряда — его передали строкой, которую журнал не пишет
        // (передача предметов, 0x6d09c4; legions день 34: знамя S073IM00b6 от KC0089 к KC0000). Переносится вслед за надеванием.
        if (e.Equip && e.Item is { } want && _state.Looks.Map.Stacks.TryGetValue(e.Stack, out var to)
            && !to.Items.Contains(want, StringComparer.OrdinalIgnoreCase)
            && _state.Looks.Map.Stacks.Values.FirstOrDefault(x => x.Items.Contains(want, StringComparer.OrdinalIgnoreCase)) is { } giver)
        {
            var passing = Session();
            int worn = D2.Rules.Magic.Equipment.Slots(giver).ToList().FindIndex(q => Same(q, want));
            if (worn >= 0) passing.UnequipItem(giver.Id, worn);   // надетое у прежнего хозяина снимается вместе с MOD_EQUIP
            _state = passing.State;
            var bag = _state.Looks.Map.Stacks[giver.Id];
            Put(bag with { Items = [.. bag.Items.Where(i => !Same(i, want))] });
            Put(_state.Looks.Map.Stacks[e.Stack] with { Items = [.. _state.Looks.Map.Stacks[e.Stack].Items, want] });
        }
        var session = Session();
        if (!session.Map.Stacks.ContainsKey(e.Stack) || e.Slot is < 0 or >= D2.Rules.Magic.Equipment.SlotCount) return;
        if (!e.Equip) session.UnequipItem(e.Stack, (int)e.Slot);
        else if (e.Item is { } item && session.Map.Stacks[e.Stack].Items.Contains(item, StringComparer.OrdinalIgnoreCase))
            session.EquipItem(e.Stack, item, (int)e.Slot);
        _state = session.State;
    }

    /// <summary>Литьё журнала в живом дне. Сначала запись <c>CMidSpellCast</c> — суточный счёт литий (<c>0x5dba61</c> → <c>0x606eb6</c>:
    /// не маг — одно литьё заклинания за ход, маг — два; <c>CastRecord</c>, как <see cref="GameSession.CastSpell"/> — у не-нейтрала и не
    /// бесплатного), без эффекта и оплаты: урон и казну дают строки журнала (<c>hp_change</c>, <c>bank</c>). Без записи годность заклинания
    /// перед следующей атакой того же хода у порта шире, чем у движка (probe65 S143PL0004 день 18: SS0097 уже брошено в 28,4, движок
    /// берёт призыв SS0098, порт — снова SS0097; <c>20261019c-cov</c> KC005f день 16: после <c>G000SS0044</c> старших у движка 1, у порта 2).
    /// Призыв затем ставится действием порта.</summary>
    public void Cast(SpellCastEvent c)
    {
        if (!c.Ret || !_book.Spells.TryGetValue(c.Spell, out var spell)) return;
        var session = Session();
        if (!c.Free && !AiTactics.NeutralOwner(session, c.Player))
        {
            var map = session.Map;
            var casts = (IReadOnlyList<CastRecord>)[.. map.SpellCasts, new CastRecord(c.Player, spell.Id, D2.Rules.Magic.SpellCast.TurnStop(map.Turn))];
            _state = _state with { Looks = GameSession.Retold(_state.Looks, GameSession.With(map, spellCasts: casts), _state.Looks.Stacks) };
            session = Session();
        }
        if (spell.Category != D2.Rules.Magic.SpellCategory.Summon) return;
        if (session.SummonCell((int)c.X, (int)c.Y) is not { } cell) return;
        var was = session.Map.Stacks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        session.Summon(c.Player, spell.SummonUnitId, cell, target: null);
        _state = session.State;
        _justSummoned.UnionWith(session.Map.Stacks.Keys.Where(k => !was.Contains(k)));
    }

    private readonly HashSet<string> _justSummoned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Первая строка <c>stack_move</c> незнакомого отряда рядом с призванным (кольца клетки призыва — до 3) — это он, с id
    /// движка (как <see cref="Hired"/>). Два призванных на равном ближайшем расстоянии — неоднозначно, не переименовывается.</summary>
    private MapStack? Summoned(MapState map, string id, int x, int y)
    {
        _justSummoned.RemoveWhere(h => !map.Stacks.ContainsKey(h));   // погибшие, не успев сойти с места
        var near = _justSummoned.Select(h => map.Stacks[h])
            .Select(h => (Stack: h, D: Math.Max(Math.Abs(h.X - x), Math.Abs(h.Y - y))))
            .Where(p => p.D <= SummonReach).ToList();
        if (near.Count == 0) return null;
        int best = near.Min(p => p.D);
        if (near.Count(p => p.D == best) > 1) return null;
        var old = near.First(p => p.D == best).Stack;
        var renamed = old with { Id = id };
        Put(renamed, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { old.Id });
        _state = _state with { Looks = GameSession.Retold(_state.Looks, _state.Looks.Map,
            [.. _state.Looks.Stacks.Select(l => Same(l.Id, old.Id) ? l with { Id = id } : l)]) };
        _justSummoned.Remove(old.Id);
        return renamed;
    }

    private const int SummonReach = 3;

    /// <summary>
    /// Первая строка <c>stack_move</c> незнакомого отряда после найма — постановка нанятого: он получает id движка. Движок ведёт
    /// счётчик номеров по типу (<c>CMidgardScenarioMap::freeIdTypeIndices</c>: при загрузке — наибольший + 1, дальше только растёт), и
    /// номера погибших за сессию не переиспользует; порт с 30.09 тоже (<see cref="GameSession.NewId"/>, <see cref="MapState.IdNext"/>), но
    /// живой день начинается с сохранения, а счётчик движка к этому часу бывает выше (<see cref="SyncIds"/>), и найм ИИ в живом дне идёт
    /// своим порядком. Строки журнала о нанятом без переименования терялись:
    /// legions, день 53 — движок нанял <c>KC009A</c>, у порта <c>KC0097</c> остался в <c>FT000A</c> и не дал на день 54 нанять
    /// помощника <c>KC009B</c>. Строка должна прийти в крепость нанятого или рядом (постановка — в клетке крепости): без этой проверки
    /// нанятый, так и не вышедший за день (legions, день 12, <c>KC007F</c> в <c>FT0001</c>), сливался с незнакомым порту нейтралом.
    /// </summary>
    private MapStack? Hired(MapState map, string id, int x, int y)
    {
        _justHired.RemoveWhere(h => !map.Stacks.ContainsKey(h));
        // Только нанятый, ещё стоящий в своей крепости, и клетка постановки — в ней или рядом (вход): призыв и прочие новые отряды
        // сюда не попадают.
        var old = _justHired.Select(h => map.Stacks.GetValueOrDefault(h)).OfType<MapStack>()
            .Where(h => AiTactics.Forts(map).FirstOrDefault(f => Same(f.Id, h.Inside)) is { } f
                        && x >= f.X - 1 && y >= f.Y - 1 && x <= f.X + Size(f) && y <= f.Y + Size(f))
            .OrderBy(h => h.X == x && h.Y == y ? 0 : 1).FirstOrDefault();
        if (old is null) return null;
        var renamed = old with { Id = id };
        Put(renamed, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { old.Id });
        // Вид отряда (StackLook) тоже под id движка.
        _state = _state with { Looks = GameSession.Retold(_state.Looks, _state.Looks.Map,
            [.. _state.Looks.Stacks.Select(l => Same(l.Id, old.Id) ? l with { Id = id } : l)]) };
        _justHired.Remove(old.Id);
        _justHired.Add(id);
        return renamed;
    }

    private static Dictionary<string, MapPlayer> WithBank(MapState map, MapPlayer player, Bank bank)
    {
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[player.Id] = map.Players[player.Id] with { Bank = bank };
        return players;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
