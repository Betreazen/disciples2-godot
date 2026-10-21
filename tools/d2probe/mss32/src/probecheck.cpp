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
#include "gameutils.h"
#include "lordcat.h"
#include "lordtype.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midstack.h"
#include "mqpoint.h"
#include "scenarioinfo.h"
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>

/*
 * Схема корпуса 6.0, партия 1: проверки, которые опрашивает интерфейс (курсор постановки жезла,
 * перетаскивание найма и продажи, обзор отряда) или ИИ на каждую цель. Их ответы - это отказы,
 * которые попадают в журнал без нарочных ошибок игрока.
 *
 * Опрос частый, поэтому все восемь пишутся по ключу (probe::emitKeyed): строка на новое сочетание
 * «признаки вопроса + ответ + адрес возврата», а число вызовов - без бюджета в counts.calls. Адрес
 * возврата стоит в каждом ключе намеренно: без него опрос интерфейса занял бы ключ раньше, и
 * настоящая проверка сервера с тем же ответом строки бы не дала.
 *
 * Адреса - Gog 3.01; пролог, ширина ret и вызывающие каждой функции сверены capstone.
 */

namespace probe {

namespace {

using namespace game;

/** Absent or unreadable values are journaled as this, never as a plausible number. */
constexpr int notComputed = -1;

/** stackScoutRange движка: min 0x6e88fc, max 0x6e8900 (1 и 8 в ванили), читает их 0x5a3900. */
constexpr std::uintptr_t scoutRangeMinAddress = 0x6e88fc;
constexpr std::uintptr_t scoutRangeMaxAddress = 0x6e8900;

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

std::uintptr_t callerOf(void* returnAddress)
{
    return reinterpret_cast<std::uintptr_t>(returnAddress);
}

/* garrison_max - CMidUnitGroupFortification vftable 0x6f0cfc слот 5 = 0x6144ce */

using GroupMax = int(__fastcall*)(const void* thisptr, int edx, const IMidgardObjectMap* objectMap);
GroupMax origGarrisonMax{};

/**
 * Предел группы укрепления, __thiscall, ret 4: maxUnitsAllowed (+0x2c), а при -1 - уровень
 * укрепления (город +0x30 -> CFortificationVftable слот 9, getTier). Ключ - укрепление и
 * maxUnitsAllowed: так видно, кто и для какой деревни спрашивает.
 */
int __fastcall garrisonMaxProbe(const void* thisptr, int /* %edx */, const IMidgardObjectMap* objectMap)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origGarrisonMax(thisptr, 0, objectMap);

    // -1 у maxUnitsAllowed законно («предел - уровень», 0x6144d1), поэтому непрочитанное - null.
    int allowedValue = 0;
    const auto allowed = readAt(thisptr, 0x2c, allowedValue) ? fmt::format("{}", allowedValue)
                                                             : std::string{"null"};
    const void* city = nullptr;
    CMidgardID cityId{};
    // IMidScenarioObject: id на +4.
    const auto fort = readAt(thisptr, 0x30, city) && readAt(city, 4, cityId)
                          ? idToString(&cityId)
                          : std::string{"null"};

