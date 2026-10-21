using D2.Data.Corpus;

namespace D2.Data.Ui;

/// <summary>Числа прогона импорта: покрытие и то, что в журнале выглядит неверно. Печатается инструментом
/// <c>tools/UiFlowImport</c> и сводится в <c>docs/ui-flow.md</c>.</summary>
public sealed record UiFlowReport
{
    public int Clicks { get; init; }
    public int ClicksNoChange { get; init; }
    public int ClicksUnnamed { get; init; }
    public int ToggleDuplicates { get; init; }
    public int EngineBursts { get; init; }
    public int StackMismatches { get; init; }
    public int ShowRefused { get; init; }
    public int Clears { get; init; }
    public int MsgBoxes { get; init; }
    public int MsgBoxesUnpaired { get; init; }
    public int OpenFailed { get; init; }
    public IReadOnlyList<string> SeenScreens { get; init; } = [];
    public IReadOnlyList<string> ClickedElements { get; init; } = [];
    public IReadOnlyList<string> UnknownNames { get; init; } = [];
}

/// <summary>Граф экранов из журналов зонда (схема 7.0, <c>docs/corpus-schema.md</c>).
///
/// <para><b>Правило пары.</b> Журнал читается по <c>seq</c> (порядок вызовов) в потоке интерфейса. Каждое
/// <c>ui_click</c> открывает <i>вспышку</i>: следующие события того же потока, пока не случится одно из —
/// следующее <c>ui_click</c>; <c>input</c> (игрок нажал что-то ещё); разрыв по <c>t</c> больше
/// <see cref="GapMs"/>. Пока наверху стека промежуточный экран (<see cref="Transitions"/>) или стек пуст
/// (<c>top = none</c>: старый экран снят, новый ещё строится), вспышка не рвётся ни по разрыву, ни по
/// <c>input</c> — так ловится меню, которое строится две с половиной секунды после анимации смены
/// (<c>DLG_PROTOCOL</c>). Итог вспышки:</para>
/// <list type="bullet">
/// <item>показан экран не из <see cref="Transitions"/> — первый такой и есть цель (окно, всплывшее следом над
/// ней, — «Ход 21» над картой — остаётся в <c>Message</c>); если верхний на момент щелчка экран снят —
/// <see cref="UiFlowAction.Goto"/>, иначе <see cref="UiFlowAction.Open"/>; первый промежуточный — <c>Via</c>;</item>
/// <item>цели нет, верхний снят — <see cref="UiFlowAction.Close"/>;</item>
/// <item>стек не менялся, но диалогов открыто больше, чем закрыто (по имени) — <see cref="UiFlowAction.Panel"/>
/// с первым таким диалогом; пересоздание одного и того же (<c>DLG_ISO_PAL</c>) взаимно гасится;</item>
/// <item>иначе <see cref="UiFlowAction.None"/>.</item>
/// </list>
/// <para>События стека вне вспышек щелчков начинают свою вспышку с тем же правилом: источник — верхний экран до
/// неё, элемента нет (<c>Element = null</c>: карта, правая кнопка, ход ИИ, событие). Смена фазы —
/// <c>game_phase</c> внутри вспышки (<c>enter</c> — партия, <c>leave</c> — меню) и показ/снятие экрана боя.</para>
///
/// <para><b>Где правило ошибается.</b> (1) Обработчик, который меняет экран позже <see cref="GapMs"/> без
/// промежуточного экрана, даст <c>None</c> плюс отдельный переход без элемента. (2) Два щелчка внутри одного
/// кадра (кнопка и её же переключатель — дубль зонда, см. <see cref="UiFlowReport.ToggleDuplicates"/>)
/// — дубль отбрасывается, остаётся <c>toggle</c>. (3) Если игрок щёлкнул, пока движок был занят, и событие
/// щелчка пришло до смены экрана от предыдущего действия, смена припишется новому щелчку. Обратное тоже: драйвер
/// партий ИИ жмёт пробел через 78 мс после «Продолжить» (<c>20260918-probe65</c>, seq 8211139) — нажатие рвёт
/// вспышку раньше, чем экран сменился, и переход уходит в «без элемента». Порог на время нажатия пробовался:
/// он же приписывает щелчку «следующий лидер» двойной щелчок по лидеру, сделанный следом, — хуже. (4) Экран, у которого
/// нет своего диалога в стеке (<c>DLG_ISO</c> — рамка трёх видов), различается по классу — таблица
/// <see cref="ViewByClass"/>.</para></summary>
public static partial class UiFlowImport
{
    /// <summary>Разрыв во времени, после которого события уже не следствие щелчка. Самое долгое честное
    /// следствие в журналах — 344 мс (конец хода: смена панели карты на панель ожидания).</summary>
    public const long GapMs = 500;

