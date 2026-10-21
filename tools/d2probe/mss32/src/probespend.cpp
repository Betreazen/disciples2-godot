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
#include "currency.h"
#include "gameutils.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midplayer.h"
#include "midstack.h"
#include "mqpoint.h"
#include "version.h"
#include <cstdint>
#include <fmt/format.h>
#include <string>
#include <vector>

/*
 * Схема корпуса 5.3: события-причины трат казны, у которых в схеме 5.2 причины не было.
 *
 * Каждое движение казны идёт через помощник 0x5e6920 (игрок, новая казна, карта, apply): он
 * строит CVisitorChangePlayerBank и применяет его - отсюда строка bank. Сканирование .text даёт
 * у помощника 27 вызовов; вызывающие опознаны по RTTI vftable эффекта или цепочкой вызовов.
 * Здесь - шесть путей, у которых события не было (game-pristine/Discipl2.exe, ImageBase 0x400000):
 *
 *   hire_unit       0x5d565f  найм юнита в группу; ИИ - CAiHireUnit через 0x41f01c -> 0x42e69d
 *   hire_leader     0x5d6957  найм лидера;          ИИ - CAiHireLeader через 0x41ef71 -> 0x42e554
 *   rod_drop        0x437606  CEffectDropRod::apply, vftable 0x6ceb94 слот +4
 *   spell_cast      0x5dc2d6  единственный вызов - CEffectSpell::apply 0x43e4a7
 *   spell_research  0x5dc1d7  единственный вызов - CEffectSpellResearch::apply 0x43e84c
 *   wand_cast       0x5dc4d6  единственный вызов - CEffectWand::apply 0x43f68d
 *
 * У каждой функции прочитаны ширина ret и то, что движок делает с каждым слотом [ebp+8]...
 * Разбор - docs/corpus-schema.md, раздел «Причины трат казны». Адреса подтверждены только на Gog.
 *
 * Строка несёт казну платящего игрока до и после оригинала (цена = before - after) и seq_end -
 * номер, взятый сразу после возврата: вложенные строки вызова, в том числе его bank, лежат строго
 * между seq и seq_end. Номер seq_end - обычный разрыв seq, такие разрывы в журнале штатны.
 */

namespace probe {

namespace {

using namespace game;

/** Absent or unreadable values are journaled as this, never as a plausible number. */
constexpr int notComputed = -1;

/** Поля CEffectDropRod, прочитанные по apply 0x437606: отряд +4, клетка +8 (два int подряд). */
struct DropRodEffect
{
    const void* vftable;
    CMidgardID stackId;
    CMqPoint position;
};

using HireUnit = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                  const CMidgardID* playerId,
                                  const CMidgardID* unitTypeId,
                                  const char* name,
                                  const CMidgardID* groupId,
                                  int position);
using HireLeader = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                    const CMidgardID* playerId,
                                    const CMidgardID* fortId,
                                    const CMidgardID* unitTypeId,
                                    const char* name,
                                    const void* order,
                                    const void* orderTarget);
using DropRodApply = bool(__fastcall*)(const DropRodEffect* thisptr,
                                       int edx,
                                       IMidgardObjectMap* objectMap,
                                       const void* arg2,
                                       const void* arg3);
using CastSpell = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                   const CMidgardID* playerId,
                                   const CMidgardID* spellId,
                                   const CMqPoint* position,
                                   int useSpellBankSlot,
                                   int freeSlot);
using ResearchSpell = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                       const CMidgardID* playerId,
                                       const CMidgardID* spellId,
                                       int freeSlot);
using CastWand = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                  const CMidgardID* playerId,
                                  const CMidgardID* targetId,
                                  const CMidgardID* ownerId,
                                  const CMidgardID* itemId,
                                  const CMqPoint* position);

/* Trampolines to the originals. Filled by Detours through HookInfo::original (main.cpp). */
HireUnit origHireUnit{};
HireLeader origHireLeader{};
DropRodApply origDropRodApply{};
CastSpell origCastSpell{};
ResearchSpell origResearchSpell{};
CastWand origCastWand{};

