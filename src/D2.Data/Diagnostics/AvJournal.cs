using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using D2.Data.Sound;

namespace D2.Data.Diagnostics;

/// <summary>Строка звука, музыки, ролика или анимации из журнала (оригинала или порта — поля одни, схема 7.3).</summary>
/// <param name="Ev">Вид строки: <c>snd</c>, <c>snd_end</c>, <c>music</c>, <c>video</c>, <c>map_anim</c>, <c>bat_anim</c>,
/// <c>bat_fx</c>, <c>bat_text</c>, <c>cursor</c>, <c>frames</c>.</param>
/// <param name="T">Мс от начала журнала.</param>
/// <param name="Fields">Поля строки как есть (строка, число, логическое, null).</param>
public sealed record AvEvent(string Ev, long Seq, long T, IReadOnlyDictionary<string, object?> Fields)
{
    public string? S(string name) => Fields.TryGetValue(name, out var v) ? v as string : null;

    public long? L(string name) => Fields.TryGetValue(name, out var v) && v is long l ? l : null;

    public bool? B(string name) => Fields.TryGetValue(name, out var v) && v is bool b ? b : null;
}

/// <summary>Одна сверка: что сверялось (<see cref="Check"/>, <see cref="Key"/>), что у оригинала и у порта, сошлось ли.</summary>
public sealed record AvDiff(string Check, string Key, string Original, string Port, bool Ok);

/// <summary>
/// Звук, музыка, ролики и анимации из журнала зонда (схема 7.3) и их сверка «оригинал ↔ порт». Две партии никогда не повторяют
/// друг друга ход в ход, поэтому сверяются не последовательности, а правила, которые обязаны совпасть при любой игре:
/// <list type="bullet">
/// <item><b>звуки экранов</b> — какие имена звучат на каждом верхнем экране (<c>snd.top</c>), только у экранов, открытых в обеих
/// партиях;</item>
/// <item><b>громкость и панорама</b> — у звука одного имени и одного <c>via</c> громкость в процентах (<c>vol</c>) одна;</item>
/// <item><b>музыка</b> — смена дорожки по видам (меню, карта, бой, столица) и длительность затухания перед ней (мс, допуск
/// <see cref="FadeTolerance"/>);</item>
/// <item><b>ролики</b> — у ролика одного имени одно число кадров и кадров в секунду;</item>
/// <item><b>анимации карты</b> — у класса <c>CMidAnim2*</c> медиана длительности (допуск <see cref="AnimTolerance"/>) и тиков;</item>
/// <item><b>анимации боя</b> — у пары «тип юнита, вид анимации» одно число кадров (данные одни — кадры обязаны совпасть точно);
/// у эффекта одного имени — одно число кадров.</item>
/// </list>
/// Сверка идёт только по ключам, встреченным в обоих журналах; ключи одной стороны — строки «только у …» с <c>Ok = true</c>
/// (не расхождение, а то, что партия не показала), чтобы отчёт говорил, что ещё сыграть.
/// </summary>
public static partial class AvJournal
{
    /// <summary>Допуск длительности затухания музыки: шаг таймера затухания и кадр.</summary>
    public const double FadeTolerance = 0.15;

    /// <summary>Допуск медианы длительности анимации карты.</summary>
    public const double AnimTolerance = 0.2;

    public static readonly IReadOnlySet<string> Events = new HashSet<string>(StringComparer.Ordinal)
    {
        "snd", "snd_end", "music", "video", "map_anim", "bat_anim", "bat_fx", "bat_effect", "bat_text", "bat_show", "bat_show_end",
        "cursor", "frames", "vol_set", "view",
        // Схема 7.6: портреты боя — вспышки и конец надписей, метки карты, кнопки гаснут и зажигаются.
        "bat_flash", "bat_flash_end", "bat_text_end", "map_sel", "map_path", "ui_enable",
    };

    /// <summary>Строки, которые сверка читает сверх <see cref="Events"/>: смена экранов порта (<c>screen</c>: открыт, закрыт — поправка
    /// звука на миг смены экрана, <see cref="SoundScreens"/>) и настройки показа порта (<c>av_settings</c>). В разбор корпуса
    /// (<c>CorpusReader</c>) они не входят.</summary>
    public static readonly IReadOnlySet<string> Context = new HashSet<string>(StringComparer.Ordinal) { "screen", "av_settings", "turn", "click", "key" };

