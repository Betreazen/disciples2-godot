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

#include "currency.h"
#include "midgardid.h"
#include "probe.h"
#include "probeaiutil.h"
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>
#include <windows.h>

/*
 * Схема корпуса 6.5, часть 1: экономический шаг ИИ (CAiStrategic).
 *
 * Разбор - disciples-modding/docs/synthetic-maps/analysis/O-re65-strategic.md. Адреса -
 * game-pristine/Discipl2.exe (Gog 3.01), пролог, ret, ecx и адреса возврата сверены capstone. Все
 * строки пишутся на КАЖДЫЙ вызов, без ключа и бюджета; рядом - счётчики counts.calls.
 *
 * - 0x44dcb8 vector<IAiEval*>::push_back, __thiscall(vec, eval*), ret 4 - ai_eval; список - по
 *   адресу возврата (восемь построителей).
 * - 0x44dd87 List<{action*, double}>::push_back, __thiscall(list, action*, double), ret 0xc -
 *   ai_candidate; idx - длина списка до вставки (список чистится сбросом 0x44e777 на каждом проходе),
 *   исполняется первый (idx 0).
 * - execute (слот 3 действия), __thiscall(action, map, ctx), ret 8, bool - ai_exec: семь действий
 *   экономики и шесть дипломатии (CAiDiploHatePlayer vftable 0x6cf5c4 - execute 0x449180: имя по
 *   RTTI vftable[-1], размер 8 по конструктору 0x449141 -> базовый 0x44db07 без своих полей).
 * - 0x4464ef списание бюджета, __thiscall(budget, int тип), ret 4 - ai_budget_spend. budget: +4 id
 *   игрока, +8 казна начала, +0x14 банк заклинаний начала, +0x20 карта бюджетов (find 0x5f0560, узел:
 *   +0x10 тип, +0x20 Bank, +0x2c флаг резерва).
 * - 0x44809c порядок городов, __thiscall(out List, map, id игрока), ret 8 - ai_city_rank (узел: +8 id,
 *   +0xc приоритет).
 * - 0x44b5c0 / 0x44b6f3 / 0x44b7d0 роль и подприказ лидера, __stdcall(12 слов), ret 0x30 -
 *   ai_leader_role (ранг 2: bool x3, int x3, float x3, int x3 - пишутся все двенадцать).
 * - 0x44ad0f тип лидера, __stdcall(out id, map, id, Bank*, int, bool), ret 0x18 - ai_leader_pick.
 * - 0x44c06a случайный юнит найма, __stdcall(map, IdList*, id, filter*), ret 0x10 - ai_unit_pick.
 * - 0x44790a / 0x446ea3 здание столицы, __stdcall(out id, map, id), ret 0xc; 0x5dcc28 храм,
 *   __stdcall(map, id), ret 8 - ai_build_pick.
 * - ai_rng - не хук: хуки rng / rng_std (probehooks.cpp) зовут tagAiRng (probeairng.cpp) с адресом
 *   возврата; места - там (схема 7.5: все броски решений игры вне показа и боя).
 * - crt_rand - msvcrt!rand (адрес из GetProcAddress): каждое сырое значение, для восстановления
 *   состояния LCG перебором.
 */

