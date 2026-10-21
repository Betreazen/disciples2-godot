using D2.Data.Corpus;
using D2.Data.Diagnostics;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Портреты боя и метки карты по живым прогонам зонда 7.6 (<c>docs/probe-av.md</c> §8): вспышки и стойкие состояния портретов, место и срок
/// надписей, эллипс выбранного отряда и путь в начале хода. Журнал оригинала — любой прогон корпуса со строкой <c>start</c> схемы 7.6 и выше
/// (через <see cref="JournalCache"/>); журнал порта — <c>D2_PORT_AV_JOURNAL</c> (путь или пути через «;») или выписка <c>golden/av/*.av.jsonl</c>
/// со строками 7.6. Пока журналов 7.6 нет — пропуск, как у соседей (<see cref="AvHarnessTests"/>).
/// </summary>
[Trait("Category", "Corpus")]
public class AvFaceHarnessTests(ITestOutputHelper output)
{
    private static readonly string[] Checks = ["battle_face", "battle_text_place", "battle_text_persist", "battle_flash_ticks", "battle_text_wrap", "turn_marks", "ui_enable",
        "ui_enable_click"];

    /// <summary>Виды строк сверки: показ (<see cref="AvJournal.Events"/>) и ввод, ходы ИИ для начала хода.</summary>
    private static readonly IReadOnlyCollection<string> Kinds = [.. AvJournal.Events.Concat(AvJournal.TurnContext)];

    /// <summary>Журналы корпуса со схемой не ниже 7.6 — по первой строке (<c>start</c>), весь журнал не читается.</summary>
    private static IReadOnlyList<string> Originals()
    {
        string corpus = GameSaveTests.CorpusDir();
        if (!Directory.Exists(corpus)) return [];
        return [.. Directory.EnumerateFiles(corpus, "passive-*.jsonl", SearchOption.AllDirectories).Where(Schema76).OrderBy(p => p, StringComparer.Ordinal)];
    }

    private static bool Schema76(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? first = reader.ReadLine();
        if (first is null || !first.Contains("\"ev\":\"start\"", StringComparison.Ordinal)) return false;
        int at = first.IndexOf("\"schema\":\"", StringComparison.Ordinal);
        if (at < 0) return false;
        string schema = first[(at + 10)..first.IndexOf('"', at + 10)];
        return Version.TryParse(schema, out var v) && v >= new Version(7, 6);
    }

    /// <summary>Строки сверки журнала оригинала через общий кэш: показ — готовые записи, нажатия, щелчки по карте и ходы ИИ — из записей.</summary>
    private static IReadOnlyList<AvEvent> Original(string path) =>
    [
        .. JournalCache.Read(path, Kinds)
            .Select(e => e switch
            {
                AvProbeEvent a => a.Av,
                UiClickEvent c => Line("ui_click", c.Seq, c.T, ("element", c.Element), ("screen", c.Screen)),
                MapClickEvent m => Line("map_click", m.Seq, m.T, ("action", m.Action)),
                AiTurnEvent t => Line("ai_turn", t.Seq, t.T, ("phase", t.Phase), ("cause", t.Cause)),
                _ => null,
            })
            .OfType<AvEvent>()
            .OrderBy(e => e.Seq),
    ];

