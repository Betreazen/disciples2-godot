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
#include "battlemsgdata.h"
#include "d2list.h"
#include "game.h"
#include "idlist.h"
#include "lordtype.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midstack.h"
#include "midunit.h"
#include "ussoldier.h"
#include "usunit.h"
#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <iterator>
#include <string>
#include <vector>

/*
 * Схема корпуса 6.0, партия 2: лидеры, предметы, бой. Выбор навыка лидера человеком и ИИ, очередь
 * выбора, зелье, расход боевых предметов в конце боя, заряд талисмана и опыт группы с Инструктором.
 * Проверки надевания и посоха - в probecheck.cpp, расширения xp и revive - в probehooks.cpp и
 * probeecon.cpp.
 *
 * Адреса - game-pristine/Discipl2.exe (Gog 3.01, ImageBase 0x400000). Разбор каждой точки -
 * disciples-modding/.superpowers/sdd/2026-09-14-probe-v6/spec-leaders.md; пролог (5 байт без
 * ветвлений внутрь), ширина ret и адреса возврата сверены capstone ещё раз перед записью сюда.
 *
 * Все хуки только читают. Ответ оригинала возвращается целым регистром (int), даже там, где
 * движок возвращает bool в al: ширина ответа у этих функций не доказана для каждого вызывающего, и
 * обёртка не должна портить старшие байты eax. В журнал ответ пишется байтом.
 */