    /// <summary>События, которые нужны импорту; прочие журнал читать незачем.</summary>
    public static readonly IReadOnlyCollection<string> Events =
    [
        "ui_open", "ui_close", "ui_show", "ui_show_refused", "ui_hide", "ui_clear", "ui_click", "ui_msgbox",
        "game_phase", "input",
    ];

    /// <summary>Промежуточные экраны: анимация смены меню, «подождите», заставки и загрузка (в том числе быстрая
    /// загрузка сохранения из партии — <c>CMenuQuickLoad</c>, <c>CMenuLoadSendMessageInterf</c>).</summary>
    public static readonly IReadOnlySet<string> Transitions = new HashSet<string>(StringComparer.Ordinal)
    {
        "DLG_MENU_TRANS", "DLG_WAIT", "CMenuFullScreenAnimSnd", "CAnimInterf", "CLoadingIntf", "CMenuQuickLoad",
        "CMenuLoadSendMessageInterf",
    };

    /// <summary><c>DLG_ISO</c> — общая рамка карты, города и дипломатии: зонд называет стек по первому диалогу,
    /// поэтому вид различается по классу экрана.</summary>
    public static readonly IReadOnlyDictionary<string, string> ViewByClass = new Dictionary<string, string>
    {
        ["CStratInterf"] = "DLG_STRATEGIC",
        ["CCapitalInterf"] = "DLG_CAPITAL",
        ["CDiplomacyInterf"] = "DLG_DIPLOMACY",
    };

    /// <summary>Имена среды записи, которых нет в разметке игры: <c>DLG_BATTLE_B</c> — широкий экран боя из
    /// DisciplesGL (<c>Hooks.cpp</c>, <c>dlgNames</c>), те же элементы, что у <c>DLG_BATTLE_A</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["DLG_BATTLE_B"] = "DLG_BATTLE_A",
    };

    public const string BattleScreen = "DLG_BATTLE_A";

    /// <summary>Журналы, из которых собран <c>data/ui/flow.json</c> (пути от корня корпуса): живая партия
    /// с проходом по всем разделам меню и две партии ИИ.</summary>
    public static readonly IReadOnlyList<string> DefaultJournals =
        ["20260919-human/passive-001.jsonl", "20260918-probe65/passive-001.jsonl", "20260920-ui/passive-001.jsonl",
         "20260921-ui/passive-001.jsonl", "20260921-ui/passive-002.jsonl", "20260921-ui/passive-003.jsonl",
         "20260921-ui/passive-004.jsonl", "20260922-ui/passive-001.jsonl"];

    /// <summary>Граф из журналов корпуса <paramref name="corpusDir"/>.</summary>
    public static (UiFlow Flow, UiFlowReport Report) FromCorpus(
        string corpusDir, IEnumerable<string> journals, IReadOnlySet<string> screenIds) =>
        Build(journals.Select(j => (j, CorpusReader.Read(Path.Combine(corpusDir, j), Events))), screenIds);

    /// <summary>Вывести граф из журналов. <paramref name="journals"/> — (имя для <c>Sources</c>, события);
    /// <paramref name="screenIds"/> — Id экранов <c>screens.json</c>: всё прочее уходит в экраны движка.</summary>
    public static (UiFlow Flow, UiFlowReport Report) Build(
        IEnumerable<(string Name, IEnumerable<CorpusEvent> Events)> journals, IReadOnlySet<string> screenIds)
    {
        var acc = new Accumulator(screenIds);
        var sources = new List<string>();
        foreach (var (name, events) in journals)
        {
            sources.Add(name);
            new Pass(acc, UiThread(events)).Run();
        }

        return (acc.ToFlow(sources), acc.ToReport());
    }

    /// <summary>События потока интерфейса (поток первого <c>ui_*</c>) по <c>seq</c>.</summary>
    private static List<CorpusEvent> UiThread(IEnumerable<CorpusEvent> events)
    {
        var all = events.Where(e => e is UiOpenEvent or UiCloseEvent or UiStackEvent or UiClearEvent or UiClickEvent
            or UiMsgBoxEvent or GamePhaseEvent or InputEvent).ToList();
        var first = all.FirstOrDefault(e => e is not InputEvent);
        if (first is null) return [];
        return all.Where(e => e.Tid == first.Tid).OrderBy(e => e.Seq).ToList();
    }

    internal static string Logical(string screen, string cls)
    {
        if (Aliases.TryGetValue(screen, out var a)) return a;
        return screen == "DLG_ISO" && ViewByClass.TryGetValue(cls, out var v) ? v : screen;
    }

    internal static string Alias(string name) => Aliases.TryGetValue(name, out var a) ? a : name;

    internal static long T(CorpusEvent e) => e switch
    {
        UiOpenEvent x => x.T, UiCloseEvent x => x.T, UiStackEvent x => x.T, UiClearEvent x => x.T,
        UiClickEvent x => x.T, UiMsgBoxEvent x => x.T, GamePhaseEvent x => x.T, InputEvent x => x.T, _ => 0,
    };
}
