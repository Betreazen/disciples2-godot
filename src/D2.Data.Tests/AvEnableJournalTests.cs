using D2.Data.Diagnostics;

namespace D2.Data.Tests;

/// <summary>Сверка погашенных кнопок (<c>ui_enable</c>, схема 7.6; <see cref="AvJournal.UiEnables"/>) на синтетических журналах: строки зонда
/// оригинала (<c>probeui.cpp</c>, <c>0x531cca</c>) и порта — с заданным временем; поля строк порта — <see cref="AvTrace.UiEnable"/>/
/// <see cref="AvTrace.UiClick"/> через настоящий <see cref="PortJournal"/>.</summary>
public class AvEnableJournalTests
{
    /// <summary>Строки журнала с заданным временем: (мс, тело строки), <c>seq</c> — по порядку.</summary>
    private static IReadOnlyList<AvEvent> Journal(params (long T, string Line)[] lines) =>
        AvJournal.Parse(lines.Select((l, i) => $"{{\"seq\":{i + 1},\"tid\":7,\"t\":{l.T},{l.Line}}}"));

    private static string Enable(string screen, string element, bool on) =>
        $"\"ev\":\"ui_enable\",\"screen\":\"{screen}\",\"element\":\"{element}\",\"child\":-1,\"on\":{(on ? "true" : "false")}," +
        "\"top\":\"none\",\"top_class\":\"none\"";

    private static string Click(string screen, string element) =>
        $"\"ev\":\"ui_click\",\"kind\":\"button\",\"screen\":\"{screen}\",\"element\":\"{element}\",\"hotkey\":false";

    private static readonly string[] BattleButtons = ["BTN_DEFEND", "BTN_RETREAT", "BTN_WAIT", "TOG_RIGHTUNITS"];

    /// <summary>Бой у движка (партия <c>20261020-faces</c>): «Защищаться» гасит четыре кнопки панели в ту же мс, к ходу человека они зажигаются.</summary>
    private static IReadOnlyList<AvEvent> EngineBattle() => Journal(
    [
        (1000, Click("DLG_BATTLE_B", "BTN_DEFEND")),
        .. BattleButtons.Select(b => (1000L, Enable("DLG_BATTLE_B", b, false))),
        .. BattleButtons.Select(b => (2500L, Enable("DLG_BATTLE_B", b, true))),
    ]);

    private static AvDiff ClickDiff(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port, string key) =>
        AvJournal.UiEnables(original, port).Single(d => d.Check == "ui_enable_click" && d.Key == key);

    [Fact]
    public void EnableChanges_FoldsFlicker_KeepsOddRuns()
    {
        var events = Journal(
            (100, Enable("DLG_SCENARIO_BRIEFING", "BTN_REPLAY", true)),   // зажглась и погасла в одну мс (0x531cca дважды за такт) — смены нет
            (100, Enable("DLG_SCENARIO_BRIEFING", "BTN_REPLAY", false)),
            (200, Enable("DLG_STRATEGIC", "BTN_GUARD", false)),
            (215, Enable("DLG_STRATEGIC", "BTN_GUARD", true)),   // 15 мс спустя — тоже дрожание
            (230, Enable("DLG_STRATEGIC", "BTN_GUARD", false)),   // третья строка серии — итог «погасла»
            (300, Enable("DLG_BATTLE_B", "BTN_WAIT", false)),
            (900, Enable("DLG_BATTLE_B", "BTN_WAIT", true)),
            (950, "\"ev\":\"ui_enable\",\"screen\":\"DLG_X\",\"element\":null,\"child\":3,\"on\":false"));
        var changes = AvJournal.EnableChanges(events);
        Assert.Equal(["DLG_STRATEGIC/BTN_GUARD−", "DLG_BATTLE_A/BTN_WAIT−", "DLG_BATTLE_A/BTN_WAIT+", "DLG_X/#child3−"],
            changes.Select(c => $"{c.Screen}/{c.Look}"));
        var guard = changes[0];
        Assert.Equal((3L, 200L, 5L, 230L), (guard.Seq, guard.T, guard.LastSeq, guard.LastT));
    }

    [Fact]
    public void UiEnables_PortClickWithoutDimming_IsDivergence()
    {
        var still = Journal((50, Click("DLG_BATTLE_A", "BTN_DEFEND")));
        var click = ClickDiff(EngineBattle(), still, "DLG_BATTLE_A/BTN_DEFEND");
        Assert.False(click.Ok);
        Assert.Equal("BTN_DEFEND− BTN_RETREAT− BTN_WAIT− TOG_RIGHTUNITS−", click.Original);
        Assert.Contains("без смен", click.Port, StringComparison.Ordinal);
        // Кнопок порт не менял — у направлений ключи одной стороны, не сыграно.
        Assert.All(AvJournal.UiEnables(EngineBattle(), still).Where(d => d.Check == "ui_enable"), d => Assert.True(d.Ok));
    }

    [Fact]
    public void UiEnables_PortDimsLikeEngine_Matches()
    {
        var port = Journal(
        [
            (50, Click("DLG_BATTLE_A", "BTN_DEFEND")),
            .. BattleButtons.Select(b => (66L, Enable("DLG_BATTLE_A", b, false))),   // кадр спустя
            .. BattleButtons.Select(b => (900L, Enable("DLG_BATTLE_A", b, true))),
        ]);
        Assert.All(AvJournal.UiEnables(EngineBattle(), port), d => Assert.True(d.Ok, $"{d.Check} {d.Key}: {d.Original} / {d.Port}"));
        // Через AvJournal.Compare — те же сверки.
        Assert.Contains(AvJournal.Compare(EngineBattle(), port), d => d is { Check: "ui_enable_click", Key: "DLG_BATTLE_A/BTN_DEFEND", Ok: true });
    }

