using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Menu;
using D2.Data.Play;
using D2.Rules.Rng;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Бросок CRT новой партии (<c>0x42ae90</c> → <c>0x42af9b</c>, <see cref="NewGame"/>): поток игры — свой поток ОС со
/// <c>srand(1)</c>, и до строки <c>game_phase new</c> он делает ровно один <c>rand()</c> — описание человека по его расе и полу
/// (<c>Tplayer.dbf</c>, одна строка на расу: текст тот же, шаг потока есть).</summary>
public class NewGameCrtTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Новая партия порта (одиночная и «горячий стул») начинает поток CRT с одного шага после <c>srand(1)</c> — первый
    /// бросок ИИ (тип найма <c>0x44c137</c>) идёт вторым значением потока (18467), как у движка.</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewGame_CrtStreamOneStepAfterSrand1(bool hotseat)
    {
        string scenario = ScenarioFile.PathOf(Path.Combine(Data, "scenarios"), "Power to Corrupt");
        Skip.IfNot(File.Exists(scenario), "нет сценария порта");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var setup = new NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == "Power to Corrupt"), Difficulty = 1, Race = 3,
            Hotseat = hotseat ? [new HotseatSeat(3, "", 0, 0), new HotseatSeat(2, "", 0, 1)] : [],
        };
        var state = NewGame.Start(ScenarioFile.Read(scenario), setup, content.Catalog, book, new Random(1));
        var engine = new CrtRand(1);
        engine.Next();
        var session = new GameSession(state, content.MapArt, book);
        Assert.Equal(engine.State, session.Crt.State);
        Assert.Equal(18467, session.Crt.Next());
    }
}

/// <summary>Тот же бросок по журналам корпуса: у каждой новой партии поток игры (первый <c>crt_rand</c> — 41, начало <c>srand(1)</c>)
/// до своей строки <c>game_phase new</c> (в журналах до схемы 7 — <c>enter</c>) делает ровно один <c>rand()</c>. Загруженная партия
/// (<c>game_phase load</c>/<c>enter</c> раньше первого броска потока) не сверяется — у неё описание не бросается (номер дня не 0).
/// Корпус 18.10: новых партий 98 — у всех один бросок; загруженных 9.</summary>
[Trait("Category", "Corpus")]
public class NewGameCrtCorpusTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void NewGame_OneCrtDrawBeforeGamePhaseNew()
    {
        string root = GameSaveTests.CorpusDir();
        Skip.IfNot(Directory.Exists(root), "нет корпуса");
        int games = 0, loaded = 0;
        var bad = new List<string>();
        foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
            foreach (string journal in Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal))
            {
                var events = JournalCache.Read(journal, ["crt_rand", "game_phase"]).OrderBy(e => e.Seq).ToList();
                var phases = events.OfType<GamePhaseEvent>().ToList();
                var main = phases.Select(p => p.Tid).ToHashSet();   // фазы игры пишет поток интерфейса — у него свой посев CRT
                foreach (var thread in events.OfType<CrtRandEvent>().Where(c => !main.Contains(c.Tid)).GroupBy(c => c.Tid))
                {
                    var first = thread.First();
                    var next = phases.FirstOrDefault(p => p.Seq > first.Seq);
                    if (next is null || next.What is not ("new" or "enter"))
                    {
                        loaded++;
                        continue;
                    }
                    games++;
                    int before = thread.Count(c => c.Seq < next.Seq);
                    if (before != 1 || first.R != 41)
                        bad.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(journal)} tid {thread.Key}: до game_phase {next.What} (seq {next.Seq}) " +
                                $"бросков {before}, первый {first.R}");
                }
            }
        }
        output.WriteLine($"новых партий {games}, загруженных {loaded}; не один бросок до начала: {bad.Count}");
        foreach (string b in bad) output.WriteLine("  " + b);
        Skip.If(games == 0, "в корпусе нет журналов с crt_rand");
        Assert.Empty(bad);
    }
}
