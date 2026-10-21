using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;

namespace D2.Rules.Tests;

/// <summary>
/// Разбор корпуса для задачи 26 Ф7 — четыре сверки картовых утверждений вики. Ни одна не сравнивает
/// модель с самой собой:
/// <list type="bullet">
/// <item><c>dead_leader_ground</c> — пара цен одной клетки из журнала (живой лидер и павший) должна
/// объясняться ОДНИМ грунтом нашей таблицы <see cref="MovementRules.Vanilla"/>; грунт при этом из
/// <c>.sg</c> не берётся вовсе, поэтому ни смена террейна заклинанием, ни маски пути сверку не
/// портят. Рядом считается гипотеза вики «мёртвый платит ровно вдвое».</item>
/// <item><c>map_effect_turn_stop</c> — <c>TURN_STOP</c> записи <c>CMidSpellCast</c> сохранения
/// против <c>CUR_TURN</c> того же файла (два разных поля файла).</item>
/// <item><c>attack_move_half</c> — остаток хода атакующего в сохранении конца дня против
/// <c>ceil(MOVE/2)</c> полного запаса (поле <c>.sg</c> против таблицы).</item>
/// <item><c>spell_ward_columns</c> — колонки <c>Gspells.WARD1..4</c> таблицы: посылка утверждения
/// «заклинание карты тратит оберег».</item>
/// </list>
/// </summary>
internal sealed class MapWikiAudit(ProgressBook book, Counters c)
{
    /// <summary>Гипотеза вики рядом со сверкой — как это делала задача 11 с <c>move_left</c>.</summary>
    private const string DoubleHypothesis = "dead_leader_ground · гипотеза вики «ровно вдвое»";

    /// <summary>Клетки рукодельной карты: 0 равнина, 1 лес, 2 вода, 3 равнина с дорогой,
    /// 4 горы. Грунт кандидата подставляется в <see cref="Movement.Cost"/> вместе с флагами
    /// события — так пара цен проверяется на существование общего грунта.</summary>
    private static readonly MapState Probe = ProbeMap();

    private static readonly (int X, string Name)[] Candidates =
        [(0, "равнина"), (1, "лес"), (2, "вода"), (3, "дорога"), (4, "горы")];

    // ---- Н19: оберег ---------------------------------------------------------------------------

    /// <summary>Ни одно вани́льное заклинание оберегов не выдаёт — колонки <c>WARD1..4</c> пусты у
    /// всех строк <c>Gspells</c>. Значит и тратить на карте нечего, а вики Н19 проверяема только
    /// живым замером (план Ф7 §3).</summary>
    public void WardColumns()
    {
        foreach (var (id, spell) in book.Prices.Spells)
            c.Count("spell_ward_columns", spell.Wards.Count == 0,
                    $"{id}: WARD1..4 несут {spell.Wards.Count} оберегов — посылка задачи 26 неверна");
    }

    // ---- Н17: павший лидер ---------------------------------------------------------------------