/** Игрок по id, если читаются карта, id и сам объект игрока; иначе nullptr. */
const CMidPlayer* playerOf(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    if (!readable(objectMap, sizeof(IMidgardObjectMap)) || !readable(playerId, sizeof(CMidgardID)))
        return nullptr;

    const auto* player = hooks::getPlayer(objectMap, playerId);
    return readable(player, sizeof(CMidPlayer)) ? player : nullptr;
}

/** Основная казна игрока (CMidPlayer::bank) или пустой Bank - как у хука bank. */
Bank purse(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    const auto* player = playerOf(objectMap, playerId);
    return player ? player->bank : Bank{};
}

/** Второй кошелёк (CMidPlayer::spellBank, +0x7c) - его двигает визитор казны режима 1. */
Bank spellPurse(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    const auto* player = playerOf(objectMap, playerId);
    return player ? player->spellBank : Bank{};
}

/** "x":..,"y":.. клетки; -1, если точку не прочитать. */
std::string pointFields(const CMqPoint* point)
{
    const bool ok = readable(point, sizeof(CMqPoint));
    return fmt::format(R"("x":{},"y":{})", ok ? point->x : notComputed,
                       ok ? point->y : notComputed);
}

/** Пишет строку причины: поля вызова, казна до и после, seq_end и ответ оригинала. */
void emitSpend(std::uint64_t seq,
               const char* event,
               const std::string& fields,
               const Bank& before,
               const Bank& after,
               bool result)
{
    const auto seqEnd = nextSeq();
    countCall(event);
    emit(seq, fmt::format(R"("ev":"{}",{},"before":{},"after":{},"seq_end":{},"ret":{})", event,
                          fields, bankObject(before), bankObject(after), seqEnd, result));
}

/**
 * Найм юнита, 0x5d565f, __stdcall, ret 0x18: (map, playerId, unitTypeId, name, groupId, position).
 * Проверка 0x5deea7; цена - слот +0x54 импла из таблицы юнитов по unitTypeId (Gunits.ENROLL_C);
 * списание 0x5e6920 по playerId; затем AddUnitToGroup 0x5e78f7 (unitTypeId, groupId, position),
 * группа - GetStackFortRuinGroup 0x5f4f87. Имя (может быть пустым) уходит в 0x5e768f и не пишется.
 */
bool __stdcall hireUnitProbe(IMidgardObjectMap* objectMap,
                             const CMidgardID* playerId,
                             const CMidgardID* unitTypeId,
                             const char* name,
                             const CMidgardID* groupId,
                             int position)
{
    const auto seq = nextSeq();
    const Bank before = purse(objectMap, playerId);

    const bool result = origHireUnit(objectMap, playerId, unitTypeId, name, groupId, position);

    emitSpend(seq, "hire_unit",
              fmt::format(R"("player":"{}","unit_type":"{}","group":"{}","position":{})",
                          safeId(playerId), safeId(unitTypeId), safeId(groupId), position),
              before, purse(objectMap, playerId), result);
    return result;
}

/**
 * Найм лидера, 0x5d6957, __stdcall, ret 0x1c:
 * (map, playerId, fortId, unitTypeId, name, order, orderTarget).
 * Проверка 0x5de921 (map, playerId, unitTypeId); цена - слот +0x54 по unitTypeId; списание
 * 0x5e6920; новый отряд 0x5e6e43 (unitTypeId, fortId); fortId движок приводит к CFortification
 * (typeinfo 0x78ce10) и берёт отряд с +0x8c. Имя и два последних слота (приказ, 0x5e811a) не пишутся.
 */
