/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2026 Disciples-Godot probe.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

/*
 * Схема 7.0: интерфейс и игровой цикл - граф экранов из одной партии. Адреса Gog 3.01, прочитаны
 * capstone по Discipl2.exe (ранг 1 - прочитано в листинге, ранг 2 - выведено):
 *
 * ui_open  - 0x5c8110, __stdcall(parent, name, center, p1, p2, childIndex, area), ret 0x1c ->
 *            CDialogInterf*. Единственная воронка создания диалога по имени из .dlg: зовут только
 *            обёртки 0x5c80bf (37 мест: меню CMenuBase::createMenu 0x4dadcf, вид карты, окна) и
 *            0x5c80df (конструктор CPopupDialogInterf 0x5c9c41); внутри - загрузчик 0x50a7a0 на
 *            [0x8388f4] и привязка к родителю слотами 0x5c/0x60. Ранг 1. Место вызова - адрес
 *            возврата из обёртки: обе без SEH и с push ebp, раскладка стека прочитана (ранг 1).
 * ui_close - деструктор CDialogInterf 0x50b6c6 (__thiscall, EH-пролог; vftable 0x6df82c по RTTI
 *            .?AVCDialogInterf@@, слот 0 = 0x50b6aa зовёт его). Ранг 1.
 * ui_show / ui_hide - CInterfManagerImpl (vftable 0x6e1bcc по RTTI): слот 4
 *            showInterface 0x53cac4, слот 5 removeInterface 0x53cb64 (снять верхний), слот 6
 *            hideInterface 0x53cbd1 (верхний - через слот 5, иначе вынимает из списка), слот 7
 *            clearInterfaces 0x53ccbf (с 7.1 не ставится). Список экранов - CInterfManagerImplData+0x18 (длина),
 *            +0x1c (голова), верхний = head->next->data (0x4f81dc). Ранг 1.
 * ui_click - CButtonInterf слот 44 = 0x5318e1 (vftable 0x6e1234): зовёт onClicked-функтор
 *            (buttonData+0xc); флаг «нажато клавишей» buttonData+0x70 читается и сбрасывается там же
 *            (ранг 1). CToggleButton слот 37 = 0x534ba3 (vftable 0x6e15a4): функтор (checked, this)
 *            после смены состояния 0x534bd6 (ранг 1). CListBoxInterf 0x535f0c(index), ret 4: ставит
 *            выбор 0x535644 и зовёт onSelectionConfirmed (+0x74) (ранг 1; что это «подтверждение»,
 *            а не просто смена строки - ранг 2).
 *            Имя элемента: предок-CDialogInterf (vftable 0x6df82c) по цепочке
 *            CInterfaceData+8 (parent); в CDialogInterfData+0x38 - дерево «имя[48] -> индекс»
 *            (голова +0xc, nil +0x14, индекс узла +0x40, имя +0x10), индекс - в векторе детей
 *            CInterfaceData+0x20 (слот 21 0x5303ce -> 0x53d7df). Ранг 1 - из findControl 0x50b70a.
 * ui_text  - едет на хуке ui_refusal (getInterfaceText 0x5c7f06, probeunit.cpp): ключ (экран, id).
 * ui_msgbox - конструктор CMidgardMsgBox 0x5c7771, __thiscall(message, yesNo, buttonHandler,
 *            updateHandler, dialogName), ret 0x14 (адрес - из тулсета midgardmsgbox.cpp, ret
 *            прочитан). Ранг 1; id текста = последний разрешённый getInterfaceText - ранг 2.
 * game_phase - CPhaseGame (RTTI .?AVCPhaseGame@@, vftable 0x6cc9ec): конструктор 0x40529f
 *            (__thiscall, 2 аргумента, ret 8, единственный вызов 0x40c1a4), деструктор 0x40566c.
 *            Ранг 1 - адреса; «вход в партию / выход в меню» - ранг 2.
 *
 * Схема 7.1 (18.09.2026, разбор журнала 20260919-human, docs/ui-flow.md «Что зонд пишет неверно»):
 *  - кнопка внутри переключателя: CToggleButton (vftable 0x6e15a4) строит дочернюю CButtonInterf
 *    (конструктор CButtonInterf 0x531040 зовётся из 0x5347bd) с функтором onClicked = 0x534bd6, который и зовёт слот 37.
 *    Кнопку, чей родитель - переключатель, хук кнопки не пишет, а её горячую клавишу отдаёт toggle.
 *    Ранг 1.
 *  - lbox: 0x535f0c зовут только 0x535f85 (сдвиг выбора 0x535f4c: кнопки прокрутки, стрелки,
 *    страницы; возврат 0x535f8a) и 0x536163 (щелчок по строке 0x536135; возврат 0x536168). Поле
 *    what = scroll | select по адресу возврата. Функтор +0x74 - «выбор сменился», а не
 *    подтверждение; двойной щелчок - 0x53616c (функтор +0x84 или кнопка +0x50), не поставлен. Ранг 1.
 *  - элемент без имени: у DLG_ISO_DIPLO в Interf.dlg нет ни одной кнопки (только IMAGE/TEXT) - кнопки
 *    портретов рас строит код, в дереве имён их нет. Тогда пишутся child (индекс ребёнка диалога) и
 *    area (CInterface::getArea, слот 12). Ранг 1 - разметка, ранг 2 - «строит CDiplomacyInterf».
 *  - ui_show/ui_hide: iface - адрес экрана; seq берётся ДО вызова оригинала (вложенный показ
 *    получает больший seq). top_class - класс верхнего экрана в момент записи.
 *  - ui_clear снят: слот 7 0x53ccbf не зовётся ни прямо (ссылка только из vftable), ни через
 *    менеджер (270 мест с геттером 0x530014 зовут слоты 4, 6, 3, …, но не 7); фазы снимают экраны
 *    поштучно через hideInterface. Ранг 1.
 *  - game_phase new|load: открытие сценария сервером 0x429603 (__thiscall, 6 аргументов, ret 0x18;
 *    зовут 0x429c92 и 0x429d3b), 4-й аргумент - «новая игра» (при нём 0x429bd4 раздаёт игроков, а
 *    0x429d3b по нему же выбирает reseed против setState из сохранения). Запоминается в атомике,
 *    строка пишется в потоке интерфейса ДО конструктора CPhaseGame (раньше заставки). Ранг 2.
 *    Часть 3: тот же хук до оригинала пишет rng_state (probestate.cpp, noteScenarioOpen).
 *  - DLG_BATTLE_B - широкий бой DisciplesGL (его таблица dlgNames, файл DLG_BATTLE_B.dlg): те же
 *    элементы, что у DLG_BATTLE_A, плюс IMG_TOGGLELEFT/RIGHT. К имени добавляется base. Ранг 1.
 *
 * Класс и вид экрана берутся из RTTI объекта (vftable[-1] -> COL -> TypeDescriptor; базы - из
 * ClassHierarchyDescriptor), с кешем по vftable: CMenuBase -> menu, CInterfDialog -> popup,
 * CInterfFullScreen -> fullscreen, прочее - other.
 */