    /// <summary>
    /// Пары событий <c>move_cost</c> одной клетки с одними флагами: живой лидер и павший.
    /// <para>Событие пишется ПО КЛЮЧУ <c>x:y:флаги:ret</c> (<see cref="MoveCostEvent"/>), поэтому
    /// пара — это пара ключей, а не пара вызовов.</para>
    /// </summary>
    public void DeadLeaderSteps(string path)
    {
        // Ключ — с потоком загрузки (tid): в одном журнале синтетического прогона десятки карт, и клетка (x,y)
        // разных карт — разный грунт (20260916-synth passive-003/004: 225 ложных расхождений).
        Dictionary<(long Tid, Key Key), long> alive = [], dead = [];
        foreach (var e in JournalCache.Read(path, "move_cost").OfType<MoveCostEvent>())
            (e.LeaderAlive ? alive : dead)[(e.Tid, KeyOf(e))] = e.Ret;
        foreach (var ((tid, key), deadCost) in dead)
        {
            if (!alive.TryGetValue((tid, key), out long aliveCost)) { c.Skip("dead_leader_ground", "у клетки нет пары с живым лидером"); continue; }
            if (key.WaterOnly) { c.Skip("dead_leader_ground", "отряд «только по воде»: глубину клетки из журнала не видно"); continue; }
            if (aliveCost == 0 || deadCost == 0)
            { c.Skip("dead_leader_ground", "в одной из двух цен ноль — клетку запретила маска пути или сменился грунт"); continue; }

            string found = "";
            foreach (var (x, name) in Candidates)
            {
                if (Movement.Cost(Probe, x, 0, key.Options(true), MovementRules.Vanilla) != aliveCost) continue;
                if (Movement.Cost(Probe, x, 0, key.Options(false), MovementRules.Vanilla) != deadCost) continue;
                found = name;
                break;
            }
            string detail = $"{Path.GetFileName(path)} ({key.X},{key.Y}) {key.Flags()}: " +
                            $"журнал живой {aliveCost}, павший {deadCost}" +
                            (found.Length > 0 ? $" — грунт «{found}»" : " — ни один грунт таблицы не даёт обе цены");
            c.Count("dead_leader_ground", found.Length > 0, detail);
            c.Count(DoubleHypothesis, deadCost == 2 * aliveCost, detail);
            if (deadCost != 2 * aliveCost)
                _notDouble[$"{aliveCost} → {deadCost}, грунт «{found}»"] =
                    _notDouble.GetValueOrDefault($"{aliveCost} → {deadCost}, грунт «{found}»") + 1;
        }
    }

    /// <summary>Сводка по расхождениям гипотезы «ровно вдвое»: каждая пара цен со своим грунтом и
    /// числом наблюдений. Печатается строкой, чтобы разбор 131 расхождения был машинным, а не
    /// пересчитанным руками в отчёте.</summary>
    public void PrintNotDouble()
    {
        if (_notDouble.Count == 0) return;
        c.Note(" гипотеза «ровно вдвое» расходится так: " +
               string.Join("; ", _notDouble.OrderByDescending(p => p.Value).Select(p => $"{p.Key} — {p.Value}")));
    }

    private readonly SortedDictionary<string, int> _notDouble = new(StringComparer.Ordinal);

    // ---- Н18: длительность на карте ------------------------------------------------------------

    /// <summary>
    /// Записи <c>CMidSpellCast</c> всех сохранений корпуса: живая запись обязана лежать в окне
    /// <c>[CUR_TURN, CUR_TURN + 1]</c> — это и есть «до начала следующего хода».
    /// Рядом считается гипотеза «держится больше суток».
    /// </summary>
    public void MapEffects()
    {
        int spells = 0, items = 0;
        foreach (var path in CorpusSaves())
        {
            MapState map;
            try { map = MapLoader.Load(path); }
            catch (Exception e) { c.Skip("map_effect_turn_stop", $"сохранение не читается: {e.GetType().Name}"); continue; }
            spells += map.SpellCasts.Count;
            items += map.ItemCasts.Count;
            foreach (var (list, kind) in new[] { (map.SpellCasts, "заклинание"), (map.ItemCasts, "предмет") })
            {
                foreach (var r in list)
                {
                    bool ok = r.TurnStop >= map.Turn && r.TurnStop <= SpellCast.TurnStop(map.Turn);
                    string detail = $"{Path.GetFileName(path)} CUR_TURN {map.Turn}: {kind} {r.Origin} " +
                                    $"игрока {r.PlayerId} TURN_STOP {r.TurnStop}";
                    c.Count("map_effect_turn_stop", ok, detail);
                    c.Count("map_effect_turn_stop · гипотеза «TURN_STOP ≥ CUR_TURN + 2»",
                            r.TurnStop > SpellCast.TurnStop(map.Turn), detail);
                }
            }
        }
        c.Note($" записей CMidSpellCast в сохранениях корпуса: заклинаний {spells}, применений предметов {items}");
    }