    emitCheck(seq, "garrison_max", fmt::format("{}:{}", fort, allowed),
              fmt::format(R"("group":"{}","fort":"{}","allowed":{})",
                          hexAddress(reinterpret_cast<std::uintptr_t>(thisptr)), fort, allowed),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* scout - CUmStack::getScout, vftable 0x6eacf4 слот 4 = 0x5a38e8 */

using GetScout = int(__fastcall*)(const void* thisptr, int edx);
GetScout origScout{};

/**
 * Радиус обзора отряда, __thiscall без аргументов, ret: getScout базы + scoutingRange модификатора
 * ([[this+0xc]+0xc]), упёртый в stackScoutRange. База = ret - bonus, если не упёрлось. Отряд из
 * this не опознаётся - строку кладут рядом со stack_move.
 *
 * Срабатывает ТОЛЬКО у лидера с модификатором отряда. Движок зовёт слот +0x10 IUsStackLeader
 * виртуально в семи местах (обзор при ходе CEffectMove 0x43a104 -> 0x5d52fe -> 0x5d50ee ->
 * 0x5d5168, вызов 0x5d520c; CEffectChangeOwner 0x4367cb; 0x41c53f, 0x43be22; ИИ 0x4505bb;
 * интерфейс 0x4d3173), и без модификатора вызов уходит в TUsStackLeaderImpl 0x5a89f9 - геттер,
 * склеенный компоновщиком со 160 другими слотами, хук на него невозможен. vftable 0x6eacf4 ставят
 * только конструкторы CUmStack (0x5a3120, 0x5a340e, 0x5a34f2), то есть обёртка модификатора.
 */
int __fastcall scoutProbe(const void* thisptr, int /* %edx */)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    int bonus = notComputed;
    const void* modifier = nullptr;
    if (readAt(thisptr, 0xc, modifier))
        readAt(modifier, 0xc, bonus);

    const int result = origScout(thisptr, 0);

    int low = notComputed;
    int high = notComputed;
    readAt(reinterpret_cast<const void*>(scoutRangeMinAddress), 0, low);
    readAt(reinterpret_cast<const void*>(scoutRangeMaxAddress), 0, high);
    const bool clamped = result == low || result == high;

    emitCheck(seq, "scout", fmt::format("{}", bonus),
              fmt::format(R"("bonus":{},"clamped":{})", bonus, clamped), fmt::format("{}", result),
              returnAddress);
    return result;
}

/* rod_check - 0x5d9e59 */

using RodCheck = int(__stdcall*)(const CMidgardID* stackId,
                                 const CMqPoint* position,
                                 const IMidgardObjectMap* objectMap,
                                 int flagSlot);
RodCheck origRodCheck{};

/**
 * Можно ли поставить жезл, __stdcall, ret 0x10: (stackId, клетка, map, flag), код 0 - можно.
 * Порядок отказов: отряд/раса -> 0x5d9ac8 -> место 0x5d9bdb -> союзник 0x5d9d1d -> казна
 * 0x5d9b84 -> при flag 0x5d9ca7. Вызывающие: CEffectDropRod::apply (0x43765c), обработчик
 * приказа (0x440d1f) и курсор постановки CTaskDropRod (0x4cdbe7).
 */
int __stdcall rodCheckProbe(const CMidgardID* stackId,
                            const CMqPoint* position,
                            const IMidgardObjectMap* objectMap,
                            int flagSlot)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origRodCheck(stackId, position, objectMap, flagSlot);

    CMqPoint cell{notComputed, notComputed};
    readAt(position, 0, cell);
    const bool flag = (flagSlot & 0xff) != 0; // движок сравнивает байтом, 0x5d9ed4
    const auto stack = safeId(stackId);
    emitCheck(seq, "rod_check", fmt::format("{}:{}:{}:{}", stack, cell.x, cell.y, int(flag)),
              fmt::format(R"("stack":"{}","x":{},"y":{},"flag":{})", stack, cell.x, cell.y, flag),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* hire_check - 0x5deea7, hire_leader_check - 0x5de921 */

using HireCheck = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                  const CMidgardID* playerId,
                                  const CMidgardID* unitTypeId,
                                  const CMidgardID* groupId,
                                  int position);
HireCheck origHireCheck{};

/**
 * Можно ли нанять юнита, __stdcall, ret 0x14, код: 0 можно, 7 казна, 8 нет постройки, 10 большому
 * нет двух мест, 11 место отвергнуто, 1 пробный AddUnitToGroup. Вызывающие: hire_unit (0x5d5683),
 * ИИ (0x44c3e3), перетаскивание CDDReinfGroup (0x502a42, 0x502b3b).
 */
int __stdcall hireCheckProbe(const IMidgardObjectMap* objectMap,
                             const CMidgardID* playerId,
                             const CMidgardID* unitTypeId,
                             const CMidgardID* groupId,
                             int position)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origHireCheck(objectMap, playerId, unitTypeId, groupId, position);

    const auto player = safeId(playerId);
    const auto unitType = safeId(unitTypeId);
    const auto group = safeId(groupId);
    emitCheck(seq, "hire_check", fmt::format("{}:{}:{}:{}", player, unitType, group, position),
              fmt::format(R"("player":"{}","unit_type":"{}","group":"{}","position":{})", player,
                          unitType, group, position),
              fmt::format("{}", result), returnAddress);
    return result;
}

using HireLeaderCheck = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                        const CMidgardID* playerId,
                                        const CMidgardID* unitTypeId);
HireLeaderCheck origHireLeaderCheck{};

/**
 * Можно ли нанять лидера, __stdcall, ret 0xc, код: 0 можно, 1 казна, 2 второй вор, 3 нет в списке.
 * Вызывающие: hire_leader (0x5d6975), ИИ (0x44b0cb), CDDEmptyGroup (0x505270).
 */
