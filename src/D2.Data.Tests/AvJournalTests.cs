using D2.Data.Diagnostics;
using D2.Data.Sound;

namespace D2.Data.Tests;

/// <summary>Сверка звука, музыки, роликов и анимаций «журнал оригинала ↔ журнал порта» (<see cref="AvJournal"/>, схема 7.3) на
/// синтетических журналах: строки оригинала — в виде зонда <c>tools/d2probe</c> (<c>tid</c>, поля движка), порта — в виде
/// <see cref="AvTrace"/> через настоящий <see cref="PortJournal"/>.</summary>
public class AvJournalTests
{
    /// <summary>Журнал порта: что пишет <see cref="AvTrace"/>, через файл, как в игре.</summary>
    private static IReadOnlyList<AvEvent> Port(Action<AvTrace> write)
    {
        string path = Path.Combine(Path.GetTempPath(), $"av-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var journal = new PortJournal(path)) write(new AvTrace(journal));
            return AvJournal.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyList<AvEvent> Original(params string[] lines) =>
        AvJournal.Parse(lines.Select((l, i) => $"{{\"seq\":{i + 1},\"tid\":7,\"t\":{i * 10},{l}}}"));

    [Fact]
    public void Parse_SkipsOtherLines_KeepsFieldsOfBothJournals()
    {
        var original = Original(
            "\"ev\":\"rng\",\"n\":1,\"max\":100,\"r\":5",
            "\"ev\":\"snd\",\"name\":\"OPENINTR\",\"archive\":null,\"vol\":100,\"pan\":64,\"loop\":false,\"out\":83,\"id\":3,\"ok\":true,\"via\":\"game\",\"bank\":\"0x1\",\"ret_addr\":\"0x5b0433\",\"top\":\"DLG_MANAGE_STACK\",\"top_class\":\"CManageStkInterf\"");
        var e = Assert.Single(original);
        Assert.Equal("snd", e.Ev);
        Assert.Equal("OPENINTR", e.S("name"));
        Assert.Equal(83, e.L("out"));
        Assert.Null(e.S("archive"));
        Assert.Equal(10, e.T);

        var port = Port(av => av.Sound("OPENINTR", "Midgard", 100, 64, false, 83, 1, true, "game", "DLG_MANAGE_STACK"));
        var p = Assert.Single(port);
        Assert.Equal("Midgard", p.S("archive"));
        Assert.Equal(true, p.B("ok"));
    }

    [Fact]
    public void ScreenSounds_SameNamesMatch_PortExtraIsMismatch_OneSidedScreensAreInfo()
    {
        var original = Original(
            Snd("OPENINTR", "DLG_MANAGE_STACK"), Snd("BOTREPRT", "DLG_ISO"), Snd("SBATTLE", "DLG_ISO"),
            Snd("HEAL", "DLG_BATTLE_B"), Snd("RUIN", "DLG_ISO", ok: false));
        var port = Port(av =>
        {
            av.Sound("OPENINTR", "Midgard", 100, 64, false, 83, 1, true, "game", "DLG_MANAGE_STACK");
            av.Sound("BOTREPRT", "Midgard", 100, 64, false, 83, 2, true, "game", "MapScreen");
            av.Sound("SBATTLE", "Midgard", 100, 64, false, 83, 3, true, "game", "MapScreen");
            av.Sound("CLOSINTR", "Midgard", 100, 64, false, 83, 4, true, "game", "MapScreen");   // у движка его нет
            av.Sound("HEAL", "Battle", 100, 64, false, 83, 5, true, "battle", "BattleScreen");
            av.Sound("OPENBOOK", "Midgard", 100, 64, false, 83, 6, true, "game", "DLG_CAST_SPELL");
        });

        var diffs = AvJournal.ScreenSounds(original, port).ToDictionary(d => d.Key);
        Assert.True(diffs["DLG_MANAGE_STACK"].Ok);
        Assert.True(diffs["DLG_BATTLE_A"].Ok);
        Assert.False(diffs["DLG_ISO"].Ok);
        Assert.Contains("CLOSINTR", diffs["DLG_ISO"].Port, StringComparison.Ordinal);
        Assert.DoesNotContain("RUIN", diffs["DLG_ISO"].Original, StringComparison.Ordinal);   // не прозвучал — не считается
        Assert.True(diffs["DLG_CAST_SPELL"].Ok);   // экран одной стороны — не расхождение
        Assert.Equal("экрана нет", diffs["DLG_CAST_SPELL"].Original);
    }

    [Fact]
    public void SoundVolumes_SameNameAndVia_MustHaveSamePercent()
    {
        var original = Original(Snd("BLDGA1ACK1", "DLG_ISO", vol: 66), Snd("OPENINTR", "DLG_ISO"));
        var port = Port(av =>
        {
            av.Sound("BLDGA1ACK1", "Midgard", 100, 64, false, 83, 1, true, "game", "MapScreen");
            av.Sound("OPENINTR", "Midgard", 100, 64, false, 83, 2, true, "game", "MapScreen");
        });

        var diffs = AvJournal.SoundVolumes(original, port).ToDictionary(d => d.Key);
        Assert.False(diffs["BLDGA1ACK1@game"].Ok);
        Assert.Equal("66", diffs["BLDGA1ACK1@game"].Original);
        Assert.True(diffs["OPENINTR@game"].Ok);
    }

    [Fact]
    public void MusicFades_FadeBeforeTrackOfOtherKind_WithinTolerance()
    {
        // Оригинал: карта → бой с затуханием 1000 мс (CMusicFader шагами), бой → карта 700 мс.
        var original = Original(
            "\"ev\":\"music\",\"action\":\"open\",\"track\":\"INGAME03\",\"file\":\"Music\\\\INGAME03.MP3\",\"ok\":true",
            "\"ev\":\"music\",\"action\":\"volume\",\"track\":\"INGAME03\",\"from\":80,\"to\":0,\"ms\":980,\"steps\":20",
            "\"ev\":\"music\",\"action\":\"stop\",\"track\":\"INGAME03\"",
            "\"ev\":\"music\",\"action\":\"open\",\"track\":\"BATTLE02\",\"file\":\"Music\\\\BATTLE02.MP3\",\"ok\":true",
            "\"ev\":\"music\",\"action\":\"volume\",\"track\":\"BATTLE02\",\"from\":80,\"to\":0,\"ms\":690,\"steps\":14",
            "\"ev\":\"music\",\"action\":\"open\",\"track\":\"INGAME03\",\"file\":\"Music\\\\INGAME03.MP3\",\"ok\":true");
        var port = Port(av =>
        {
            av.Music("open", "INGAME05", "Music/INGAME05");
            av.MusicVolume("INGAME05", 80, 0, 1000, 1);
            av.Music("open", "BATTLE01", "Music/BATTLE01");
            av.MusicVolume("BATTLE01", 80, 0, 300, 1);   // порт затухает слишком быстро
            av.Music("open", "AMB02", "Music/AMB02");
            av.Music("open", "MENUTRCK", "Music/MENUTRCK");   // перехода у оригинала нет — не расхождение
        });

        var diffs = AvJournal.MusicFades(original, port).ToDictionary(d => d.Key);
        Assert.True(diffs["map→battle"].Ok);
        Assert.False(diffs["battle→map"].Ok);
        Assert.True(diffs["map→menu"].Ok);
        Assert.Equal("перехода нет", diffs["map→menu"].Original);
    }

    [Theory]
    [InlineData("MENUTRCK", "menu")]
    [InlineData("BATTLE04", "battle")]
    [InlineData("Music\\INGAME11.mp3", "map")]
    [InlineData("AMB01", "map")]
    [InlineData("WINFGEN", "other")]
    [InlineData("dwrftrk2.wav", "capital")]
    public void TrackKind_ByName(string track, string kind) => Assert.Equal(kind, AvJournal.TrackKind(track));

    [Fact]
    public void Videos_FramesAndRateFromHeaderMatchCatalog()
    {
        var original = Original(
            "\"ev\":\"video\",\"action\":\"open\",\"name\":\"INTRO\",\"frames\":1830,\"rate\":15.000,\"shown\":0,\"ms\":0,\"ok\":true,\"flags\":0",
            "\"ev\":\"video\",\"action\":\"open\",\"name\":\"SF\",\"frames\":300,\"rate\":15.000,\"shown\":0,\"ms\":0,\"ok\":true,\"flags\":0");
        var port = Port(av =>
        {
            av.Video("open", "INTRO", 1830, 15, 0, 0);
            av.Video("open", "SF", 299, 15, 0, 0);
        });

        var diffs = AvJournal.Videos(original, port).ToDictionary(d => d.Key);
        Assert.True(diffs["INTRO"].Ok);
        Assert.False(diffs["SF"].Ok);
    }

    [Fact]
    public void MapAnims_MedianDurationWithinTolerance()
    {
        var original = Original(
            Anim("CMidAnim2Battle", 1122), Anim("CMidAnim2Battle", 1130), Anim("CMidAnim2Battle", 1190),
            Anim("CMidAnim2Spell", 990), Anim("CMidAnim2Unit", 340, ticks: 10));
        var port = Port(av =>
        {
            av.MapAnim("end", "CMidAnim2Battle", 3, 4, null, 1150, 17);
            av.MapAnim("end", "CMidAnim2Spell", 3, 4, null, 400, 6);
            av.MapAnim("end", "CMidAnim2Unit", 3, 4, "S1", 1700, 50);   // весь ход порта — тот же период кадра
        });

        var diffs = AvJournal.MapAnims(original, port).ToDictionary(d => d.Key);
        Assert.True(diffs["CMidAnim2Battle"].Ok);
        Assert.False(diffs["CMidAnim2Spell"].Ok);
        Assert.True(diffs["CMidAnim2Unit (мс на кадр, группа 0)"].Ok);
    }

    /// <summary>Длительность — в тиках своих часов: у оригинала с обёрткой DisciplesGL (<c>GameSpeed</c> 4) медленная группа тикает по 47 мс
    /// (<see cref="AvJournal.WrapperClockMs"/>, строки <c>frames</c>), и 750 мс начала боя — те же 16 тиков, что 1041 мс у порта по 66.</summary>
    [Fact]
    public void MapAnims_InTicksOfOwnClock()
    {
        Assert.Equal((66, 47, 33), (AvJournal.WrapperClockMs(0), AvJournal.WrapperClockMs(4), AvJournal.WrapperClockMs(10)));
        var original = Original(
            "\"ev\":\"frames\",\"n\":600,\"ms\":10000,\"max_ms\":20,\"slow_ticks\":212,\"fast_ticks\":1235,\"top\":\"DLG_ISO\"",
            "\"ev\":\"frames\",\"n\":600,\"ms\":10000,\"max_ms\":20,\"slow_ticks\":300,\"fast_ticks\":1235,\"top\":\"DLG_BATTLE_B\"",
            Anim("CMidAnim2Battle", 750, 15), Anim("CMidAnim2Dying", 704, 14));
        var port = Port(av =>
        {
            av.MapAnim("end", "CMidAnim2Battle", 3, 4, null, 1041, 16);
            av.MapAnim("end", "CMidAnim2Dying", 3, 4, null, 500, 8);
        });

        Assert.Equal(47.17, AvJournal.ClockMs(original, 0), 2);   // окна над картой, окно боя не в счёт
        Assert.Equal(AvJournal.EngineSlowClockMs, AvJournal.ClockMs(port, 0));
        var diffs = AvJournal.MapAnims(original, port).ToDictionary(d => d.Key);
        Assert.True(diffs["CMidAnim2Battle"].Ok, diffs["CMidAnim2Battle"].Original + " | " + diffs["CMidAnim2Battle"].Port);
        Assert.False(diffs["CMidAnim2Dying"].Ok);   // 15 тиков против 7,6
    }

    [Fact]
    public void BattleFrames_SameUnitTypeAndKind_ExactFrames()
    {
        var original = Original(
            "\"ev\":\"bat_anim\",\"unit\":\"S143UU0001\",\"type\":\"G000UU0001\",\"kind\":\"HMOV\",\"frames\":24",
            "\"ev\":\"bat_anim\",\"unit\":\"S143UU0002\",\"type\":\"G000UU0002\",\"kind\":\"HHIT\",\"frames\":12",
            "\"ev\":\"bat_fx\",\"kind\":\"TUCH\",\"unit\":\"S143UU0001\",\"name\":null,\"frames\":9,\"left\":false,\"pos\":2");
        var port = Port(av =>
        {
            av.BattleAnim("S1", "G000UU0001", "HMOV", 24);
            av.BattleAnim("S2", "G000UU0002", "HHIT", 11);
            av.BattleFx("TUCH", "S1", "G000UU0001TUCH", 9);
        });

        var diffs = AvJournal.BattleFrames(original, port).ToDictionary(d => d.Key);
        Assert.True(diffs["G000UU0001/HMOV"].Ok);
        Assert.False(diffs["G000UU0002/HHIT"].Ok);
        Assert.True(diffs["TUCH@G000UU0001"].Ok);
    }

    [Fact]
    public void CorpusReader_V73Lines_AreTypedEvents()
    {
        string path = Path.Combine(Path.GetTempPath(), $"av73-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                "{\"seq\":1,\"tid\":7,\"t\":0,\"ev\":\"coverage\",\"armed\":22364,\"listed\":22364,\"skipped\":0,\"file\":\"x.cov.txt\"}",
                "{\"seq\":2,\"tid\":7,\"t\":5,\"ev\":\"music\",\"action\":\"open\",\"track\":\"MENUTRCK\",\"file\":\"Music\\\\menutrck.wav\",\"ok\":true,\"top\":\"none\",\"top_class\":\"null\"}",
                "{\"seq\":3,\"tid\":7,\"t\":9,\"ev\":\"snd_end\",\"id\":4,\"name\":\"OPENINTR\",\"how\":\"end\",\"ms\":700,\"vol_min\":83,\"vol_max\":83,\"changes\":0}",
            ]);
            var events = D2.Data.Corpus.CorpusReader.Read(path).ToList();
            Assert.DoesNotContain(events, e => e is D2.Data.Corpus.UnknownEvent);
            var cov = Assert.IsType<D2.Data.Corpus.CoverageEvent>(events[0]);
            Assert.Equal(22364, cov.Armed);
            var music = Assert.IsType<D2.Data.Corpus.AvProbeEvent>(events[1]);
            Assert.Equal("MENUTRCK", music.Av.S("track"));
            Assert.Equal(700, Assert.IsType<D2.Data.Corpus.AvProbeEvent>(events[2]).Av.L("ms"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_BrokenLastLine_IsCountedNotThrown_VideoWithoutRate()
    {
        var events = AvJournal.Parse(
        [
            "{\"seq\":1,\"tid\":7,\"t\":0,\"ev\":\"video\",\"action\":\"open\",\"name\":\"SF\",\"frames\":300}",
            "{\"seq\":2,\"tid\":7,\"t\":3,\"ev\":\"snd\",\"name\":\"OPEN",
        ]);
        Assert.Single(events);
        Assert.Equal(1, AvJournal.Broken);
        Assert.Equal("300@?", Assert.Single(AvJournal.Videos(events, events)).Original);
    }

    [Fact]
    public void Compare_EmptyJournals_NoDiffs()
    {
        Assert.Empty(AvJournal.Compare([], []));
    }

    /// <summary>Звук и окно в один миг: порт играет покупку, пока окно подтверждения ещё не закрыто, и вход в лавку, когда её окно уже в
    /// дереве, — звук ложится на тот экран рядом, где его играл движок; голоса юнитов сверяются ролью, а не типом.</summary>
    [Fact]
    public void ScreenSounds_SameMomentWindowChange_AndUnitVoiceRoles()
    {
        var original = Original(Snd("BUYITEM", "DLG_MERCHANT"), Snd("ENTRSITE", "DLG_ISO"), Snd("G000UU0020ACK1", "DLG_ISO"));
        var port = Port(av =>
        {
            av.Sound("ENTRSITE", "Midgard", 100, 64, false, 83, 1, true, "game", "DLG_MERCHANT");
        });
        var withScreens = AvJournal.Parse(
        [
            "{\"seq\":1,\"t\":0,\"tid\":2,\"ev\":\"screen\",\"action\":\"open\",\"name\":\"MapScreen\"}",
            "{\"seq\":2,\"t\":10,\"tid\":2,\"ev\":\"snd\",\"name\":\"ENTRSITE\",\"vol\":100,\"via\":\"game\",\"top\":\"DLG_MERCHANT\"}",
            "{\"seq\":3,\"t\":10,\"tid\":2,\"ev\":\"screen\",\"action\":\"open\",\"name\":\"DLG_MERCHANT\"}",
            "{\"seq\":4,\"t\":900,\"tid\":2,\"ev\":\"screen\",\"action\":\"open\",\"name\":\"DLG_MESSAGE_BOX\"}",
            "{\"seq\":5,\"t\":1500,\"tid\":2,\"ev\":\"snd\",\"name\":\"BUYITEM\",\"vol\":100,\"via\":\"game\",\"top\":\"DLG_MESSAGE_BOX\"}",
            "{\"seq\":6,\"t\":1501,\"tid\":2,\"ev\":\"screen\",\"action\":\"close\",\"name\":\"DLG_MESSAGE_BOX\"}",
            "{\"seq\":7,\"t\":5000,\"tid\":2,\"ev\":\"snd\",\"name\":\"G000UU0072ACK3\",\"vol\":33,\"via\":\"game\",\"top\":\"DLG_STRATEGIC\"}",
        ]);
        Assert.Single(port);
        var diffs = AvJournal.ScreenSounds(original, withScreens).ToDictionary(d => d.Key);
        Assert.True(diffs["DLG_MERCHANT"].Ok, diffs["DLG_MERCHANT"].Port);
        Assert.True(diffs["DLG_ISO"].Ok, diffs["DLG_ISO"].Port);
        Assert.Equal("<юнит>ACK,ENTRSITE", diffs["DLG_ISO"].Port);
        Assert.DoesNotContain("DLG_MESSAGE_BOX", diffs.Keys);
    }

    [Fact]
    public void MusicFadeIns_StartWithoutFade_ResumeWithFade()
    {
        var original = Original(
            "\"ev\":\"music\",\"action\":\"open\",\"track\":\"INGAME01\",\"kind\":\"main\"",
            "\"ev\":\"music\",\"action\":\"fade\",\"track\":\"INGAME01\",\"dir\":\"out\",\"then\":\"call\",\"ms\":1000",
            "\"ev\":\"music\",\"action\":\"open\",\"track\":\"BATTLE01\",\"kind\":\"temp\"",
            "\"ev\":\"music\",\"action\":\"fade\",\"track\":\"BATTLE01\",\"dir\":\"out\",\"then\":\"call\",\"ms\":700",
            "\"ev\":\"music\",\"action\":\"open\",\"track\":\"INGAME01\",\"kind\":\"resume\"",
            "\"ev\":\"music\",\"action\":\"fade\",\"track\":\"INGAME01\",\"dir\":\"in\",\"then\":\"none\",\"ms\":1000");
        var port = Port(av =>
        {
            av.Music("open", "INGAME02");
            av.MusicFade("INGAME02", "out", "call", 1000);
            av.Music("open", "BATTLE02");
            av.MusicFade("BATTLE02", "in", "none", 1000);   // временная с нарастанием — у движка его нет
            av.MusicFade("BATTLE02", "out", "call", 700);
            av.Music("open", "INGAME02");
            av.MusicFade("INGAME02", "in", "none", 1000);
        });
        var diffs = AvJournal.MusicFadeIns(original, port).ToDictionary(d => d.Key);
        Assert.False(diffs["map→battle"].Ok);
        Assert.True(diffs["battle→map"].Ok);
    }

    [SkippableFact]
    public void BattleSounds_RolesOfActor_AndSoundsOutsideUnitLists()
    {
        var sounds = UnitSounds.Load(ImportedData.PathOf("sound/UnitSounds.json"));
        var original = Original(
            "\"ev\":\"bat_anim\",\"unit\":\"S1\",\"type\":\"G000UU0153\",\"kind\":\"HMOV\",\"frames\":31",
            "\"ev\":\"snd\",\"name\":\"0153_ATTACK_B\",\"vol\":100,\"via\":\"battle\",\"ok\":true",
            "\"ev\":\"snd\",\"name\":\"G000SS0008\",\"vol\":100,\"via\":\"battle\",\"ok\":true",
            "\"ev\":\"snd\",\"name\":\"UNSUMMON\",\"vol\":100,\"via\":\"battle\",\"ok\":true");
        var port = Port(av =>
        {
            av.BattleAnim("S1", "g000uu0153", "HMOV", 31);
            av.Sound("DEATH", "Battle", 100, 64, false, 41, 1, true, "battle", "DLG_BATTLE_A");
        });
        var diffs = AvJournal.BattleSounds(original, port, sounds).ToDictionary(d => d.Key);
        Assert.False(diffs["G000UU0153"].Ok);   // замах у движка есть, у порта — нет
        Assert.Equal("attack,effect,hit", diffs["G000UU0153"].Original);
        Assert.False(diffs["battle_other"].Ok);
        Assert.Contains("DEATH", diffs["battle_other"].Port, StringComparison.Ordinal);
        Assert.Empty(AvJournal.BattleSounds(original, port));   // без звуков юнитов сверки нет
    }

    [Theory]
    [InlineData("Battle:CUATTACKNA", "CUATTACKNA")]   // «A» — анимированный вариант при BattleAnim 1, а не буква кадра
    [InlineData("Battle:CUATTACK+G000IG9027", "CUATTACK")]
    [InlineData("IsoCursr:NODRAGDROP+IconItem:G000IG0006", "NODRAGDROP")]
    [InlineData("ui:_CUDEFAUL", "?")]
    [InlineData("MOVE", "MOVE")]
    public void CursorName_InEngineNames(string port, string engine) => Assert.Equal(engine, AvJournal.CursorName(port));

    [Fact]
    public void BattleTexts_TemplatesWithoutNumbers()
    {
        var original = Original("\"ev\":\"bat_text\",\"unit\":\"S1\",\"text\":\"\\\\hC;\\\\vC;\\\\fLarge;-35\",\"src\":\"target\"");
        var same = Port(av => av.BattleText("S2", "\\hC;\\vC;\\fLarge;-12"));
        var other = Port(av => av.BattleText("S2", "-12"));
        Assert.True(Assert.Single(AvJournal.BattleTexts(original, same)).Ok);
        Assert.False(Assert.Single(AvJournal.BattleTexts(original, other)).Ok);
    }

    /// <summary>Журналы одной стороны подряд (<see cref="AvJournal.Join"/>): вид музыки и ходящий не переходят через границу.</summary>
    [Fact]
    public void Join_StateDoesNotCrossJournals()
    {
        var first = Original("\"ev\":\"music\",\"action\":\"open\",\"track\":\"BATTLE01\",\"kind\":\"temp\"");
        var second = Original("\"ev\":\"music\",\"action\":\"open\",\"track\":\"MENUTRCK\",\"kind\":\"main\"");
        var joined = AvJournal.Join([first, second]);
        Assert.Equal(3, joined.Count);
        Assert.Empty(AvJournal.MusicFades(joined, joined));   // без границы был бы переход battle→menu
    }

    private static string Snd(string name, string top, int vol = 100, bool ok = true) =>
        $"\"ev\":\"snd\",\"name\":\"{name}\",\"archive\":null,\"vol\":{vol},\"pan\":64,\"loop\":false,\"out\":83,\"id\":1," +
        $"\"ok\":{(ok ? "true" : "false")},\"via\":\"game\",\"bank\":\"0x1\",\"ret_addr\":\"0x5b0433\",\"top\":\"{top}\",\"top_class\":\"X\"";

    private static string Anim(string cls, int ms, int ticks = 17) =>
        $"\"ev\":\"map_anim\",\"action\":\"end\",\"class\":\"{cls}\",\"x\":1,\"y\":2,\"obj\":null,\"ms\":{ms},\"ticks\":{ticks}";
}
