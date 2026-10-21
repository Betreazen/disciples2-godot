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
#include "attack.h"
#include "batattack.h"
#include "batlogic.h"
#include "battlemsgdata.h"
#include "categoryids.h"
#include "currency.h"
#include "game.h"
#include "menuphase.h"
#include "gameutils.h"
#include "menuphasehooks.h"
#include "midgardid.h"
#include "midgardmapblock.h"
#include "midplayer.h"
#include "midsite.h"
#include "midstack.h"
#include "midunit.h"
#include "mqpoint.h"
#include "originalfunctions.h"
#include "racetype.h"
#include "scenarioinfo.h"
#include "terraincat.h"
#include "ussoldier.h"
#include "usunit.h"
#include "version.h"
#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <string>

/*
 * Probe hooks. Every hook takes a sequence number, calls the original engine function and writes
 * a single JSONL line. None of them changes the result: the journal must describe vanilla
 * behaviour. 'seq' is taken before the call, so nested events (computeDamage calls
 * computeDamageWithBuffs, computeArmor and getAttackPower) can be nested back by their numbers
 * even though their lines are written first.
 *
 * The battle state hooks write their line BEFORE the original call where the line describes the
 * state the call starts from (battle_start, battle_units, battle_turn), and after it where the
 * line describes what the call produced (round, turn_before, turn_after, battle_end).
 */