#include "probe.h"
#include "midgardid.h"
#include <atomic>
#include <cstdint>
#include <cstring>
#include <fmt/format.h>
#include <intrin.h>
#include <iterator>
#include <cctype>
#include <map>
#include <mutex>
#include <string>
#include <unordered_set>
#include <vector>

namespace probe {

namespace {

using namespace game;

constexpr std::uintptr_t dialogInterfVftable = 0x6df82c;
constexpr std::uintptr_t toggleButtonVftable = 0x6e15a4;

/* Адреса возврата в 0x535f0c: сдвиг выбора (прокрутка, клавиши) и щелчок по строке. */
constexpr std::uintptr_t retListScroll = 0x535f8a;
constexpr std::uintptr_t retListSelect = 0x536168;
constexpr std::size_t dlgNameSize = 48;

/** Потолок узлов дерева имён одного диалога и глубины подъёма к диалогу. */
constexpr std::uint32_t maxControls = 1024;
constexpr int maxParentWalk = 6;

/** Потолок длины текста окна сообщения в журнале. */
constexpr std::size_t maxMessageLength = 1024;

/* Адреса возврата из обёрток 0x5c80bf / 0x5c80df в воронку 0x5c8110. */
constexpr std::uintptr_t retFromWrapperA = 0x5c80db;
constexpr std::uintptr_t retFromWrapperB = 0x5c810c;

std::uintptr_t word(const void* base, std::size_t offset)
{
    std::uintptr_t value{};
    return readAt(base, offset, value) ? value : 0u;
}

std::string fixedName(const void* p, std::size_t size)
{
    char buffer[dlgNameSize + 1]{};
    if (size > dlgNameSize || !readable(p, size))
        return {};

    std::memcpy(buffer, p, size);
    return std::string{buffer};
}

/* ---------------------------------------------------------------------------------------------
 * RTTI: класс и вид экрана
 */

struct ClassInfo
{
    std::string name;
    const char* kind;
};

std::mutex uiMutex;
std::map<std::uintptr_t, ClassInfo> classCache; // vftable -> класс

std::string typeName(std::uintptr_t typeDescriptor)
{
    auto name = fixedName(reinterpret_cast<const char*>(typeDescriptor) + 8, 40);
    // ".?AVCMenuMain@@" -> "CMenuMain".
    if (name.rfind(".?AV", 0) == 0 || name.rfind(".?AU", 0) == 0)
        name.erase(0, 4);
    // Имя кончается на первом '@': у классов из анонимного пространства имён дальше идёт "?%<путь к
    // исходнику>" с обратными слешами — 18.09.2026 они ломали JSON строки ui_open (1454 строки двух прогонов).
    const auto at = name.find('@');
    if (at != std::string::npos)
        name.erase(at);
    // Страховка: в имя класса попадают только буквы, цифры и '_'.
    for (auto& c : name)
        if (!(std::isalnum(static_cast<unsigned char>(c)) || c == '_'))
            c = '_';
    return name;
}

ClassInfo readClass(std::uintptr_t vftable)
{
    const auto col = word(reinterpret_cast<const void*>(vftable - 4), 0);
    const auto type = word(reinterpret_cast<const void*>(col), 12);
    if (!type)
        return {"?", "other"};

    ClassInfo info{typeName(type), "other"};
    const auto hierarchy = word(reinterpret_cast<const void*>(col), 16);
    const auto count = word(reinterpret_cast<const void*>(hierarchy), 8);
    const auto bases = word(reinterpret_cast<const void*>(hierarchy), 12);
    bool menu = false, popup = false, full = false;
    for (std::uintptr_t i = 0; i < count && i < 32; ++i) {
        const auto base = word(reinterpret_cast<const void*>(bases), i * 4);
        const auto name = typeName(word(reinterpret_cast<const void*>(base), 0));
        menu = menu || name == "CMenuBase";
        popup = popup || name == "CInterfDialog";
        full = full || name == "CInterfFullScreen";
    }
    info.kind = menu ? "menu" : popup ? "popup" : full ? "fullscreen" : "other";
    return info;
}

ClassInfo classOf(const void* object)
{
    const auto vftable = word(object, 0);
    if (!vftable)
        return {"null", "other"};

    {
        std::lock_guard<std::mutex> lock{uiMutex};
        const auto found = classCache.find(vftable);
        if (found != classCache.end())
            return found->second;
    }

    auto info = readClass(vftable);
    std::lock_guard<std::mutex> lock{uiMutex};
    classCache.emplace(vftable, info);
    return info;
}

/* ---------------------------------------------------------------------------------------------
 * Диалоги и их элементы
 */

const void* interfaceParent(const void* interf)
{
    return reinterpret_cast<const void*>(word(reinterpret_cast<const void*>(word(interf, 4)), 8));
}

bool isDialog(const void* p)
{
    return p && word(p, 0) == dialogInterfVftable;
}

std::string dialogName(const void* dialog)
{
    return isDialog(dialog) ? fixedName(reinterpret_cast<const void*>(word(dialog, 8)), dlgNameSize)
                            : std::string{};
}

/** Имя прямого ребёнка диалога по дереву «имя -> индекс» и вектору детей. */
std::string controlName(const void* dialog, const void* child)
{
    const auto data = reinterpret_cast<const char*>(word(dialog, 8));
    const auto childs = reinterpret_cast<const void*>(word(dialog, 4));
    const auto begin = word(childs, 0x20);
    const auto end = word(childs, 0x24);
    const auto head = word(data, 0x38 + 0xc);
    const auto nil = word(data, 0x38 + 0x14);
    if (!data || !begin || end < begin || !head)
        return {};

    std::vector<std::uintptr_t> stack{word(reinterpret_cast<const void*>(head), 4)}; // корень
    std::uint32_t visited = 0;
    while (!stack.empty() && visited++ < maxControls) {
        const auto node = stack.back();
        stack.pop_back();
        if (!node || node == nil || node == head)
            continue;

        const auto* p = reinterpret_cast<const void*>(node);
        int index{-1};
        if (readAt(p, 0x40, index) && index >= 0
            && begin + static_cast<std::uintptr_t>(index) * 4 < end
            && word(reinterpret_cast<const void*>(begin), static_cast<std::size_t>(index) * 4)
                   == reinterpret_cast<std::uintptr_t>(child))
            return fixedName(static_cast<const char*>(p) + 0x10, dlgNameSize);

        stack.push_back(word(p, 8));
        stack.push_back(word(p, 12));
    }

    return {};
}

struct Element
{
    std::string screen;  // имя диалога, в котором элемент
    std::string element; // имя элемента в .dlg
    int child{-1};       // индекс прямого ребёнка диалога (пишется, когда имени нет)
};

/** Индекс прямого ребёнка в векторе детей диалога; -1 - не нашёлся. */
int childIndex(const void* dialog, const void* child)
{
    const auto childs = reinterpret_cast<const void*>(word(dialog, 4));
    const auto begin = word(childs, 0x20);
    const auto end = word(childs, 0x24);
    for (std::uintptr_t p = begin; p && p < end && (p - begin) / 4 < maxControls; p += 4) {
        if (word(reinterpret_cast<const void*>(p), 0) == reinterpret_cast<std::uintptr_t>(child))
            return static_cast<int>((p - begin) / 4);
    }
    return -1;
}

Element elementOf(const void* control)
{
    const void* child = control;
    for (int i = 0; i < maxParentWalk && child; ++i) {
        const auto* parent = interfaceParent(child);
        if (isDialog(parent)) {
            Element found{dialogName(parent), controlName(parent, child)};
            if (found.element.empty())
                found.child = childIndex(parent, child);
            return found;
        }
        child = parent;
    }

    return {};
}

/** Широкий экран боя DisciplesGL - копия DLG_BATTLE_A с теми же элементами. */
std::string baseField(const std::string& name)
{
    return name == "DLG_BATTLE_B" ? std::string{R"(,"base":"DLG_BATTLE_A")"} : std::string{};
}

/* ---------------------------------------------------------------------------------------------
 * Стек экранов
 */

/** Интерфейс-родитель -> созданный под ним диалог (первый живой). */
std::map<const void*, const void*> dialogOfParent;
const void* interfManager{};
std::string topScreenName{"none"};
std::vector<std::string> screenNames{"none"}; // интернированные имена для ключа ui_text
int topScreenIndex{0};
int openingScreenIndex{-1}; // диалог, который сейчас создаётся (тексты его разметки)
int lastTextId{0};
bool haveLastText{false};
std::unordered_set<std::uint64_t> seenTexts;

int internLocked(const std::string& name)
{
    for (std::size_t i = 0; i < screenNames.size(); ++i) {
        if (screenNames[i] == name)
            return static_cast<int>(i);
    }
    screenNames.push_back(name);
    return static_cast<int>(screenNames.size() - 1);
}

/** Имя экрана: имя его диалога, если он живой и всё ещё ребёнок, иначе класс. */
std::string screenOf(const void* interf)
{
    if (!interf)
        return "none";

    const void* dialog{};
    {
        std::lock_guard<std::mutex> lock{uiMutex};
        const auto found = dialogOfParent.find(interf);
        if (found != dialogOfParent.end())
            dialog = found->second;
    }

    if (dialog && interfaceParent(dialog) == interf) {
        auto name = dialogName(dialog);
        if (!name.empty())
            return name;
    }

    return classOf(interf).name;
}

std::uintptr_t managerData()
{
    return word(interfManager, 8);
}

std::uint32_t stackDepth()
{
    return static_cast<std::uint32_t>(word(reinterpret_cast<const void*>(managerData()), 0x18));
}

const void* topInterface()
{
    const auto data = reinterpret_cast<const void*>(managerData());
    if (!stackDepth())
        return nullptr;

    const auto head = word(data, 0x1c);
    const auto first = word(reinterpret_cast<const void*>(head), 0);
    return reinterpret_cast<const void*>(word(reinterpret_cast<const void*>(first), 8));
}

void refreshTop()
{
    const auto name = screenOf(topInterface());
    std::lock_guard<std::mutex> lock{uiMutex};
    topScreenName = name;
    topScreenIndex = internLocked(name);
}

std::string topScreen()
{
    std::lock_guard<std::mutex> lock{uiMutex};
    return topScreenName;
}

/** Верхний экран и его класс в момент записи: DLG_ISO - общая рамка карты, города и дипломатии. */
std::string topFields()
{
    const auto* top = interfManager ? topInterface() : nullptr;
    return fmt::format(R"("top":"{}","top_class":"{}")", topScreen(),
                       top ? classOf(top).name : std::string{"none"});
}

/** seq взят до вызова оригинала: вложенный показ получит больший. */
void emitStack(std::uint64_t seq, const char* event, const void* interf, std::uintptr_t returnAddress)
{
    const auto info = classOf(interf);
    const auto screen = screenOf(interf);
    emit(seq, fmt::format(R"("ev":"{}","screen":"{}"{},"iface":"{}","class":"{}","kind":"{}","depth":{},{},"ret_addr":"{}")",
                          event, screen, baseField(screen),
                          hexAddress(reinterpret_cast<std::uintptr_t>(interf)), info.name, info.kind,
                          stackDepth(), topFields(), hexAddress(returnAddress)));
}

/* ---------------------------------------------------------------------------------------------
 * Хуки
 */

using CreateDialog = void*(__stdcall*)(void* parent,
                                       const char* name,
                                       int center,
                                       const void* p1,
                                       const void* p2,
                                       int childIndex,
                                       const void* area);
CreateDialog origCreateDialog{};

void* __stdcall createDialogProbe(void* parent,
                                  const char* name,
                                  int center,
                                  const void* p1,
                                  const void* p2,
                                  int childIndex,
                                  const void* area)
{
    const auto* slots = static_cast<const std::uintptr_t*>(_AddressOfReturnAddress());
    // Обёртка A: 7 аргументов, сохранённый ebp, возврат - слот 9; обёртка B: ещё 0x10 локальных.
    std::uintptr_t caller = slots[0];
    if (slots[0] == retFromWrapperA)
        caller = slots[9];
    else if (slots[0] == retFromWrapperB)
        caller = slots[13];

    const auto dlg = fixedName(name, dlgNameSize);
    int previous{};
    {
        std::lock_guard<std::mutex> lock{uiMutex};
        previous = openingScreenIndex;
        openingScreenIndex = internLocked(dlg);
    }

    void* dialog = origCreateDialog(parent, name, center, p1, p2, childIndex, area);

    {
        std::lock_guard<std::mutex> lock{uiMutex};
        openingScreenIndex = previous;
        if (dialog && parent) {
            const auto found = dialogOfParent.find(parent);
            const bool alive = found != dialogOfParent.end() && isDialog(found->second)
                               && interfaceParent(found->second) == parent;
            if (!alive)
                dialogOfParent[parent] = dialog;
        }
    }

    const auto info = classOf(parent);
    emit(nextSeq(),
         fmt::format(R"("ev":"ui_open","name":"{}"{},"dialog":"{}","parent":"{}","parent_class":"{}","kind":"{}",{},"ok":{},"ret_addr":"{}")",
                     dlg, baseField(dlg), hexAddress(reinterpret_cast<std::uintptr_t>(dialog)),
                     hexAddress(reinterpret_cast<std::uintptr_t>(parent)), info.name, info.kind,
                     topFields(), dialog != nullptr, hexAddress(caller)));
    return dialog;
}

using DialogDtor = void(__fastcall*)(void* thisptr, int edx);
DialogDtor origDialogDtor{};

void __fastcall dialogDtorProbe(void* thisptr, int edx)
{
    const auto name = dialogName(thisptr);
    {
        std::lock_guard<std::mutex> lock{uiMutex};
        for (auto it = dialogOfParent.begin(); it != dialogOfParent.end();) {
            it = it->second == thisptr ? dialogOfParent.erase(it) : std::next(it);
        }
    }

    emit(nextSeq(), fmt::format(R"("ev":"ui_close","name":"{}","dialog":"{}")", name,
                                hexAddress(reinterpret_cast<std::uintptr_t>(thisptr))));
    origDialogDtor(thisptr, edx);
}

using ShowInterface = bool(__fastcall*)(void* thisptr, int edx, void* interf);
ShowInterface origShow{};
ShowInterface origHide{};

using RemoveInterface = void*(__fastcall*)(void* thisptr, int edx);
RemoveInterface origRemove{};

/** hideInterface верхнего зовёт removeInterface - строка одна, от hide. */
int hideDepth{0};

bool __fastcall showProbe(void* thisptr, int edx, void* interf)
{
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    interfManager = thisptr;
    const auto seq = nextSeq();
    const bool result = origShow(thisptr, edx, interf);
    refreshTop();
    emitStack(seq, result ? "ui_show" : "ui_show_refused", interf, returnAddress);
    return result;
}

void* __fastcall removeProbe(void* thisptr, int edx)
{
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    interfManager = thisptr;
    const auto seq = nextSeq(); // внутри hide номер пропадает - разрывы seq штатны
    void* removed = origRemove(thisptr, edx);
    refreshTop();
    if (!hideDepth && removed)
        emitStack(seq, "ui_hide", removed, returnAddress);
    return removed;
}

bool __fastcall hideProbe(void* thisptr, int edx, void* interf)
{
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    interfManager = thisptr;
    const auto seq = nextSeq();
    ++hideDepth;
    const bool result = origHide(thisptr, edx, interf);
    --hideDepth;
    refreshTop();
    if (result)
        emitStack(seq, "ui_hide", interf, returnAddress);
    return result;
}

void emitClick(const char* kind,
               const void* control,
               const std::string& extra,
               std::uintptr_t returnAddress)
{
    const auto element = elementOf(control);
    std::string unnamed;
    if (element.element.empty() && !element.screen.empty()) {
        // Элемент не из разметки (кнопку построил код) - индекс ребёнка и прямоугольник getArea.
        using GetArea = const std::int32_t*(__thiscall*)(const void* thisptr);
        const auto vftable = reinterpret_cast<const void*>(word(control, 0));
        const auto getArea = reinterpret_cast<GetArea>(word(vftable, 0x30));
        const auto* r = getArea ? getArea(control) : nullptr;
        unnamed = r && readable(r, 16)
                      ? fmt::format(R"(,"child":{},"area":[{},{},{},{}])", element.child, r[0], r[1],
                                    r[2], r[3])
                      : fmt::format(R"(,"child":{})", element.child);
    }
    emit(nextSeq(), fmt::format(R"("ev":"ui_click","kind":"{}","screen":"{}"{},"element":"{}"{}{},{},"ret_addr":"{}")",
                                kind, element.screen, baseField(element.screen), element.element,
                                unnamed, extra, topFields(), hexAddress(returnAddress)));
}

using ButtonClick = void(__fastcall*)(void* thisptr, int edx);
ButtonClick origButtonClick{};
ButtonClick origToggleClick{};

/** Горячая клавиша кнопки внутри переключателя - для строки toggle (тот же поток, вложенный вызов). */
thread_local std::string toggleHotkey;

void __fastcall buttonClickProbe(void* thisptr, int edx)
{
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    // Флаг клавиши оригинал сбрасывает - читается до вызова. Первая клавиша - из List<char>
    // hotkeys (buttonData+0x10: длина, +0x14: голова, узел: next, prev, char +8).
    const auto* data = reinterpret_cast<const void*>(word(thisptr, 8));
    std::uint8_t byHotkey{};
    readAt(data, 0x70, byHotkey);
    std::string extra = fmt::format(R"(,"hotkey":{})", byHotkey != 0);
    const auto head = word(data, 0x14);
    std::uint8_t key{};
    if (byHotkey && word(data, 0x10) > 0
        && readAt(reinterpret_cast<const void*>(word(reinterpret_cast<const void*>(head), 0)), 8, key))
        extra += fmt::format(R"(,"key":{})", key);

    // Кнопка внутри переключателя: её onClicked (0x534bd6) зовёт слот 37 - строку пишет toggle.
    if (word(interfaceParent(thisptr), 0) == toggleButtonVftable) {
        toggleHotkey = extra;
        origButtonClick(thisptr, edx);
        toggleHotkey.clear();
        return;
    }

    emitClick("button", thisptr, extra, returnAddress);
    origButtonClick(thisptr, edx);
}

void __fastcall toggleClickProbe(void* thisptr, int edx)
{
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    std::uint8_t checked{};
    readAt(reinterpret_cast<const void*>(word(thisptr, 8)), 4, checked);
    emitClick("toggle", thisptr, fmt::format(R"({},"checked":{})", toggleHotkey, checked != 0),
              returnAddress);
    origToggleClick(thisptr, edx);
}

using ListConfirm = void(__fastcall*)(void* thisptr, int edx, int index);
ListConfirm origListConfirm{};

void __fastcall listConfirmProbe(void* thisptr, int edx, int index)
{
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    // Зовут двое: сдвиг выбора (прокрутка, клавиши) и щелчок по строке - различаются возвратом.
    const char* what = returnAddress == retListScroll   ? "scroll"
                       : returnAddress == retListSelect ? "select"
                                                        : "other";
    emitClick("lbox", thisptr, fmt::format(R"(,"what":"{}","index":{})", what, index), returnAddress);
    origListConfirm(thisptr, edx, index);
}

/*
 * Схема 7.6: ui_enable - кнопка погасла или зажглась. CButtonInterf::setEnabled - слот vftable +0xa0
 * (0x6e1234 + 160 = 0x531cca, __thiscall(bool) ret 4; тулсет button.h): флаг buttonData+4, при смене -
 * картинка кнопки 0x53155c. Переключатель (слот +0x8c 0x534993) отдаёт его своей кнопке - строку пишет
 * она, а имя элемента - переключателя (elementOf идёт по родителям). Строка только на смену флага.
 */
using SetEnabled = void(__fastcall*)(void* thisptr, int edx, bool value);
SetEnabled origSetEnabled{};

void __fastcall setEnabledProbe(void* thisptr, int edx, bool value)
{
    const auto* data = reinterpret_cast<const void*>(word(thisptr, 8));
    std::uint8_t before = 2;
    readAt(data, 4, before);
    origSetEnabled(thisptr, edx, value);
    std::uint8_t after = 2;
    readAt(data, 4, after);
    if (before == after || before > 1)
        return;
    const auto element = elementOf(thisptr);
    if (element.screen.empty())
        return; // кнопка ещё не в диалоге (конструктор окна) - её первое состояние покажет первая смена
    countCall("ui_enable");
    emit(nextSeq(), fmt::format(R"("ev":"ui_enable","screen":"{}"{},"element":"{}","child":{},"on":{},{})",
                                element.screen, baseField(element.screen), element.element, element.child,
                                after != 0, topFields()));
}

using MsgBoxCtor = void*(__fastcall*)(void* thisptr,
                                      int edx,
                                      const char* message,
                                      int yesNo,
                                      void* buttonHandler,
                                      void* updateHandler,
                                      const char* dialogName);
MsgBoxCtor origMsgBox{};

void* __fastcall msgBoxProbe(void* thisptr,
                             int edx,
                             const char* message,
                             int yesNo,
                             void* buttonHandler,
                             void* updateHandler,
                             const char* customDialog)
{
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    std::string textId{"null"};
    {
        std::lock_guard<std::mutex> lock{uiMutex};
        if (haveLastText) {
            CMidgardID id{};
            id.value = lastTextId;
            textId = fmt::format(R"("{}")", idToString(&id));
        }
    }

    emit(nextSeq(), fmt::format(R"("ev":"ui_msgbox","text":"{}","last_text_id":{},"yes_no":{},{},"ret_addr":"{}")",
                                message ? jsonCString(message, maxMessageLength) : std::string{},
                                textId, yesNo != 0, topFields(), hexAddress(returnAddress)));
    return origMsgBox(thisptr, edx, message, yesNo, buttonHandler, updateHandler, customDialog);
}

using PhaseGameCtor = void*(__fastcall*)(void* thisptr, int edx, void* a1, void* a2);
PhaseGameCtor origPhaseGameCtor{};

using PhaseGameDtor = void(__fastcall*)(void* thisptr, int edx);
PhaseGameDtor origPhaseGameDtor{};

/** Последнее открытие сценария сервером: 0 - не было, 1 - новая игра, 2 - загрузка. */
std::atomic<int> pendingStart{0};

using OpenScenario = bool(__fastcall*)(void* thisptr,
                                       int edx,
                                       void* out,
                                       void* a1,
                                       void* a2,
                                       int newGame,
                                       int a5,
                                       int a6);
OpenScenario origOpenScenario{};

bool __fastcall openScenarioProbe(void* thisptr,
                                  int edx,
                                  void* out,
                                  void* a1,
                                  void* a2,
                                  int newGame,
                                  int a5,
                                  int a6)
{
    // Флаг - байт, положенный на стек словом (push dword ptr [ebp+0x10]): смотреть младший байт.
    const bool isNew = (newGame & 0xff) != 0;
    pendingStart.store(isNew ? 1 : 2);
    // Схема 7.1, часть 3: генератор уже пересеян/восстановлен (или нет) вызывающим 0x429d3b.
    noteScenarioOpen(thisptr, isNew, reinterpret_cast<std::uintptr_t>(_ReturnAddress()));
    return origOpenScenario(thisptr, edx, out, a1, a2, newGame, a5, a6);
}

void* __fastcall phaseGameCtorProbe(void* thisptr, int edx, void* a1, void* a2)
{
    // До оригинала: конструктор сам показывает заставку сценария.
    const int start = pendingStart.exchange(0);
    const char* what = start == 1 ? "new" : start == 2 ? "load" : "enter";
    emit(nextSeq(), fmt::format(R"("ev":"game_phase","what":"{}","phase":"{}",{})", what,
                                hexAddress(reinterpret_cast<std::uintptr_t>(thisptr)), topFields()));
    // Схема 7.4: новая карта - новые ключи; строка game_phase выше - граница сброса в журнале.
    resetKeys();
    return origPhaseGameCtor(thisptr, edx, a1, a2);
}

void __fastcall phaseGameDtorProbe(void* thisptr, int edx)
{
    emit(nextSeq(), fmt::format(R"("ev":"game_phase","what":"leave","phase":"{}",{})",
                                hexAddress(reinterpret_cast<std::uintptr_t>(thisptr)), topFields()));
    origPhaseGameDtor(thisptr, edx);
}

} // namespace

std::string screenFields()
{
    return topFields();
}

std::string rttiClassName(const void* object)
{
    return classOf(object).name;
}

void noteInterfaceText(const game::CMidgardID* textId)
{
    if (!textId)
        return;

    int screen{};
    std::string screenName;
    bool isNew{};
    bool opening{};
    {
        std::lock_guard<std::mutex> lock{uiMutex};
        lastTextId = textId->value;
        haveLastText = true;
        opening = openingScreenIndex >= 0;
        screen = opening ? openingScreenIndex : topScreenIndex;
        const auto key = (static_cast<std::uint64_t>(screen) << 32)
                         | static_cast<std::uint32_t>(textId->value);
        isNew = seenTexts.insert(key).second;
        if (isNew)
            screenName = screenNames[static_cast<std::size_t>(screen)];
    }

    // Повтор - только счёт: функция зовётся на каждой подписи, строка и id не собираются.
    if (!isNew) {
        countCall("ui_text");
        return;
    }

    const auto id = idToString(textId);
    emitKeyed(nextSeq(), "ui_text", fmt::format("{}:{}", screenName, id),
              fmt::format(R"("ev":"ui_text","screen":"{}","id":"{}","opening":{})", screenName, id,
                          opening));
}

} // namespace probe

