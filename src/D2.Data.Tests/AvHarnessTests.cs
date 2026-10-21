using D2.Data.Corpus;
using D2.Data.Diagnostics;
using D2.Data.Sound;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Звук, музыка, ролики, анимации и курсоры по живым прогонам зонда 7.3/7.4 (<c>docs/probe-av.md</c>, §3а и §3б) — две пары «оригинал ↔ порт»:
/// <list type="number">
/// <item><see cref="First"/> — партия пользователя 16–17.10 в оригинале (<c>corpus/20261016e-av</c> — greenskins дни 1–8, синтетические карты 5–7 и
/// начало 8; <c>corpus/20261016g-port-synth</c> — карты 8 и 9) и та же партия в порте (<c>port-20261017-134341</c>);</item>
/// <item><see cref="Second"/> — карты 10–12 вечера 17.10 (<c>corpus/20261017-port-synth</c>: карта 11, 12, затем 10; на 11–12 у оригинала звук
/// эффектов и музыка выключены, на 10 включена «дополнительная анимация боя») и порт (<c>port-20261017-215431</c>).</item>
/// </list>
/// Выписка строк звука, анимаций и смен экранов порта — <c>golden/av/*.av.jsonl</c> (корпус порта не хранит; поле потока <c>th</c> переименовано в
/// <c>tid</c>, как у зонда оригинала, — иначе строки не читает <see cref="CorpusReader"/>). Сверка — <see cref="AvJournal.Compare"/>: имена и
/// порядок — точно, время — с допусками сверок. Журналы порта сняты до правок своего раунда — эти расхождения перечислены в <c>Fixed</c> (сверка
/// их правку не доказывает: журнал старый, доказывают факты движка ниже и юнит-тесты), прочие — в <c>Open</c>. После следующей партии в порте тест
/// скажет, какие строки пора убрать.
/// </summary>
[Trait("Category", "Corpus")]
public class AvHarnessTests(ITestOutputHelper output)
{
    /// <summary>Пара журналов: оригинал (прогоны корпуса), выписка порта и разобранные расхождения.</summary>
    private sealed class Run(string[] originals, string golden, IReadOnlyDictionary<string, string> fixedDiffs,
                             IReadOnlyDictionary<string, string> openDiffs)
    {
        public string[] Originals { get; } = originals;

        public IReadOnlyDictionary<string, string> Fixed { get; } = fixedDiffs;

        public IReadOnlyDictionary<string, string> Open { get; } = openDiffs;

        public string PortJournal { get; } = Path.Combine(AppContext.BaseDirectory, "golden", "av", golden);

        public IEnumerable<string> Known => Fixed.Keys.Concat(Open.Keys);

        private (IReadOnlyList<AvEvent> Original, IReadOnlyList<AvEvent> Port)? _journals;

        private readonly Lock _gate = new();

        /// <summary>Оригинал и порт: у каждого журнала свой seq и свои id юнитов — подряд, с границей (<see cref="AvJournal.Join"/>).</summary>
        public (IReadOnlyList<AvEvent> Original, IReadOnlyList<AvEvent> Port) Journals
        {
            get
            {
                lock (_gate)
                {
                    string corpus = GameSaveTests.CorpusDir();
                    return _journals ??= (AvJournal.Join(Originals.Select(run => Av(Path.Combine(corpus, run, "passive-001.jsonl")))), Av(PortJournal));
                }
            }
        }

        public void SkipWithout()
        {
            string corpus = GameSaveTests.CorpusDir();
            foreach (var run in Originals)
                Skip.IfNot(File.Exists(Path.Combine(corpus, run, "passive-001.jsonl")), $"нет журнала corpus/{run}");
            Skip.IfNot(File.Exists(PortJournal), "нет выписки журнала порта golden/av");
        }
    }

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Виды строк, нужные сверке: звук и анимации (<see cref="AvJournal.Events"/>) и смены экранов порта.</summary>
    private static readonly IReadOnlyCollection<string> Kinds = [.. AvJournal.Events.Concat(AvJournal.Context)];

    /// <summary>Строки звука и анимаций журнала через общий кэш (<see cref="JournalCache"/>): виды 7.3 — уже разобранные записи, смены
    /// экранов порта — из сырой строки.</summary>
    private static IReadOnlyList<AvEvent> Av(string path) =>
    [
        .. JournalCache.Read(path, Kinds)
            .Select(e => e switch
            {
                AvProbeEvent a => a.Av,
                UnknownEvent u => AvJournal.ParseLine(u.RawJson, u.Ev),
                _ => null,
            })
            .OfType<AvEvent>()
            .OrderBy(e => e.Seq),
    ];