namespace probe {

namespace {

using namespace game;
using namespace aiutil;

/* ---------------------------------------------------------------------------------------------
 * Разбор объектов оценок и действий по vftable
 */

enum class Kind
{
    None,
    Id,
    Double,
    Int,
    Byte,
};

struct FieldDef
{
    const char* name;
    std::size_t offset;
    Kind kind;
};

struct ObjectDef
{
    std::uintptr_t vftable;
    const char* cls;
    int type;
    std::size_t size;
    FieldDef fields[5];
};

// clang-format off
constexpr ObjectDef objectDefs[] = {
    // Оценки (IAiEval): слот 1 - тип, слот 2 - id памяти дня.
    {0x6cf79c, "eval_hire_unit",     2, 0x10, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"b12", 0xc, Kind::Byte}, {"b13", 0xd, Kind::Byte}}},
    {0x6cf71c, "eval_hire_leader",   0, 0x24, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"d16", 0x10, Kind::Double}, {"cat32", 0x20, Kind::Int}}},
    {0x6cf69c, "eval_heal_revive",   5, 0x0c, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}}},
    {0x6cfa4c, "eval_upgrade_leader", 1, 0x08, {{"id4", 4, Kind::Id}}},
    {0x6cf4ec, "eval_capital",       4, 0x0c, {{"id4", 4, Kind::Id}, {"b8", 8, Kind::Byte}}},
    {0x6cf644, "eval_grow",          3, 0x10, {{"id4", 4, Kind::Id}, {"d8", 8, Kind::Double}}},
    {0x6cf89c, "eval_spell_research", 6, 0x08, {{"id4", 4, Kind::Id}}},
    {0x6cf56c, "eval_diplomacy",     8, 0x08, {{"id4", 4, Kind::Id}}},
    // Действия (IAiAction): слот 1 - тип, слот 2 - id (+4), слот 3 - execute.
    {0x6cf7f4, "hire_unit",          2, 0x10, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"id12", 0xc, Kind::Id}}},
    // Раскладки действий - по конструкторам (0x44b030, 0x447d0c, 0x449041..0x4492c4); лишнее поле-id
    // за концом объекта уходило в toString движка мусором кучи и роняло игру (дипломатия, 18.09).
    {0x6cf734, "hire_leader",        0, 0x1c, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"id12", 0xc, Kind::Id}, {"cat24", 0x18, Kind::Int}}},
    {0x6cf6bc, "heal_revive",        5, 0x10, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"id12", 0xc, Kind::Id}}},
    {0x6cfa64, "upgrade_leader",     1, 0x0c, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}}},
    {0x6cf51c, "capital",            4, 0x10, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"b12", 0xc, Kind::Byte}}},
    {0x6cf664, "grow",               3, 0x0c, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}}},
    {0x6cf8bc, "spell_research",     6, 0x0c, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}}},
    {0x6cf594, "dipl_break",         8, 0x08, {{"id4", 4, Kind::Id}}},
    {0x6cf5ac, "dipl_offer",         8, 0x08, {{"id4", 4, Kind::Id}}},
    {0x6cf5c4, "dipl_hate",          8, 0x08, {{"id4", 4, Kind::Id}}},
    {0x6cf5dc, "dipl_item",          8, 0x14, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"id12", 0xc, Kind::Id}, {"i16", 0x10, Kind::Int}}},
    {0x6cf5f4, "dipl_spell",         8, 0x10, {{"id4", 4, Kind::Id}, {"id8", 8, Kind::Id}, {"i12", 0xc, Kind::Int}}},
    {0x6cf60c, "dipl_gold",          8, 0x0c, {{"id4", 4, Kind::Id}, {"i8", 8, Kind::Int}}},
};
// clang-format on

constexpr std::size_t unknownObjectSize = 0x10;

std::string fieldJson(const void* object, const FieldDef& field)
{
    switch (field.kind) {
    case Kind::Id:
        return jsonId(static_cast<const char*>(object) + field.offset);
    case Kind::Double:
        return doubleAt(object, field.offset);
    case Kind::Int:
        return intAt(object, field.offset);
    case Kind::Byte:
        return byteAt(object, field.offset);
    default:
        return {"null"};
    }
}

/** {"vft","class","type",поля...,"raw"} - raw: байты после vftable, поля - ранг 2/3. */
std::string objectJson(const void* object)
{
    if (!object)
        return {"null"};

    const auto vftable = vftableOf(object);
    for (const auto& def : objectDefs) {
        if (def.vftable != vftable)
            continue;

        std::string fields;
        for (const auto& field : def.fields) {
            if (field.kind != Kind::None)
                fields += fmt::format(R"(,"{}":{})", field.name, fieldJson(object, field));
        }
        return fmt::format(R"({{"vft":{},"class":"{}","type":{}{},"raw":{}}})", hexOf(vftable),
                           def.cls, def.type, fields, rawHex(object, 4, def.size - 4));
    }

    return fmt::format(R"({{"vft":{},"class":null,"type":null,"raw":{}}})", hexOf(vftable),
                       rawHex(object, 4, unknownObjectSize - 4));
}

struct CallSite
{
    std::uintptr_t returnAddress;
    const char* name;
};

template <std::size_t N>
std::string siteName(const CallSite (&sites)[N], std::uintptr_t returnAddress)
{
    for (const auto& site : sites) {
        if (site.returnAddress == returnAddress)
            return fmt::format(R"("{}")", site.name);
    }
    return {"null"};
}

/* ---------------------------------------------------------------------------------------------
 * ai_eval - 0x44dcb8
 */

