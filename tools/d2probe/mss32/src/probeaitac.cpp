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

#include "midgardid.h"
#include "midgardobjectmap.h"
#include "mqpoint.h"
#include "probe.h"
#include "probeaiutil.h"
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>

/*
 * Схема корпуса 6.5, часть 2: тактика ИИ на карте (CAiTactic).
 *
 * Разбор - disciples-modding/docs/synthetic-maps/analysis/N-re65-tactics.md. Адреса - Gog 3.01,
 * сверены capstone. Каждый вызов - строка, счётчики counts.calls без бюджета.
 *
 * Действие тактики (IAiTacticAction): слот 1 тип, 2 id отряда, 3 id цели, 4 CMqPoint*; все - геттеры,
 * зонд зовёт их только по vftable, чей слот лежит в коде exe.
 * Список кандидатов L = [CAiTactic+0x51b8]: +0 карта, +4 память, +8 List (длина +8, голова +0xc);
 * узел: +8 действие, +0x10 double счёт. Фаза шага - CAiTactic+0x51bc, отряд - IAiStack*
 * CAiTactic+0x5170 (слот 1 - &id отряда, слот 2 - CMidStack*, слот 13 - расстояние Чебышёва).
 *
 * - 0x4686cf вставка, __thiscall(L, action*, double), ret 0xc - ai_tactic_candidate. Действие,
 *   отброшенное вставкой, движок тут же удаляет: поля читаются ДО оригинала.
 * - 0x466d5c поправка приоритета редактора, __stdcall(CMidStack*, map, elem*), ret 0xc; 0x466cd8
 *   поправка +100 по множеству, __stdcall(set*, elem*), ret 8 - ai_tactic_adjust. elem: +0 действие,
 *   +8 double счёт. Приоритет - IAiPriority (+4) объекта цели, как в самой поправке. Первый аргумент -
 *   ответ слота 2 IAiStack (0x466884: mov ecx,[esi+0x5170]; call [eax+8]), то есть CMidStack*, а не
 *   объект сценария с id по +4: сам движок читает по нему приказ 0x466e73 (sub eax,[ecx+0xac] -
 *   CMidStack::orderTargetId). Id отряда лежит по +0x18 (CMidStack::id), по +4 - CMqPoint позиции.
 * - 0x466388 шаг, __thiscall(CAiTactic*), ret 0, bool - ai_tactic_pick; ai_tactic_stack - на смене
 *   фазы 0 -> 1 (выбран следующий отряд).
 * - 0x461fb2 память результата, __thiscall(memo, id отряда, id цели, CMqPoint*, int* день, статус),
 *   ret 0x14 - ai_tactic_result.
 * - 0x46164d клетка жезла, __stdcall(CMqPoint* out, map, plan, player*, IAiStack*, CMidCrystal*),
 *   ret 0x18, bool - ai_rod_cell: после оригинала зонд повторяет проверку восьми соседей (0x5d9bdb,
 *   0x5d9d1d, 0x5cd11f - те же аргументы, то же короткое замыкание) и расстояние (слот 13).
 * - 0x5dda25 сила, double __stdcall(p1, p2, double A, p4, double B, double C), ret 0x24 -
 *   ai_strength. Аргументы-указатели пересверены по 0x5dcd53(p1, p4, p2) и вызову 0x5ddb2c: p1 -
 *   IMidgardObjectMap* (0x5dcdc2: findScenarioObjectById по нему, слот 0x14), p2 и p4 - IdVector
 *   идентификаторов юнитов (0x5f4441: ([+8] - [+4]) >> 2 - длина). Это НЕ id: пишутся сырые
 *   указатели и длины векторов (p2_n, p4_n).
 */