namespace probe {

namespace {

/* Absent out-parameters are journaled as this value, never as a plausible number. */
constexpr int notComputed = -1;

/**
 * BattleMsgData of the battle that already produced a battle_start line, null between battles.
 * A CBatLogic::battleTurn call with another BattleMsgData is therefore a new battle. Reset by the
 * BattleMsgData constructor and not by battle_end: healers get one more turn after the battle is
 * over, and that must not read as a second battle. The engine runs battles on one thread, the
 * atomics only guard against a surprise.
 */
std::atomic<const game::BattleMsgData*> currentBattle{nullptr};

/** Battle that already produced a battle_end line: updateGroupsIfBattleIsOver is called again. */
std::atomic<const game::BattleMsgData*> endedBattle{nullptr};

/** Battle that already produced an autobattle_forced line: the flag readers are asked a lot. */
std::atomic<const game::BattleMsgData*> announcedBattle{nullptr};

/**
 * Battle that already produced a battle_units line. Separate from currentBattle: a live run shows
 * autobattle_forced (and the whole first beforeBattleRound call) landing before the first
 * battleTurn call, so reusing either of the other two atomics here would make firstTimeSeen()
 * report "already seen" and battle_units would never be written.
 */
std::atomic<const game::BattleMsgData*> unitsAnnounced{nullptr};

/** Drops battleMsgData from slot if it is the one remembered there. */
void forgetBattle(std::atomic<const game::BattleMsgData*>& slot,
                  const game::BattleMsgData* battleMsgData)
{
    const game::BattleMsgData* remembered = battleMsgData;
    slot.compare_exchange_strong(remembered, nullptr);
}

/** True once per battleMsgData, i.e. the first time the battle is seen in this slot. */
bool firstTimeSeen(std::atomic<const game::BattleMsgData*>& slot,
                   const game::BattleMsgData* battleMsgData)
{
    const game::BattleMsgData* remembered = slot.load();
    return remembered != battleMsgData && slot.compare_exchange_strong(remembered, battleMsgData);
}

/** Unit type, level and max hp from Gunits, so the journal can be joined with the game data. */
std::string identityFields(const game::IMidgardObjectMap* objectMap,
                           const game::CMidgardID* unitId)
{
    const auto& fn = game::gameFunctions();
    const game::CMidUnit* unit = objectMap ? fn.findUnitById(objectMap, unitId) : nullptr;
    if (!unit || !unit->unitImpl)
        return {};

    const auto* soldier = fn.castUnitImplToSoldier(unit->unitImpl);
    if (!soldier)
        return fmt::format(R"(,"type":"{}")", idToString(&unit->unitImpl->id));

    return fmt::format(R"(,"type":"{}","level":{},"hp_max":{})", idToString(&unit->unitImpl->id),
                       soldier->vftable->getLevel(soldier), soldier->vftable->getHitPoints(soldier));
}

/** Схема 6.0, партия 2: юнит строки xp - HP, опыт, уровень, XP_NEXT, тип и лидер ли. */
struct XpUnitState
{
    int hp{notComputed};
    int xp{notComputed};
    int level{notComputed};
    int xpNext{notComputed};
    std::string impl{"null"};
    const char* leader{"null"};
};

XpUnitState xpUnitState(const game::IMidgardObjectMap* objectMap, const game::CMidgardID* unitId)
{
    XpUnitState state;
    const auto* unit = readableUnit(objectMap, unitId);
    if (!unit)
        return state;

    state.hp = unit->currentHp;
    state.xp = unit->currentXp;
    const auto* impl = unit->unitImpl;
    if (!readable(impl, sizeof(game::IUsUnit)))
        return state;

    const auto& fn = game::gameFunctions();
    state.impl = idToString(&impl->id);
    state.leader = fn.castUnitImplToStackLeader(impl) ? "true" : "false";
    if (const auto* soldier = fn.castUnitImplToSoldier(impl)) {
        state.level = soldier->vftable->getLevel(soldier);
        state.xpNext = soldier->vftable->getXpNext(soldier);
    }

    return state;
}

/** CScenarioInfo::leaderMaxLevel (+0x68, MAX_LEADER) или -1. */
int leaderMaxLevel(const game::IMidgardObjectMap* objectMap)
{
    if (!readableMap(objectMap))
        return notComputed;

    const auto* info = hooks::getScenarioInfo(objectMap);
    return readable(info, sizeof(game::CScenarioInfo)) ? info->leaderMaxLevel : notComputed;
}

/**
 * True for a battle slot / turn-order id that has never held a unit. resetUnitInfo()
 * (battlemsgdatahooks.cpp) sets an empty unitsInfo slot's id to invalidId, not emptyId - checking
 * both keeps every slot filter correct regardless of which sentinel a given field actually uses.
 */
bool isUnoccupied(const game::CMidgardID& id)
{
    return id == game::emptyId || id == game::invalidId;
}

/** Builds the "units":[..] field over the occupied slots. objectMap != null adds identityFields. */
std::string unitsField(const game::BattleMsgData* battleMsgData,
                       const game::IMidgardObjectMap* objectMap)
{
    std::string result{R"("units":[)"};
    if (!battleMsgData)
        return result + ']';

    // A full group is 12 units of ~160 bytes of JSON each; one allocation instead of a dozen.
    result.reserve(2048);

    const char* separator = "";
    for (const auto& info : battleMsgData->unitsInfo) {
        if (isUnoccupied(info.unitId1))
            continue;

        result += fmt::format(
            R"({}{{"id":"{}","hp":{},"xp":{},"st":{},"st2":{},"flags":{},"shattered":{},"fort":{}{}}})",
            separator, idToString(&info.unitId1), info.unitHp, info.unitXp, info.unitStatuses,
            info.unitStatuses2, static_cast<std::uint32_t>(info.unitFlags.value),
            info.shatteredArmor, info.fortificationArmor, identityFields(objectMap, &info.unitId1));
        separator = ",";
    }

    return result + ']';
}

/**
 * Builds the "order":[..] field: turnsOrder up to the first empty id.
 *
 * The engine builds turnsOrder at a moment of its own choosing; at a hook that runs before that,
 * every entry is still invalidId and the field comes out as an empty array. That is why the same
 * field is written from four events (round, turn_before, turn_after, battle_units) - whichever of
 * them the engine has already filled it for shows it. See docs/corpus-schema.md.
 */
std::string orderField(const game::BattleMsgData* battleMsgData)
{
    std::string result{R"("order":[)"};
    if (!battleMsgData)
        return result + ']';

    const char* separator = "";
    for (const auto& turn : battleMsgData->turnsOrder) {
        if (isUnoccupied(turn.unitId))
            break;

        result += fmt::format(R"({}{{"id":"{}","attacks":{}}})", separator, idToString(&turn.unitId),
                              static_cast<int>(turn.attackCount));
        separator = ",";
    }

    return result + ']';
}

/** Builds the "units":[..] field for battle_units: id plus identity (type/level/hp_max) when the
 *  unit is found in objectMap - same identityFields() rule as before, no invented values. */
std::string battleUnitsField(const game::BattleMsgData* battleMsgData,
                             const game::IMidgardObjectMap* objectMap)
{
    std::string result{R"("units":[)"};
    if (!battleMsgData)
        return result + ']';

    const char* separator = "";
    for (const auto& info : battleMsgData->unitsInfo) {
        if (isUnoccupied(info.unitId1))
            continue;

        result += fmt::format(R"({}{{"id":"{}"{}}})", separator, idToString(&info.unitId1),
                              identityFields(objectMap, &info.unitId1));
        separator = ",";
    }

    return result + ']';
}

int currentRound(const game::BattleMsgData* battleMsgData)
{
    return battleMsgData ? static_cast<int>(battleMsgData->currentRound) : notComputed;
}

int __stdcall generateRandomNumberProbe(unsigned int maxValue)
{
    const auto seq = nextSeq();
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const int result = hooks::getOriginalFunctions().generateRandomNumber(maxValue);

    emitRng(seq, false, maxValue, result);
    // Схема 6.5: броски выбора ИИ - отдельной строкой и вне боя.
    tagAiRng(seq, returnAddress, _AddressOfReturnAddress(), false, maxValue, result);
    return result;
}

int __stdcall generateRandomNumberStdProbe(unsigned int maxValue)
{
    const auto seq = nextSeq();
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const int result = hooks::getOriginalFunctions().generateRandomNumberStd(maxValue);

    emitRng(seq, true, maxValue, result);
    tagAiRng(seq, returnAddress, _AddressOfReturnAddress(), true, maxValue, result);
    return result;
}

bool __stdcall attackShouldMissProbe(const int* power)
{
    const auto seq = nextSeq();
    const auto before = rngCount();
    const bool miss = hooks::getOriginalFunctions().attackShouldMiss(power);

    emit(seq, fmt::format(R"("ev":"miss","power":{},"miss":{},"rng_before":{},"rng_after":{})",
                          power ? *power : notComputed, miss, before, rngCount()));
    return miss;
}

int __stdcall computeDamageProbe(const game::IMidgardObjectMap* objectMap,
                                 const game::BattleMsgData* battleMsgData,
                                 const game::IAttack* attack,
                                 const game::CMidgardID* attackerUnitId,
                                 const game::CMidgardID* targetUnitId,
                                 bool computeCriticalHit,
                                 int* attackDamage,
                                 int* criticalHitDamage)
{
    const auto seq = nextSeq();
    const auto before = rngCount();
    const int result = hooks::getOriginalFunctions().computeDamage(objectMap, battleMsgData, attack,
                                                                   attackerUnitId, targetUnitId,
                                                                   computeCriticalHit, attackDamage,
                                                                   criticalHitDamage);

    emit(seq,
         fmt::format(
             R"("ev":"dmg","attacker":"{}","target":"{}","crit":{},"ret":{},"dmg":{},"crit_dmg":{},"rng_before":{},"rng_after":{})",
             idToString(attackerUnitId), idToString(targetUnitId), computeCriticalHit, result,
             attackDamage ? *attackDamage : notComputed,
             criticalHitDamage ? *criticalHitDamage : notComputed, before, rngCount()));
    return result;
}

int __stdcall computeDamageMaxProbe(const game::IMidgardObjectMap* objectMap,
                                    const game::CMidgardID* unitId)
{
    const auto seq = nextSeq();
    const int result = hooks::getOriginalFunctions().computeDamageMax(objectMap, unitId);

    emit(seq, fmt::format(R"("ev":"dmg_max","unit":"{}","ret":{})", idToString(unitId), result));
    return result;
}

int __stdcall computeDamageWithBuffsProbe(const game::IAttack* attack,
                                          int damageMax,
                                          const game::BattleMsgData* battleMsgData,
                                          const game::CMidgardID* unitId,
                                          bool addRandomDamage,
                                          bool easyDifficulty)
{
    const auto seq = nextSeq();
    const int result = hooks::getOriginalFunctions()
                           .computeDamageWithBuffs(attack, damageMax, battleMsgData, unitId,
                                                   addRandomDamage, easyDifficulty);

    emit(seq,
         fmt::format(R"("ev":"dmg_buffs","unit":"{}","dmg_max":{},"random":{},"easy":{},"ret":{})",
                     idToString(unitId), damageMax, addRandomDamage, easyDifficulty, result));
    return result;
}

int* __stdcall computeArmorProbe(int* armor,
                                 const game::IMidgardObjectMap* objectMap,
                                 const game::BattleMsgData* battleMsgData,
                                 const game::CMidgardID* unitId)
{
    const auto seq = nextSeq();
    int* result = hooks::getOriginalFunctions().computeArmor(armor, objectMap, battleMsgData,
                                                             unitId);

    emit(seq, fmt::format(R"("ev":"armor","unit":"{}","ret":{})", idToString(unitId),
                          result ? *result : notComputed));
    return result;
}

void __stdcall getAttackPowerProbe(int* power,
                                   const game::IAttack* attack,
                                   const game::IMidgardObjectMap* objectMap,
                                   const game::CMidgardID* unitId,
                                   const game::BattleMsgData* battleMsgData)
{
    const auto seq = nextSeq();
    hooks::getOriginalFunctions().getAttackPower(power, attack, objectMap, unitId, battleMsgData);

    emit(seq, fmt::format(R"("ev":"power","unit":"{}","ret":{})", idToString(unitId),
                          power ? *power : notComputed));
}

/*
 * Magic, items and modifiers, corpus schema v4. Same rule as everywhere else here: addresses are
 * never written in this file, getProbeHooks() reads them from the version table (game.cpp, third
 * Functions{} block = Gog); the Gog values are listed in docs/corpus-schema.md.
 */

/**
 * The identity of an attack: its own id plus the class and the source it is dispatched on. An
 * absent attack is journaled as "null" / -1, never as a plausible category.
 */
std::string attackFields(const game::IAttack* attack)
{
    if (!attack || !attack->vftable)
        return fmt::format(R"("attack":"null","class":{},"source":{})", notComputed, notComputed);

    const auto* attackClass = attack->vftable->getAttackClass(attack);
    const auto* attackSource = attack->vftable->getAttackSource(attack);
    return fmt::format(R"("attack":"{}","class":{},"source":{})", idToString(&attack->id),
                       attackClass ? static_cast<int>(attackClass->id) : notComputed,
                       attackSource ? static_cast<int>(attackSource->id) : notComputed);
}

/**
 * getAttackById as the probe should call it from another hook: the trampoline when the attack_get
 * hook is installed (so the lookup does not re-enter it and does not inflate its counter), the raw
 * address otherwise - where it is the original anyway, because nothing was attached to it.
 */
game::GetAttackById getAttackByIdForProbe()
{
    const auto original = hooks::getOriginalFunctions().getAttackById;
    return original ? original : game::gameFunctions().getAttackById;
}

bool __stdcall isUnitImmuneToAttackProbe(const game::IMidgardObjectMap* objectMap,
                                         const game::BattleMsgData* battleMsgData,
                                         const game::CMidgardID* unitId,
                                         const game::IAttack* attack,
                                         bool checkAlwaysImmuneOnly)
{
    const auto seq = nextSeq();
    const bool result = hooks::getOriginalFunctions().isUnitImmuneToAttack(objectMap, battleMsgData,
                                                                           unitId, attack,
                                                                           checkAlwaysImmuneOnly);

    // Asked over and over for the same pair while the battle UI marks reachable targets, hence the
    // key. checkAlwaysImmuneOnly is part of it because it changes the answer, and so is the answer
    // itself: a ward put on the unit mid-battle flips the same (unit, attack) pair, and both
    // outcomes must reach the journal. The unbudgeted counter below is what keeps "no line" and
    // "never called" apart.
    countCall("immune");
    const auto identity = attackFields(attack);
    const auto key = fmt::format("{}:{}:{}:{}", idToString(unitId), identity,
                                 int(checkAlwaysImmuneOnly), int(result));
    emitKeyed(seq, "immune", key,
              fmt::format(R"("ev":"immune","unit":"{}",{},"always_only":{},"ret":{})",
                          idToString(unitId), identity, checkAlwaysImmuneOnly, result));
    return result;
}

game::IAttack* __stdcall getAttackByIdProbe(const game::IMidgardObjectMap* objectMap,
                                            const game::CMidgardID* id,
                                            int attackNumber,
                                            bool checkAltAttack)
{
    const auto seq = nextSeq();
    game::IAttack* result = hooks::getOriginalFunctions().getAttackById(objectMap, id, attackNumber,
                                                                        checkAltAttack);

    // A pure lookup the interface asks on every redraw, so keyed: the line says which attack a unit
    // or an item resolves to, and that answer only changes when the impl does.
    countCall("attack_get");
    const auto identity = attackFields(result);
    const auto key = fmt::format("{}:{}:{}:{}", idToString(id), attackNumber, int(checkAltAttack),
                                 identity);
    emitKeyed(seq, "attack_get", key,
              fmt::format(R"("ev":"attack_get","impl":"{}","number":{},"alt":{},{})",
                          idToString(id), attackNumber, checkAltAttack, identity));
    return result;
}

game::IBatAttack* __stdcall createBatAttackProbe(const game::IMidgardObjectMap* objectMap,
                                                 const game::BattleMsgData* battleMsgData,
                                                 const game::CMidgardID* unitId,
                                                 const game::CMidgardID* id2,
                                                 int attackNumber,
                                                 const game::LAttackClass* attackClass,
                                                 bool a7)
{
    const auto seq = nextSeq();
    game::IBatAttack* result = hooks::getOriginalFunctions().createBatAttack(objectMap,
                                                                             battleMsgData, unitId,
                                                                             id2, attackNumber,
                                                                             attackClass, a7);

    // Written on every call, not keyed: this is one battle action, and the point of the event is to
    // put a name on the action the engine is about to perform - including the ones that deal no
    // damage and therefore leave no 'dmg' line. A battle is a few dozen of these, not thousands.
    countCall("attack_new");

    // The attack itself is not an argument here - createBatAttack is given the class alone - so it
    // is resolved from id2 (the unit or item the attack belongs to) plus the attack number, the way
    // the engine does it right before calling this (battlemsgdatahooks.cpp:352,
    // bestowwardshooks.cpp:54). checkAltAttack is false on purpose: what comes back is the primary
    // attack, and 'impl'/'number' are journaled as well, so the reader can resolve the alternative
    // one from the data if 'class' below says the engine dispatched on it.
    const auto getAttack = getAttackByIdForProbe();
    const game::IAttack* attack = (getAttack && objectMap && id2)
                                      ? getAttack(objectMap, id2, attackNumber, false)
                                      : nullptr;

    // Which group the attack may target, allies or enemies: the created object knows it, and it
    // costs one virtual call. The object belongs to the caller and is never destroyed here.
    game::CMidgardID targetGroupId{game::emptyId};
    if (result && result->vftable && battleMsgData)
        result->vftable->getTargetGroupId(result, &targetGroupId, battleMsgData);

    // Схема 6.1: attack_hit находит по объекту эту строку - кто бьёт и какой атакой.
    probe::rememberBatAttack(result, seq, idToString(unitId),
                             attack ? idToString(&attack->id) : std::string{"null"});

    emit(seq,
         fmt::format(
             R"("ev":"attack_new","attacker":"{}","impl":"{}","number":{},{},"disp_class":{},"target_group":"{}")",
             idToString(unitId), idToString(id2), attackNumber, attackFields(attack),
             attackClass ? static_cast<int>(attackClass->id) : notComputed,
             idToString(&targetGroupId)));
    return result;
}

/** Terrain of tile (x, y) inside 'block', or -1 when the tile is not in this block at all. */
int tileTerrainAt(const game::CMidgardMapBlock* block, int x, int y)
{
    if (!block)
        return notComputed;

    // A block is 8 tiles along X by 4 along Y; same range check and index arithmetic as
    // ScenarioView::getTile. The range check is what turns 'this block is not the one holding
    // (x, y)' into a refusal instead of a wrong answer: the index alone stays inside [0, 32) for
    // a neighbouring block as well, so without the check a stale block would quietly put another
    // tile's terrain into the journal.
    const auto& blockPosition = block->position;
    if (x < blockPosition.x || x >= blockPosition.x + 8 || y < blockPosition.y
        || y >= blockPosition.y + 4)
        return notComputed;

    const int index = x + 8 * (y - blockPosition.y) - blockPosition.x;
    if (index < 0 || index >= 32)
        return notComputed;

    return static_cast<int>(game::tileTerrain(block->tiles[index]));
}

/**
 * The player's own tiles as runs, row by row: "y:x0-x1,x2;y2:x3". A single tile is written as one
 * number, a span as "from-to"; rows without a single owned tile are left out. On the C4 map this is
 * hundreds of tiles a day, and a plain list of pairs would be an order of magnitude longer for the
 * same content. Writes the tile count to 'ownedCount'.
 */
std::string ownedTilesRle(const game::IMidgardObjectMap* objectMap,
                          const game::CMidgardID* scenarioId,
                          int mapSize,
                          int ownTerrain,
                          int* ownedCount)
{
    std::string cells;
    int owned = 0;

    for (int y = 0; y < mapSize; ++y) {
        const game::CMidgardMapBlock* block = nullptr;
        std::string row;
        int runStart = notComputed;
        int runEnd = notComputed;

        // One extra step past the last column closes a run that reaches the map edge.
        for (int x = 0; x <= mapSize; ++x) {
            if (x < mapSize && (!block || x % 8 == 0))
                block = hooks::getMidgardMapBlock(objectMap, scenarioId, mapSize, x, y);

            if (x < mapSize && tileTerrainAt(block, x, y) == ownTerrain) {
                ++owned;
                if (runStart == notComputed)
                    runStart = x;
                runEnd = x;
                continue;
            }

            if (runStart == notComputed)
                continue;

            row += row.empty() ? "" : ",";
            row += runStart == runEnd ? fmt::format("{}", runStart)
                                      : fmt::format("{}-{}", runStart, runEnd);
            runStart = notComputed;
        }

        if (row.empty())
            continue;

        cells += cells.empty() ? "" : ";";
        cells += fmt::format("{}:{}", y, row);
    }

    *ownedCount = owned;
    return cells;
}

/**
 * Writes 'terrain_day': how much of the map belongs to the player's race on this day, and which
 * tiles exactly. There is no terrain writer in the version table and finding one is a reverse
 * engineering job of its own; computePlayerDailyIncome is called exactly once per player per day
 * (145 calls over the 49-turn C4 run) and already holds the object map, so the map is read from
 * there instead. Owned tiles are counted, never guessed: when the scenario, the player or its race
 * cannot be read, 'owned' is -1 and 'cells' is empty - the counter still says the code ran.
 */
void emitTerrainDay(const game::IMidgardObjectMap* objectMap,
                    const game::CMidgardID* playerId,
                    int turn)
{
    const auto seq = nextSeq();
    const auto* info = hooks::getScenarioInfo(objectMap);
    const auto* player = hooks::getPlayer(objectMap, playerId);
    const auto* raceData = (player && player->raceType) ? player->raceType->data : nullptr;

    countCall("terrain_day");
    if (!info || !raceData || info->mapSize <= 0) {
        emit(seq,
             fmt::format(
                 R"("ev":"terrain_day","player":"{}","turn":{},"race":{},"terrain":{},"size":{},"owned":{},"cells":"")",
                 idToString(playerId), turn, notComputed, notComputed, notComputed, notComputed));
        return;
    }

    // Race to terrain is the engine's own mapping and is kept in one place; the category is a
    // global and is never null, the same way unitutils.cpp dereferences it.
    const int terrain = static_cast<int>(hooks::getTerrainCategory(&raceData->raceType)->id);
    int owned = 0;
    const auto cells = ownedTilesRle(objectMap, &info->id, info->mapSize, terrain, &owned);

    emit(seq,
         fmt::format(
             R"("ev":"terrain_day","player":"{}","turn":{},"race":{},"terrain":{},"size":{},"owned":{},"cells":"{}")",
             idToString(playerId), turn, static_cast<int>(raceData->raceType.id), terrain,
             info->mapSize, owned, cells));
}

game::Bank* __stdcall computePlayerDailyIncomeProbe(game::Bank* income,
                                                    game::IMidgardObjectMap* objectMap,
                                                    const game::CMidgardID* playerId)
{
    const auto seq = nextSeq();
    game::Bank* result = hooks::getOriginalFunctions().computePlayerDailyIncome(income, objectMap,
                                                                                playerId);
    const game::Bank empty{};
    const game::Bank& bank = result ? *result : empty;

    // Schema v3: 'turn' and 'bank' are added, nothing removed or renamed. The daily income alone
    // cannot be checked against a save - the treasury after the day can, and the day number is
    // what joins a journal line to a save file.
    const auto* info = hooks::getScenarioInfo(objectMap);
    const auto* player = hooks::getPlayer(objectMap, playerId);
    const int turn = info ? info->currentTurn : notComputed;
    const game::Bank& purse = player ? player->bank : empty;

    countCall("income");
    emit(seq,
         fmt::format(
             R"("ev":"income","player":"{}","turn":{},"gold":{},"mana":{{"infernal":{},"life":{},"death":{},"runic":{},"grove":{}}},"bank":{{"gold":{},"infernal":{},"life":{},"death":{},"runic":{},"grove":{}}})",
             idToString(playerId), turn, bank.gold, bank.infernalMana, bank.lifeMana, bank.deathMana,
             bank.runicMana, bank.groveMana, purse.gold, purse.infernalMana, purse.lifeMana,
             purse.deathMana, purse.runicMana, purse.groveMana));

    // Schema v4: the growth of the player's land, read off the map from here - see emitTerrainDay.
    // It is not a hook of its own, so it carries no name in 'start'.'hooks'; it is still switchable
    // by name in probe/hooks.txt, because walking the map is the heaviest thing the probe does and
    // a bisection has to be able to take it out without losing the income line.
    if (hookEnabled("terrain_day"))
        emitTerrainDay(objectMap, playerId, turn);

    // Schema v5: the same trick for the player's forts - level, day of the last growth, garrison
    // capacity. It walks the object map too, so it gets its own name in probe/hooks.txt for the
    // same reason; the 'income' line above stays byte for byte what it was. See probeecon.cpp.
    if (hookEnabled("fort_day"))
        emitFortDay(objectMap, playerId, turn);

    // The day changed: dump the unbudgeted counters, so every journal says how often each keyed
    // hook actually ran, not just which combinations it saw. The final dump is at process detach
    // (main.cpp), so a run that never changed the day still gets one.
    static std::atomic<int> lastTurn{notComputed};
    if (turn != notComputed && lastTurn.exchange(turn) != turn) {
        flushMusicRamps(); // Схема 7.3: незаписанное затухание музыки и прокрутка - до счётчиков.
        flushViewRun();
        emitCounts();
    }

    return result;
}

/*
 * Adventure map hooks, corpus schema v3. Addresses are never written here - the version table
 * (game.cpp, third Functions{} block = Gog) is what getProbeHooks() reads - but they are listed in
 * docs/corpus-schema.md, and every one of them was checked against that block before use.
 */

int __stdcall computeMovementCostProbe(const game::CMqPoint* mapPosition,
                                       const game::IMidgardObjectMap* objectMap,
                                       const game::CMidgardMap* midgardMap,
                                       const game::CMidgardPlan* plan,
                                       const game::CMidgardID* stackId,
                                       const char* a6,
                                       const char* a7,
                                       bool leaderAlive,
                                       bool plainsBonus,
                                       bool forestBonus,
                                       bool waterBonus,
                                       bool waterOnly,
                                       bool forbidWaterOnlyOnLand)
{
    const auto seq = nextSeq();
    const int result = hooks::getOriginalFunctions().computeMovementCost(
        mapPosition, objectMap, midgardMap, plan, stackId, a6, a7, leaderAlive, plainsBonus,
        forestBonus, waterBonus, waterOnly, forbidWaterOnlyOnLand);

    // The ground of the tile is not an argument here (the engine reads it from midgardMap), so the
    // key is position + stack + flags + result; the ground is joined back from the .sg on our side.
    // The stack id belongs in the key, not only in the line: without it two stacks whose flags
    // happen to match on one tile collapse into a single line attributed to whichever asked first.
    //
    // ponytail: the discarded line is formatted on every call, not only on a new key (~1 us per
    // call, a few ms per mouse move on a 48x48 map). If a long run ever lags, split emitKeyed()
    // into "count and test the key" plus a lazy emit at the call site.
    const auto key = fmt::format("{}:{}:{}:{}{}{}{}{}{}:{}",
                                 mapPosition ? mapPosition->x : notComputed,
                                 mapPosition ? mapPosition->y : notComputed, idToString(stackId),
                                 int(leaderAlive), int(plainsBonus), int(forestBonus),
                                 int(waterBonus), int(waterOnly), int(forbidWaterOnlyOnLand),
                                 result);
    emitKeyed(seq, "move_cost", key,
              fmt::format(
                  R"("ev":"move_cost","x":{},"y":{},"stack":"{}","leader_alive":{},"plains":{},"forest":{},"water":{},"water_only":{},"forbid_water":{},"ret":{})",
                  mapPosition ? mapPosition->x : notComputed,
                  mapPosition ? mapPosition->y : notComputed, idToString(stackId), leaderAlive,
                  plainsBonus, forestBonus, waterBonus, waterOnly, forbidWaterOnlyOnLand, result));
    return result;
}

bool __stdcall stackCanMoveToPositionProbe(const game::IMidgardObjectMap* objectMap,
                                           const game::CMqPoint* position,
                                           const game::CMidStack* stack,
                                           const game::CMidgardPlan* plan)
{
    const auto seq = nextSeq();
    const bool result = hooks::getOriginalFunctions().stackCanMoveToPosition(objectMap, position,
                                                                             stack, plan);

    // Stack id in the key for the same reason as in move_cost above.
    const auto key = fmt::format("{}:{}:{}:{}", position ? position->x : notComputed,
                                 position ? position->y : notComputed,
                                 stack ? idToString(&stack->id) : "null", int(result));
    emitKeyed(seq, "can_move", key,
              fmt::format(R"("ev":"can_move","x":{},"y":{},"stack":"{}","ret":{})",
                          position ? position->x : notComputed,
                          position ? position->y : notComputed,
                          stack ? idToString(&stack->id) : "null", result));
    return result;
}

void __stdcall showMovementPathProbe(const game::IMidgardObjectMap* objectMap,
                                     const game::CMidgardID* stackId,
                                     game::List<game::CMqPoint>* path,
                                     const game::CMqPoint* lastReachablePoint,
                                     const game::CMqPoint* pathEnd,
                                     bool a6)
{
    const auto seq = nextSeq();
    // Схема 7.6: метки пути (map_path, probemarks.cpp) делят этот адрес - Detours оставляет один
    // перехват на адрес, поэтому флажки считает этот хук (Ин130: до него map_path не писался).
    // Адреса меток (флажки 0x5ae20c, слой 0x4cd329) подтверждены только на Gog.
    static const bool marks = hooks::gameVersion() == hooks::GameVersion::Gog && hookEnabled("map_path");
    const auto original = hooks::getOriginalFunctions().showMovementPath;
    if (marks)
        showPathMarked(reinterpret_cast<ShowPathFn>(original), objectMap, stackId, path,
                       lastReachablePoint, pathEnd, a6);
    else
        original(objectMap, stackId, path, lastReachablePoint, pathEnd, a6);

    // Written on every call on purpose: this is the tile under the cursor, and the map driver
    // calibrates screen->tile from it without moving anything (hovering alone draws the path).
    // One line per cursor move is not a flood. 'len' is List<T>::length (d2list.h), not a walk of
    // the node chain.
    countCall("path");
    emit(seq,
         fmt::format(
             R"("ev":"path","stack":"{}","len":{},"last_x":{},"last_y":{},"end_x":{},"end_y":{})",
             idToString(stackId), path ? int(path->length) : notComputed,
             lastReachablePoint ? lastReachablePoint->x : notComputed,
             lastReachablePoint ? lastReachablePoint->y : notComputed,
             pathEnd ? pathEnd->x : notComputed, pathEnd ? pathEnd->y : notComputed));
}

/**
 * CMidStackApi::get().setPosition is __thiscall, so the wrapper is __fastcall with a dummy %edx.
 * 'move_left' is CMidStack::movement AFTER the move: the movement points the stack has left, which
 * is what the cost of the step is read from (previous line minus this one).
 */
bool __fastcall midStackSetPositionProbe(game::CMidStack* thisptr,
                                         int /* %edx */,
                                         const game::IMidgardObjectMap* objectMap,
                                         const game::CMqPoint* position,
                                         bool checkArea)
{
    const auto seq = nextSeq();
    const bool result = hooks::getOriginalFunctions().midStackSetPosition(thisptr, objectMap,
                                                                          position, checkArea);

    countCall("stack_move");
    emit(seq,
         fmt::format(
             R"("ev":"stack_move","stack":"{}","x":{},"y":{},"check_area":{},"move_left":{},"ret":{})",
             thisptr ? idToString(&thisptr->id) : "null", position ? position->x : notComputed,
             position ? position->y : notComputed, checkArea,
             thisptr ? int(thisptr->movement) : notComputed, result));
    return result;
}

bool __stdcall changeUnitXpCheckUpgradeProbe(game::IMidgardObjectMap* objectMap,
                                             const game::CMidgardID* playerId,
                                             const game::CMidgardID* unitId,
                                             int amount)
{
    const auto seq = nextSeq();
    // Схема 6.0: у 0x5d7412 два вызова в .text - чит сложности ИИ AiCheatUnitXP (возврат
    // 0x431d07) и бой, CBatAttackGroupUpgrade::upgradeGroup (0x665788).
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    // Схема 6.0, партия 2: состояние юнита до начисления - уровень, XP_NEXT и тип до возможного
    // повышения, опыт до и после (addXp 0x5e9a7b зажимает в [0, XP_NEXT]).
    const auto before = xpUnitState(objectMap, unitId);
    const int maxLeader = leaderMaxLevel(objectMap);

    const bool result = hooks::getOriginalFunctions().changeUnitXpCheckUpgrade(objectMap, playerId,
                                                                               unitId, amount);

    const auto* unitAfter = readableUnit(objectMap, unitId);
    countCall("xp");
    emit(seq,
         fmt::format(
             R"("ev":"xp","player":"{}","unit":"{}","amount":{},"xp_before":{},"xp_after":{},"hp":{},"level":{},"xp_next":{},"impl":"{}","leader":{},"max_leader":{},"ret_addr":"{}","ret":{})",
             idToString(playerId), idToString(unitId), amount, before.xp,
             unitAfter ? unitAfter->currentXp : notComputed, before.hp, before.level,
             before.xpNext, before.impl, before.leader, maxLeader, hexAddress(returnAddress),
             result));
    return result;
}

game::BuildingStatus __stdcall getBuildingStatusProbe(const game::IMidgardObjectMap* objectMap,
                                                      const game::CMidgardID* playerId,
                                                      const game::CMidgardID* buildingId,
                                                      bool ignoreBuildTurnAndCost)
{
    const auto seq = nextSeq();
    const auto result = hooks::getOriginalFunctions().getBuildingStatus(objectMap, playerId,
                                                                        buildingId,
                                                                        ignoreBuildTurnAndCost);

    // The build dialog asks about every building of the capital on every redraw; the answer only
    // changes when the treasury or the buildings do, hence the key.
    const auto key = fmt::format("{}:{}:{}:{}", idToString(playerId), idToString(buildingId),
                                 int(ignoreBuildTurnAndCost), int(result));
    emitKeyed(seq, "build_status", key,
              fmt::format(
                  R"("ev":"build_status","player":"{}","building":"{}","ignore_cost":{},"ret":{})",
                  idToString(playerId), idToString(buildingId), ignoreBuildTurnAndCost,
                  int(result)));
    return result;
}

bool __stdcall addPlayerUnitsToHireListProbe(game::CMidDataCache2* dataCache,
                                             const game::CMidgardID* playerId,
                                             const game::CMidgardID* a3,
                                             game::IdList* hireList)
{
    const auto seq = nextSeq();
    const bool result = hooks::getOriginalFunctions().addPlayerUnitsToHireList(dataCache, playerId,
                                                                               a3, hireList);

    // The key carries a3 (the object the list is asked for - toolset's own name, meaning unknown)
    // and the answer, not the player alone: one line per player would print once for the whole run
    // and hide every other fort and every change of the list.
    const auto count = hireList ? int(hireList->length) : notComputed;
    const auto key = fmt::format("{}:{}:{}:{}", idToString(playerId), idToString(a3), count,
                                 int(result));
    emitKeyed(seq, "hire_list", key,
              fmt::format(R"("ev":"hire_list","player":"{}","a3":"{}","count":{},"ret":{})",
                          idToString(playerId), idToString(a3), count, result));
    return result;
}

void __stdcall updateEncLayoutSiteProbe(const game::CMidSite* site, game::CTextBoxInterf* textBox)
{
    const auto seq = nextSeq();
    hooks::getOriginalFunctions().updateEncLayoutSite(site, textBox);

    // Says which map site the player is looking at (merchant / mage tower / mercenaries / trainer,
    // LSiteCategory ids in sitecategories.h). The layout is updated on every redraw, so it is keyed
    // by the site id.
    const auto key = site ? idToString(&site->id) : std::string{"null"};
    emitKeyed(seq, "site", key,
              fmt::format(R"("ev":"site","site":"{}","category":{})", key,
                          site ? int(site->siteCategory.id) : notComputed));
}

void __fastcall beforeBattleRoundProbe(game::BattleMsgData* thisptr, int /* %edx */)
{
    // battle_start describes the state the battle starts from, so it is taken before the
    // original call, same as battle_turn/battle_units below. beforeBattleRound runs twice for
    // round 1 (see README); firstTimeSeen() makes sure only the first of those two writes it.
    if (thisptr && firstTimeSeen(currentBattle, thisptr)) {
        emit(nextSeq(),
             fmt::format(
                 R"("ev":"battle_start","attacker_group":"{}","defender_group":"{}","attacker_player":"{}","defender_player":"{}","round":{},{}{})",
                 idToString(&thisptr->attackerGroupId), idToString(&thisptr->defenderGroupId),
                 idToString(&thisptr->attackerPlayerId), idToString(&thisptr->defenderPlayerId),
                 currentRound(thisptr), unitsField(thisptr, nullptr), rngIndexField()));
        setBattleActive(true);
    }

    const auto seq = nextSeq();
    hooks::getOriginalFunctions().beforeBattleRound(thisptr);

    // After the original, so that whatever the call fills in is already there. In the corpus of
    // 06.09 "order" came out empty in every round: turnsOrder is not built by this call. The
    // field is written from turn_before/turn_after/battle_units as well - see orderField().
    emit(seq, fmt::format(R"("ev":"round","round":{},{},{}{})", currentRound(thisptr),
                          orderField(thisptr), unitsField(thisptr, nullptr), rngIndexField()));
}

void __stdcall beforeBattleTurnProbe(const game::BattleMsgData* battleMsgData,
                                     const game::IMidgardObjectMap* objectMap,
                                     const game::CMidgardID* unitId)
{
    const auto seq = nextSeq();
    hooks::getOriginalFunctions().beforeBattleTurn(battleMsgData, objectMap, unitId);

    emit(seq, fmt::format(R"("ev":"turn_before","unit":"{}","round":{},{},{})", idToString(unitId),
                          currentRound(battleMsgData), orderField(battleMsgData),
                          unitsField(battleMsgData, nullptr)));
}

void __stdcall afterBattleTurnProbe(const game::BattleMsgData* battleMsgData,
                                    const game::CMidgardID* unitId,
                                    const game::CMidgardID* nextUnitId)
{
    const auto seq = nextSeq();
    hooks::getOriginalFunctions().afterBattleTurn(battleMsgData, unitId, nextUnitId);

    emit(seq, fmt::format(R"("ev":"turn_after","unit":"{}","next":"{}","round":{},{},{})",
                          idToString(unitId), idToString(nextUnitId), currentRound(battleMsgData),
                          orderField(battleMsgData), unitsField(battleMsgData, nullptr)));
}

void* __fastcall battleTurnProbe(game::CBatLogic* thisptr,
                                 int /* %edx */,
                                 game::CResultSender* resultSender,
                                 int groupBattleCondition,
                                 game::CMidgardID* a4,
                                 game::CMidgardID* a5)
{
    game::BattleMsgData* battleMsgData = thisptr ? thisptr->battleMsgData : nullptr;
    if (!battleMsgData) {
        // Nothing to snapshot, and a line with -1 in every field would be indistinguishable from
        // an honest one.
        return hooks::getOriginalFunctions().battleTurn(thisptr, resultSender, groupBattleCondition,
                                                        a4, a5);
    }

    const auto seq = nextSeq();

    // Unit identity (type/level/hp_max) needs objectMap, which only CBatLogic has - BattleMsgData
    // alone (beforeBattleRound, where battle_start is now written) does not. Written once per
    // battle, at the first battleTurn call, same point battle_start used to fire at.
    if (firstTimeSeen(unitsAnnounced, battleMsgData)) {
        emit(seq, fmt::format(R"("ev":"battle_units",{},{})", orderField(battleMsgData),
                              battleUnitsField(battleMsgData, thisptr->objectMap)));
    } else {
        emit(seq, fmt::format(R"("ev":"battle_turn","round":{})", currentRound(battleMsgData)));
    }

    return hooks::getOriginalFunctions().battleTurn(thisptr, resultSender, groupBattleCondition, a4,
                                                    a5);
}

void* __fastcall updateGroupsIfBattleIsOverProbe(game::CBatLogic* thisptr,
                                                 int /* %edx */,
                                                 game::CResultSender* resultSender)
{
    void* result = hooks::getOriginalFunctions().updateGroupsIfBattleIsOver(thisptr, resultSender);

    // The only hook that takes its seq after the original: whether there is a line at all is
    // decided by the state the original leaves behind, and a seq taken for nothing would look
    // like a lost line to the journal check.
    const auto& api = game::CBatLogicApi::get();
    game::BattleMsgData* battleMsgData = thisptr ? thisptr->battleMsgData : nullptr;
    if (battleMsgData && api.isBattleOver(thisptr) && firstTimeSeen(endedBattle, battleMsgData)) {
        game::CMidgardID winnerGroupId{};
        api.getBattleWinnerGroupId(thisptr, &winnerGroupId);

        emit(nextSeq(), fmt::format(R"("ev":"battle_end","winner":"{}","round":{},{}{})",
                                    idToString(&winnerGroupId), currentRound(battleMsgData),
                                    unitsField(battleMsgData, nullptr), rngIndexField()));

        // rng/rng_std lines are only journaled between battle_start and battle_end (inclusive);
        // this is the "inclusive" end of that window - see setBattleActive().
        setBattleActive(false);
    }

    return result;
}

/**
 * The engine reuses BattleMsgData memory, so a construction over a remembered battle is what marks
 * the end of it for the probe. Nothing is journaled here: the interesting lines are battle_start
 * and battle_end, and constructors also run on copies and on the client.
 */
game::BattleMsgData* __fastcall battleMsgDataCtorProbe(game::BattleMsgData* thisptr,
                                                       int /* %edx */)
{
    forgetBattle(currentBattle, thisptr);
    forgetBattle(endedBattle, thisptr);
    forgetBattle(announcedBattle, thisptr);
    forgetBattle(unitsAnnounced, thisptr);

    return hooks::getOriginalFunctions().battleMsgDataCtor(thisptr);
}

/**
 * Auto and fast battle are forced by answering their readers, not by writing the flags into
 * BattleMsgData: the structure travels over the network and a copy would wipe the byte back.
 * These two are the only probe hooks that do not call the original - the answer is the point.
 */
bool __fastcall isAutoBattleProbe(const game::BattleMsgData* thisptr, int /* %edx */)
{
    if (!autoBattleForced())
        return hooks::getOriginalFunctions().isAutoBattle(thisptr);

    if (firstTimeSeen(announcedBattle, thisptr))
        emit(nextSeq(), R"("ev":"autobattle_forced")");

    return true;
}

bool __fastcall isFastBattleProbe(const game::BattleMsgData* thisptr, int /* %edx */)
{
    if (!autoBattleForced())
        return hooks::getOriginalFunctions().isFastBattle(thisptr);

    if (firstTimeSeen(announcedBattle, thisptr))
        emit(nextSeq(), R"("ev":"autobattle_forced")");

    return true;
}

/**
 * Experimental bisection hook for the intro-video -> main-menu crash described at
 * menuCompatHooks below: calls transitionToMain instead of the original
 * transitionToMainOrCloseGame, so whatever branch of ...OrCloseGame decides to close the game is
 * never reached. Does not call the original - the whole point is to skip it. Off by default;
 * enable with 'menu_force_main' in probe/hooks.txt. Do not enable together with 'menu_transition'
 * - both target the same function.
 */
void __fastcall menuForceMainProbe(game::CMenuPhase* thisptr,
                                   int /* %edx */,
                                   bool showIntroTransition)
{
    const auto seq = nextSeq();
    game::CMenuPhaseApi::get().transitionToMain(thisptr, showIntroTransition);

    emit(seq, fmt::format(R"("ev":"menu_force_main","show_intro_transition":{})",
                          showIntroTransition));
}

} // namespace

} // namespace probe

