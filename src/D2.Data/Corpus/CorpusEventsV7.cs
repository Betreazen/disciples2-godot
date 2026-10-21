namespace D2.Data.Corpus;

// Схема 7.0: интерфейс и игровой цикл (probeui.cpp) — граф экранов из одной партии. Имена экранов и элементов —
// имена из Interf.dlg (DLG_*, BTN_*, TOG_*, LBOX_*); класс — RTTI объекта-экрана движка, вид — по его базам
// (menu — CMenuBase, popup — CInterfDialog, fullscreen — CInterfFullScreen, other — прочее).
// Схема 7.1 добавила поля в конец записей; в журналах 7.0 их нет — там они null (кроме выводимых, см. ниже).

/// <summary><c>ui_open</c> (7.0) — движок создал диалог из <c>.dlg</c> (воронка <c>0x5c8110</c>). <see cref="Dialog"/> —
/// адрес объекта (пара к <see cref="UiCloseEvent"/>), <see cref="ParentClass"/>/<see cref="Kind"/> — экран-хозяин,
/// <see cref="Top"/> — верхний экран стека в момент создания, <see cref="Ok"/> — диалог нашёлся в разметке.
/// 7.1: <see cref="Parent"/> — адрес хозяина (равен <see cref="UiStackEvent.Iface"/> его показа; класс хозяина здесь
/// читается из конструктора — базовый, окончательный — в <c>ui_show</c>), <see cref="TopClass"/> — класс верхнего,
/// <see cref="Base"/> — имя в <c>Interf.dlg</c> для экрана среды записи (<c>DLG_BATTLE_B</c> → <c>DLG_BATTLE_A</c>).</summary>
public sealed record UiOpenEvent(long Seq, long Tid, long T, string Name, string Dialog, string ParentClass,
    string Kind, string Top, bool Ok, string RetAddr, string? Parent = null, string? TopClass = null,
    string? Base = null) : CorpusEvent(Seq, Tid);

/// <summary><c>ui_close</c> (7.0) — деструктор диалога (<c>0x50b6c6</c>).</summary>
public sealed record UiCloseEvent(long Seq, long Tid, long T, string Name, string Dialog) : CorpusEvent(Seq, Tid);

/// <summary><c>ui_show</c>, <c>ui_show_refused</c>, <c>ui_hide</c> (7.0) — стек экранов менеджера интерфейса;
/// <see cref="Action"/> — <c>show</c>/<c>show_refused</c>/<c>hide</c>, <see cref="Depth"/> — длина стека после
/// вызова, <see cref="Top"/> — верхний экран после вызова. 7.1: <see cref="Iface"/> — адрес объекта-экрана (пара
/// показ/снятие без эвристик), <see cref="TopClass"/> — класс верхнего; <c>seq</c> взят до вызова оригинала, поэтому
/// экран, показанный изнутри показа другого, идёт в журнале после него.</summary>
public sealed record UiStackEvent(long Seq, long Tid, long T, string Action, string Screen, string Class, string Kind,
    long Depth, string Top, string RetAddr, string? Iface = null, string? TopClass = null, string? Base = null)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ui_clear</c> (только 7.0) — менеджер снял все экраны разом (<c>0x53ccbf</c>). В живых журналах не