namespace probe {

namespace {

using namespace game;
using namespace aiutil;

constexpr std::size_t topCap = 4;

/** {"ptr","vft","type","stack","target","point"} действия тактики - слоты 1-4. */
std::string tacticActionJson(const void* action)
{
    if (!action)
        return {"null"};

    bool ok = false;
    const int type = vget<int>(action, 1, ok);
    const std::string typeJson = ok ? std::to_string(type) : std::string{"null"};
    const auto* stack = vget<const CMidgardID*>(action, 2, ok);
    const auto stackJson = ok ? jsonId(stack) : std::string{"null"};
    const auto* target = vget<const CMidgardID*>(action, 3, ok);
    const auto targetJson = ok ? jsonId(target) : std::string{"null"};
    const auto* point = vget<const CMqPoint*>(action, 4, ok);
    const auto pointText = ok ? pointJson(point) : std::string{"null"};

    return fmt::format(R"({{"ptr":{},"vft":{},"type":{},"stack":{},"target":{},"point":{}}})",
                       hexOf(reinterpret_cast<std::uintptr_t>(action)),
                       hexOf(vftableOf(action)), typeJson, stackJson, targetJson, pointText);
}

/** Узлы списка L (действие +8, счёт +0x10), не больше cap. */
std::string candidatesJson(const void* candidates, std::size_t cap)
{
    std::string out;
    const auto* body = candidates ? static_cast<const char*>(candidates) + 8 : nullptr;
    walkList(body, cap, [&out](const void* node) {
        const void* action = nullptr;
        readAt(node, 8, action);
        out += fmt::format(R"({}{{"action":{},"score":{}}})", out.empty() ? "" : ",",
                           tacticActionJson(action), doubleAt(node, 0x10));
        return true;
    });
    return '[' + out + ']';
}

/* ---------------------------------------------------------------------------------------------
 * ai_tactic_candidate - 0x4686cf
 */

using TacticInsert = void(__fastcall*)(void* thisptr, int /* %edx */, void* action, double score);
TacticInsert origTacticInsert{};

void __fastcall tacticInsertProbe(void* thisptr, int /* %edx */, void* action, double score)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto* body = static_cast<const char*>(thisptr) + 8;
    int before = -1;
    const bool beforeKnown = readAt(body, 0, before);
    const auto actionJson = tacticActionJson(action);

    origTacticInsert(thisptr, 0, action, score);

    int after = -1;
    const bool afterKnown = readAt(body, 0, after);
    const bool inserted = beforeKnown && afterKnown && after > before;

    // Вставленный узел - хвост: head->prev, его действие обязано совпасть с аргументом.
    std::string finalScore{"null"};
    const void* head = nullptr;
    const void* tail = nullptr;
    const void* tailAction = nullptr;
    if (inserted && readAt(body, 4, head) && readAt(head, 4, tail) && readAt(tail, 8, tailAction)
        && tailAction == action)
        finalScore = doubleAt(tail, 0x10);

