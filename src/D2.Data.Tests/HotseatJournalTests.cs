using System.Text.Json;
using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui.Windows;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Партия «горячего стула» пользователя 28.09 (<c>corpus/20260929-hotseat</c>, карта vertigo: Легионы <c>S073PL0009</c> и Нежить
/// <c>S073PL000a</c>, оба люди; памятка <c>docs/tasks/hotseat-load-memo.md</c>) против порта: окна после загрузки и окно защитника в бою
/// двух людей. Сохранение «hs-load» — копия <c>t13-025</c>, последнее перед боем людей — <c>t20-040</c>.
/// </summary>
[Trait("Category", "Corpus")]
public class HotseatJournalTests(ITestOutputHelper output)
{
    private const string Run = "20260929-hotseat", Legions = "S073PL0009", Undead = "S073PL000a";

    /// <summary>Строки журнала, нужные сверке: открытия окон, тексты, щелчки, начало боя, фаза партии.</summary>
    private sealed record Line(long Seq, string Ev, JsonElement Data);

    private static readonly Lazy<IReadOnlyList<Line>> Journal = new(() =>
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), Run, "passive-001.jsonl");
        if (!File.Exists(path)) return [];
        string[] wanted = ["\"ev\":\"ui_open\"", "\"ev\":\"ui_text\"", "\"ev\":\"ui_click\"", "\"ev\":\"battle_start\"", "\"ev\":\"game_phase\""];
        var lines = new List<Line>();
        foreach (string raw in File.ReadLines(path))
        {
            if (!wanted.Any(raw.Contains)) continue;
            var root = JsonDocument.Parse(raw).RootElement;
            lines.Add(new Line(root.GetProperty("seq").GetInt64(), root.GetProperty("ev").GetString() ?? "", root.Clone()));
        }
        return lines;
    });

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules"))));

    private static IReadOnlyList<Line> Lines()
    {
        Skip.If(Journal.Value.Count == 0, $"нет журнала {Run}");
        return Journal.Value;
    }

    private static GameSession FromSave(string save)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), Run, save);
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("vertigo", looks), looks), Shared.Value.Content.MapArt, Shared.Value.Book);
    }

    private static string Str(Line l, string name) => l.Data.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static bool Popup(Line l) => l.Ev == "ui_open" && Str(l, "kind") == "popup";

    /// <summary>
    /// Загрузка (<c>docs/day-cycle.md</c> §19): брифинг, затем до первого «Конца хода» — только окно передачи хода и сообщение «Начало
    /// задания, день N» (<c>X005TA0828</c> — в хотсите, ранг 2 → 1); ни начала хода, ни окна навыка, ни попапов, ни итогов. Окно навыка
    /// лидера, повысившегося в день 13 до записи, приходит только в следующее начало хода Легионов (после двух окон начала хода). Порт:
    /// в загруженном «hs-load» у Легионов навык ждёт (<see cref="UpgradeLeaderQueue"/> не пуст) — окна нет потому, что после загрузки
    /// очередь навыков не собирается (<c>MapScreen.QueueUpgrades</c>), текст сообщения — по <see cref="GameSession.Hotseat"/>
    /// (<c>MapScreen.ResumeMessage</c>).
    /// </summary>
    [SkippableFact]
    public void Load_ResumeWindows_UpgradeOnlyNextTurn()
    {
        var lines = Lines();
        long load = lines.First(l => l.Ev == "game_phase" && Str(l, "what") == "load").Seq;
        var after = lines.Where(l => l.Seq > load).ToList();
        int briefing = after.FindIndex(l => l.Ev == "ui_open" && Str(l, "name") == "DLG_SCENARIO_BRIEFING");
        int end = after.FindIndex(l => l.Ev == "ui_click" && Str(l, "element") == "BTN_END_TURN");
        Assert.True(briefing >= 0 && end > briefing, "брифинг и «Конец хода» после загрузки");
        var popups = after.Take(end).Where(Popup).Select(l => Str(l, "name")).ToList();
        output.WriteLine($"после загрузки: {string.Join(" → ", popups)}");
        Assert.Equal(["DLG_HOTSEAT_POPUP", "DLG_MESSAGE_BOX"], popups);
        Assert.Contains(after.Take(end), l => l.Ev == "ui_text" && Str(l, "id") == "X005TA0828");
        Assert.DoesNotContain(after.Take(end), l => l.Ev == "ui_text" && Str(l, "id") == "X005TA0079");

        var later = after.Skip(end).Where(Popup).Select(l => Str(l, "name")).ToList();
        int upgrade = later.IndexOf("DLG_UPGRADE_LEADER");
        output.WriteLine($"дальше до навыка: {string.Join(" → ", later.Take(upgrade + 1))}");
        Assert.Equal(2, later.Take(upgrade).Count(n => n == "DLG_BEGIN_TURN"));
        Assert.Equal("DLG_BEGIN_TURN", later[upgrade - 1]);

        var session = FromSave("vertigo-heretic-undead-t13-025.sg");
        Assert.True(session.Hotseat);
        Assert.Equal(13, session.Map.Turn);
        Assert.NotEmpty(UpgradeLeaderQueue.For(session, Legions));
    }

    /// <summary>
    /// Бой двух людей (<c>docs/day-cycle.md</c> §18): Нежить нападает на отряд Легионов — между началом боя и экраном боя окно
    /// <c>DLG_HOTSEAT_POPUP</c> режима 2 (<c>X005TA0893</c> «… атакован! Вы должны защищаться!»); в боях Легионов с нейтралами окна нет.
    /// Порт: <see cref="HotseatCalls.Defender"/> зовёт Легионы к этому бою, к бою с нейтралом — никого; обе стороны — люди.
    /// </summary>
    [SkippableFact]
    public void Defender_CalledBeforeHumanBattle_NotBeforeNeutral()
    {
        var lines = Lines();
        var starts = lines.Where(l => l.Ev == "battle_start" && l.Data.GetProperty("round").GetInt32() == 0).ToList();
        int human = 0, neutral = 0;
        foreach (var start in starts)
        {
            var until = lines.SkipWhile(l => l.Seq <= start.Seq)
                             .TakeWhile(l => !(l.Ev == "ui_open" && Str(l, "name") == "DLG_BATTLE_B")).ToList();
            bool called = until.Any(l => Popup(l) && Str(l, "name") == "DLG_HOTSEAT_POPUP")
                          && until.Any(l => l.Ev == "ui_text" && Str(l, "id") == "X005TA0893");
            bool both = Str(start, "defender_player") is Legions or Undead;
            output.WriteLine($"бой {Str(start, "attacker_group")} → {Str(start, "defender_group")} ({Str(start, "defender_player")}): окно={called}");
            Assert.Equal(both, called);
            if (both) human++;
            else neutral++;
        }
        Assert.Equal(1, human);
        Assert.True(neutral > 0);

        var session = FromSave("vertigo-heretic-undead-t20-040.sg");
        var call = HotseatCalls.Defender(session, "S073KC0000", id => id);
        Assert.NotNull(call);
        Assert.Equal(Legions, call.Value.Player.Id, ignoreCase: true);
        Assert.True(HotseatCalls.BothHuman(session, "S073KC0001", "S073KC0000"));
        var wild = session.Map.Stacks.Values.First(s => !session.State.Info.Humans.Contains(s.Owner, StringComparer.OrdinalIgnoreCase));
        Assert.Null(HotseatCalls.Defender(session, wild.Id, id => id));
    }
}
