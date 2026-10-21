using System.Diagnostics;
using System.Text;
using System.Text.Json;
using D2.Data;
using D2.Data.Map;
using D2.Data.Sg;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

[Trait("Category", "Corpus")]
public class MapLoadTests
{
    private readonly ITestOutputHelper _out;

    public MapLoadTests(ITestOutputHelper output) => _out = output;

    // ---------------------------------------------------------------- казна

    [Fact]
    public void Bank_ParsesRaceIncome()
    {
        // Строки прочитаны из Globals/Grace.dbf, колонка INCOME (ранг 1).
        var empire = Bank.Parse("g0100:r0000:y0025:e0000:w0000");   // g000rr0000
        Assert.Equal(100, empire.Gold);
        Assert.Equal(25, empire.Life);
        Assert.Equal(0, empire.Infernal + empire.Death + empire.Runic + empire.Grove);

        var elves = Bank.Parse("g0100:r0000:y0000:e0000:w0000:b0025");   // g000rr0005
        Assert.Equal(100, elves.Gold);
        Assert.Equal(25, elves.Grove);
        Assert.Equal(0, elves.Life);
    }

    [Fact]
    public void Bank_AddsAndSubtracts()
    {
        var a = Bank.Parse("g0100:r0000:y0025:e0000:w0000");
        var b = Bank.Parse("g0050:r0005:y0005:e0000:w0000");

        Assert.Equal(new Bank(150, 5, 30, 0, 0, 0), a + b);
        Assert.Equal(new Bank(50, -5, 20, 0, 0, 0), a - b);
        Assert.Equal(a, a + Bank.Zero);
        Assert.Equal(Bank.Zero, a - a);
    }

    // ---------------------------------------------------------------- сценарий

    [SkippableFact]
    public void Load_ReadsSizeTerrainAndStacks()
    {
        var scenario = FindScenario();
        Skip.If(scenario is null, "нет ни одного сценария в Exports");

        var map = MapLoader.Load(scenario!);
        _out.WriteLine($"{Path.GetFileName(scenario)}: размер {map.Size}, отрядов {map.Stacks.Count}, " +
                       $"юнитов {map.Units.Count}, игроков {map.Players.Count}, " +
                       $"объектов с координатой {map.Objects.Count}, клеток плана {map.Plan.Count}");

        Assert.InRange(map.Size, 24, 144);
        Assert.Equal(map.Size, map.Tiles.GetLength(0));
        Assert.Equal(map.Size, map.Tiles.GetLength(1));
        // Сторона карты лежит в файле дважды: CMidgardMap (её берёт загрузчик) и
        // ScenarioInfo.MAP_SIZE (её читает SgFile). Расхождение означало бы, что мы взяли не то поле.
        Assert.Equal(SgFile.Open(scenario!).MapSize, map.Size);

        Assert.NotEmpty(map.Stacks);
        Assert.NotEmpty(map.Players);
        Assert.NotEmpty(map.Units);

        foreach (var stack in map.Stacks.Values)
        {
            Assert.InRange(stack.X, 0, map.Size - 1);
            Assert.InRange(stack.Y, 0, map.Size - 1);
            Assert.NotEmpty(stack.UnitIds);
            Assert.All(stack.UnitIds, id => Assert.True(map.Units.ContainsKey(id), id));
            Assert.Equal(6, stack.Slots.Count);
            // Пустая клетка приходит как 0xFFFFFFFF: если приведение к int потерялось,
            // здесь окажется 4294967295.
            Assert.All(stack.Slots, slot => Assert.InRange(slot, -1, 5));
            // Slots — карта КЛЕТКА -> индекс юнита в UnitIds, а не юнит -> клетка.
            // Проверка различающая: РАЗНЫХ индексов в занятых клетках ровно столько, сколько
            // юнитов, и это перестановка 0..UnitIds.Count-1. При обратном прочтении утверждение
            // ложно, как только у отряда меньше шести юнитов: там в POS_k лежал бы номер клетки,
            // а клетки идут через одну (0/2/4 передний ряд), то есть встретилось бы значение
            // >= UnitIds.Count. Измерено на 'A Return To Simpler Times'.sg: например у
            // S001KC0006 юнитов 3, а POS = [1,1,0,-1,2,-1].
            var filled = stack.Slots.Where(v => v >= 0).ToList();
            var indices = filled.Distinct().OrderBy(v => v).ToList();
            Assert.Equal(Enumerable.Range(0, stack.UnitIds.Count), indices);
            // Крупный юнит занимает две клетки, поэтому его индекс встречается дважды —
            // но не чаще (измерено там же: 13 отрядов карты из 68 с повтором, все по два).
            Assert.All(indices, i => Assert.InRange(filled.Count(v => v == i), 1, 2));
            Assert.True(map.Players.ContainsKey(stack.Owner), stack.Owner);
        }

        // Казна из настоящего файла. Декодер один: запись собирается в строку и разбирается
        // Bank.Parse по БУКВЕ валюты, поэтому чужая буква (то есть съехавшее поле) — исключение
        // прямо в MapLoader.Load, а не молча другая валюта. Проверка охватывает все 33 карты:
        // Load_GroundGridMatchesPythonReader грузит каждую целиком, вместе с игроками.
        foreach (var player in map.Players.Values)
        {
            _out.WriteLine($"казна {player.Id}: {player.Bank}");
            var b = player.Bank;
            Assert.All(new[] { b.Gold, b.Infernal, b.Life, b.Death, b.Runic, b.Grove },
                v => Assert.True(v >= 0, $"отрицательная валюта у {player.Id}: {b}"));
        }

        // Дороги — отдельные объекты класса RA, а не флаг клетки.
        int roads = map.Objects.Values.Count(o => o.Kind == "RA");
        _out.WriteLine($"дорог {roads}");
        if (roads > 0)
        {
            var road = map.Objects.Values.First(o => o.Kind == "RA");
            Assert.True(map.IsRoad(road.X, road.Y));
        }
    }