namespace hooks {

namespace {

/**
 * One entry of the probe hook table: the short name probe/hooks.txt whitelists it by (see
 * README "Режим зонда"), plus the usual target/hook/original triple.
 */
struct NamedHook
{
    const char* name;
    HookInfo info;
};

/** Keeps 'hook' (and its name, for the 'start' line) when its target exists and passes the probe/hooks.txt whitelist. */
void keepIfEnabled(const NamedHook& hook, Hooks& hooks, std::vector<std::string>& installed)
{
    if (!hook.info.target || !probe::hookEnabled(hook.name))
        return;

    hooks.push_back(hook.info);
    installed.push_back(hook.name);
}

/**
 * Splices the probe/hooks.txt 'toolset:A-B' / 'toolset:all' slice of getGameHooks() into 'hooks'.
 * A toolset hook whose target already belongs to one of the probe hooks above is skipped (the
 * probe hook wins) and its index recorded in 'skipped'. getGameHooks()'s doc comment (hooks.h)
 * says it must not be called more than once per run - this is that one call. Returns the number
 * of toolset hooks actually added.
 */
std::size_t addToolsetRangeHooks(const ToolsetDirectives& toolset,
                                 Hooks& hooks,
                                 std::vector<std::size_t>& skipped)
{
    if (!toolset.rangeRequested)
        return 0;

    const Hooks gameHooks{getGameHooks()};
    const std::size_t from = toolset.rangeAll ? 0 : toolset.rangeFrom;
    const std::size_t to = toolset.rangeAll ? (gameHooks.empty() ? 0 : gameHooks.size() - 1)
                                            : toolset.rangeTo;

    std::size_t added = 0;
    for (std::size_t i = from; i < gameHooks.size() && i <= to; ++i) {
        const auto& candidate = gameHooks[i];
        if (!candidate.target)
            continue;

        const bool conflict = std::any_of(hooks.begin(), hooks.end(), [&](const HookInfo& hook) {
            return hook.target == candidate.target;
        });
        if (conflict) {
            skipped.push_back(i);
            continue;
        }

        hooks.push_back(candidate);
        ++added;
    }

    return added;
}

} // namespace

Hooks getProbeHooks()
{
    auto& fn = game::gameFunctions();
    auto& orig = getOriginalFunctions();

    // clang-format off
    const NamedHook all[] = {
        {"rng", {fn.generateRandomNumber, probe::generateRandomNumberProbe, (void**)&orig.generateRandomNumber}},
        {"rng_std", {fn.generateRandomNumberStd, probe::generateRandomNumberStdProbe, (void**)&orig.generateRandomNumberStd}},
        {"miss", {fn.attackShouldMiss, probe::attackShouldMissProbe, (void**)&orig.attackShouldMiss}},
        {"dmg", {fn.computeDamage, probe::computeDamageProbe, (void**)&orig.computeDamage}},
        {"dmg_max", {fn.computeDamageMax, probe::computeDamageMaxProbe, (void**)&orig.computeDamageMax}},
        {"dmg_buffs", {fn.computeDamageWithBuffs, probe::computeDamageWithBuffsProbe, (void**)&orig.computeDamageWithBuffs}},
        {"armor", {fn.computeArmor, probe::computeArmorProbe, (void**)&orig.computeArmor}},
        {"power", {fn.getAttackPower, probe::getAttackPowerProbe, (void**)&orig.getAttackPower}},
        {"income", {fn.computePlayerDailyIncome, probe::computePlayerDailyIncomeProbe, (void**)&orig.computePlayerDailyIncome}},
        // Adventure map and economy, corpus schema v3. Addresses come from the version table, not
        // from here; the Gog values are listed in docs/corpus-schema.md.
        {"move_cost", {fn.computeMovementCost, probe::computeMovementCostProbe, (void**)&orig.computeMovementCost}},
        {"can_move", {fn.stackCanMoveToPosition, probe::stackCanMoveToPositionProbe, (void**)&orig.stackCanMoveToPosition}},
        {"path", {fn.showMovementPath, probe::showMovementPathProbe, (void**)&orig.showMovementPath}},
        {"stack_move", {game::CMidStackApi::get().setPosition, probe::midStackSetPositionProbe, (void**)&orig.midStackSetPosition}},
        {"xp", {fn.changeUnitXpCheckUpgrade, probe::changeUnitXpCheckUpgradeProbe, (void**)&orig.changeUnitXpCheckUpgrade}},
        {"build_status", {fn.getBuildingStatus, probe::getBuildingStatusProbe, (void**)&orig.getBuildingStatus}},
        {"hire_list", {fn.addPlayerUnitsToHireList, probe::addPlayerUnitsToHireListProbe, (void**)&orig.addPlayerUnitsToHireList}},
        {"site", {fn.updateEncLayoutSite, probe::updateEncLayoutSiteProbe, (void**)&orig.updateEncLayoutSite}},
        // Magic, items and modifiers, corpus schema v4. All three are null in the scenario editor
        // block of the version table, so keepIfEnabled() drops them there on its own.
        {"immune", {fn.isUnitImmuneToAttack, probe::isUnitImmuneToAttackProbe, (void**)&orig.isUnitImmuneToAttack}},
        {"attack_new", {fn.createBatAttack, probe::createBatAttackProbe, (void**)&orig.createBatAttack}},
        {"attack_get", {fn.getAttackById, probe::getAttackByIdProbe, (void**)&orig.getAttackById}},
    };
    // clang-format on

    Hooks hooks;
    std::vector<std::string> installed;
    for (const auto& hook : all) {
        // The scenario editor leaves most of these addresses null (game.cpp), and DetourAttach on
        // a null target fails the whole transaction in DllMain.
        keepIfEnabled(hook, hooks, installed);
    }

    // The battle tables hold three versions of the game and no entry for the scenario editor
    // (battlemsgdata.cpp, batlogic.cpp), so they must not even be read there. getGameHooks() is
    // "used only in game" too (hooks.cpp), so the 'toolset:A-B' / 'toolset:all' splice below does
    // not apply here either - only toolset_vftable/toolset_restrictions can be non-default.
    if (!executableIsGame()) {
        probe::emitStart(installed, 0, {});
        return hooks;
    }

    // Experimental group: the toolset's own CMenuPhase hooks, reused unchanged. They were added
    // when the intro-video -> main-menu transition closed the game with the probe installed, but
    // the bisection of 05.09 found the real cause elsewhere - the modded Globals in game-dev
    // (install.py --vanilla-globals); a clean engine walks into the menu without any of these.
    // So they default OFF like menu_force_main: only an explicit name in probe/hooks.txt puts
    // one in, and only for experiments. The corpus is taken without them.
    const NamedHook menuExperimentHooks[] = {
        {"menu_ctor", {game::CMenuPhaseApi::get().constructor, menuPhaseCtorHooked, (void**)&orig.menuPhaseCtor}},
        {"menu_dtor", {game::CMenuPhaseApi::vftable()->destructor, menuPhaseDtorHooked, (void**)&orig.menuPhaseDtor}},
        // No original slot: the toolset itself replaces this one outright instead of wrapping it.
        {"menu_switch_phase", {game::CMenuPhaseApi::get().switchPhase, menuPhaseSwitchPhaseHooked}},
        {"menu_transition", {game::CMenuPhaseApi::get().transitionToMainOrCloseGame, menuPhaseTransitionToMainOrCloseGameHooked, (void**)&orig.menuPhaseTransitionToMainOrCloseGame}},
    };

    // Not keepIfEnabled(): that treats a missing hooks.txt as "everything on", and these must
    // stay off unless named - same rule as menu_force_main below.
    for (const auto& hook : menuExperimentHooks) {
        if (!hook.info.target || !probe::hookExplicitlyEnabled(hook.name))
            continue;

        hooks.push_back(hook.info);
        installed.push_back(hook.name);
    }

    // Experimental, and not run through keepIfEnabled() for the same reason as the group above:
    // hookEnabled() treats a missing hooks.txt as "everything on", but this one must default
    // OFF - it is a bisection experiment, not a probe hook. Same target as
    // "menu_transition" (do not enable both - Detours would attach twice to one address), but
    // calls transitionToMain instead of ever calling the original - see probe::menuForceMainProbe.
    if (game::CMenuPhaseApi::get().transitionToMainOrCloseGame &&
        probe::hookExplicitlyEnabled("menu_force_main")) {
        hooks.push_back(
            {game::CMenuPhaseApi::get().transitionToMainOrCloseGame, probe::menuForceMainProbe});
        installed.push_back("menu_force_main");
    }

    auto& battle = game::BattleMsgDataApi::get();
    auto& batLogic = game::CBatLogicApi::get();

    // clang-format off
    const NamedHook battleHooks[] = {
        {"round", {battle.beforeBattleRound, probe::beforeBattleRoundProbe, (void**)&orig.beforeBattleRound}},
        {"turn_before", {battle.beforeBattleTurn, probe::beforeBattleTurnProbe, (void**)&orig.beforeBattleTurn}},
        {"turn_after", {battle.afterBattleTurn, probe::afterBattleTurnProbe, (void**)&orig.afterBattleTurn}},
        {"battle_turn", {batLogic.battleTurn, probe::battleTurnProbe, (void**)&orig.battleTurn}},
        {"battle_end", {batLogic.updateGroupsIfBattleIsOver, probe::updateGroupsIfBattleIsOverProbe, (void**)&orig.updateGroupsIfBattleIsOver}},
        {"bmd_ctor", {battle.constructor, probe::battleMsgDataCtorProbe, (void**)&orig.battleMsgDataCtor}},
        // Without the marker these two are not hooked at all: nothing to force, nothing to log.
        {"auto_battle", {probe::autoBattleForced() ? battle.isAutoBattle : nullptr, probe::isAutoBattleProbe, (void**)&orig.isAutoBattle}},
        {"fast_battle", {probe::autoBattleForced() ? battle.isFastBattle : nullptr, probe::isFastBattleProbe, (void**)&orig.isFastBattle}},
    };
    // clang-format on

    for (const auto& hook : battleHooks) {
        keepIfEnabled(hook, hooks, installed);
    }

    // Corpus schema v5. Both live in files of their own: the economy hooks because they carry
    // their own address table (probeecon.cpp explains why it is not in game.cpp), the input hooks
    // because they target user32 exports and not the engine at all.
    addEconHooks(hooks, installed);
    addInputHooks(hooks, installed);
    // Схема 5.2: мутатор отношений, свой файл probediplo.cpp по той же причине, что и v5.
    addDiploHooks(hooks, installed);
    // Схема 5.3: причины трат казны, свой файл probespend.cpp с адресами по той же причине.
    addSpendHooks(hooks, installed);
    // Схема 6.0, партия 1: свои файлы с адресами Gog по той же причине - probemap.cpp,
    // probecheck.cpp, probenoble.cpp.
    addMapHooks(hooks, installed);
    addCheckHooks(hooks, installed);
    addNobleHooks(hooks, installed);
    // Схема 6.0, партия 2: лидеры, предметы, бой - probeleader.cpp (проверки - в addCheckHooks).
    addLeaderHooks(hooks, installed);
    // Схема 6.1: цель атак без урона - probehit.cpp.
    addHitHooks(hooks, installed);
    // Схема 6.2: пересев генератора и запас хода - probestate.cpp.
    addStateHooks(hooks, installed);
    // Схема 6.3: перенос юнитов и отказы интерфейса - probeunit.cpp.
    addUnitHooks(hooks, installed);
    // Схема 6.4: ход ИИ на карте - probeai.cpp.
    addAiHooks(hooks, installed);
    // Схема 6.5: экономика и тактика ИИ - probeaieco.cpp, probeaitac.cpp.
    addAiEcoHooks(hooks, installed);
    addAiTacticHooks(hooks, installed);
    // Схема 7.0: экраны, нажатия, тексты, окна сообщений, вход в партию - probeui.cpp.
    addUiHooks(hooks, installed);
    // Схема 7.1: снимок экрана F12 - probeshot.cpp.
    addShotHooks(hooks, installed);
    // Схема 7.1, часть 3: щелчок по виду карты - probeclick.cpp (rng_state едет на game_start).
    addClickHooks(hooks, installed);
    // Схема 7.2: кэш целей тактики, сила по юнитам, лечение, повышение, A*, выдача id - probeai72.cpp.
    addAi72Hooks(hooks, installed);
    // Схема 7.3: звук, музыка, ролики - probeav.cpp (Miles и Bink - ячейками импорта exe).
    addAvHooks(hooks, installed);

    std::vector<std::size_t> toolsetSkipped;
    const std::size_t toolsetCount = addToolsetRangeHooks(probe::toolsetDirectives(), hooks,
                                                           toolsetSkipped);

    probe::emitStart(installed, toolsetCount, toolsetSkipped);
    return hooks;
}

} // namespace hooks
