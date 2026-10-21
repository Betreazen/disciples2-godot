using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 11 против сохранений оригинала (партия 17.10, <c>20261017-port-synth</c>): каждое литьё книгой, свитком и посохом
/// (<c>spell_cast</c>/<c>wand_cast</c> с <c>ret</c>) повторяется портом на сохранении движка «до» и сверяется с сохранением «после»
/// числом — земля, туман каждого игрока, запас хода, здоровье, модификаторы, невидимость, мана.</summary>
public partial class PortSynthMapsTests
{
    private const string Magic2Run = "20261017-port-synth";

    /// <summary>Окон литий между сохранениями одного дня в партии 17.10 (ход, Сокрытие, по три земли, тумана и развеивания, LW1–LW5, два
    /// «Ветра», «Свет дня»).</summary>
    private const int Magic2CastWindows = 20;

    /// <summary>Сохранения <c>port-synth-11-magic2-*</c> журнала 17.10 и литья между ними: группа — литья между двумя соседними
    /// сохранениями (свиток и посох «Быстроты» — оба до <c>t01-005</c>). Литьё на границе дня (до него — ход ИИ: «Сумерки» дня 2,
    /// <c>t01-024</c> → <c>t02-025</c>) не сверяется — сохранения «до» на том же дне нет.</summary>
    private static List<(SaveCopy Before, SaveCopy After, List<SpendEvent> Casts)> Magic2CastPairs()
    {
        var (journal, saves) = Magic2Saves();
        var casts = JournalCache.Read(journal, ["spell_cast", "wand_cast"]).OfType<SpendEvent>()
                                .Where(c => c is SpellCastEvent or WandCastEvent && c.Ret && c.Seq > saves[0].Seq && c.Seq < saves[^1].Seq).ToList();
        var pairs = new List<(SaveCopy, SaveCopy, List<SpendEvent>)>();
        for (int i = 0; i + 1 < saves.Count; i++)
        {
            var inside = casts.Where(c => c.Seq > saves[i].Seq && c.Seq < saves[i + 1].Seq).ToList();
            if (inside.Count > 0 && saves[i].Turn == saves[i + 1].Turn) pairs.Add((saves[i], saves[i + 1], inside));
        }
        return pairs;
    }

