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
#include "batattack.h"
#include "battlemsgdata.h"
#include "categoryids.h"
#include "midgardid.h"
#include <array>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <iterator>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

/*
 * Схема корпуса 6.1: цель атак без урона - attack_hit.
 *
 * До 6.1 журнал называл цель только у атак с уроном (dmg). У оберегов, лечения, паралича, усиления
 * и прочих классов без урона было видно лишь, КТО действует (attack_new), но не НА КОГО: оберег,
 * наложенный в бою, давал иммунитет, которого харнесс С1 не мог объяснить
 * (corpus/20260915-passive/passive-001.jsonl, seq 7798789 и 8264982).
 *
 * Точка - IBatAttack::onHit (слот 12, +0x30) каждого класса. Единственный вызов из боя -
 * CBatLogic::battleTurn 0x6251ab (call [eax+0x30] на объекте, который вернула фабрика 0x644394 ->
 * 0x6445d5 -> createBatAttack 0x644b3e). Все двадцать функций - __thiscall(map, bmd, targetId,
 * attackInfo), пролог push ebp / mov ebp,esp / push -1 / push imm32, выход ret 0x10 (capstone по
 * game-pristine/Discipl2.exe). Оригинал BestowWards 0x65d528: цикл по getWards (+0x38 у атаки
 * this+0x10), для каждого модификатора 0x65d448 -> CMidUnit::addModifier 0x5e9902 ->
 * BattleMsgData 0x62180a(&this->unitId, target, modifier), который пишет пару в modifiedUnits
 * наложившего и id в modifierIds цели - если у наложившего есть свободная пара из восьми (0x62184a).
 *
 * Кто бьёт, onHit не знает: у каждого класса свой объект. Поэтому createBatAttack (attack_new)
 * запоминает созданный объект в кольце, и attack_hit ссылается на ту строку attack_new по new_seq.
 *
 * Хуки только читают: снимок цели в BattleMsgData до и после оригинала.
 */

