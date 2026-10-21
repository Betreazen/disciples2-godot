using System.Text.Json;
using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Начало хода человека против журнала (<c>docs/day-cycle.md</c> §1): последнее сохранение дня N снято прямо перед
/// концом хода человека, поэтому «сохранение → <see cref="GameSession.NextDay"/>» порта сравнивается со строками журнала,
/// которые движок пишет в начале хода человека дня N + 1: рост земли (<c>land_grow</c>), доход (<c>income</c>), своя земля
/// (<c>terrain_day</c>), укрепления (<c>fort_day</c>), запас хода (<c>ap_restore</c>). Ходы ИИ между ними порт не проводит —
/// их земля повторяется из журнала (<see cref="WithAiLand"/>): рост ИИ забирает клетки человека, жезл ИИ на жиле отвергает клетки
/// у неё, снесённый жезл человека не растит.
/// <para>Измерено 25.09 (партия 20260919-human, 69 дней): доход, рост земли, вся земля, укрепления — 69 из 69, запас хода 179 из 179.
/// Прежнее правило порта (ландмарки с <c>MOUNTAIN = T</c> не отвергались) даёт всю землю 7 из 69: клетки гор-ландмарков порт
/// забирал, движок — нет. D2_LAND_SHOW=1 печатает обходы дня и лишние клетки.</para></summary>
[Trait("Category", "Corpus")]
public class HumanDayHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableFact]
    public void HumanTurnStart_MatchesJournal()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        string journal = Path.Combine(dir, "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var events = Events(journal, "income", "spell_expire", "land_grow", "terrain_day", "fort_day", "ap_restore",
                            "rod_drop", "stack_move");
        var c = new Dictionary<string, (int Same, int Differ)>();
        var unpaired = new List<int>();
        void Count(string what, bool same, string why)
        {
            var (s, d) = c.GetValueOrDefault(what);
            c[what] = same ? (s + 1, d) : (s, d + 1);
            if (!same && d < 8) output.WriteLine($"{what}: {why}");
        }
        foreach (string sg in LastSaveOfEachDay(dir))
        {
            var looks = MapLooksLoader.Load(sg);
            var human = looks.Map.Players.Values.First(p => p.IsHuman);
            int day = looks.Map.Turn;
            string name = Path.GetFileName(sg);
            // Начало хода человека дня N + 1 — его строка income; при перезагрузках дня их несколько, берётся та, чья казна
            // до дохода — казна сохранения.
            var income = events.FirstOrDefault(e => Ev(e) == "income" && Player(e) == human.Id && Turn(e) == day + 1
                                                    && BankOf(e.GetProperty("bank")) == human.Bank);
            if (income.ValueKind != JsonValueKind.Object)
            {
                // День 58 — человек потратил 500 золота после последнего сохранения; день 71 — последний в журнале.
                unpaired.Add(day);
                output.WriteLine($"{name}: нет строки income дня {day + 1} с казной {human.Bank}");
                continue;
            }
            long end = Seq(income);
            long start = events.Last(e => Ev(e) == "spell_expire" && Player(e) == human.Id && Seq(e) < end).GetProperty("seq").GetInt64();
            var window = events.Where(e => Seq(e) > start && Seq(e) <= end + 3).ToList();
            // Ходы ИИ между сохранением и ходом человека порт не проводит, но их земля забирает клетки человека (движок
            // потом берёт их обратно): она повторяется из журнала — вся земля ИИ по terrain_day и обходы land_grow после них.
            long from = events.LastOrDefault(e => Ev(e) == "income" && Player(e) == human.Id && Turn(e) == day && Seq(e) < start) is { ValueKind: JsonValueKind.Object } prev
                ? Seq(prev) : 0;
            looks = WithAiLand(looks, events.Where(e => Seq(e) > from && Seq(e) < start), human.Id);

            var session = new GameSession(new GameState(new GameInfo("PtC", "PtC", [human.Id], looks.Map.Limits?.DifficultyGame ?? 1, looks.Map.Limits?.SuggestedLevel ?? 1), looks),
                                          content.MapArt, book);
            session.NextDay();
            var after = session.Map;

            // Доход: прибавка казны порта против строки income.
            var gained = after.Players[human.Id].Bank - human.Bank;
            Count("доход", gained == Income(income), $"{name} день {day + 1}: журнал {Income(income)}, порт {gained}");

            // Земля: новые клетки порта против клеток land_grow окна, всё пятно — против terrain_day.
            int terrain = (int)window.First(e => Ev(e) == "terrain_day").GetProperty("terrain").GetInt64();
            // land_grow пишет клетки обхода; квадраты жил рядом с ними перекраска добавляет сама (LandWalk.WithCrystalSquares).
            var crystals = looks.Map.Objects.Values.Where(o => o.Kind.Equals("CR", StringComparison.OrdinalIgnoreCase)).Select(o => (o.X, o.Y)).ToHashSet();
            var grownWant = window.Where(e => Ev(e) == "land_grow")
                                  .SelectMany(e => LandWalk.WithCrystalSquares([.. GrowCells(e.GetProperty("cells").GetString()!)], crystals, looks.Map.Size))
                                  .Where(p => looks.Map.Tiles[p.X, p.Y].Passable && looks.Map.Tiles[p.X, p.Y].Terrain != terrain).ToHashSet();
            var grownGot = Cells(after, terrain).Where(p => looks.Map.Tiles[p.X, p.Y].Terrain != terrain).ToHashSet();
            if (!grownGot.SetEquals(grownWant) && Environment.GetEnvironmentVariable("D2_LAND_SHOW") is { Length: > 0 })
            {
                foreach (var e in window.Where(e => Ev(e) == "land_grow"))
                    output.WriteLine($"  land_grow ({e.GetProperty("x")},{e.GetProperty("y")}) q {e.GetProperty("quota")}: {e.GetProperty("cells")}");
                output.WriteLine($"  лишние {string.Join(" ", grownGot.Except(grownWant))}; недостающие {string.Join(" ", grownWant.Except(grownGot))}");
                output.WriteLine($"  жезлы: {string.Join(" ", looks.Map.Rods.Values.Select(r => $"{r.Id}@{r.X},{r.Y}:{r.Owner}"))}");
            }
            Count("рост земли", grownGot.SetEquals(grownWant),
                  $"{name} день {day + 1}: журнал {grownWant.Count}, порт {grownGot.Count}, лишних {grownGot.Except(grownWant).Count()}, " +
                  $"недостающих {grownWant.Except(grownGot).Count()}");
            var ownWant = TerrainDayEvent.ParseCells(window.First(e => Ev(e) == "terrain_day").GetProperty("cells").GetString()!);
            var ownGot = Cells(after, terrain).ToHashSet();
            if (!ownGot.SetEquals(ownWant) && Environment.GetEnvironmentVariable("D2_LAND_SHOW") is { Length: > 0 })
                foreach (var p in ownGot.Except(ownWant).Concat(ownWant.Except(ownGot)))
                    output.WriteLine($"  день {day + 1} {(ownWant.Contains(p) ? "нет у порта" : "лишняя")} {p}: терр {looks.Map.Tiles[p.X, p.Y].Terrain} " +
                                     $"план [{string.Join(",", looks.Map.Plan.GetValueOrDefault(p, []))}]");
            Count("земля дня", ownGot.SetEquals(ownWant),
                  $"{name} день {day + 1}: журнал {ownWant.Count}, порт {ownGot.Count}, лишних {ownGot.Except(ownWant).Count()}, " +
                  $"недостающих {ownWant.Except(ownGot).Count()}");

            // Укрепления: владелец, уровень, ходы роста и бунта, визитёр.
            var forts = window.First(e => Ev(e) == "fort_day").GetProperty("forts").EnumerateArray().ToList();
            foreach (var f in forts)
            {
                string id = f.GetProperty("id").GetString()!;
                var site = after.Sites.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                string want = $"{f.GetProperty("owner").GetString()} ур {f.GetProperty("size").GetInt64()} рост {f.GetProperty("growth_t").GetInt64()} " +
                              $"бунт {f.GetProperty("riot_t").GetInt64()} гость {Stack(f.GetProperty("stack").GetString())}";
                string got = site is null ? "нет" : $"{site.Owner} ур {site.Size ?? -1} рост {site.GrowthTurn ?? -1} бунт {site.RiotTurn ?? -1} гость {Stack(site.StackId)}";
                Count("укрепление", want.Equals(got, StringComparison.OrdinalIgnoreCase), $"{name} день {day + 1} {id}: журнал {want}, порт {got}");
            }
            int fortsGot = after.Sites.Count(s => s.Kind is SiteKind.Capital or SiteKind.Village && s.Owner.Equals(human.Id, StringComparison.OrdinalIgnoreCase));
            Count("число укреплений", fortsGot == forts.Count, $"{name} день {day + 1}: журнал {forts.Count}, порт {fortsGot}");

            // Дипломатия дня: движок на смене дня и в начале хода отношений не меняет (0x420a6b, 0x443ca2 — только шпионы;
            // журналы: 0 строк relation/alliance_change в окнах начала хода человека, docs/day-cycle.md §5).
            Count("отношения", after.Relations.SequenceEqual(looks.Map.Relations), $"{name} день {day + 1}: отношения изменились");

            // Запас хода отрядов.
            foreach (var ap in window.Where(e => Ev(e) == "ap_restore" && Player(e) == human.Id))
            {
                string stack = ap.GetProperty("stack").GetString()!;
                long want = ap.GetProperty("move_after").GetInt64();
                int? got = after.Stacks.TryGetValue(stack, out var s) ? s.Move : null;
                Count("запас хода", got == want, $"{name} день {day + 1} {stack}: журнал {want}, порт {got}");
            }
        }
        foreach (var (what, (same, differ)) in c)
            output.WriteLine($"{what}: сошлось {same}, разошлось {differ}");
        Assert.Equal([58, 71], unpaired);
        Assert.Equal((69, 0), c["доход"]);
        Assert.Equal((69, 0), c["рост земли"]);
        Assert.Equal((69, 0), c["земля дня"]);
        Assert.Equal((88, 0), c["укрепление"]);
        Assert.Equal((69, 0), c["число укреплений"]);
        Assert.Equal((179, 0), c["запас хода"]);
        Assert.Equal((69, 0), c["отношения"]);
    }

    /// <summary>Земля ИИ из журнала поверх сохранения: клетки <c>terrain_day</c> игроков ИИ и обходы <c>land_grow</c> после
    /// первой из них (захваты и жезлы в ходах ИИ, с квадратами жил; у поставленного жезла — и его клетка) получают террейн строки;
    /// поставленные жезлы ИИ добавляются, снесённые (<c>ap_restore</c> с <c>cause = rod_break</c>) — убираются.</summary>
    private static MapLooks WithAiLand(MapLooks looks, IEnumerable<JsonElement> between, string human)
    {
        var tiles = (Tile[,])looks.Map.Tiles.Clone();
        var crystals = looks.Map.Objects.Values.Where(o => o.Kind.Equals("CR", StringComparison.OrdinalIgnoreCase)).Select(o => (o.X, o.Y)).ToHashSet();
        bool ai = false;
        (int X, int Y)? rod = null;
        var rods = looks.Map.Rods.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        var at = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase);
        void Set((int X, int Y) p, int terrain) => tiles[p.X, p.Y] = new Tile((byte)((tiles[p.X, p.Y].Raw & ~0b111) | terrain));
        foreach (var e in between)
        {
            if (Ev(e) == "terrain_day" && Player(e) != human)
            {
                ai = true;
                int terrain = (int)e.GetProperty("terrain").GetInt64();
                foreach (var p in TerrainDayEvent.ParseCells(e.GetProperty("cells").GetString()!)) Set(p, terrain);
            }
            else if (Ev(e) == "rod_drop" && ai)
            {
                // Поставленный жезл ИИ сразу отвергает клетки у своей жилы для чужих обходов (LandWalk.NearForeignRodCrystal).
                rod = ((int)e.GetProperty("x").GetInt64(), (int)e.GetProperty("y").GetInt64());
                string id = $"{human[..4]}RDF{rods.Count:x3}";
                rods[id] = new MapRod(id, Player(e), rod.Value.X, rod.Value.Y);
            }
            else if (Ev(e) == "stack_move") at[e.GetProperty("stack").GetString()!] = ((int)e.GetProperty("x").GetInt64(), (int)e.GetProperty("y").GetInt64());
            else if (Ev(e) == "ap_restore" && ai && e.GetProperty("cause").GetString() == "rod_break"
                     && at.TryGetValue(e.GetProperty("stack").GetString()!, out var by))
            {
                // Снесённый жезл — чужой жезл рядом с клеткой, где встал сносящий (строки сноса позиции жезла не пишут).
                string breaker = Player(e);
                var broken = rods.Values.Where(r => !r.Owner.Equals(breaker, StringComparison.OrdinalIgnoreCase)
                                                    && Math.Max(Math.Abs(r.X - by.X), Math.Abs(r.Y - by.Y)) <= 1).ToList();
                if (broken.Count == 1) rods.Remove(broken[0].Id);
            }
            else if (Ev(e) == "land_grow" && ai)
            {
                // Обход после постановки жезла: клетка жезла перекрашивается вместе с квадратом жилы под ним.
                var centre = ((int)e.GetProperty("x").GetInt64(), (int)e.GetProperty("y").GetInt64());
                List<(int X, int Y)> cells = [.. GrowCells(e.GetProperty("cells").GetString()!)];
                if (rod == centre) cells.Insert(0, centre);
                rod = null;
                foreach (var p in LandWalk.WithCrystalSquares(cells, crystals, looks.Map.Size))
                    if (tiles[p.X, p.Y].Passable) Set(p, (int)e.GetProperty("terrain").GetInt64());
            }
        }
        return GameSession.Retold(looks, GameSession.WithRods(GameSession.WithTiles(looks.Map, tiles), rods), looks.Stacks);
    }

    /// <summary>Клетки <c>land_grow</c>: <c>"x:y;x:y"</c> в порядке взятия (у <c>terrain_day</c> — другой вид, строки по y).</summary>
    private static IEnumerable<(int X, int Y)> GrowCells(string cells) =>
        cells.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Select(p => (int.Parse(p[0]), int.Parse(p[1])));

    private static string Stack(string? id) => id is null or "" or "G000000000" ? "-" : id.ToUpperInvariant();

    private static IEnumerable<(int X, int Y)> Cells(MapState map, int terrain)
    {
        for (int x = 0; x < map.Size; x++)
            for (int y = 0; y < map.Size; y++)
                if (map.Tiles[x, y].Terrain == terrain) yield return (x, y);
    }

    /// <summary>Последнее сохранение каждого дня — снято прямо перед концом хода человека.</summary>
    internal static IEnumerable<string> LastSaveOfEachDay(string dir) =>
        Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal)
                 .GroupBy(f => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Value)
                 .Select(g => g.Last());

    private static string Ev(JsonElement e) => e.GetProperty("ev").GetString()!;

    private static long Seq(JsonElement e) => e.GetProperty("seq").GetInt64();

    private static string Player(JsonElement e) => e.TryGetProperty("player", out var p) ? p.GetString() ?? "" : "";

    private static long Turn(JsonElement e) => e.TryGetProperty("turn", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : -1;

    private static Bank Income(JsonElement e)
    {
        var m = e.GetProperty("mana");
        return new Bank(e.GetProperty("gold").GetInt64(), m.GetProperty("infernal").GetInt64(), m.GetProperty("life").GetInt64(),
                        m.GetProperty("death").GetInt64(), m.GetProperty("runic").GetInt64(), m.GetProperty("grove").GetInt64());
    }

    private static Bank BankOf(JsonElement b) =>
        new(b.GetProperty("gold").GetInt64(), b.GetProperty("infernal").GetInt64(), b.GetProperty("life").GetInt64(),
            b.GetProperty("death").GetInt64(), b.GetProperty("runic").GetInt64(), b.GetProperty("grove").GetInt64());

    private static List<JsonElement> Events(string path, params string[] names)
    {
        var markers = names.Select(n => $"\"ev\":\"{n}\"").ToArray();
        var list = new List<JsonElement>();
        foreach (string line in File.ReadLines(path))
        {
            if (!markers.Any(m => line.Contains(m, StringComparison.Ordinal))) continue;
            using var doc = JsonDocument.Parse(line);
            list.Add(doc.RootElement.Clone());
        }
        return [.. list.OrderBy(Seq)];
    }
}