    countCall("ai_tactic_candidate");
    emit(seq,
         fmt::format(
             R"("ev":"ai_tactic_candidate","player":{},"action":{},"score_in":{},"idx":{},"inserted":{},"score":{},"ret_addr":"{}")",
             aiTurnPlayer(), actionJson, jsonDouble(true, score),
             beforeKnown ? std::to_string(before) : "null",
             beforeKnown && afterKnown ? (inserted ? "true" : "false") : "null", finalScore,
             hexAddress(returnAddress)));
}

/* ---------------------------------------------------------------------------------------------
 * ai_tactic_adjust - 0x466d5c, 0x466cd8
 */

using DynamicCast = void*(__cdecl*)(const void* object, int vfDelta, const void* source,
                                    const void* target, int isReference);
const auto rtDynamicCast = reinterpret_cast<DynamicCast>(0x66bda6);
constexpr std::uintptr_t rttiScenarioObject = 0x78cd50; // IMidScenarioObject
constexpr std::uintptr_t rttiAiPriority = 0x798b08;     // IAiPriority

/** Приоритет редактора цели - как 0x466d8c: findScenarioObjectById, приведение, +4. */
std::string priorityOf(const IMidgardObjectMap* objectMap, const CMidgardID* target)
{
    CMidgardID id{};
    if (!readableMap(objectMap) || !readAt(target, 0, id))
        return {"null"};

    const auto* object = objectMap->vftable->findScenarioObjectById(objectMap, &id);
    if (!object)
        return {"null"};

    const auto* priority = rtDynamicCast(object, 0, reinterpret_cast<const void*>(rttiScenarioObject),
                                         reinterpret_cast<const void*>(rttiAiPriority), 0);
    return priority ? intAt(priority, 4) : std::string{"null"};
}

void emitAdjust(std::uint64_t seq,
                const char* kind,
                const std::string& stack,
                const std::string& actionJson,
                const std::string& priority,
                const std::string& before,
                const void* element,
                std::uintptr_t returnAddress)
{
    countCall("ai_tactic_adjust");
    emit(seq,
         fmt::format(
             R"("ev":"ai_tactic_adjust","player":{},"kind":"{}","stack":{},"action":{},"priority":{},"before":{},"after":{},"ret_addr":"{}")",
             aiTurnPlayer(), kind, stack, actionJson, priority, before, doubleAt(element, 8),
             hexAddress(returnAddress)));
}

/** CMidStack::id - +0x18 (IMapElement 20 байт, потом vftable IMidScenarioObject и id). */
constexpr std::size_t midStackIdOffset = 0x18;

const void* elementAction(const void* element)
{
    const void* action = nullptr;
    readAt(element, 0, action);
    return action;
}

using AdjustPriority = void(__stdcall*)(const void* stack, IMidgardObjectMap* objectMap, void* element);
AdjustPriority origAdjustPriority{};

void __stdcall adjustPriorityProbe(const void* stack, IMidgardObjectMap* objectMap, void* element)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto before = doubleAt(element, 8);
    const auto* action = elementAction(element);
    const auto actionJson = tacticActionJson(action);
    bool ok = false;
    const auto* target = vget<const CMidgardID*>(action, 3, ok);
    const auto priority = ok ? priorityOf(objectMap, target) : std::string{"null"};

    origAdjustPriority(stack, objectMap, element);

    emitAdjust(seq, "priority", jsonId(static_cast<const char*>(stack) + midStackIdOffset),
               actionJson, priority, before, element, returnAddress);
}

using AdjustSet = void(__stdcall*)(void* set, void* element);
AdjustSet origAdjustSet{};

void __stdcall adjustSetProbe(void* set, void* element)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto before = doubleAt(element, 8);
    const auto actionJson = tacticActionJson(elementAction(element));

    origAdjustSet(set, element);

    emitAdjust(seq, "set", "null", actionJson, "null", before, element, returnAddress);
}

/* ---------------------------------------------------------------------------------------------
 * ai_tactic_pick, ai_tactic_stack - 0x466388
 */

constexpr std::size_t phaseOffset = 0x51bc;
constexpr std::size_t candidatesOffset = 0x51b8;
constexpr std::size_t stackOffset = 0x5170;
/** CMidStack::insideId - крепость, в которой стоит отряд (0x4637de: StayInside только при непустом). */
constexpr std::size_t midStackInsideOffset = 0x94;

std::string phaseAt(const void* tactic)
{
    return intAt(tactic, phaseOffset);
}

/** Id отряда текущего IAiStack (слот 1 - &id) или null. */
std::string currentStack(const void* tactic)
{
    const void* stack = nullptr;
    if (!readAt(tactic, stackOffset, stack) || !stack)
        return {"null"};

    bool ok = false;
    const auto* id = vget<const CMidgardID*>(stack, 1, ok);
    return ok ? jsonId(id) : std::string{"null"};
}

/** CMidStack* текущего IAiStack (слот 2) или nullptr. */
const void* currentMidStack(const void* tactic)
{
    const void* stack = nullptr;
    if (!readAt(tactic, stackOffset, stack) || !stack)
        return nullptr;

    bool ok = false;
    const auto* midStack = vget<const void*>(stack, 2, ok);
    return ok ? midStack : nullptr;
}

const void* candidatesOf(const void* tactic)
{
    const void* candidates = nullptr;
    readAt(tactic, candidatesOffset, candidates);
    return candidates;
}