bool __stdcall hireLeaderProbe(IMidgardObjectMap* objectMap,
                               const CMidgardID* playerId,
                               const CMidgardID* fortId,
                               const CMidgardID* unitTypeId,
                               const char* name,
                               const void* order,
                               const void* orderTarget)
{
    const auto seq = nextSeq();
    const Bank before = purse(objectMap, playerId);

    const bool result = origHireLeader(objectMap, playerId, fortId, unitTypeId, name, order,
                                       orderTarget);

    emitSpend(seq, "hire_leader",
              fmt::format(R"("player":"{}","fort":"{}","unit_type":"{}")", safeId(playerId),
                          safeId(fortId), safeId(unitTypeId)),
              before, purse(objectMap, playerId), result);
    return result;
}

/**
 * Постановка жезла, CEffectDropRod::apply 0x437606, __thiscall, ret 0xc: (map, ...).
 * Проверка 0x5d9e59 (stackId, position, map); трата хода 0x5e780d; цена GVars ROD_COST
 * (0x587851); списание 0x5e6920 по владельцу отряда (CMidStack +0x8c); CVisitorCreateRod 0x5e86ef.
 * Владелец и поля эффекта читаются до вызова, пока отряд заведомо на месте.
 */
bool __fastcall dropRodApplyProbe(const DropRodEffect* thisptr,
                                  int /* %edx */,
                                  IMidgardObjectMap* objectMap,
                                  const void* arg2,
                                  const void* arg3)
{
    const auto seq = nextSeq();
    const bool readableThis = readable(thisptr, sizeof(DropRodEffect));
    const bool readableMap = readable(objectMap, sizeof(IMidgardObjectMap));
    const CMidgardID stackId = readableThis ? thisptr->stackId : CMidgardID{};
    const CMqPoint position = readableThis ? thisptr->position : CMqPoint{notComputed, notComputed};
    const auto* stack = readableThis && readableMap ? hooks::getStack(objectMap, &stackId) : nullptr;
    const CMidgardID ownerId = stack ? stack->ownerId : CMidgardID{};
    const auto* owner = stack ? &ownerId : nullptr;
    const Bank before = purse(objectMap, owner);

    const bool result = origDropRodApply(thisptr, 0, objectMap, arg2, arg3);

    emitSpend(seq, "rod_drop",
              fmt::format(R"("player":"{}","stack":"{}",{})",
                          owner ? idToString(owner) : std::string{"null"},
                          readableThis ? idToString(&stackId) : std::string{"null"},
                          pointFields(&position)),
              before, purse(objectMap, owner), result);
    return result;
}

/**
 * Заклинание на карте, 0x5dc2d6, __stdcall, ret 0x18:
 * (map, playerId, spellId, position, useSpellBank, free); оба флага движок сравнивает байтом.
 * Проверка 0x5dbd75; при играбельной расе (0x5e5358) и free = 0 - оплата 0x5dc334 и запись каста
 * 0x5dc481. Оплата: цена - Gspells.CASTING_C (0x582afa); если useSpellBank и основной казны не
 * хватает, недостача снимается со spellBank визитором режима 1 (0x5e69b3), а основная казна
 * обнуляется целиком, золото тоже (0x586b5a -> 0x5e6920); иначе основная казна минус цена.
 */
bool __stdcall castSpellProbe(IMidgardObjectMap* objectMap,
                              const CMidgardID* playerId,
                              const CMidgardID* spellId,
                              const CMqPoint* position,
                              int useSpellBankSlot,
                              int freeSlot)
{
    const auto seq = nextSeq();
    const Bank before = purse(objectMap, playerId);
    const Bank spellBankBefore = spellPurse(objectMap, playerId);

    const bool result = origCastSpell(objectMap, playerId, spellId, position, useSpellBankSlot,
                                      freeSlot);

    emitSpend(
        seq, "spell_cast",
        fmt::format(
            R"("player":"{}","spell":"{}",{},"use_spell_bank":{},"free":{},"spell_bank_before":{},"spell_bank_after":{})",
            safeId(playerId), safeId(spellId), pointFields(position),
            (useSpellBankSlot & 0xff) != 0, (freeSlot & 0xff) != 0, bankObject(spellBankBefore),
            bankObject(spellPurse(objectMap, playerId))),
        before, purse(objectMap, playerId), result);
    return result;
}