namespace probe {

namespace {

using namespace game;

/** Absent or unreadable values are journaled as this, never as a plausible number. */
constexpr int notComputed = -1;

/** Потолок узлов списка предложенных навыков: в GleaUpg.dbf у лорда их около двадцати. */
constexpr std::uint32_t maxOfferIds = 64;

std::uintptr_t callerOf(void* returnAddress)
{
    return reinterpret_cast<std::uintptr_t>(returnAddress);
}

bool lowByte(int value)
{
    return (value & 0xff) != 0;
}

/** Уровень юнита (IUsSoldier::getLevel) или -1. */
int soldierLevel(const IMidgardObjectMap* objectMap, const CMidgardID* unitId)
{
    const auto* unit = readableUnit(objectMap, unitId);
    if (!unit || !readable(unit->unitImpl, sizeof(IUsUnit)))
        return notComputed;

    const auto* soldier = gameFunctions().castUnitImplToSoldier(unit->unitImpl);
    return soldier ? soldier->vftable->getLevel(soldier) : notComputed;
}

int unitHp(const IMidgardObjectMap* objectMap, const CMidgardID* unitId)
{
    const auto* unit = readableUnit(objectMap, unitId);
    return unit ? unit->currentHp : notComputed;
}

/** Тип лорда игрока (TLordType::id, GLord) или "null". */
std::string lordOf(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    const auto* player = readablePlayer(objectMap, playerId);
    const auto& fn = gameFunctions();
    const auto* lord = player && fn.getLordByPlayer ? fn.getLordByPlayer(player) : nullptr;
    return readable(lord, sizeof(TLordType)) ? idToString(&lord->id) : std::string{"null"};
}

/** Список id как JSON-массив строк, по порядку узлов. */
std::string idsArray(const IdList* list)
{
    std::string result{"["};
    if (!readable(list, sizeof(IdList)))
        return result + ']';

    const auto* head = list->head;
    if (!readable(head, sizeof(IdListNode)))
        return result + ']';

    const auto cap = std::min<std::uint32_t>(list->length, maxOfferIds);
    const auto* node = head->next;
    for (std::uint32_t i = 0; i < cap && node != head; ++i) {
        if (!readable(node, sizeof(IdListNode)))
            break;

        result += fmt::format(R"({}"{}")", i ? "," : "", idToString(&node->data));
        node = node->next;
    }

    return result + ']';
}

/** Счётчик выбора навыков отряда (CMidStack::upgCount, +0x9a) или -1. */
int upgCountOf(const IMidgardObjectMap* objectMap, const CMidgardID* stackId)
{
    const auto* stack = readableStack(objectMap, stackId);
    return stack ? static_cast<int>(stack->upgCount) : notComputed;
}

/* ---------------------------------------------------------------------------------------------
 * skill_offer - 0x5d8b84
 */

using SkillOffer = int(__stdcall*)(IdList* out,
                                   const CMidgardID* leaderId,
                                   const CMidgardID* playerId,
                                   const IMidgardObjectMap* objectMap);
SkillOffer origSkillOffer{};

/**
 * Список навыков, доступных лидеру, __stdcall, ret 0x10: записи GleaUpg лорда игрока с
 * MIN_LEVEL <= уровень и MODIF_ID, которого нет у юнита, в порядке массива лорда. Вызывающие: оценка
 * ИИ (0x44f16b), окно выбора человека (0x4a703e), CVisitorLeaderUpgrade::canApply (0x60ea56).
 * Опрашивается ИИ на каждую оценку отряда - поэтому по ключу leader:level:ids:ret. PRIORITY
 * записей зонд не читает: контейнер записей у движка кусочный и в заголовке тулсета описан иначе;
 * вес - GleaUpg.dbf по паре (lord, MODIF_ID).
 */
int __stdcall skillOfferProbe(IdList* out,
                              const CMidgardID* leaderId,
                              const CMidgardID* playerId,
                              const IMidgardObjectMap* objectMap)
{
    const auto seq = nextSeq();

    const int result = origSkillOffer(out, leaderId, playerId, objectMap);

    const auto leader = safeId(leaderId);
    const int level = soldierLevel(objectMap, leaderId);
    const auto ids = idsArray(out);
    const bool ret = lowByte(result);
    emitKeyed(seq, "skill_offer", fmt::format("{}:{}:{}:{}", leader, level, ids, ret),
              fmt::format(R"("ev":"skill_offer","leader":"{}","player":"{}","lord":"{}","level":{},"ids":{},"ret":{})",
                          leader, safeId(playerId), lordOf(objectMap, playerId), level, ids, ret));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * skill_pick - 0x5e84c2, upg_count - 0x5e8513
 */

using SkillPick = int(__stdcall*)(const CMidgardID* stackId,
                                  const CMidgardID* modifId,
                                  int freeSlot,
                                  const IMidgardObjectMap* objectMap,
                                  int applySlot);
SkillPick origSkillPick{};

/**
 * visitorLeaderUpgrade, __stdcall, ret 0x14: (stackId, modifId, free, map, apply). Выбор навыка
 * человеком и ИИ сходится сюда через 0x5d6e21 (возврат 0x5d6e6e); бесплатный - 0x42f4da; фильтр
 * ИИ с apply = 0 - 0x44f35d. Вопросы apply = 0 идут по разу на кандидата каждой оценки ИИ, поэтому
 * только считаются (skill_pick_query), строка - на apply = 1.
 */
int __stdcall skillPickProbe(const CMidgardID* stackId,
                             const CMidgardID* modifId,
                             int freeSlot,
                             const IMidgardObjectMap* objectMap,
                             int applySlot)
{
    if (!lowByte(applySlot)) {
        const int query = origSkillPick(stackId, modifId, freeSlot, objectMap, applySlot);
        countCall("skill_pick_query");
        return query;
    }

    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto* stack = readableStack(objectMap, stackId);
    const auto leader = stack ? idToString(&stack->leaderId) : std::string{"null"};
    const int upgBefore = stack ? static_cast<int>(stack->upgCount) : notComputed;
    const auto rngBefore = rngCount();

    const int result = origSkillPick(stackId, modifId, freeSlot, objectMap, applySlot);

    const auto seqEnd = nextSeq();
    const auto rngAfter = rngCount();
    countCall("skill_pick");
    emit(seq,
         fmt::format(
             R"("ev":"skill_pick","stack":"{}","modif":"{}","free":{},"apply":true,"leader":"{}","upg_before":{},"upg_after":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             safeId(stackId), safeId(modifId), lowByte(freeSlot), leader, upgBefore,
             upgCountOf(objectMap, stackId), rngBefore, rngAfter, hexAddress(returnAddress),
             lowByte(result), seqEnd));
    return result;
}

using UpgCount = int(__stdcall*)(const CMidgardID* stackId,
                                 int count,
                                 const IMidgardObjectMap* objectMap,
                                 int applySlot);
UpgCount origUpgCount{};

/**
 * visitorLeaderUpgradeCount, __stdcall, ret 0x10: (stackId, count, map, apply) - двигает upgCount
 * до count. Вызывающие 0x422467 (повышение лидера), 0x42f100, 0x42f5b8; редко, пишется каждый.
 */
int __stdcall upgCountProbe(const CMidgardID* stackId,
                            int count,
                            const IMidgardObjectMap* objectMap,
                            int applySlot)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const int before = upgCountOf(objectMap, stackId);

    const int result = origUpgCount(stackId, count, objectMap, applySlot);

    const auto seqEnd = nextSeq();
    countCall("upg_count");
    emit(seq,
         fmt::format(
             R"("ev":"upg_count","stack":"{}","count":{},"before":{},"after":{},"apply":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             safeId(stackId), count, before, upgCountOf(objectMap, stackId), lowByte(applySlot),
             hexAddress(returnAddress), lowByte(result), seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * potion_use - 0x5d70f9
 */

using PotionUse = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                  const CMidgardID* playerId,
                                  const CMidgardID* targetId,
                                  const CMidgardID* groupId,
                                  const CMidgardID* itemId,
                                  int destroySlot);
PotionUse origPotionUse{};

/**
 * applyPotion, __stdcall, ret 0x18: (map, player, target, group, item, destroyNow). В бою зовётся
 * из CBatAttackUsePotion::onHit (0x664c4b, destroyNow = 0, строка item_unequip уже лежит перед
 * ней), на карте - 0x42e99b (destroyNow = 1). Предмет может быть уничтожен: id и тип снимаются до
 * вызова.
 */
int __stdcall potionUseProbe(const IMidgardObjectMap* objectMap,
                             const CMidgardID* playerId,
                             const CMidgardID* targetId,
                             const CMidgardID* groupId,
                             const CMidgardID* itemId,
                             int destroySlot)
{
    const auto seq = nextSeq();
    const auto item = safeId(itemId);
    const auto itemType = itemTypeOf(objectMap, itemId);
    const int hpBefore = unitHp(objectMap, targetId);

    const int result = origPotionUse(objectMap, playerId, targetId, groupId, itemId, destroySlot);

    const auto seqEnd = nextSeq();
    countCall("potion_use");
    emit(seq,
         fmt::format(
             R"("ev":"potion_use","player":"{}","target":"{}","group":"{}","item":"{}","item_type":{},"destroy":{},"in_battle":{},"hp_before":{},"hp_after":{},"ret":{},"seq_end":{})",
             safeId(playerId), safeId(targetId), safeId(groupId), item, itemType,
             lowByte(destroySlot), battleActive(), hpBefore, unitHp(objectMap, targetId),
             lowByte(result), seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * item_consume - 0x629824, talisman_charge - 0x5e74bb
 */

/**
 * Надетый предмет отряда в слоте (CMidStack::leaderEquippedItems[slot], +0x7c) в 'out'; false, если
 * группа не отряд или слот не прочитать.
 */
bool equippedItem(const IMidgardObjectMap* objectMap,
                  const CMidgardID* groupId,
                  int slot,
                  CMidgardID& out)
{
    const auto* stack = readableStack(objectMap, groupId);
    if (!stack || slot < 0)
        return false;

    const auto& items = stack->leaderEquippedItems;
    const auto count = items.end > items.bgn ? items.end - items.bgn : 0;
    return slot < count
           && readAt(items.bgn, static_cast<std::size_t>(slot) * sizeof(CMidgardID), out);
}

using ItemConsume = int(__stdcall*)(const IMidgardObjectMap* objectMap,
                                    int slot,
                                    const CMidgardID* groupId,
                                    const BattleMsgData* battleMsgData);
ItemConsume origItemConsume{};

/**
 * Расход боевого предмета в конце боя, __stdcall, ret 0x10: (map, slot, groupId, bmd). Из
 * 0x629755 внутри updateGroupsIfBattleIsOver - до строки battle_end, для слотов 2 и 3 обеих групп,
 * если группа - отряд. Сфера и зелья, применённые в бою, - unEquipItem + DestroyItem; талисман -
 * заряд -1 (talisman_charge), на нуле снимается. Вложенные строки лежат между seq и seq_end.
 */
int __stdcall itemConsumeProbe(const IMidgardObjectMap* objectMap,
                               int slot,
                               const CMidgardID* groupId,
                               const BattleMsgData* battleMsgData)
{
    const auto seq = nextSeq();
    CMidgardID before{};
    const bool hadItem = equippedItem(objectMap, groupId, slot, before);
    // Тип - до вызова: применённый предмет оригинал уничтожает.
    const auto itemType = hadItem ? itemTypeOf(objectMap, &before) : std::string{"null"};
    std::int8_t round = notComputed;
    readAt(battleMsgData, offsetof(BattleMsgData, currentRound), round);

    const int result = origItemConsume(objectMap, slot, groupId, battleMsgData);

    const auto seqEnd = nextSeq();
    CMidgardID after{};
    const bool hasItem = equippedItem(objectMap, groupId, slot, after);
    countCall("item_consume");
    emit(seq,
         fmt::format(
             R"("ev":"item_consume","group":"{}","slot":{},"item_before":"{}","item_type":{},"item_after":"{}","battle_round":{},"seq_end":{})",
             safeId(groupId), slot, hadItem ? idToString(&before) : "null", itemType,
             hasItem ? idToString(&after) : "null", round, seqEnd));
    return result;
}

using TalismanCharge = int(__stdcall*)(const CMidgardID* itemId,
                                       const IMidgardObjectMap* objectMap,
                                       int applySlot);
TalismanCharge origTalismanCharge{};

/**
 * Списание заряда талисмана, __stdcall, ret 0xc: (itemId, map, apply); единственный вызов -
 * 0x6299d6 внутри item_consume. Остаток зарядов (CMidTalismanCharges) не разобран - только факт.
 */
int __stdcall talismanChargeProbe(const CMidgardID* itemId,
                                  const IMidgardObjectMap* objectMap,
                                  int applySlot)
{
    const auto seq = nextSeq();

    const int result = origTalismanCharge(itemId, objectMap, applySlot);

    countCall("talisman_charge");
    emit(seq, fmt::format(R"("ev":"talisman_charge","item":"{}","apply":{},"ret":{})",
                          safeId(itemId), lowByte(applySlot), lowByte(result)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * xp_group - CBatAttackGroupUpgrade::upgradeGroup 0x665217, weapon_master - 0x665851
 */

/** Ответ последнего вызова 0x665851: -1 - не звался с начала текущего upgradeGroup. */
std::atomic<int> weaponMasterAnswer{notComputed};

using WeaponMaster = int(__stdcall*)(const IMidgardObjectMap* objectMap, const CMidgardID* groupId);
WeaponMaster origWeaponMaster{};

/**
 * Есть ли у лидера группы Инструктор (leader->hasAbility(weaponMaster 0x837628)), __stdcall,
 * ret 8: (map, groupId). Единственный вызов - начало upgradeGroup (0x66525e). Строки нет: ответ
 * ложится в поле weapon_master события xp_group.
 */
int __stdcall weaponMasterProbe(const IMidgardObjectMap* objectMap, const CMidgardID* groupId)
{
    const int result = origWeaponMaster(objectMap, groupId);
    countCall("weapon_master");
    weaponMasterAnswer.store(lowByte(result) ? 1 : 0);
    return result;
}

using UpgradeGroup = void(__stdcall*)(IMidgardObjectMap* objectMap,
                                      BattleMsgData* battleMsgData,
                                      const CMidgardID* groupId,
                                      void* attackInfo,
                                      int aiSlot);
UpgradeGroup origUpgradeGroup{};

struct GroupUnit
{
    std::size_t slot;
    std::string id;
    int acc;
};

/**
 * Опыт группы в конце боя, __stdcall, ret 0x14: (map, bmd, groupId, attackInfo, playableRaceAi).
 * На каждого получателя: acc = unitXp слота -> +acc*WEAPN_MSTR/100 при Инструкторе -> +20 % человеку
 * на лёгкой -> не меньше 1 -> бонус ИИ; затем урезание и changeUnitXpCheckUpgrade (строки xp внутри
 * seq...seq_end). После вызова unitXp слота - применённый опыт. Два вызова на бой.
 */
void __stdcall upgradeGroupProbe(IMidgardObjectMap* objectMap,
                                 BattleMsgData* battleMsgData,
                                 const CMidgardID* groupId,
                                 void* attackInfo,
                                 int aiSlot)
{
    const auto seq = nextSeq();
    weaponMasterAnswer.store(notComputed);

    const bool ok = readable(battleMsgData, sizeof(BattleMsgData))
                    && readable(groupId, sizeof(CMidgardID));
    const bool attacker = ok && battleMsgData->attackerGroupId == *groupId;
    std::vector<GroupUnit> units;
    if (ok) {
        for (std::size_t i = 0; i < std::size(battleMsgData->unitsInfo); ++i) {
            const auto& info = battleMsgData->unitsInfo[i];
            if (info.unitId1 == emptyId || info.unitId1 == invalidId
                || info.unitFlags.parts.attacker != attacker)
                continue;

            units.push_back({i, idToString(&info.unitId1), info.unitXp});
        }
    }
    const auto player = ok ? idToString(attacker ? &battleMsgData->attackerPlayerId
                                                 : &battleMsgData->defenderPlayerId)
                           : std::string{"null"};

    origUpgradeGroup(objectMap, battleMsgData, groupId, attackInfo, aiSlot);

    const auto seqEnd = nextSeq();
    std::string list;
    for (const auto& unit : units) {
        list += fmt::format(R"({}{{"id":"{}","acc":{},"applied":{}}})", list.empty() ? "" : ",",
                            unit.id, unit.acc, battleMsgData->unitsInfo[unit.slot].unitXp);
    }

    const int wm = weaponMasterAnswer.load();
    countCall("xp_group");
    emit(seq,
         fmt::format(
             R"("ev":"xp_group","group":"{}","player":"{}","attacker":{},"ai":{},"weapon_master":{},"units":[{}],"seq_end":{})",
             safeId(groupId), player, attacker, lowByte(aiSlot),
             wm < 0 ? std::string{"null"} : (wm ? "true" : "false"), list, seqEnd));
}

} // namespace

} // namespace probe

namespace hooks {

void addLeaderHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // Навыки лидера - одним блоком: start.hooks различает сборки с ними и без них.
    // clang-format off
    const GogHook entries[] = {
        {"skill_offer",     0x5d8b84, (void*)skillOfferProbe,     (void**)&origSkillOffer},
        {"skill_pick",      0x5e84c2, (void*)skillPickProbe,      (void**)&origSkillPick},
        {"upg_count",       0x5e8513, (void*)upgCountProbe,       (void**)&origUpgCount},
        {"potion_use",      0x5d70f9, (void*)potionUseProbe,      (void**)&origPotionUse},
        {"item_consume",    0x629824, (void*)itemConsumeProbe,    (void**)&origItemConsume},
        {"talisman_charge", 0x5e74bb, (void*)talismanChargeProbe, (void**)&origTalismanCharge},
        {"xp_group",        0x665217, (void*)upgradeGroupProbe,   (void**)&origUpgradeGroup},
        {"weapon_master",   0x665851, (void*)weaponMasterProbe,   (void**)&origWeaponMaster},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
