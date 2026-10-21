using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Gfx;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using Godot;

namespace D2.Godot;

/// <summary>
/// Бенчмарки сцен порта (этап 6 пайплайна gdscript-knowledge): открытие карты, кадр карты, сборка земли, экран боя, окна UI.
/// Формат JSON — как у <c>bench.gd</c> скилла (<c>header</c>, <c>iterations</c>, <c>results</c>: имя → отсортированные замеры, мкс),
/// сравнение — <c>bench_compare.gd</c>. 3 прогрева, 15 замеров.
///
/// <para>Запуск (из корня репозитория; графика — <c>D2_ASSETS_DIR</c>, корпус — <c>D2_CORPUS_DIR</c>):
/// <c>godot --headless --path godot res://bench/BenchScene.tscn -- --no-mods [--filter=a,b] [--save=../bench/scene-before.json]</c>.
/// <c>--loop=имя --seconds=N</c> — крутить один случай (снять стеки <c>dotnet-stack report -p PID</c>).</para>
///
/// <para>Headless: рендера нет (заглушка RenderingServer), GPU и draw calls не мерятся. Кадр — время между соседними <c>process_frame</c>
/// при выключенном сне headless (весь шаг главного цикла на CPU), среднее на кадр за <see cref="Frames"/> кадров; открытие —
/// Stopwatch вокруг <c>AddChild</c> (весь синхронный <c>_Ready</c>). Сборка C# из редактора — Debug: цифры годятся для сравнения
/// «до/после» на этой же сборке, не как абсолютные.</para>
/// </summary>
public partial class BenchScene : Node
{
    private const int Warmup = 3;
    private const int Runs = 15;

    /// <summary>Кадров на один замер сценария кадра.</summary>
    private const int Frames = 64;

    /// <summary>Сколько кадров ждать готовности земли под камерой, прежде чем считать открытие сорванным.</summary>
    private const int GroundTimeoutFrames = 2000;

    private sealed record SceneCase(string Name, Func<Task<double>> Sample, Func<Task>? Setup = null, Func<Task>? Teardown = null);