    /// <summary>
    /// Сетка террейна против независимого читателя того же формата: питоновский
    /// <c>tools/sgfile.py</c> разбирает <c>.sg</c> побайтово и ничего не знает про Kaitai.
    /// Сверяется каждая клетка каждой карты <c>Exports</c>, числа печатаются.
    /// </summary>
    [SkippableFact]
    public void Load_GroundGridMatchesPythonReader()
    {
        Skip.IfNot(Directory.Exists(GamePaths.Exports), "нет каталога Exports");
        // Дальше — только падения: сверка, которая молча превращается в Skip, ничего не сверяет.
        var golden = PythonTileGrids();

        int expectedMaps = Directory.GetFiles(GamePaths.Exports, "*.sg").Length;
        int checkedMaps = 0, checkedTiles = 0, matched = 0, mismatched = 0;
        var badMaps = new List<string>();
        foreach (var (name, rows) in golden)
        {
            string path = Path.Combine(GamePaths.Exports, name);
            if (!File.Exists(path))
                continue;
            checkedMaps++;
            var map = MapLoader.Load(path);
            int bad = 0;
            Assert.Equal(map.Size, rows.Count);
            for (int y = 0; y < map.Size; y++)
                for (int x = 0; x < map.Size; x++)
                {
                    checkedTiles++;
                    if (map.At(x, y).Raw == rows[y][x]) matched++;
                    else { mismatched++; bad++; }
                }
            if (bad > 0)
                badMaps.Add($"{name}: {bad}");
        }

        _out.WriteLine($"карт проверено {checkedMaps}, клеток проверено {checkedTiles}, " +
                       $"совпало {matched}, разошлось {mismatched}");
        if (badMaps.Count > 0)
            _out.WriteLine("расхождения по картам: " + string.Join(", ", badMaps));

        // Число проверенных карт утверждается, а не «больше нуля»: иначе сверка тихо слабеет
        // до одной карты, стоит питоновскому читателю споткнуться на остальных.
        Assert.Equal(expectedMaps, checkedMaps);
        Assert.True(checkedTiles > 0, "не проверено ни одной клетки");
        Assert.Equal(0, mismatched);
    }

    // ---------------------------------------------------------------- сохранение

