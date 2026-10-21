using D2.Bench;
using D2.Data.Battle;
using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Dbf;
using D2.Data.Gfx;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Battle;
using D2.Rules.Rng;

// Горячие пути чистой логики порта (этап 6 пайплайна gdscript-knowledge). Корпус — D2_CORPUS_DIR (только чтение), данные —
// data/ репозитория. Кадры и графика Godot — в godot/bench (BenchScene).
//   dotnet run -c Release --project tools/bench/D2.Bench -- --save=bench/logic-before.json
//   ... -- --loop=ai_endturn_t48 --seconds=40     (крутить один случай, чтобы снять стеки dotnet-stack)
//   D2_BENCH_SAVE=путь.d2save ... -- --filter=ai_endturn_save   (ход ИИ на сохранении порта)

string repo = Repo();
string data = Path.Combine(repo, "data");
string corpus = Environment.GetEnvironmentVariable("D2_CORPUS_DIR") is { Length: > 0 } c ? c : Path.Combine(repo, "corpus");
string human = Path.Combine(corpus, "20260919-human");
string Save(string day) => Directory.GetFiles(human, $"*-{day}-*.sg").Order(StringComparer.Ordinal).Last();
string t48 = Save("t48"), t30 = Save("t30"), t01 = Save("t01");

var content = GameContent.Load(data);
var book = GameBook.Load(Path.Combine(data, "rules"));
var rules = RulesLoader.Load(Path.Combine(data, "rules"));

GameSession Session(string sg)
{
    var looks = MapLooksLoader.Load(sg);
    var player = looks.Map.Players.Values.First(p => p.IsHuman);
    return new GameSession(new GameState(new GameInfo("PtC", "PtC", [player.Id], looks.Map.Limits?.DifficultyGame ?? 1, 1), looks),
                           content.MapArt, book);
}

// Бои корпуса для автобоя: законченные бои, которые загрузчик собирает без сохранений.
var battles = new List<D2.Data.Corpus.Battle>();
foreach (string journal in new[] { Path.Combine(corpus, "sample", "one_battle.jsonl"),
                                   Path.Combine(corpus, "20260909-arena1", "map-001.jsonl"),
                                   Path.Combine(corpus, "20260909-arena2", "map-001.jsonl") })
    if (File.Exists(journal))
        battles.AddRange(CorpusReader.Battles(journal).Where(b => b.End is not null && CorpusBattleLoader.TryLoad(b, rules, out _, out _)));

var cases = new List<BenchCase>
{
    // Загрузка: разбор .sg большой карты (Растленная власть, 96×96, день 48) и сценария порта (JSON).
    BenchCase.Plain("load_sg_t48", () => MapLooksLoader.Load(t48).Map.Stacks.Count),
    BenchCase.Plain("load_scenario_ptc", () => ScenarioFile.Read(ScenarioFile.PathOf(Path.Combine(data, "scenarios"), "Power to Corrupt")).Looks.Map.Size),
    BenchCase.Plain("load_scenario_s142", () => ScenarioFile.Read(ScenarioFile.PathOf(Path.Combine(data, "scenarios"), "S142SC0000")).Looks.Map.Size),   // самая большая карта данных, 120×120
    BenchCase.Plain("load_content", () => GameContent.Load(data).Screens.Count),
    BenchCase.Plain("load_rules_book", () => GameBook.Load(Path.Combine(data, "rules")).GetHashCode()),
    // То же с пустым кэшем таблиц (GameTables.Read кэширует на процесс) — первая загрузка партии.
    BenchCase.Plain("load_rules_book_cold", () => { GameTables.ClearCache(); return GameBook.Load(Path.Combine(data, "rules")).GetHashCode(); }),
    // Сборка списка картинок карты (MapScene) — то, что MapView.Show/Reload делает на каждую смену объектов.
    new("mapscene_build_t48", () => MapLooksLoader.Load(t48),
        o => { var looks = (MapLooks)o!; var viewer = looks.Map.Players.Values.First(p => p.IsHuman).Id;
               return MapScene.Build(looks, content.MapArt, content.Gfx, true, viewer).Sprites.Count; }),
    // Ход ИИ целиком без экрана (GameSession.EndTurn: все игроки ИИ, бои автобоем, смена дня).
    new("ai_endturn_t01", () => Session(t01), o => ((GameSession)o!).EndTurn().Events.Count),
    new("ai_endturn_t30", () => Session(t30), o => ((GameSession)o!).EndTurn().Events.Count),
    new("ai_endturn_t48", () => Session(t48), o => ((GameSession)o!).EndTurn().Events.Count),
    // A*: все отряды карты к восьми точкам (углы, середины сторон, центр) — как запросы пути ИИ и щелчков человека.
    new("pathfind_t48_all_stacks", () => Session(t48), o => Paths((GameSession)o!)),
    // Автобой: все законченные бои образцов корпуса (sample + арены), каждый на свежем состоянии.
    new("battle_autoplay_corpus", () => battles.Select(Load).ToList(), o => PlayAll((List<BattleState>)o!)),
};

// Ход ИИ на сохранении порта (.d2save, например сохранение дня из каталога зонда; D2_BENCH_SAVE): чистый расчёт без показа — для
// сверки с полем ms строки ai_turn журнала зонда, где ещё и ожидание показа (Г20).
if (Environment.GetEnvironmentVariable("D2_BENCH_SAVE") is { Length: > 0 } d2save)
{
    var saved = GameSave.Read(d2save);
    cases.Add(new("ai_endturn_save", () => new GameSession(saved, content.MapArt, book), o => ((GameSession)o!).EndTurn().Events.Count));
}

if (Harness.Arg(args, "--loop=") is { } loop)
{
    var one = cases.First(x => x.Name == loop);
    double seconds = double.Parse(Harness.Arg(args, "--seconds=") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
    Console.WriteLine($"loop {loop} pid={Environment.ProcessId}");
    var until = DateTime.UtcNow.AddSeconds(seconds);
    int n = 0;
    while (DateTime.UtcNow < until) { one.Run(one.Prepare()); n++; }
    Console.WriteLine($"loop done runs={n}");
    return 0;
}
Console.WriteLine($"battles={battles.Count} map={MapLooksLoader.Load(t48).Map.Size}");
return Harness.Run(cases, args);

BattleState Load(D2.Data.Corpus.Battle b) =>
    CorpusBattleLoader.TryLoad(b, rules, out var state, out string reason) ? state : throw new InvalidDataException(reason);

int PlayAll(List<BattleState> states)
{
    int actions = 0;
    foreach (var state in states)
        actions += new BattleSession(state, new BattleSim(state, new EngineRng(), rules.Constants), rules.Constants) { Auto = true }.PlayOut().Count;
    return actions;
}

static int Paths(GameSession session)
{
    var map = session.Map;
    int n = map.Size, found = 0;
    (int, int)[] targets = [(1, 1), (n / 2, 1), (n - 2, 1), (1, n / 2), (n / 2, n / 2), (n - 2, n / 2), (1, n - 2), (n - 2, n - 2)];
    foreach (var stack in map.Stacks.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        foreach (var (x, y) in targets)
            if (session.PathTo(stack, x, y).Points.Count > 0) found++;
    return found;
}

static string Repo()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "Disciples.sln"))) d = d.Parent;
    return d?.FullName ?? throw new DirectoryNotFoundException("нет Disciples.sln выше каталога сборки");
}