std::string candidatesCount(const void* tactic)
{
    const auto* candidates = candidatesOf(tactic);
    return candidates ? listLength(static_cast<const char*>(candidates) + 8) : std::string{"null"};
}

using TacticStep = bool(__fastcall*)(void* thisptr, int /* %edx */);
TacticStep origTacticStep{};

bool __fastcall tacticStepProbe(void* thisptr, int /* %edx */)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    int phaseBefore = -1;
    readAt(thisptr, phaseOffset, phaseBefore);
    const auto stackBefore = currentStack(thisptr);
    const auto rngBefore = rngCount();
    // Фаза 4 исполняет первого кандидата: он записывается до исполнения.
    const auto chosen = phaseBefore == 4 ? candidatesJson(candidatesOf(thisptr), 1)
                                         : std::string{"null"};

    const bool result = origTacticStep(thisptr, 0);

    const auto rngAfter = rngCount();
    int phaseAfter = -1;
    readAt(thisptr, phaseOffset, phaseAfter);
    // Фаза 3 сортирует и обрезает до четырёх.
    const auto top = phaseBefore == 3 ? candidatesJson(candidatesOf(thisptr), topCap)
                                      : std::string{"null"};
    const auto stackAfter = currentStack(thisptr);

    countCall("ai_tactic_pick");
    emit(seq,
         fmt::format(
             R"("ev":"ai_tactic_pick","player":{},"phase_before":{},"phase_after":{},"stack":{},"stack_after":{},"count":{},"top":{},"chosen":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             aiTurnPlayer(), phaseBefore, phaseAfter, stackBefore, stackAfter,
             candidatesCount(thisptr), top, chosen,
             rngBefore, rngAfter, hexAddress(returnAddress), result, nextSeq()));

    if (phaseBefore == 0 && phaseAfter == 1) {
        const auto* midStack = currentMidStack(thisptr);
        countCall("ai_tactic_stack");
        emit(nextSeq(),
             fmt::format(
                 R"("ev":"ai_tactic_stack","player":{},"stack":{},"inside":{},"pos":{},"pick_seq":{})",
                 aiTurnPlayer(), stackAfter,
                 midStack ? jsonId(static_cast<const char*>(midStack) + midStackInsideOffset)
                          : std::string{"null"},
                 pointJson(midStack ? static_cast<const char*>(midStack) + 4 : nullptr), seq));
    }
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_tactic_result - 0x461fb2
 */

using TacticResult = void(__fastcall*)(void* thisptr, int /* %edx */, const CMidgardID* stack,
                                       const CMidgardID* target, const CMqPoint* point,
                                       const int* turn, int status);
TacticResult origTacticResult{};

void __fastcall tacticResultProbe(void* thisptr, int /* %edx */, const CMidgardID* stack,
                                  const CMidgardID* target, const CMqPoint* point,
                                  const int* turn, int status)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    origTacticResult(thisptr, 0, stack, target, point, turn, status);

    countCall("ai_tactic_result");
    emit(seq,
         fmt::format(
             R"("ev":"ai_tactic_result","player":{},"stack":{},"target":{},"point":{},"turn":{},"turn_ptr":{},"status":{},"ret_addr":"{}")",
             aiTurnPlayer(), jsonId(stack), jsonId(target), pointJson(point), intAt(turn, 0),
             hexOf(reinterpret_cast<std::uintptr_t>(turn)), status, hexAddress(returnAddress)));
}

/* ---------------------------------------------------------------------------------------------
 * ai_rod_cell - 0x46164d
 */

constexpr CMqPoint rodNeighbours[] = {
    {-1, -1}, {0, -1}, {1, -1}, {-1, 0}, {1, 0}, {-1, 1}, {0, 1}, {1, 1},
};

using PlaceCheck = int(__stdcall*)(const void* id, const CMqPoint* cell, IMidgardObjectMap* map);
const auto placeCheck = reinterpret_cast<PlaceCheck>(0x5d9bdb);
const auto allyCheck = reinterpret_cast<PlaceCheck>(0x5d9d1d);