namespace hooks {

void addUiHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"ui_open",       0x5c8110, (void*)createDialogProbe,  (void**)&origCreateDialog},
        {"ui_close",      0x50b6c6, (void*)dialogDtorProbe,    (void**)&origDialogDtor},
        {"ui_show",       0x53cac4, (void*)showProbe,          (void**)&origShow},
        {"ui_remove",     0x53cb64, (void*)removeProbe,        (void**)&origRemove},
        {"ui_hide",       0x53cbd1, (void*)hideProbe,          (void**)&origHide},
        {"ui_button",     0x5318e1, (void*)buttonClickProbe,   (void**)&origButtonClick},
        {"ui_toggle",     0x534ba3, (void*)toggleClickProbe,   (void**)&origToggleClick},
        {"ui_lbox",       0x535f0c, (void*)listConfirmProbe,   (void**)&origListConfirm},
        {"ui_msgbox",     0x5c7771, (void*)msgBoxProbe,        (void**)&origMsgBox},
        {"ui_enable",     0x531cca, (void*)setEnabledProbe,    (void**)&origSetEnabled},
        {"game_enter",    0x40529f, (void*)phaseGameCtorProbe, (void**)&origPhaseGameCtor},
        {"game_leave",    0x40566c, (void*)phaseGameDtorProbe, (void**)&origPhaseGameDtor},
        {"game_start",    0x429603, (void*)openScenarioProbe,  (void**)&origOpenScenario},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