constexpr CallSite evalSites[] = {
    {0x446e92, "capital"},        {0x44866a, "diplomacy"}, {0x4498b8, "grow"},
    {0x449dd0, "heal_revive"},    {0x44ac50, "hire_leader"}, {0x44be46, "hire_unit"},
    {0x44cf15, "spell_research"}, {0x44f058, "upgrade_leader"},
};

using EvalPush = void(__fastcall*)(void* thisptr, int /* %edx */, void* eval);
EvalPush origEvalPush{};

void __fastcall evalPushProbe(void* thisptr, int /* %edx */, void* eval)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto object = objectJson(eval);

    origEvalPush(thisptr, 0, eval);

    countCall("ai_eval");
    emit(seq, fmt::format(R"("ev":"ai_eval","player":{},"pass":{},"list":{},"eval":{},"ret_addr":"{}")",
                          aiTurnPlayer(), aiPass(), siteName(evalSites, returnAddress), object,
                          hexAddress(returnAddress)));
}

/* ---------------------------------------------------------------------------------------------
 * ai_candidate - 0x44dd87
 */

using CandidatePush = void(__fastcall*)(void* thisptr, int /* %edx */, void* action, double score);
CandidatePush origCandidatePush{};

void __fastcall candidatePushProbe(void* thisptr, int /* %edx */, void* action, double score)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    int idx = -1;
    const bool idxKnown = readAt(thisptr, 0, idx);
    const auto object = objectJson(action);

    origCandidatePush(thisptr, 0, action, score);

    countCall("ai_candidate");
    emit(seq,
         fmt::format(
             R"("ev":"ai_candidate","player":{},"pass":{},"idx":{},"chosen":{},"action":{},"score":{},"count_after":{},"ret_addr":"{}")",
             aiTurnPlayer(), aiPass(), idxKnown ? std::to_string(idx) : "null",
             idxKnown ? (idx == 0 ? "true" : "false") : "null", object, jsonDouble(true, score),
             listLength(thisptr), hexAddress(returnAddress)));
}

/* ---------------------------------------------------------------------------------------------
 * ai_exec - слот 3 действий
 */

constexpr std::uintptr_t execTargets[] = {
    0x449fcb, 0x44f3d2, 0x44c6c6, 0x44b09f, 0x447d5c, 0x4499d2,
    0x44d30c, 0x449080, 0x449100, 0x449180, 0x4491f7, 0x449278, 0x449307,
};
constexpr std::size_t execCount = std::size(execTargets);

using Execute = bool(__fastcall*)(void* thisptr, int /* %edx */, void* objectMap, void* context);
Execute origExec[execCount]{};

bool executeProbe(std::size_t index, void* thisptr, void* objectMap, void* context, void* ret)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(ret);
    const auto rngBefore = rngCount();
    const auto object = objectJson(thisptr);

    const bool result = origExec[index](thisptr, 0, objectMap, context);

    const auto rngAfter = rngCount();
    countCall("ai_exec");
    emit(seq,
         fmt::format(
             R"("ev":"ai_exec","player":{},"pass":{},"fn":"0x{:x}","action":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             aiTurnPlayer(), aiPass(), execTargets[index], object, rngBefore, rngAfter,
             hexAddress(returnAddress), result, nextSeq()));
    return result;
}

template <std::size_t I>
bool __fastcall executeProbeAt(void* thisptr, int /* %edx */, void* objectMap, void* context)
{
    return executeProbe(I, thisptr, objectMap, context, _ReturnAddress());
}

/* ---------------------------------------------------------------------------------------------
 * ai_budget_spend - 0x4464ef
 */

struct MapIterator
{
    std::uint8_t flag;
    void* node;
    void* ptr;
};

using BudgetFind = MapIterator*(__thiscall*)(void* map, MapIterator* out, const int* key);
const auto budgetFind = reinterpret_cast<BudgetFind>(0x5f0560);

struct BudgetView
{
    std::string start{"null"};
    std::string spellStart{"null"};
    std::string budget{"null"};
    std::string reserve{"null"};
};

std::string bankJson(const void* base, std::size_t offset)
{
    Bank bank{};
    return readAt(base, offset, bank) ? bankObject(bank) : std::string{"null"};
}