/**
 * Изучение заклинания, 0x5dc1d7, __stdcall, ret 0x10: (map, playerId, spellId, free).
 * Проверка 0x5dbf3d; при free = 0 цена - 0x604039: GspellR.RESEARCH для лорда игрока (CMidPlayer
 * +0x28 -> таблица лордов 0x580c36 -> 0x58b6d2 по spellId), у лорда категории [0x837760] пополам
 * (0x5867f0); не хватает - false без списания, иначе 0x5e6920. Затем заклинание игроку, 0x5e6afc.
 */
bool __stdcall researchSpellProbe(IMidgardObjectMap* objectMap,
                                  const CMidgardID* playerId,
                                  const CMidgardID* spellId,
                                  int freeSlot)
{
    const auto seq = nextSeq();
    const Bank before = purse(objectMap, playerId);

    const bool result = origResearchSpell(objectMap, playerId, spellId, freeSlot);

    emitSpend(seq, "spell_research",
              fmt::format(R"("player":"{}","spell":"{}","free":{})", safeId(playerId),
                          safeId(spellId), (freeSlot & 0xff) != 0),
              before, purse(objectMap, playerId), result);
    return result;
}

/**
 * Посох или свиток на карте, 0x5dc4d6, __stdcall, ret 0x18:
 * (map, playerId, targetId, ownerId, itemId, position).
 * Проверка 0x5dbdd2 (itemId, targetId, position), у targetId движок берёт владельца (0x5f501a);
 * цена - слот +4 заклинательного расширения предмета (0x407b7e); не хватает - false; списание
 * 0x5e6920; отметка применения 0x5e7f59; расходуемый предмет (слот +0xc) уничтожается из
 * инвентаря ownerId - DestroyItem 0x5e737d.
 */
bool __stdcall castWandProbe(IMidgardObjectMap* objectMap,
                             const CMidgardID* playerId,
                             const CMidgardID* targetId,
                             const CMidgardID* ownerId,
                             const CMidgardID* itemId,
                             const CMqPoint* position)
{
    const auto seq = nextSeq();
    const Bank before = purse(objectMap, playerId);
    // Предмет может быть уничтожен оригиналом: id и клетка снимаются до вызова.
    const auto item = safeId(itemId);
    const auto cell = pointFields(position);

    const bool result = origCastWand(objectMap, playerId, targetId, ownerId, itemId, position);

    emitSpend(seq, "wand_cast",
              fmt::format(R"("player":"{}","target":"{}","owner":"{}","item":"{}",{})",
                          safeId(playerId), safeId(targetId), safeId(ownerId), item, cell),
              before, purse(objectMap, playerId), result);
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addSpendHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    // Адреса подтверждены только на Gog; на прочих сборках хуки не ставятся, как нуль в таблице.
    if (gameVersion() != GameVersion::Gog)
        return;

    struct Entry
    {
        const char* name;
        std::uintptr_t target;
        void* hook;
        void** original;
    };

    using namespace probe;
    // clang-format off
    const Entry entries[] = {
        {"hire_unit",      0x5d565f, (void*)hireUnitProbe,      (void**)&origHireUnit},
        {"hire_leader",    0x5d6957, (void*)hireLeaderProbe,    (void**)&origHireLeader},
        {"rod_drop",       0x437606, (void*)dropRodApplyProbe,  (void**)&origDropRodApply},
        {"spell_cast",     0x5dc2d6, (void*)castSpellProbe,     (void**)&origCastSpell},
        {"spell_research", 0x5dc1d7, (void*)researchSpellProbe, (void**)&origResearchSpell},
        {"wand_cast",      0x5dc4d6, (void*)castWandProbe,      (void**)&origCastWand},
    };
    // clang-format on

    for (const auto& entry : entries) {
        if (!probe::hookEnabled(entry.name))
            continue;

        hooks.push_back({reinterpret_cast<void*>(entry.target), entry.hook, entry.original});
        installed.push_back(entry.name);
    }
}

} // namespace hooks
