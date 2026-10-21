using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui.Windows;
using D2.Rules.Magic;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// П. 29 <c>docs/port-status-20261016.md</c> — действия человека (и те же проверки у ИИ и сервера) против журналов зонда оригинала:
/// каждая строка <c>*_check</c> корпуса (по ключу — разные входы с ответом), перенос юнитов рукой человека (<c>unit_transfer</c>
/// <c>swap_msg</c>) и ответы ИИ на предложения (<c>ai_answer</c>). Состояние — сохранение корпуса до строки (той же загрузки карты);
/// казна игрока — из последней строки <c>bank</c> до проверки (она точнее сохранения). Действие зовётся через <see cref="GameSession"/>
/// (найм, рост, жезл, посох, продажа, наёмник, обмен — сами действия порта на копии партии; надевание и отпуск — их проверки), ответ
/// переводится в код движка (<see cref="HumanCheckCodes"/>) и сверяется с <c>ret</c> журнала.
/// <para><b>Сдвиг состояния.</b> Между сохранением и проверкой человек и ИИ успевают сделать своё (вырос уровень лидера, куплен
/// предмет, сдвинулся туман). Поэтому та же проверка считается и по следующему сохранению той же загрузки: ответ порта по сохранению
/// «до» совпал с движком — <c>совпало</c>; не совпал, а по сохранению «после» совпал — <c>совпало после</c> (состояние сменилось между
/// ними); не совпал ни там, ни там — расхождение. Объектов проверки нет ни в одном сохранении — строка не считается
/// (<c>нет состояния</c>).</para>
/// </summary>
public partial class HumanChecksHarnessTests(ITestOutputHelper output)
{
    /// <summary>Партии корпуса со строками проверок схемы 6.x и сохранениями.</summary>
    private static readonly string[] Runs =
    [
        "20260918-probe65", "20260919-f7", "20260919-f7b", "20260919-human", "20260920-gaps", "20260920-legions", "20260920-ui",
        "20260921-ui", "20260922-ui", "20260928-shots", "20260929-hotseat", "20260930-probe72", "20261014-replay",
        "20261016-port-synth", "20261016b-port-synth", "20261016c-port-synth", "20261016d-port-synth", "20261016e-av",
        "20261016g-port-synth", "20261017-port-synth", "20261019c-cov", "20261020-port-synth",
    ];

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(MapArtData Art, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data).MapArt, GameBook.Load(Path.Combine(Data, "rules"))));

    /// <summary>Итог одной строки журнала.</summary>
    internal sealed record Outcome(string Kind, string Run, long Seq, string RetAddr, long Engine, long? Before, long? After, string Detail)
    {
        public string Verdict => Before is { } b && b == Engine ? Matched
            : After is { } a && a == Engine ? MatchedAfter
            : Before is null && After is null ? Unknown
            : Differs;
    }

    internal const string Matched = "совпало", MatchedAfter = "совпало после", Unknown = "нет состояния", Differs = "расхождение";

    /// <summary>Все строки корпуса — один прогон на процесс тестов (их читают восемь харнессов ниже); партии синтетических карт —
    /// отдельно (<see cref="CorpusSets.IsSynth"/>, группа <c>Synth</c>): быстрый прогон без них их и не разбирает.</summary>
    private static readonly Lazy<IReadOnlyList<Outcome>> All = new(() => Evaluate(synth: false));

    private static readonly Lazy<IReadOnlyList<Outcome>> AllSynth = new(() => Evaluate(synth: true));

    private static IReadOnlyList<Outcome> Evaluate(bool synth)
    {
        var journals = Runs.Where(r => CorpusSets.IsSynth(r) == synth).Select(r => Path.Combine(GameSaveTests.CorpusDir(), r)).Where(Directory.Exists)
                           .SelectMany(d => Directory.GetFiles(d, "passive-*.jsonl").Order(StringComparer.Ordinal)).ToList();
        var results = new System.Collections.Concurrent.ConcurrentBag<Outcome>();
        Parallel.ForEach(journals, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, path =>
        {
            foreach (var o in EvaluateJournal(path)) results.Add(o);
        });
        return [.. results.OrderBy(o => o.Run, StringComparer.Ordinal).ThenBy(o => o.Seq)];
    }

    private static IEnumerable<Outcome> EvaluateJournal(string path)
    {
        var j = CheckJournal.Load(path);
        string run = $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileNameWithoutExtension(path)}";
        var looks = new Dictionary<string, MapLooks>(StringComparer.OrdinalIgnoreCase);
        var clusters = SwapClusters(j);
        for (int i = 0; i < j.Events.Count; i++)
        {
            var e = j.Events[i];
            if (Kind(e) is not { } kind) continue;
            var before = j.Before(i);
            var after = j.After(i);
            foreach (string stale in looks.Keys.Where(k => k != before?.Path && k != after?.Path).ToList()) looks.Remove(stale);
            long? b = null, a = null;
            string detail = Describe(e);
            try
            {
                b = before is null ? null : Port(Session(Looks(looks, before.Path)), new Context(j, e, clusters.GetValueOrDefault(e.Seq), before));
                a = after is null ? null : Port(Session(Looks(looks, after.Path)), new Context(j, e, clusters.GetValueOrDefault(e.Seq), after));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Сбой одной строки — её расхождение с причиной, а не падение всего прогона (Lazy запомнил бы исключение для всех харнессов).
                (b, a) = (-2, -2);
                detail += $" — исключение {ex.GetType().Name}: {ex.Message}";
            }
            yield return new Outcome(kind, run, e.Seq, RetAddrOf(e), EngineCode(e), b, a, detail);
        }
    }

    private static MapLooks Looks(Dictionary<string, MapLooks> cache, string path)
    {
        if (!cache.TryGetValue(path, out var l)) cache[path] = l = MapLooksLoader.Load(path);
        return l;
    }

    private static GameSession Session(MapLooks looks) =>
        new(new GameState(GameInfo.FromMap("x", looks), looks), Shared.Value.Art, Shared.Value.Book);

    /// <summary>Та же партия с другой картой (подставленные казна, клетки группы, тип предмета).</summary>
    private static GameSession Rebuilt(GameSession s, MapState map) =>
        new(s.State with { Looks = GameSession.Retold(s.Looks, map, s.Looks.Stacks) }, s.Art, s.Book);

    /// <summary>Строка журнала, сохранение, по которому она считается, и то, что харнесс из журнала берёт в состояние: казна, клетки групп
    /// у начала перетаскивания, действия между сохранением и строкой.</summary>
    private sealed record Context(CheckJournal Journal, CorpusEvent Event, SwapCluster? Cluster, SaveCopy Save)
    {
        /// <summary>Сохранение снято после строки: в нём уже есть итог действия, о котором спрашивала проверка.</summary>
        public bool AfterCheck => Save.Seq > Event.Seq;

        /// <summary>Строки журнала между сохранением «до» и проверкой (у сохранения «после» — пусто).</summary>
        public IEnumerable<CorpusEvent> Since => AfterCheck ? [] : Journal.Between(Save.Seq, Event.Seq);
    }

    private static string? Kind(CorpusEvent e) => e switch
    {
        EquipCheckEvent => "equip_check",
        SwapCheckEvent => "swap_check",
        DismissCheckEvent => "dismiss_check",
        HireCheckEvent => "hire_check",
        HireLeaderCheckEvent => "hire_leader_check",
        GrowCheckEvent => "grow_check",
        RodCheckEvent => "rod_check",
        WandCheckEvent => "wand_check",
        MercCheckEvent => "merc_check",
        SellCheckEvent => "sell_check",
        UnitTransferEvent { Cause: "swap_msg" } => "unit_transfer",
        AiAnswerEvent { Ret: not null } => "ai_answer",
        _ => null,
    };

    private static long EngineCode(CorpusEvent e) => e switch
    {
        EquipCheckEvent q => q.Ret,
        SwapCheckEvent q => q.Ret,
        DismissCheckEvent q => q.Ret,
        HireCheckEvent q => q.Ret,
        HireLeaderCheckEvent q => q.Ret,
        GrowCheckEvent q => q.Ret,
        RodCheckEvent q => q.Ret,
        WandCheckEvent q => q.Ret,
        MercCheckEvent q => q.Ret ? 0 : 1,
        SellCheckEvent q => q.Ret ? 0 : 1,
        UnitTransferEvent q => HumanCheckCodes.Transfer(q.Ret),
        AiAnswerEvent q => HumanCheckCodes.Answer(q.Ret!.Value, q.Accept),
        _ => -1,
    };

    private static string RetAddrOf(CorpusEvent e) => e.GetType().GetProperty("RetAddr")?.GetValue(e) as string ?? "";

    private static string Describe(CorpusEvent e) => e switch
    {
        EquipCheckEvent q => $"{q.Stack} {q.Item} ({q.ItemType}) слот {q.Slot}{(q.CheckEffect ? "" : " без эффекта")}",
        SwapCheckEvent q => $"{q.From}:{q.FromPos} ({q.Unit}) → {q.To}:{q.ToPos} ({q.Other})",
        DismissCheckEvent q => $"{q.Group}:{q.Position} ({q.Unit})",
        HireCheckEvent q => $"{q.Player} {q.UnitType} → {q.Group}:{q.Position}",
        HireLeaderCheckEvent q => $"{q.Player} {q.UnitType}",
        GrowCheckEvent q => $"{q.Player} {q.Fort} день {q.Turn}",
        RodCheckEvent q => $"{q.Stack} ({q.X},{q.Y}){(q.Flag ? " flag" : "")}",
        WandCheckEvent q => $"{q.Target} {q.Item} ({q.X},{q.Y})",
        MercCheckEvent q => $"{q.Stack} {q.Site} {q.UnitType}",
        SellCheckEvent q => $"{q.Stack} {q.Site} {q.Item}",
        UnitTransferEvent q => $"{q.From}:{q.FromPos} ({q.Unit}) → {q.To}:{q.ToPos} ({q.Other}) = {q.FromAfter}/{q.ToAfter}",
        AiAnswerEvent q => $"{q.Kind} {q.Proposer} → {q.Player} rel {q.Rel} золото {q.Gold} цена {q.Price} {q.Object} бросок {q.Roll}",
        _ => "",
    };

    /// <summary>Ответ порта кодом движка; null — объектов проверки в этом сохранении нет.</summary>
    private static long? Port(GameSession s, Context c) => c.Event switch
    {
        EquipCheckEvent q => Equip(s, c, q),
        SwapCheckEvent q => Swap(s, c, q),
        DismissCheckEvent q => s.GroupCells(q.Group) is null ? null : s.DismissCheck(q.Group, q.Unit ?? ""),
        HireCheckEvent q => Hire(s, c, q),
        HireLeaderCheckEvent q => HireLeader(s, c, q),
        GrowCheckEvent q => Grow(s, c, q),
        RodCheckEvent q => Rod(s, c, q),
        WandCheckEvent q => Wand(s, c, q),
        MercCheckEvent q => Merc(s, c, q),
        SellCheckEvent q => Sell(s, c, q),
        UnitTransferEvent q => Transfer(s, c, q),
        AiAnswerEvent q => Answer(s, c, q),
        _ => null,
    };

    /// <summary>Казна игрока из журнала на момент строки.</summary>
    private static GameSession WithBank(GameSession s, Context c, string player)
    {
        if (!s.Map.Players.TryGetValue(player, out var p) || c.Journal.BankAt(player, c.Event.Seq) is not { } bank || bank == p.Bank) return s;
        var players = s.Map.Players.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        players[p.Id] = p with { Bank = bank };
        return Rebuilt(s, GameSession.With(s.Map, players: players));
    }

    private static long? Equip(GameSession s, Context c, EquipCheckEvent q)
    {
        if (!s.Map.Stacks.ContainsKey(q.Stack) || q.Slot is < 0 or >= Equipment.SlotCount) return null;
        if (!s.Map.ItemTypes.ContainsKey(q.Item))
        {
            // Предмет пришёл после сохранения — тип из строки журнала (экземпляр в проверке важен только типом).
            if (q.ItemType is not { Length: > 0 } type) return null;
            var types = s.Map.ItemTypes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            types[q.Item] = type;
            s = Rebuilt(s, GameSession.With(s.Map, itemTypes: types));
        }
        return (long)s.EquipCheck(q.Stack, q.Item, (int)q.Slot, q.CheckEffect);
    }

    private static long? Hire(GameSession s, Context c, HireCheckEvent q)
    {
        if (!s.Map.Sites.Any(x => Same(x.Id, q.Group)) || !s.Map.Players.ContainsKey(q.Player)) return null;
        s = WithBank(Replayed(Arranged(s, c, q.Player, q.Group), c, q.Group), c, q.Player);
        int? cell = q.Position is >= 0 and < 6 ? (int)q.Position : null;
        return HumanCheckCodes.Hire(s.HireUnit(q.Group, q.UnitType, cell));
    }

    /// <summary>Найм лидера в столице игрока; гость столицы снимается — проверка <c>0x5de921</c> на гостя не смотрит.</summary>
    private static long? HireLeader(GameSession s, Context c, HireLeaderCheckEvent q)
    {
        if (s.Map.Sites.FirstOrDefault(x => x.Kind == SiteKind.Capital && Same(x.Owner, q.Player)) is not { } capital) return null;
        s = WithBank(s, c, q.Player);
        if (capital.StackId.Length > 0)
            s = Rebuilt(s, GameSession.With(s.Map, sites: [.. s.Map.Sites.Select(x => Same(x.Id, capital.Id) ? x with { StackId = "" } : x)]));
        return HumanCheckCodes.HireLeader(s.HireLeader(capital.Id, q.UnitType));
    }

    private static long? Grow(GameSession s, Context c, GrowCheckEvent q)
    {
        if (s.Map.Sites.FirstOrDefault(x => Same(x.Id, q.Fort)) is not { Kind: SiteKind.Village } village || !Same(village.Owner, q.Player)) return null;
        // Сохранение «после» с ростом, внутри которого идёт эта проверка (строка city_grow начата раньше неё, как у жезла): деревня в нём уже выросла, казна
        // заплачена — вопроса, на который отвечал движок, в нём нет (20261016e-av seq 1777275: первый день карты, сохранения «до» нет).
        if (c.AfterCheck && c.Journal.Between(q.Seq - EnclosingWindow, c.Save.Seq).OfType<CityGrowEvent>().Any(g => g.Ret && g.Seq < q.Seq && Same(g.Fort, q.Fort)))
            return null;
        s = WithBank(Replayed(s, c, q.Fort), c, q.Player);
        if (s.Map.Turn != q.Turn) s = Rebuilt(s, GameSession.With(s.Map, turn: (int)q.Turn));
        // Проверка идёт после начала хода хозяина: мятеж, дошедший до RIOT_T, к ней уже снят (Fortifications.EndRiot), а сохранение дня
        // снято до хода ИИ (replay seq 1753860: S143FT0008 у движка 0, по сохранению — мятеж).
        s = Rebuilt(s, GameSession.With(s.Map, sites: [.. s.Map.Sites.Select(x => Same(x.Id, q.Fort) ? Fortifications.EndRiot(x, s.Map.Turn) : x)]));
        return (long)s.GrowCity(q.Fort);
    }

    private static long? Rod(GameSession s, Context c, RodCheckEvent q)
    {
        if (!s.Map.Stacks.TryGetValue(q.Stack, out var stack)) return null;
        s = Moved(WithBank(s, c, stack.Owner), c);
        var rods = s.Map.Rods.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var plan = s.Map.Plan.ToDictionary(x => x.Key, x => x.Value);
        // Жезлы, поставленные между сохранением «до» и проверкой, — в карту (жезл виден проверке через план клетки); в сохранении «после» —
        // без жезлов, поставленных после проверки (legions seq 6761667: KC008d ставит жезл в (69,54), он и есть «жезл рядом»).
        foreach (var drop in c.Since.OfType<RodDropEvent>().Where(d => d.SeqEnd < q.Seq))
        {
            string id = $"S000RD{drop.Seq:x}";
            rods[id] = new MapRod(id, drop.Player, (int)drop.X, (int)drop.Y);
            plan[((int)drop.X, (int)drop.Y)] = [.. plan.GetValueOrDefault(((int)drop.X, (int)drop.Y)) ?? [], id];
        }
        if (c.AfterCheck)
        {
            // Постановка, внутри которой идёт проверка (CEffectDropRod::apply 0x43765c, flag), начата строкой раньше проверки.
            var placed = c.Journal.Between(q.Seq - EnclosingWindow, c.Save.Seq).OfType<RodDropEvent>().Where(d => d.SeqEnd > q.Seq)
                          .Select(d => ((int)d.X, (int)d.Y)).ToHashSet();
            foreach (var r in rods.Values.Where(r => placed.Contains((r.X, r.Y))).ToList())
            {
                rods.Remove(r.Id);
                if (plan.TryGetValue((r.X, r.Y), out var here)) plan[(r.X, r.Y)] = [.. here.Where(o => !Same(o, r.Id))];
            }
        }
        s = Rebuilt(s, GameSession.WithRods(GameSession.With(s.Map, plan: plan), rods));
        return HumanCheckCodes.Rod(s.PlaceRod(q.Stack, (int)q.X, (int)q.Y));
    }

    /// <summary>Сколько строк до проверки может начаться действие, внутри которого она идёт (постановка жезла — на строку раньше).</summary>
    private const long EnclosingWindow = 64;

    private static long? Wand(GameSession s, Context c, WandCheckEvent q)
    {
        if (!s.Map.Stacks.TryGetValue(q.Target, out var stack) || !s.Map.ItemTypes.ContainsKey(q.Item)) return null;
        s = Moved(WithBank(s, c, stack.Owner), c);
        stack = s.Map.Stacks[q.Target];
        if (!stack.Items.Contains(q.Item, StringComparer.OrdinalIgnoreCase))
        {
            // Посох или свиток пришёл в отряд после сохранения (human seq 8450530: S073IM0069 у KC0001) — проверка о нём, он у отряда.
            var stacks = s.Map.Stacks.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            stacks[stack.Id] = stack with { Items = [.. stack.Items, q.Item] };
            s = Rebuilt(s, GameSession.With(s.Map, stacks: stacks));
        }
        return HumanCheckCodes.Spell(s.ItemCastRefusal(q.Target, q.Item, (int)q.X, (int)q.Y));
    }

    private static long? Merc(GameSession s, Context c, MercCheckEvent q)
    {
        if (!s.Map.Stacks.TryGetValue(q.Stack, out var stack) || !s.Map.Sites.Any(x => Same(x.Id, q.Site))) return null;
        s = WithBank(s, c, stack.Owner);
        return s.HireMercenary(q.Stack, q.Site, q.UnitType) is null ? 0 : 1;
    }

    private static long? Sell(GameSession s, Context c, SellCheckEvent q)
    {
        if (!s.Map.Stacks.TryGetValue(q.Stack, out var stack) || !s.Map.Sites.Any(x => Same(x.Id, q.Site)) || !s.Map.ItemTypes.ContainsKey(q.Item))
            return null;
        // 20261016e-av: 9 строк продажи дня 1 greenskins — до первого сохранения карты, в сохранении «после» предмет уже продан: нет состояния.
        if (!stack.Items.Contains(q.Item, StringComparer.OrdinalIgnoreCase)) return null;
        s = WithBank(s, c, stack.Owner);
        return s.SellItem(q.Stack, q.Site, q.Item) is null ? 0 : 1;
    }

    /// <summary>Действия с группами <paramref name="groups"/> между сохранением «до» и проверкой — по строкам журнала: рост деревни
    /// (<c>city_grow</c>), найм в неё (<c>hire_unit</c>) и перенос юнитов (<c>unit_transfer</c>: клетки после вызова). ИИ в одном ходу
    /// расставляет гарнизон, растит деревню и нанимает, а сохранение дня снято до его хода (probe65 seq 4240734: FT0009 уровня 2 с двумя
    /// юнитами в клетках 0 и 1 растёт, переставляет их в 2 и 3 и нанимает в 0).</summary>
    private static GameSession Replayed(GameSession s, Context c, params string[] groups)
    {
        foreach (var e in c.Since)
        {
            if (e is CityGrowEvent g && groups.Contains(g.Fort, StringComparer.OrdinalIgnoreCase)
                && s.Map.Sites.FirstOrDefault(x => Same(x.Id, g.Fort)) is { Size: not null } v)
                s = Rebuilt(s, GameSession.With(s.Map, sites: [.. s.Map.Sites.Select(x => ReferenceEquals(x, v) ? Fortifications.Grow(x, s.Map.Turn) : x)]));
            else if (e is HireUnitEvent h && groups.Contains(h.Group, StringComparer.OrdinalIgnoreCase))
                s.HireUnit(h.Group, h.UnitType, h.Position is >= 0 and < 6 ? (int)h.Position : null);
            else if (e is UnitTransferEvent { Ret: true } t && t.To is { } to
                     && (groups.Contains(t.From, StringComparer.OrdinalIgnoreCase) || groups.Contains(to, StringComparer.OrdinalIgnoreCase)))
            {
                Put(s, t.From, t.FromPos, t.FromAfter);
                Put(s, to, t.ToPos, t.ToAfter);
            }
        }
        return s;
    }

    /// <summary>Расстановка гарнизона в начале стратегии ИИ (<c>0x44edee</c> → <c>0x5d57de</c>, <see cref="AiStackUnits.Arrange"/>) —
    /// строк перестановки внутри группы зонд не пишет, а сохранение дня снято до хода ИИ (<c>20261016b-port-synth</c>, карта 2, день 3: у
    /// всех трёх ИИ найм в клетку 0, где по сохранению стоит вчерашний найм; в сохранении дня 4 он уже в клетке 2). Только для игрока ИИ,
    /// укрепления без гостя и сохранения «до».</summary>
    private static GameSession Arranged(GameSession s, Context c, string player, string fort)
    {
        if (c.AfterCheck || !s.Map.Players.TryGetValue(player, out var p) || p.IsHuman
            || s.Map.Sites.FirstOrDefault(x => Same(x.Id, fort)) is not { StackId.Length: 0 }) return s;
        AiStackUnits.Arrange(s, fort, center: true);
        return s;
    }

    /// <summary>Клетки отрядов на миг проверки — последний <c>stack_move</c> каждого отряда между сохранением «до» и проверкой (клетка и
    /// план; туман не трогается). human seq 2910130: жезлоносец KC0075 встал в (70,24) после сохранения — своя клетка занята отрядом (код 7).</summary>
    private static GameSession Moved(GameSession s, Context c)
    {
        var last = new Dictionary<string, StackMoveEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in c.Since.OfType<StackMoveEvent>()) last[m.Stack] = m;
        if (last.Count == 0) return s;
        var stacks = s.Map.Stacks.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var plan = s.Map.Plan.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (id, m) in last)
        {
            if (!stacks.TryGetValue(id, out var st) || (st.X == m.X && st.Y == m.Y) || !s.Map.Contains((int)m.X, (int)m.Y)) continue;
            if (plan.TryGetValue((st.X, st.Y), out var from)) plan[(st.X, st.Y)] = [.. from.Where(o => !Same(o, id))];
            plan[((int)m.X, (int)m.Y)] = [.. plan.GetValueOrDefault(((int)m.X, (int)m.Y)) ?? [], id];
            stacks[id] = st with { X = (int)m.X, Y = (int)m.Y, Inside = "" };
        }
        return Rebuilt(s, GameSession.With(s.Map, stacks: stacks, plan: plan));
    }

    /// <summary>Клетка группы — юнит из журнала (null — пусто); юнит, которого в сохранении нет, не ставится.</summary>
    private static void Put(GameSession s, string group, long pos, string? unit)
    {
        if (s.GroupCells(group) is not { } cells || pos is < 0 or >= 6 || (unit is not null && !s.Map.Units.ContainsKey(unit))) return;
        string?[] occupants = [.. cells.Cells.Select(x => x.Length > 0 ? x : null)];
        occupants[pos] = unit;
        s.SetCells(group, CellsOf(cells, occupants));
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // --- итоги ---

    private (int Same, int SameAfter, int Differ, int NoState) Report(string kind, bool synth = false)
    {
        var rows = (synth ? AllSynth : All).Value.Where(o => o.Kind == kind).ToList();
        int same = rows.Count(o => o.Verdict == Matched), after = rows.Count(o => o.Verdict == MatchedAfter);
        int differ = rows.Count(o => o.Verdict == Differs), none = rows.Count(o => o.Verdict == Unknown);
        output.WriteLine($"{kind}: строк {rows.Count}, совпало {same}, совпало после {after}, расхождений {differ}, нет состояния {none}");
        foreach (var g in rows.GroupBy(o => o.Run.Split('/')[0]).OrderBy(g => g.Key, StringComparer.Ordinal))
            output.WriteLine($"  партия {g.Key}: строк {g.Count()}, совпало {g.Count(o => o.Verdict == Matched)}, после {g.Count(o => o.Verdict == MatchedAfter)}, "
                             + $"расхождений {g.Count(o => o.Verdict == Differs)}, нет состояния {g.Count(o => o.Verdict == Unknown)}");
        foreach (var g in rows.GroupBy(o => (o.RetAddr, o.Engine)).OrderBy(g => g.Key.RetAddr, StringComparer.Ordinal).ThenBy(g => g.Key.Engine))
            output.WriteLine($"  {g.Key.RetAddr} код {g.Key.Engine:x}: {g.Count()} — {string.Join(", ", g.GroupBy(o => o.Verdict).Select(v => $"{v.Key} {v.Count()}"))}");
        foreach (var o in rows.Where(o => o.Verdict == Differs).Take(int.TryParse(Environment.GetEnvironmentVariable("D2_CHECKS_SHOW"), out int n) ? n : 25))
            output.WriteLine($"  РАСХ {o.Run} seq {o.Seq} {o.RetAddr}: движок {o.Engine:x}, порт до {Hex(o.Before)}, после {Hex(o.After)} — {o.Detail}");
        return (same, after, differ, none);
    }

    private static string Hex(long? v) => v is { } x ? x.ToString("x", System.Globalization.CultureInfo.InvariantCulture) : "—";
}
