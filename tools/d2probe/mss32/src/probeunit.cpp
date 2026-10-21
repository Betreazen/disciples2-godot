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

#include "probe.h"
#include "game.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midunitgroup.h"
#include <array>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>

/*
 * Схема корпуса 6.3: перенос юнитов между группами и отказы интерфейса.
 *
 * Адреса - game-dev/Discipl2.exe (Gog 3.01, ImageBase 0x400000), capstone. Хуки только читают.
 *
 * unit_transfer. Сервер принимает два сообщения (карта CMidServerLogic, записи vftable 0x6ce384 и
 * 0x6ce3fc, регистрация 0x41fc8d / 0x41fcf6):
 *   - CStackSwapUnitMsg -> 0x42342b -> 0x41eec9 -> 0x42e431 -> 0x5d5642(map, group1, pos1, group2,
 *     pos2), ret 0x14 -> CVisitorSwapUnitPosition (0x5e7554, vftable 0x6f0a64, apply 1). Одна
 *     функция на перестановку внутри группы, перенос в пустую клетку и обмен двух юнитов, в том
 *     числе между гарнизоном и отрядом. Второй вызывающий - ИИ StackReorder (0x42e4f4 -> 0x5d5d73
 *     -> 0x5d6152, вызов 0x5d642a).
 *   - CStackDismissUnitMsg -> 0x423598 -> 0x41f059 -> 0x42e703 -> 0x5d5773(map, unitId, groupId),
 *     ret 0xc: позиция 0x5f455f, проверка 0x5df266, удаление 0x5e7a1d. Вызывающий один (0x42e721).
 *
 * swap_check - 0x5df4fb(map, group1, pos1, group2, pos2), ret 0x14, код: 0 можно; 1 группа не
 * найдена или в клетке pos1 пусто; 2 страж (IUsGuardian) в другую группу; 3 юнит типа 0x78d0c0
 * в другую группу; 0xB - CVisitorSwapUnitPosition::canApply (0x61111f) отказал. Зовёт только
 * интерфейс (перетаскивание CDDUnitGroup, 0x500d81); сервер кода не спрашивает - у него только
 * ответ визитёра (unit_transfer.ret).
 *
 * dismiss_check - 0x5df266(map, groupId, position), ret 0xc, код: 0 можно; 4 в клетке пусто;
 * 5 лидер при других юнитах; 6 лидер вне города (CMidStack +0x94 пуст); 2 страж (IUsGuardian);
 * 1 - удаление 0x5e7a1d (apply 0) отказало. Вызывающие: сервер 0x5d5792, увольнение лидера
 * 0x5d6b06, интерфейс 0x500c73, 0x50230f, 0x5025e6. Мёртвого лидера код не проверяет - запрет
 * увольнения при павшем лидере ставит интерфейс (ui_refusal X005TA0211).
 *
 * equip_drop - CDDEquipment vftable 0x6df114 слот 9 = 0x504782, __thiscall(source, arg), ret 8:
 * статус сброса предмета в слот. 2 - молча не принят (источник - предмет чужой витрины 0x49a722;
 * источник не предмет; 0x57201e: категория предмета не подходит слоту; отряда нет), 1 - отказ с
 * текстом (0x50425b зовёт 0x50416b -> equip_check 0x5df6b7 за кодом текста), 0 - принят. Свиток
 * в слот экипировки получает 2 на 0x57201e, поэтому до equip_check не доходит и кода 9 не даёт.
 *
 * ui_refusal - getInterfaceText 0x5c7f06(textId), ret 4: строка пишется только для текстов
 * отказа из перечня refusalTexts (найм, посох, экипировка, увольнение, перенос при мёртвом
 * лидере); место отказа - адрес возврата. Интерфейс решает эти отказы в теле обработчиков
 * (CSiteMercenaryInterf 0x498291, CDDReinfGroup 0x502999, CDDStackGroup 0x4ff396 и 0x500885,
 * CCityStackInterf 0x4b1918, CDDEquipment 0x50416b, CWandScrollInterf 0x4a8289), отдельной функции
 * с кодом там нет.
 */