BudgetView budgetView(void* budget, int type)
{
    BudgetView view{bankJson(budget, 8), bankJson(budget, 0x14)};
    auto* map = static_cast<char*>(budget) + 0x20;
    void* header = nullptr;
    if (!budget || !readAt(map, 0xc, header) || !header)
        return view;

    MapIterator it{};
    budgetFind(map, &it, &type);
    int key = -1;
    if (it.node == header || !readAt(it.node, 0x10, key) || key != type)
        return view;

    view.budget = bankJson(it.node, 0x20);
    view.reserve = byteAt(it.node, 0x2c);
    return view;
}

using BudgetSpend = void(__fastcall*)(void* thisptr, int /* %edx */, int type);
BudgetSpend origBudgetSpend{};

void __fastcall budgetSpendProbe(void* thisptr, int /* %edx */, int type)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto before = budgetView(thisptr, type);

    origBudgetSpend(thisptr, 0, type);

    const auto after = budgetView(thisptr, type);
    countCall("ai_budget_spend");
    emit(seq,
         fmt::format(
             R"("ev":"ai_budget_spend","player":{},"pass":{},"type":{},"reserve":{},"budget_before":{},"budget_after":{},"start_before":{},"start_after":{},"spell_start_before":{},"spell_start_after":{},"ret_addr":"{}")",
             jsonId(static_cast<const char*>(thisptr) + 4), aiPass(), type, after.reserve,
             before.budget, after.budget, before.start, after.start, before.spellStart,
             after.spellStart, hexAddress(returnAddress)));
}

/* ---------------------------------------------------------------------------------------------
 * ai_city_rank - 0x44809c
 */

constexpr std::size_t listCap = 64;

using CityRank = void*(__fastcall*)(void* thisptr,
                                    int /* %edx */,
                                    void* objectMap,
                                    const CMidgardID* player);
CityRank origCityRank{};