int __stdcall hireLeaderCheckProbe(const IMidgardObjectMap* objectMap,
                                   const CMidgardID* playerId,
                                   const CMidgardID* unitTypeId)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origHireLeaderCheck(objectMap, playerId, unitTypeId);

    const auto player = safeId(playerId);
    const auto unitType = safeId(unitTypeId);
    emitCheck(seq, "hire_leader_check", fmt::format("{}:{}", player, unitType),
              fmt::format(R"("player":"{}","unit_type":"{}")", player, unitType),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* merc_check - 0x5e00c6, sell_check - 0x5dfe1a */

using SiteCheck = bool(__stdcall*)(const IMidgardObjectMap* objectMap,
                                   const CMidgardID* siteId,
                                   const CMidgardID* stackId,
                                   const CMidgardID* objectId);
SiteCheck origMercCheck{};
SiteCheck origSellCheck{};

/**
 * Можно ли нанять у наёмника, __stdcall, ret 0x10: сайт-наёмник, юнит в ростере, казна не меньше
 * цены. Места и лидерства не проверяет. Вызывающие: onSiteBuyUnit (0x5d7ac2) и 0x4598bc (ИИ);
 * интерфейсного вызова в .text нет.
 */
bool __stdcall mercCheckProbe(const IMidgardObjectMap* objectMap,
                              const CMidgardID* siteId,
                              const CMidgardID* stackId,
                              const CMidgardID* unitTypeId)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const bool result = origMercCheck(objectMap, siteId, stackId, unitTypeId);

    const auto site = safeId(siteId);
    const auto stack = safeId(stackId);
    const auto unitType = safeId(unitTypeId);
    emitCheck(seq, "merc_check", fmt::format("{}:{}:{}", site, stack, unitType),
              fmt::format(R"("site":"{}","stack":"{}","unit_type":"{}")", site, stack, unitType),
              fmt::format("{}", result), returnAddress);
    return result;
}

/**
 * Можно ли продать предмет, __stdcall, ret 0x10: сайт-торговец, категория [0x837718] - всегда,
 * [0x837658] - никогда, иначе BUY_* торговца (0x5e7c51). Вызывающие: onSiteSellItem (0x5d7816),
 * перетаскивание CDDMerchantInventory (0x4fe8c8), 0x4998e5 (не опознан).
 */
bool __stdcall sellCheckProbe(const IMidgardObjectMap* objectMap,
                              const CMidgardID* siteId,
                              const CMidgardID* stackId,
                              const CMidgardID* itemId)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const bool result = origSellCheck(objectMap, siteId, stackId, itemId);

    const auto site = safeId(siteId);
    const auto item = safeId(itemId);
    emitCheck(seq, "sell_check", fmt::format("{}:{}", site, item),
              fmt::format(R"("site":"{}","stack":"{}","item":"{}","item_type":{})", site,
                          safeId(stackId), item, itemTypeOf(objectMap, itemId)),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* grow_check - 0x5dc62a */

using GrowCheck = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                  const CMidgardID* playerId,
                                  const CMidgardID* fortId);
GrowCheck origGrowCheck{};

/**
 * Можно ли растить деревню, __stdcall, ret 0xc, код: 0 можно, 11 бунт, 4 потолок уровня, 7 уже
 * росла сегодня, 3 нет денег. Интерфейс не опрашивает; по ключу с днём, код ИИ пишется раз в день.
 * Вызывающие: growCity (0x5da03c) и CAiGrowCities (vftable 0x6cf664): 0x44972d обходит укрепления
 * и спрашивает только про ДЕРЕВНИ игрока (0x4497d5 владелец +0x84, 0x4497fb категория против
 * village.id [0x837a88]), выходит сразу, если CMidPlayer +0x8c непуст (0x44974e); второй - 0x4499ea.
 * У ИИ с одной столицей вызовов нет.
 */