    private static AvEvent Line(string ev, long seq, long t, params (string Name, object? Value)[] fields) =>
        new(ev, seq, t, fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal));

    /// <summary>Журналы порта 7.6 из <c>D2_PORT_AV_JOURNAL</c> (путь или пути через «;») — свежая партия до выписки.</summary>
    private static IReadOnlyList<IReadOnlyList<AvEvent>> EnvPorts() =>
        Environment.GetEnvironmentVariable("D2_PORT_AV_JOURNAL") is { Length: > 0 } env
            ? [.. env.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).Select(AvJournal.Load).Where(Has76)]
            : [];

    private static bool Has76(IReadOnlyList<AvEvent> journal) => journal.Any(e => e.Ev == "bat_text" && e.Fields.ContainsKey("w"));

    /// <summary>Пара «партия оригинала ↔ журнал порта» со сверками 7.6 и разобранными расхождениями: <c>Fixed</c> — сняты правкой после
    /// партии (журнал порта старый; правку доказывает свежий журнал <see cref="Synth"/> и проверки, названные в причине), <c>Open</c> — правкой не
    /// снимаются.</summary>
    private sealed record Run(string Corpus, string[] Golden, IReadOnlyDictionary<string, string> Fixed, IReadOnlyDictionary<string, string> Open)
    {
        public IEnumerable<string> Known => Fixed.Keys.Concat(Open.Keys);
    }

    /// <summary>Вторая атака у порта до 20.10 шла разом с первой (Ин110): надписи «−N»/«+N» и строка класса второй атаки ложились в одну мс,
    /// вспышка второй гасила первую — сверка спаривает надпись со следующей вспышкой, и всё съезжает на одну.</summary>
    private const string SecondAttack = "Ин110: вторая атака разом с первой — свой шаг (BattleScreen.Numbers, part); журнал Synth и ui-render-check A1o second";

    /// <summary>Партия пользователя 20.10 на полигонах A1–A5 и I1 карты <c>port-synth-1-battle</c>: оригинал <c>corpus/20261020-faces</c> (зонд 7.6,
    /// 401047ec) и порт <c>port-20261020-175130</c> (тот же коммит, до правок 20.10 вечер; выписка — <c>golden/av/port-20261020-175130.faces.av.jsonl</c>:
    /// строки портретов, меток карты, ход и ввод; <c>th</c> → <c>tid</c>).</summary>
    private static readonly Run User = new("20261020-faces", ["port-20261020-175130.faces.av.jsonl"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [@"battle_face|(без надписи: target)"] = SecondAttack + "; Ин111: усиление, дар атаки, превращение себя — вспышка ещё и по показанной записи",
            [@"battle_face|\hC;\vC;\fLarge;+#"] = SecondAttack + " (лечение и «Лечение» исцеления)",
            [@"battle_face|\hC;\vC;\fLarge;-#"] = SecondAttack,
            [@"battle_face|\hC;\vC;Замедление"] = SecondAttack,
            [@"battle_face|\hC;\vC;Лечение"] = SecondAttack,
            [@"battle_face|\hC;\vC;Меняет облик!"] = "Ин111: превращение себя — красным по атаке уже превращённого, затем чёрным",
            [@"battle_face|\hC;\vC;Мороз"] = SecondAttack,
            [@"battle_face|\hC;\vC;Понижение уровня"] = SecondAttack,
            [@"battle_face|\hC;\vC;Пузырь"] = SecondAttack,
            [@"battle_face|\hC;\vC;Разрушение"] = SecondAttack,
            [@"battle_face|\hC;\vC;Уменьшение"] = SecondAttack,
            [@"battle_face|\hC;\vC;Яд"] = SecondAttack,
            [@"battle_text_persist|\hC;\vC;\fMedium;Опыт:\n+#"] = "Зс53: «Опыт: +N» без срока (BattleView.Note persist, перерисовка сборкой панелей); ui-render-check «бой с карты» стойких=",
        },
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Те же полигоны в порте после правок 20.10 вечер: бой каждого полигона целиком с показом (<c>--synth F</c>, <c>D2_BATTLE_PLAYBACK=1</c>,
    /// <c>D2_PORT_PROBE=1</c>; выписки — <c>golden/av/faces-synth/*.av.jsonl</c>). Ходы человека — не те, что у пользователя (проверка выбирает
    /// цели сама), поэтому сверка — «набор порта ⊆ набора оригинала».</summary>
    private static readonly Run Synth = new("20261020-faces",
        [.. new[] { "4-S143KC0005", "6-S143KC0007", "8-S143KC0009", "a-S143KC000b", "c-S143KC000d", "e-S143KC000f" }
            .Select(pair => Path.Combine("faces-synth", $"S143KC000{pair}.av.jsonl"))],
        new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));

    private static Run RunOf(string name) => name == nameof(Synth) ? Synth : User;

    public static TheoryData<string> Runs => [nameof(User), nameof(Synth)];

    /// <summary>Факты движка по журналу 7.6 (то, на что опирается вид порта): надпись — 70 или 150 на 85; вспышка — на 15/10/5 тиков
    /// (скорость боя); лечение «+N» у цели — синим (5); опыт конца боя — стойкая надпись и жёлтое (11); вспышка не пережила срок надписи.
    /// Печатает таблицу «надпись → состояния» — ей и проверять вид порта.</summary>
    [SkippableFact]
    public void Engine_PortraitFacts()
    {
        var originals = Originals();
        Skip.If(originals.Count == 0, "нет журнала оригинала схемы 7.6 в корпусе");
        var events = AvJournal.Join(originals.Select(Original));
        var faces = AvJournal.FaceEvents(events);
        foreach (var g in faces.GroupBy(f => f.Template).OrderBy(g => g.Key, StringComparer.Ordinal))
            output.WriteLine($"{(g.Key.Length == 0 ? "(без надписи)" : g.Key)} → {string.Join(", ", g.Select(AvJournal.FaceLook).Distinct())} ({g.Count()})");

        var texts = events.Where(e => e.Ev == "bat_text" && e.B("found") != false && e.L("w") is > 0).ToList();
        Skip.If(texts.Count == 0, "в журнале 7.6 нет боя с надписями");
        Assert.All(texts, e => Assert.Contains(e.L("w"), new long?[] { 70, 150 }));
        Assert.All(texts, e => Assert.Equal(85, e.L("h")));
        var ticks = events.Where(e => e.Ev == "bat_flash" && e.S("mode") == "flash").Select(e => e.L("ticks")).Distinct().ToList();
        Assert.All(ticks, t => Assert.Contains(t, new long?[] { 5, 10, 15 }));
        foreach (var heal in faces.Where(f => f.Src == "target" && f.Template.StartsWith('+') && f.State is not null))
            Assert.Equal(PortraitFlash.Heal, heal.State);
        foreach (var xp in faces.Where(f => f.Src == "xp"))
            Assert.Equal((PortraitFlash.Xp, "set"), (xp.State ?? -1, xp.Mode));
        Assert.All(texts.Where(e => e.S("src") == "xp"), e => Assert.Equal(true, e.B("persist")));
        // Вспышка держится не дольше надписи (оба срока — +0x44 группы): конец вспышки — «end» на тот же срок, что у надписи.
        var flashMs = events.Where(e => e.Ev == "bat_flash_end" && e.S("how") == "end").Select(e => e.L("ms") ?? 0).ToList();
        var textMs = events.Where(e => e.Ev == "bat_text_end" && e.S("how") == "end").Select(e => e.L("ms") ?? 0).ToList();
        output.WriteLine($"надписей {texts.Count}, вспышек {ticks.Count} видов тиков, мс вспышки {Med(flashMs)}, мс надписи {Med(textMs)}");
        if (flashMs.Count > 0 && textMs.Count > 0) Assert.InRange(Med(flashMs), Med(textMs) * 0.7, Med(textMs) * 1.3);
    }

    private static double Med(List<long> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>Порт против оригинала по сверкам 7.6 (<see cref="AvJournal.BattleFaces"/>, места и сроки надписей, перенос, метки карты в начале
    /// хода) на выписках <c>golden/av</c>: расхождение без строки в <c>Fixed</c>/<c>Open</c> — разобрать; строка, которой нет, — убрать.</summary>
    [SkippableTheory]
    [MemberData(nameof(Runs))]
    public void Port_MatchesEngine(string name)
    {
        var run = RunOf(name);
        string original = Path.Combine(GameSaveTests.CorpusDir(), run.Corpus, "passive-001.jsonl");
        Skip.IfNot(File.Exists(original), $"нет журнала corpus/{run.Corpus}");
        string[] golden = [.. run.Golden.Select(g => Path.Combine(AppContext.BaseDirectory, "golden", "av", g))];
        Skip.If(golden.Any(g => !File.Exists(g)), "нет выписки журнала порта golden/av");
        var bad = Diffs(Original(original), AvJournal.Join(golden.Select(AvJournal.Load)));
        Assert.DoesNotContain(bad, k => !run.Known.Contains(k, StringComparer.Ordinal));
        Assert.DoesNotContain(run.Known, k => !bad.Contains(k));
    }

    /// <summary>Свежий журнал порта (<c>D2_PORT_AV_JOURNAL</c>) против всех журналов 7.6 корпуса: после правок 20.10 расхождений нет — новое
    /// расхождение разобрать и завести в реестр; без переменной — пропуск.</summary>
    [SkippableFact]
    public void Port_FreshJournal_MatchesEngine()
    {
        var originals = Originals();
        Skip.If(originals.Count == 0, "нет журнала оригинала схемы 7.6 в корпусе");
        var ports = EnvPorts();
        Skip.If(ports.Count == 0, "нет журнала порта схемы 7.6 (D2_PORT_AV_JOURNAL)");
        Assert.Empty(Diffs(AvJournal.Join(originals.Select(Original)), AvJournal.Join(ports)));
    }

    /// <summary>Выписки зонда порта о кнопках (<c>golden/av/buttons/*.av.jsonl</c>: строки <c>ui_enable</c> и <c>ui_click</c>, <c>th</c> →
    /// <c>tid</c>) после Зс55–Зс57: бои полигонов <c>--synth K</c> (<c>D2_BATTLE_PLAYBACK=1</c>, «Ждать», «Защищаться» и переключатель групп
    /// щелчком по панели) и три прогона карты (<c>--clicks</c> с шагами <c>pause</c>/<c>aidone</c>): карта 13 — перебор лидеров, отказ жезла,
    /// «Конец хода» и ход ИИ; f7-m6-rods — режим жезла (панель <c>DLG_DROP_BAG</c>); «Растленная власть» день 66 — посох у лидера с
    /// навыком, обмен с соседом, «Конец хода».</summary>
    private static readonly string[] ButtonRuns =
        ["battle-K-A1.av.jsonl", "battle-K-A2.av.jsonl", "battle-K-I1.av.jsonl", "map13.av.jsonl", "rods.av.jsonl", "dwarf.av.jsonl"];

    /// <summary>Расхождения кнопок, которых журналы движка не сыграли: правило движка то же (листинг), сыграть — у оригинала.</summary>
    private static readonly IReadOnlyDictionary<string, string> ButtonsUnplayed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ui_enable|DLG_STRATEGIC/BTN_EXCHANGE"] =
            "обмен зажигается у отряда со своим соседом (0x48df76 → 0x48e0e2); в журналах 7.6 соседнего своего отряда не было — только «гаснет»",
        ["ui_enable_click|DLG_STRATEGIC/BTN_NEXT_LEADER"] =
            "перебор с отряда-обменщика и посохом на отряд без них — «BTN_EXCHANGE− BTN_WAND_SCROLL−»: та же причина (обмена у движка не было)",
    };

    /// <summary>Кнопки порта против всех журналов 7.6 корпуса (<see cref="AvJournal.UiEnables"/>): панель боя гаснет на показ и ход ИИ (Зс55),
    /// жезл, посох и «Охранять» — по выбранному отряду (Зс56), палитра — по панели команд, «Конец хода» — по щелчку (Зс57). Расхождений
    /// нет, кроме не сыгранных у оригинала (<see cref="ButtonsUnplayed"/>).</summary>
    [SkippableFact]
    public void Port_ButtonsMatchEngine()
    {
        var originals = Originals();
        Skip.If(originals.Count == 0, "нет журнала оригинала схемы 7.6 в корпусе");
        string[] golden = [.. ButtonRuns.Select(g => Path.Combine(AppContext.BaseDirectory, "golden", "av", "buttons", g))];
        Skip.If(golden.Any(g => !File.Exists(g)), "нет выписки кнопок golden/av/buttons");
        var port = AvJournal.Join(golden.Select(AvJournal.Load));
        var diffs = AvJournal.UiEnables(AvJournal.Join(originals.Select(Original)), port).ToList();
        foreach (var d in diffs.OrderBy(d => d.Ok)) output.WriteLine($"{(d.Ok ? "=" : "≠")} {d.Check} | {d.Key} | {d.Original} | {d.Port}");
        var bad = diffs.Where(d => !d.Ok).Select(d => $"{d.Check}|{d.Key}").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ButtonsUnplayed.Keys.OrderBy(k => k, StringComparer.Ordinal), bad.OrderBy(k => k, StringComparer.Ordinal));
        // Сыгранное с обеих сторон — точь-в-точь: щелчок «Защищаться»/«Ждать» гасит четыре, «Конец хода» — себя.
        foreach (string key in (string[])["DLG_BATTLE_A/BTN_DEFEND", "DLG_BATTLE_A/BTN_WAIT", "DLG_STRATEGIC/BTN_END_TURN"])
            Assert.Contains(diffs, d => d is { Check: "ui_enable_click", Ok: true } && d.Key == key && !d.Port.StartsWith("нет у", StringComparison.Ordinal));
    }

    private HashSet<string> Diffs(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        var diffs = AvJournal.Compare(original, port).Where(d => Checks.Contains(d.Check)).ToList();
        foreach (var d in diffs.OrderBy(d => d.Ok)) output.WriteLine($"{(d.Ok ? "=" : "≠")} {d.Check} | {d.Key} | {d.Original} | {d.Port}");
        return diffs.Where(d => !d.Ok).Select(d => $"{d.Check}|{d.Key}").ToHashSet(StringComparer.Ordinal);
    }
}