namespace probe {

namespace {

using namespace game;

constexpr int notComputed = -1;
constexpr int groupSize = 6;

struct CallSite
{
    std::uintptr_t returnAddress;
    const char* cause;
};

template <std::size_t N>
const char* causeOf(const CallSite (&sites)[N], std::uintptr_t returnAddress)
{
    for (const auto& site : sites) {
        if (site.returnAddress == returnAddress)
            return site.cause;
    }

    return "unknown";
}

std::uintptr_t callerOf(void* returnAddress)
{
    return reinterpret_cast<std::uintptr_t>(returnAddress);
}

/** Строка проверки по ключу: к признакам 'key' добавляются ответ и адрес возврата. */
void emitCheck(std::uint64_t seq,
               const char* event,
               const std::string& key,
               const std::string& fields,
               const std::string& answer,
               std::uintptr_t returnAddress)
{
    const auto address = hexAddress(returnAddress);
    emitKeyed(seq, event, fmt::format("{}:{}:{}", key, answer, address),
              fmt::format(R"("ev":"{}",{},"ret_addr":"{}","ret":{})", event, fields, address,
                          answer));
}

/** Id юнита в клетке 'position' группы стека, укрепления или руины как JSON; null - пусто или не прочитано. */
std::string unitAt(const IMidgardObjectMap* objectMap, const CMidgardID* groupId, int position)
{
    if (position < 0 || position >= groupSize || !readableMap(objectMap) || !readable(groupId, sizeof(CMidgardID)))
        return {"null"};

    const auto& fn = gameFunctions();
    const auto* group = fn.getStackFortRuinGroup ? fn.getStackFortRuinGroup(nullptr, objectMap, groupId)
                                                 : nullptr;
    if (!readable(group, sizeof(CMidUnitGroup)))
        return {"null"};

    const auto* unit = CMidUnitGroupApi::get().getUnitIdByPosition(group, position);
    if (!readable(unit, sizeof(CMidgardID)) || unit->value == emptyId.value)
        return {"null"};

    return fmt::format(R"("{}")", idToString(unit));
}

/** Позиция юнита в группе или -1 (0x5f455f; -1 - не найден или не прочитано). */
int positionOf(const IMidgardObjectMap* objectMap, const CMidgardID* groupId, const CMidgardID* unitId)
{
    if (!readableMap(objectMap) || !readable(groupId, sizeof(CMidgardID))
        || !readable(unitId, sizeof(CMidgardID)))
        return notComputed;

    const auto& fn = gameFunctions();
    const auto* group = fn.getStackFortRuinGroup ? fn.getStackFortRuinGroup(nullptr, objectMap, groupId)
                                                 : nullptr;
    if (!readable(group, sizeof(CMidUnitGroup)))
        return notComputed;

    return CMidUnitGroupApi::get().getUnitPosition(group, unitId);
}

/* ---------------------------------------------------------------------------------------------
 * unit_transfer - 0x5d5642 (swap/move), 0x5d5773 (dismiss)
 */

constexpr CallSite swapSites[] = {
    {0x42e45a, "swap_msg"},   // CStackSwapUnitMsg (человек и ИИ) -> 0x42e431
    {0x5d642f, "ai_reorder"}, // StackReorder 0x42e4f4 -> 0x5d5d73 -> 0x5d6152
};

constexpr CallSite dismissSites[] = {
    {0x42e726, "dismiss_msg"}, // CStackDismissUnitMsg -> 0x42e703
};

using SwapUnit = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                  const CMidgardID* group1,
                                  int position1,
                                  const CMidgardID* group2,
                                  int position2);
SwapUnit origSwapUnit{};

using DismissUnit = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                     const CMidgardID* unitId,
                                     const CMidgardID* groupId);
DismissUnit origDismissUnit{};

void emitTransfer(std::uint64_t seq, const std::string& fields, bool result)
{
    const auto seqEnd = nextSeq();
    countCall("unit_transfer");
    emit(seq, fmt::format(R"("ev":"unit_transfer",{},"ret":{},"seq_end":{})", fields, result,
                          seqEnd));
}