using BlockCheck = int(__stdcall*)(IMidgardObjectMap* map, void* plan, void* stackObject,
                                   const CMqPoint* cell, CMqPoint* scratch, int flag);
const auto blockCheck = reinterpret_cast<BlockCheck>(0x5cd11f);

std::string jsonBool(bool known, bool value)
{
    return known ? (value ? "true" : "false") : "null";
}

/** Одна клетка - повтор 0x46167e..0x4616f8: проверки по порядку, дальше первой неудачной не идут. */
std::string rodCellJson(const CMqPoint& cell,
                        IMidgardObjectMap* objectMap,
                        void* plan,
                        void* player,
                        void* stack,
                        const CMqPoint* chosen)
{
    bool ok = false;
    const auto* stackId = vget<const void*>(stack, 1, ok);
    const bool placeKnown = ok;
    const bool place = placeKnown && placeCheck(stackId, &cell, objectMap) == 0;
    const bool allyKnown = place;
    const bool ally = allyKnown && allyCheck(player, &cell, objectMap) == 0;

    bool blockKnown = false;
    bool block = false;
    if (ally) {
        auto* stackObject = vget<void*>(stack, 2, ok);
        blockKnown = ok;
        CMqPoint scratch{-1, -1};
        block = ok && blockCheck(objectMap, plan, stackObject, &cell, &scratch, 0) == 0;
    }

    std::string dist{"null"};
    if (block) {
        const auto function = vslot(stack, 13);
        if (function) {
            using Distance = int(__thiscall*)(const void*, const CMqPoint*);
            dist = std::to_string(reinterpret_cast<Distance>(function)(stack, &cell));
        }
    }

    const bool isChosen = chosen && chosen->x == cell.x && chosen->y == cell.y;
    return fmt::format(
        R"({{"x":{},"y":{},"ok_place":{},"ok_ally":{},"ok_block":{},"dist":{},"chosen":{}}})",
        cell.x, cell.y, jsonBool(placeKnown, place), jsonBool(allyKnown, ally),
        jsonBool(blockKnown, block), dist, isChosen);
}

using RodCell = bool(__stdcall*)(CMqPoint* out, IMidgardObjectMap* objectMap, void* plan,
                                 void* player, void* stack, void* crystal);
RodCell origRodCell{};

bool __stdcall rodCellProbe(CMqPoint* out, IMidgardObjectMap* objectMap, void* plan,
                            void* player, void* stack, void* crystal)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const bool result = origRodCell(out, objectMap, plan, player, stack, crystal);

    CMqPoint chosen{};
    const bool chosenKnown = result && readAt(out, 0, chosen);
    CMqPoint center{};
    const bool centerKnown = readAt(crystal, 0xc, center);

    std::string cells;
    if (centerKnown && readableMap(objectMap)) {
        for (const auto& d : rodNeighbours) {
            const CMqPoint cell{center.x + d.x, center.y + d.y};
            cells += (cells.empty() ? "" : ",")
                     + rodCellJson(cell, objectMap, plan, player, stack,
                                   chosenKnown ? &chosen : nullptr);
        }
    }

    bool ok = false;
    const auto* stackId = vget<const CMidgardID*>(stack, 1, ok);
    countCall("ai_rod_cell");
    emit(seq,
         fmt::format(
             R"("ev":"ai_rod_cell","player":{},"stack":{},"crystal":{},"crystal_pos":{},"result":{},"cells":[{}],"ret_addr":"{}","ret":{})",
             aiTurnPlayer(), ok ? jsonId(stackId) : std::string{"null"}, objectId(crystal),
             centerKnown ? pointJson(&center) : std::string{"null"},
             chosenKnown ? pointJson(&chosen) : std::string{"null"}, cells,
             hexAddress(returnAddress), result));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_strength - 0x5dda25
 */

/** Длина IdVector - ([+8] - [+4]) >> 2, как 0x5f4441; null - не читается или не похоже на вектор. */
std::string idVectorSize(const void* vector)
{
    const char* begin = nullptr;
    const char* end = nullptr;
    if (!readAt(vector, 4, begin) || !readAt(vector, 8, end) || end < begin)
        return {"null"};
    return std::to_string((end - begin) / 4);
}