int __stdcall growCheckProbe(const IMidgardObjectMap* objectMap,
                             const CMidgardID* playerId,
                             const CMidgardID* fortId)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origGrowCheck(objectMap, playerId, fortId);

    int turn = notComputed;
    if (readableMap(objectMap)) {
        const auto* info = hooks::getScenarioInfo(objectMap);
        if (readable(info, sizeof(CScenarioInfo)))
            turn = info->currentTurn;
    }

    const auto player = safeId(playerId);
    const auto fort = safeId(fortId);
    emitCheck(seq, "grow_check", fmt::format("{}:{}:{}", player, fort, turn),
              fmt::format(R"("player":"{}","fort":"{}","turn":{})", player, fort, turn),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* equip_check - 0x5df6b7 (схема 6.0, партия 2) */

using EquipCheck = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                   const CMidgardID* itemId,
                                   const CMidgardID* stackId,
                                   int slot,
                                   int checkEffectSlot);
EquipCheck origEquipCheck{};

/**
 * Можно ли надеть предмет, __stdcall, ret 0x14: (map, item, stack, slot, checkEffect), код 0 можно,
 * 9 не экипировка / лидер мёртв / CMidStack::canEquip, 8 (0x5df765), 2 броня-оружие, 3 книга,
 * 4 знамя, 5 сфера, 6 талисман, 7 сапоги - нет навыка, 0xB при checkEffect - не даст эффекта
 * (0x5ec48f). Вызывающие: CDDEquipment (0x5041b1), ИИ (0x452126), сервер (0x5d6b80). Отказ виден
 * только здесь: equipItem получает уже годный предмет.
 */
int __stdcall equipCheckProbe(const IMidgardObjectMap* objectMap,
                              const CMidgardID* itemId,
                              const CMidgardID* stackId,
                              int slot,
                              int checkEffectSlot)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origEquipCheck(objectMap, itemId, stackId, slot, checkEffectSlot);

    const auto item = safeId(itemId);
    const auto stack = safeId(stackId);
    const bool checkEffect = (checkEffectSlot & 0xff) != 0; // движок сравнивает байтом, 0x5df8ab
    emitCheck(seq, "equip_check", fmt::format("{}:{}:{}:{}", item, stack, slot, int(checkEffect)),
              fmt::format(R"("item":"{}","item_type":{},"stack":"{}","slot":{},"check_effect":{})",
                          item, itemTypeOf(objectMap, itemId), stack, slot, checkEffect),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* wand_check - 0x5dbdd2 (схема 6.0, партия 2) */

/**
 * Сколько раз в день лорд игрока-владельца отряда 'targetId' может применить один посох или свиток:
 * Маг - 2, прочие - 1 (0x5dbd0b...0x5dbd37: категория лорда против [0x837760] = mage.id). -1, если
 * отряд, игрока или лорда не прочитать. Сколько применений уже было, зонд не читает: это обход
 * записей 0x606f50, то есть повторное исполнение движка; ответ 0x11/0x12 и так говорит «исчерпано».
 */
int dailyWandUses(const IMidgardObjectMap* objectMap, const CMidgardID* targetId)
{
    const auto* stack = readableStack(objectMap, targetId);
    const auto* player = stack ? readablePlayer(objectMap, &stack->ownerId) : nullptr;
    const auto& fn = gameFunctions();
    const auto* lord = player && fn.getLordByPlayer ? fn.getLordByPlayer(player) : nullptr;
    const auto* mage = LordCategories::get().mage;
    if (!readable(lord, sizeof(TLordType)) || !readable(lord->data, sizeof(TLordTypeData))
        || !readable(mage, sizeof(LLordCategory)))
        return notComputed;

    return lord->data->lordCategory.id == mage->id ? 2 : 1;
}

using WandCheck = int(__stdcall*)(const CMidgardID* itemId,
                                  const CMidgardID* targetId,
                                  const CMqPoint* position,
                                  int flagSlot,
                                  const IMidgardObjectMap* objectMap);
WandCheck origWandCheck{};

/**
 * Можно ли применить посох или свиток, __stdcall, ret 0x14: (item, target, клетка, flag, map).
 * Порядок кодов: 1 не посох/свиток или у цели нет владельца -> 0x5dab48 годность цели -> 0x5dbbe8:
 * 0xE нет навыка wandScrollUse, 9 нет маны, 0x11 уже применён сегодня (не Маг), 0x12 применён
 * дважды (Маг) -> 0x5db2cb проверки заклинания. Вызывающие: интерфейс (0x4cd5ff), сервер
 * CEffectWand (0x5dc4fa).
 */
int __stdcall wandCheckProbe(const CMidgardID* itemId,
                             const CMidgardID* targetId,
                             const CMqPoint* position,
                             int flagSlot,
                             const IMidgardObjectMap* objectMap)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origWandCheck(itemId, targetId, position, flagSlot, objectMap);

    CMqPoint cell{notComputed, notComputed};
    readAt(position, 0, cell);
    const bool flag = (flagSlot & 0xff) != 0;
    const auto item = safeId(itemId);
    const auto target = safeId(targetId);
    emitCheck(seq, "wand_check",
              fmt::format("{}:{}:{}:{}:{}", item, target, cell.x, cell.y, int(flag)),
              fmt::format(R"("item":"{}","target":"{}","x":{},"y":{},"flag":{},"daily_uses":{})",
                          item, target, cell.x, cell.y, flag, dailyWandUses(objectMap, targetId)),
              fmt::format("{}", result), returnAddress);
    return result;
}