/**
 * Обмен клеток (map, group1, pos1, group2, pos2), __stdcall, ret 0x14. kind: swap - обе клетки
 * заняты, move - одна пуста, empty - обе пусты (визитёр откажет). Юниты читаются до и после.
 */
bool __stdcall swapUnitProbe(IMidgardObjectMap* objectMap,
                             const CMidgardID* group1,
                             int position1,
                             const CMidgardID* group2,
                             int position2)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto unit1 = unitAt(objectMap, group1, position1);
    const auto unit2 = unitAt(objectMap, group2, position2);

    const bool result = origSwapUnit(objectMap, group1, position1, group2, position2);

    const bool full1 = unit1 != "null";
    const bool full2 = unit2 != "null";
    const char* kind = full1 && full2 ? "swap" : (full1 || full2 ? "move" : "empty");
    const auto from = safeId(group1);
    const auto to = safeId(group2);
    // Сравнение сырых id: две нечитаемые группы («null» и «null») - не одна группа.
    CMidgardID id1{};
    CMidgardID id2{};
    const bool sameGroup = readAt(group1, 0, id1) && readAt(group2, 0, id2) && id1.value == id2.value;
    emitTransfer(seq,
                 fmt::format(
                     R"("kind":"{}","same_group":{},"from":"{}","from_pos":{},"to":"{}","to_pos":{},"unit":{},"other":{},"from_after":{},"to_after":{},"cause":"{}","ret_addr":"{}")",
                     kind, sameGroup, from, position1, to, position2, unit1, unit2,
                     unitAt(objectMap, group1, position1), unitAt(objectMap, group2, position2),
                     causeOf(swapSites, returnAddress), hexAddress(returnAddress)),
                 result);
    return result;
}

/** Увольнение (map, unitId, groupId), __stdcall, ret 0xc; код отказа - строка dismiss_check внутри. */
bool __stdcall dismissUnitProbe(IMidgardObjectMap* objectMap,
                                const CMidgardID* unitId,
                                const CMidgardID* groupId)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const int position = positionOf(objectMap, groupId, unitId);

    const bool result = origDismissUnit(objectMap, unitId, groupId);

    emitTransfer(seq,
                 fmt::format(
                     R"("kind":"dismiss","same_group":false,"from":"{}","from_pos":{},"to":null,"to_pos":-1,"unit":"{}","other":null,"from_after":{},"to_after":null,"cause":"{}","ret_addr":"{}")",
                     safeId(groupId), position, safeId(unitId),
                     unitAt(objectMap, groupId, position), causeOf(dismissSites, returnAddress),
                     hexAddress(returnAddress)),
                 result);
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * swap_check - 0x5df4fb, dismiss_check - 0x5df266
 */

using SwapCheck = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                  const CMidgardID* group1,
                                  int position1,
                                  const CMidgardID* group2,
                                  int position2);
SwapCheck origSwapCheck{};

int __stdcall swapCheckProbe(const IMidgardObjectMap* objectMap,
                             const CMidgardID* group1,
                             int position1,
                             const CMidgardID* group2,
                             int position2)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origSwapCheck(objectMap, group1, position1, group2, position2);

    const auto from = safeId(group1);
    const auto to = safeId(group2);
    const auto unit = unitAt(objectMap, group1, position1);
    const auto other = unitAt(objectMap, group2, position2);
    emitCheck(seq, "swap_check",
              fmt::format("{}:{}:{}:{}:{}:{}", from, position1, to, position2, unit, other),
              fmt::format(R"("from":"{}","from_pos":{},"to":"{}","to_pos":{},"unit":{},"other":{})",
                          from, position1, to, position2, unit, other),
              fmt::format("{}", result), returnAddress);
    return result;
}

using DismissCheck = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                     const CMidgardID* groupId,
                                     int position);
DismissCheck origDismissCheck{};