    /// <summary>Туман, казна и день читаются из сохранения. Источник — закреплённый манифестом
    /// образец корпуса (<see cref="FogSample"/>), а не живой <c>game-dev/SaveGame</c>: 18.09.2026 там
    /// лежал <c>QuickSave.sg</c> дня 71 партии «Растленная власть», где человек открыл всю карту
    /// (скрыто 0 из 9216), и тест падал от содержимого чужой партии — тот же дефект, что задача 30
    /// сняла с проверки порядка бит.</summary>
    [SkippableFact]
    public void Load_SaveGameCarriesFogAndBank()
    {
        var save = Path.Combine(CorpusFiles.Dir, FogSample);
        Skip.IfNot(File.Exists(save), $"нет образца тумана {save}: корпус лежит вне git");

        var map = MapLoader.Load(save);
        _out.WriteLine($"{Path.GetFileName(save)}: день {map.Turn}, размер {map.Size}, " +
                       $"игроков {map.Players.Count}, объектов тумана {map.Fog.Count}");

        Assert.NotEmpty(map.Players);
        Assert.True(map.Turn > 0, "в сохранении не прочитан номер дня");

        var human = map.Players.Values.First(p => p.IsHuman);
        _out.WriteLine($"человек {human.Id} раса {human.RaceId}: казна {human.Bank}, " +
                       $"построек {human.Buildings.Count}");
        Assert.True(human.Bank.Gold >= 0);
        Assert.True(map.Fog.ContainsKey(human.FogId), "у игрока-человека нет объекта тумана");

        var fog = map.Fog[human.FogId];
        Assert.Equal(map.Size, fog.GetLength(0));
        Assert.Equal(map.Size, fog.GetLength(1));

        int hidden = 0;
        foreach (var bit in fog) if (bit) hidden++;
        int total = map.Size * map.Size;
        _out.WriteLine($"туман человека: скрыто {hidden} из {total}, открыто {total - hidden}");
        // true — клетка ещё скрыта (ранг 1, измерено: бит своей столицы 0, бит чужой 1),
        // поэтому у игравшего человека обязаны быть клетки обоих видов.
        Assert.True(hidden > 0, "у человека не осталось ни одной скрытой клетки");
        Assert.True(hidden < total, "у человека не открыто ни одной клетки");
    }

    /// <summary>
    /// Открытая область тумана связна. Это проверка ПОРЯДКА БИТ внутри байта маски, а не их
    /// числа: перестановка бит внутри группы из восьми не меняет ни «скрыто N из M», ни какой
    /// угодно счётчик, но рвёт открытое пятно ровно на границах байта — оторванные столбцы и
    /// дыры шириной в клетку. Разведанная игроком область одна и вокруг своей столицы, поэтому
    /// правильному чтению соответствует ровно одна компонента связности.
    /// (Измерение столиц из задачи 4 про порядок бит не говорит ничего: оно подтверждает только
    /// полярность — <c>true</c> = клетка скрыта.)
    /// <para><b>Образец неизменяемый — задача 30 Ф7.</b> Прежде тест читал первое попавшееся сохранение
    /// из живого <c>game-dev/SaveGame</c> и падал от содержимого текущей партии пользователя («туман
    /// S143FG0001: открыто 367 из 5184, компонент связности 2»): разведанное пятно игрока в ходе партии
    /// законно рвётся. Порядок бит проверяется на сохранении из корпуса, записанном прогоном и
    /// закреплённом манифестом, — проверка та же, меняется только источник.</para>
    /// </summary>
    [SkippableFact]
    public void Load_SaveGameFogOpenAreaIsConnected()
    {
        var save = Path.Combine(CorpusFiles.Dir, FogSample);
        Skip.IfNot(File.Exists(save), $"нет образца тумана {save}: корпус лежит вне git");
        var map = MapLoader.Load(save);

        int checkedFogs = 0;
        foreach (var (fogId, mask) in map.Fog)
        {
            int open = 0;
            foreach (var bit in mask) if (!bit) open++;
            // Сплошная маска (всё скрыто или всё открыто — так у нейтралов и в сценариях
            // Exports) связна при любом порядке бит и потому ничего не проверяет.
            if (open == 0 || open == map.Size * map.Size)
                continue;
            int components = OpenComponents(mask, map.Size);
            _out.WriteLine($"туман {fogId}: открыто {open} из {map.Size * map.Size}, " +
                           $"компонент связности {components}");
            Assert.Equal(1, components);
            checkedFogs++;
        }

        _out.WriteLine($"проверено масок тумана: {checkedFogs}");
        Assert.True(checkedFogs > 0, "в сохранении не нашлось ни одной маски с открытыми клетками");
    }