namespace probe {

namespace {

using namespace game;

constexpr int notComputed = -1;

/* ---------------------------------------------------------------------------------------------
 * Кольцо созданных боевых действий
 */

struct CreatedAttack
{
    const IBatAttack* object;
    std::uint64_t seq;
    std::string attacker;
    std::string attack;
};

/**
 * Потолок кольца. Между созданием действия и его onHit в battleTurn проходит несколько вызовов, но
 * ИИ создаёт объекты и для оценок; 64 с запасом. Адрес, освобождённый и выданный снова, просто
 * перезаписывается более свежим созданием - поиск идёт от последней записи.
 */
constexpr std::size_t createdCapacity = 64;

std::mutex createdMutex;
std::array<CreatedAttack, createdCapacity> created{};
std::size_t createdNext{0};

/* ---------------------------------------------------------------------------------------------
 * attack_hit
 */

using OnHit = void(__fastcall*)(IBatAttack* thisptr,
                                int edx,
                                IMidgardObjectMap* objectMap,
                                BattleMsgData* battleMsgData,
                                CMidgardID* targetUnitId,
                                BattleAttackInfo** attackInfo);

struct HitPoint
{
    const char* name;
    std::uintptr_t target;
    AttackClassId attackClass;
};

// Слот 12 vftable каждого класса, Gog. vftable - по RTTI в .rdata (.?AVCBatAttack*@@); адреса
// BestowWards, Heal, Shatter, DrainLevel, Summon, TransformOther/Self, Doppelganger, GiveAttack
// совпадают с таблицами тулсета (batattack*.cpp). Классы с уроном (Damage, Drain, DrainOverflow)
// не берутся: их цель уже в dmg.
// clang-format off
constexpr HitPoint hitPoints[] = {
    {"hit_paralyze",        0x65ae85, AttackClassId::Paralyze},        // vftable 0x6f2d7c
    {"hit_petrify",         0x65b207, AttackClassId::Petrify},         // 0x6f2dcc
    {"hit_heal",            0x65c417, AttackClassId::Heal},            // 0x6f2ebc
    {"hit_fear",            0x65c76f, AttackClassId::Fear},            // 0x6f2f0c
    {"hit_boost_damage",    0x65cdca, AttackClassId::BoostDamage},     // 0x6f2f5c
    {"hit_bestow_wards",    0x65d528, AttackClassId::BestowWards},     // 0x6f2fac
    {"hit_shatter",         0x65d8dc, AttackClassId::Shatter},         // 0x6f2ffc
    {"hit_lower_damage",    0x65dc91, AttackClassId::LowerDamage},     // 0x6f304c
    {"hit_lower_ini",       0x65e008, AttackClassId::LowerInitiative}, // 0x6f309c
    {"hit_poison",          0x65e39d, AttackClassId::Poison},          // 0x6f30ec
    {"hit_frostbite",       0x65e9c4, AttackClassId::Frostbite},       // 0x6f318c
    {"hit_blister",         0x65efbd, AttackClassId::Blister},         // 0x6f322c
    {"hit_revive",          0x65f4de, AttackClassId::Revive},          // 0x6f32cc
    {"hit_cure",            0x6600c8, AttackClassId::Cure},            // 0x6f336c
    {"hit_give_attack",     0x6603dc, AttackClassId::GiveAttack},      // 0x6f33bc
    {"hit_transform_other", 0x660845, AttackClassId::TransformOther},  // 0x6f340c
    {"hit_transform_self",  0x6613e1, AttackClassId::TransformSelf},   // 0x6f345c
    {"hit_doppelganger",    0x6621cd, AttackClassId::Doppelganger},    // 0x6f34ac
    {"hit_drain_level",     0x662880, AttackClassId::DrainLevel},      // 0x6f3534
    {"hit_summon",          0x6631eb, AttackClassId::Summon},          // 0x6f3584
};
// clang-format on

constexpr std::size_t hitCount = std::size(hitPoints);
OnHit origOnHit[hitCount]{};

/** Снимок цели в BattleMsgData: found = false - юнита с таким id в слотах нет (или bmd не читается). */
struct TargetState
{
    bool found{false};
    int hp{notComputed};
    std::uint64_t statuses{0};
    std::string mods{"null"};
};

TargetState targetState(const BattleMsgData* battleMsgData, const CMidgardID* targetUnitId)
{
    TargetState state;
    if (!readable(battleMsgData, sizeof(BattleMsgData))
        || !readable(targetUnitId, sizeof(CMidgardID)))
        return state;

    for (const auto& info : battleMsgData->unitsInfo) {
        if (info.unitId1 != *targetUnitId)
            continue;

        state.found = true;
        state.hp = info.unitHp;
        state.statuses = info.unitStatuses;
        state.mods = "[";
        for (const auto& modifierId : info.modifierIds) {
            if (modifierId == emptyId || modifierId == invalidId)
                continue;

            state.mods += fmt::format(R"({}"{}")", state.mods.size() > 1 ? "," : "",
                                      idToString(&modifierId));
        }
        state.mods += ']';
        break;
    }

    return state;
}

/** Строка attack_new, создавшая 'object': (seq, attacker, attack) или (-1, "null", "null"). */
std::string creatorFields(const IBatAttack* object)
{
    std::lock_guard<std::mutex> lock(createdMutex);
    for (std::size_t i = 0; i < createdCapacity; ++i) {
        const auto& entry = created[(createdNext + createdCapacity - 1 - i) % createdCapacity];
        if (entry.object && entry.object == object)
            return fmt::format(R"("new_seq":{},"attacker":"{}","attack":"{}")", entry.seq,
                               entry.attacker, entry.attack);
    }

    return R"("new_seq":-1,"attacker":"null","attack":"null")";
}

void onHit(std::size_t index,
           IBatAttack* thisptr,
           IMidgardObjectMap* objectMap,
           BattleMsgData* battleMsgData,
           CMidgardID* targetUnitId,
           BattleAttackInfo** attackInfo)
{
    const auto seq = nextSeq();
    const auto before = targetState(battleMsgData, targetUnitId);

    origOnHit[index](thisptr, 0, objectMap, battleMsgData, targetUnitId, attackInfo);

    const auto seqEnd = nextSeq();
    const auto after = targetState(battleMsgData, targetUnitId);
    countCall("attack_hit");
    emit(seq,
         fmt::format(
             R"("ev":"attack_hit","disp_class":{},{},"target":"{}","found":{},"hp_before":{},"hp_after":{},"st_before":{},"st_after":{},"mods_before":{},"mods_after":{},"seq_end":{})",
             static_cast<int>(hitPoints[index].attackClass), creatorFields(thisptr),
             safeId(targetUnitId), before.found && after.found, before.hp, after.hp,
             before.statuses, after.statuses, before.mods, after.mods, seqEnd));
}

/** Своя обёртка на каждую точку: у каждой свой оригинал (трамплин Detours). */
template <std::size_t I>
void __fastcall onHitProbe(IBatAttack* thisptr,
                           int /* %edx */,
                           IMidgardObjectMap* objectMap,
                           BattleMsgData* battleMsgData,
                           CMidgardID* targetUnitId,
                           BattleAttackInfo** attackInfo)
{
    onHit(I, thisptr, objectMap, battleMsgData, targetUnitId, attackInfo);
}

template <std::size_t... I>
constexpr std::array<OnHit, sizeof...(I)> makeProbes(std::index_sequence<I...>)
{
    return {{onHitProbe<I>...}};
}

constexpr auto hitProbes = makeProbes(std::make_index_sequence<hitCount>{});

} // namespace

void rememberBatAttack(const IBatAttack* object,
                       std::uint64_t seq,
                       const std::string& attacker,
                       const std::string& attack)
{
    if (!object)
        return;

    std::lock_guard<std::mutex> lock(createdMutex);
    created[createdNext] = {object, seq, attacker, attack};
    createdNext = (createdNext + 1) % createdCapacity;
}

} // namespace probe

namespace hooks {

void addHitHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    std::vector<GogHook> entries;
    entries.reserve(hitCount);
    for (std::size_t i = 0; i < hitCount; ++i) {
        entries.push_back({hitPoints[i].name, hitPoints[i].target, (void*)hitProbes[i],
                           (void**)&origOnHit[i]});
    }

    addGogHooks(entries.data(), entries.size(), hooks, installed);
}

} // namespace hooks