/* wand_precheck - 0x5dbbe8 (схема 6.3) */

using WandPrecheck = int(__stdcall*)(const CMidgardID* itemId,
                                     const CMidgardID* stackId,
                                     const IMidgardObjectMap* objectMap);
WandPrecheck origWandPrecheck{};

/**
 * Калитки применяющего посох или свиток, __stdcall, ret 0xc: (item, stack, map). Порядок кодов:
 * 1 - нет предмета/отряда/лидера/игрока или владелец не играбельный (0x5e5358); 0xE - лидер мёртв
 * (0x5e98ad) или нет способности 0x8375a8 (wandScrollUse); 9 - мана (0x5869e6); 0x11 - не Маг и
 * уже применён сегодня; 0x12 - Маг и применён дважды (0x606f50 по паре игрок + экземпляр).
 * Вызывающие: интерфейс напрямую, до режима наведения (CWandScrollInterf 0x4a82be, CDDStackGroup
 * 0x4ff96b), и обёртка wand_check 0x5dbdd2 (0x5dbe40). В wand_check этих кодов не видно:
 * интерфейс отсекает их раньше.
 */
int __stdcall wandPrecheckProbe(const CMidgardID* itemId,
                                const CMidgardID* stackId,
                                const IMidgardObjectMap* objectMap)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    const int result = origWandPrecheck(itemId, stackId, objectMap);

    const auto item = safeId(itemId);
    const auto stack = safeId(stackId);
    emitCheck(seq, "wand_precheck", fmt::format("{}:{}", item, stack),
              fmt::format(R"("item":"{}","item_type":{},"stack":"{}","daily_uses":{})", item,
                          itemTypeOf(objectMap, itemId), stack, dailyWandUses(objectMap, stackId)),
              fmt::format("{}", result), returnAddress);
    return result;
}

} // namespace

std::string itemTypeOf(const IMidgardObjectMap* objectMap, const CMidgardID* itemId)
{
    const auto item = safeId(itemId);
    if (item.size() != 10 || item.compare(4, 2, "IM") != 0 || !readableMap(objectMap))
        return {"null"};

    const auto* object = objectMap->vftable->findScenarioObjectById(objectMap, itemId);
    CMidgardID type{};
    return readAt(object, 8, type) ? fmt::format(R"("{}")", idToString(&type))
                                   : std::string{"null"};
}

} // namespace probe

namespace hooks {

void addCheckHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"garrison_max",      0x6144ce, (void*)garrisonMaxProbe,     (void**)&origGarrisonMax},
        {"scout",             0x5a38e8, (void*)scoutProbe,           (void**)&origScout},
        {"rod_check",         0x5d9e59, (void*)rodCheckProbe,        (void**)&origRodCheck},
        {"hire_check",        0x5deea7, (void*)hireCheckProbe,       (void**)&origHireCheck},
        {"hire_leader_check", 0x5de921, (void*)hireLeaderCheckProbe, (void**)&origHireLeaderCheck},
        {"merc_check",        0x5e00c6, (void*)mercCheckProbe,       (void**)&origMercCheck},
        {"sell_check",        0x5dfe1a, (void*)sellCheckProbe,       (void**)&origSellCheck},
        {"grow_check",        0x5dc62a, (void*)growCheckProbe,       (void**)&origGrowCheck},
        // Схема 6.0, партия 2.
        {"equip_check",       0x5df6b7, (void*)equipCheckProbe,      (void**)&origEquipCheck},
        {"wand_check",        0x5dbdd2, (void*)wandCheckProbe,       (void**)&origWandCheck},
        // Схема 6.3.
        {"wand_precheck",     0x5dbbe8, (void*)wandPrecheckProbe,    (void**)&origWandPrecheck},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