void* __fastcall cityRankProbe(void* thisptr,
                               int /* %edx */,
                               void* objectMap,
                               const CMidgardID* player)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());

    auto* result = origCityRank(thisptr, 0, objectMap, player);

    std::string cities;
    walkList(thisptr, listCap, [&cities](const void* node) {
        cities += fmt::format(R"({}{{"id":{},"prio":{}}})", cities.empty() ? "" : ",",
                              jsonId(static_cast<const char*>(node) + 8), intAt(node, 0xc));
        return true;
    });

    countCall("ai_city_rank");
    emit(seq,
         fmt::format(
             R"("ev":"ai_city_rank","player":{},"pass":{},"count":{},"cities":[{}],"ret_addr":"{}")",
             jsonId(player), aiPass(), listLength(thisptr), cities, hexAddress(returnAddress)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_leader_role - 0x44b5c0, 0x44b6f3, 0x44b7d0
 */

constexpr std::uintptr_t roleTargets[] = {0x44b5c0, 0x44b6f3, 0x44b7d0};
constexpr const char* roleNames[] = {"role", "fighter", "thief"};

using LeaderRole = int(__stdcall*)(int b1, int b2, int b3, int i1, int i2, int i3,
                                   float f1, float f2, float f3, int j1, int j2, int j3);
LeaderRole origRole[std::size(roleTargets)]{};

std::string jsonFloat(float value)
{
    return jsonDouble(true, static_cast<double>(value));
}

template <std::size_t I>
int __stdcall leaderRoleProbeAt(int b1, int b2, int b3, int i1, int i2, int i3,
                                float f1, float f2, float f3, int j1, int j2, int j3)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const int result = origRole[I](b1, b2, b3, i1, i2, i3, f1, f2, f3, j1, j2, j3);

    countCall("ai_leader_role");
    emit(seq,
         fmt::format(
             R"("ev":"ai_leader_role","player":{},"pass":{},"fn":"{}","b":[{},{},{}],"i":[{},{},{}],"f":[{},{},{}],"j":[{},{},{}],"ret_addr":"{}","ret":{})",
             aiTurnPlayer(), aiPass(), roleNames[I], b1 & 0xff, b2 & 0xff, b3 & 0xff, i1, i2, i3,
             jsonFloat(f1), jsonFloat(f2), jsonFloat(f3), j1, j2, j3, hexAddress(returnAddress),
             result));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_leader_pick - 0x44ad0f
 */

using LeaderPick = CMidgardID*(__stdcall*)(CMidgardID* out, void* objectMap, const CMidgardID* id,
                                            const Bank* bank, int role, int flag);
LeaderPick origLeaderPick{};

std::string rollAt(std::uint64_t before, std::uint64_t after)
{
    return after > before ? std::to_string(rngResultAt(before + 1)) : std::string{"null"};
}

CMidgardID* __stdcall leaderPickProbe(CMidgardID* out, void* objectMap, const CMidgardID* id,
                                      const Bank* bank, int role, int flag)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    auto* result = origLeaderPick(out, objectMap, id, bank, role, flag);

    const auto rngAfter = rngCount();
    countCall("ai_leader_pick");
    emit(seq,
         fmt::format(
             R"("ev":"ai_leader_pick","player":{},"pass":{},"id":{},"bank":{},"role":{},"flag":{},"result":{},"roll":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","seq_end":{})",
             aiTurnPlayer(), aiPass(), jsonId(id), bankJson(bank, 0), role, (flag & 0xff) != 0,
             jsonId(out), rollAt(rngBefore, rngAfter), rngBefore, rngAfter,
             hexAddress(returnAddress), nextSeq()));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_unit_pick - 0x44c06a
 */

const char* filterName(std::uintptr_t vftable)
{
    switch (vftable) {
    case 0x6cf794:
        return R"("melee_small")";
    case 0x6cf78c:
        return R"("big")";
    case 0x6cf784:
        return R"("any")";
    default:
        return "null";
    }
}

using UnitPick = void*(__stdcall*)(void* objectMap, const void* list, const CMidgardID* id,
                                   const void* filter);
UnitPick origUnitPick{};

void* __stdcall unitPickProbe(void* objectMap, const void* list, const CMidgardID* id,
                              const void* filter)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto stdBefore = rngStdCount();
    const auto listLen = listLength(list);

    auto* result = origUnitPick(objectMap, list, id, filter);

    const auto stdAfter = rngStdCount();
    const auto filterVft = vftableOf(filter);
    countCall("ai_unit_pick");
    emit(seq,
         fmt::format(
             R"("ev":"ai_unit_pick","player":{},"pass":{},"id":{},"filter":{},"filter_vft":{},"list_len":{},"result":{},"rng_std_before":{},"rng_std_after":{},"ret_addr":"{}","seq_end":{})",
             aiTurnPlayer(), aiPass(), jsonId(id), filterName(filterVft), hexOf(filterVft),
             listLen, objectId(result), stdBefore, stdAfter, hexAddress(returnAddress),
             nextSeq()));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_build_pick - 0x44790a, 0x446ea3 (id здания), 0x5dcc28 (статус храма)
 */

constexpr std::uintptr_t buildTargets[] = {0x44790a, 0x446ea3};
constexpr const char* buildNames[] = {"s1", "s2"};

using BuildPick = CMidgardID*(__stdcall*)(CMidgardID* out, void* objectMap, const CMidgardID* id);
BuildPick origBuild[std::size(buildTargets)]{};

void emitBuildPick(std::uint64_t seq,
                   const char* strategy,
                   const CMidgardID* id,
                   const std::string& result,
                   const std::string& status,
                   std::uintptr_t returnAddress)
{
    countCall("ai_build_pick");
    emit(seq,
         fmt::format(
             R"("ev":"ai_build_pick","player":{},"pass":{},"strategy":"{}","id":{},"result":{},"status":{},"ret_addr":"{}")",
             aiTurnPlayer(), aiPass(), strategy, jsonId(id), result, status,
             hexAddress(returnAddress)));
}

template <std::size_t I>
CMidgardID* __stdcall buildPickProbeAt(CMidgardID* out, void* objectMap, const CMidgardID* id)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    auto* result = origBuild[I](out, objectMap, id);
    emitBuildPick(seq, buildNames[I], id, jsonId(out), "null", returnAddress);
    return result;
}

using TempleStatus = int(__stdcall*)(void* objectMap, const CMidgardID* id);
TempleStatus origTemple{};

int __stdcall templeStatusProbe(void* objectMap, const CMidgardID* id)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const int result = origTemple(objectMap, id);
    emitBuildPick(seq, "temple", id, "null", std::to_string(result), returnAddress);
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * crt_rand - msvcrt!rand; ai_rng - метка бросков ИИ
 */

std::atomic<std::uint64_t> crtRandCounter{0};

struct RawRand
{
    std::uint64_t seq{};
    int value{};
};

thread_local RawRand lastRawRand;

using CrtRand = int(__cdecl*)();
CrtRand origCrtRand{};