int __stdcall dismissCheckProbe(const IMidgardObjectMap* objectMap,
                                const CMidgardID* groupId,
                                int position)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origDismissCheck(objectMap, groupId, position);

    const auto group = safeId(groupId);
    const auto unit = unitAt(objectMap, groupId, position);
    emitCheck(seq, "dismiss_check", fmt::format("{}:{}:{}", group, position, unit),
              fmt::format(R"("group":"{}","position":{},"unit":{})", group, position, unit),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * equip_drop - CDDEquipment 0x504782
 */

using EquipDrop = int(__fastcall*)(void* thisptr, int edx, void* source, void* arg);
EquipDrop origEquipDrop{};

/** 0x49a6ed(source), __stdcall, ret 4: расширение предмета источника перетаскивания или nullptr. */
using DropItemExt = void*(__stdcall*)(void* source);
const auto dropItemExt = reinterpret_cast<DropItemExt>(0x49a6ed);

/** Id перетаскиваемого предмета: ext->vftable[1]() (как 0x571c9f) или nullptr. */
const CMidgardID* droppedItem(void* source)
{
    if (!readable(source, sizeof(void*)))
        return nullptr;

    auto* ext = dropItemExt(source);
    void** vftable = nullptr;
    if (!readAt(ext, 0, vftable) || !readable(vftable, 2 * sizeof(void*)))
        return nullptr;

    using GetId = const CMidgardID*(__thiscall*)(void* thisptr);
    const auto* id = reinterpret_cast<GetId>(vftable[1])(ext);
    return readable(id, sizeof(CMidgardID)) ? id : nullptr;
}

/** Статус сброса: 0 принят, 1 отказ с текстом (дальше equip_check), 2 молча не принят. */
int __fastcall equipDropProbe(void* thisptr, int /* %edx */, void* source, void* arg)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origEquipDrop(thisptr, 0, source, arg);

    // Слот - [[this+0x10]+4], как его берёт 0x571df0.
    const void* slotHolder = nullptr;
    int slot = notComputed;
    if (readAt(thisptr, 0x10, slotHolder))
        readAt(slotHolder, 4, slot);

    const auto* itemId = droppedItem(source);
    const auto item = itemId ? idToString(itemId) : std::string{"null"};
    emitCheck(seq, "equip_drop", fmt::format("{}:{}", item, slot),
              fmt::format(R"("item":{},"slot":{})",
                          itemId ? fmt::format(R"("{}")", item) : std::string{"null"}, slot),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ui_refusal - getInterfaceText 0x5c7f06
 */

struct RefusalText
{
    const char* id;
    const char* what;
};

/** Тексты отказов (Interf/TApp.dbf) и где интерфейс их берёт. */
constexpr RefusalText refusalTexts[] = {
    // CSiteMercenaryInterf 0x498291 - наём у наёмника.
    {"X005TA0247", "no_space"},            // 0x49846c: в отряде нет места
    {"X005TA0501", "leadership_big"},      // 0x498356: лидерство - только обычные, большой нельзя
    {"X005TA0502", "leadership_big_only"}, // 0x498369: у наёмника только большие
    {"X005TA0514", "merc_empty"},          // 0x4983e8: всех уже наняли
    // CDDReinfGroup 0x502999 - наём в столице.
    {"X005TA0452", "no_building"},    // 0x502a6e: hire_check 8
    {"X005TA0288", "city_level"},     // 0x502b58: hire_check 0xB
    {"X005TA0053", "no_gold"},        // 0x502be5
    {"X005TA0077", "big_needs_both"}, // 0x502c90: большому нужны оба ряда
    {"X005TA0590", "no_gold_stack"},  // CDDStackGroup/CDDReinfGroup слот 18 0x4ffaf1 (0x4ffb50)
    // Посохи и свитки: CDDStackGroup 0x4ff396, CWandScrollInterf 0x4a8289.
    {"X005TA0644", "staff_dead"},
    {"X005TA0645", "scroll_dead"},
    {"X005TA0858", "wand_dead"},
    {"X005TA0591", "staff_no_skill"},
    {"X005TA0592", "scroll_no_skill"},
    {"X005TA0214", "wand_no_mana"},  // 0x5dbbe8 код 9
    {"X005TA0579", "wand_used_1"},   // код 0x11
    {"X005TA0580", "wand_used_2"},   // код 0x12
    // Зелья: CDDStackGroup 0x4ff396.
    {"X005TA0593", "potion_used"},
    {"X005TA0643", "potion_dead"},
    {"X005TA0596", "heal_dead"},
    {"X005TA0594", "heal_not_wounded"},
    {"X005TA0595", "revive_alive"},
    // Экипировка: CDDEquipment 0x50416b по коду equip_check.
    {"X005TA0608", "equip_9"},
    {"X005TA0601", "equip_2"},
    {"X005TA0602", "equip_3"},
    {"X005TA0603", "equip_4"},
    {"X005TA0696", "equip_5"},
    {"X005TA0697", "equip_6"},
    {"X005TA0698", "equip_7"},
    {"X005TA0651", "equip_8"},
    {"X005TA0810", "equip_b"},
    // Увольнение и перенос.
    {"X005TA0211", "dead_leader_transfer"}, // CCityStackInterf 0x4b1918
    {"X005TA0883", "dismiss_quest_hero"},   // CDDStackGroup 0x500885
    {"X005TA0450", "dismiss_guard"},        // CDDReinfGroup 0x50257e
    // Захват: «Призванные существа и воры не могут захватывать города» (ai-q22b, 20260917-synth);
    // место вызова в интерфейсе не искали.
    {"X005TA0458", "capture_summon_thief"},
};

using GetText = const char*(__stdcall*)(const CMidgardID* textId);
GetText origGetText{};

constexpr std::size_t refusalCount = std::size(refusalTexts);

/** Значения id текстов перечня, разобранные движком один раз: на горячем пути - только сравнение int. */
const std::array<int, refusalCount>& refusalValues()
{
    static const auto values = [] {
        std::array<int, refusalCount> result{};
        for (std::size_t i = 0; i < refusalCount; ++i) {
            CMidgardID id{};
            CMidgardIDApi::get().fromString(&id, refusalTexts[i].id);
            result[i] = id.value;
        }
        return result;
    }();
    return values;
}

const char* refusalOf(const CMidgardID* textId)
{
    const auto& values = refusalValues();
    for (std::size_t i = 0; i < refusalCount; ++i) {
        if (values[i] == textId->value)
            return refusalTexts[i].what;
    }

    return nullptr;
}

const char* __stdcall getTextProbe(const CMidgardID* textId)
{
    const auto returnAddress = callerOf(_ReturnAddress());
    const char* result = origGetText(textId);

    // Схема 7.0: каждый id - в ui_text по ключу (экран, id), probeui.cpp. textId оригинал прочитал.
    noteInterfaceText(textId);

    // Прочие тексты - без строки и без счёта: функция зовётся на каждой подписи интерфейса, поэтому
    // для них только сравнение int. textId оригинал уже прочитал - без VirtualQuery на каждый вызов.
    const char* what = textId ? refusalOf(textId) : nullptr;
    if (!what)
        return result;

    const auto id = idToString(textId);

    const auto seq = nextSeq();
    const auto address = hexAddress(returnAddress);
    emitKeyed(seq, "ui_refusal", fmt::format("{}:{}", id, address),
              fmt::format(R"("ev":"ui_refusal","text":"{}","what":"{}","ret_addr":"{}")", id, what,
                          address));
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addUnitHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"unit_swap",     0x5d5642, (void*)swapUnitProbe,     (void**)&origSwapUnit},
        {"unit_dismiss",  0x5d5773, (void*)dismissUnitProbe,  (void**)&origDismissUnit},
        {"swap_check",    0x5df4fb, (void*)swapCheckProbe,    (void**)&origSwapCheck},
        {"dismiss_check", 0x5df266, (void*)dismissCheckProbe, (void**)&origDismissCheck},
        {"equip_drop",    0x504782, (void*)equipDropProbe,    (void**)&origEquipDrop},
        {"ui_refusal",    0x5c7f06, (void*)getTextProbe,      (void**)&origGetText},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
