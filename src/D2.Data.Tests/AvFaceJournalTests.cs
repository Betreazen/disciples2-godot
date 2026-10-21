using D2.Data.Diagnostics;

namespace D2.Data.Tests;

/// <summary>Сверка портретов боя и меток карты схемы 7.6 (<see cref="AvJournal.BattleFaces"/>, <see cref="AvJournal.BattleTextPlaces"/>,
/// <see cref="AvJournal.BattleTextWraps"/>, <see cref="AvJournal.TurnMarks"/>) на синтетических журналах: оригинал — строки зонда
/// <c>probeflash.cpp</c>/<c>probemarks.cpp</c>, порт — <see cref="AvTrace"/> через настоящий <see cref="PortJournal"/>.</summary>
public class AvFaceJournalTests
{
    private static IReadOnlyList<AvEvent> Port(Action<AvTrace> write) => Port((av, _) => write(av));

    /// <summary>Журнал порта; время строки (<c>t</c>, мс часов журнала) заменено номером строки, как у <see cref="Original"/>: надпись и
    /// вспышка склеиваются по окну <see cref="AvJournal.FaceJoinMs"/>, а пауза сборщика мусора на загруженной машине (два прогона тестов
    /// разом, 20.10) разносила строки одного удара дальше окна, и сверка падала.</summary>
    private static IReadOnlyList<AvEvent> Port(Action<AvTrace, PortJournal> write)
    {
        string path = Path.Combine(Path.GetTempPath(), $"av76-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var journal = new PortJournal(path)) write(new AvTrace(journal), journal);
            return [.. AvJournal.Load(path).Select((e, i) => e with { T = i })];
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyList<AvEvent> Original(params string[] lines) =>
        AvJournal.Parse(lines.Select((l, i) => $"{{\"seq\":{i + 1},\"tid\":7,\"t\":{i},{l}}}"));

    private static string Text(string unit, string text, string src = "target", bool persist = false, bool found = true, int w = 70) =>
        $"\"ev\":\"bat_text\",\"unit\":\"{unit}\",\"text\":\"{text}\",\"src\":\"{src}\",\"persist\":{(persist ? "true" : "false")},\"on\":true," +
        $"\"found\":{(found ? "true" : "false")},\"big\":{(w == 150 ? "true" : "false")},\"w\":{w},\"h\":85";

    private static string Flash(string unit, int state, string mode = "flash", string src = "target", bool hold = false, bool? found = true) =>
        $"\"ev\":\"bat_flash\",\"unit\":\"{unit}\",\"state\":{state},\"color\":null,\"mode\":\"{mode}\",\"src\":\"{src}\"," +
        $"\"hold\":{(hold ? "true" : "false")},\"on\":true,\"found\":{(found is null ? "null" : found.Value ? "true" : "false")},\"big\":false,\"ticks\":15";

    private static PortraitText Place(string src = "target", bool persist = false, int lines = 1, int rule = 1) =>
        new(src, persist, On: true, Big: false, 70, 85, lines, rule, 60);

    /// <summary>Лечение у движка — надпись «+N» и синяя вспышка (5); опыт конца боя — стойкая надпись и стойкое жёлтое (11); строка группы,
    /// где юнита нет (опыт зовётся на четыре группы), не в счёт.</summary>
    private static IReadOnlyList<AvEvent> Engine() => Original(
        Text("S1", "+20"), Flash("S1", PortraitFlash.Heal),
        Text("S2", "-15"), Flash("S2", PortraitFlash.Damage),
        Text("S3", "Опыт: +30", "xp", persist: true, found: false),
        Text("S3", "Опыт: +30", "xp", persist: true), Flash("S3", PortraitFlash.Xp, "set", "xp", hold: true, found: null));

    [Fact]
    public void FaceEvents_JoinTextWithItsFlash_SkipGroupsWithoutTheUnit()
    {
        var faces = AvJournal.FaceEvents(Engine());
        Assert.Equal(3, faces.Count);
        Assert.Equal(("+#", PortraitFlash.Heal), (faces[0].Template, faces[0].State!.Value));
        Assert.Equal("11 опыт стойко держит", AvJournal.FaceLook(faces[2]));
    }

    [Fact]
    public void BattleFaces_TextWithoutFlash_IsMismatch_SameFlashMatches()
    {
        var bare = Port(av =>
        {
            av.BattleText("P1", "+20", Place());
            av.BattleText("P2", "-15", Place());
            av.BattleFlash("P2", PortraitFlash.Damage, "flash", "target", hold: false, on: true, big: false, ticks: 15);
        });
        var diffs = AvJournal.BattleFaces(Engine(), bare).ToList();
        Assert.False(diffs.Single(d => d.Key == "+#").Ok);   // лечение без синего — то, о чём говорил пользователь
        Assert.True(diffs.Single(d => d.Key == "-#").Ok);
        Assert.True(diffs.Single(d => d.Key == "Опыт: +#").Ok);   // у порта не сыграно — не расхождение

        var flashed = Port(av =>
        {
            av.BattleText("P1", "+7", Place());
            av.BattleFlash("P1", PortraitFlash.Heal, "flash", "target", hold: false, on: true, big: false, ticks: 15);
        });
        Assert.True(AvJournal.BattleFaces(Engine(), flashed).Single(d => d.Key == "+#").Ok);
    }

    [Fact]
    public void BattleFaces_OldJournalsWithoutPlace_NothingToCompare()
    {
        var old = Port(av => av.BattleText("P1", "+20"));
        Assert.Empty(AvJournal.BattleFaces(Engine(), old));
        Assert.Empty(AvJournal.BattleTextPlaces(Engine(), old));
    }

    [Fact]
    public void TextPlaces_SizeAndPersistence_PortSubsetOfEngine()
    {
        var port = Port(av =>
        {
            av.BattleText("P1", "+20", Place());
            av.BattleText("P3", "Опыт: +5", Place("xp"));   // у порта опыт со сроком, у движка — без срока
            av.BattleFlash("P1", PortraitFlash.Heal, "flash", "target", false, true, false, ticks: 15);
        });
        var diffs = AvJournal.BattleTextPlaces(Engine(), port).ToList();
        Assert.True(diffs.Single(d => d is { Check: "battle_text_place", Key: "+#" }).Ok);
        Assert.False(diffs.Single(d => d is { Check: "battle_text_persist", Key: "Опыт: +#" }).Ok);
        Assert.True(diffs.Single(d => d.Check == "battle_flash_ticks").Ok);
    }

    [Fact]
    public void TextWraps_FewerLinesThanTheRule_IsMismatch()
    {
        var port = Port(av =>
        {
            av.BattleText("P1", "Понижение уровня", Place(lines: 1, rule: 2));
            av.BattleText("P2", "-15", Place());
        });
        var diffs = AvJournal.BattleTextWraps(Engine(), port).ToList();
        Assert.False(diffs.Single(d => d.Key == "Понижение уровня").Ok);
        Assert.True(diffs.Single(d => d.Key == "-#").Ok);
        Assert.Equal("70×85", diffs.Single(d => d.Key == "-#").Original);
    }

    [Fact]
    public void TurnMarks_PathLeftAtTurnEnd_EngineSelectsAgain_PortLosesIt()
    {
        var engine = Original(
            "\"ev\":\"map_sel\",\"action\":\"show\",\"x\":5,\"y\":6",
            "\"ev\":\"map_path\",\"action\":\"show\",\"names\":\"MOVENORMAL*2,MOVEOUT*3\"",
            "\"ev\":\"ui_click\",\"kind\":\"button\",\"screen\":\"DLG_STRATEGIC\",\"element\":\"BTN_END_TURN\"",
            "\"ev\":\"ai_turn\",\"phase\":\"begin\",\"cause\":\"ai\"",
            "\"ev\":\"ui_click\",\"kind\":\"button\",\"screen\":\"DLG_CHAT_AI\",\"element\":\"BTN_CLOSE\"",   // посреди хода ИИ — не новый ход
            "\"ev\":\"ai_turn\",\"phase\":\"end\",\"cause\":\"ai\"",
            "\"ev\":\"map_click\",\"action\":\"select\"");
        (string, int, int)[] path = [("SELECTED", 5, 6), ("MOVENORMAL", 6, 6), ("MOVEOUT", 7, 6)];
        var lost = Port((av, j) =>
        {
            j.Write("turn", new Dictionary<string, object?> { ["human"] = true });
            av.MapMarks(path);
            j.Write("click", new Dictionary<string, object?>());
            j.Write("turn", new Dictionary<string, object?> { ["human"] = false });
            av.MapMarks([]);
            j.Write("turn", new Dictionary<string, object?> { ["human"] = true });
            j.Write("click", new Dictionary<string, object?>());
        });
        var diff = Assert.Single(AvJournal.TurnMarks(engine, lost));
        Assert.Equal("выбран да, путь да", diff.Original);
        Assert.False(diff.Ok);

        var kept = Port((av, j) =>
        {
            j.Write("turn", new Dictionary<string, object?> { ["human"] = true });
            av.MapMarks(path);
            j.Write("click", new Dictionary<string, object?>());
            j.Write("turn", new Dictionary<string, object?> { ["human"] = false });
            j.Write("turn", new Dictionary<string, object?> { ["human"] = true });
            j.Write("click", new Dictionary<string, object?>());
        });
        Assert.True(Assert.Single(AvJournal.TurnMarks(engine, kept)).Ok);
    }

    [Fact]
    public void MapMarks_OnlyChangesAreWritten_PathAsRuns()
    {
        var port = Port(av =>
        {
            (string, int, int)[] marks = [("SELECTED", 1, 1), ("MOVENORMAL", 2, 1), ("MOVENORMAL", 3, 1), ("MOVEOUT", 4, 1)];
            av.MapMarks(marks, "S143KC0004");
            av.MapMarks(marks, "S143KC0004");   // перерисовка без смены — строки нет
            av.MapMarks(marks, "S143KC0006");   // те же флажки другого отряда — смена
            av.MapMarks([("SELECTED", 1, 1)]);
        });
        Assert.Equal(["map_sel", "map_path", "map_path", "map_path"], port.Select(e => e.Ev));
        Assert.Equal("MOVENORMAL*2,MOVEOUT*1", port[1].S("names"));
        Assert.Equal("S143KC0004", port[1].S("stack"));
        Assert.Equal("S143KC0006", port[2].S("stack"));
        Assert.Equal("clear", port[3].S("action"));
    }

    [Fact]
    public void PortraitColors_FromEngineTable()
    {
        Assert.Equal("#0000FF", PortraitFlash.ColorOf(PortraitFlash.Heal));
        Assert.Equal("#FFFF40", PortraitFlash.ColorOf(PortraitFlash.Xp));
        Assert.Equal("#808000", PortraitFlash.ColorOf(PortraitFlash.Poison));   // как подкраска яда панели (BattlePanel.Tint)
        Assert.Null(PortraitFlash.ColorOf(PortraitFlash.Immune));
        Assert.Null(PortraitFlash.ColorOf(PortraitFlash.HealOverflow));
        Assert.True(PortraitFlash.Steady(PortraitFlash.Dead));
    }
}