    /// <summary>
    /// Первая пара. <c>Fixed</c> — расхождения журнала порта 17.10 (день), снятые правкой после партии: их правку доказывают факты движка
    /// (<see cref="Music_StartsWithoutFadeIn_ResumeFadesIn1000_AsPortPlan"/>, <see cref="IdleVoices_OnlyOverMap"/>,
    /// <see cref="Options_VolumeStepsPreviewSounds"/>, <see cref="RuinTakenByBattle_LootSounds"/>) и юнит-тесты
    /// (<c>BattleSoundPlanTests.NoCasterEffect_NoEffectSound</c>). <c>Open</c> — правкой порта не снимаются: «окружение» — у двух партий разные
    /// настройки или копии игры (сверять нечего).
    /// </summary>
    private static readonly Run First = new(["20261016e-av", "20261016g-port-synth"], "port-20261017-134341.av.jsonl",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Музыка: основная и временная — без нарастания (69 и 30 стартов движка), у порта было 1000 мс (tempIn).
            ["music_fade_in|map→battle"] = "временная дорожка без нарастания",
            ["music_fade_in|map→capital"] = "временная дорожка без нарастания",
            // Голоса скуки под окнами: у движка — только при верхней карте (9 из 9), у порта 22 из 40 под окнами.
            ["screen_sounds|DLG_CAST_SPELL"] = "голос скуки под окном (BKPOPUP ПКМ — только у движка)",
            ["screen_sounds|DLG_CITY_STACK"] = "голос скуки под окном",
            ["screen_sounds|DLG_MAGE"] = "голос скуки под окном",
            ["screen_sounds|DLG_MERCENARIES"] = "голос скуки под окном",
            ["screen_sounds|DLG_MERCHANT"] = "голос скуки под окном",
            ["screen_sounds|DLG_MESSAGE_BOX"] = "голос скуки под окном; BUILDING — окно построек у порта не закрывается само после стройки (интерфейс)",
            // Звуки боя: призыв и усиление без записей хода — у порта тишина (исправлено для хода человека).
            ["battle_sounds|G000UU0033"] = "усиление союзника без записей хода — замах и удар (TENDERFOOT, BOOSTFX)",
            ["battle_sounds|G000UU0153"] = "призыв без записей хода — замах ELEMENTALIST и удар SUMMON",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["screen_sounds|DLG_RESEARCH_SPELL"] = "окружение сверки: OPENBOOK/CLOSBOOK звучат у обоих, движок пишет верхним экраном DLG_ISO",
            ["battle_sounds|battle_other"] = "окружение: DEATH звучит в конце удара цели HHIT, а он есть только при BattleAnim 1 "
                                             + "(Death_AtHurtEnd_OnlyWithBattleAnim); оригинал — 0, порт — 1",
            ["video|SF"] = "окружение: SF.bik game-dev (640×480, 554 кадра) не тот, что в game-pristine (800×600, 120)",
            ["map_anim|CMidAnim2Unit (мс на кадр, группа 0)"] =
                "окружение: в журнале порта 17.10 нет поля group, а шёл он быстрой группой (StackSmoothScroll 1, 33 мс), оригинал — медленной",
            ["cursor|DLG_BATTLE_A"] = "окружение: курсоры боя «…A» — при BattleAnim 1 (порт), у оригинала 0 (CUATTACK)",
        });

    /// <summary>
    /// Вторая пара (карты 10–12, 17.10 вечер). <c>Fixed</c>: курсор тащимого предмета — по «дополнительной анимации боя» (<c>CUATTACKA</c>).
    /// Показ предмета в бою (молния Сферы грома на цели, удар цели, надпись, два звука сразу) и <c>BEEP</c> щелчка без пути журнал порта не
    /// доказывает (у порта таких строк не было вовсе) — доказывают <see cref="ItemUse_TouchAndSoundsAtOnce"/> и <see cref="NoPath_Beeps"/>.
    /// </summary>
    private static readonly Run Second = new(["20261017-port-synth"], "port-20261017-215431.av.jsonl",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cursor|DLG_BATTLE_A"] = "тащимый предмет — CUATTACK/CUATTACKN без «A» при BattleAnim 1 (DragCursor)",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["battle_text|шаблоны"] = "партия: у оригинала показанных промахов не было (bat_show без miss), «Промах» движка — в 20261016e-av",
            ["screen_sounds|DLG_ISO"] = "BEEP — исправлено (NoPath_Beeps, журнал порта старый); AICHAT — исправлено (окно DLG_CHAT_AI, Ин36; журнал порта старый); "
                + "<заклинание>, <шаг> — у оригинала звук эффектов выключен на картах 11–12 (vol_set fx 0 с 60 по 2243 с)",
            ["video|SF"] = "окружение: SF.bik game-dev (640×480, 554 кадра) не тот, что в game-pristine (800×600, 120)",
        });

    /// <summary>Журналы первой пары — для фактов движка ниже.</summary>
    private static (IReadOnlyList<AvEvent> Original, IReadOnlyList<AvEvent> Port) Journals => First.Journals;

    private static void SkipWithoutJournals() => First.SkipWithout();

    [SkippableFact]
    public void Compare_OnlyKnownDiffs() => OnlyKnown(First, frames: 20, volumes: 80);

    [SkippableFact]
    public void Compare_Maps10to12_OnlyKnownDiffs() => OnlyKnown(Second, frames: 15, volumes: 8);

    private void OnlyKnown(Run run, int frames, int volumes)
    {
        run.SkipWithout();
        var (original, port) = run.Journals;
        var diffs = AvJournal.Compare(original, port, UnitSounds.Load(Path.Combine(Data, "sound", "UnitSounds.json")));
        var bad = diffs.Where(d => !d.Ok).ToList();
        foreach (var d in bad) output.WriteLine($"≠ {d.Check} | {d.Key} | {d.Original} | {d.Port}");
        output.WriteLine($"сверок {diffs.Count}, совпало {diffs.Count(d => d.Ok)}, расхождений {bad.Count}");
        var keys = bad.Select(d => $"{d.Check}|{d.Key}").ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(keys, k => !run.Fixed.ContainsKey(k) && !run.Open.ContainsKey(k));   // новое расхождение — разобрать
        Assert.DoesNotContain(run.Known, k => !keys.Contains(k));   // ушло — убрать строку из Fixed или Open
        // Точные сверки, которые обязаны совпасть: кадры боя по типу, громкости по месту вызова, затухания переходов, кадры и длительности
        // анимаций карты (в тиках своих часов), ролик INTRO.
        Assert.All(diffs.Where(d => d.Check is "battle_frames" or "sound_volume" or "music_fade" or "battle_volumes" or "map_anim_frames"
                                    || (d.Check == "map_anim" && !d.Key.Contains("мс на кадр", StringComparison.Ordinal))),
            d => Assert.True(d.Ok, d.Key));
        output.WriteLine($"кадры боя {diffs.Count(d => d.Check == "battle_frames")}, громкости {diffs.Count(d => d.Check == "sound_volume")}");
        Assert.True(diffs.Count(d => d.Check == "battle_frames") >= frames);
        Assert.True(diffs.Count(d => d.Check == "sound_volume") >= volumes);
        Assert.Contains(diffs, d => d is { Check: "video", Key: "INTRO", Ok: true });
        Assert.Contains(diffs, d => d is { Check: "map_anim", Key: "CMidAnim2Battle", Ok: true } && d.Original != "анимации нет" && d.Port != "анимации нет");
    }

    /// <summary>Мс на кадр боя у журнала: от замаха <c>HMOV</c> юнита до его следующей строки <c>bat_anim</c> (покой), на кадр замаха; медиана.</summary>
    private static double BattleFrameMs(IReadOnlyList<AvEvent> events)
    {
        var periods = new List<double>();
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is not { Ev: "bat_anim" } swing || swing.S("kind") != "HMOV" || swing.L("frames") is not > 0) continue;
            string? unit = swing.S("unit");
            var next = events.Skip(i + 1).Take(200).FirstOrDefault(e => e.Ev == "bat_anim" && e.S("unit") == unit);
            if (next is not null) periods.Add((next.T - swing.T) / (double)swing.L("frames")!.Value);
        }
        periods.Sort();
        return periods.Count == 0 ? 0 : periods[periods.Count / 2];
    }

    /// <summary>
    /// Темп анимаций оригинала 17.10 — ускорение обёртки DisciplesGL (<c>game-dev/Disciple.ini</c>, <c>[Wrapper]</c>: <c>GameSpeed</c> 4,
    /// <c>SpeedEnabled</c> 1, <c>BattleSpeed</c> 10; <see cref="AvJournal.WrapperClockMs"/>), а не движка: тик задач карты 47 мс (а не 66, «открытое»
    /// 16–17.10), кадр боя 33 мс. Движок без обёртки — 66 (<c>0x51d871</c>); порт держит 66 и на карте, и в бою — как движок.
    /// </summary>
    [SkippableFact]
    public void WrapperClocks_GameSpeed4_BattleSpeed10()
    {
        Second.SkipWithout();
        var original = Second.Journals.Original;
        double map = AvJournal.ClockMs(original, 0), battle = BattleFrameMs(original);
        output.WriteLine($"тик карты {map:0.0} мс, кадр боя {battle:0.0} мс");
        Assert.InRange(map, AvJournal.WrapperClockMs(4) - 1, AvJournal.WrapperClockMs(4) + 1);
        Assert.InRange(battle, AvJournal.WrapperClockMs(10) - 2, AvJournal.WrapperClockMs(10) + 2);
        // Задачи карты — те же тики, что у движка без обёртки: начало боя 17 кадров — 16 тиков (тики после старта + старт).
        var battles = original.Where(e => e.Ev == "map_anim" && e.S("class") == "CMidAnim2Battle").ToList();
        Assert.NotEmpty(battles);
        Assert.All(battles, e => Assert.Equal(16, (e.L("ticks") ?? 0) + 1));
    }

    /// <summary>
    /// Смерть в бою: <c>DEATH</c> звучит в конце удара цели <c>HHIT</c> (<c>0x6533fd</c> → <c>0x65363e</c>), одновременно с эффектом смерти, а удар
    /// цели есть только при «дополнительной анимации боя» (<c>0x6517e1</c>). <c>BattleAnim</c> 0 (первая пара): ни одной строки <c>HHIT</c>, смертей
    /// много, <c>DEATH</c> — ни одного; <c>BattleAnim</c> 1 (карта 10): у каждой смерти — <c>DEATH</c> в миг эффекта, через длину <c>HHIT</c> павшего.
    /// Порт так и делает (<c>BattleScreen.HurtShown</c>, <c>DeathAfter</c>).
    /// </summary>
    [SkippableFact]
    public void Death_AtHurtEnd_OnlyWithBattleAnim()
    {
        First.SkipWithout();
        Second.SkipWithout();
        var still = First.Journals.Original;
        Assert.DoesNotContain(still, e => e.Ev == "bat_anim" && e.S("kind") == "HHIT");
        Assert.True(still.Count(e => e.Ev == "bat_effect" && e.L("code") == 1) >= 40);
        Assert.DoesNotContain(still, e => e.Ev == "snd" && e.S("name") == "DEATH");

        var animated = Second.Journals.Original;
        double frame = BattleFrameMs(animated);
        int deaths = 0;
        for (int i = 0; i < animated.Count; i++)
        {
            if (animated[i] is not { Ev: "bat_effect" } death || death.L("code") != 1) continue;
            deaths++;
            string? unit = death.S("unit");
            var hurt = animated.Take(i).Last(e => e.Ev == "bat_anim" && e.S("unit") == unit && e.S("kind") == "HHIT");
            long frames = hurt.L("frames") ?? 0;
            Assert.InRange(death.T - hurt.T, (frames - 1) * frame, (frames + 1) * frame);
            Assert.Contains(animated, e => e.Ev == "snd" && e.S("name") == "DEATH" && Math.Abs(e.T - death.T) <= 20);
        }
        output.WriteLine($"смертей {deaths}, кадр {frame:0.0} мс");
        Assert.True(deaths >= 10);
        Assert.Equal(deaths, animated.Count(e => e.Ev == "snd" && e.S("name") == "DEATH"));
    }

    /// <summary>
    /// Шаг боевого предмета (<c>0x6365f2</c>, <c>BattleItemUse</c>): в миг анимации применения <c>ITEM</c> у носителя — <c>TUCH</c> на цели и два звука
    /// (замах и удар юнита предмета: Сфера грома — <c>unit08a</c> и <c>mage1</c>; зелья — <c>unit161c</c>/<c>unit12b</c> и <c>heal</c>/<c>heal2</c>), надпись
    /// цели — через кадр-два; шаг длится анимацию <c>ITEM</c>. Партия 17.10 у порта: «при применении Талисмана грома не было анимации на гноме».
    /// </summary>
    [SkippableFact]
    public void ItemUse_TouchAndSoundsAtOnce()
    {
        First.SkipWithout();
        Second.SkipWithout();
        int uses = 0;
        foreach (var events in new[] { First.Journals.Original, Second.Journals.Original })
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is not { Ev: "bat_anim" } item || item.S("kind") != "ITEM") continue;
                uses++;
                var near = events.Skip(i).TakeWhile(e => e.T - item.T <= 20).ToList();
                Assert.Contains(near, e => e.Ev == "bat_fx" && e.S("kind") == "TUCH");
                Assert.Equal(2, near.Count(e => e.Ev == "snd" && e.S("via") == "battle"));
                var text = events.Skip(i).FirstOrDefault(e => e.Ev == "bat_text");
                Assert.NotNull(text);
                Assert.InRange(text.T - item.T, 0, 100);
                var end = events.Skip(i).FirstOrDefault(e => e.Ev == "bat_show_end");
                if (end is not null && end.L("step") == 12)
                    Assert.InRange(end.T - item.T, ((item.L("frames") ?? 0) - 2) * 30, ((item.L("frames") ?? 0) + 2) * 50);
            }
        output.WriteLine($"применений {uses}");
        Assert.True(uses >= 3);
    }

    /// <summary>
    /// Щелчок выбранным отрядом туда, куда пути нет, — <c>BEEP</c> (<c>0x4ccaff</c>: <c>0x4cca01</c> не построил путь → <c>0x5b03ce</c>(11)): карта 10
    /// оригинала, каждый щелчок <c>CTaskSelectUnit</c> по клетке с ответом 0 — <c>BEEP</c> в ту же миллисекунду, щелчки с ответом 1 — без него. Порт —
    /// <c>MapView.NoPathSound</c>.
    /// </summary>
    [SkippableFact]
    public void NoPath_Beeps()
    {
        Second.SkipWithout();
        string path = Path.Combine(GameSaveTests.CorpusDir(), Second.Originals[0], "passive-001.jsonl");
        var clicks = JournalCache.Read(path, ["map_click"]).OfType<MapClickEvent>()
            .Where(c => c is { Task: "CTaskSelectUnit", Action: "down", Button: 1, Cell: not null }).ToList();   // мимо клеток карты (панель) — не путь
        var beeps = Second.Journals.Original.Where(e => e.Ev == "snd" && e.S("name") == "BEEP").Select(e => e.T).ToList();
        int refused = 0;
        foreach (var click in clicks)
        {
            bool beeped = beeps.Any(t => Math.Abs(t - click.T) <= 5);
            Assert.Equal(click.Ret == 0, beeped);
            if (click.Ret == 0) refused++;
        }
        output.WriteLine($"щелчков {clicks.Count}, без пути {refused}, BEEP {beeps.Count}");
        Assert.True(refused >= 10);
        Assert.Equal(refused, beeps.Count);
    }

    /// <summary>Музыка движка: основная (меню, бриф, карта) и временная (бой, столица) начинаются сразу полной громкостью, нарастание 1000 мс —
    /// только у продолжения основной (<c>0x5aa388</c>); ровно так задан план порта (<c>data/music/playlists.json</c>).</summary>
    [SkippableFact]
    public void Music_StartsWithoutFadeIn_ResumeFadesIn1000_AsPortPlan()
    {
        SkipWithoutJournals();
        var music = Journals.Original.Where(e => e.Ev == "music").ToList();
        var opens = new Dictionary<string, (int Opens, int FadeIns, SortedSet<long> Ms)>(StringComparer.Ordinal);
        for (int i = 0; i < music.Count; i++)
        {
            if (music[i].S("action") != "open" || music[i].S("kind") is not { } kind) continue;
            long? fadeIn = null;
            for (int j = i + 1; j < music.Count && music[j].S("action") != "open"; j++)
                if (music[j].S("action") == "fade") { fadeIn = music[j].S("dir") == "in" ? music[j].L("ms") : null; break; }
            var (n, f, ms) = opens.GetValueOrDefault(kind, (0, 0, new SortedSet<long>()));
            if (fadeIn is { } v) ms.Add(v);
            opens[kind] = (n + 1, f + (fadeIn is null ? 0 : 1), ms);
        }
        output.WriteLine(string.Join("; ", opens.Select(o => $"{o.Key}: {o.Value.Opens} открытий, нарастаний {o.Value.FadeIns} ({string.Join(",", o.Value.Ms)})")));
        Assert.Equal(0, opens["main"].FadeIns);
        Assert.Equal(0, opens["temp"].FadeIns);
        Assert.Equal(opens["resume"].Opens, opens["resume"].FadeIns);
        Assert.Equal([1000L], opens["resume"].Ms);
        var plan = MusicPlan.Load(Path.Combine(Data, "music", "playlists.json")).Fades;
        Assert.Equal((0, 1000, 1000), (plan.TempIn, plan.ResumeIn, plan.MenuIn));
    }

    /// <summary>Бриф у движка — своя музыка <c>GENBRIEF</c> (основная, <c>0x480b59</c>) при каждом начале и загрузке партии, перед ней меню
    /// затухает: так и в плане порта (<c>briefing</c>).</summary>
    [SkippableFact]
    public void Briefing_PlaysGenbrief()
    {
        SkipWithoutJournals();
        var briefing = Journals.Original.Where(e => e.Ev == "music" && e.S("action") == "open" && e.S("top") == "DLG_SCENARIO_BRIEFING").ToList();
        Assert.True(briefing.Count >= 8, $"брифов {briefing.Count}");
        Assert.All(briefing, e => Assert.Equal(("GENBRIEF", "main"), (e.S("track"), e.S("kind"))));
        Assert.Equal(["GENBRIEF"], MusicPlan.Load(Path.Combine(Data, "music", "playlists.json")).Briefing);
    }

    /// <summary>Голос скуки выбранного отряда (<c>why = idle</c>) у движка звучит только при верхней карте — порт молчит под окнами
    /// (<c>MapView.StepIdle</c>).</summary>
    [SkippableFact]
    public void IdleVoices_OnlyOverMap()
    {
        SkipWithoutJournals();
        var idle = Journals.Original.Where(e => e.Ev == "snd" && e.S("why") == "idle").ToList();
        Assert.NotEmpty(idle);
        Assert.All(idle, e => Assert.Equal("DLG_ISO", e.S("top")));
    }

    /// <summary>Окно настроек: каждый шаг громкости звука — проба <c>SOUNDFX</c> на 100 %, голосов — голос <c>G000UU0021ACK{1|2|3}</c> на
    /// новой громкости голосов (<c>0x4c1b60</c>, <c>0x4c1c41</c>); у музыки пробы нет (<c>GameAudio.Apply</c>).</summary>
    [SkippableFact]
    public void Options_VolumeStepsPreviewSounds()
    {
        SkipWithoutJournals();
        // Шаг ползунка в окне — vol_set и в тот же миг звук пробы; vol_set без звука следом — применение настроек при загрузке партии.
        var events = Journals.Original.Where(e => e.Ev is "vol_set" or "snd").ToList();
        int fx = 0, voice = 0;
        for (int i = 0; i + 1 < events.Count; i++)
        {
            var next = events[i + 1];
            if (events[i].Ev != "vol_set" || next.Ev != "snd" || next.T - events[i].T > AvJournal.SettleMs || next.S("top") != "DLG_OPTIONS")
                continue;
            switch (events[i].S("kind"))
            {
                case "fx":
                    Assert.Equal(("SOUNDFX", 100L), (next.S("name"), next.L("vol")));
                    fx++;
                    break;
                case "voice":
                    Assert.Matches("^G000UU0021ACK[123]$", next.S("name") ?? "");
                    Assert.Equal(events[i].L("value"), next.L("vol"));
                    voice++;
                    break;
                default:
                    Assert.Fail($"проба у громкости {events[i].S("kind")}");
                    break;
            }
        }
        output.WriteLine($"проб звука {fx}, голосов {voice}");
        Assert.True(fx >= 4 && voice >= 4);
    }

    /// <summary>Руина, взятая боем: движок играет <c>LOOTRUIN</c> и <c>ENTRRUIN</c> окна добычи после закрытия боя (карта 8,
    /// <c>20261016g-port-synth</c>) — порт теперь звучит по уведомлению модели о разграблении, а не только при входе.</summary>
    [SkippableFact]
    public void RuinTakenByBattle_LootSounds()
    {
        SkipWithoutJournals();
        var original = Journals.Original;
        int after = 0;
        for (int i = 0; i < original.Count; i++)
        {
            if (original[i] is not { Ev: "snd" } loot || loot.S("name") != "LOOTRUIN") continue;
            var near = original.Skip(Math.Max(0, i - 30)).Take(60).ToList();
            if (near.Any(e => e.Ev == "music" && e.S("action") == "fade" && e.S("track") is { } t && t.StartsWith("BATTLE", StringComparison.Ordinal))) after++;
            Assert.Contains(near, e => e.Ev == "snd" && e.S("name") == "ENTRRUIN");
        }
        output.WriteLine($"разграблений после боя {after}");
        Assert.True(after >= 1);
    }
}
