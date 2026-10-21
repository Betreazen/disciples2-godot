using D2.Data.Content;
using D2.Data.Leaders;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Вывоз героя после победы (<c>docs/day-cycle.md</c> §14): запись героя из отряда карты, кандидаты и отказы, окно
/// <c>DLG_LEADER_PICKER</c>.</summary>
[Trait("Category", "Corpus")]
public class LeaderExportTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules"))));

    /// <summary>Сохранение карты <c>ui-end-win</c> перед боем (корпус <c>20260922-ui</c>): Рыцарь на Пегасе Империи рядом с
    /// Крестьянином; после победы его вывезли — безымянный <c>.ldr</c> в <c>game-dev\Exports</c>.</summary>
    private static GameSession EndWin()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260922-ui", "ui-end-win-human-t01-006.sg");
        Skip.IfNot(File.Exists(path), $"нет {path}");
        var looks = MapLooksLoader.Load(path);
        return new GameSession(new GameState(GameInfo.FromMap("x", looks), looks), Shared.Value.Content.MapArt, Shared.Value.Book);
    }

    private static MapStack Knight(GameSession session) =>
        session.Map.Stacks.Values.Single(s => session.Map.Units[s.LeaderId].TypeId.Equals("G000UU0019", StringComparison.OrdinalIgnoreCase)
                                              && s.Inside.Length == 0);

    /// <summary>Запись Рыцаря совпадает с файлом, который написал движок, во всех полях, кроме тех, что дал бой (опыт, победы) и
    /// названия атаки (у образца — английское).</summary>
    [SkippableFact]
    public void Knight_MatchesEngineLdr()
    {
        var sample = LdrFile.Read(File.ReadAllBytes(Path.Combine(LeaderImportTests.Exports(), ".ldr")));
        var session = EndWin();
        var got = session.ExportOf(Knight(session), _ => "Long Sword");
        var want = sample with { Xp = got.Xp, BattlesWon = got.BattlesWon };
        Assert.Equal(LeaderFile.ToJson(want), LeaderFile.ToJson(got));
        Assert.Equal((5, 1), (sample.Xp, sample.BattlesWon));   // бой с Крестьянином: +5 опыта, одна победа
    }

    /// <summary>Кандидаты — герои Империи (Рыцарь и лидер столицы); отказ: у стартового героя столицы <c>CREAT_LVL</c> = уровню
    /// сценария (3) — <c>X005TA0726</c> (так ответила игра пользователю 19.09), у Рыцаря — нет отказа.</summary>
    [SkippableFact]
    public void Candidates_And_Refusal()
    {
        var session = EndWin();
        var knight = Knight(session);
        var candidates = session.ExportCandidates(knight.Owner);
        Assert.Contains(candidates, s => s.Id == knight.Id);
        Assert.Null(session.ExportRefusal(knight));
        var capital = candidates.Single(s => s.Id != knight.Id);
        Assert.Equal(3, capital.CreationLevel);
        Assert.Equal("X005TA0726", session.ExportRefusal(capital));
        Assert.All(candidates, s => Assert.Equal(knight.Owner, s.Owner));
    }

    /// <summary>Окно: две строки (лицо и «имя, уровень, победы»); «ОК» на герое столицы — отказ; на Рыцаре — файл в каталоге и
    /// «сохранён», строка уходит; второй вывоз того же имени — «имя2»; список пуст — окно закрыто, <c>done</c> позван.</summary>
    [SkippableFact]
    public void Picker_ExportsAndCloses()
    {
        var session = EndWin();
        var knight = Knight(session);
        string dir = Path.Combine(Path.GetTempPath(), "d2-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var context = new WindowContext(session, knight.Owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"),
                                            UnitFaces.Load(Path.Combine(Data, "rules")));
            int done = 0;
            var logic = new LeaderPickerLogic(context, dir, () => done++, id => new MessageBoxLogic(id, null));
            var ui = new UiScreenState(Shared.Value.Content.Screens.Single(s => s.Id == LeaderPickerLogic.Screen));
            logic.Open(ui);
            var cards = ui.Cards("LBOX_LEADERS");
            Assert.Equal(2, cards.Count);
            int k = session.ExportCandidates(knight.Owner).ToList().FindIndex(s => s.Id == knight.Id);
            Assert.Contains("Выигранных сражений: 0", cards[k].Text, StringComparison.Ordinal);
            Assert.StartsWith("Faces:G000UU0019FACE", cards[k].Image, StringComparison.Ordinal);

            ui.Select("LBOX_LEADERS", 1 - k);
            logic.Handle(new UiClicked("BTN_OK"), ui);
            Assert.IsType<UiTell>(logic.TakeRequest());
            Assert.Empty(logic.Saved);

            ui.Select("LBOX_LEADERS", k);
            logic.Handle(new UiClicked("BTN_MANAGESTACK"), ui);
            Assert.Equal(ManageStackLogic.Screen, Assert.IsType<UiOpen>(logic.TakeRequest()).Screen);
            logic.Handle(new UiClicked("BTN_OK"), ui);
            var told = Assert.IsType<UiTell>(logic.TakeRequest());
            Assert.Contains("сохранен вместе", told.Text, StringComparison.Ordinal);
            Assert.Equal(Path.Combine(dir, ".json"), Assert.Single(logic.Saved));   // безымянный — как «.ldr» движка
            Assert.Equal("G000UU0019", LeaderFile.Read(logic.Saved[0]).UnitType);

            logic.Resume(ui);
            Assert.Single(ui.Cards("LBOX_LEADERS"));
            Assert.Equal(0, done);
            logic.Handle(new UiClicked("BTN_CANCEL"), ui);
            Assert.Equal(1, done);
            Assert.IsType<UiClose>(logic.TakeRequest());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Имя файла — имя героя; занято — с номером; знак, запрещённый в имени файла, — ошибка записи (как у движка).</summary>
    [Fact]
    public void Save_NamesLikeEngine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "d2-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hero = new ExportedLeader { Name = "Merlot", UnitType = "G000UU0021", Attack = new LeaderAttack("G000AA0021", 1, "", 80, 0, 30, 7, 40, 1) };
            Assert.Equal("Merlot.json", Path.GetFileName(LeaderStore.Save(dir, hero)));
            Assert.Equal("Merlot2.json", Path.GetFileName(LeaderStore.Save(dir, hero)));
            Assert.Equal("Merlot3.json", Path.GetFileName(LeaderStore.Save(dir, hero)));
            Assert.Equal(".json", Path.GetFileName(LeaderStore.Save(dir, hero with { Name = "" })));
            Assert.Equal("2.json", Path.GetFileName(LeaderStore.Save(dir, hero with { Name = "" })));
            Assert.Throws<IOException>(() => LeaderStore.Save(dir, hero with { Name = "a?b" }));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