int __cdecl crtRandProbe()
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const int result = origCrtRand();
    lastRawRand = {seq, result};

    countCall("crt_rand");
    emit(seq, fmt::format(R"("ev":"crt_rand","n":{},"r":{},"ret_addr":"{}")", ++crtRandCounter,
                          result, hexAddress(returnAddress)));
    return result;
}

std::uintptr_t crtRandAddress()
{
    const auto module = GetModuleHandleA("msvcrt.dll");
    return module ? reinterpret_cast<std::uintptr_t>(GetProcAddress(module, "rand")) : 0;
}

} // namespace

bool rawRandAfter(std::uint64_t seq, int& value)
{
    const auto raw = lastRawRand;
    if (raw.seq <= seq)
        return false;
    value = raw.value;
    return true;
}

} // namespace probe

namespace hooks {

void addAiEcoHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    std::vector<GogHook> entries = {
        {"ai_eval",              0x44dcb8, (void*)evalPushProbe,      (void**)&origEvalPush},
        {"ai_candidate",         0x44dd87, (void*)candidatePushProbe, (void**)&origCandidatePush},
        {"ai_exec_heal",         execTargets[0],  (void*)executeProbeAt<0>,  (void**)&origExec[0]},
        {"ai_exec_upgrade",      execTargets[1],  (void*)executeProbeAt<1>,  (void**)&origExec[1]},
        {"ai_exec_hire_unit",    execTargets[2],  (void*)executeProbeAt<2>,  (void**)&origExec[2]},
        {"ai_exec_hire_leader",  execTargets[3],  (void*)executeProbeAt<3>,  (void**)&origExec[3]},
        {"ai_exec_capital",      execTargets[4],  (void*)executeProbeAt<4>,  (void**)&origExec[4]},
        {"ai_exec_grow",         execTargets[5],  (void*)executeProbeAt<5>,  (void**)&origExec[5]},
        {"ai_exec_spell",        execTargets[6],  (void*)executeProbeAt<6>,  (void**)&origExec[6]},
        {"ai_exec_dipl_break",   execTargets[7],  (void*)executeProbeAt<7>,  (void**)&origExec[7]},
        {"ai_exec_dipl_offer",   execTargets[8],  (void*)executeProbeAt<8>,  (void**)&origExec[8]},
        {"ai_exec_dipl_hate",    execTargets[9],  (void*)executeProbeAt<9>,  (void**)&origExec[9]},
        {"ai_exec_dipl_item",    execTargets[10], (void*)executeProbeAt<10>, (void**)&origExec[10]},
        {"ai_exec_dipl_spell",   execTargets[11], (void*)executeProbeAt<11>, (void**)&origExec[11]},
        {"ai_exec_dipl_gold",    execTargets[12], (void*)executeProbeAt<12>, (void**)&origExec[12]},
        {"ai_budget_spend",      0x4464ef, (void*)budgetSpendProbe,   (void**)&origBudgetSpend},
        {"ai_city_rank",         0x44809c, (void*)cityRankProbe,      (void**)&origCityRank},
        {"ai_leader_role",         roleTargets[0], (void*)leaderRoleProbeAt<0>, (void**)&origRole[0]},
        {"ai_leader_role_fighter", roleTargets[1], (void*)leaderRoleProbeAt<1>, (void**)&origRole[1]},
        {"ai_leader_role_thief",   roleTargets[2], (void*)leaderRoleProbeAt<2>, (void**)&origRole[2]},
        {"ai_leader_pick",       0x44ad0f, (void*)leaderPickProbe,    (void**)&origLeaderPick},
        {"ai_unit_pick",         0x44c06a, (void*)unitPickProbe,      (void**)&origUnitPick},
        {"ai_build_pick_s1",     buildTargets[0], (void*)buildPickProbeAt<0>, (void**)&origBuild[0]},
        {"ai_build_pick_s2",     buildTargets[1], (void*)buildPickProbeAt<1>, (void**)&origBuild[1]},
        {"ai_build_temple",      0x5dcc28, (void*)templeStatusProbe,  (void**)&origTemple},
    };
    // clang-format on

    // msvcrt!rand: адрес из загруженного модуля; нет модуля - нет хука (нулевая цель уронила бы Detours).
    if (const auto crtRand = crtRandAddress())
        entries.push_back({"crt_rand", crtRand, (void*)crtRandProbe, (void**)&origCrtRand});

    addGogHooks(entries.data(), entries.size(), hooks, installed);
}

} // namespace hooks