    // ---- Н16: атака стоит половину запаса ------------------------------------------------------

    /// <summary>
    /// Окно «конец хода дня N — конец хода дня N+1»: у каждого игрока внутри ровно один ход, и в
    /// начале своего хода отряд получил полный запас. Отсюда два счётчика:
    /// <list type="bullet">
    /// <item><c>attack_move_half</c> — отряд, атаковавший за этот ход ровно одну цель, потратил
    /// <b>не меньше половины</b> запаса: <c>запас − MOVE сохранения ≥ ceil(запас/2)</c>. Равенства
    /// требовать нельзя — чтобы ударить, отряд ещё и подходил, а цена пути между двумя снимками
    /// неизвестна (клетки пути в сохранениях не остаются). Ровно поэтому точное равенство ушло
    /// открытым пунктом §3 плана Ф7.</item>
    /// <item><c>attack_move_idle</c> — контроль: отряд, который за этот ход не атаковал и остался
    /// на месте, обязан прийти с ПОЛНЫМ запасом. Без него неравенство выше ничего не стоило бы:
    /// оно проверяет и то, что <see cref="Movement.StartMovement"/> даёт настоящее число дня, и
    /// что <c>MOVE</c> сохранения читается верно.</item>
    /// </list>
    /// </summary>
    public void AttacksOnMap(string path)
    {
        // ponytail: потолок сверки — неравенство, а не равенство. Цену пути между двумя
        // автосохранениями восстановить нечем (клетки маршрута в .sg не остаются, а move_left
        // журнала внутри одного приказа не меняется), и дня «атаковал, но не ходил» в корпусе нет ни
        // одного. Доработка — прогон с атакой вплотную (план Ф7 §3 п. 51): тогда здесь встанет
        // равенство Movement.AfterAttack(full, full).
        var j = TreasuryJournal.Load(path);
        var points = JournalSaves.Of(j);
        if (points.Count < 2) { c.Skip("attack_move_half", "у журнала меньше двух сохранений в манифесте"); return; }
        // Шаги отряда — строки stack_move (без t, поэтому не в TreasuryJournal): ИИ-отряд за ход уходит и
        // возвращается на ту же клетку (разбор 17.09: 10 окон, 14–18 шагов, последний move_left 0), и по паре
        // снимков «остался на месте» неотличимо от «не ходил».
        var walks = JournalCache.Read(path, "stack_move").OfType<StackMoveEvent>().Where(m => m.Ret).ToList();
        // Приказ ИИ «ждать» — CAiAttackStackAction (vftable 0x6d010c) с флагом +0x5c шлёт StackEmptyMoveAllowance
        // (0x41e507 → 0x42eb1d, changeStackMove 9999): запас 0 без шага (20260917-synth q09 S143KC00a7, три дня).
        // Зонд до 6.4 ap_restore не пишет — там такое окно не отличить.
        var restores = JournalCache.Read(path, "ap_restore").OfType<ApRestoreEvent>().ToList();
        foreach (var (a, b) in JournalSaves.Pairs(points))
        {
            // Окно обязано начинаться дневным тиком: полный запас у отряда бывает только в начале
            // ЕГО хода, а в окне (a.Cut, b.Cut) у каждого игрока ход ровно один. Пара снимков одного
            // дня (в корпусе такие есть — окна захвата столицы задачи 14) этого не даёт: остаток в
            // первом из них уже неизвестен.
            if (b.Save.Map.Turn != a.Save.Map.Turn + 1)
            { c.Skip("attack_move_half", "оба сохранения одного дня — полного запаса в начале окна не было"); continue; }
            var window = JournalSaves.Between(j, a.Cut, b.Cut);
            if (window.OfType<ThiefRollEvent>().Any())
            { c.Skip("attack_move_half", "в окне есть действие вора — оно платит тем же числом"); continue; }

            var attacks = window.OfType<BattleStartEvent>()
                .GroupBy(e => e.AttackerGroup, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => g.Select(e => e.DefenderGroup).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                              StringComparer.OrdinalIgnoreCase);
            var touched = window.OfType<BattleStartEvent>()
                .SelectMany(e => new[] { e.AttackerGroup, e.DefenderGroup })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var walked = walks.Where(m => m.Seq > a.Cut && m.Seq < b.Cut).Select(m => m.Stack)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var now in b.Save.Map.Stacks.Values)
            {
                int targets = attacks.GetValueOrDefault(now.Id);
                if (targets > 1) { c.Skip("attack_move_half", "за окно отряд атаковал больше одной цели — списаний несколько"); continue; }
                if (targets == 0 && touched.Contains(now.Id))
                { c.Skip("attack_move_idle", "отряд за окно оборонялся: «атакованный не тратит» — своё правило вики, здесь не мерим"); continue; }
                bool hold = restores.Any(r => r.Seq > a.Cut && r.Seq < b.Cut && r.Cause == "zero_9999"
                                              && JournalSaves.Same(r.Stack, now.Id));
                OneStack(a.Save, b.Save, now, attacked: targets == 1, walked.Contains(now.Id), window, hold,
                         restoresLogged: restores.Count > 0);
            }
        }
    }

    private void OneStack(SaveAt a, SaveAt b, MapStack now, bool attacked, bool walked, List<CorpusEvent> window,
                          bool aiHold, bool restoresLogged)
    {
        string name = attacked ? "attack_move_half" : "attack_move_idle";
        if (!a.Map.Stacks.TryGetValue(now.Id, out var was)) { c.Skip(name, "отряда нет в первом сохранении"); return; }
        bool moved = walked || was.X != now.X || was.Y != now.Y || !JournalSaves.Same(was.Inside, now.Inside);
        if (!attacked && moved) { c.Skip(name, "отряд за этот день ходил — цена пути неизвестна"); return; }
        if (!was.LeaderAlive || !now.LeaderAlive)
        { c.Skip(name, "лидер отряда мёртв — цена шага у него другая"); return; }
        if (!b.Map.Units.TryGetValue(now.LeaderId, out var leader)) { c.Skip(name, "лидера нет в сохранении"); return; }
        if (!attacked && book.LeaderCategoryOf(leader.TypeId) == RodLeaderCategory)
        { c.Skip(name, "лидер — жезлоносец (L_ROD): постановка и снос жезла съедают ход целиком (задача 8), " +
                       "а в паре сохранений само действие не видно"); return; }
        if (a.Map.Units.GetValueOrDefault(now.LeaderId) is { } before
            && (before.Level != leader.Level || !before.Modifiers.SequenceEqual(leader.Modifiers, StringComparer.OrdinalIgnoreCase)))
        { c.Skip(name, "за этот день у лидера сменились уровень или цепочка — запас дня неизвестен"); return; }
        if (book.Grown(leader.TypeId, leader.Level) is not { } grown)
        { c.Skip(name, $"тип лидера {leader.TypeId} не выращивается — MOVE неизвестен"); return; }

        var moveOfLeader = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            { [leader.TypeId] = grown.MoveAllowance };
        int full = Movement.StartMovement(now, b.Map, moveOfLeader, modifiers: book.Rules.Modifiers);
        // Конец мятежа в начале хода владельца обнуляет запас визитёра (Fortifications.EndRiot; ai-q11 S143KC0029:
        // ap_effect riot_turn_start 30 → 0).
        bool riotEnd = !attacked && RiotEndsFor(a, was, window);
        bool hold = !attacked && aiHold;
        if (!attacked && !restoresLogged && now.Move == 0 && full > 0)
        { c.Skip(name, "зонд < 6.4: обнуление запаса приказом ИИ (StackEmptyMoveAllowance) в журнал не пишется"); return; }
        if (riotEnd || hold) full = 0;
        int half = Movement.HalfAllowance(full);
        string detail = $"{Path.GetFileName(a.Path)} → {Path.GetFileName(b.Path)} {now.Id} " +
                        $"лидер {leader.TypeId} ур. {leader.Level} запас {full}{(riotEnd ? " (конец мятежа)" : "")}{(hold ? " (приказ ИИ ждать)" : "")}: " +
                        $"MOVE сохранения {now.Move}, потрачено {full - now.Move}" +
                        (attacked ? $", половина {half}{(moved ? ", отряд ещё и ходил" : "")}" : "");
        c.Count(name, attacked ? full - now.Move >= half : now.Move == full, detail);
        if (attacked) _spentOverHalf[full - now.Move - half] = _spentOverHalf.GetValueOrDefault(full - now.Move - half) + 1;
    }

    /// <summary>Отряд стоит в укреплении, чей мятеж кончается в окне: <see cref="Fortifications.RiotEndsAt"/> по
    /// <c>RIOT_T</c> снимка «до» и ходу строки <c>income</c> <b>владельца укрепления</b> в окне. Не <c>CUR_TURN</c>
    /// снимка: ИИ-владелец ходит после сохранения своего дня (ai-q11: сохранение хода 5 seq 10524894, затем
    /// <c>riot_turn_start</c> 10524903 и <c>income</c> S143PL0002 ход 5 — окно t05-169 → t06-170, а не t04 → t05).
    /// Тика владельца в окне нет — мятеж в нём не кончался.</summary>
    private static bool RiotEndsFor(SaveAt a, MapStack was, List<CorpusEvent> window) =>
        a.Sites.FirstOrDefault(s => s.Kind is SiteKind.Village or SiteKind.Capital
                                    && (JournalSaves.Same(s.Id, was.Inside) || JournalSaves.Same(s.StackId, was.Id)))
            is { RiotTurn: { } riot } fort
        && window.OfType<IncomeEvent>().FirstOrDefault(i => JournalSaves.Same(i.Player, fort.Owner) && i.Turn is not null)
            is { Turn: { } turn }
        && Fortifications.RiotEndsAt(riot, (int)turn);

    /// <summary>Насколько атаковавший потратил БОЛЬШЕ половины запаса: это цена подхода, которую
    /// сверка восстановить не может (см. <c>ponytail:</c> в <see cref="AttacksOnMap"/>). Ноль здесь
    /// значил бы равенство — то самое, что ушло открытым пунктом §3 п. 51.</summary>
    public void PrintSpentOverHalf()
    {
        if (_spentOverHalf.Count == 0) return;
        c.Note(" у атаковавших потрачено сверх половины: " +
               string.Join(", ", _spentOverHalf.Select(p => $"{p.Key} — {p.Value}")));
    }

    private readonly SortedDictionary<int, int> _spentOverHalf = [];

    /// <summary><c>Gunits.LEADER_CAT</c> = 4 <c>L_ROD</c> (<c>LleadC.dbf</c>): жезлоносец. Его ход
    /// съедает постановка или снос жезла целиком (<see cref="Movement.WholeTurnSpent"/>, задача 8),
    /// а в паре сохранений само действие не видно — все 24 расхождения первой редакции
    /// <c>attack_move_idle</c> были ровно такими (MOVE 0 у стоящего на месте жезлоносца).</summary>
    private const int RodLeaderCategory = 4;

    // ---- Н24: уровень наёмника -----------------------------------------------------------------

    /// <summary>
    /// Ростеры наёмников всех сохранений корпуса: цена предложения базового уровня обязана быть
    /// ровно <c>ENROLL_C</c> (это и подтверждает единственный наём корпуса), а предложения выше
    /// базового — строго дороже. Сколько в корпусе тех и других, печатается строкой: наёма выше
    /// базового уровня в корпусе нет, и это открытый пункт §3 плана Ф7.
    /// </summary>
    public void MercenaryOffers()
    {
        int atBase = 0, above = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CorpusSaves())
        {
            IReadOnlyList<MapSite> sites;
            try { sites = SiteLoader.Load(path); }
            catch (Exception e) { c.Skip("merc_price_level", $"сохранение не читается: {e.GetType().Name}"); continue; }
            foreach (var offer in sites.Where(s => s.Kind == SiteKind.Mercenaries).SelectMany(s => s.Roster))
            {
                if (!seen.Add($"{offer.UnitType}:{offer.Level}")) continue;
                if (!book.Prices.Economy.Hire.TryGetValue(offer.UnitType, out var hire))
                { c.Skip("merc_price_level", "тип из ростера не найден в Gunits"); continue; }
                if (book.Prices.HirePrice(offer.UnitType, offer.Level) is not { } price)
                { c.Skip("merc_price_level", "тип из ростера не выращивается"); continue; }
                bool atBaseLevel = offer.Level <= hire.Level;
                if (atBaseLevel) atBase++; else above++;
                bool ok = atBaseLevel ? price == hire.Cost : price.Gold >= hire.Cost.Gold;
                c.Count("merc_price_level",
                        ok,
                        $"{Path.GetFileName(path)} {offer.UnitType} ур. {offer.Level} (базовый {hire.Level}): " +
                        $"ENROLL_C {hire.Cost.Gold}, модель {price.Gold}");
                if (!atBaseLevel)
                    c.Skip("merc_price_level", "предложение выше базового уровня: цена в корпусе не наблюдалась");
            }
        }
        c.Note($" предложений наёмника в корпусе: базового уровня {atBase}, выше базового {above}");
    }

    // ---- общее ---------------------------------------------------------------------------------

    /// <summary>Все сохранения корпуса, кроме выведенных из оборота каталогов на «_».</summary>
    private static IEnumerable<string> CorpusSaves() =>
        Directory.EnumerateDirectories(CorpusFiles.Dir)
            .Where(d => !Path.GetFileName(d).StartsWith('_'))
            .OrderBy(d => d, StringComparer.Ordinal)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.sg").OrderBy(f => f, StringComparer.Ordinal));

    private static Key KeyOf(MoveCostEvent e) =>
        new(e.X, e.Y, e.Plains, e.Forest, e.Water, e.WaterOnly, e.ForbidWater);

    /// <summary>Клетка и флаги отряда — ключ события <c>move_cost</c> без цены и без признака
    /// живого лидера: именно по нему ищется пара «живой — павший».</summary>
    private readonly record struct Key(long X, long Y, bool Plains, bool Forest, bool Water,
                                       bool WaterOnly, bool ForbidWater)
    {
        public MoveOptions Options(bool leaderAlive) =>
            new(leaderAlive, Plains, Forest, Water, WaterOnly, ForbidWater);

        public string Flags() =>
            $"plains={(Plains ? 1 : 0)} forest={(Forest ? 1 : 0)} water={(Water ? 1 : 0)} " +
            $"only={(WaterOnly ? 1 : 0)} forbid={(ForbidWater ? 1 : 0)}";
    }

    private static MapState ProbeMap()
    {
        var grounds = new[] { Ground.Plain, Ground.Forest, Ground.Water, Ground.Plain, Ground.Mountain };
        int size = grounds.Length;
        var tiles = new Tile[size, size];
        var objects = new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tiles[x, y] = new Tile((byte)(((int)(y == 0 ? grounds[x] : Ground.Plain) << 3) | 1));
        objects["S001RA0300"] = new MapObject { Id = "S001RA0300", Kind = "RA", X = 3, Y = 0 };
        return new MapState
        {
            Size = size, Tiles = tiles, Objects = objects,
            Stacks = new Dictionary<string, MapStack>(), Units = new Dictionary<string, MapUnit>(),
            Players = new Dictionary<string, MapPlayer>(),
            Plan = new Dictionary<(int X, int Y), IReadOnlyList<string>>(),
            Fog = new Dictionary<string, bool[,]>(), Turn = 1,
        };
    }
}