/** Id из IdVector ([+4], [+8]) как JSON-массив не длиннее maxVectorIds; null - не читается. */
constexpr std::ptrdiff_t maxVectorIds = 16;

std::string idVectorJson(const void* vector)
{
    const char* begin = nullptr;
    const char* end = nullptr;
    if (!readAt(vector, 4, begin) || !readAt(vector, 8, end) || end < begin)
        return {"null"};

    std::string out{"["};
    const auto count = std::min<std::ptrdiff_t>((end - begin) / 4, maxVectorIds);
    for (std::ptrdiff_t i = 0; i < count; ++i) {
        if (i)
            out += ',';
        out += jsonId(begin + i * 4);
    }
    return out + ']';
}

using Strength = double(__stdcall*)(void* p1, void* p2, double a, void* p4, double b, double c);
Strength origStrength{};

double __stdcall strengthProbe(void* p1, void* p2, double a, void* p4, double b, double c)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const double result = origStrength(p1, p2, a, p4, b, c);

    countCall("ai_strength");
    emit(seq,
         fmt::format(
             R"("ev":"ai_strength","player":{},"p1":{},"p2":{},"p4":{},"p2_n":{},"p4_n":{},"p2_units":{},"p4_units":{},"a":{},"b":{},"c":{},"ret_addr":"{}","ret":{})",
             aiTurnPlayer(), hexOf(reinterpret_cast<std::uintptr_t>(p1)),
             hexOf(reinterpret_cast<std::uintptr_t>(p2)),
             hexOf(reinterpret_cast<std::uintptr_t>(p4)), idVectorSize(p2), idVectorSize(p4),
             idVectorJson(p2), idVectorJson(p4),
             jsonDouble(true, a), jsonDouble(true, b), jsonDouble(true, c),
             hexAddress(returnAddress), jsonDouble(true, result)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_hire_score - 0x45a34c
 *
 * Счёт найма в отряд у крепости, double* __stdcall(double* out, map, ctx, IAiStack*, CMidgardID*
 * крепость), ret 0x14 (единственный вызов 0x45a153). После оригинала зонд повторяет запросы самой
 * функции - те же аргументы, без побочных действий: крепость 0x45a325(map, &id), свободные места
 * группы 0x5f4485 (CMidStack+0x30, map), снимаемые из гарнизона 0x45a50e(map, крепость), можно ли
 * нанять 0x45a498(map, свободно, крепость) - при false счёт = NoScore (0x45a441).
 */

using HireScore = double*(__stdcall*)(double* out, IMidgardObjectMap* objectMap, void* context,
                                      void* stack, const CMidgardID* fortId);
HireScore origHireScore{};

using FortById = const void*(__stdcall*)(IMidgardObjectMap* objectMap, const CMidgardID* fortId);
using FreeSlots = int(__thiscall*)(const void* group, IMidgardObjectMap* objectMap);
using GarrisonRemovable = int(__stdcall*)(IMidgardObjectMap* objectMap, const void* fort);
using CanHire = bool(__stdcall*)(IMidgardObjectMap* objectMap, int free, const void* fort);

constexpr std::size_t midStackGroupOffset = 0x30;

double* __stdcall hireScoreProbe(double* out, IMidgardObjectMap* objectMap, void* context,
                                 void* stack, const CMidgardID* fortId)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    double* result = origHireScore(out, objectMap, context, stack, fortId);

    bool haveId = false;
    const auto* stackId = vget<const CMidgardID*>(stack, 1, haveId);
    bool haveMid = false;
    const auto* midStack = vget<const char*>(stack, 2, haveMid);
    const auto* fort = reinterpret_cast<FortById>(0x45a325)(objectMap, fortId);

    std::string free{"null"};
    std::string removable{"null"};
    std::string canHire{"null"};
    if (haveMid && midStack && fort) {
        const int freeSlots = reinterpret_cast<FreeSlots>(0x5f4485)(midStack + midStackGroupOffset,
                                                                    objectMap);
        free = std::to_string(freeSlots);
        removable = std::to_string(reinterpret_cast<GarrisonRemovable>(0x45a50e)(objectMap, fort));
        canHire = reinterpret_cast<CanHire>(0x45a498)(objectMap, freeSlots, fort) ? "true"
                                                                                  : "false";
    }

    countCall("ai_hire_score");
    emit(seq,
         fmt::format(
             R"("ev":"ai_hire_score","player":{},"stack":{},"fort":{},"score":{},"free":{},"removable":{},"can_hire":{},"ret_addr":"{}")",
             aiTurnPlayer(), haveId ? jsonId(stackId) : std::string{"null"}, jsonId(fortId),
             doubleAt(result, 0), free, removable, canHire, hexAddress(returnAddress)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_grid, ai_grid_fort - 0x461c85, 0x461dfc
 *
 * Сетка влияния тактики: 0x461c85 __thiscall(grid, map, CMidgardID* игрок), ret 8 - строит её по
 * крепостям игрока; 0x461dfc __thiscall(grid, CMqPoint* центр, int радиус), ret 8 - метка одной
 * крепости: радиус 13 у столицы, 13 + уровень у деревни (0x461d36..0x461d61). Строки ai_grid_fort
 * лежат между seq и seq_end своей ai_grid.
 */

using Grid = void*(__fastcall*)(void* thisptr, int /* %edx */, IMidgardObjectMap* objectMap,
                                const CMidgardID* player);
Grid origGrid{};

void* __fastcall gridProbe(void* thisptr, int /* %edx */, IMidgardObjectMap* objectMap,
                           const CMidgardID* player)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    void* result = origGrid(thisptr, 0, objectMap, player);

    countCall("ai_grid");
    emit(seq, fmt::format(R"("ev":"ai_grid","player":{},"owner":{},"grid":{},"ret_addr":"{}","seq_end":{})",
                          aiTurnPlayer(), jsonId(player),
                          hexOf(reinterpret_cast<std::uintptr_t>(thisptr)),
                          hexAddress(returnAddress), nextSeq()));
    return result;
}

using GridFort = void*(__fastcall*)(void* thisptr, int /* %edx */, const CMqPoint* center,
                                    int radius);
GridFort origGridFort{};

void* __fastcall gridFortProbe(void* thisptr, int /* %edx */, const CMqPoint* center, int radius)
{
    const auto seq = nextSeq();
    const auto point = pointJson(center);
    void* result = origGridFort(thisptr, 0, center, radius);

    countCall("ai_grid_fort");
    emit(seq, fmt::format(R"("ev":"ai_grid_fort","player":{},"grid":{},"center":{},"radius":{})",
                          aiTurnPlayer(), hexOf(reinterpret_cast<std::uintptr_t>(thisptr)), point,
                          radius));
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addAiTacticHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"ai_tactic_candidate",  0x4686cf, (void*)tacticInsertProbe,   (void**)&origTacticInsert},
        {"ai_tactic_adjust",     0x466d5c, (void*)adjustPriorityProbe, (void**)&origAdjustPriority},
        {"ai_tactic_adjust_set", 0x466cd8, (void*)adjustSetProbe,      (void**)&origAdjustSet},
        {"ai_tactic_pick",       0x466388, (void*)tacticStepProbe,     (void**)&origTacticStep},
        {"ai_tactic_result",     0x461fb2, (void*)tacticResultProbe,   (void**)&origTacticResult},
        {"ai_rod_cell",          0x46164d, (void*)rodCellProbe,        (void**)&origRodCell},
        {"ai_strength",          0x5dda25, (void*)strengthProbe,       (void**)&origStrength},
        {"ai_hire_score",        0x45a34c, (void*)hireScoreProbe,      (void**)&origHireScore},
        {"ai_grid",              0x461c85, (void*)gridProbe,           (void**)&origGrid},
        {"ai_grid_fort",         0x461dfc, (void*)gridFortProbe,       (void**)&origGridFort},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