    /// <summary>Сохранение-образец для проверки порядка бит тумана (путь от <c>corpus/</c>): партия C4,
    /// день 30, одна частично открытая маска (<c>S143FG0001</c>, открыто 1138 из 2304 — пятно
    /// пересекает десятки границ байта). Выбрано замером задачи 30 Ф7, а не на глаз.
    /// <para><b>Одна компонента — свойство образца, а не игры.</b> Живой <c>game-dev/SaveGame</c> был
    /// сохранением карты M22: генератор ставит отряд человека у чужой столицы
    /// (<c>tools/make_f7_m22_map.py</c>, <c>move_stack</c>), и пятен честно два — то же «367 из 5184,
    /// компонент 2» даёт и <c>20260911-passive/p1-f7-m22-capture3-human-t01-057.sg</c>. Два пятна
    /// бывают и на ванильной карте: <c>p1-rastlennaya-vlast-heretic-t01-003.sg</c>, <c>S073FG000b</c>.
    /// Поэтому образец назван по имени, а не берётся первым попавшимся.</para></summary>
    private const string FogSample = "20260907-c4/save-30.sg";

    /// <summary>Число компонент связности (по четырём соседям) среди клеток <c>false</c>.</summary>
    private static int OpenComponents(bool[,] mask, int size)
    {
        var seen = new bool[size, size];
        var stack = new Stack<(int X, int Y)>();
        int components = 0;
        for (int y0 = 0; y0 < size; y0++)
            for (int x0 = 0; x0 < size; x0++)
            {
                if (mask[x0, y0] || seen[x0, y0])
                    continue;
                components++;
                seen[x0, y0] = true;
                stack.Push((x0, y0));
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                        if (mask[nx, ny] || seen[nx, ny]) continue;
                        seen[nx, ny] = true;
                        stack.Push((nx, ny));
                    }
                }
            }
        return components;
    }

    // ---------------------------------------------------------------- вспомогательное

    private static string? FindScenario()
    {
        if (!Directory.Exists(GamePaths.Exports))
            return null;
        string fast = Path.Combine(GamePaths.Exports, "test_fast_battle.sg");
        // Порядок перечисления каталога зависит от файловой системы; берём первый по Ordinal,
        // чтобы «первая карта» была одной и той же на любой машине.
        return File.Exists(fast)
            ? fast
            : Directory.EnumerateFiles(GamePaths.Exports, "*.sg")
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
    }


    // Первая строка обязательна: скрипт идёт в python через stdin, а там кириллица
    // комментариев без объявления кодировки — SyntaxError «Non-UTF-8 code».
    private const string TileGridScript = """
        # coding: utf-8
        import json, sys, pathlib
        sys.path.insert(0, sys.argv[1])
        import sgfile
        out = {}
        for p in sorted(pathlib.Path(sys.argv[2]).glob("*.sg")):
            # Без try: карта, которую питоновский читатель не разобрал, обязана уронить сверку,
            # а не тихо выпасть из неё.
            grid = sgfile.tile_grid(sgfile.Sg.read(p))
            out[p.name] = [bytes(row).hex() for row in grid]
        sys.stdout.write(json.dumps(out))
        """;

    /// <summary>
    /// Гонит питоновский читатель по всем картам <c>Exports</c> и возвращает
    /// <c>{имя карты: [строка клеток]}</c>. Любой отказ — падение с текстом: сверка с независимым
    /// читателем, которая при отсутствии <c>python</c> в <c>PATH</c> превращается в Skip,
    /// перестаёт быть сверкой незаметно для того, кто читает зелёный прогон.
    /// </summary>
    private Dictionary<string, List<byte[]>> PythonTileGrids()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tools = Path.Combine(repoRoot, "tools");
        string reader = Path.Combine(tools, "sgfile.py");
        Assert.True(File.Exists(reader), $"нет независимого читателя {reader}");

        // Интерпретатор: D2_PYTHON, иначе на Windows лаунчер py (имя python там бывает заглушкой Microsoft Store), иначе python3.
        string python = Environment.GetEnvironmentVariable("D2_PYTHON") is { Length: > 0 } custom ? custom
            : OperatingSystem.IsWindows() ? "py" : "python3";
        var info = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-");
        info.ArgumentList.Add(tools);
        info.ArgumentList.Add(GamePaths.Exports);
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("не запустился python (нужен в PATH)");
        process.StandardInput.Write(TileGridScript);
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"python вышел с кодом {process.ExitCode}: {stderr}");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stdout)
            ?? throw new InvalidOperationException($"python вернул не JSON: {stdout}");
        Assert.NotEmpty(parsed);
        return parsed.ToDictionary(p => p.Key, p => p.Value.Select(Convert.FromHexString).ToList());
    }
}
