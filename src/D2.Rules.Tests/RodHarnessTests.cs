using D2.Data;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Magic;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Харнесс задачи 8 Ф7: команды жезла против сохранений корпуса. Наблюдение — цепочки сохранений
/// (<see cref="SaveChains"/>), а не журнал: события <c>rod_drop</c> схемы 5.3 есть, но снос жезла
/// своего хука не имеет вовсе (run2 §2.2), а земля и <c>MOVE</c> видны только в <c>.sg</c>.
///
/// <list type="bullet">
/// <item><c>rod_place_allowed</c> — каждый новый <c>RD</c> проходит нашу калитку места
/// (<see cref="Rods.PlaceRefusal"/>), считанную по состоянию ПРЕДЫДУЩЕГО сохранения;</item>
/// <item><c>rod_place_leader</c> — ставивший отряд ведёт лидер со способностью <c>L_ROD</c>;</item>
/// <item><c>rod_place_move</c> и <c>rod_break_move</c> — у ставившего и у сносившего
/// <c>MOVE = 0</c>, у стоявшего на месте и ничего не делавшего — полный запас;</item>
/// <item><c>rod_break_freeze</c> — земля снесённого жезла не возвращается;</item>
/// <item><c>sinkhole_own_refused</c> — свой жезл своим заклинанием не снесён, мана не убыла.</item>
/// </list>
///
/// <para><b>+3 клетки в день установки</b> (<c>rod_place_bonus</c> приёмки) заново не измеряется:
/// это строка <c>rod_placement</c> харнесса задачи 7 (<see cref="LandHarnessTests"/>,
/// <c>Land_MatchesSourceQuotasOnSaveChains</c>) — семь установок по 3 клетки, 14 сравнений, 0
/// расхождений. Здесь она только названа, чтобы приёмка задачи 8 читалась целиком.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class RodHarnessTests(ITestOutputHelper output)
{
    private const string PlaceAllowed = "rod_place_allowed";
    private const string PlaceLeader = "rod_place_leader";
    private const string PlaceMove = "rod_place_move";
    private const string BreakMove = "rod_break_move";
    private const string BreakFreeze = "rod_break_freeze";
    private const string Sinkhole = "sinkhole_own_refused";

    /// <summary>Заклинание снятия жезла ванили — единственное с категорией
    /// <see cref="SpellCategory.RemoveRod"/>.</summary>
    private const string SinkholeSpell = "g000ss0119";

    /// <summary>Карты, где очки хода и снос наблюдаемы: на <c>m6-rods</c> жезлы ставит и сносит
    /// человек, отряды стоят на месте, а ИИ на карте нет вовсе. В больших партиях ни того, ни
    /// другого: жезлы ставят четыре ИИ внутри своего хода, их отряды в том же окне ходят, и
    /// <c>MOVE</c> в сохранении ничего про жезл не говорит. Калитка места (<c>rod_place_allowed</c>)
    /// от этого не страдает и считается на всех картах.</summary>
    private static readonly string[] Named = ["f7-m6-rods", "ai-g1-rod-break"];

    [SkippableFact] public void Rods_MatchTheSaveChains()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Dir.Length > 0 && Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var book = ProgressBook.Load(GamePaths.Globals);
        var spell = book.Prices.Spells.Values.FirstOrDefault(s => s.Category == SpellCategory.RemoveRod);
        Skip.If(spell is null, $"в Gspells нет заклинания категории RemoveRod ({SinkholeSpell})");

        var c = new Counters();
        c.Note($"ROD_COST = {Treasury.RodPrice(book.Prices.Treasury).Gold} золота; L_ROD = " +
               $"{Rods.LeaderAbilityRod} у {book.InnateAbilities.Count(a => a.Value.Contains(Rods.LeaderAbilityRod))} " +
               $"типов GLabi; заклинание сноса {spell!.Id}, цена {Mana(spell.CastingCost)}");
        c.Note("rod_place_bonus (+3 клетки в день установки) — строка rod_placement харнесса задачи 7: "
             + "семь установок, 14 сравнений, 0 расхождений; заново здесь не измеряется.");

        foreach (var (name, files) in SaveChains.All()) Chain(name, files, book, spell, c);
        foreach (var path in CorpusFiles.Journals()) Journal(path, c);

        c.Print(output, "задача 8 — жезлы: постановка, снос, заморозка земли");
        c.Require(PlaceAllowed, PlaceLeader, PlaceMove, BreakMove);
        c.RequireOrSkip(
            (BreakFreeze, "карта ai-g1-rod-break: отряд Горных кланов сам сносит жезл человека, рядом нет других источников земли"),
            (Sinkhole, "карта плана f7-m6-rods: человек, знающий Sinkhole (g000ss0119), колдует его на свой жезл"));
    }

    /// <summary>Одно сохранение цепочки в объёме, нужном жезлам.</summary>
    private sealed record Shot(string File, MapState Map, IReadOnlySet<(int X, int Y)> Owned,
                               string Human, Bank Purse, bool KnowsSinkhole);

    /// <summary>Цепочка целиком: по каждому переходу — новые и исчезнувшие жезлы, плюс два
    /// проверяемых на всю цепочку правила (первый день установки и Sinkhole).</summary>
    private void Chain(string name, IReadOnlyList<string> files, ProgressBook book, Spell spell, Counters c)
    {
        bool named = Named.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
        Shot? prev = null;
        var shots = new List<Shot>();
        foreach (var f in files)
        {
            var shot = Load(f, book);
            if (prev is not null)
            {
                var gone = prev.Map.Rods.Values.Where(r => !shot.Map.Rods.ContainsKey(r.Id)).ToList();
                foreach (var rod in shot.Map.Rods.Values.Where(r => !prev.Map.Rods.ContainsKey(r.Id)))
                    Placed(name, prev, shot, rod, gone, book, c);
                if (gone.Count > 0) Broken(name, named, prev, shot, gone, book, c);
            }
            shots.Add(shot);
            prev = shot;
        }
        if (named) FirstDay(name, shots, book, c);
        if (named) SinkholeOnChain(name, shots, spell, c);
    }

    // ------------------------------------------------------------------------------- постановка

    /// <summary>Новый жезл: калитка места по состоянию предыдущего сохранения и способность
    /// <c>L_ROD</c> у лидера ставившего отряда.</summary>
    private void Placed(string name, Shot before, Shot after, MapRod rod, IReadOnlyList<MapRod> gone,
                        ProgressBook book, Counters c)
    {
        string where = $"{name} {after.File} {rod.Id} ({rod.X},{rod.Y}) игрока {rod.Owner}";
        if (gone.FirstOrDefault(g => Chebyshev(g, rod) <= Rods.RodKeepOut) is { } clash)
        {
            c.Skip(PlaceAllowed, $"{name}: в одном окне снят жезл {clash.Id} в коробке 5×5 от нового — " +
                                 "порядок событий внутри окна из сохранений не виден");
            return;
        }
        // В кошелёк кладётся ровно ROD_COST: считается МЕСТО. Казна из соседнего сохранения — это
        // конец окна, когда ИИ уже всё потратил (в корпусе она меньше 150 на 22 установках из 71),
        // и калиткой цены она не является; саму цену проверил харнесс казны задачи 1 по строкам
        // bank — 7 установок из 7. Способность лидера тоже подставляется, а мерится отдельной
        // строкой rod_place_leader: иначе «проверка места» мерила бы не место.
        var why = Rods.PlaceRefusal(before.Map, rod.X, rod.Y, Treasury.RodPrice(book.Prices.Treasury),
                                    book.Prices.Treasury, [Rods.LeaderAbilityRod]);
        c.Count(PlaceAllowed, why is null, $"{where}: наша калитка отказала — {why}");
        if (Treasury.Affordable(Purse(before.Map, rod.Owner), Treasury.RodPrice(book.Prices.Treasury)) < 1)
            c.Skip("rod_place_price", "казна владельца в соседнем сохранении меньше ROD_COST — это конец "
                                    + "окна, а не момент постановки: цена этой установки не проверяется");

        var stacks = after.Map.Stacks.Values
            .Where(s => Same(s.Owner, rod.Owner) && Chebyshev(s.X, s.Y, rod.X, rod.Y) <= 1).ToList();
        if (stacks.Count != 1)
        { c.Skip(PlaceLeader, $"{name}: рядом с {rod.Id} отрядов владельца {stacks.Count} — ставивший не опознан"); return; }
        // Способности — все три источника задачи 9 (GLabi ∪ навыки уровня ∪ MOD_EQUIP надетого):
        // жезлоносцам корпуса хватает врождённой, но выдать L_ROD может и модификатор.
        if (book.Abilities(after.Map, stacks[0], -1, out var whyNot) is not { } has)
        { c.Skip(PlaceLeader, $"{name}: {whyNot}"); return; }
        c.Count(PlaceLeader, has.All.Contains(Rods.LeaderAbilityRod),
                $"{where}: у лидера отряда {stacks[0].Id} способности {Show(has.All)} — L_ROD нет");
    }

    /// <summary>
    /// Первое сохранение цепочки, начатой с хода 1: жезлы в нём поставлены в этот же ход, значит у
    /// ставивших отрядов остаток хода съеден целиком (<see cref="Movement.WholeTurnSpent"/>).
    /// Это три установки прогона 2 <c>m6-rods</c> — единственное место корпуса, где постановка
    /// отделена от ходьбы: в прогоне 1 жезлоносец до установки шёл и <c>MOVE</c> у него 0 и без неё.
    /// </summary>
    private void FirstDay(string name, IReadOnlyList<Shot> shots, ProgressBook book, Counters c)
    {
        var first = shots[0];
        if (first.Map.Turn != 1)
        { c.Skip(PlaceMove, $"{name}: цепочка начата с хода {first.Map.Turn} — когда ставили, не видно"); return; }
        foreach (var rod in first.Map.Rods.Values.Where(r => Same(r.Owner, first.Human)))
        {
            if (Placer(first, rod) is not { } stack)
            { c.Skip(PlaceMove, $"{name}: ставивший {rod.Id} не опознан по соседству"); continue; }
            c.Count(PlaceMove, stack.Move == Movement.WholeTurnSpent,
                    $"{name} {first.File} {rod.Id}: у ставившего {stack.Id} MOVE {stack.Move}, ждали " +
                    $"{Movement.WholeTurnSpent}");
        }
        if (first.Map.Rods.Count > 0)
            c.Note($"  {name} {first.File} ход 1: жезлов {first.Map.Rods.Count}, MOVE отрядов " +
                   Show(first, book));
    }

    /// <summary>Очки хода при постановке и сносе — по журналу: строка <c>ap_restore</c> с причиной <c>rod_place</c>
    /// (<c>0x437681</c>, внутри окна <c>rod_drop</c>) или <c>rod_break</c> сама называет отряд и его <c>MOVE</c> до и
    /// после, поэтому ставивший и сносивший опознаются на любой карте, а не только на <c>m6-rods</c> (прежде — по
    /// соседству в сохранении). Ранг 1 по журналу 20260919-human: seq 2911529 — S073KC0075 ставит жезл при 13 очках,
    /// после — 0, <c>zero = spent</c>. Схема зонда 6.x; в старых журналах строк нет, и здесь они ничего не дают.</summary>
    private static void Journal(string path, Counters c)
    {
        string name = $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";
        foreach (var e in JournalCache.Read(path, "ap_restore").OfType<ApRestoreEvent>()
                     .Where(e => e.Ret && e.Cause is "rod_place" or "rod_break"))
            c.Count(e.Cause == "rod_place" ? PlaceMove : BreakMove, e.MoveAfter == Movement.WholeTurnSpent,
                    $"{name} seq {e.Seq} {e.Cause} {e.Stack} ({e.Player}): MOVE {e.MoveBefore} → {e.MoveAfter}, " +
                    $"ждали {Movement.WholeTurnSpent}");
    }

    // ------------------------------------------------------------------------------------- снос

    /// <summary>
    /// Исчезнувшие жезлы: у сносившего отряда остаток хода ноль, у стоявшего на месте и не
    /// сносившего — полный запас, а земля снесённого жезла остаётся нашей.
    /// </summary>
    private void Broken(string name, bool named, Shot before, Shot after, IReadOnlyList<MapRod> gone,
                        ProgressBook book, Counters c)
    {
        string when = $"{name} {after.File} ход {after.Map.Turn}";
        // Вне карт плана не наблюдаемо ни то, ни другое: сносивший ИИ в том же окне ходит, а его
        // землю в это же окно перекрашивают чужие источники — измеренная задачей 7 причина, по
        // которой land_owned_other красный. Потеря клетки там не про снос жезла.
        if (!named)
        {
            c.Skip(BreakMove, $"{name}: снос вне карт плана — сносивший и его ходьба в окне не различимы");
            c.Skip(BreakFreeze, $"{name}: вне карт плана землю перекрашивают чужие источники — " +
                                "потерянная клетка не про снос жезла");
            return;
        }

        var breakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rod in gone)
        {
            // Заморозка: клетка снесённого жезла остаётся нашей.
            if (Same(rod.Owner, before.Human))
                c.Count(BreakFreeze, after.Owned.Contains((rod.X, rod.Y)),
                        $"{when}: клетка снесённого {rod.Id} ({rod.X},{rod.Y}) больше не наша");
            if (Placer(after, rod) is not { } stack)
            { c.Skip(BreakMove, $"{name}: сносивший {rod.Id} не опознан по соседству"); continue; }
            breakers.Add(stack.Id);
            c.Count(BreakMove, stack.Move == Movement.WholeTurnSpent,
                    $"{when}: у сносившего {stack.Id} MOVE {stack.Move}, ждали {Movement.WholeTurnSpent}");
        }

        var lost = before.Owned.Except(after.Owned).ToList();
        c.Count(BreakFreeze, lost.Count == 0,
                $"{when}: потеряно клеток своей земли {lost.Count}, первая {(lost.Count == 0 ? "-" : $"{lost[0]}")}");
        Bystanders(when, before, after, breakers, book, c);
    }

    /// <summary>Отряды, которые в этот ход не ставили и не сносили и с места не сдвинулись: у них
    /// полный запас хода лидера (<c>Gunits.MOVE</c> с уровневым ростом).</summary>
    private void Bystanders(string when, Shot before, Shot after, IReadOnlySet<string> busy,
                            ProgressBook book, Counters c)
    {
        foreach (var s in after.Map.Stacks.Values.Where(s => Same(s.Owner, after.Human) && !busy.Contains(s.Id)))
        {
            if (before.Map.Stacks.TryGetValue(s.Id, out var was) is false || was.X != s.X || was.Y != s.Y)
            { c.Skip(BreakMove, "отряд за окно сместился — остаток хода съеден шагами, а не жезлом"); continue; }
            if (FullMove(s, after.Map, book) is not { } full)
            { c.Skip(BreakMove, $"запас хода лидера отряда {s.Id} неизвестен"); continue; }
            c.Count(BreakMove, s.Move == full,
                    $"{when}: у не сносившего {s.Id} MOVE {s.Move}, ждали полный запас {full}");
        }
    }

    // -------------------------------------------------------------------------------- Sinkhole

    /// <summary>
    /// Свой жезл своим заклинанием не сносится, и отказ идёт до списания. Модель отвечает отказом
    /// (<see cref="Rods.SpellRefusal"/>), наблюдение — мана человека за цепочку не убыла ни разу ни
    /// в одном из пяти видов, хотя заклинание лежало в его книге, а маны хватало с запасом.
    /// </summary>
    private void SinkholeOnChain(string name, IReadOnlyList<Shot> shots, Spell spell, Counters c)
    {
        var own = shots.SelectMany(s => s.Map.Rods.Values.Where(r => Same(r.Owner, s.Human)))
                       .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (own.Count == 0) { c.Skip(Sinkhole, $"{name}: своих жезлов в цепочке нет"); return; }
        if (!shots.All(s => s.KnowsSinkhole))
        { c.Skip(Sinkhole, $"{name}: {spell.Id} не во всех сохранениях в книге человека — отказ ненаблюдаем"); return; }

        var drops = new List<string>();
        for (int k = 1; k < shots.Count; k++)
            foreach (var (kind, was, now) in Manas(shots[k - 1].Purse, shots[k].Purse))
                if (now < was) drops.Add($"{shots[k].File}: {kind} {was} → {now}");
        c.Note($"  {name} мана человека по цепочке: " +
               string.Join(" | ", shots.Select(s => $"{s.File} {Mana(s.Purse)}")));
        foreach (var rod in own)
        {
            var cast = SpellCast.RemoveRod(spell, shots[0].Purse, rod, shots[0].Human);
            bool ok = !cast.Done && cast.Purse == shots[0].Purse && drops.Count == 0;
            c.Count(Sinkhole, ok, $"{name} {rod.Id}: модель {(cast.Done ? "снесла свой жезл" : "отказала")}, " +
                                  $"падений маны {drops.Count} ({string.Join("; ", drops.Take(3))})");
        }
    }

    // ------------------------------------------------------------------------------ вспомогательное

    private Shot Load(string file, ProgressBook book)
    {
        var map = SaveMaps.Map(file);
        var human = map.Players.Values.FirstOrDefault(p => p.IsHuman);
        int terrain = human is not null && book.Prices.Economy.Races.TryGetValue(human.RaceId, out var race)
            ? Economy.TerrainOfRace(race.RaceType) : -1;
        return new Shot(Path.GetFileName(file), map, Cells(map, terrain), human?.Id ?? "",
                        human?.Bank ?? Bank.Zero,
                        human?.KnownSpells.Any(s => Same(s, SinkholeSpell)) ?? false);
    }

    /// <summary>Единственный отряд владельца жезла в соседней клетке (Чебышёв ≤ 1): и постановка, и
    /// снос требуют контакта, а отряд при них не двигается (run2 §2.2). Двусмысленность —
    /// пропуск с причиной, а не догадка.</summary>
    private static MapStack? Placer(Shot shot, MapRod rod)
    {
        var near = shot.Map.Stacks.Values
            .Where(s => Same(s.Owner, rod.Owner) && Chebyshev(s.X, s.Y, rod.X, rod.Y) <= 1).ToList();
        return near.Count == 1 ? near[0] : null;
    }

    /// <summary>Полный запас хода отряда — <c>MOVE</c> лидера с уровневым ростом.
    /// ponytail: цепочка <c>MODIF_ID</c> (сапоги, знамя, <c>TYPE 7</c> MoveAllowance) здесь не
    /// накладывается — у жезлоносцев <c>m6-rods</c> предметов нет вовсе. Потолок: лидер в сапогах
    /// даст расхождение, и его будет видно числом.</summary>
    private static int? FullMove(MapStack stack, MapState map, ProgressBook book) =>
        map.Units.TryGetValue(stack.LeaderId, out var leader)
            ? book.Grown(leader.TypeId, leader.Level)?.MoveAllowance : null;

    private static Bank Purse(MapState map, string playerId) =>
        map.Players.TryGetValue(playerId, out var p) ? p.Bank : Bank.Zero;

    private static IReadOnlySet<(int X, int Y)> Cells(MapState map, int terrain)
    {
        var set = new HashSet<(int X, int Y)>();
        if (terrain < 0) return set;
        for (int x = 0; x < map.Size; x++)
            for (int y = 0; y < map.Size; y++)
                if (map.Tiles[x, y].Terrain == terrain) set.Add((x, y));
        return set;
    }

    private static IEnumerable<(string Kind, long Was, long Now)> Manas(Bank a, Bank b)
    {
        yield return ("инферно", a.Infernal, b.Infernal);
        yield return ("жизнь", a.Life, b.Life);
        yield return ("смерть", a.Death, b.Death);
        yield return ("руны", a.Runic, b.Runic);
        yield return ("роща", a.Grove, b.Grove);
    }

    private static string Mana(Bank b) => $"I{b.Infernal}/L{b.Life}/D{b.Death}/R{b.Runic}/G{b.Grove}";

    private static string Show(Shot shot, ProgressBook book) =>
        string.Join(", ", shot.Map.Stacks.Values.Where(s => Same(s.Owner, shot.Human))
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => $"{s.Id}={s.Move}/{FullMove(s, shot.Map, book)?.ToString() ?? "?"}"));

    private static string Show(IReadOnlySet<int> abilities) =>
        abilities.Count == 0 ? "нет" : string.Join(",", abilities.Order());

    private static int Chebyshev(int ax, int ay, int bx, int by) =>
        Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    private static int Chebyshev(MapRod a, MapRod b) => Chebyshev(a.X, a.Y, b.X, b.Y);

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