    /// <summary>Строки журнала оригинала, которые читает сверка меток карты в начале хода (<see cref="TurnMarks"/>): у <c>CorpusReader</c>
    /// они свои (не <see cref="Events"/>), их разбирает <see cref="Parse"/> из сырых строк, а харнесс по корпусу — из записей.</summary>
    public static readonly IReadOnlySet<string> TurnContext = new HashSet<string>(StringComparer.Ordinal) { "ui_click", "map_click", "ai_turn" };

    /// <summary>Строки звука и анимаций журнала <paramref name="path"/> (прочие строки пропускаются без разбора JSON). Журнал открывается с
    /// <see cref="FileShare.ReadWrite"/>: идущая игра держит его на запись.</summary>
    public static IReadOnlyList<AvEvent> Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return Parse(Lines(reader));
    }

    private static IEnumerable<string> Lines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>Строк звука и анимаций, которые не разобрались (оборванная последняя строка журнала упавшей игры), в последнем
    /// <see cref="Parse"/> этого потока.</summary>
    [ThreadStatic] private static int _broken;

    public static int Broken => _broken;

    /// <summary>То же из готовых строк JSONL.</summary>
    public static IReadOnlyList<AvEvent> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var list = new List<AvEvent>();
        _broken = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0 || EvOf(line) is not { } ev || (!Events.Contains(ev) && !Context.Contains(ev) && !TurnContext.Contains(ev))) continue;
            try
            {
                list.Add(ParseLine(line, ev));
            }
            catch (JsonException)
            {
                _broken++;   // оборванная строка (игра упала посреди записи) — не повод терять весь журнал
            }
        }
        // По seq, а не по порядку файла: зонд оригинала берёт seq до вызова, и строка вложенного вызова ложится в файл раньше внешней
        // (старт дорожки 0x67cbb1 и её start/seek внутри). У порта порядок файла и есть порядок seq.
        return [.. list.OrderBy(e => e.Seq)];
    }

    /// <summary>Одна строка звука или анимации (вид <paramref name="ev"/> из <see cref="Events"/>) — разбор
    /// <c>CorpusReader</c> для схемы 7.3 и <see cref="Parse"/>.</summary>
    public static AvEvent ParseLine(string line, string ev)
    {
        ArgumentNullException.ThrowIfNull(line);
        using var doc = D2.Data.Corpus.CorpusReader.ParseJson(line);   // id юнита без кавычек у портретов 7.6 — Зс210
        var root = doc.RootElement;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
            fields[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.TryGetInt64(out long l) ? l : (object)p.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => p.Value.GetRawText(),
            };
        long seq = fields.TryGetValue("seq", out var s) && s is long sl ? sl : 0;
        long t = fields.TryGetValue("t", out var tv) && tv is long tl ? tl : 0;
        return new AvEvent(ev, seq, t, fields);
    }

    private static string? EvOf(string line)
    {
        const string key = "\"ev\":\"";
        int start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? null : line[start..end];
    }

    /// <summary>Строка-граница между журналами в <see cref="Join"/>: сверки с состоянием (стек экранов, вид музыки, ходящий, юниты боя)
    /// на ней начинают заново — у каждого журнала свои <c>seq</c>, время и id юнитов.</summary>
    public const string JournalBreak = "journal";

    /// <summary>Несколько журналов одной стороны подряд — с границей <see cref="JournalBreak"/> между ними.</summary>
    public static IReadOnlyList<AvEvent> Join(IEnumerable<IReadOnlyList<AvEvent>> journals)
    {
        ArgumentNullException.ThrowIfNull(journals);
        var all = new List<AvEvent>();
        foreach (var journal in journals)
        {
            if (all.Count > 0) all.Add(new AvEvent(JournalBreak, 0, 0, new Dictionary<string, object?>()));
            all.AddRange(journal);
        }
        return all;
    }

    /// <summary>Все сверки двух журналов (оригинал, порт); <paramref name="sounds"/> — звуки юнитов для сверки звуков боя по ходящему
    /// (<see cref="BattleSounds"/>), без них она не идёт.</summary>
    public static IReadOnlyList<AvDiff> Compare(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port, UnitSounds? sounds = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(port);
        var diffs = new List<AvDiff>();
        diffs.AddRange(ScreenSounds(original, port, sounds));
        diffs.AddRange(SoundVolumes(original, port));
        diffs.AddRange(BattleSounds(original, port, sounds));
        diffs.AddRange(MusicFades(original, port));
        diffs.AddRange(MusicFadeIns(original, port));
        diffs.AddRange(Videos(original, port));
        diffs.AddRange(MapAnims(original, port));
        diffs.AddRange(MapAnimFrames(original, port));
        diffs.AddRange(BattleFrames(original, port));
        diffs.AddRange(BattleTexts(original, port));
        diffs.AddRange(Cursors(original, port));
        diffs.AddRange(BattleFaces(original, port));
        diffs.AddRange(BattleTextPlaces(original, port));
        diffs.AddRange(BattleTextWraps(original, port));
        diffs.AddRange(TurnMarks(original, port));
        diffs.AddRange(UiEnables(original, port));
        return diffs;
    }

    /// <summary>Звуки по верхнему экрану: экран → имена (без фона мест и без звуков проигрывателя боя — их сверяет
    /// <see cref="BattleSounds"/> по ходящему: какие звуки звучат в бою, решают юниты партии, а не экран). Имя приводится к роли
    /// (<see cref="SoundRole"/>: голос юнита — без типа и номера, шаг — «шаг»), экран — с поправкой на миг смены экрана
    /// (<see cref="SoundScreens"/>): звук порта ложится на тот из своих экранов, где движок его играл. Только экраны обеих партий.</summary>
    public static IEnumerable<AvDiff> ScreenSounds(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port, UnitSounds? sounds = null)
    {
        var a = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var b = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, SortedSet<string>> map, string screen, string name)
        {
            if (!map.TryGetValue(screen, out var set)) map[screen] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(name);
        }
        foreach (var (e, screens) in SoundScreens(original).Where(x => x.Sound.S("via") != "battle"))
            Add(a, screens[0], SoundRole(e.S("name") ?? "?", sounds));
        foreach (var (e, screens) in SoundScreens(port).Where(x => x.Sound.S("via") != "battle"))
        {
            string name = SoundRole(e.S("name") ?? "?", sounds);
            string screen = screens.FirstOrDefault(c => a.TryGetValue(c, out var heard) && heard.Contains(name)) ?? screens[0];
            Add(b, screen, name);
        }
        foreach (var screen in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(screen, out var x)) { yield return new AvDiff("screen_sounds", screen, "экрана нет", Join(b[screen]), true); continue; }
            if (!b.TryGetValue(screen, out var y)) { yield return new AvDiff("screen_sounds", screen, Join(x), "экрана нет", true); continue; }
            var onlyA = x.Except(y, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyB = y.Except(x, StringComparer.OrdinalIgnoreCase).ToList();
            yield return new AvDiff("screen_sounds", screen, onlyA.Count == 0 ? Join(x) : "только у оригинала: " + string.Join(",", onlyA),
                onlyB.Count == 0 ? Join(y) : "только у порта: " + string.Join(",", onlyB), onlyB.Count == 0);
        }
    }

    /// <summary>Имя звука как роль: голос юнита <c>G000UU0072ACK2</c> → <c>&lt;юнит&gt;ACK</c> (тип и вариант решает партия —
    /// какой лидер выбран), звук заклинания → <c>&lt;заклинание&gt;</c>, шаг лидера (имя из списков шага, <paramref name="sounds"/>) →
    /// <c>&lt;шаг&gt;</c>; прочее — как есть.</summary>
    public static string SoundRole(string name, UnitSounds? sounds = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var voice = UnitVoice().Match(name);
        if (voice.Success) return "<юнит>" + voice.Groups[1].Value.ToUpperInvariant();
        if (SpellSound().IsMatch(name)) return "<заклинание>";
        if (sounds is not null && sounds.IsWalkSound(name)) return "<шаг>";
        return name.ToUpperInvariant();
    }

    [GeneratedRegex("^G000UU[0-9]{4}(ACK|IDLE|KILL|ATTK)[0-9]?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnitVoice();

    /// <summary>Звук заклинания (<c>G000SS####</c>): какое заклинание звучит, решает партия.</summary>
    [GeneratedRegex("^G000SS[0-9]{4}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SpellSound();

    /// <summary>Экран в одних именах у двух игр: карта порта — <c>DLG_ISO</c> движка (и панели карты, которые у порта — свои экраны над
    /// ней: <see cref="MapPanels"/>), бой — <c>DLG_BATTLE_A</c> (у движка — ещё <c>DLG_BATTLE_B</c>, экран среды записи), «нет экрана» — «-».</summary>
    public static string Screen(string? top) => top switch
    {
        null or "" or "none" or "-" => "-",
        "MapScreen" => "DLG_ISO",
        "BattleScreen" or "DLG_BATTLE_B" => "DLG_BATTLE_A",
        _ when MapPanels.Contains(top) => "DLG_ISO",
        _ => top,
    };

    /// <summary>Панели и режимы карты, которые порт показывает своими экранами над картой (правая панель, всплывающие ресурсы, палитра,
    /// режим литья, ожидание хода): у движка это окна внутри <c>DLG_ISO</c>, и верхним экраном зонд оригинала пишет <c>DLG_ISO</c>
    /// (журнал <c>20261016e-av</c>: ни одного звука с таким верхним экраном).</summary>
    public static readonly IReadOnlySet<string> MapPanels = new HashSet<string>(StringComparer.Ordinal)
    {
        "DLG_ISO", "DLG_ISO_VIEW", "DLG_STRATEGIC", "DLG_ISO_RESPOPUP", "DLG_ISO_PAL", "DLG_STRAT_SPELL", "DLG_STRAT_WAIT",
    };

    /// <summary>Миг смены экрана, мс: звук и открытие или закрытие окна в одном кадре — для слуха одно и то же.</summary>
    public const long SettleMs = 50;

    /// <summary>
    /// Звуки (кроме фона мест и не нашедшихся) и экраны, к которым звук может относиться: первый — верхний экран строки
    /// (<see cref="Screen"/>), дальше — верхние экраны до и после смен экранов в пределах <see cref="SettleMs"/> вокруг звука (строки
    /// <c>screen</c> журнала порта). Порядок «звук — окно» в одном кадре у двух игр разный и у движка от места к месту свой: покупка
    /// (<c>BUYITEM</c>) звучит, когда окно подтверждения уже закрыто, вход в лавку (<c>ENTRSITE</c>) — до её окна, а мешок (<c>TAKEBAG</c>) —
    /// когда окно добычи уже открыто; у порта узел окна в этот миг уже или ещё в дереве. У журнала оригинала строк <c>screen</c> нет — у
    /// него экран один.
    /// </summary>
    public static IEnumerable<(AvEvent Sound, IReadOnlyList<string> Screens)> SoundScreens(IReadOnlyList<AvEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        // Верх стека экранов после каждой строки screen: время и верх.
        var stack = new List<string>();
        var tops = new List<(long T, string Before, string After)>();
        var at = new int[events.Count];   // сколько смен экрана было до строки i
        var from = new int[events.Count];   // первая смена экрана журнала строки i (Join)
        int start = 0;
        for (int i = 0; i < events.Count; i++)
        {
            at[i] = tops.Count;
            var e = events[i];
            if (e.Ev == JournalBreak)
            {
                stack.Clear();
                start = tops.Count;
            }
            from[i] = start;
            if (e.Ev != "screen") continue;
            string before = stack.Count > 0 ? stack[^1] : "-";
            string name = Screen(e.S("name"));
            if (e.S("action") == "open") stack.Add(name);
            else if (stack.LastIndexOf(name) is var k and >= 0) stack.RemoveAt(k);
            tops.Add((e.T, before, stack.Count > 0 ? stack[^1] : "-"));
        }
        var to = new int[events.Count];   // конец смен экрана журнала строки i: начало следующего журнала
        for (int i = events.Count - 1, end = tops.Count; i >= 0; i--)
        {
            to[i] = end;
            if (events[i].Ev == JournalBreak) end = at[i];
        }
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Ev != "snd" || e.B("ok") == false || e.B("loop") == true) continue;
            var screens = new List<string> { Screen(e.S("top")) };
            for (int j = at[i] - 1; j >= from[i] && e.T - tops[j].T <= SettleMs; j--) Put(screens, tops[j].Before);
            for (int j = at[i]; j < to[i] && tops[j].T - e.T <= SettleMs; j++)
            {
                Put(screens, tops[j].Before);
                Put(screens, tops[j].After);
            }
            yield return (e, screens);
        }

        static void Put(List<string> list, string screen)
        {
            if (!list.Contains(screen)) list.Add(screen);
        }
    }

    /// <summary>Громкость звука в процентах по имени и <c>via</c>: у движка она задаётся местом вызова, а не игрой. Звуки боя — иначе:
    /// одно имя бывает в двух списках юнита (<c>WOLF1B</c> — и замах волка 100 %, и его «больно» 70 %, <c>0x5c1291</c>), и громкость
    /// решает роль, а не имя; у боя сверяется набор громкостей (<c>battle_volumes</c>: лишняя громкость порта — расхождение).</summary>
    public static IEnumerable<AvDiff> SoundVolumes(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        static IEnumerable<AvEvent> Played(IEnumerable<AvEvent> events) =>
            events.Where(e => e.Ev == "snd" && e.B("ok") != false && e.B("loop") != true && e.L("vol") is not null);
        var battleA = new SortedSet<long>(Played(original).Where(e => e.S("via") == "battle").Select(e => e.L("vol")!.Value));
        var battleB = new SortedSet<long>(Played(port).Where(e => e.S("via") == "battle").Select(e => e.L("vol")!.Value));
        if (battleA.Count > 0 && battleB.Count > 0)
            yield return new AvDiff("battle_volumes", "battle", string.Join(",", battleA), string.Join(",", battleB), battleB.IsSubsetOf(battleA));
        static Dictionary<string, SortedSet<long>> Vols(IEnumerable<AvEvent> events) => Played(events)
            .Where(e => e.S("via") != "battle")
            .GroupBy(e => $"{(e.S("name") ?? "?").ToUpperInvariant()}@{e.S("via")}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new SortedSet<long>(g.Select(e => e.L("vol")!.Value)), StringComparer.Ordinal);
        var a = Vols(original);
        var b = Vols(port);
        foreach (var key in a.Keys.Intersect(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            yield return new AvDiff("sound_volume", key, string.Join(",", a[key]), string.Join(",", b[key]), a[key].SetEquals(b[key]));
    }

    /// <summary>Вид дорожки по имени (опись <c>Music</c>, <c>data/music/playlists.json</c>).</summary>
    public static string TrackKind(string track)
    {
        string t = Path.GetFileNameWithoutExtension(track ?? "").ToUpperInvariant();
        if (t.StartsWith("MENU", StringComparison.Ordinal)) return "menu";
        if (t == "GENBRIEF") return "briefing";
        if (t.StartsWith("BATTLE", StringComparison.Ordinal)) return "battle";
        if (t.StartsWith("INGAME", StringComparison.Ordinal) || t.StartsWith("AMB", StringComparison.Ordinal)) return "map";
        return CapitalTrack().IsMatch(t) ? "capital" : "other";
    }

    [GeneratedRegex("^[A-Z]+TRK[0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex CapitalTrack();

    /// <summary>Переходы музыки: «вид до → вид после» → затухание перед открытием новой дорожки (мс, медиана).</summary>
    public static IEnumerable<AvDiff> MusicFades(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        var a = Transitions(original);
        var b = Transitions(port);
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(key, out var x)) { yield return new AvDiff("music_fade", key, "перехода нет", Ms(b[key]), true); continue; }
            if (!b.TryGetValue(key, out var y)) { yield return new AvDiff("music_fade", key, Ms(x), "перехода нет", true); continue; }
            double mx = Median(x), my = Median(y);
            bool ok = Math.Abs(mx - my) <= Math.Max(50, mx * FadeTolerance);
            yield return new AvDiff("music_fade", key, Ms(x), Ms(y), ok);
        }
    }

    /// <summary>Переход музыки — открытие дорожки другого вида; затухание — последняя строка <c>fade</c> вниз перед ним (длительность,
    /// заданная игрой), а в журнале без строк <c>fade</c> — последняя смена громкости вниз (<c>volume</c>, сводка шагов); 0 — без
    /// затухания. Затухание уже остановленной дорожки (после её <c>stop</c>, до нового <c>start</c>) не в счёт: движок на брифе гасит меню
    /// второй раз за 1 мс (<c>fade … then stop</c>), когда оно уже затухло 700 мс и остановлено (журнал <c>20261017-port-synth</c>, три брифа).</summary>
    private static Dictionary<string, List<long>> Transitions(IReadOnlyList<AvEvent> events)
    {
        var map = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        string? kind = null;
        long fade = 0;
        bool fades = events.Any(e => e.Ev == "music" && e.S("action") == "fade");
        var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events.Where(e => e.Ev is "music" or JournalBreak))
        {
            if (e.Ev == JournalBreak)
            {
                (kind, fade) = (null, 0);
                stopped.Clear();
                continue;
            }
            string track = e.S("track") ?? "";
            switch (e.S("action"))
            {
                case "stop":
                    stopped.Add(track);
                    break;
                case "start":
                    stopped.Remove(track);
                    break;
                case "fade" when e.S("dir") == "out" && !stopped.Contains(track):
                    fade = e.L("ms") ?? 0;
                    break;
                case "volume" when !fades && e.L("to") < e.L("from"):
                    fade = e.L("ms") ?? 0;
                    break;
                case "open":
                    stopped.Remove(track);   // открытая заново дорожка снова играет (и без строки start)
                    string next = TrackKind(e.S("track") ?? "");
                    if (kind is not null && kind != next)
                    {
                        string key = $"{kind}→{next}";
                        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<long>();
                        list.Add(fade);
                    }
                    kind = next;
                    fade = 0;
                    break;
            }
        }
        return map;
    }

    /// <summary>Нарастание новой дорожки по переходу «вид до → вид после» (мс, медиана; 0 — дорожка сразу полной громкостью): строка
    /// <c>fade</c> вверх сразу после открытия (у движка — <c>CMusicFader</c> из <c>endTemp</c> <c>0x5aa30e</c>: продолжение основной).</summary>
    public static IEnumerable<AvDiff> MusicFadeIns(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        var a = FadeIns(original);
        var b = FadeIns(port);
        foreach (var key in a.Keys.Intersect(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            double mx = Median(a[key]), my = Median(b[key]);
            yield return new AvDiff("music_fade_in", key, Ms(a[key]), Ms(b[key]), Math.Abs(mx - my) <= Math.Max(50, mx * FadeTolerance));
        }
    }

    /// <summary>Переход «вид до → вид после» → нарастание после открытия дорожки (0 — нет: до следующего открытия или затухания вниз
    /// строки <c>fade in</c> не было). Двойное открытие одной дорожки подряд у движка (основная, затем её продолжение, <c>0x5aa388</c>) —
    /// одно открытие. Открытие сразу за концом прежней дорожки (<c>end</c>: следующая основная плейлиста) — свой переход «… (после конца)»:
    /// у движка основная начинается без нарастания, а продолжение после боя, столицы и выхода в меню — с нарастанием 1000 мс, и в одном
    /// ключе «меню → меню» эти два случая смешались бы.</summary>
    private static Dictionary<string, List<long>> FadeIns(IReadOnlyList<AvEvent> events)
    {
        var map = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        string? kind = null;
        var music = events.Where(e => e.Ev is "music" or JournalBreak).ToList();
        for (int i = 0; i < music.Count; i++)
        {
            if (music[i].Ev == JournalBreak) kind = null;
            if (music[i].S("action") != "open") continue;
            if (NextOpen(music, i) is { } again && music[again].S("track") == music[i].S("track") && music[again].T - music[i].T <= SettleMs)
                continue;
            string next = TrackKind(music[i].S("track") ?? "");
            bool afterEnd = Enumerable.Range(0, i).Reverse().Select(k => music[k])
                .FirstOrDefault(m => m.S("action") is not ("volume" or "start" or "stop" or "pause" or "seek"))?.S("action") == "end";
            long fadeIn = 0;
            for (int j = i + 1; j < music.Count && music[j].S("action") != "open"; j++)
            {
                if (music[j].S("action") != "fade") continue;
                if (music[j].S("dir") == "in") fadeIn = music[j].L("ms") ?? 0;
                break;
            }
            if (kind is not null)
            {
                string key = $"{kind}→{next}" + (afterEnd ? " (после конца)" : "");
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<long>();
                list.Add(fadeIn);
            }
            kind = next;
        }
        return map;
    }

    private static int? NextOpen(List<AvEvent> music, int from)
    {
        for (int j = from + 1; j < music.Count; j++)
            if (music[j].S("action") == "open") return j;
        return null;
    }

    /// <summary>Ролики: имя → кадров и кадров в секунду (у одного файла — одно и то же).</summary>
    public static IEnumerable<AvDiff> Videos(IReadOnlyList<AvEvent> original, IReadOnlyList<AvEvent> port)
    {
        static Dictionary<string, string> Opened(IEnumerable<AvEvent> events) => events
            .Where(e => e.Ev == "video" && e.S("action") == "open")
            .GroupBy(e => Path.GetFileNameWithoutExtension(e.S("name") ?? "?").ToUpperInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(e => $"{e.L("frames")}@{Num(e.Fields.GetValueOrDefault("rate"))}").Distinct()),
                StringComparer.Ordinal);
        var a = Opened(original);
        var b = Opened(port);
        foreach (var key in a.Keys.Intersect(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            yield return new AvDiff("video", key, a[key], b[key], a[key] == b[key]);
    }

    private static string Join(IEnumerable<string> names) => string.Join(",", names);

    private static string Ms(List<long> values) => $"{Median(values):0} мс (n={values.Count})";

    private static string Num(object? v) => v switch
    {
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        _ => "?",
    };

    private static double Median(IEnumerable<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