    private string _corpus = "";
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);

    public override void _Ready() => _ = Main();

    private async Task Main()
    {
        string[] args = OS.GetCmdlineUserArgs();
        int code = 0;
        try
        {
            OS.LowProcessorUsageModeSleepUsec = 0;   // headless иначе спит ~7 мс на кадр (окно не рисуется) — замеру не мешает, но тянет время
            _corpus = System.Environment.GetEnvironmentVariable("D2_CORPUS_DIR") is { Length: > 0 } c
                ? c : Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "corpus"));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var cases = Cases();
            if (Arg(args, "--loop=") is { } loop) await Loop(cases.First(x => x.Name == loop), args);
            else await RunAll(cases, args);
        }
        catch (Exception e)
        {
            // Необработанное исключение C# процесс не роняет (docs/godot-notes.md) — выйти с кодом самим.
            GD.PrintErr($"BenchScene: {e}");
            code = 1;
        }
        // Выход — на всех путях: без него процесс остаётся крутить пустые кадры без сна (LowProcessorUsageModeSleepUsec = 0).
        GetTree().Quit(code);
    }

    private async Task RunAll(List<SceneCase> cases, string[] args)
    {
        string header = $"Godot {Engine.GetVersionInfo()["string"]}, debug={OS.IsDebugBuild()}, csharp={CSharpConfig}, headless";
        GD.Print(header);
        var only = (Arg(args, "--filter=") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var results = new global::Godot.Collections.Dictionary();
        foreach (var one in cases.Where(x => only.Length == 0 || only.Contains(x.Name)))
            results[one.Name] = await Run(one);
        foreach (var (name, note) in _notes) GD.Print($"note {name}: {note}");
        if (Arg(args, "--save=") is { } save) Save(save, header, results);
    }

    private static string CSharpConfig =>
        typeof(BenchScene).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            is [DebuggableAttribute { IsJITOptimizerDisabled: true }] ? "Debug" : "Release";

    private List<SceneCase> Cases()
    {
        string human = Path.Combine(_corpus, "20260919-human");
        string t48 = Directory.GetFiles(human, "*-t48-*.sg").Order(StringComparer.Ordinal).Last();
        var cases = new List<SceneCase>
        {
            // Открытие экрана карты: MapScreen._Ready (MapScene, вода, объекты, облака, панели, мини-карта) — синхронная часть;
            // и до готовности земли под камерой (куски собираются в фоновых потоках, текстуры — на главном).
            new("map_open_t48", () => OpenMap(() => SaveSession(t48), groundReady: false)),
            new("map_open_ground_t48", () => OpenMap(() => SaveSession(t48), groundReady: true)),
            new("map_open_ground_s142", () => OpenMap(() => ScenarioSession("S142SC0000"), groundReady: true)),
            // То же с пустыми кэшами картинок порта (GfxTextures, UiArt) перед каждым замером — первое открытие карты в партии: разбор
            // файлов графики (WebP/PNG) целиком в замере. Кэш файлов ОС при этом тёплый.
            new("map_open_cold_t48", () => Cold(() => OpenMap(() => SaveSession(t48), groundReady: false))),
            new("map_open_ground_cold_t48", () => Cold(() => OpenMap(() => SaveSession(t48), groundReady: true))),
        };
        cases.AddRange(MapFrameCases(t48));
        cases.Add(new("ground_compose_view_t48", () => ComposeView(t48)));
        cases.Add(new("ai_turn_view_t48", () => AiTurnOnView(t48)));
        cases.Add(new("ai_turn_logic_t48", () => AiTurnLogic(t48)));
        // Ход ИИ с показом на сохранении порта (D2_BENCH_SAVE — путь к .d2save, например сохранение дня из каталога зонда; Г20).
        if (System.Environment.GetEnvironmentVariable("D2_BENCH_SAVE") is { Length: > 0 } d2save)
            cases.Add(new("ai_turn_shown_save", () => AiTurnShown(d2save)));
        cases.AddRange(BattleCases());
        foreach (string screen in new[] { "DLG_CITY_STACK", "DLG_OPTIONS", "DLG_BATTLE_A", "DLG_CAST_SPELL", "DLG_MAIN_MENU" })
            cases.Add(new($"ui_open_{screen}", () => OpenUi(screen)));
        return cases;
    }

    // --- карта -------------------------------------------------------------------------------------------------

    private static GameSession SaveSession(string sg)
    {
        var looks = MapLooksLoader.Load(sg);
        var player = looks.Map.Players.Values.First(p => p.IsHuman);
        var info = GameInfo.FromMap(Path.GetFileNameWithoutExtension(sg), looks) with { Humans = [player.Id] };
        return new GameSession(new GameState(info, looks), GameData.Content.MapArt, GameData.Book);
    }

    private static GameSession ScenarioSession(string id)
    {
        string path = ScenarioFile.PathOf(GameData.ScenariosDir, id);
        var scenario = ScenarioFile.Read(path);
        var looks = scenario.Looks;
        var state = new GameState(GameInfo.FromMap(id, looks), looks) with
        {
            Events = scenario.Events, EventProgress = D2.Rules.Map.EventProgress.From(scenario.Events),
        };
        return new GameSession(state, GameData.Content.MapArt, GameData.Book);
    }

    private static string? ViewerOf(GameSession session) =>
        session.State.Info.Humans.FirstOrDefault() ?? session.Map.Players.Values.FirstOrDefault(p => p.IsHuman)?.Id
        ?? session.Map.Players.Keys.Order(StringComparer.Ordinal).FirstOrDefault();

    private MapScreen? _mapScreen;
    private MapView? _mapView;

    /// <summary>Открыть экран карты. Партия читается вне замера; замер — <c>AddChild</c> (синхронный <c>_Ready</c>), при
    /// <paramref name="groundReady"/> — ещё и кадры до готовности всей земли под камерой.</summary>
    private async Task<double> OpenMap(Func<GameSession> session, bool groundReady)
    {
        var s = session();
        var screen = new MapScreen { Session = s, Viewer = ViewerOf(s), Name = "BenchMap" };
        long t0 = Stopwatch.GetTimestamp();
        AddChild(screen);
        double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        var view = (MapView)screen.FindChild("MapView", true, false);
        if (groundReady)
        {
            int frames = 0;
            while (!view.GroundReady)
            {
                if (++frames > GroundTimeoutFrames) throw new TimeoutException("земля под камерой не собралась");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        }
        _notes[groundReady ? $"map_open_ground_{s.State.Info.ScenarioName}" : "map_open"] =
            $"nodes={Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} sprites={view.SpriteCount} size={s.Map.Size}";
        await Close(screen);
        return us;
    }

    /// <summary>Замер с пустыми кэшами картинок: предыдущий экран уже снят (<see cref="Close"/>), так что отпустить текстуры можно.</summary>
    private static Task<double> Cold(Func<Task<double>> sample)
    {
        GfxTextures.Release();
        UiArt.ForgetImages();
        return sample();
    }

    private async Task Close(Node node)
    {
        RemoveChild(node);
        node.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Кадр карты: стоим (анимации воды, облаков, объектов, фон мест) и прокрутка камеры на клетку каждый кадр
    /// по кругу (куски земли — подгрузка, текстуры на главном потоке).</summary>
    private IEnumerable<SceneCase> MapFrameCases(string t48)
    {
        async Task Open()
        {
            var s = SaveSession(t48);
            _mapScreen = new MapScreen { Session = s, Viewer = ViewerOf(s), Name = "BenchMap" };
            AddChild(_mapScreen);
            _mapView = (MapView)_mapScreen.FindChild("MapView", true, false);
            for (int i = 0; i < GroundTimeoutFrames && !_mapView.GroundReady; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _notes["map_frame"] = $"nodes={Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} sprites={_mapView.SpriteCount} " +
                                  $"draw_calls={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} (headless — 0)";
        }
        async Task Shut()
        {
            if (_mapScreen is not null) await Close(_mapScreen);
            _mapScreen = null;
            _mapView = null;
        }
        yield return new("map_frame_idle_t48", () => FrameMean("map_frame_idle_t48", null), Open, Shut);
        // Кадр боя поверх карты, как в партии (MapScreen.OpenBattle: карта скрыта, экран боя — ребёнок экрана карты).
        async Task OpenBattleOverMap()
        {
            await Open();
            var s = _mapScreen!.Session ?? throw new InvalidOperationException("карта без партии");
            string viewer = ViewerOf(s)!;
            var mine = s.Map.Stacks.Values.First(x => x.Owner.Equals(viewer, StringComparison.OrdinalIgnoreCase));
            var other = s.Map.Stacks.Values.First(x => !x.Owner.Equals(viewer, StringComparison.OrdinalIgnoreCase));
            typeof(MapScreen).GetMethod("OpenBattleNow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(_mapScreen, [mine.Id, other.Id, (int?)0]);
            for (int i = 0; i < 10; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        yield return new("battle_over_map_frame_t48", () => FrameMean("battle_over_map_frame_t48", null), OpenBattleOverMap, Shut);
        // Пересборка картинок объектов (MapView.Reload: MapScene.Build + MapObjects.Build) — после каждого шага отряда и хода ИИ.
        // Текстуры уже в кэше вида: замер — сборка сцены и пересоздание узлов Sprite2D.
        yield return new("map_reload_t48", () =>
        {
            long t0 = Stopwatch.GetTimestamp();
            _mapView!.Reload();
            return Task.FromResult(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
        }, Open, Shut);
        // Восемь направлений по восемь шагов (8 клеток = кусок земли 256 точек по горизонтали): за замер камера обходит круг и
        // возвращается на место, каждый замер одинаков.
        (int, int)[] ring = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
        yield return new("map_frame_scroll_t48",
                         () => FrameMean("map_frame_scroll_t48", i => { var (dx, dy) = ring[i / 8 % ring.Length]; _mapView!.Step(dx, dy); }),
                         Open, Shut);
    }

    /// <summary>Среднее время кадра (мкс) за <see cref="Frames"/> кадров: между соседними <c>process_frame</c> при выключенном сне
    /// headless — весь шаг главного цикла на CPU (process, physics, заглушка рендера). <c>TIME_PROCESS</c> не годится: движок
    /// обновляет его раз в секунду максимумом за секунду (проверено прогоном: 15 одинаковых замеров). <paramref name="perFrame"/> —
    /// действие перед кадром; самый долгий кадр — в заметки.</summary>
    private async Task<double> FrameMean(string name, Action<int>? perFrame)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        long start = Stopwatch.GetTimestamp(), last = start;
        double worst = 0;
        for (int i = 0; i < Frames; i++)
        {
            perFrame?.Invoke(i);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            long now = Stopwatch.GetTimestamp();
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(last, now).TotalMicroseconds);
            last = now;
        }
        _notes[$"{name} worst_frame_us"] = $"{Math.Max(worst, _worst.GetValueOrDefault(name)):F0}";
        _worst[name] = Math.Max(worst, _worst.GetValueOrDefault(name));
        return Stopwatch.GetElapsedTime(start, last).TotalMicroseconds / Frames;
    }

    private readonly Dictionary<string, double> _worst = new(StringComparer.Ordinal);

    /// <summary>Ход ИИ через вид карты: <see cref="MapView.EndTurn"/> (рабочий поток + заявки главному на каждый ход отряда, бой,
    /// событие) до <see cref="MapView.AiTurnFinished"/> (с перерисовкой карты). Показ ходов выключен (<c>AiAnimate</c>), скорость
    /// пауз — 1000×: остаются логика хода, передачи между потоками (заявка — не чаще раза за кадр) и перестройка картинок. Вид без
    /// <see cref="MapScreen"/>: подписчика боя нет, бой с человеком — автобой ядра, как в консольном <c>ai_endturn_t48</c>.</summary>
    private async Task<double> AiTurnOnView(string t48)
    {
        var s = SaveSession(t48);
        var view = new MapView { Standalone = false, Name = "BenchAiView" };
        AddChild(view);
        view.Show(s, "bench", ViewerOf(s));
        view.AiAnimate = false;
        view.AiSpeed = 1000f;
        for (int i = 0; i < GroundTimeoutFrames && !view.GroundReady; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var done = new TaskCompletionSource();
        view.AiTurnFinished += () => done.TrySetResult();
        ulong frame0 = Engine.GetProcessFrames();
        long t0 = Stopwatch.GetTimestamp();
        view.EndTurn();
        await done.Task;
        double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        _notes["ai_turn_view_t48"] = $"frames={Engine.GetProcessFrames() - frame0} day={s.Map.Turn}";
        // Продолжение идёт прямо из MapView._Process (событие AiTurnFinished) — снимать вид только со следующего кадра.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await Close(view);
        return us;
    }

    /// <summary>Тот же ход ИИ без вида — чистая логика в сборке Godot (Debug): разница с <c>ai_turn_view_t48</c> — цена вида,
    /// разница с консольным <c>ai_endturn_t48</c> (Release) — цена Debug-сборки.</summary>
    private static Task<double> AiTurnLogic(string t48)
    {
        var s = SaveSession(t48);
        long t0 = Stopwatch.GetTimestamp();
        s.EndTurn();
        return Task.FromResult(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
    }

    /// <summary>Ход ИИ с показом, как у игрока: <see cref="MapView.AiAnimate"/>, скорость противника 3 (<see cref="MapSettings.SpeedFactor"/>),
    /// зритель — человек сохранения порта <c>.d2save</c> (<c>D2_BENCH_SAVE</c>). Замер — от <see cref="MapView.EndTurn"/> до
    /// <see cref="MapView.AiTurnFinished"/>: расчёт ИИ, шаги отрядов по ключевым кадрам, паузы событий и работа главного потока в них; бой с
    /// человеком — автобой ядра (подписчика экрана боя нет). Самый долгий кадр — в заметку (<c>max_frame</c>). Г20.</summary>
    private async Task<double> AiTurnShown(string path)
    {
        var state = GameSave.Read(path);
        var s = new GameSession(state, GameData.Content.MapArt, GameData.Book);
        var view = new MapView { Standalone = false, Name = "BenchAiShown" };
        AddChild(view);
        view.Show(s, "bench", state.Info.Humans.FirstOrDefault());
        view.AiAnimate = true;
        view.AiSpeed = MapSettings.SpeedFactor(ShownOpponentSpeed);
        for (int i = 0; i < GroundTimeoutFrames && !view.GroundReady; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool finished = false;
        view.AiTurnFinished += () => finished = true;
        double worst = 0;
        var pause0 = GC.GetTotalPauseDuration();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long t0 = Stopwatch.GetTimestamp(), last = t0;
        view.EndTurn();
        while (!finished)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            long now = Stopwatch.GetTimestamp();
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(last, now).TotalMicroseconds);
            last = now;
        }
        double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        _worst["ai_turn_shown_save"] = Math.Max(worst, _worst.GetValueOrDefault("ai_turn_shown_save"));
        // Паузы сборки мусора останавливают и главный поток (ожидание в точке опроса GC — 22 % времени MapView._Process в профиле Г20).
        _notes["ai_turn_shown_save"] = $"day={state.Looks.Map.Turn} worst_frame_us={_worst["ai_turn_shown_save"]:F0} " +
            $"last_gc_pause_ms={(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:F0} " +
            $"last_gc={GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}";
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await Close(view);
        return us;
    }

    /// <summary>Скорость противника в партии пользователя 15.10 (<c>settings противник=3</c> в журнале зонда).</summary>
    private const int ShownOpponentSpeed = 3;

    private GroundArt? _groundArt;

    /// <summary>Сборка земли без потоков: все куски 256×256, покрывающие вид 1024×640 в центре карты, свежим сборщиком
    /// (картинки узоров уже в памяти — как в игре после первого открытия). Тумана нет.</summary>
    private Task<double> ComposeView(string t48)
    {
        var map = MapLooksLoader.Load(t48).Map;
        _groundArt ??= new GroundArt(GameData.Content.Gfx, GameData.GfxRoot);
        var chunks = new GroundChunks(new GroundComposer(MapSource.GroundCells(map), map.MapSeed, _groundArt), 256, capacity: 96);
        var (cx, cy) = GroundGeometry.CellOrigin(map.Size / 2, map.Size / 2);
        var keys = chunks.Covering(cx - 512, cy - 320, 1024, 640).ToList();
        long t0 = Stopwatch.GetTimestamp();
        long pixels = 0;
        foreach (var key in keys) pixels += chunks.Get(key).Pixels.Length;
        double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        _notes["ground_compose_view_t48"] = $"chunks={keys.Count} bytes={pixels}";
        return Task.FromResult(us);
    }

    // --- бой ---------------------------------------------------------------------------------------------------

    private BattleScreen? _battle;

    private IEnumerable<SceneCase> BattleCases()
    {
        string journal = Path.Combine(_corpus, "sample", "one_battle.jsonl");
        var rules = RulesLoader.Load(GameData.RulesDir);
        BattleState Prepared()
        {
            var battle = CorpusReader.Battles(journal).First(b => b.End is not null);
            return CorpusBattleLoader.TryLoad(battle, rules, out var state, out string reason) ? state : throw new InvalidDataException(reason);
        }
        BattleScreen New() => new() { Prepared = Prepared(), Rules = rules, Name = "BenchBattle" };

        // Открытие экрана боя: разметка DLG_BATTLE_A, поле, анимации юнитов, панели.
        yield return new("battle_open", async () =>
        {
            var screen = New();
            long t0 = Stopwatch.GetTimestamp();
            AddChild(screen);
            double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            _notes["battle_open"] = $"nodes={Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}";
            await Close(screen);
            return us;
        });
        // Первый бой партии: открытие с пустыми кэшами картинок (разбор файлов графики в замере).
        yield return new("battle_open_cold", () => Cold(async () =>
        {
            var screen = New();
            long t0 = Stopwatch.GetTimestamp();
            AddChild(screen);
            double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            _notes["battle_open_cold"] = $"texture_mem={Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed)} gfx_bytes={GfxTextures.Bytes}";
            await Close(screen);
            return us;
        }));
        // Кадр экрана боя в ожидании хода человека (анимации стояния, курсор).
        yield return new("battle_frame_idle", () => FrameMean("battle_frame_idle", null),
            async () => { _battle = New(); AddChild(_battle); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); },
            async () => { if (_battle is not null) await Close(_battle); _battle = null; });
        // Показ шагов автобоя с окном (без окна показ мгновенный): среднее время кадра за 64 кадра показа.
        yield return new("battle_frame_playback", () => FrameMean("battle_frame_playback", null),
            async () =>
            {
                _battle = New();
                AddChild(_battle);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                _battle.Session.Auto = true;
                _battle.Continue();
            },
            async () => { if (_battle is not null) await Close(_battle); _battle = null; });
        // Автобой на открытом экране (BattleScreen.AutoFinish: модель доигрывает бой и экран обновляется).
        yield return new("battle_autofinish_screen", async () =>
        {
            var screen = New();
            AddChild(screen);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            long t0 = Stopwatch.GetTimestamp();
            screen.AutoFinish();
            double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            await Close(screen);
            return us;
        });
    }

    // --- окна UI -----------------------------------------------------------------------------------------------

    /// <summary>Открытие экрана разметки (<see cref="UiScreenView"/> под хозяином): разбор экрана, кадры, надписи, узлы.</summary>
    private async Task<double> OpenUi(string screen)
    {
        var view = new UiScreenView { Hosted = screen, Name = screen };
        long t0 = Stopwatch.GetTimestamp();
        AddChild(view);
        double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        _notes[$"ui_open_{screen}"] = $"nodes_in_screen={CountNodes(view)}";
        await Close(view);
        return us;
    }

    private static int CountNodes(Node node) => 1 + node.GetChildren().Sum(CountNodes);

    // --- харнесс -----------------------------------------------------------------------------------------------

    private async Task<global::Godot.Collections.Array> Run(SceneCase one)
    {
        if (one.Setup is not null) await one.Setup();
        try
        {
            for (int i = 0; i < Warmup; i++) await one.Sample();
            var samples = new List<long>();
            for (int i = 0; i < Runs; i++)
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                samples.Add((long)Math.Round(await one.Sample()));
            }
            samples.Sort();
            long median = samples[samples.Count >> 1];
            GD.Print($"{one.Name,-26} median {median,10} us | min {samples[0],10} | max {samples[^1],10} | " +
                     $"IQR {samples[samples.Count / 4]}-{samples[3 * samples.Count / 4]}");
            var array = new global::Godot.Collections.Array();
            foreach (long s in samples) array.Add(s);
            return array;
        }
        finally
        {
            if (one.Teardown is not null) await one.Teardown();
        }
    }

    private async Task Loop(SceneCase one, string[] args)
    {
        double seconds = double.Parse(Arg(args, "--seconds=") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
        GD.Print($"loop {one.Name} pid={OS.GetProcessId()}");
        if (one.Setup is not null) await one.Setup();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        int n = 0;
        while (DateTime.UtcNow < until) { await one.Sample(); n++; }
        if (one.Teardown is not null) await one.Teardown();
        GD.Print($"loop done runs={n}");
    }

    private static void Save(string path, string header, global::Godot.Collections.Dictionary results)
    {
        string full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), path));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var report = new global::Godot.Collections.Dictionary { ["header"] = header, ["iterations"] = 1, ["results"] = results };
        File.WriteAllText(full, Json.Stringify(report, "\t"));
        GD.Print($"saved {full}");
    }

    private static string? Arg(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal)) is { } a ? a[prefix.Length..] : null;
}