/// встречался ни разу; с 7.1 хук снят — движок эту функцию не зовёт.</summary>
public sealed record UiClearEvent(long Seq, long Tid, long T, long DepthBefore, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>ui_click</c> (7.0) — сработал обработчик элемента: <see cref="Kind"/> <c>button</c> (с
/// <see cref="Hotkey"/> и кодом <see cref="Key"/>, если нажато клавишей), <c>toggle</c> (<see cref="Checked"/> —
/// новое состояние), <c>lbox</c> (<see cref="Index"/> — строка). <see cref="Screen"/> —
/// диалог элемента, <see cref="Element"/> — имя в нём; пустая строка — не прочитано.
/// 7.1: <see cref="What"/> у <c>lbox</c> — <c>select</c> (щелчок по строке) или <c>scroll</c> (кнопка прокрутки,
/// стрелки, страницы); в журналах 7.0 выводится из <see cref="RetAddr"/>. Переключатель пишется один раз
/// (<c>toggle</c>, с горячей клавишей своей кнопки). <see cref="Child"/>/<see cref="Area"/> — у элемента без имени
/// (построен кодом, а не разметкой): индекс ребёнка диалога и прямоугольник.</summary>
public sealed record UiClickEvent(long Seq, long Tid, long T, string Kind, string Screen, string Element,
    bool? Hotkey, long? Key, bool? Checked, long? Index, string Top, string RetAddr, string? What = null,
    long? Child = null, IReadOnlyList<long>? Area = null, string? TopClass = null, string? Base = null)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ui_text</c> (7.0) — id текста (<c>TApp.dbf</c>/<c>TAppEdit</c>), впервые взятый на экране
/// <see cref="Screen"/>; <see cref="Opening"/> — во время создания этого диалога (подпись разметки), иначе на
/// уже показанном верхнем экране. Пишется по ключу, число вызовов — в <c>counts</c>.</summary>
public sealed record UiTextEvent(long Seq, long Tid, long T, string Screen, string Id, bool Opening)
    : CorpusEvent(Seq, Tid);

/// <summary><c>ui_msgbox</c> (7.0) — окно сообщения (<c>CMidgardMsgBox</c> <c>0x5c7771</c>): текст (UTF-8),
/// <see cref="LastTextId"/> — последний id, разрешённый до него (ранг 2: обычно id самого текста),
/// <see cref="YesNo"/> — кнопки «да/нет» вместо «ок».</summary>
public sealed record UiMsgBoxEvent(long Seq, long Tid, long T, string Text, string? LastTextId, bool YesNo, string Top,
    string RetAddr, string? TopClass = null) : CorpusEvent(Seq, Tid);

/// <summary><c>screenshot</c> (7.1) — снимок экрана клавишей F12 (<c>0x5aaa38</c>): <see cref="File"/> — самый новый
/// <c>ScreenShots\ScreenShotNNN.bmp</c> после снимка, <see cref="Camera"/> — клетка в центре вида карты (<c>0x540b6c</c>;
/// null вне карты), <see cref="Display"/> — размер экрана и настройки картинки, <see cref="AiPlayer"/> — игрок, чей ход ИИ
/// начался последним.</summary>
/// <see cref="Via"/>: <c>key</c> — нажатие F12 из хука ввода (под обёрткой DisciplesGL снимок пишет она, файла ещё нет —
/// ищется по <see cref="TimeMs"/>, мс от 1970), <c>engine</c> — снимок самого движка.
public sealed record ScreenshotEvent(long Seq, long Tid, long T, string? File, ShotCamera? Camera, ShotDisplay? Display,
    string? AiPlayer, string Top, string? TopClass, string? Via = null, long? TimeMs = null) : CorpusEvent(Seq, Tid);

/// <summary>Клетка в центре вида карты и два необязательных выхода той же функции (<c>dx</c>, <c>dy</c>, как есть).</summary>
public sealed record ShotCamera(long X, long Y, long Dx, long Dy);

/// <summary>Настройки экрана на момент снимка (<c>GameSettings</c>).</summary>
public sealed record ShotDisplay(long Width, long Height, bool UseD3d, bool Fullscreen, bool AnimatedIso, bool IsoAnim,
    bool FlippedBattle, bool TurnSummary);

/// <summary><c>map_click</c> (7.1, часть 3) — нажатие кнопки мыши в виде карты (<c>CStratInterf::handleMouse</c>
/// <c>0x48e3da</c>, поток интерфейса; только нажатия и двойные щелчки). <see cref="Msg"/> — оконное сообщение,
/// <see cref="Button"/> — 1 левая, 2 правая, 3 средняя, <see cref="Action"/> — <c>down</c>/<c>dblclk</c>;
/// <see cref="X"/>/<see cref="Y"/> — точка, как её получил обработчик. <see cref="Cell"/> — клетка карты под точкой так,
/// как её считает задача карты (<c>0x540f6a</c>, как <c>CTaskStrat</c> <c>0x4d3f71</c>); null — точка не на карте.
/// <see cref="Objects"/> — id объектов плана карты в клетке до обработки щелчка (отряд, крепость, сайт, руины, ориентир,
/// кристалл, сумка, гробница, жезл, дорога — в этом порядке), null — клетки нет. <see cref="Task"/> — класс текущей
/// задачи вида (<c>CTaskStrat</c>, <c>CTaskSpell</c>, …) до щелчка, <see cref="Busy"/> — <c>[this+0xf0]</c> (больше 0 —
/// вид щелчок не принял), <see cref="Ret"/> — ответ обработчика (не 0 — щелчок взят).</summary>
public sealed record MapClickEvent(long Seq, long Tid, long T, long Msg, long Button, string Action, long X, long Y,
    MapClickCell? Cell, IReadOnlyList<string>? Objects, string? Task, long Busy, long Ret, string Top, string? TopClass)
    : CorpusEvent(Seq, Tid);

/// <summary>Клетка карты щелчка.</summary>
public sealed record MapClickCell(long X, long Y);

/// <summary><c>rng_state</c> (7.1, часть 3) — состояние генератора боя R250, с которым сервер открывает сценарий
/// (хук <c>game_start</c> <c>0x429603</c> до оригинала, серверный поток): после пересева новой игры или восстановления
/// из сохранения — если они были. <see cref="What"/> — <c>new</c>/<c>load</c>; <see cref="N"/>, <see cref="Index"/>,
/// <see cref="Table"/>, <see cref="Wide"/> — как у <see cref="RngSeedEvent"/> (<c>n</c>, <c>index_after</c>,
/// <c>table</c>, <c>wide</c>). <see cref="SavedIndex"/> — индекс из заголовка сохранения (<c>+0x669</c>), null — заголовка
/// нет (новая игра, путь <c>0x429c92</c>); <see cref="SavedSame"/> — состояние процесса совпало с сохранённым (генератор восстановлен из файла).
/// <see cref="Random"/> — <c>GameSettings::random</c> (<c>Disciple.ini</c> «Random»): при true загрузка генератор не
/// трогает — он идёт дальше из состояния процесса, а новая игра пересевает от времени.</summary>
public sealed record RngStateEvent(long Seq, long Tid, long T, string What, long N, long Index, long Wide,
    IReadOnlyList<ushort> Table, long? SavedIndex, bool? SavedSame, bool? Random, string RetAddr) : CorpusEvent(Seq, Tid);

/// <summary><c>game_phase</c> — <see cref="What"/>: в 7.0 <c>enter</c> (создана фаза партии <c>CPhaseGame</c>, новая
/// игра или загрузка — неразличимы, строка после заставки сценария) и <c>leave</c> (выход в меню); в 7.1 вместо
/// <c>enter</c> — <c>new</c> или <c>load</c> по открытию сценария сервером, строка до конструктора (раньше заставки);
/// <c>enter</c> в 7.1 — сценарий не открывался в этом процессе.</summary>
public sealed record GamePhaseEvent(long Seq, long Tid, long T, string What, string Phase, string Top,
    string? TopClass = null) : CorpusEvent(Seq, Tid)
{
    /// <summary>Вход в партию: <c>enter</c> (7.0), <c>new</c>, <c>load</c> (7.1).</summary>
    public bool IsEnter => What is "enter" or "new" or "load";
}