    [Fact]
    public void UiEnables_PortDimsAndRelightsInOneFrame_NoChange()
    {
        // Погасла и зажглась в пределах 50 мс — для глаза ничего не было: смены нет, у нажатия «без смен».
        var port = Journal(
        [
            (50, Click("DLG_BATTLE_A", "BTN_DEFEND")),
            .. BattleButtons.Select(b => (60L, Enable("DLG_BATTLE_A", b, false))),
            .. BattleButtons.Select(b => (80L, Enable("DLG_BATTLE_A", b, true))),
        ]);
        var d = ClickDiff(EngineBattle(), port, "DLG_BATTLE_A/BTN_DEFEND");
        Assert.Equal(("только у порта: без смен", false), (d.Port, d.Ok));
    }

    [Fact]
    public void ClickWindow_EdgesOtherScreensAndFlickerAcrossClick()
    {
        var engine = Journal(
            (1000, Enable("DLG_STRATEGIC", "BTN_GUARD", false)),   // серия началась до нажатия…
            (1000, Click("DLG_STRATEGIC", "BTN_NEXT_LEADER")),
            (1010, Enable("DLG_STRATEGIC", "BTN_GUARD", true)),
            (1020, Enable("DLG_STRATEGIC", "BTN_GUARD", false)),   // …итог «погасла» — после него: следствие нажатия
            (1100, Enable("DLG_STRATEGIC", "BTN_CONT_MOVE", false)),   // ровно 100 мс — в окне
            (1101, Enable("DLG_STRATEGIC", "BTN_EXCHANGE", false)),   // 101 мс — вне окна
            (1050, Enable("DLG_ISO_PAL", "BTN_OPTIONS", false)),   // другой экран — не в счёт
            (5000, Click("DLG_STRATEGIC", "BTN_NEXT_LEADER")));   // то же нажатие без смен — второй вид
        var port = Journal((10, Click("DLG_STRATEGIC", "BTN_NEXT_LEADER")));
        var d = ClickDiff(engine, port, "DLG_STRATEGIC/BTN_NEXT_LEADER");
        Assert.Equal("BTN_CONT_MOVE− BTN_GUARD−,без смен", d.Original);
        Assert.True(d.Ok);
    }

    [Fact]
    public void UiEnables_PortDimsWhatEngineNeverDims_IsDivergence()
    {
        var port = Journal((10, Enable("DLG_BATTLE_A", "BTN_RETREAT", true)));
        var engine = Journal((10, Enable("DLG_BATTLE_B", "BTN_RETREAT", false)));
        var d = AvJournal.UiEnables(engine, port).Single(x => x.Check == "ui_enable");
        Assert.Equal(("DLG_BATTLE_A/BTN_RETREAT", "гаснет", false), (d.Key, d.Original, d.Ok));
        Assert.Contains("зажигается", d.Port, StringComparison.Ordinal);
    }

    [Fact]
    public void UiEnables_PortJournalWithoutUiLines_IsSkipped()
    {
        var old = Journal((1, "\"ev\":\"cursor\",\"name\":\"DEFAULT\",\"top\":\"-\""));   // зонд порта до Зс54: строк ui_enable и ui_click нет
        Assert.Empty(AvJournal.UiEnables(EngineBattle(), old));
        // У оригинала без ui_enable (журнал до 7.6) — тоже пропуск.
        Assert.Empty(AvJournal.UiEnables(Journal((1, Click("DLG_MAIN_MENU", "BTN_SINGLE"))), Journal((1, Click("DLG_MAIN_MENU", "BTN_SINGLE")))));
    }

    [Fact]
    public void UiEnables_JoinedJournals_SeqRestartsPerJournal()
    {
        // Второй журнал начинает seq и время заново: его нажатие в ту же мс, что и смены первого, их не видит.
        var engine = AvJournal.Join([EngineBattle(), Journal((1000, Click("DLG_BATTLE_B", "BTN_WAIT")))]);
        var d = ClickDiff(engine, Journal((1, Click("DLG_BATTLE_A", "BTN_WAIT"))), "DLG_BATTLE_A/BTN_WAIT");
        Assert.Equal(("без смен", true), (d.Original, d.Ok));
    }

    [Fact]
    public void PortTrace_WritesEngineFields()
    {
        string path = Path.Combine(Path.GetTempPath(), $"av76e-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var journal = new PortJournal(path))
            {
                var av = new AvTrace(journal);
                av.UiClick("toggle", "DLG_BATTLE_A", "TOG_AUTOBATTLE", true, true, "BattleScreen", "BattleButtons");
                av.UiEnable("DLG_STRATEGIC", "BTN_CONT_MOVE", false, "DLG_STRATEGIC", "StrategicPanelLogic");
            }
            var lines = AvJournal.Load(path);
            var click = lines.Single(e => e.Ev == "ui_click");
            Assert.Equal(("toggle", "DLG_BATTLE_A", "TOG_AUTOBATTLE", true, true, "BattleButtons"),
                (click.S("kind"), click.S("screen"), click.S("element"), click.B("hotkey"), click.B("checked"), click.S("top_class")));
            var enable = lines.Single(e => e.Ev == "ui_enable");
            Assert.Equal(("DLG_STRATEGIC", "BTN_CONT_MOVE", false, -1L, "DLG_STRATEGIC"),
                (enable.S("screen"), enable.S("element"), enable.B("on"), enable.L("child"), enable.S("top")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