    /// <summary>Журнал 17.10 и сохранения карты 11 на его оси (<see cref="CheckJournal"/>, по <c>seq</c>).</summary>
    private static (string Journal, List<SaveCopy> Saves) Magic2Saves()
    {
        string journal = Path.Combine(GameSaveTests.CorpusDir(), Magic2Run, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), $"нет {Magic2Run}");
        var saves = CheckJournal.Load(journal).Saves
                                .Where(s => Path.GetFileName(s.Path).StartsWith(Magic2Map + "-", StringComparison.OrdinalIgnoreCase)).ToList();
        Skip.If(saves.Count < 2, "нет сохранений карты 11");
        return (journal, saves);
    }

    private static GameSession SessionOf(MapLooks looks) =>
        new(new GameState(GameInfo.FromMap("x", looks), looks), Shared.Value.Content.MapArt, Shared.Value.Book);

    /// <summary>Литья журнала на сохранении «до» — по <see cref="GameSession.CastSpell"/>/<see cref="GameSession.CastItem"/>; отказ порта —
    /// строкой.</summary>
    private static List<string> Replay(GameSession s, IEnumerable<SpendEvent> casts)
    {
        var refused = new List<string>();
        foreach (var c in casts)
        {
            string? why = c switch
            {
                SpellCastEvent sc => s.CastSpell(sc.Player, sc.Spell, (int)sc.X, (int)sc.Y, sc.UseSpellBank),
                WandCastEvent wc => s.CastItem(wc.Owner, wc.Item, (int)wc.X, (int)wc.Y),
                _ => "?",
            };
            if (why is not null) refused.Add($"{Describe(c)}: {why}");
        }
        return refused;
    }

    private static string Describe(SpendEvent c) => c switch
    {
        SpellCastEvent sc => $"{sc.Spell} ({sc.X},{sc.Y})",
        WandCastEvent wc => $"{wc.Item} ({wc.X},{wc.Y})",
        _ => c.GetType().Name,
    };

    /// <summary>Чем карта порта после литья расходится с картой движка после него: строка на признак, пусто — совпало. У каждого
    /// признака — сколько изменилось у движка (от «до») и сколько клеток или объектов расходятся.</summary>
    private static List<string> Differences(MapLooks beforeLooks, MapLooks engineLooks, GameSession port)
    {
        var before = beforeLooks.Map;
        var engine = engineLooks.Map;
        var map = port.Map;
        var rows = new List<string>();
        void Row(string what, int changed, int differ, string sample)
        {
            if (differ > 0) rows.Add($"{what}: у движка изменилось {changed}, расходится {differ} {sample}");
        }
        var cells = (from x in Enumerable.Range(0, map.Size) from y in Enumerable.Range(0, map.Size) select (x, y)).ToList();
        var land = cells.Where(c => engine.Tiles[c.x, c.y].Terrain != map.Tiles[c.x, c.y].Terrain).ToList();
        Row("земля", cells.Count(c => engine.Tiles[c.x, c.y].Terrain != before.Tiles[c.x, c.y].Terrain), land.Count,
            string.Join(" ", land.Take(6).Select(c => $"({c.x},{c.y}) {engine.Tiles[c.x, c.y].Terrain}/{map.Tiles[c.x, c.y].Terrain}")));
        foreach (var p in engine.Players.Values)
        {
            if (!engine.Fog.TryGetValue(p.FogId, out var ef) || !map.Fog.TryGetValue(p.FogId, out var pf) || !before.Fog.TryGetValue(p.FogId, out var bf))
                continue;
            var fog = cells.Where(c => ef[c.x, c.y] != pf[c.x, c.y]).ToList();
            Row($"туман {p.Id}", cells.Count(c => ef[c.x, c.y] != bf[c.x, c.y]), fog.Count, fog.Count == 0 ? "" : FogShape(cells, bf, ef, pf));
        }
        Diff(rows, "ход", engine.Stacks.Values, s => s.Id, s => before.Stacks.GetValueOrDefault(s.Id)?.Move,
             s => s.Move, s => map.Stacks.GetValueOrDefault(s.Id)?.Move);
        Diff(rows, "здоровье", engine.Units.Values, u => u.Id, u => before.Units.GetValueOrDefault(u.Id)?.Hp,
             u => u.Hp, u => map.Units.GetValueOrDefault(u.Id)?.Hp);
        static string Mods(MapUnit? u) => u is null ? "нет" : string.Join("+", u.Modifiers.Select(m => m.ToUpperInvariant()).Order(StringComparer.Ordinal));
        Diff(rows, "модификаторы", engine.Units.Values, u => u.Id, u => Mods(before.Units.GetValueOrDefault(u.Id)),
             u => Mods(u), u => Mods(map.Units.GetValueOrDefault(u.Id)));
        static bool? Hidden(MapLooks l, string id) => l.Stacks.FirstOrDefault(s => Same(s.Id, id))?.Invisible;
        Diff(rows, "невидимость", engine.Stacks.Values, s => s.Id, s => Hidden(beforeLooks, s.Id), s => Hidden(engineLooks, s.Id),
             s => Hidden(port.Looks, s.Id));
        Diff(rows, "казна", engine.Players.Values, p => p.Id, p => before.Players.GetValueOrDefault(p.Id)?.Bank, p => p.Bank,
             p => map.Players.GetValueOrDefault(p.Id)?.Bank);
        Diff(rows, "опыт", engine.Units.Values, u => u.Id, u => before.Units.GetValueOrDefault(u.Id)?.Xp, u => u.Xp,
             u => map.Units.GetValueOrDefault(u.Id)?.Xp);
        static string Ids(IEnumerable<string> ids) => string.Join("+", ids.Select(i => i.ToUpperInvariant()).Order(StringComparer.Ordinal));
        static string Guards(MapState m, string site) => m.Sites.FirstOrDefault(x => Same(x.Id, site)) is { } f ? Ids(f.Guards) : "нет";
        Diff(rows, "гарнизон", engine.Sites.Where(x => x.Guards.Count > 0 || before.Sites.Any(b => Same(b.Id, x.Id) && b.Guards.Count > 0)),
             x => x.Id, x => Guards(before, x.Id), x => Guards(engine, x.Id), x => Guards(map, x.Id));
        static string Items(MapState m, string stack) => m.Stacks.TryGetValue(stack, out var st) ? Ids(st.Items) : "нет";
        Diff(rows, "предметы", engine.Stacks.Values, x => x.Id, x => Items(before, x.Id), x => Items(engine, x.Id), x => Items(map, x.Id));
        static string Bags(MapState m) => Ids(m.Sites.Where(x => x.Kind == SiteKind.Bag && !x.Gone).Select(x => $"{x.Id}@{x.X},{x.Y}:{x.Items.Count}"));
        Diff(rows, "мешки", [engine], _ => "карта", _ => Bags(before), Bags, _ => Bags(map));
        Diff(rows, "юниты", [engine], _ => "карта", _ => Ids(before.Units.Keys), m => Ids(m.Units.Keys), _ => Ids(map.Units.Keys));
        return rows;
    }

    /// <summary>Рамка изменившихся клеток тумана строками «/»: <c>E</c> — сменилась только у движка, <c>P</c> — только у порта, <c>B</c> — у
    /// обоих, <c>#</c>/<c>.</c> — не менялась (закрыта/открыта).</summary>
    private static string FogShape(List<(int x, int y)> cells, bool[,] before, bool[,] engine, bool[,] port)
    {
        var moved = cells.Where(c => engine[c.x, c.y] != before[c.x, c.y] || port[c.x, c.y] != before[c.x, c.y]).ToList();
        int x0 = moved.Min(c => c.x), x1 = moved.Max(c => c.x), y0 = moved.Min(c => c.y), y1 = moved.Max(c => c.y);
        char At(int x, int y) => (engine[x, y] != before[x, y], port[x, y] != before[x, y]) switch
        {
            (true, true) => 'B', (true, false) => 'E', (false, true) => 'P', _ => before[x, y] ? '#' : '.',
        };
        return $"[{x0}..{x1}×{y0}..{y1}] " + string.Join("/", Enumerable.Range(y0, y1 - y0 + 1)
                                                               .Select(y => string.Concat(Enumerable.Range(x0, x1 - x0 + 1).Select(x => At(x, y)))));
    }

    private static void Diff<T, TV>(List<string> rows, string what, IEnumerable<T> items, Func<T, string> id, Func<T, TV?> before,
                                    Func<T, TV?> engine, Func<T, TV?> port)
    {
        var all = items.ToList();
        int changed = all.Count(i => !Equals(before(i), engine(i)));
        var differ = all.Where(i => !Equals(engine(i), port(i))).ToList();
        if (differ.Count > 0)
            rows.Add($"{what}: у движка изменилось {changed}, расходится {differ.Count} " +
                     string.Join(" ", differ.Take(6).Select(i => $"{id(i)} {engine(i)}/{port(i)}")));
    }

    /// <summary>
    /// Литья карты 11 у движка — числом против порта: сохранение оригинала «до», литья журнала портом, сохранение «после». Пары — все
    /// группы литий одного дня (<see cref="Magic2CastPairs"/>): ход книгой, свитком и посохом, Сокрытие, смена земли, туман, развеивание
    /// (по три источника), ослабление LW1–LW5, «Ветер возрождения» и «Ветер дорог» на всю карту, «Свет дня» дня 2.
    /// </summary>
    [SkippableFact]
    public void Magic2_CastsMatchOriginalSaves()
    {
        var pairs = Magic2CastPairs();
        var failed = new List<string>();
        foreach (var (before, after, casts) in pairs)
        {
            var beforeLooks = MapLooksLoader.Load(before.Path);
            var engineLooks = MapLooksLoader.Load(after.Path);
            var port = SessionOf(MapLooksLoader.Load(before.Path));   // своя копия: литьё правит туман на месте
            var refused = Replay(port, casts);
            var rows = Differences(beforeLooks, engineLooks, port);
            string head = $"{Path.GetFileName(before.Path)[^10..^3]} → {Path.GetFileName(after.Path)[^10..^3]} {string.Join(", ", casts.Select(Describe))}";
            Out.WriteLine($"{head}: {(refused.Count + rows.Count == 0 ? "совпало" : string.Join("; ", refused.Concat(rows)))}");
            if (refused.Count + rows.Count > 0) failed.Add(head);
        }
        Assert.Equal(Magic2CastWindows, pairs.Count);
        Assert.Empty(failed);
    }

    /// <summary>«Сумерки» дня 2 (<c>G000SS0095</c>, туман на всю карту): сохранения «до» того же дня нет (между <c>t01-024</c> и <c>t02-025</c> —
    /// ходы ИИ), поэтому сверка — неподвижной точкой: то же литьё портом на сохранении движка «после» туман игроков не меняет (закрыть всё
    /// чужим, кроме нейтрала и союзников, и открыть снова их обзор). Слабая: у движка «Сумерки» ИИ ничего не закрыли (закрытых клеток у
    /// каждого не больше, чем в конце дня 1, — проверяется ниже); нашла обзор у угла карты.</summary>
    [SkippableFact]
    public void Magic2_Nightfall_FixedPointOnOriginalSave()
    {
        var (journal, saves) = Magic2Saves();
        var nightfall = JournalCache.Read(journal, "spell_cast").OfType<SpellCastEvent>()
                                    .Single(c => c.Ret && Same(c.Spell, "G000SS0095") && c.Seq > saves[0].Seq && c.Seq < saves[^1].Seq);
        var after = saves.First(s => s.Seq > nightfall.Seq);
        Assert.NotEqual(saves.Last(s => s.Seq < nightfall.Seq).Turn, after.Turn);
        var looks = MapLooksLoader.Load(after.Path);
        var port = SessionOf(MapLooksLoader.Load(after.Path));
        Assert.Empty(Replay(port, [nightfall]));
        var rows = Differences(looks, looks, port).Where(r => r.StartsWith("туман", StringComparison.Ordinal)).ToList();
        foreach (var r in rows) Out.WriteLine(r);
        Assert.Empty(rows);
        // Слабая сверка: у движка «Сумерки» не закрыли ИИ ни одной клетки, открытой в конце дня 1 (ИИ разведал только то, что видит), —
        // проверяется лишь обзор после тумана; он и нашёл угол карты (143,0) у столицы Империи (Fog.Reveal, 0x5d4dfc).
        var dayOne = MapLooksLoader.Load(saves.Last(s => s.Seq < nightfall.Seq).Path).Map;
        Assert.All(looks.Map.Players.Values, p => Assert.Equal(HiddenCount(dayOne.Fog[p.FogId]) - Opened(dayOne.Fog[p.FogId], looks.Map.Fog[p.FogId]),
                                                               HiddenCount(looks.Map.Fog[p.FogId])));
    }

    private static int Opened(bool[,] was, bool[,] now) =>
        Enumerable.Range(0, now.GetLength(0)).Sum(x => Enumerable.Range(0, now.GetLength(1)).Count(y => was[x, y] && !now[x, y]));

    /// <summary>Клетки тумана заклинания (<c>0x5d4dfc</c>): квадрат без углов; угол, совпавший с тем же углом карты, остаётся — у
    /// обрезанного квадрата (вся карта) все четыре; при <c>r</c> = 0 единственная клетка — все четыре угла сразу и снимается и в углу карты
    /// (угол (x0, y1−1) у (0,0) с углом карты (0, size−1) не совпал).</summary>
    [Fact]
    public void SpellFogCells_SquareWithoutCorners_LikeEngine()
    {
        Assert.Equal(21, GameSession.SpellFogCells(30, 23, 2, 144).Count());
        Assert.DoesNotContain((28, 21), GameSession.SpellFogCells(30, 23, 2, 144));
        Assert.Equal(144 * 144, GameSession.SpellFogCells(72, 72, 72, 144).Count());
        Assert.Equal(144 * 144, GameSession.SpellFogCells(72, 72, 144, 144).Count());
        Assert.Empty(GameSession.SpellFogCells(0, 0, 0, 144));
        Assert.Equal(8, GameSession.SpellFogCells(0, 0, 2, 144).Count());   // у угла карты: 3×3 на карте без угла квадрата (2,2)
        Assert.DoesNotContain((2, 2), GameSession.SpellFogCells(0, 0, 2, 144));
    }

    /// <summary>
    /// Карта 11, действия с карты 6 против сохранений оригинала: отпуск Одержимого из гарнизона столицы (<c>unit_transfer</c> <c>dismiss</c>,
    /// <c>t01-020</c> → <c>t01-021</c>; страж — код 2 у <c>dismiss_check</c>, сверка — <c>HumanChecksHarnessTests.Magic2_*</c>), затем в одном окне
    /// сохранений (<c>t01-021</c> → <c>t01-022</c>) пустой мешок EB (бросок и закрытие без вещей: мешка у движка нет, эликсир у отряда) и
    /// частичное обучение Горгульи TR (<c>site_train</c>, X005TA0392 «за 400 золота — 100 очков»); вторая Горгулья — нет золота (X005TA0678).
    /// </summary>
    [SkippableFact]
    public void Magic2_ActionsMatchOriginalSaves()
    {
        var (journal, saves) = Magic2Saves();
        var events = JournalCache.Read(journal, ["unit_transfer", "site_train"])
                                 .Where(e => e.Seq > saves[0].Seq && e.Seq < saves[^1].Seq).ToList();
        var dismiss = events.OfType<UnitTransferEvent>().Single(t => t is { Kind: "dismiss", Ret: true });
        var train = events.OfType<SiteTrainEvent>().Single(t => t.Ret);
        var facts = Facts(Magic2Map);
        var eb = Zone(facts, "EB");
        var tr = Zone(facts, "TR");
        (SaveCopy Before, SaveCopy After) Around(long seq) => (saves.Last(s => s.Seq < seq), saves.First(s => s.Seq > seq));
        var failed = new List<string>();
        void Check(string what, (SaveCopy Before, SaveCopy After) pair, Action<GameSession> act)
        {
            var beforeLooks = MapLooksLoader.Load(pair.Before.Path);
            var port = SessionOf(MapLooksLoader.Load(pair.Before.Path));
            act(port);
            var rows = Differences(beforeLooks, MapLooksLoader.Load(pair.After.Path), port);
            Out.WriteLine($"{what} {Path.GetFileName(pair.Before.Path)[^10..^3]} → {Path.GetFileName(pair.After.Path)[^10..^3]}: " +
                          (rows.Count == 0 ? "совпало" : string.Join("; ", rows)));
            if (rows.Count > 0) failed.Add(what);
        }
        Check("отпуск", Around(dismiss.Seq), s => Assert.Equal(0, s.DismissUnit(dismiss.From, dismiss.Unit!)));
        string? second = null;
        Check("мешок и обучение", Around(train.Seq), s =>
        {
            var (dx, dy) = XY(eb, "drop_at");
            Assert.Null(s.DropBag(Str(eb, "stack"), dx, dy, out string bag));
            s.CloseBag(bag);
            var pupil = s.Map.Stacks[Str(tr, "stack")];
            Assert.Null(s.Train(pupil.Id, train.Site, train.Unit, out var paid));
            Out.WriteLine($"обучение {train.Unit}: заплачено {paid.Gold}");
            string other = pupil.UnitIds.First(u => !Same(u, pupil.LeaderId) && !Same(u, train.Unit));
            second = s.Train(pupil.Id, train.Site, other, out _);
        });
        Assert.Equal("у хозяина нет золота", second);   // окно — X005TA0678 (TrainerLogic), у движка тоже (ui_msgbox seq 1255497)
        Assert.Empty(failed);
    }
}
